using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sdk.Tests;

/// <summary>
/// The kept listings' file (spec 6.6, the user's two GitLab servers of 6-Nov-2026, closed a fifth of the way through their
/// first walks and walked again from the start the next day): written as a walk goes and when it stops, so the next run goes
/// on from what the last had; a run that ended without stopping loses only what it had not written yet; a write cut short
/// is left out and written over; the file before this format is read once.
/// </summary>
[TestClass]
public class KeptListingsFileTests
{
    private const string Key = "jane@contoso.com";

    private readonly ConcurrentDictionary<string, List<VfsEntry>> _tree = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _listed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _held = new(StringComparer.Ordinal);
    private readonly List<string> _files = [];

    public KeptListingsFileTests()
    {
        // twenty folders of five files and a folder of one, each of the twenty stamped
        _tree[""] = [.. Enumerable.Range(0, 20).Select(i => VfsEntry.Directory($"d{i:D2}")), VfsEntry.File("root.txt", 1)];
        for (var i = 0; i < 20; i++)
        {
            _tree[$"d{i:D2}"] = [VfsEntry.Directory("sub"), .. Enumerable.Range(0, 5).Select(f => VfsEntry.File($"f{f}.txt", f + 1))];
            _tree[$"d{i:D2}/sub"] = [VfsEntry.File("x.txt", 7)];
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var held in _held.Values)
        {
            held.TrySetResult();
        }

        foreach (var file in _files)
        {
            File.Delete(file);
            File.Delete(file + ".tmp");
        }
    }

    private int Listed(string path) => _listed.GetValueOrDefault(path);

