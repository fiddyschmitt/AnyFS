using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

[TestClass]
public class ReadAheadTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Pass(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    // one open from its asking to its answer, as the sources make them, whose read takes so many seconds
    private static async Task<bool> OpenAsync(ReadAhead ahead, string place, Clock? clock = null, double readSeconds = 0)
    {
        using var opening = ahead.ForOneThing();
        await Task.Yield();
        var wanted = ahead.Wanted(place);
        clock?.Pass(readSeconds);
        return wanted;
    }

    [TestMethod]
    public void AListingGoesByTheListingBeforeItInTheSamePlace()
    {
        var clock = new Clock();
        var ahead = new ReadAhead(clock);
        Assert.IsFalse(ahead.Wanted("a"), "the first there");
        Assert.IsTrue(ahead.Wanted("a"), "the next, moments after");
        Assert.IsFalse(ahead.Wanted("b"), "the first in another place");
        clock.Pass(9);
        Assert.IsTrue(ahead.Wanted("a"), "however long the read took, within ten seconds");
        clock.Pass(11);
        Assert.IsFalse(ahead.Wanted("a"), "too long after");
    }

    [TestMethod]
    public async Task AnOpenGoesByTheOpensBeforeItWhateverIsListedBesideIt()
    {
        var ahead = new ReadAhead(new Clock());
        ahead.Wanted("a");
        Assert.IsTrue(ahead.Wanted("a"), "a client going through the folders");
        Assert.IsFalse(await OpenAsync(ahead, "a"), "someone waiting for one file");
        Assert.IsFalse(await OpenAsync(ahead, "a"), "or two");
        Assert.IsTrue(await OpenAsync(ahead, "a"), "the third at once: a client going through the files");
        Assert.IsTrue(await OpenAsync(ahead, "a"), "and on");
    }

    [TestMethod]
    public async Task ListingsGoByListingsWhateverIsOpenedBesideThem()
    {
        var ahead = new ReadAhead(new Clock());
        for (var i = 0; i < 3; i++)
        {
            await OpenAsync(ahead, "a");
        }

        Assert.IsFalse(ahead.Wanted("a"), "the first listing there");
    }

    [TestMethod]
    public async Task AProgramsNextOpenComesAtOnceHoweverLongTheLastTook()
    {
        var clock = new Clock();
        var ahead = new ReadAhead(clock);
        Assert.IsFalse(await OpenAsync(ahead, "a", clock, readSeconds: 30));
        Assert.IsFalse(await OpenAsync(ahead, "a", clock, readSeconds: 30));
        Assert.IsTrue(await OpenAsync(ahead, "a", clock, readSeconds: 30), "counted from the answer to the one before");
    }

    [TestMethod]
    public async Task APersonsNextOpenDoesNot()
    {
        var clock = new Clock();
        var ahead = new ReadAhead(clock);
        for (var i = 0; i < 6; i++)
        {
            Assert.IsFalse(await OpenAsync(ahead, "a", clock, readSeconds: 3), "the same folder, one after another");
            clock.Pass(3);
        }
    }

    [TestMethod]
    public async Task OpensAskedForTogetherAreARun()
    {
        var ahead = new ReadAhead(new Clock());
        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => OpenAsync(ahead, "a")));
        Assert.IsTrue(answers.Any(a => a), "several at once, from a client with several threads");
    }

    [TestMethod]
    public async Task OnePlacesOpensSayNothingOfAnothers()
    {
        var ahead = new ReadAhead(new Clock());
        for (var i = 0; i < 3; i++)
        {
            await OpenAsync(ahead, "a");
        }

        Assert.IsFalse(await OpenAsync(ahead, "b"), "someone opening a file while a client searches another folder");
    }

    [TestMethod]
    public async Task ForOneThingEndsWithTheCallThatSaidSo()
    {
        var ahead = new ReadAhead(new Clock());
        await OpenAsync(ahead, "a");
        Assert.IsFalse(ahead.Wanted("b"));
        Assert.IsTrue(ahead.Wanted("b"), "a listing's answer: the second there, where an open would be the first of a run");
    }

    [TestMethod]
    public async Task WhatAnOpenSetsOffIsForThatOpen()
    {
        var ahead = new ReadAhead(new Clock());
        ahead.Wanted("a");
        using var opening = ahead.ForOneThing();
        Assert.IsFalse(await Task.Run(() => ahead.Wanted("a")), "a stat lists the folder of a file on a task of its own");
    }

    [TestMethod]
    public async Task AfterAReadOfSeveralFailsAllIsReadAloneForAWhile()
    {
        var clock = new Clock();
        var ahead = new ReadAhead(clock);
        ahead.Wanted("a");
        Assert.IsTrue(ahead.Wanted("a"));
        ahead.Failed();
        Assert.IsFalse(ahead.Wanted("a"));
        for (var i = 0; i < 3; i++)
        {
            Assert.IsFalse(await OpenAsync(ahead, "b"));
        }

        clock.Pass(TimeSpan.FromMinutes(10).TotalSeconds + 1);
        ahead.Wanted("a");
        Assert.IsTrue(ahead.Wanted("a"), "and tried again");
    }

    [TestMethod]
    public async Task AListingWaitsForThoseBesideIt()
    {
        var ahead = new ReadAhead(new Clock());
        var held = new TaskCompletionSource();
        var waiting = ahead.ReadAsync("several", () => held.Task, CancellationToken.None);
        await Task.Delay(50);
        Assert.IsFalse(waiting.IsCompleted);
        held.SetResult();
        await waiting;
    }

    [TestMethod]
    public async Task AnOpenWaitsForNoOthersUnlessTheyAreOnTheirWay()
    {
        var ahead = new ReadAhead(new Clock());
        var held = new TaskCompletionSource();
        var reads = 0;
        Task Read()
        {
            Interlocked.Increment(ref reads);
            return held.Task;
        }

        using var opening = ahead.ForOneThing();
        await ahead.ReadAsync("several", Read, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref reads) == 1, TimeSpan.FromSeconds(20)), "they come behind it");

        // the next from them, as a client going straight through asks for it
        var next = ahead.ReadAsync("several", Read, CancellationToken.None);
        await Task.Delay(50);
        Assert.IsFalse(next.IsCompleted, "it waits for them, not to read them again");
        held.SetResult();
        await next;
        Assert.AreEqual(1, Volatile.Read(ref reads));
    }

    [TestMethod]
    public async Task OpensStartOnlyAFewBehindThem()
    {
        var ahead = new ReadAhead(new Clock());
        var held = new TaskCompletionSource();
        var reads = 0;
        Task Read()
        {
            Interlocked.Increment(ref reads);
            return held.Task;
        }

        using (ahead.ForOneThing())
        {
            for (var i = 0; i < 6; i++)
            {
                await ahead.ReadAsync($"several {i}", Read, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
            }
        }

        Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref reads) == 2, TimeSpan.FromSeconds(20)));
        await Task.Delay(50);
        Assert.AreEqual(2, Volatile.Read(ref reads), "each holds a request slot, and whoever opens next is behind them");
        held.SetResult();
    }

    [TestMethod]
    public async Task WhatWasReadTogetherCanBeReadAgain()
    {
        var ahead = new ReadAhead(new Clock());
        var reads = 0;
        for (var i = 0; i < 2; i++)
        {
            await ahead.ReadAsync("several", () =>
            {
                reads++;
                return Task.CompletedTask;
            }, CancellationToken.None);
        }

        Assert.AreEqual(2, reads);
    }

    [TestMethod]
    public void WhatIsRememberedOfPlacesIsBounded()
    {
        var ahead = new ReadAhead(new Clock());
        ahead.Wanted("a");
        for (var i = 0; i < 2_100; i++)
        {
            ahead.Wanted($"place {i}");
        }

        Assert.IsFalse(ahead.Wanted("a"), "started again");
    }
}
