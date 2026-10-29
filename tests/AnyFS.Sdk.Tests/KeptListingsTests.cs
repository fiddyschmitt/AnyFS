using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sdk.Tests;

/// <summary>A slow source's listings kept and its tree walked into them (spec 6.8), over a tree held in memory.</summary>
[TestClass]
public class KeptListingsTests
{
    private readonly ConcurrentDictionary<string, List<VfsEntry>> _tree = new(StringComparer.Ordinal)
    {
        [""] = [VfsEntry.Directory("docs"), VfsEntry.Directory("photos"), VfsEntry.File("readme.txt", 10)],
        ["docs"] = [VfsEntry.Directory("sub"), VfsEntry.File("a.txt", 1), VfsEntry.File("b.txt", 2)],
        ["docs/sub"] = [VfsEntry.File("c.txt", 3)],
        ["photos"] = [VfsEntry.File("p1.jpg", 100), VfsEntry.File("p2.jpg", 200)],
    };

    private readonly ConcurrentDictionary<string, int> _listed = new(StringComparer.Ordinal);
    private TaskCompletionSource? _gate;

    private int Listed(string path) => _listed.GetValueOrDefault(path);

    private async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        _listed.AddOrUpdate(path, 1, (_, n) => n + 1);
        if (_gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        if (!_tree.TryGetValue(path, out var entries))
        {
            throw VfsException.NotFound(path);
        }

        foreach (var e in entries.ToList())
        {
            yield return e;
        }
    }

    private KeptListings Kept(SourceCounters counters, KeptListingsOptions? options = null, TimeProvider? time = null)
        => new(ListAsync, counters, NullLogger.Instance, options ?? new KeptListingsOptions { WalkEvery = TimeSpan.Zero }, time);

    [TestMethod]
    public async Task TheCountWalksEveryFolderAndKeepsWhatItListed()
    {
        var counters = new SourceCounters();
        await using var kept = Kept(counters);
        await kept.CountAsync(CancellationToken.None);
        Assert.AreEqual(6, counters.FilesKnown);
        Assert.AreEqual(3, counters.DirectoriesKnown);
        Assert.AreEqual(EnumerationState.Complete, counters.State);

        // what the host's generic walk counts of the same tree
        var generic = new SourceCounters();
        await TreeCount.CountAsync(new ListingSource(ListAsync), generic, TreeCount.DefaultParallelism, null, CancellationToken.None);
        Assert.AreEqual(generic.FilesKnown, counters.FilesKnown);
        Assert.AreEqual(generic.DirectoriesKnown, counters.DirectoriesKnown);

        // every folder answers from what the walk saw
        _listed.Clear();
        foreach (var folder in new[] { "", "docs", "docs/sub", "photos" })
        {
            await kept.Cache.GetAsync(folder, CancellationToken.None);
        }

        Assert.AreEqual(2, (await kept.Cache.StatAsync("docs/b.txt", CancellationToken.None))!.Length);
        Assert.IsTrue(_listed.IsEmpty, "nothing listed again: " + string.Join(", ", _listed.Keys));
    }

    [TestMethod]
    public async Task WithoutKeepingTheCountIsOneWalkAndListingsLastTheirTime()
    {
        var time = new FakeTime();
        var counters = new SourceCounters();
        await using var kept = Kept(counters, new KeptListingsOptions { Keep = false }, time);
        await kept.CountAsync(CancellationToken.None);
        Assert.AreEqual(6, counters.FilesKnown);
        Assert.AreEqual(EnumerationState.InProgress, counters.State, "the host says Complete when the count returns");

        await kept.Cache.GetAsync("docs", CancellationToken.None);
        var listed = Listed("docs");
        time.Advance(TimeSpan.FromSeconds(3));
        await kept.Cache.GetAsync("docs", CancellationToken.None);
        Assert.AreEqual(listed + 1, Listed("docs"), "two seconds, then listed again");
    }

    [TestMethod]
    public async Task FoldersLeftOutAreListedWhenOpenedAndMakeTheCountALowerBound()
    {
        var counters = new SourceCounters();
        var options = new KeptListingsOptions
        {
            WalkEvery = TimeSpan.Zero,
            LeftOut = (folder, _) => ValueTask.FromResult<IReadOnlySet<string>?>(folder == "docs" ? new HashSet<string> { "sub" } : null),
        };
        await using var kept = Kept(counters, options);
        await kept.CountAsync(CancellationToken.None);
        Assert.AreEqual(0, Listed("docs/sub"), "not walked into");
        Assert.AreEqual(5, counters.FilesKnown, "its file not counted");
        Assert.AreEqual(3, counters.DirectoriesKnown, "though it is counted as a folder");
        Assert.AreEqual(EnumerationState.Unknown, counters.State, "a lower bound");

        await kept.Cache.GetAsync("docs/sub", CancellationToken.None);
        Assert.AreEqual(1, Listed("docs/sub"), "listed when opened");
    }

