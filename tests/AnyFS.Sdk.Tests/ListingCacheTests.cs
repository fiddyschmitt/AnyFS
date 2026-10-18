using System.Runtime.CompilerServices;

namespace AnyFS.Sdk.Tests;

[TestClass]
public class ListingCacheTests
{
    private readonly FakeTime _time = new();
    private readonly Dictionary<string, List<VfsEntry>> _tree = new(StringComparer.Ordinal)
    {
        [""] = [VfsEntry.Directory("docs"), VfsEntry.File("readme.txt", 10)],
        ["docs"] = [VfsEntry.File("a.txt", 1), VfsEntry.File("b.txt", 2)],
        ["docs/sub"] = [VfsEntry.File("c.txt", 3)],
    };
    private int _calls;
    private TaskCompletionSource? _gate;

    private async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        if (_gate is not null)
        {
            await _gate.Task;
        }

        if (!_tree.TryGetValue(path, out var entries))
        {
            throw VfsException.NotFound(path);
        }

        foreach (var e in entries)
        {
            yield return e;
        }
    }

    [TestMethod]
    public async Task CachesWithinTtlAndExpiresAfter()
    {
        var cache = new ListingCache(TimeSpan.FromSeconds(2), ListAsync, time: _time);
        var first = await cache.GetAsync("docs", CancellationToken.None);
        var second = await cache.GetAsync("docs", CancellationToken.None);
        Assert.AreEqual(1, _calls);
        Assert.AreSame(first, second);

        _time.Advance(TimeSpan.FromSeconds(3));
        await cache.GetAsync("docs", CancellationToken.None);
        Assert.AreEqual(2, _calls);
    }

    [TestMethod]
    public async Task UpdateChangesOneEntryWithoutAnotherListing()
    {
        var cache = new ListingCache(TimeSpan.FromSeconds(2), ListAsync, time: _time);
        await cache.GetAsync("docs", CancellationToken.None);

        cache.Update("docs/c.txt", VfsEntry.File("c.txt", 3));                       // added
        cache.Update("docs/A.TXT", VfsEntry.File("a.txt", 11), StringComparison.OrdinalIgnoreCase);   // replaced, by name case-insensitively
        cache.Update("docs/b.txt", null);                                            // removed
        var listing = await cache.GetAsync("docs", CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { "a.txt", "c.txt" }, listing.Select(e => e.Name).ToArray());
        Assert.AreEqual(11, listing.Single(e => e.Name == "a.txt").Length);
        Assert.AreEqual(3, (await cache.StatAsync("docs/c.txt", CancellationToken.None))!.Length);
        Assert.AreEqual(1, _calls, "no listing call for the updates");

        cache.Update("docs/sub/x.txt", VfsEntry.File("x.txt", 1));                   // not cached: nothing happens, no call either
        Assert.AreEqual(1, _calls);

        _time.Advance(TimeSpan.FromSeconds(3));                                      // the updated listing expires when the original would have
        var fresh = await cache.GetAsync("docs", CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { "a.txt", "b.txt" }, fresh.Select(e => e.Name).ToArray());
        Assert.AreEqual(2, _calls);
    }

    [TestMethod]
    public async Task ConcurrentMissesShareOneUpstreamCall()
    {
        _gate = new TaskCompletionSource();
        var cache = new ListingCache(TimeSpan.FromSeconds(2), ListAsync, time: _time);
        var tasks = Enumerable.Range(0, 8).Select(_ => cache.GetAsync("docs", CancellationToken.None).AsTask()).ToList();
        await Task.Delay(50);
        Assert.AreEqual(1, _calls);
        _gate.SetResult();
        var results = await Task.WhenAll(tasks);
        Assert.AreEqual(1, _calls);
        Assert.IsTrue(results.All(r => r.Count == 2));
    }

    [TestMethod]
    public async Task ACancelledCallerDoesNotPoisonTheSharedListing()
    {
        _gate = new TaskCompletionSource();
        var cache = new ListingCache(TimeSpan.FromSeconds(2), ListAsync, time: _time);
        using var cts = new CancellationTokenSource();
        var cancelled = cache.GetAsync("docs", cts.Token).AsTask();
        var waiting = cache.GetAsync("docs", CancellationToken.None).AsTask();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelled);
        _gate.SetResult();
        Assert.AreEqual(2, (await waiting).Count);
        Assert.AreEqual(1, _calls);
    }

    [TestMethod]
    public async Task ErrorsAreNotCached()
    {
        var cache = new ListingCache(TimeSpan.FromSeconds(2), ListAsync, time: _time);
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("nope", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("nope", CancellationToken.None));
        Assert.AreEqual(2, _calls);
        Assert.AreEqual(0, cache.Count);
    }

    [TestMethod]
    public async Task StatUsesTheParentListing()
    {
        var cache = new ListingCache(TimeSpan.FromSeconds(2), ListAsync, time: _time);
        var entry = await cache.StatAsync("docs/b.txt", CancellationToken.None);
        Assert.AreEqual(2, entry!.Length);
        Assert.IsNull(await cache.StatAsync("docs/B.TXT", CancellationToken.None));
        Assert.IsNotNull(await cache.StatAsync("docs/B.TXT", CancellationToken.None, StringComparison.OrdinalIgnoreCase));
        Assert.IsNull(await cache.StatAsync("nope/x", CancellationToken.None));
        Assert.IsTrue((await cache.StatAsync("", CancellationToken.None))!.IsDirectory);
        Assert.AreEqual(2, _calls, "the stats of docs/* came from one listing; 'nope' cost one failed listing");
    }

    [TestMethod]
    public async Task InvalidationVariants()
    {
        var cache = new ListingCache(TimeSpan.FromMinutes(1), ListAsync, time: _time);
        await cache.GetAsync("", CancellationToken.None);
        await cache.GetAsync("docs", CancellationToken.None);
        await cache.GetAsync("docs/sub", CancellationToken.None);
        Assert.AreEqual(3, cache.Count);

        cache.InvalidateParentOf("docs/sub/c.txt");
        Assert.AreEqual(2, cache.Count);
        await cache.GetAsync("docs/sub", CancellationToken.None);
        Assert.AreEqual(4, _calls);

        cache.InvalidateTree("docs");
        Assert.AreEqual(1, cache.Count);

        cache.Invalidate("");
        Assert.AreEqual(0, cache.Count);

        await cache.GetAsync("docs", CancellationToken.None);
        cache.InvalidateAll();
        Assert.AreEqual(0, cache.Count);
    }

    [TestMethod]
    public async Task TrimsWhenOverTheDirectoryLimit()
    {
        for (var i = 0; i < 40; i++)
        {
            _tree[$"d{i}"] = [VfsEntry.File("x", 1)];
        }

        var cache = new ListingCache(TimeSpan.FromMinutes(1), ListAsync, maxDirectories: 10, time: _time);
        for (var i = 0; i < 40; i++)
        {
            await cache.GetAsync($"d{i}", CancellationToken.None);
        }

        Assert.IsTrue(cache.Count <= 10, $"count was {cache.Count}");
    }
}
