using AnyFS.Sdk;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

[TestClass]
public class RequestCacheTests
{
    /// <summary>
    /// Past its limit the cache lets its oldest go, while others go on asking: a count of 3,500 GitLab projects got there, and some
    /// of its folders failed with what sorting the dictionary as it grew threw (a NullReferenceException, an ArgumentException).
    /// </summary>
    [TestMethod]
    public async Task ManyAskingAtOncePastItsLimitAllGetTheirAnswers()
    {
        var cache = new RequestCache(CancellationToken.None);
        var next = 0;
        await Task.WhenAll(Enumerable.Range(0, Math.Max(8, Environment.ProcessorCount)).Select(_ => Task.Run(async () =>
        {
            int key;
            while ((key = Interlocked.Increment(ref next)) <= RequestCache.MaxEntries * 3)
            {
                Assert.AreEqual("answer", await cache.GetAsync($"key{key}", null, _ => Task.FromResult("answer"), CancellationToken.None));
            }
        })));
    }

    [TestMethod]
    public async Task PastItsLimitItsOldestGo()
    {
        var cache = new RequestCache(CancellationToken.None);
        for (var key = 0; key <= RequestCache.MaxEntries; key++)
        {
            await cache.GetAsync($"key{key}", null, _ => Task.FromResult("answer"), CancellationToken.None);
        }

        Assert.IsNull(cache.Peek<string>("key0"), "the oldest let go");
        Assert.AreEqual("answer", cache.Peek<string>($"key{RequestCache.MaxEntries}"), "the newest kept");
    }
    /// <summary>A person's call under the host's limit, on a flow of its own: asks, and is given up when told to.</summary>
    private static (Task<Exception?> Asked, Action GiveUp) AskedByAPerson(RequestCache cache, string key, Func<CancellationToken, Task<string>> fetch)
    {
        var waiting = new UpstreamPatience.Waiting(TimeSpan.FromSeconds(1));
        var call = new CancellationTokenSource();
        var asked = Task.Run(async () =>
        {
            UpstreamPatience.Put(waiting);
            try
            {
                await cache.GetAsync(key, null, fetch, call.Token);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });
        return (asked, () =>
        {
            waiting.GiveUp();
            call.Cancel();
        });
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            Assert.IsTrue(waited < 5000, "timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    [TestMethod]
    public async Task ARequestNoOneWaitsForAfterItsAskerWasGivenUpIsStopped()
    {
        // The host gives a person's call up for a server that says nothing. The request the call waited on gave up its
        // place, so the next to ask sent one of its own, and then went on to the end of its own time, holding one of the
        // source's places: a source whose every place was held so answered again only as they timed out (the review of
        // 21-Nov-2026, R6)
        using var stopping = new CancellationTokenSource();
        var cache = new RequestCache(stopping.Token);
        var started = new SemaphoreSlim(0);
        var stopped = 0;
        async Task<string> NeverAnswers(CancellationToken ct)
        {
            started.Release();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref stopped);
                throw;
            }

            return "never";
        }

        // one asker, given up: no one waits for the request any more
        var (alone, giveUp) = AskedByAPerson(cache, "alone", NeverAnswers);
        Assert.IsTrue(await started.WaitAsync(TimeSpan.FromSeconds(10)));
        giveUp();
        Assert.IsInstanceOfType<OperationCanceledException>(await alone);
        await UntilAsync(() => Volatile.Read(ref stopped) == 1, "the request no one waits for stopped");

        // two askers of one request: the first given up, the second (a walk, under no limit) waits on, and so does the request
        var (first, giveUpFirst) = AskedByAPerson(cache, "shared", NeverAnswers);
        Assert.IsTrue(await started.WaitAsync(TimeSpan.FromSeconds(10)));
        using var walk = new CancellationTokenSource();
        var second = cache.GetAsync("shared", null, NeverAnswers, walk.Token);
        await Task.Delay(100);
        giveUpFirst();
        Assert.IsInstanceOfType<OperationCanceledException>(await first);
        await Task.Delay(200);
        Assert.AreEqual(1, Volatile.Read(ref stopped), "a request someone still waits for goes on");
        Assert.IsFalse(second.IsCompleted);

        // the second leaves too (its walk stopped): the request had given up its place, so no one can come to wait for it
        await walk.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => second);
        await UntilAsync(() => Volatile.Read(ref stopped) == 2, "the request stopped once the last has left");

        // an asker that goes away of itself (its client did) leaves the request to whoever asks next, as before
        using var client = new CancellationTokenSource();
        var gone = cache.GetAsync("kept", null, NeverAnswers, client.Token);
        Assert.IsTrue(await started.WaitAsync(TimeSpan.FromSeconds(10)));
        await client.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => gone);
        await Task.Delay(200);
        Assert.AreEqual(2, Volatile.Read(ref stopped), "a request that kept its place goes on for the next to ask");

