using System.Runtime.CompilerServices;
using AnyFS.Sdk.Testing;

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
    private readonly Dictionary<string, VfsException> _failing = new(StringComparer.Ordinal);
    private int _calls;
    private TaskCompletionSource? _gate;

    private async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        if (_gate is not null)
        {
            await _gate.Task;
        }

        if (_failing.TryGetValue(path, out var failure))
        {
            throw failure;
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
    public async Task AFailedListingIsAnsweredAtOnceForItsTimeThenAskedAgain()
    {
        _failing["docs"] = VfsException.AccessDenied("docs");
        _failing["docs/sub"] = new VfsException(VfsError.Unavailable, "the service did not answer", new TimeoutException());
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time)
        {
            FailureKeptFor = ex => ex.Error == VfsError.AccessDenied ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(1),
        };

        // a refusal: the second answer the same, without the lister
        var refused = await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs", CancellationToken.None));
        var again = await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs", CancellationToken.None));
        Assert.AreEqual(VfsError.AccessDenied, again.Error);
        Assert.AreEqual(refused.Message, again.Message);
        Assert.AreEqual(1, _calls);
        Assert.AreEqual(0, cache.Count, "a failure is no listing");

        // another kind for its own time, with what caused it
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs/sub", CancellationToken.None));
        var kept = await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs/sub", CancellationToken.None));
        Assert.AreEqual(VfsError.Unavailable, kept.Error);
        Assert.IsInstanceOfType<TimeoutException>(kept.InnerException);
        Assert.AreEqual(2, _calls);

        _time.Advance(TimeSpan.FromMinutes(2));
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs/sub", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs", CancellationToken.None));
        Assert.AreEqual(3, _calls, "the unavailable folder asked again past its minute, the refusal still kept");

        _time.Advance(TimeSpan.FromMinutes(30));
        _failing.Remove("docs");
        Assert.AreEqual(2, (await cache.GetAsync("docs", CancellationToken.None)).Count);
        Assert.AreEqual(4, _calls);
    }

    [TestMethod]
    public async Task AFolderNotFoundIsNeverKeptAsFailed()
    {
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time) { FailureKeptFor = _ => TimeSpan.FromMinutes(30) };
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("nope", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("nope", CancellationToken.None));
        Assert.AreEqual(2, _calls, "whether it is there is its parent's listing's to say");
    }

    [TestMethod]
    public async Task AFailureKeptIsAskedAgainByARefreshWhenForgottenAndOnceInvalidated()
    {
        _failing["docs"] = VfsException.AccessDenied("docs");
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time) { FailureKeptFor = _ => TimeSpan.FromMinutes(30) };
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs", CancellationToken.None));

        // a walk's listing afresh asks whatever is kept; once the upstream answers, the listing replaces the failure
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.RefreshAsync("docs", CancellationToken.None));
        Assert.AreEqual(2, _calls);
        _failing.Remove("docs");
        await cache.RefreshAsync("docs", CancellationToken.None);
        Assert.AreEqual(2, (await cache.GetAsync("docs", CancellationToken.None)).Count);
        Assert.AreEqual(3, _calls);

        // let go by the source (a token come since) and by an invalidation
        _failing["docs/sub"] = VfsException.Unavailable("no token yet");
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs/sub", CancellationToken.None));
        cache.ForgetFailures();
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs/sub", CancellationToken.None));
        Assert.AreEqual(5, _calls);
        cache.InvalidateTree("docs");
        _failing.Remove("docs/sub");
        Assert.AreEqual(1, (await cache.GetAsync("docs/sub", CancellationToken.None)).Count);
        Assert.AreEqual(6, _calls);
    }

    [TestMethod]
    public async Task AFailureMetByAWalkCountsFromTheWalksEnd()
    {
        _failing["docs"] = new VfsException(VfsError.Unavailable, "the service did not answer");
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time) { FailureKeptFor = _ => TimeSpan.FromMinutes(30) };
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.RefreshAsync("docs", CancellationToken.None));
        Assert.AreEqual(1, _calls);

        // the walk goes on for most of the time, then ends: what it met holds a whole interval from there, to the next walk
        _time.Advance(TimeSpan.FromMinutes(29));
        cache.RenewFailures();
        _time.Advance(TimeSpan.FromMinutes(29));
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs", CancellationToken.None));
        Assert.AreEqual(1, _calls, "still kept, 58 minutes after it was noted");

        _time.Advance(TimeSpan.FromMinutes(2));
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs", CancellationToken.None));
        Assert.AreEqual(2, _calls, "over: asked again, where no walk came");
    }

    [TestMethod]
    public async Task HeldAnswersWithoutAsking()
    {
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time);
        Assert.IsNull(cache.Held("docs"), "nothing held yet");
        Assert.AreEqual(0, _calls, "and nothing asked");

        var listed = await cache.GetAsync("docs", CancellationToken.None);
        Assert.AreSame(listed, cache.Held("docs"));
        Assert.AreEqual(1, _calls, "looking at it is not asking for it");
    }

    [TestMethod]
    public async Task AskedIsWhatIsHeldOrWhatFailedAndIsKeptSo()
    {
        _failing["docs/sub"] = new VfsException(VfsError.AccessDenied, "refused");
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time) { FailureKeptFor = _ => TimeSpan.FromMinutes(1) };
        Assert.IsFalse(cache.Asked("docs"));
        await cache.GetAsync("docs", CancellationToken.None);
        Assert.IsTrue(cache.Asked("docs"));

        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs/sub", CancellationToken.None));
        Assert.IsTrue(cache.Asked("docs/sub"), "its failure is kept");
        Assert.IsFalse(cache.Asked("nope"));

        cache.ForgetFailures();
        Assert.IsFalse(cache.Asked("docs/sub"), "let go");
    }

    [TestMethod]
    public async Task EachListingFromTheUpstreamSaysWhy()
    {
        var told = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time)
        {
            FailureKeptFor = _ => TimeSpan.FromMinutes(1),
            OnListing = (path, why) => told.Enqueue($"{path}: {why}"),
        };

        await cache.GetAsync("docs", CancellationToken.None);
        StringAssert.Contains(told.Last(), "docs: no listing is held");

        await cache.GetAsync("docs", CancellationToken.None);
        Assert.AreEqual(1, told.Count, "held: nothing asked, nothing told");

        await cache.RefreshAsync("docs", CancellationToken.None);
        StringAssert.Contains(told.Last(), "docs: a walk");

        // a failure over; the first, kept, is not asked again and so tells nothing
        _failing["docs/sub2"] = new VfsException(VfsError.Unavailable, "the service did not answer");
        _tree["docs/sub2"] = [];
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs/sub2", CancellationToken.None));
        var count = told.Count;
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs/sub2", CancellationToken.None));
        Assert.AreEqual(count, told.Count, "the failure kept answers");
        _time.Advance(TimeSpan.FromMinutes(2));
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await cache.GetAsync("docs/sub2", CancellationToken.None));
        StringAssert.Contains(told.Last(), "it failed 120 s ago and that failure was kept 60 s (the service did not answer)");

        // past its time, where listings last a time
        var timed = new ListingCache(TimeSpan.FromSeconds(2), ListAsync, time: _time) { OnListing = (path, why) => told.Enqueue($"{path}: {why}") };
        await timed.GetAsync("docs", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(3));
        await timed.GetAsync("docs", CancellationToken.None);
        StringAssert.Contains(told.Last(), "docs: its listing is past its time");
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
    public void TheFoldersUnderOneAreItsOwnAndNotItsNamesakes()
    {
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time);
        foreach (var path in new[] { "docs", "docs/sub", "docs/sub/deep", "docs0", "docs-old", "docsx/y", "" })
        {
            cache.Keep(path, [VfsEntry.File("f", 1)]);
        }

        CollectionAssert.AreEquivalent(new[] { "docs", "docs/sub", "docs/sub/deep" }, cache.Under("docs").Keys.ToArray());
        CollectionAssert.AreEquivalent(new[] { "docs/sub", "docs/sub/deep" }, cache.Under("docs/sub").Keys.ToArray());
        Assert.AreEqual(7, cache.Under("").Count, "everything for the root");
        Assert.AreEqual(0, cache.Under("nope").Count);

        var version = cache.Version;
        cache.InvalidateTree("docs");
        CollectionAssert.AreEquivalent(new[] { "", "docs0", "docs-old", "docsx/y" }, cache.Snapshot().Keys.ToArray(), "the namesakes stay");
        Assert.IsTrue(cache.Version > version, "the version moved with the change");

        // the same listing again is no change; another is
        version = cache.Version;
        cache.Keep("docs0", [VfsEntry.File("f", 1)]);
        Assert.AreEqual(version, cache.Version, "the same again");
        cache.Keep("docs0", [VfsEntry.File("f", 2)]);
        Assert.AreNotEqual(version, cache.Version);
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
    }

    [TestMethod]
    public async Task AKeptListingIsNotAskedForHoweverOldUntilAWalkListsItAfresh()
    {
        var stamped = 0;
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time)
        {
            StampOf = (_, _, _) =>
            {
                Interlocked.Increment(ref stamped);
                return ValueTask.FromResult<string?>("v1");
            },
        };
        await cache.GetAsync("docs", CancellationToken.None);
        Assert.AreEqual((1, 1), (_calls, stamped));

        // changed upstream, and however old the listing and however often it is asked for: nothing behind the answer
        _tree["docs"].Add(VfsEntry.File("new.txt", 4));
        _time.Advance(TimeSpan.FromDays(30));
        for (var i = 0; i < 3; i++)
        {
            Assert.AreEqual(2, (await cache.GetAsync("docs", CancellationToken.None)).Count, "answered from what is held");
        }

        await Task.Delay(50);
        Assert.AreEqual((1, 1), (_calls, stamped), "neither the listing nor its stamp asked for");

        await cache.RefreshAsync("docs", CancellationToken.None);
        Assert.AreEqual(3, (await cache.GetAsync("docs", CancellationToken.None)).Count, "listed afresh");
        _tree["docs"].RemoveAt(2);
    }

    [TestMethod]
    public async Task ARestoredListingAnswersWithItsStampUntilAWalkListsItAfresh()
    {
        var stamps = new Dictionary<string, string>(StringComparer.Ordinal) { ["docs"] = "v2" };
        var cache = new ListingCache(ListingCache.Kept, ListAsync, time: _time) { StampOf = (path, _, _) => ValueTask.FromResult(stamps.GetValueOrDefault(path)) };

        // restored from a run before, with its stamp: answered at once, nothing asked
        cache.Restore("docs", [VfsEntry.File("old.txt", 1)], "v1");
        Assert.AreEqual(1, (await cache.GetAsync("docs", CancellationToken.None)).Count);
        await Task.Delay(50);
        Assert.AreEqual(0, _calls);
        Assert.AreEqual("v1", cache.StampHeld("docs"));

        // a walk's listing: its stamp read before it and kept with it
        Assert.AreEqual(2, (await cache.RefreshAsync("docs", CancellationToken.None)).Count);
        Assert.AreEqual("v2", cache.StampHeld("docs"), "read before the listing, once");

        // a folder with no stamp
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
