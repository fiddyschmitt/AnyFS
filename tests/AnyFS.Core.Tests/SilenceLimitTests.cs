using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AnyFS.Core.Operations;
using AnyFS.Core.Runtime;
using AnyFS.Core.Status;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;

namespace AnyFS.Core.Tests;

/// <summary>
/// A person's call stops waiting for an upstream that says nothing (the review of 21-Nov-2026, R6): by the source's
/// silence, which is requests out and nothing back, not by how long the call has taken. The clock is the tests' own:
/// a call looks at it again at least every <see cref="Limit"/> of real time, so a test moves the clock and then waits.
/// </summary>
[TestClass]
public class SilenceLimitTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    /// <summary>A source whose every answer is the test's to give: a path asks its upstream and waits to be answered, but "kept", which answers from what it holds.</summary>
    private sealed class Asking : SourceBase
    {
        public IUpstreamWatcher? Watcher { get; set; }

        public ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyList<VfsEntry>>> Answers { get; } = new();

        public ConcurrentDictionary<string, IWatchedRequest> Rows { get; } = new();

        public ConcurrentQueue<TimeSpan?> PatienceSeen { get; } = new();

        public int Stopped;

        /// <summary>Whether a path that waits says so to the watcher: false for one that waits on something that is no request.</summary>
        public bool Reports { get; set; } = true;

        public void Answer(string path, params string[] names)
        {
            Rows[path].Answered("200 OK", refused: false);
            Answers[path].TrySetResult([.. names.Select(n => VfsEntry.File(n, 1))]);
        }

        private async Task<IReadOnlyList<VfsEntry>> AskAsync(string path, CancellationToken ct)
        {
            PatienceSeen.Enqueue(UpstreamPatience.Current);
            if (path == "kept")
            {
                return [VfsEntry.File("held.txt", 1)];
            }

            var answer = Answers.GetOrAdd(path, _ => new TaskCompletionSource<IReadOnlyList<VfsEntry>>(TaskCreationOptions.RunContinuationsAsynchronously));
            var asked = Reports ? Watcher!.Asked("GET", path) : null;
            if (asked is not null)
            {
                // as an HTTP request does: what it takes with it is told as it goes, and is no word from the server
                asked.Sent(120);
                Rows[path] = asked;
            }

            try
            {
                return await answer.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException ex)
            {
                Interlocked.Increment(ref Stopped);
                asked?.Failed(ex, ct);
                throw;
            }
            finally
            {
                asked?.Done();
            }
        }

        public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var entry in await AskAsync(path, ct))
            {
                yield return entry;
            }
        }

        public override async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
            => (await AskAsync(path, ct)).FirstOrDefault();

        public override async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
        {
            await AskAsync(path, ct);
            return new Opened();
        }
    }

    private sealed class Opened : IVfsFile
    {
        public long Length => 0;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) => ValueTask.FromResult(0);

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask SetLengthAsync(long length, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed record Rig(FakeTime Time, Asking Inner, TrackedSource Source, SourceHandle Handle);

    private static Rig Make(TimeSpan? limit)
    {
        var time = new FakeTime();
        var handle = new SourceHandle(Guid.NewGuid(), "alpha", "test") { Status = ComponentStatus.ReadyStatus };
        var inner = new Asking();
        var source = new TrackedSource(inner, handle, new OperationTracker(time), () => limit);
        inner.Watcher = source.UpstreamRows();
        return new Rig(time, inner, source, handle);
    }

    private static async Task<List<string>> NamesAsync(ISource source, string path)
    {
        var names = new List<string>();
        await foreach (var entry in source.ListAsync(path, None))
        {
            names.Add(entry.Name);
        }

        return names;
    }

    /// <summary>Real time for a call under way to look at the clock again, and to be seen still waiting.</summary>
    private static Task ALookAsync() => Task.Delay(Limit + Limit);

    [TestMethod]
    public async Task AListingIsGivenUpOnceRequestsAreOutAndNothingHasComeForTheLimit()
    {
        var rig = Make(Limit);
        var listing = NamesAsync(rig.Source, "docs");
        await ALookAsync();
        Assert.IsFalse(listing.IsCompleted, "within the limit it waits");

        rig.Time.Advance(Limit);
        var given = await Assert.ThrowsExactlyAsync<VfsException>(() => listing.WaitAsync(Soon));
        Assert.AreEqual(VfsError.Unavailable, given.Error);
        StringAssert.Contains(given.Message, "has sent nothing since");
        Assert.AreEqual(1, rig.Inner.Stopped, "the call is stopped, not left to its own retries");
        Assert.AreEqual(ComponentState.Degraded, rig.Handle.EffectiveStatus.State, "and the source says so");
        StringAssert.Contains(rig.Handle.EffectiveStatus.Text, "has sent nothing since");
    }

    [TestMethod]
    public async Task AStatAndAnOpenForReadingAreGivenUpTheSameWayAndAnOpenForWritingIsNot()
    {
        var rig = Make(Limit);
        var stat = rig.Source.StatAsync("a", None);
        var read = rig.Source.OpenAsync("b", VfsOpenMode.Read, None);
        var write = rig.Source.OpenAsync("c", VfsOpenMode.Create, None);
        await ALookAsync();
        rig.Time.Advance(Limit);
        Assert.AreEqual(VfsError.Unavailable, (await Assert.ThrowsExactlyAsync<VfsException>(() => stat.WaitAsync(Soon))).Error);
        Assert.AreEqual(VfsError.Unavailable, (await Assert.ThrowsExactlyAsync<VfsException>(() => read.WaitAsync(Soon))).Error);

        rig.Time.Advance(Limit * 10);
        await ALookAsync();
        Assert.IsFalse(write.IsCompleted, "what writes is not given up: its end would be neither done nor not");
        rig.Inner.Answer("c");
        await (await write.WaitAsync(Soon)).DisposeAsync();
    }

    [TestMethod]
    public async Task ACallIsNotGivenUpWhileSomethingKeepsComing()
    {
        var rig = Make(Limit);
        var listing = NamesAsync(rig.Source, "big");
        await ALookAsync();
        for (var i = 0; i < 6; i++)
        {
            // a great folder: long, with a page or some bytes every while
            rig.Time.Advance(Limit * 0.8);
            rig.Inner.Rows["big"].AddBytes(1000);
            await ALookAsync();
            Assert.IsFalse(listing.IsCompleted, $"after {i + 1} times most of the limit, each with something come");
        }

        rig.Inner.Answer("big", "one.txt", "two.txt");
        CollectionAssert.AreEqual(new[] { "one.txt", "two.txt" }, await listing.WaitAsync(Soon));
        Assert.AreEqual(ComponentState.Ready, rig.Handle.EffectiveStatus.State);
    }

    [TestMethod]
    public async Task ACallThatWaitsWithNothingOutIsNotGivenUp()
    {
        // waiting for a turn, for a sign-in, on work of its own: no request of the source's is out, so nothing is silent
        var rig = Make(Limit);
        rig.Inner.Reports = false;
        var listing = NamesAsync(rig.Source, "local");
        await ALookAsync();
        rig.Time.Advance(Limit * 20);
        await ALookAsync();
        Assert.IsFalse(listing.IsCompleted);
        rig.Inner.Answers["local"].SetResult([VfsEntry.File("done.txt", 1)]);
        CollectionAssert.AreEqual(new[] { "done.txt" }, await listing.WaitAsync(Soon));
    }

    [TestMethod]
    public async Task AWalksCallAndACallWithNoLimitWaitAsBefore()
    {
        var rig = Make(Limit);
        var walking = Task.Run(async () =>
        {
            Walks.Current = true;
            return await NamesAsync(rig.Source, "walked");
        });
        var unlimited = Make(null);
        var waiting = NamesAsync(unlimited.Source, "any");
        await ALookAsync();
        rig.Time.Advance(Limit * 20);
        unlimited.Time.Advance(Limit * 20);
        await ALookAsync();
        Assert.IsFalse(walking.IsCompleted, "a walk waits as long as the source does");
        Assert.IsFalse(waiting.IsCompleted, "and so does every call of a source set to 0");

        rig.Inner.Answer("walked", "w.txt");
        unlimited.Inner.Answer("any", "u.txt");
        CollectionAssert.AreEqual(new[] { "w.txt" }, await walking.WaitAsync(Soon));
        CollectionAssert.AreEqual(new[] { "u.txt" }, await waiting.WaitAsync(Soon));
        CollectionAssert.AreEqual(new TimeSpan?[] { null }, rig.Inner.PatienceSeen.ToArray(), "a walk's call sees no limit");
    }

    [TestMethod]
    public async Task ACallSeesItsLimitForAWaitItWouldMakeBeforeItAsks()
    {
        var rig = Make(Limit);
        CollectionAssert.AreEqual(new[] { "held.txt" }, await NamesAsync(rig.Source, "kept"));
        CollectionAssert.AreEqual(new TimeSpan?[] { Limit }, rig.Inner.PatienceSeen.ToArray());
        Assert.IsNull(UpstreamPatience.Current, "and it is the call's alone");
    }

    [TestMethod]
    public async Task OnceFoundSilentTheCallsThatWouldWaitAreGivenUpAtOnceButOneInTenSecondsUntilSomethingComes()
    {
        var rig = Make(Limit);
        var first = NamesAsync(rig.Source, "one");
        await ALookAsync();
        rig.Time.Advance(Limit);
        await Assert.ThrowsExactlyAsync<VfsException>(() => first.WaitAsync(Soon));

        // the next that would ask the server: refused before its request is sent, with no wait at all
        var second = await Assert.ThrowsExactlyAsync<VfsException>(() => NamesAsync(rig.Source, "two").WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.AreEqual(VfsError.Unavailable, second.Error);
        Assert.IsFalse(rig.Inner.Rows.ContainsKey("two"), "nothing went to the server for it");

        // what is answered from what is kept asks nothing, and answers
        CollectionAssert.AreEqual(new[] { "held.txt" }, await NamesAsync(rig.Source, "kept"));
        Assert.AreEqual(ComponentState.Degraded, rig.Handle.EffectiveStatus.State);

        // ten seconds on, one call waits the limit out: that is how a server that answers again is found
        rig.Time.Advance(UpstreamSilence.ProbeEvery);
        var probe = NamesAsync(rig.Source, "three");
        await Assert.ThrowsExactlyAsync<VfsException>(() => NamesAsync(rig.Source, "four").WaitAsync(TimeSpan.FromSeconds(1)));
        await ALookAsync();
        rig.Time.Advance(UpstreamSilence.Grace);
        await ALookAsync();
        Assert.IsFalse(probe.IsCompleted, "the one let wait is still waiting");

        rig.Inner.Answer("three", "back.txt");
        CollectionAssert.AreEqual(new[] { "back.txt" }, await probe.WaitAsync(Soon));
        Assert.AreEqual(ComponentState.Ready, rig.Handle.EffectiveStatus.State, "something came: the source is not silent any more");

        // and a call waits its whole limit again
        var after = NamesAsync(rig.Source, "five");
        await ALookAsync();
        rig.Time.Advance(UpstreamSilence.Grace);
        await ALookAsync();
        Assert.IsFalse(after.IsCompleted);
        rig.Inner.Answer("five", "five.txt");
        CollectionAssert.AreEqual(new[] { "five.txt" }, await after.WaitAsync(Soon));
    }

    [TestMethod]
    public async Task TheCallsWaitingBesideTheOneThatFindsTheSilenceAreToldThen()
    {
        var rig = Make(Limit);
        var early = NamesAsync(rig.Source, "early");
        await ALookAsync();
        rig.Time.Advance(Limit * 0.9);
        var late = NamesAsync(rig.Source, "late");
        await ALookAsync();

        // the first has waited its limit; the second a tenth of it, and is not made to wait the rest
        rig.Time.Advance(Limit * 0.1);
        await Assert.ThrowsExactlyAsync<VfsException>(() => early.WaitAsync(Soon));
        rig.Time.Advance(UpstreamSilence.Grace);
        await Assert.ThrowsExactlyAsync<VfsException>(() => late.WaitAsync(Soon));
    }

    [TestMethod]
    public async Task AServerThatSaysNothingIsNotHiddenByOneThatAnswers()
    {
        // a source of several servers (several sites, a service with several hosts): silence is each server's own
        var rig = Make(Limit);
        var dead = NamesAsync(rig.Source, "https://dead.example/docs");
        var walked = rig.Inner.Watcher!.Asked("GET (walk)", "https://alive.example/everything");
        await ALookAsync();
        rig.Time.Advance(Limit * 0.6);
        walked.AddBytes(1000);
        rig.Time.Advance(Limit * 0.6);
        walked.AddBytes(1000);
        var given = await Assert.ThrowsExactlyAsync<VfsException>(() => dead.WaitAsync(Soon));
        StringAssert.Contains(given.Message, "dead.example has sent nothing since");

        // and what is asked of the one that answers is not given up with it
        var alive = NamesAsync(rig.Source, "https://alive.example/docs");
        await ALookAsync();
        rig.Time.Advance(UpstreamSilence.Grace);
        await ALookAsync();
        Assert.IsFalse(alive.IsCompleted);
        rig.Inner.Answer("https://alive.example/docs", "here.txt");
        CollectionAssert.AreEqual(new[] { "here.txt" }, await alive.WaitAsync(Soon));
        StringAssert.Contains(rig.Handle.EffectiveStatus.Text, "dead.example", "the one that is silent is still said to be");
        walked.Done();
    }

    [TestMethod]
    public async Task ACallThatWaitsOnWhatAnotherAskedIsGivenUpOnlyWhenEveryServerAskedIsSilent()
    {
        // the call asks nothing itself: it has joined a listing a walk is fetching, and cannot be told which server that is at
        var rig = Make(Limit);
        rig.Inner.Reports = false;
        var hung = rig.Inner.Watcher!.Asked("GET (walk)", "https://one.example/a");
        var answering = rig.Inner.Watcher!.Asked("GET (walk)", "https://two.example/b");
        var joined = NamesAsync(rig.Source, "joined");
        await ALookAsync();
        rig.Time.Advance(Limit * 0.7);
        answering.AddBytes(1);
        rig.Time.Advance(Limit * 0.7);
        answering.AddBytes(1);
        await ALookAsync();
        Assert.IsFalse(joined.IsCompleted, "one of the servers answers, and may be the one it waits on");

        answering.Done();
        rig.Time.Advance(Limit);
        Assert.AreEqual(VfsError.Unavailable, (await Assert.ThrowsExactlyAsync<VfsException>(() => joined.WaitAsync(Soon))).Error);
        hung.Done();
    }

    [TestMethod]
    public void AnAddressNamesItsServerAndAnythingElseIsTheSourcesOneUpstream()
    {
        Assert.AreEqual("gitlab.example.com", TrackedSource.UpstreamOf("https://GitLab.Example.com/api/v4/projects?page=2"));
        Assert.AreEqual("host:8443", TrackedSource.UpstreamOf("https://host:8443"));
        Assert.AreEqual("", TrackedSource.UpstreamOf(@"C:\data\folder"));
        Assert.AreEqual("", TrackedSource.UpstreamOf("/home/user/docs"));
        Assert.AreEqual("", TrackedSource.UpstreamOf("server:22"));
        Assert.AreEqual("", TrackedSource.UpstreamOf(@"C:\mail.pst: item 12"));
        Assert.AreEqual("", TrackedSource.UpstreamOf("a note that holds https://x/y"));
    }
}
