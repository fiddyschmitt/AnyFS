using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A sign-in made again when the server no longer takes it (<see cref="SignInRenewal"/>): what every session's renewal
/// does, shown with a sign-in that is a counter. What a product's own sign-in adds is in that product's tests.
/// </summary>
[TestClass]
public class SignInRenewalTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Again = TimeSpan.FromSeconds(30);

    /// <summary>A sign-in that takes as long as a test lets it, and counts its tries.</summary>
    private sealed class SignIn
    {
        private int _tries;

        public int Tries => Volatile.Read(ref _tries);

        /// <summary>What a try waits for before it ends; none, and it ends at once.</summary>
        public TaskCompletionSource? Hold { get; set; }

        /// <summary>What a try fails with; null, and it works.</summary>
        public Exception? FailsWith { get; set; }

        public bool WasAWalks { get; private set; }

        public bool HadPatience { get; private set; }

        public async Task AgainAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _tries);
            WasAWalks = Walks.Current;
            HadPatience = UpstreamPatience.Current is not null;
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(ct);
            }

            if (FailsWith is { } failure)
            {
                throw failure;
            }
        }

        public async Task UntilTriedAsync(int tries)
        {
            for (var waited = 0; Tries < tries && waited < 1000; waited++)
            {
                await Task.Delay(5);
            }

            Assert.AreEqual(tries, Tries);
        }
    }

    [TestMethod]
    public async Task EveryRequestRefusedWithOneSignInGetsTheOneRenewal()
    {
        var signIn = new SignIn { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var renewal = new SignInRenewal(signIn.AgainAsync, Again);
        var renewed = 0;
        renewal.Renewed += () => renewed++;

        var refused = Enumerable.Range(0, 5).Select(_ => renewal.RenewAsync(0, CancellationToken.None)).ToArray();
        await signIn.UntilTriedAsync(1);
        Assert.IsFalse(refused.Any(r => r.IsCompleted), "all wait for the one sign-in");

        signIn.Hold.SetResult();
        CollectionAssert.AreEqual(new[] { true, true, true, true, true }, await Task.WhenAll(refused).WaitAsync(Soon));
        Assert.AreEqual(1, signIn.Tries);
        Assert.AreEqual(1, renewal.Generation);
        Assert.AreEqual(1, renewed);

        // and a request that went with the old sign-in and is refused only now needs none
        Assert.IsTrue(await renewal.RenewAsync(0, CancellationToken.None));
        Assert.AreEqual(1, signIn.Tries);
    }

    [TestMethod]
    public async Task ARequestGivenUpLeavesTheRenewalToTheOthers()
    {
        var signIn = new SignIn { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var renewal = new SignInRenewal(signIn.AgainAsync, Again);
        using var givenUp = new CancellationTokenSource();
        var first = renewal.RenewAsync(0, givenUp.Token);
        await signIn.UntilTriedAsync(1);
        var second = renewal.RenewAsync(0, CancellationToken.None);

        givenUp.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        Assert.IsFalse(second.IsCompleted, "the sign-in goes on");

        signIn.Hold.SetResult();
        Assert.IsTrue(await second.WaitAsync(Soon));
        Assert.AreEqual(1, signIn.Tries);
        Assert.AreEqual(1, renewal.Generation);
    }

    [TestMethod]
    public async Task ARenewalNobodyWaitsForAnyMoreStillRenews()
    {
        var signIn = new SignIn { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var renewal = new SignInRenewal(signIn.AgainAsync, Again);
        using var givenUp = new CancellationTokenSource();
        var only = renewal.RenewAsync(0, givenUp.Token);
        await signIn.UntilTriedAsync(1);
        givenUp.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => only);

        signIn.Hold.SetResult();
        for (var waited = 0; renewal.Generation == 0 && waited < 1000; waited++)
        {
            await Task.Delay(5);
        }

        Assert.AreEqual(1, renewal.Generation, "the next request goes with the new sign-in");
    }

    [TestMethod]
    public async Task ARenewalThatFailedIsToldAsItFailedAndTriedAgainOnlyLater()
    {
        var time = new FakeTime();
        var signIn = new SignIn { FailsWith = new VfsException(VfsError.Unavailable, "the sign-in has lapsed and only you can sign in") };
        using var renewal = new SignInRenewal(signIn.AgainAsync, Again, time);

        var first = await Assert.ThrowsExactlyAsync<VfsException>(() => renewal.RenewAsync(0, CancellationToken.None));
        Assert.AreEqual("the sign-in has lapsed and only you can sign in", first.Message);

        // the time of a renewal was written down before it ran: one that failed counted as made, and the next request
        // was told the session had been "refused again just after it was renewed"
        time.Advance(TimeSpan.FromSeconds(29));
        var second = await Assert.ThrowsExactlyAsync<VfsException>(() => renewal.RenewAsync(0, CancellationToken.None));
        Assert.AreSame(first, second);
        Assert.AreEqual(1, signIn.Tries, "not tried for it: each try may be a browser started");
        Assert.AreEqual(0, renewal.Generation);

        time.Advance(TimeSpan.FromSeconds(2));
        signIn.FailsWith = null;
        Assert.IsTrue(await renewal.RenewAsync(0, CancellationToken.None));
        Assert.AreEqual(2, signIn.Tries);
        Assert.AreEqual(1, renewal.Generation);
    }

    [TestMethod]
    public async Task AFailureForgottenIsTriedAgainAtOnce()
    {
        var signIn = new SignIn { FailsWith = new VfsException(VfsError.Unavailable, "no") };
        using var renewal = new SignInRenewal(signIn.AgainAsync, Again, new FakeTime());
        await Assert.ThrowsExactlyAsync<VfsException>(() => renewal.RenewAsync(0, CancellationToken.None));

        signIn.FailsWith = null;
        renewal.Forget();
        Assert.IsTrue(await renewal.RenewAsync(0, CancellationToken.None));
        Assert.AreEqual(2, signIn.Tries);
    }

    [TestMethod]
    public async Task ASignInRefusedAgainJustAfterItWasRenewedIsNotMadeAgainYet()
    {
        var time = new FakeTime();
        var signIn = new SignIn();
        using var renewal = new SignInRenewal(signIn.AgainAsync, Again, time);
        Assert.IsTrue(await renewal.RenewAsync(0, CancellationToken.None));

        // a request that went with the new sign-in, refused: the server does not take what it just gave
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.IsFalse(await renewal.RenewAsync(1, CancellationToken.None));
        Assert.AreEqual(1, signIn.Tries);

        time.Advance(Again);
        Assert.IsTrue(await renewal.RenewAsync(1, CancellationToken.None));
        Assert.AreEqual(2, signIn.Tries);
        Assert.AreEqual(2, renewal.Generation);
    }

    [TestMethod]
    public async Task TheRenewalIsNobodysRequest()
    {
        // begun by a walk's request, under a person's patience: neither goes with the sign-in, which is for all
        var signIn = new SignIn();
        using var renewal = new SignInRenewal(signIn.AgainAsync, Again);
        await BegunByAWalkAsync(renewal);
        Assert.IsFalse(signIn.WasAWalks);
        Assert.IsFalse(signIn.HadPatience);

        static async Task BegunByAWalkAsync(SignInRenewal renewal)
        {
            Walks.Current = true;
            UpstreamPatience.Current = TimeSpan.FromSeconds(1);
            Assert.IsTrue(await renewal.RenewAsync(0, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task ARenewalUnderWayStopsWithTheSession()
    {
        var signIn = new SignIn { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var renewal = new SignInRenewal(signIn.AgainAsync, Again);
        var waiting = renewal.RenewAsync(0, CancellationToken.None);
        await signIn.UntilTriedAsync(1);

        renewal.Dispose();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting.WaitAsync(Soon));
        Assert.AreEqual(0, renewal.Generation);
    }
}
