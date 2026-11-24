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
}