    [TestMethod]
    public async Task AWholeSubtreeGivenAtOnceIsKeptAndCountedWithoutListingItsFolders()
    {
        var counters = new SourceCounters();
        var options = new KeptListingsOptions
        {
            WalkEvery = TimeSpan.Zero,
            ListTree = (folder, _) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>?>(folder != "docs" ? null : new Dictionary<string, IReadOnlyList<VfsEntry>>
            {
                ["docs"] = _tree["docs"],
                ["docs/sub"] = [VfsEntry.File("c.txt", 3), VfsEntry.File("given.txt", 5)],
            }),
        };
        await using var kept = Kept(counters, options);
        await kept.CountAsync(CancellationToken.None);
        Assert.AreEqual(0, Listed("docs") + Listed("docs/sub"), "neither listed alone");
        Assert.AreEqual(1, Listed("photos"));
        Assert.AreEqual(7, counters.FilesKnown, "the subtree's files as given");
        Assert.AreEqual(EnumerationState.Complete, counters.State);
        Assert.AreEqual(5, (await kept.Cache.StatAsync("docs/sub/given.txt", CancellationToken.None))!.Length, "kept as given");
        Assert.AreEqual(0, Listed("docs/sub"));
    }

    [TestMethod]
    public async Task AFolderThatCannotBeListedIsLeftOutAndTheRestCounted()
    {
        _tree["docs"] = [VfsEntry.Directory("gone"), VfsEntry.File("a.txt", 1)];
        var counters = new SourceCounters();
        await using var kept = Kept(counters);
        await kept.CountAsync(CancellationToken.None);
        Assert.AreEqual(1 + 1 + 2, counters.FilesKnown);
        Assert.AreEqual(EnumerationState.Complete, counters.State, "a folder that fails is no folder left out on purpose");
    }

    [TestMethod]
    public async Task PartsAreWalkedAtOnceAFewFoldersOfEachAtATime()
    {
        foreach (var part in new[] { "docs", "photos" })
        {
            _tree[part] = Enumerable.Range(0, 12).Select(i => VfsEntry.Directory($"f{i}")).ToList();
            for (var i = 0; i < 12; i++)
            {
                _tree[$"{part}/f{i}"] = [VfsEntry.File("x.txt", 1)];
            }
        }

        var inFlight = new ConcurrentDictionary<string, int>();
        var most = new ConcurrentDictionary<string, int>();
        async IAsyncEnumerable<VfsEntry> SlowAsync(string path, [EnumeratorCancellation] CancellationToken ct)
        {
            var part = path.Split('/')[0];
            var now = inFlight.AddOrUpdate(part, 1, (_, n) => n + 1);
            most.AddOrUpdate(part, now, (_, n) => Math.Max(n, now));
            await Task.Delay(20, ct);
            inFlight.AddOrUpdate(part, 0, (_, n) => n - 1);
            await foreach (var e in ListAsync(path, ct))
            {
                yield return e;
            }
        }

        var counters = new SourceCounters();
        var options = new KeptListingsOptions { WalkEvery = TimeSpan.Zero, Parallelism = 2, PartsBelow = (folder, _) => ValueTask.FromResult(folder.Length == 0) };
        await using var kept = new KeptListings(SlowAsync, counters, NullLogger.Instance, options);
        await kept.CountAsync(CancellationToken.None);
        Assert.AreEqual(1 + 24, counters.FilesKnown);
        foreach (var part in new[] { "docs", "photos" })
        {
            Assert.AreEqual(2, most[part], $"{part}: two folders at a time");
        }
    }

