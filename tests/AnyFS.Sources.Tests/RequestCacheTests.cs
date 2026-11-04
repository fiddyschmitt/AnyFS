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
}
