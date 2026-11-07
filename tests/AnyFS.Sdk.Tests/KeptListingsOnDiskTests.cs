using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sdk.Tests;

/// <summary>
/// The kept listings on disk (<see cref="KeptListingsOptions.OnDisk"/>, the default): a database beside the file, holding what
/// the walks saw, so a source of more than fits in memory keeps all of it; the start reads nothing back and answers at once;
/// what an earlier run left in the file goes into the database once; another account's or another shape's database is not read.
/// </summary>
[TestClass]
public class KeptListingsOnDiskTests
{
    private const string Key = "jane@contoso.com";

    private readonly ConcurrentDictionary<string, List<VfsEntry>> _tree = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _listed = new(StringComparer.Ordinal);
    private readonly List<string> _folders = [];

    public KeptListingsOnDiskTests()
    {
        _tree[""] = [.. Enumerable.Range(0, 20).Select(i => VfsEntry.Directory($"d{i:D2}")), VfsEntry.File("root.txt", 1)];
        for (var i = 0; i < 20; i++)
        {
            _tree[$"d{i:D2}"] = [VfsEntry.Directory("sub"), .. Enumerable.Range(0, 5).Select(f => VfsEntry.File($"f{f}.txt", f + 1, etag: $"{i:x2}{f:x2}aa"))];
            _tree[$"d{i:D2}/sub"] = [VfsEntry.File("x.txt", 7)];
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var folder in _folders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // a file still closing: left for the temp folder
            }
        }
    }

    private int Listed(string path) => _listed.GetValueOrDefault(path);

    private async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        _listed.AddOrUpdate(path, 1, (_, n) => n + 1);
        if (!_tree.TryGetValue(path, out var entries))
        {
            throw VfsException.NotFound(path);
        }

        foreach (var e in entries.ToList())
        {
            yield return e;
        }
    }

    private static ValueTask<string?> StampOf(string path, string? held, CancellationToken ct)
        => ValueTask.FromResult<string?>(path.Length == 3 ? "v1" : null);

    private string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"anyfs-kept-disk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        _folders.Add(folder);
        return folder;
    }

    private static KeptListingsOptions Options(string folder, bool onDisk = true, string key = Key, Func<string, CancellationToken, ValueTask<IReadOnlySet<string>?>>? leftOut = null) => new()
    {
        WalkEvery = TimeSpan.Zero,
        SaveTo = Path.Combine(folder, "listings.json.gz"),
        SaveKey = key,
        StampOf = StampOf,
        Parallelism = 2,
        SaveEvery = TimeSpan.Zero,
        OnDisk = onDisk,
        LeftOut = leftOut,
    };

    private KeptListings Kept(KeptListingsOptions options, SourceCounters? counters = null) => new(ListAsync, counters ?? new SourceCounters(), NullLogger.Instance, options);

    [TestMethod]
    public async Task WhatAWalkSawAnswersAtOnceAtTheNextStartFromTheDatabase()
    {
        var folder = TempFolder();
        var counters = new SourceCounters();
        await using (var kept = Kept(Options(folder), counters))
        {
            await kept.CountAsync(CancellationToken.None);
            Assert.AreEqual(121, counters.FilesKnown, "a hundred files in the twenty folders, twenty in their subs, one at the root");
            Assert.AreEqual(41, kept.Cache.Count);
        }

        Assert.IsTrue(File.Exists(Path.Combine(folder, "listings.db")), "the database beside the file");
        Assert.IsFalse(File.Exists(Path.Combine(folder, "listings.json.gz")), "no file: the listings are in the database as they are stored");
        _listed.Clear();

        await using var next = Kept(Options(folder));
        Assert.AreEqual(41, next.Cache.Count, "nothing read back: the database is what is held");
        var d05 = next.Cache.Held("d05")!;
        Assert.AreEqual(6, d05.Count);
        Assert.AreEqual("0500aa", d05.Single(e => e.Name == "f0.txt").ETag, "the entries as they were");
        Assert.AreEqual("v1", next.Cache.StampHeld("d05"));
        Assert.AreEqual("x.txt", (await next.Cache.StatAsync("d05/sub/x.txt", CancellationToken.None))!.Name);
        Assert.AreEqual(0, _listed.Count, "nothing asked of the source");

        // the next walk keeps every stamped folder by its stamp, with everything below it
        await next.CountAsync(CancellationToken.None);
        Assert.AreEqual(1, Listed(""), "the root has no stamp: listed");
        Assert.AreEqual(0, Listed("d05"), "kept by its stamp");
        Assert.AreEqual(0, Listed("d05/sub"), "kept below it");
        Assert.AreEqual(41, next.Cache.Count);
    }

    [TestMethod]
    public async Task TheDatabaseIsTheDefault()
    {
        if (Environment.GetEnvironmentVariable("ANYFS_LISTINGS_ON_DISK") is "0" or "false")
        {
            Assert.Inconclusive("the environment keeps the listings in memory");
        }

        var folder = TempFolder();
        var options = new KeptListingsOptions { WalkEvery = TimeSpan.Zero, SaveTo = Path.Combine(folder, "listings.json.gz"), SaveKey = Key, StampOf = StampOf, Parallelism = 2, SaveEvery = TimeSpan.Zero };
        Assert.IsNull(options.OnDisk, "nothing said");
        await using (var kept = Kept(options))
        {
            await kept.CountAsync(CancellationToken.None);
            Assert.IsNotNull(kept.DiskStatistics, "the database holds the listings");
            Assert.AreEqual(41, kept.Cache.Count);
        }

        Assert.IsTrue(File.Exists(Path.Combine(folder, "listings.db")), "the database, with nothing said");
        Assert.IsFalse(File.Exists(Path.Combine(folder, "listings.json.gz")), "and no file");
    }

    [TestMethod]
    public async Task WhatTheDatabaseDoesIsCounted()
    {
        var folder = TempFolder();
        await using (var inMemory = Kept(Options(folder, onDisk: false)))
        {
            Assert.IsNull(inMemory.DiskStatistics, "no database");
        }

        File.Delete(Path.Combine(folder, "listings.json.gz"));
        await using (var kept = Kept(Options(folder)))
        {
            var opened = kept.DiskStatistics!;
            Assert.AreEqual(0, opened.Reads.Count + opened.Inserts.Count + opened.Commits.Count, "nothing done yet");
            await kept.CountAsync(CancellationToken.None);
            var walked = kept.DiskStatistics!;
            Assert.AreEqual(41, walked.Inserts.Count, "a row a folder");
            Assert.AreEqual(0, walked.Updates.Count);
            Assert.AreEqual(0, walked.Deletes.Count);
            Assert.IsTrue(walked.Reads.Count >= 41, $"each folder is looked for before it is stored: {walked.Reads.Count} reads");
            Assert.AreEqual(1, walked.Commits.Count, "the walk's end commits everything stored since");
            Assert.IsTrue(walked.Inserts.Total > TimeSpan.Zero && walked.Inserts.Longest >= walked.Inserts.Mean, "timed");
            Assert.IsTrue(walked.BytesWritten > 41 * 10, "the listings' bytes");
        }

        await using var next = Kept(Options(folder));
        var started = next.DiskStatistics!;
        Assert.AreEqual(0, started.Reads.Count, "the start reads nothing back");
        Assert.IsNotNull(next.Cache.Held("d05"));
        Assert.IsNull(next.Cache.Held("nowhere"));
        var asked = next.DiskStatistics!;
        Assert.AreEqual(2, asked.Reads.Count);
        Assert.AreEqual(1, asked.ReadsFound);
    }

    [TestMethod]
    public async Task ATreeKeptAtOnceStoresOnlyWhatDiffers()
    {
        var folder = TempFolder();
        var moved = new HashSet<string>(StringComparer.Ordinal);
        Task<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>?> TreeOf(string path, CancellationToken ct)
        {
            if (path.Length != 3)
            {
                return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>?>(null);
            }

            var tree = new Dictionary<string, IReadOnlyList<VfsEntry>>(StringComparer.Ordinal);
            foreach (var (p, entries) in _tree)
            {
                if (p == path || p.StartsWith(path + "/", StringComparison.Ordinal))
                {
                    tree[p] = entries.ToList();
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>?>(tree);
        }

        var options = Options(folder) with
        {
            ListTree = TreeOf,
            StampOf = (path, _, _) => ValueTask.FromResult<string?>(path.Length == 3 ? (moved.Contains(path) ? "v2" : "v1") : null),
        };
        await using (var kept = Kept(options))
        {
            await kept.CountAsync(CancellationToken.None);
            var first = kept.DiskStatistics!;
            Assert.AreEqual(41, first.Inserts.Count);
            Assert.AreEqual(0, first.Updates.Count + first.Deletes.Count);
            Assert.AreEqual(0, first.RangeRows, "nothing held below any of them yet");
        }

        // a file added below d05, d06's sub gone, a folder added to d07; nothing else
        _tree["d05/sub"] = [VfsEntry.File("x.txt", 7), VfsEntry.File("y.txt", 8)];
        _tree["d06"] = [.. Enumerable.Range(0, 5).Select(f => VfsEntry.File($"f{f}.txt", f + 1, etag: $"06{f:x2}aa"))];
        _tree.TryRemove("d06/sub", out _);
        _tree["d07"] = [VfsEntry.Directory("sub"), VfsEntry.Directory("more"), .. Enumerable.Range(0, 5).Select(f => VfsEntry.File($"f{f}.txt", f + 1, etag: $"07{f:x2}aa"))];
        _tree["d07/more"] = [VfsEntry.File("z.txt", 9)];
        moved.UnionWith(["d05", "d06", "d07"]);

        await using var next = Kept(options);
        Assert.AreEqual(41, next.Cache.Count);
        await next.CountAsync(CancellationToken.None);
        var again = next.DiskStatistics!;
        Assert.AreEqual(4, again.Updates.Count, "d05 (its stamp moved), d05/sub, d06 and d07 differ");
        Assert.AreEqual(1, again.Inserts.Count, "d07/more is new");
        Assert.AreEqual(1, again.Deletes.Count, "d06/sub is gone");
        Assert.IsTrue(again.RangeRows >= 3, $"what is held below each moved folder is read as a range: {again.RangeRows} rows");
        Assert.AreEqual(41, next.Cache.Count);
        Assert.IsNull(next.Cache.Held("d06/sub"));
        Assert.AreEqual("z.txt", next.Cache.Held("d07/more")!.Single().Name);
        Assert.AreEqual(2, next.Cache.Held("d05/sub")!.Count);
        Assert.AreEqual("v2", next.Cache.StampHeld("d05"));
        Assert.AreEqual("v1", next.Cache.StampHeld("d04"), "kept as it was");
    }

    [TestMethod]
    public async Task WhatIsStoredBeforeTheCommitIsReadBackAtOnce()
    {
        var folder = TempFolder();
        await using (var kept = Kept(Options(folder)))
        {
            await kept.CountAsync(CancellationToken.None);
            var committed = kept.DiskStatistics!.Commits.Count;

            // kept, let go, and kept again, with no commit between: each answers as it is now, not as the last commit left it
            kept.Cache.Keep("d05/extra", [VfsEntry.File("e.txt", 5)]);
            Assert.AreEqual("e.txt", kept.Cache.Held("d05/extra")!.Single().Name);
            Assert.AreEqual(42, kept.Cache.Count);
            kept.Cache.Invalidate("d05/sub");
            Assert.IsNull(kept.Cache.Held("d05/sub"));
            kept.Cache.Keep("d05/sub", [VfsEntry.File("x.txt", 7), VfsEntry.File("w.txt", 1)]);
            Assert.AreEqual(2, kept.Cache.Held("d05/sub")!.Count);
            var reads = Enumerable.Range(0, 64).AsParallel().Select(i => kept.Cache.Held(i % 2 == 0 ? "d05/extra" : "d05/sub")!.Count).ToList();
            Assert.IsTrue(reads.All(n => n is 1 or 2), "every reader sees what this run stored");

            // what is held below a folder says the same: the folder let go is not among it, the ones stored since are, in place
            var below = kept.Cache.Under("d05");
            CollectionAssert.AreEqual(new[] { "d05", "d05/extra", "d05/sub" }, below.Keys.Order(StringComparer.Ordinal).ToArray());
            Assert.AreEqual(2, below["d05/sub"].Count);
            kept.Cache.Invalidate("d05/extra");
            Assert.IsFalse(kept.Cache.Under("d05").ContainsKey("d05/extra"));
            Assert.AreEqual(committed, kept.DiskStatistics!.Commits.Count, "none of it committed yet");
        }

        await using var next = Kept(Options(folder));
        Assert.AreEqual(41, next.Cache.Count, "the stop committed it: one kept, one let go");
        Assert.AreEqual(2, next.Cache.Held("d05/sub")!.Count);
        Assert.IsNull(next.Cache.Held("d05/extra"));
    }

    [TestMethod]
    public async Task TheFileOfAnEarlierRunMovesIntoTheDatabaseOnce()
    {
        var folder = TempFolder();
        await using (var inMemory = Kept(Options(folder, onDisk: false)))
        {
            await inMemory.CountAsync(CancellationToken.None);
        }

        Assert.IsTrue(File.Exists(Path.Combine(folder, "listings.json.gz")), "the earlier run's file");
        _listed.Clear();
        await using (var onDisk = Kept(Options(folder)))
        {
            Assert.AreEqual(41, onDisk.Cache.Count, "moved in");
            Assert.AreEqual("v1", onDisk.Cache.StampHeld("d07"));
            Assert.AreEqual(6, onDisk.Cache.Held("d07")!.Count);
            Assert.IsFalse(File.Exists(Path.Combine(folder, "listings.json.gz")), "and the file is gone");
        }

        await using var third = Kept(Options(folder));
        Assert.AreEqual(41, third.Cache.Count, "still there at the start after that");
        Assert.AreEqual(0, _listed.Count);
    }

    [TestMethod]
    public async Task AnothersDatabaseIsNotRead()
    {
        var folder = TempFolder();
        await using (var kept = Kept(Options(folder)))
        {
            await kept.CountAsync(CancellationToken.None);
        }

        await using var other = Kept(Options(folder, key: "someone-else"));
        Assert.AreEqual(0, other.Cache.Count, "another account's listings: none of it is this run's");
        Assert.IsNull(other.Cache.Held("d05"));
        await other.CountAsync(CancellationToken.None);
        Assert.AreEqual(41, other.Cache.Count, "walked afresh, and kept under the new key");
    }

    [TestMethod]
    public async Task AListingChangedAfterARestartIsWhatTheNextStartHolds()
    {
        var folder = TempFolder();
        await using (var kept = Kept(Options(folder)))
        {
            await kept.CountAsync(CancellationToken.None);
        }

        await using (var second = Kept(Options(folder)))
        {
            second.Cache.Keep("d03", [VfsEntry.File("changed.txt", 3)], "v2");
            Assert.AreEqual("changed.txt", second.Cache.Held("d03")!.Single().Name, "what was made of the listing before is let go");
            second.Cache.InvalidateTree("d04");
            Assert.IsNull(second.Cache.Held("d04/sub"));
        }

        await using var third = Kept(Options(folder));
        Assert.AreEqual("changed.txt", third.Cache.Held("d03")!.Single().Name);
        Assert.AreEqual("v2", third.Cache.StampHeld("d03"));
        Assert.IsNull(third.Cache.Held("d04"));
        Assert.IsNull(third.Cache.Held("d04/sub"));
        Assert.IsNull(third.Cache.Held("d03/sub"), "no longer named by d03's listing: let go with it");
        Assert.AreEqual(38, third.Cache.Count);
    }

    [TestMethod]
    public async Task ADatabaseStillHeldByTheRunBeforeIsWaitedFor()
    {
        // a run killed holds its database a moment after it dies (an I/O error, busy, locked at the open): waited for,
        // never put aside as damaged, which would have thrown away everything it held
        var folder = TempFolder();
        await using (var kept = Kept(Options(folder)))
        {
            await kept.CountAsync(CancellationToken.None);
        }

        var db = Path.Combine(folder, "listings.db");
        using var holder = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Mode=ReadWrite;Pooling=False");
        holder.Open();
        using (var take = holder.CreateCommand())
        {
            take.CommandText = "BEGIN IMMEDIATE; INSERT OR REPLACE INTO meta(name, value) VALUES('held', 'by the run before')";
            take.ExecuteNonQuery();
        }

        var letGo = Task.Run(async () =>
        {
            await Task.Delay(700);
            using var give = holder.CreateCommand();
            give.CommandText = "COMMIT";
            give.ExecuteNonQuery();
        });

        await using var next = Kept(Options(folder));
        await letGo;
        Assert.IsFalse(File.Exists(db + ".damaged"), "not put aside");
        Assert.AreEqual(41, next.Cache.Count, "opened once the run before let go, with everything it held");
        Assert.AreEqual(6, next.Cache.Held("d05")!.Count);
    }

    [TestMethod]
    public async Task ADamagedDatabaseIsPutAsideAndTheListingsWalkedAgain()
    {
        var folder = TempFolder();
        await using (var kept = Kept(Options(folder)))
        {
            await kept.CountAsync(CancellationToken.None);
        }

        var db = Path.Combine(folder, "listings.db");
        File.WriteAllBytes(db, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        await using (var next = Kept(Options(folder)))
        {
            Assert.AreEqual(0, next.Cache.Count, "not a database: nothing of it is read");
            Assert.IsTrue(File.Exists(db + ".damaged"), "put aside");
            await next.CountAsync(CancellationToken.None);
            Assert.AreEqual(41, next.Cache.Count, "walked again into a new one");
        }

        await using var third = Kept(Options(folder));
        Assert.AreEqual(41, third.Cache.Count, "and that one is read at the next start");
    }

    [TestMethod]
    public async Task WhatIsHeldBelowAFolderIsARangeOfThePaths()
    {
        var folder = TempFolder();
        await using var kept = Kept(Options(folder));
        await kept.CountAsync(CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { "d04", "d04/sub" }, kept.Cache.Under("d04").Keys.ToArray());
        Assert.AreEqual(41, kept.Cache.Under("").Count);
        Assert.AreEqual(0, kept.Cache.Under("d0").Count, "no folder of that name: not 'd00' to 'd09'");
        Assert.AreEqual(0, kept.Cache.Under("nope").Count);
    }

    [TestMethod]
    public async Task WhatWalksLeaveOutAndWhenEveryFolderWasListedAreKeptToo()
    {
        var folder = TempFolder();
        var leftOut = new HashSet<string>(StringComparer.Ordinal) { "d19" };
        var options = Options(folder, leftOut: (path, _) => new ValueTask<IReadOnlySet<string>?>(path.Length == 0 ? leftOut : null));
        await using (var kept = Kept(options))
        {
            await kept.CountAsync(CancellationToken.None);
            Assert.IsNull(kept.Cache.Held("d19"), "left out of the walk");
            Assert.AreEqual(39, kept.Cache.Count);
        }

        _listed.Clear();
        await using var next = Kept(options);
        Assert.AreEqual(39, next.Cache.Count);
        await next.CountAsync(CancellationToken.None);
        Assert.AreEqual(0, Listed("d19"), "still left out, as the last run noted");
        Assert.AreEqual(0, Listed("d05"), "kept by its stamp: the walk of every folder is not due, as the last run noted when it was");
    }
}