        // and the source's own stop still ends whatever is on its way
        await stopping.CancelAsync();
        await UntilAsync(() => Volatile.Read(ref stopped) == 3, "the source's stop ending the last request");
    }

    // --- what it holds, by weight (the review of 21-Nov-2026, P6) -----------------------------------------------------

    private const long Kilo = 1024;

    /// <summary>An answer of <paramref name="bytes"/> bytes read for a fetch, as a source's client says it.</summary>
    private static Task<string> Answer(long bytes, string value = "answer")
    {
        RequestCache.Received(bytes);
        return Task.FromResult(value);
    }

    [TestMethod]
    public async Task AnAnswerWeighsByWhatWasReadForIt()
    {
        var cache = new RequestCache(CancellationToken.None);
        Assert.AreEqual((0, 0L), (cache.Count, cache.Bytes));

        // half as much again as the bytes read for it, and the least an entry costs
        await cache.GetAsync("read", null, _ => Answer(10 * Kilo), CancellationToken.None);
        Assert.AreEqual(RequestCache.LeastWeight + (15 * Kilo), cache.Bytes);

        // through every request of the fetch, and those it makes at once
        await cache.GetAsync("pages", null, async _ =>
        {
            await Answer(4 * Kilo);
            await Task.Yield();
            await Task.WhenAll(Task.Run(() => Answer(2 * Kilo)), Task.Run(() => Answer(2 * Kilo)));
            return "answer";
        }, CancellationToken.None);
        Assert.AreEqual((2 * RequestCache.LeastWeight) + (15 * Kilo) + (12 * Kilo), cache.Bytes);

        // text and bytes are what they are, where that is more than was read for them; a box weighs what is in it
        await cache.GetAsync("text", null, _ => Task.FromResult(new string('x', 5000)), CancellationToken.None);
        await cache.GetAsync("bytes", null, _ => Task.FromResult(new byte[3000]), CancellationToken.None);
        await cache.GetAsync("boxed", null, _ => Task.FromResult(new Box<string>(new string('y', 1000))), CancellationToken.None);
        await cache.GetAsync("small", null, _ => Task.FromResult(new Box<long>(7)), CancellationToken.None);
        Assert.AreEqual((6 * RequestCache.LeastWeight) + (27 * Kilo) + 10_000 + 3000 + 2000, cache.Bytes);
        Assert.AreEqual(6, cache.Count);

        // what was read for one fetch is not another's, nor anyone's outside a fetch
        RequestCache.Received(1 << 20);
        await cache.GetAsync("outer", null, async c =>
        {
            await cache.GetAsync("inner", null, _ => Answer(8 * Kilo), c);
            return await Answer(2 * Kilo);
        }, CancellationToken.None);
        Assert.AreEqual((8 * RequestCache.LeastWeight) + (27 * Kilo) + 15_000 + (12 * Kilo) + (3 * Kilo), cache.Bytes);

        // and what leaves takes its weight with it: forgotten, replaced, or failed
        cache.Forget("inner");
        cache.Forget("outer");
        cache.Forget("no such key");
        Assert.AreEqual((6 * RequestCache.LeastWeight) + (27 * Kilo) + 15_000, cache.Bytes);
        await Assert.ThrowsExactlyAsync<VfsException>(() => cache.GetAsync<string>("failed", null, _ =>
        {
            RequestCache.Received(1 << 20);
            throw VfsException.Unavailable("no");
        }, CancellationToken.None));
        Assert.AreEqual((6, (6 * RequestCache.LeastWeight) + (27 * Kilo) + 15_000), (cache.Count, cache.Bytes));
    }

    [TestMethod]
    public async Task PastWhatItMayWeighTheOldestGoAndWhatIsPastItsTimeGoesFirst()
    {
        // Twenty thousand answers of any size were kept, and one past its time went only when it was asked for again: a
        // GitLab's notes and diffs, a Confluence's comments, a wiki read whole stayed for the run
        var cache = new RequestCache(CancellationToken.None, maxBytes: 1000 * Kilo);
        var each = RequestCache.LeastWeight + (15 * Kilo);
        for (var key = 0; key < 20; key++)
        {
            await cache.GetAsync($"short{key}", TimeSpan.FromMilliseconds(1), _ => Answer(10 * Kilo), CancellationToken.None);
        }

        for (var key = 0; key < 20; key++)
        {
            await cache.GetAsync($"kept{key}", null, _ => Answer(10 * Kilo), CancellationToken.None);
        }

        Assert.AreEqual((40, 40 * each), (cache.Count, cache.Bytes), "under what it may weigh, all are there, those past their time too");
        await Task.Delay(50);

        // 25 more reach the bound: what is past its time goes, which is enough, and nothing in its time does
        for (var key = 0; key < 25; key++)
        {
            await cache.GetAsync($"more{key}", null, _ => Answer(10 * Kilo), CancellationToken.None);
        }

        Assert.AreEqual((45, 45 * each), (cache.Count, cache.Bytes));
        Assert.AreEqual("answer", cache.Peek<string>("kept19"));
        Assert.AreEqual("answer", cache.Peek<string>("more24"));

        // and past it again with nothing past its time, what was longest unasked for goes, down to four fifths of the
        // bound: not the oldest, which is asked for all the while (a server's list of projects, as old as the run)
        for (var key = 25; key < 45; key++)
        {
            Assert.AreEqual("answer", await cache.GetAsync("kept1", null, _ => Answer(0, "asked again"), CancellationToken.None));
            Assert.AreEqual("answer", cache.Peek<string>("kept2"));
            await cache.GetAsync($"more{key}", null, _ => Answer(10 * Kilo), CancellationToken.None);
        }

        Assert.IsTrue(cache.Bytes <= 1000 * Kilo, $"{cache.Bytes:N0} B kept");
        Assert.IsNull(cache.Peek<string>("kept0"), "the longest unasked for let go");
        Assert.IsNull(cache.Peek<string>("kept3"));
        Assert.AreEqual("answer", cache.Peek<string>("kept1"), "one asked for all the while kept, old as it is");
        Assert.AreEqual("answer", cache.Peek<string>("kept2"));
        Assert.AreEqual("answer", cache.Peek<string>("more44"), "the newest kept");
        Assert.AreEqual(cache.Count * each, cache.Bytes, "what is kept is what is weighed");
        Assert.IsTrue(cache.Count >= 800 * Kilo / each - 1, $"{cache.Count} kept: no more went than had to");
    }

    [TestMethod]
    public async Task WhatIsPutIsFoundByItsKeyAndWeighsWhatItsCallerSays()
    {
        // one of many a single request brought (a page of a search read ahead): no fetch of its own stands for it
        var cache = new RequestCache(CancellationToken.None, maxBytes: 100 * Kilo);
        cache.Put("issue:A-1", new Box<string>("one"), TimeSpan.FromMinutes(1), 20 * Kilo);
        Assert.AreEqual("one", cache.Peek<Box<string>>("issue:A-1")?.Value);
        Assert.AreEqual((1, RequestCache.LeastWeight + (20 * Kilo)), (cache.Count, cache.Bytes));

        // an asking by the same key is answered by it, with nothing fetched
        var fetched = 0;
        Assert.AreEqual("one", (await cache.GetAsync("issue:A-1", TimeSpan.FromMinutes(1), _ =>
        {
            fetched++;
            return Task.FromResult(new Box<string>("fetched"));
        }, CancellationToken.None)).Value);
        Assert.AreEqual(0, fetched);

        // put again, it takes the place and the weight of what was there
        cache.Put("issue:A-1", new Box<string>("two"), TimeSpan.FromMinutes(1), 30 * Kilo);
        Assert.AreEqual("two", cache.Peek<Box<string>>("issue:A-1")?.Value);
        Assert.AreEqual((1, RequestCache.LeastWeight + (30 * Kilo)), (cache.Count, cache.Bytes));

        // past its time it answers no more, and the asking fetches
        cache.Put("issue:A-2", new Box<string>("brief"), TimeSpan.FromMilliseconds(1), Kilo);
        await Task.Delay(50);
        Assert.IsNull(cache.Peek<Box<string>>("issue:A-2"));
        Assert.AreEqual("fetched", (await cache.GetAsync("issue:A-2", TimeSpan.FromMinutes(1), _ => Task.FromResult(new Box<string>("fetched")), CancellationToken.None)).Value);

        // and those put count against what the cache may weigh as those fetched do
        for (var n = 3; n < 20; n++)
        {
            cache.Put($"issue:A-{n}", new Box<string>("more"), TimeSpan.FromMinutes(1), 20 * Kilo);
        }

        Assert.IsTrue(cache.Bytes <= 100 * Kilo, $"{cache.Bytes:N0} B kept");
        Assert.IsNull(cache.Peek<Box<string>>("issue:A-1"), "the oldest let go");
        Assert.AreEqual("more", cache.Peek<Box<string>>("issue:A-19")?.Value);
    }

    [TestMethod]
    public void ManyPuttingAtOncePastTheBoundAreTrimmedWithoutAFault()
    {
        // (as Jira's whole issues and Confluence's whole pages were, in maps of their own, whose sort as they grew threw)
        const int Threads = 4;
        const int Each = 5_000;
        var cache = new RequestCache(CancellationToken.None, maxBytes: 2000 * Kilo);
        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var go = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
        {
            go.Wait();
            try
            {
                for (var n = 0; n < Each; n++)
                {
                    cache.Put($"{t}-{n}", new Box<string>("page"), TimeSpan.FromMinutes(1), 4 * Kilo);
                }
            }
            catch (Exception ex)
            {
                faults.Enqueue(ex);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        go.Set();
        threads.ForEach(t => t.Join());
        Assert.AreEqual(0, faults.Count, faults.FirstOrDefault()?.ToString());

        // a trim under way lets the others add without one of their own, so more may be kept just now: one more trims again
        for (var n = 0; cache.Bytes > 2000 * Kilo && n < Threads * Each; n++)
        {
            cache.Put($"more-{n}", new Box<string>("page"), TimeSpan.FromMinutes(1), 4 * Kilo);
        }

        Assert.IsTrue(cache.Bytes <= 2000 * Kilo, $"{cache.Bytes:N0} B kept");
        Assert.AreEqual(cache.Count * (RequestCache.LeastWeight + (4 * Kilo)), cache.Bytes, "and what is kept is what is weighed, after all that");
    }

    [TestMethod]
    public async Task AnAnswerThatComesAfterItsEntryLeftWeighsNothing()
    {
        var cache = new RequestCache(CancellationToken.None);
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = cache.GetAsync("slow", null, async _ =>
        {
            RequestCache.Received(1 << 20);
            return await answer.Task;
        }, CancellationToken.None);
        await UntilAsync(() => cache.Count == 1, "the fetch taking its place");
        cache.Forget("slow");
        answer.SetResult("late");
        Assert.AreEqual("late", await asked, "whoever waited is answered");
        Assert.AreEqual((0, 0L), (cache.Count, cache.Bytes));
    }
}
