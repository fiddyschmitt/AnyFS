using AnyFS.Sdk.Testing;

namespace AnyFS.Sdk.Tests;

/// <summary>
/// A place found unreachable, remembered: its callers are refused at once with what it was found unreachable with, one
/// of them let through every ten seconds to try it for real, until a try reaches it.
/// </summary>
[TestClass]
public class ReachMemoryTests
{
    private const string Gone = "aus-files-01:22 did not answer in 20 s";

    private static VfsException Refused(ReachMemory reach)
    {
        Assert.IsFalse(reach.TryEnter(out var turn, out var refusal), "let through");
        Assert.IsNull(turn);
        Assert.IsNotNull(refusal);
        return refusal;
    }

    [TestMethod]
    public void ACallerIsRefusedAtOnceWithWhatThePlaceWasFoundUnreachableWith()
    {
        var time = new FakeTime();
        var reach = new ReachMemory(time: time);
        for (var i = 0; i < 3; i++)
        {
            using var free = reach.Enter();
            free.ThrowIfFoundSince();
        }

        reach.Remember(Gone);
        var ex = Assert.ThrowsExactly<VfsException>(() => reach.Enter());
        Assert.AreEqual(VfsError.Unavailable, ex.Error);
        Assert.AreEqual(Gone + " (tried again every 10 s)", ex.Message);

        time.Advance(TimeSpan.FromSeconds(9.9));
        Assert.AreEqual(ex.Message, Refused(reach).Message, "and for as long as ten seconds have not gone by");

        reach.Remember("aus-files-01:22 is not reachable: No route to host");
        StringAssert.StartsWith(Refused(reach).Message, "aus-files-01:22 is not reachable: No route to host", "with what it was last found unreachable with");
    }

    [TestMethod]
    public void OneCallerIsLetThroughEveryTenSecondsAndTheOthersAreRefusedWhileItTries()
    {
        var time = new FakeTime();
        var reach = new ReachMemory(time: time);
        Assert.AreEqual(TimeSpan.FromSeconds(10), ReachMemory.RetryEvery);
        reach.Remember(Gone);
        time.Advance(ReachMemory.RetryEvery);

        using (var first = reach.Enter())
        {
            first.ThrowIfFoundSince();
            Refused(reach);
            Refused(reach);

            // however long its try takes: a connect's timeout may be longer than the ten seconds
            time.Advance(TimeSpan.FromSeconds(30));
            Refused(reach);
            reach.Remember(Gone);
        }

        Refused(reach);
        time.Advance(TimeSpan.FromSeconds(9));
        Refused(reach);
        time.Advance(TimeSpan.FromSeconds(1));
        using (reach.Enter())
        {
            Refused(reach);
        }

        // a turn given back with nothing learnt (its caller gave up) is the next caller's, with no ten seconds to wait
        using (reach.Enter())
        {
            Refused(reach);
        }
    }

    [TestMethod]
    public void TheTenSecondsAreTheConstructorsToShorten()
    {
        var time = new FakeTime();
        var reach = new ReachMemory(TimeSpan.FromSeconds(1.5), time);
        reach.Remember(Gone);
        Assert.AreEqual(Gone + " (tried again every 1.5 s)", Refused(reach).Message);
        time.Advance(TimeSpan.FromSeconds(1.5));
        using var turn = reach.Enter();
        Refused(reach);
    }

    [TestMethod]
    public void ATryThatReachesThePlaceClearsTheMemory()
    {
        var time = new FakeTime();
        var reach = new ReachMemory(time: time);
        reach.Remember(Gone);
        time.Advance(ReachMemory.RetryEvery);
        using (reach.Enter())
        {
            Refused(reach);
            reach.Forget();
        }

        // everyone is let through again, at the same time
        using var one = reach.Enter();
        using var other = reach.Enter();
        one.ThrowIfFoundSince();

        // and what is found after that is remembered from then
        time.Advance(TimeSpan.FromMinutes(5));
        reach.Remember(Gone);
        Refused(reach);
        time.Advance(ReachMemory.RetryEvery);
        using var again = reach.Enter();
        Refused(reach);
    }

    [TestMethod]
    public void OneWhoWaitedWhileThePlaceWasFoundUnreachableIsRefusedLikeThoseWhoComeAfter()
    {
        var time = new FakeTime();
        var reach = new ReachMemory(time: time);

        // two set out while nothing is remembered, the second waiting behind the first (a lock that lets one connect at a time)
        using var first = reach.Enter();
        using var waiting = reach.Enter();
        waiting.ThrowIfFoundSince();
        reach.Remember(Gone);
        var ex = Assert.ThrowsExactly<VfsException>(waiting.ThrowIfFoundSince);
        Assert.AreEqual(VfsError.Unavailable, ex.Error);
        Assert.AreEqual(Gone + " (tried again every 10 s)", ex.Message);

        // the one let through is the try itself: never refused here, whatever is remembered
        time.Advance(ReachMemory.RetryEvery);
        using var through = reach.Enter();
        through.ThrowIfFoundSince();
        Assert.ThrowsExactly<VfsException>(waiting.ThrowIfFoundSince);
    }

    [TestMethod]
    public void ManyAskingAtOnceLetExactlyOneThroughEachTenSeconds()
    {
        const int Callers = 16;
        var time = new FakeTime();
        var reach = new ReachMemory(time: time);
        reach.Remember(Gone);
        time.Advance(ReachMemory.RetryEvery);
        for (var window = 0; window < 25; window++)
        {
            var through = new List<ReachMemory.Turn>();
            var refused = 0;
            using var together = new Barrier(Callers);
            var threads = Enumerable.Range(0, Callers).Select(_ => new Thread(() =>
            {
                together.SignalAndWait();
                if (reach.TryEnter(out var turn, out var refusal))
                {
                    lock (through)
                    {
                        through.Add(turn);
                    }
                }
                else if (refusal.Error == VfsError.Unavailable)
                {
                    Interlocked.Increment(ref refused);
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.AreEqual(1, through.Count, $"let through at once, the {window + 1}. time");
            Assert.AreEqual(Callers - 1, refused);

            // one finds the place unreachable again, and ten seconds go by; the next gives up without a word, and its turn
            // is there for the taking at once
            if (window % 2 == 0)
            {
                reach.Remember(Gone);
                through[0].Dispose();
                time.Advance(ReachMemory.RetryEvery);
            }
            else
            {
                through[0].Dispose();
            }
        }
    }
}