    private async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        _listed.AddOrUpdate(path, 1, (_, n) => n + 1);
        if (_held.TryGetValue(path, out var held))
        {
            await held.Task;
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

    private static ValueTask<string?> StampOf(string path, string? held, CancellationToken ct)
        => ValueTask.FromResult<string?>(path.Length == 3 ? "v1" : null);

    private string TempFile()
    {
        var file = Path.Combine(Path.GetTempPath(), $"anyfs-kept-{Guid.NewGuid():N}.bin");
        _files.Add(file);
        return file;
    }

    private KeptListingsOptions Options(string file, TimeSpan? saveEvery = null) => new()
    {
        WalkEvery = TimeSpan.Zero,
        SaveTo = file,
        SaveKey = Key,
        StampOf = StampOf,
        Parallelism = 1,
        SaveEvery = saveEvery ?? TimeSpan.Zero,
    };

    private KeptListings Kept(KeptListingsOptions options, SourceCounters? counters = null) => new(ListAsync, counters ?? new SourceCounters(), NullLogger.Instance, options);

    /// <summary>Entries of every shape the format tells apart: each field there or not, times equal or not, etags as text and as hex, a key, a name beyond ASCII.</summary>
    private static List<VfsEntry> EveryKindOfEntry()
    {
        var created = new DateTimeOffset(2026, 11, 7, 9, 30, 0, TimeSpan.FromHours(10));
        var modified = new DateTimeOffset(2026, 11, 7, 23, 59, 59, TimeSpan.FromHours(-5)).AddTicks(1234567);
        return
        [
            VfsEntry.Directory("dir", created, created),
            VfsEntry.Directory("dir-modified-only", modified),
            VfsEntry.File("plain.txt", 0),
            VfsEntry.File("len.bin", 123_456_789_012, modified),
            VfsEntry.File("both.txt", 1, modified, created, VfsAttributes.Hidden | VfsAttributes.ReadOnly, "W/\"abc\""),
            VfsEntry.File("blob.py", 10, created, created, etag: "0123456789abcdef0123456789abcdef01234567"),
            VfsEntry.File("sha256.bin", 11, etag: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
            VfsEntry.File("upper.bin", 12, etag: "ABCDEF0123456789"),
            VfsEntry.File("odd.bin", 13, etag: "abc"),
            VfsEntry.File("created-only.txt", 14, null, created),
            VfsEntry.File("ключ 文件.md", 15, modified, created) with { Key = "12345" },
            VfsEntry.File("empty-etag.txt", 16, etag: ""),
        ];
    }

    [TestMethod]
    public async Task TheFileIsThereFromTheStartSoEverySaveAppends()
    {
        // a save that wrote the whole to a file beside it and moved it into place left nothing if the run ended during it,
        // and a first walk's first save, under the walk's load, took longer than the time to the next
        var file = TempFile();
        await using (var kept = Kept(Options(file)))
        {
            Assert.IsTrue(File.Exists(file), "made at the start, the header alone");
            var header = new FileInfo(file).Length;
            Assert.IsTrue(header is > 8 and < 200, $"{header} bytes");
            kept.Cache.Keep("d01", _tree["d01"], "v1");
            await kept.CountAsync(CancellationToken.None);
            Assert.IsFalse(File.Exists(file + ".tmp"), "no write of the whole");
        }

        await using var next = Kept(Options(file));
        Assert.AreEqual(41, next.Cache.Count, "appended as the walk went, and at the stop");
    }

    [TestMethod]
    public async Task EveryKindOfEntryComesBackAsItWas()
    {
        var file = TempFile();
        var entries = EveryKindOfEntry();
        await using (var kept = Kept(Options(file)))
        {
            kept.Cache.Keep("mix", entries, "stamp");
            CollectionAssert.AreEqual(entries, kept.Cache.Held("mix")!.ToList(), "as held, made from the bytes");
        }

        await using var next = Kept(Options(file));
        var back = next.Cache.Held("mix")!.ToList();
        CollectionAssert.AreEqual(entries, back, "as saved");
        Assert.AreEqual("stamp", next.Cache.StampHeld("mix"));
        Assert.AreEqual(TimeSpan.FromHours(10), back[0].Created!.Value.Offset, "the offset as it was, not the instant alone");
        Assert.AreEqual(TimeSpan.FromHours(-5), back[3].Modified!.Value.Offset);
        Assert.AreEqual("ABCDEF0123456789", back[7].ETag, "uppercase hex is text: it comes back as written");
        Assert.AreEqual("12345", back[10].Key);
    }

    /// <summary>A file as the format before this one wrote it (format 1: each entry's flags one byte, its etag always text), one frame.</summary>
    private static void WriteFormatOne(string file, string key, params (string Path, string? Stamp, List<VfsEntry> Entries)[] listings)
    {
        using var plain = new MemoryStream();
        using (var w = new BinaryWriter(plain, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)1);
            w.Write(key);
            foreach (var (path, stamp, entries) in listings)
            {
                w.Write((byte)2);
                w.Write(path);
                w.Write(stamp is not null);
                if (stamp is not null)
                {
                    w.Write(stamp);
                }

                w.Write7BitEncodedInt(entries.Count);
                foreach (var e in entries)
                {
                    var flags = 0;
                    flags |= e.IsDirectory ? 1 : 0;
                    flags |= e.Length != 0 ? 2 : 0;
                    flags |= e.Created.HasValue ? 4 : 0;
                    if (e.Modified is { } m)
                    {
                        flags |= e.Created is { } c && c.Ticks == m.Ticks && c.Offset == m.Offset ? 16 : 8;
                    }

                    flags |= e.Attributes != VfsAttributes.None ? 32 : 0;
                    flags |= e.ETag is not null ? 64 : 0;
                    flags |= e.Key is not null ? 128 : 0;
                    w.Write((byte)flags);
                    w.Write(e.Name);
                    if ((flags & 2) != 0)
                    {
                        w.Write7BitEncodedInt64(e.Length);
                    }

                    if (e.Created is { } created)
                    {
                        w.Write(created.Ticks);
                        w.Write((short)created.Offset.TotalMinutes);
                    }

                    if ((flags & 8) != 0)
                    {
                        w.Write(e.Modified!.Value.Ticks);
                        w.Write((short)e.Modified.Value.Offset.TotalMinutes);
                    }

                    if ((flags & 32) != 0)
                    {
                        w.Write7BitEncodedInt((int)e.Attributes);
                    }

                    if (e.ETag is { } etag)
                    {
                        w.Write(etag);
                    }

                    if (e.Key is { } k)
                    {
                        w.Write(k);
                    }
                }
            }

            w.Write((byte)5);
            w.Write(false);
        }

        using var packed = new MemoryStream();
        using (var zip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zip.Write(plain.GetBuffer(), 0, (int)plain.Length);
        }

        using var f = File.Create(file);
        f.Write("AFKL"u8);
        Span<byte> n = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(n, 1);
        f.Write(n);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(n, (int)packed.Length);
        f.Write(n);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(n, (int)plain.Length);
        f.Write(n);
        f.Write(packed.GetBuffer(), 0, (int)packed.Length);
    }

    [TestMethod]
    public async Task AFileOfFormatOneIsReadOnceAndWrittenInTheFormatNow()
    {
        var file = TempFile();
        var entries = EveryKindOfEntry();
        WriteFormatOne(file, Key, ("d03", "v1", entries), ("d03/sub", null, [VfsEntry.File("x.txt", 7)]));
        await using (var kept = Kept(Options(file)))
        {
            CollectionAssert.AreEqual(entries, kept.Cache.Held("d03")!.ToList(), "read as the format before wrote it");
            Assert.AreEqual("v1", kept.Cache.StampHeld("d03"));
            Assert.AreEqual("x.txt", kept.Cache.Held("d03/sub")!.Single().Name);
        }

        var head = new byte[8];
        using (var f = File.OpenRead(file))
        {
            f.ReadExactly(head);
        }

        Assert.AreEqual(2, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4)), "written whole in the format now at the first save");
        await using var next = Kept(Options(file));
        CollectionAssert.AreEqual(entries, next.Cache.Held("d03")!.ToList());
        Assert.AreEqual("v1", next.Cache.StampHeld("d03"));
    }

    private static async Task UntilAsync(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, what);
            await Task.Delay(10);
        }
    }

    [TestMethod]
    public async Task AWalkStoppedPartWayStartsTheNextRunFromWhereItWas()
    {
        var file = TempFile();
        var options = Options(file);

        // the walk goes a folder at a time, depth first: held at d10, it has listed d00 to d09 and all below them
        _held["d10"] = new TaskCompletionSource();
        await using (var first = Kept(options))
        {
            var counting = first.CountAsync(CancellationToken.None);
            await UntilAsync(() => Listed("d10") == 1, "the walk comes to d10");
            Assert.IsFalse(counting.IsCompleted);
        }

        _held["d10"].TrySetResult();
        _listed.Clear();

        // the next run: what the first listed answers at once, and its walk goes on from there
        var counters = new SourceCounters();
        await using var next = Kept(options, counters);
        Assert.IsNotNull(next.Cache.Held("d09/sub"), "listed by the first run, answering at once");
        Assert.IsNull(next.Cache.Held("d10"), "never listed: the first run was stopped while it waited for it");
        await next.CountAsync(CancellationToken.None);
        Assert.AreEqual(1, Listed(""), "the root has no stamp: listed");
        for (var i = 0; i < 20; i++)
        {
            var folder = $"d{i:D2}";
            Assert.AreEqual(i < 10 ? 0 : 1, Listed(folder), folder);
            Assert.AreEqual(i < 10 ? 0 : 1, Listed(folder + "/sub"), folder + "/sub");
        }

        Assert.AreEqual((1 + 20 * 6L, 40L, EnumerationState.Complete), (counters.FilesKnown, counters.DirectoriesKnown, counters.State), "kept and listed, all counted");
    }

    [TestMethod]
    public async Task WhatAWalkListsIsWrittenDownAsItGoes()
    {
        var file = TempFile();
        var copy = TempFile();
        _held["d10"] = new TaskCompletionSource();
        await using var running = Kept(Options(file, TimeSpan.FromMilliseconds(30)));
        var counting = running.CountAsync(CancellationToken.None);
        await UntilAsync(() => Listed("d10") == 1, "the walk comes to d10");

        // a run that never stops (killed, the machine off): its file as it is now, read by the next
        KeptListings? next = null;
        await UntilAsync(() =>
        {
            next?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (!File.Exists(file))
            {
                return false;
            }

            File.Copy(file, copy, overwrite: true);
            next = new KeptListings(ListAsync, new SourceCounters(), NullLogger.Instance, Options(copy) with { SaveTo = copy });
            return next.Cache.Held("d09/sub") is not null;
        }, "written down while the walk waits");

        Assert.IsNotNull(next!.Cache.Held("d00"));
        Assert.IsNull(next.Cache.Held("d10"));
        await next.DisposeAsync();
        _held["d10"].TrySetResult();
        await counting.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [TestMethod]
    public async Task AWriteCutShortIsLeftOutAndWrittenOver()
    {
        var file = TempFile();
        var options = Options(file);
        await using (var first = Kept(options))
        {
            await first.CountAsync(CancellationToken.None);
        }

        var whole = new FileInfo(file).Length;

        // the next run changes a folder and writes it down: the file grows by a frame, which is then cut in half
        await using (var second = Kept(options))
        {
            second.Cache.Keep("d03", [VfsEntry.File("changed.txt", 3)], "v2");
        }

        var grown = new FileInfo(file).Length;
        Assert.IsTrue(grown > whole, "appended, not written whole");
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(whole + (grown - whole) / 2);
        }

        // what came before the cut is read; the next write goes where the cut frame began, and is read after
        await using (var third = Kept(options))
        {
            CollectionAssert.AreEqual(new[] { "sub", "f0.txt", "f1.txt", "f2.txt", "f3.txt", "f4.txt" }, third.Cache.Held("d03")!.Select(e => e.Name).ToArray(), "the change was in the frame cut short");
            third.Cache.Keep("d04", [VfsEntry.File("later.txt", 4)], "v3");
        }

        await using var fourth = Kept(options);
        Assert.AreEqual("later.txt", fourth.Cache.Held("d04")!.Single().Name);
        Assert.AreEqual(6, fourth.Cache.Held("d05")!.Count, "and everything before it");
        Assert.AreEqual("v3", fourth.Cache.StampHeld("d04"));
    }

    [TestMethod]
    public async Task TheFormatBeforeIsReadOnceAndWrittenInThisOne()
    {
        var file = TempFile();
        var json = $$$"""
            {"Version":2,"Key":"{{{Key}}}","Listings":{"":[{"Name":"docs","IsDirectory":true,"Length":0,"Created":null,"Modified":null,"Attributes":0,"ETag":null}],
            "docs":[{"Name":"a.txt","IsDirectory":false,"Length":12,"Created":"2026-10-20T09:30:00+10:00","Modified":"2026-10-21T09:30:00+10:00","Attributes":0,"ETag":"e1"}]},
            "Stamps":{"docs":"s1"},"FullWalkAt":"2026-10-21T10:00:00+00:00","LeftOut":{"":["issues"]}}
            """;
        using (var stream = new GZipStream(File.Create(file), CompressionLevel.Fastest))
        {
            stream.Write(Encoding.UTF8.GetBytes(json));
        }

        await using (var kept = Kept(Options(file)))
        {
            var docs = kept.Cache.Held("docs")!.Single();
            Assert.AreEqual(("a.txt", 12L, "e1"), (docs.Name, docs.Length, docs.ETag));
            Assert.AreEqual("s1", kept.Cache.StampHeld("docs"));
        }

        using (var stream = File.OpenRead(file))
        {
            var head = new byte[4];
            stream.ReadExactly(head);
            Assert.AreEqual("AFKL", Encoding.ASCII.GetString(head), "written in the new format at the stop");
        }

        await using var again = Kept(Options(file));
        Assert.AreEqual("a.txt", again.Cache.Held("docs")!.Single().Name);
        Assert.AreEqual("s1", again.Cache.StampHeld("docs"));
    }

    [TestMethod]
    public async Task AnotherKeysFileIsNotRead()
    {
        var file = TempFile();
        await using (var jane = Kept(Options(file)))
        {
            await jane.CountAsync(CancellationToken.None);
        }

        await using var john = Kept(Options(file) with { SaveKey = "john@contoso.com" });
        Assert.AreEqual(0, john.Cache.Count, "jane's listings are not john's");
    }

    [TestMethod]
    public async Task EveryPartOfAnEntryComesBack()
    {
        var file = TempFile();
        var ten = TimeSpan.FromHours(10);
        var listing = new List<VfsEntry>
        {
            VfsEntry.Directory("folder", new DateTimeOffset(2026, 10, 20, 9, 30, 0, ten), new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero)),
            VfsEntry.File("empty.txt", 0),
            VfsEntry.File("big.bin", 5L << 40, new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.FromHours(-7)), new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.FromHours(-7)), VfsAttributes.Hidden | VfsAttributes.ReadOnly, "\"etag\""),
            new VfsEntry("名前 with spaces.md", false, 42, null, new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.FromMinutes(330))) { Key = "page:123" },
        };
        await using (var kept = Kept(Options(file)))
        {
            kept.Cache.Keep("a/b", listing, "stamp");
        }

        await using var back = Kept(Options(file));
        var read = back.Cache.Held("a/b")!;
        CollectionAssert.AreEqual(listing, read.ToList());
        for (var i = 0; i < listing.Count; i++)
        {
            Assert.AreEqual(listing[i].Created?.Offset, read[i].Created?.Offset, listing[i].Name);
            Assert.AreEqual(listing[i].Modified?.Offset, read[i].Modified?.Offset, listing[i].Name);
            Assert.AreEqual(listing[i].Key, read[i].Key, listing[i].Name);
        }

        Assert.AreEqual("stamp", back.Cache.StampHeld("a/b"));
    }

    [TestMethod]
    public async Task AFileMostlyUndoneByLaterRecordsIsWrittenWholeAgain()
    {
        // run after run changes the same thousand folders: each stop appends what changed, until most of the file is records
        // later ones undo, and the stop that finds it so writes the whole afresh
        var file = TempFile();
        var sizes = new List<long>();
        for (var round = 0; round < 15; round++)
        {
            await using (var kept = Kept(Options(file)))
            {
                if (round > 0)
                {
                    Assert.AreEqual($"round{round - 1}.txt", kept.Cache.Held("folder0999")!.Single().Name, "what the run before left");
                }

                for (var f = 0; f < 1000; f++)
                {
                    kept.Cache.Keep($"folder{f:D4}", [VfsEntry.File($"round{round}.txt", round + 1)]);
                }
            }

            sizes.Add(new FileInfo(file).Length);
        }

        Assert.IsTrue(sizes[^1] < sizes.Max() / 2, "written whole again at some stop: " + string.Join(", ", sizes));
        await using var back = Kept(Options(file));
        Assert.AreEqual("round14.txt", back.Cache.Held("folder0999")!.Single().Name);
        Assert.AreEqual(1000, back.Cache.Count);
    }

    [TestMethod]
    public async Task AWalkOfWhatIsHeldGivesWayToTheCount()
    {
        // a run that listed everything, unstamped: the next run's walks list every folder they go through
        var file = TempFile();
        var options = Options(file) with { StampOf = null };
        await using (var first = Kept(options))
        {
            await first.CountAsync(CancellationToken.None);
        }

        // the next run: its walk of what is held begins soon after its start and is held up at d10; the count asked for then
        // begins its own walk at once, not after the other has gone through the rest
        _listed.Clear();
        _held["d10"] = new TaskCompletionSource();
        var log = new LinesLogger();
        await using var next = new KeptListings(ListAsync, new SourceCounters(), log, options);
        next.StartWalksOfHeld();
        await UntilAsync(() => Listed("d10") == 1, "the walk of what is held comes to d10");
        var counting = next.CountAsync(CancellationToken.None);
        await UntilAsync(() => log.Said("gave way to the count"), "it gives way");
        _held["d10"].TrySetResult();
        await counting.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(2, Listed("d05"), "once by each walk");
        Assert.AreEqual(1, Listed("d15"), "by the count's walk alone: the other never came to it");
    }

    /// <summary>What was logged, line by line.</summary>
    private sealed class LinesLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public bool Said(string what) => _lines.Any(l => l.Contains(what, StringComparison.Ordinal));

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _lines.Enqueue(formatter(state, exception));
    }
}