    [TestMethod]
    public async Task TheWalksComeAgainAndShowTheirTotalWhenDone()
    {
        var counters = new SourceCounters();
        await using var kept = Kept(counters, new KeptListingsOptions { WalkEvery = TimeSpan.FromMilliseconds(50) });
        await kept.CountAsync(CancellationToken.None);
        Assert.AreEqual(6, counters.FilesKnown);

        // changed upstream: the next walk lists it afresh, counts aside, and shows the total when done
        _tree["photos"] = [.. _tree["photos"], VfsEntry.File("p3.jpg", 300)];
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (counters.FilesKnown != 7 || (await kept.Cache.GetAsync("photos", CancellationToken.None)).Count != 3)
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "the next walk");
            Assert.IsTrue(counters.FilesKnown is 6 or 7, $"never a count half way: {counters.FilesKnown}");
            await Task.Delay(10);
        }

        Assert.AreEqual(EnumerationState.Complete, counters.State);
    }

    [TestMethod]
    public async Task TheCountSaysHowTheWalkEndedThoughItEndedBeforeTheCountAsked()
    {
        var counters = new SourceCounters();
        var options = new KeptListingsOptions
        {
            WalkEvery = TimeSpan.Zero,
            LeftOut = (folder, _) => ValueTask.FromResult<IReadOnlySet<string>?>(folder == "docs" ? new HashSet<string> { "sub" } : null),
        };
        await using var kept = Kept(counters, options);

        // the source starts the walks itself (SharePoint does); the host marks the count in progress when it asks
        kept.StartWalks();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (counters.State != EnumerationState.Unknown)
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "the walk");
            await Task.Delay(10);
        }

        counters.SetState(EnumerationState.InProgress);
        await kept.CountAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Unknown, counters.State);
    }

    [TestMethod]
    public async Task DisposingStopsTheWalksAndTheCountWaitingForThem()
    {
        _gate = new TaskCompletionSource();
        var counters = new SourceCounters();
        var kept = Kept(counters, new KeptListingsOptions { WalkEvery = TimeSpan.FromMinutes(30) });
        var counting = kept.CountAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.IsFalse(counting.IsCompleted, "the walk waits on the upstream");
        await kept.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await counting.WaitAsync(TimeSpan.FromSeconds(10));

        // and none starts again
        kept.StartWalks();
        Assert.AreEqual(1, Listed(""));
    }

    [TestMethod]
    public async Task SavedListingsAnswerTheNextRunAtOnceThenAreListedAfresh()
    {
        var file = Path.Combine(Path.GetTempPath(), $"anyfs-kept-{Guid.NewGuid():N}.json.gz");
        try
        {
            var options = new KeptListingsOptions { WalkEvery = TimeSpan.Zero, RefreshAfter = TimeSpan.FromMinutes(1), SaveTo = file, SaveKey = "jane@contoso.com" };
            await using (var first = Kept(new SourceCounters(), options))
            {
                await first.Cache.GetAsync("", CancellationToken.None);
                await first.Cache.GetAsync("docs", CancellationToken.None);
            }

            Assert.IsTrue(File.Exists(file), "saved when the source stops");
            _listed.Clear();
            _tree["docs"].Add(VfsEntry.File("new.txt", 4));

            // the next run: the saved listing at once, without the upstream; then listed afresh behind that answer
            await using (var next = Kept(new SourceCounters(), options))
            {
                var docs = await next.Cache.GetAsync("docs", CancellationToken.None);
                CollectionAssert.AreEqual(new[] { "sub", "a.txt", "b.txt" }, docs.Select(e => e.Name).ToArray(), "what the last run saw");
                for (var i = 0; i < 100 && Listed("docs") == 0; i++)
                {
                    await Task.Delay(20);
                }

                Assert.AreEqual(1, Listed("docs"), "listed afresh behind the first answer");
                for (var i = 0; i < 100 && (await next.Cache.GetAsync("docs", CancellationToken.None)).Count < 4; i++)
                {
                    await Task.Delay(20);
                }

                Assert.AreEqual(4, (await next.Cache.GetAsync("docs", CancellationToken.None)).Count);
            }

            // another account's saved listings are not read
            _listed.Clear();
            await using (var other = Kept(new SourceCounters(), options with { SaveKey = "john@contoso.com" }))
            {
                await other.Cache.GetAsync("docs", CancellationToken.None);
                Assert.AreEqual(1, Listed("docs"), "listed, not answered from jane's");
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>A source that only lists, for the host's generic walk.</summary>
    private sealed class ListingSource(Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> list) : IVfsOperations
    {
        public IAsyncEnumerable<VfsEntry> ListAsync(string path, CancellationToken ct) => list(path, ct);

        public Task<VfsEntry?> StatAsync(string path, CancellationToken ct) => throw new NotSupportedException();

        public Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct) => throw new NotSupportedException();

        public Task CreateDirectoryAsync(string path, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteAsync(string path, CancellationToken ct) => throw new NotSupportedException();

        public Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct) => throw new NotSupportedException();

        public Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct) => throw new NotSupportedException();

        public Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }
}
