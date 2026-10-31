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
    public async Task AKeptListingStaysUntilListedAfresh()
    {
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time);
        await cache.GetAsync("docs", CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(5));
        await cache.GetAsync("docs", CancellationToken.None);
        Assert.AreEqual(1, _calls, "no expiry");

        _tree["docs"].Add(VfsEntry.File("new.txt", 4));
        var fresh = await cache.RefreshAsync("docs", CancellationToken.None);
        Assert.AreEqual(2, _calls);
        CollectionAssert.Contains(fresh.Select(e => e.Name).ToList(), "new.txt");
        CollectionAssert.Contains((await cache.GetAsync("docs", CancellationToken.None)).Select(e => e.Name).ToList(), "new.txt", "the fresh listing replaced the old");
        Assert.AreEqual(2, _calls);
    }

    [TestMethod]
    public async Task AKeptListingIsNotListedAfreshForBeingListedAgainSoon()
    {
        // a program opening a file lists its folder over and over: no sign that anything changed
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time);
        await cache.GetAsync("docs", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        await cache.GetAsync("docs", CancellationToken.None);
        Assert.AreEqual(1, _calls);

        // what a plugin built against SDK 0.2 calls: the refresh window it gives is ignored
#pragma warning disable CS0618
        var old = new ListingCache(ListingCache.Kept, ListAsync, 10_000, _time, TimeSpan.FromSeconds(5));
        await old.ListAsync("docs", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        await old.ListAsync("docs", CancellationToken.None);
#pragma warning restore CS0618
        Assert.AreEqual(2, _calls, "one listing for the second cache too");
    }

    [TestMethod]
    public async Task AListingOlderThanRefreshAfterIsAnsweredAndListedAfreshBehindTheAnswer()
    {
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time) { RefreshAfter = TimeSpan.FromSeconds(10) };
        await cache.GetAsync("docs", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(5));
        await cache.GetAsync("docs", CancellationToken.None);
        Assert.AreEqual(1, _calls, "young enough");

        _tree["docs"].Add(VfsEntry.File("new.txt", 4));
        _time.Advance(TimeSpan.FromSeconds(6));
        _gate = new TaskCompletionSource();
        Assert.AreEqual(2, (await cache.GetAsync("docs", CancellationToken.None)).Count, "answered at once from what was held");
        await cache.GetAsync("docs", CancellationToken.None);
        await WaitForAsync(() => _calls == 2);
        _gate.SetResult();
        await WaitForAsync(async () => (await cache.GetAsync("docs", CancellationToken.None)).Count == 3);
        Assert.AreEqual(2, _calls, "one listing behind, however many asked meanwhile");

        _time.Advance(TimeSpan.FromSeconds(9));
        await cache.GetAsync("docs", CancellationToken.None);
        Assert.AreEqual(2, _calls, "the fresh listing is young again");
        _tree["docs"].RemoveAt(2);
    }

    [TestMethod]
    public async Task AListingBehindTheAnswerThatFailsLeavesWhatWasHeldAndTheNextWaitsAsLongAgain()
    {
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time) { RefreshAfter = TimeSpan.FromSeconds(10) };
        await cache.GetAsync("docs", CancellationToken.None);
        var held = _tree["docs"];
        _tree.Remove("docs");
        _time.Advance(TimeSpan.FromSeconds(11));
        Assert.AreEqual(2, (await cache.GetAsync("docs", CancellationToken.None)).Count);
        await WaitForAsync(() => _calls == 2);
        Assert.AreEqual(2, (await cache.GetAsync("docs", CancellationToken.None)).Count, "what was held still answers");
        Assert.AreEqual(2, _calls, "not asked again at once");

        // as long again: asked behind the next answer, once the failed listing has ended
        _tree["docs"] = held;
        _time.Advance(TimeSpan.FromSeconds(11));
        await WaitForAsync(async () =>
        {
            await cache.GetAsync("docs", CancellationToken.None);
            return _calls == 3;
        });
    }

    [TestMethod]
    public async Task AnOldListingWhoseStampHoldsIsNotListedAfreshBehindTheAnswer()
    {
        var stamps = new Dictionary<string, string>(StringComparer.Ordinal) { ["docs"] = "v1" };
        var asked = new List<string?>();
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time)
        {
            RefreshAfter = TimeSpan.FromSeconds(10),
            StampOf = (path, held, _) =>
            {
                asked.Add(held);
                return ValueTask.FromResult(stamps.GetValueOrDefault(path));
            },
        };

        // restored from a run before, with its stamp: answered at once, and the stamp holds, so not listed
        cache.Restore("docs", [VfsEntry.File("old.txt", 1)], "v1");
        Assert.AreEqual(1, (await cache.GetAsync("docs", CancellationToken.None)).Count);
        await Task.Delay(100);
        Assert.AreEqual(0, _calls, "the stamp holds: the held listing stands");
        CollectionAssert.AreEqual(new[] { "v1" }, asked, "asked with the stamp held");
        Assert.AreEqual("v1", cache.StampHeld("docs"));

        // the stamp moved: listed afresh behind the next answer, the new stamp kept with the listing
        stamps["docs"] = "v2";
        _time.Advance(TimeSpan.FromSeconds(11));
        Assert.AreEqual(1, (await cache.GetAsync("docs", CancellationToken.None)).Count, "answered from what was held");
        await WaitForAsync(() => _calls == 1);
        await WaitForAsync(async () => (await cache.GetAsync("docs", CancellationToken.None)).Count == 2);
        Assert.AreEqual("v2", cache.StampHeld("docs"), "read before the listing, once");
        Assert.AreEqual(2, asked.Count);

        // a folder listed afresh on its own reads its stamp too
        await cache.RefreshAsync("docs/sub", CancellationToken.None);
        Assert.IsNull(cache.StampHeld("docs/sub"), "no stamp for it");
        CollectionAssert.AreEquivalent(new[] { "docs" }, cache.Stamps().Keys.ToArray());
    }

    [TestMethod]
    public async Task AListingKeptFromElsewhereAnswersWithoutTheLister()
    {
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time);
        cache.Keep("docs/sub", [VfsEntry.File("c.txt", 3), VfsEntry.File("d.txt", 4)]);
        Assert.AreEqual(2, (await cache.GetAsync("docs/sub", CancellationToken.None)).Count);
        Assert.AreEqual(4, (await cache.StatAsync("docs/sub/d.txt", CancellationToken.None))!.Length);
        Assert.AreEqual(0, _calls);
    }

    private static async Task WaitForAsync(Func<bool> done) => await WaitForAsync(() => Task.FromResult(done()));

    private static async Task WaitForAsync(Func<Task<bool>> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await done())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(10);
        }
    }

    [TestMethod]
    public async Task AFailedRefreshKeepsWhatWasHeld()
    {
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time);
        await cache.GetAsync("docs", CancellationToken.None);
        var held = _tree["docs"];
        _tree.Remove("docs");
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.RefreshAsync("docs", CancellationToken.None));
        Assert.AreEqual(2, (await cache.GetAsync("docs", CancellationToken.None)).Count, "the old listing still answers");
        Assert.AreEqual(2, _calls);
        _tree["docs"] = held;
    }

    [TestMethod]
    public async Task AWriteWhileTheFolderIsBeingListedIsInTheListingThatLands()
    {
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time);
        await cache.GetAsync("docs", CancellationToken.None);
        _gate = new TaskCompletionSource();
        var refresh = cache.RefreshAsync("docs", CancellationToken.None).AsTask();
        await Task.Delay(50);

        // written through the source while the upstream's answer (from before the write) is on its way
        cache.Update("docs/made.txt", VfsEntry.File("made.txt", 5));
        cache.Update("docs/b.txt", null);
        _gate.SetResult();
        var landed = await refresh;
        CollectionAssert.AreEquivalent(new[] { "a.txt", "made.txt" }, landed.Select(e => e.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "a.txt", "made.txt" }, (await cache.GetAsync("docs", CancellationToken.None)).Select(e => e.Name).ToArray(), "and that is what is kept");
    }

    [TestMethod]
    public async Task AListingInvalidatedOnItsWayIsHandedOverButNotKept()
    {
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time);
        _gate = new TaskCompletionSource();
        var waiting = cache.GetAsync("docs", CancellationToken.None).AsTask();
        await Task.Delay(50);
        cache.Invalidate("docs");
        _gate.SetResult();
        Assert.AreEqual(2, (await waiting).Count);
        Assert.AreEqual(0, cache.Count);
        _gate = null;
        await cache.GetAsync("docs", CancellationToken.None);
        Assert.AreEqual(2, _calls, "listed again");
    }

    [TestMethod]
    public async Task ATimedListingIsGoodForItsTimeFromWhenItArrived()
    {
        var cache = new ListingCache(TimeSpan.FromSeconds(2), ListAsync, time: _time);
        _gate = new TaskCompletionSource();
        var slow = cache.GetAsync("docs", CancellationToken.None).AsTask();
        await Task.Delay(50);
        _time.Advance(TimeSpan.FromSeconds(3));   // the upstream took longer than the listing's whole life
        _gate.SetResult();
        await slow;
        await cache.GetAsync("docs", CancellationToken.None);
        Assert.AreEqual(1, _calls, "not stale on arrival");
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
