using System.Diagnostics;
using System.Globalization;

namespace LibSmb.E2E;

/// <summary>An SMB condition: its name, the client OSes it applies to (null for all) and what it does.</summary>
public sealed record SmbCondition(string Name, IReadOnlyList<string>? Only, Action<SmbPair> Run);

/// <summary>
/// The SMB conditions of spec 16.2: portable-smb-server's rig (test/rig/matrix_test.go and multiclient_test.go), ported when
/// LibSmb took its place, so the operating systems' own SMB clients exercise anyfs's server as they did the Go one. Each
/// runs against one host's anyfs from one client machine. The shares are collections of their own (AnyFsHost): smbdata, a
/// Mirror of an empty folder of the host's, writable; smbsecond, another, for isolation; smbro, smbdata read-only; and
/// smbguest, smbdata to guests on a port of its own. Every condition that writes checks the bytes on both ends: what the
/// client reads back, and what is in the folder on the host. The rig's vfs_share and vfs_readonly_share are every
/// condition here (each share is a collection's VFS), and its log checks are the host's ZLogHasNoErrors.
/// </summary>
public static class SmbConditions
{
    private static readonly string[] LinuxOnly = ["linux"];

    /// <summary>A Windows client will not use a guest session: guest sessions are unsigned and Windows 11 requires signing.</summary>
    private static readonly string[] NotWindows = ["linux", "macos"];

    public static IReadOnlyList<SmbCondition> All { get; } =
    [
        new("roundtrip_4mib", null, RoundTrip(4 << 20)),
        new("empty_file", null, RoundTrip(0)),
        new("large_256mib", null, Large),
        new("random_access_reads", null, RandomAccess),
        new("append_overwrite_truncate", null, Mutate),
        new("rename_file_dir_replace", null, RenameFileDirReplace),
        new("case_only_rename", null, CaseOnlyRename),
        new("delete_semantics", null, DeleteSemantics),
        new("special_filenames", null, SpecialNames),
        new("case_insensitive_lookup", null, CaseInsensitiveLookup),
        new("deep_tree_long_names", null, DeepTree),
        new("many_small_files_1000", null, ManySmall),
        new("big_folder_5000", null, BigFolder),
        new("timestamps", null, Timestamps),
        new("disk_space", null, DiskSpace),
        new("sparse_4_5gib", null, Sparse),
        new("concurrent_writers_8", null, ConcurrentWriters),
        new("concurrent_readers_8", null, ConcurrentReaders),
        new("churn_while_listing", null, Churn),
        new("client_killed_mid_transfer", null, KillMidTransfer),
        new("parallel_listings_50", null, ParallelListings),
        new("second_share_isolation", null, SecondShare),
        new("wrong_credentials_refused", null, BadCredentials),
        new("readonly_share", null, ReadOnlyShare),
        new("guest_roundtrip", NotWindows, Guest),
        new("linux_dialects_and_signing", LinuxOnly, Dialects),
        new("linux_smbclient", LinuxOnly, SmbClient),
        new("linux_sessions_50", LinuxOnly, ManySessions),
    ];

    public static SmbCondition Named(string name) => All.Single(c => c.Name == name);

    private static string Seconds(Stopwatch sw) => sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static string MiBps(long bytes, Stopwatch sw) => (bytes / 1048576.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB/s";

    /// <summary>Writes a file of <paramref name="size"/> bytes through the share, reads it back, and checks the bytes on both ends.</summary>
    private static Action<SmbPair> RoundTrip(long size) => p =>
    {
        var name = SmbPair.Unique("rt") + ".dat";
        var (src, want) = p.RandomFile("src-" + name, size);
        var dst = p.Mnt(name);
        p.Cleanup(() => SmbOps.Remove(p.C, dst));
        SmbOps.Copy(p.C, src, dst).Must("copy to the share");
        var readBack = p.ScratchPath("rb-" + name);
        SmbOps.Copy(p.C, dst, readBack).Must("copy back from the share");
        var got = SmbOps.Hash(p.C, readBack);
        p.Must(got == want, $"read back hash {got}, wrote {want}");
        var length = SmbOps.Size(p.C, dst);
        p.Check(length == size, $"size through the client {length}, want {size}");
        p.Must(SmbOps.WaitForHash(p.S, p.Backing(name), want, 30), "the host's folder");
    };

    /// <summary>256 MiB each way (many READ and WRITE requests, the credits' flow), with the throughput.</summary>
    private static void Large(SmbPair p)
    {
        const long size = 256 << 20;
        var name = SmbPair.Unique("large") + ".bin";
        var (src, want) = p.RandomFile("src-" + name, size);
        var dst = p.Mnt(name);
        p.Cleanup(() => SmbOps.Remove(p.C, dst));
        var write = Stopwatch.StartNew();
        SmbOps.Copy(p.C, src, dst).Must("copy 256 MiB to the share");
        write.Stop();
        var readBack = p.ScratchPath("rb-" + name);
        var read = Stopwatch.StartNew();
        SmbOps.Copy(p.C, dst, readBack).Must("copy 256 MiB back");
        read.Stop();
        p.Note($"write {MiBps(size, write)}, read {MiBps(size, read)}");
        var got = SmbOps.Hash(p.C, readBack);
        p.Must(got == want, $"read back hash {got}, wrote {want}");
        p.Must(SmbOps.WaitForHash(p.S, p.Backing(name), want, 60), "the host's folder");
    }

    /// <summary>MiB-aligned ranges out of the middle of a 64 MiB file (what a video player or a zip reader does), compared with the source.</summary>
    private static void RandomAccess(SmbPair p)
    {
        const long size = 64 << 20;
        var name = SmbPair.Unique("ra") + ".bin";
        var (src, _) = p.RandomFile("src-" + name, size);
        var dst = p.Mnt(name);
        p.Cleanup(() => SmbOps.Remove(p.C, dst));
        SmbOps.Copy(p.C, src, dst).Must("copy to the share");
        foreach (var (offset, length) in new (long, long)[] { (0, 1), (13, 2), (31, 1), (63, 1), (40, 8), (5, 3) })
        {
            var want = SmbOps.HashRangeMiB(p.C, src, offset, length);
            var got = SmbOps.HashRangeMiB(p.C, dst, offset, length);
            p.Check(got == want, $"range {offset}+{length} MiB: read {got}, want {want}");
        }
    }

    /// <summary>
    /// The same edits on a local file and on its copy on the share (append, overwrite in the middle, shrink, grow, overwrite
    /// past the end); after each, the share's copy matches the local one byte for byte (writes at offsets, end of file).
    /// </summary>
    private static void Mutate(SmbPair p)
    {
        var name = SmbPair.Unique("mut") + ".bin";
        var (local, _) = p.RandomFile("local-" + name, 3 << 20);
        var mounted = p.Mnt(name);
        p.Cleanup(() => SmbOps.Remove(p.C, mounted));
        SmbOps.Copy(p.C, local, mounted).Must("copy to the share");
        var (blobA, _) = p.RandomFile("blobA-" + name, 1 << 20);
        var (blobB, _) = p.RandomFile("blobB-" + name, 300 << 10);

        void Step(string what, Func<string, SmbResult> op)
        {
            op(local).Must(what + " (local)");
            op(mounted).Must(what + " (share)");
            var localSize = SmbOps.Size(p.C, local);
            var shareSize = SmbOps.Size(p.C, mounted);
            p.Must(localSize == shareSize, $"after {what}: size through the share {shareSize}, local {localSize}");
            p.Must(SmbOps.Hash(p.C, local) == SmbOps.Hash(p.C, mounted), $"after {what}: the share's copy differs from the local one ({localSize} bytes)");
        }

        Step("append 1 MiB", f => SmbOps.Append(p.C, f, blobA));
        Step("overwrite 300 KiB at 1 MiB", f => SmbOps.WriteAtMiB(p.C, f, 1, blobB));
        Step("shrink to 2.5 MiB", f => SmbOps.Truncate(p.C, f, 2621440));
        Step("grow to 6 MiB", f => SmbOps.Truncate(p.C, f, 6 << 20));
        Step("append 300 KiB", f => SmbOps.Append(p.C, f, blobB));
        Step("overwrite past the end at 8 MiB", f => SmbOps.WriteAtMiB(p.C, f, 8, blobA));
        p.Must(SmbOps.WaitForHash(p.S, p.Backing(name), SmbOps.Hash(p.C, local), 30), "the host's folder");
    }

    /// <summary>Renames a file, a directory, onto an existing file (replacing it), and across directories.</summary>
    private static void RenameFileDirReplace(SmbPair p)
    {
        var d = SmbPair.Unique("ren");
        SmbOps.Mkdir(p.C, p.Mnt(d)).Must("mkdir");
        p.Cleanup(() => SmbOps.Remove(p.C, p.Mnt(d)));
        string M(params string[] parts) => p.Mnt([d, .. parts]);
        string B(params string[] parts) => p.Backing([d, .. parts]);

        SmbOps.WriteText(p.C, M("a.txt"), "alpha").Must("write a.txt");
        SmbOps.Rename(p.C, M("a.txt"), M("b.txt")).Must("rename a.txt -> b.txt");
        p.Check(!SmbOps.Exists(p.C, M("a.txt")), "a.txt still exists after the rename");
        var b = SmbOps.ReadText(p.C, M("b.txt"));
        p.Check(b == "alpha", $"b.txt reads {b}");
        p.Must(SmbOps.WaitForGone(p.S, B("a.txt"), 10), "a.txt gone on the host");

        SmbOps.Mkdir(p.C, M("d1")).Must("mkdir d1");
        SmbOps.WriteText(p.C, M("d1", "inner.txt"), "inner").Must("write d1/inner.txt");
        SmbOps.Rename(p.C, M("d1"), M("d2")).Must("rename d1 -> d2");
        var inner = SmbOps.ReadText(p.C, M("d2", "inner.txt"));
        p.Check(inner == "inner", $"d2/inner.txt reads {inner}");
        p.Check(SmbOps.Exists(p.S, B("d2", "inner.txt")), "d2/inner.txt is not on the host");

        SmbOps.WriteText(p.C, M("c.txt"), "gamma").Must("write c.txt");
        SmbOps.Rename(p.C, M("b.txt"), M("c.txt")).Must("rename b.txt onto c.txt (replace)");
        var c = SmbOps.ReadText(p.C, M("c.txt"));
        p.Check(c == "alpha", $"after the replacing rename, c.txt reads {c} (want alpha)");
        p.Check(!SmbOps.Exists(p.C, M("b.txt")), "b.txt still exists after the replacing rename");
        var onHost = SmbOps.ReadText(p.S, B("c.txt"));
        p.Check(onHost == "alpha", $"on the host c.txt reads {onHost} (want alpha)");

        SmbOps.Rename(p.C, M("c.txt"), M("d2", "moved.txt")).Must("rename c.txt -> d2/moved.txt");
        var moved = SmbOps.ReadText(p.S, B("d2", "moved.txt"));
        p.Check(moved == "alpha", $"on the host d2/moved.txt reads {moved}");
        var names = SmbOps.ListNames(p.C, M());
        p.Check(names.Count == 1 && names[0] == "d2", $"final listing [{string.Join(", ", names)}], want [d2]");
    }

    /// <summary>
    /// Renames a file to its name in another case (Foo.txt -> foo.txt). What happens depends on the client and the host's
    /// file system: a Windows client may refuse a case-only Move-Item before it reaches the server, and macOS's client folds
    /// a case-only rename when the new name looks up as the file itself (it reports success and asks the server for nothing). So
    /// this demands no one result: the data must survive as exactly one readable file, and where the client did send the
    /// rename, the server must have done it. What it guards against is a case-insensitive store refusing the rename as a
    /// collision, which would leave the file under its old name or lose it.
    /// </summary>
    private static void CaseOnlyRename(SmbPair p)
    {
        var d = SmbPair.Unique("case");
        SmbOps.Mkdir(p.C, p.Mnt(d)).Must("mkdir");
        p.Cleanup(() => SmbOps.Remove(p.C, p.Mnt(d)));
        SmbOps.WriteText(p.C, p.Mnt(d, "Foo.txt"), "foo").Must("write Foo.txt");

        var rename = SmbOps.Rename(p.C, p.Mnt(d, "Foo.txt"), p.Mnt(d, "foo.txt"));

        var names = SmbOps.ListNames(p.S, p.Backing(d));
        p.Must(names.Count == 1, $"after the case-only rename the host holds [{string.Join(", ", names)}], want one file (data lost or duplicated)");
        var text = SmbOps.ReadText(p.S, p.Backing(d, names[0]));
        p.Check(text == "foo", $"the surviving file {names[0]} reads {text} (want foo)");
        if (!rename.Ok)
        {
            // the client refused it (common on Windows, which sees the share case-insensitively); fine while the file is
            // intact under its old name
            p.Check(names[0] == "Foo.txt", $"the client says the rename failed, but the host renamed it to {names[0]}");
            p.Note($"the client did not do the case-only rename ({SmbOps.Tail(rename.Output.Replace('\n', ' '), 120)}); the file is intact as Foo.txt");
        }
        else if (names[0] == "foo.txt")
        {
            p.Note("case-only rename applied: Foo.txt -> foo.txt");
        }
        else if (p.ClientOs == "macos" && names[0] == "Foo.txt")
        {
            // where the lookup of foo.txt answers with the file itself (on a Linux host, AnyFS's collections find a name in
            // another case through their fallback, under the file's own name and id), macOS's client takes the rename for one
            // onto itself and reports success without asking the server, as it does on a case-insensitive server
            p.Note("macOS's client folded the case-only rename (reported success, left Foo.txt): foo.txt looked up as the file itself; the data is intact");
        }
        else
        {
            p.Check(false, $"the client reported success but the host still has {names[0]}, not foo.txt");
        }
    }

    /// <summary>A directory with something in it refuses rmdir and an empty one goes; a tree goes recursively; deleting what isn't there fails.</summary>
    private static void DeleteSemantics(SmbPair p)
    {
        var d = SmbPair.Unique("del");
        SmbOps.Mkdir(p.C, p.Mnt(d, "full")).Must("mkdir");
        p.Cleanup(() => SmbOps.Remove(p.C, p.Mnt(d)));
        SmbOps.WriteText(p.C, p.Mnt(d, "full", "f.txt"), "x").Must("write");
        p.Check(!SmbOps.Rmdir(p.C, p.Mnt(d, "full")).Ok, "rmdir of a directory with a file in it succeeded");
        p.Check(SmbOps.Exists(p.C, p.Mnt(d, "full", "f.txt")), "the file inside is gone after the refused rmdir");
        SmbOps.RemoveFile(p.C, p.Mnt(d, "full", "f.txt")).Must("delete the file");
        SmbOps.Rmdir(p.C, p.Mnt(d, "full")).Must("rmdir the now empty directory");
        p.Must(SmbOps.WaitForGone(p.S, p.Backing(d, "full"), 10), "gone on the host");
        p.Check(!SmbOps.RemoveFile(p.C, p.Mnt(d, "never-there.txt")).Ok, "deleting a file that doesn't exist succeeded");
        SmbOps.Mkdir(p.C, p.Mnt(d, "tree", "a", "b", "c")).Must("mkdir tree");
        foreach (var f in new[] { new[] { "tree", "1.txt" }, ["tree", "a", "2.txt"], ["tree", "a", "b", "c", "3.txt"] })
        {
            SmbOps.WriteText(p.C, p.Mnt([d, .. f]), "x").Must("write " + string.Join('/', f));
        }

        SmbOps.RemoveStrict(p.C, p.Mnt(d, "tree")).Must("recursive delete");
        p.Must(SmbOps.WaitForGone(p.S, p.Backing(d, "tree"), 10), "the tree gone on the host");
    }

    /// <summary>Names with spaces, punctuation, quotes, non-ASCII, an emoji, a leading dot and 200+ characters, written, read back and listed.</summary>
    private static void SpecialNames(SmbPair p)
    {
        var d = SmbPair.Unique("names");
        SmbOps.Mkdir(p.C, p.Mnt(d)).Must("mkdir");
        p.Cleanup(() => SmbOps.Remove(p.C, p.Mnt(d)));
        string[] names =
        [
            "hello world.dat",
            "paren(1).dat",
            "plus+dash-_.dat",
            "dotted.name.v2.dat",
            "it's here.txt",
            "#hash %percent &amp ,comma ;semi =eq @at.txt",
            "café_münchen.dat",
            "日本語ファイル.txt",
            "emoji-😀.txt",
            ".hidden",
            "UPPER.TXT",
            new string('a', 200) + ".txt",
            string.Concat(Enumerable.Repeat("日", 60)) + ".txt",
        ];
        foreach (var n in names)
        {
            var f = p.Mnt(d, n);
            var wrote = SmbOps.WriteText(p.C, f, "name:" + n);
            if (!wrote.Ok)
            {
                p.Check(false, $"'{n}': write: {SmbOps.Tail(wrote.Output, 300)}");
                continue;
            }

            var text = SmbOps.ReadText(p.C, f);
            p.Check(text == "name:" + n, $"'{n}': read back {text}");
            p.Check(SmbOps.Exists(p.S, p.Backing(d, n)), $"'{n}': not in the host's folder");
        }

        var count = SmbOps.CountEntries(p.C, p.Mnt(d));
        p.Check(count == names.Length, $"the client lists {count} entries, want {names.Length}");
        foreach (var n in names)
        {
            var deleted = SmbOps.RemoveFile(p.C, p.Mnt(d, n));
            p.Check(deleted.Ok, $"'{n}': delete: {SmbOps.Tail(deleted.Output, 300)}");
        }
    }

    /// <summary>
    /// A name that differs only in case finds the file, on every host: a file is found by any case and a create in another
    /// case is the same file, as Explorer, Finder and anything that stores a path assume. The rig expected a case-sensitive
    /// host (ext4) to make another file; AnyFS's collections look a name up exactly first and then, on a case-sensitive
    /// source, take a unique match in another case (CollectionVfs), so a Linux host answers as the others do.
    /// </summary>
    private static void CaseInsensitiveLookup(SmbPair p)
    {
        var d = SmbPair.Unique("icase");
        SmbOps.Mkdir(p.C, p.Mnt(d)).Must("mkdir");
        p.Cleanup(() => SmbOps.Remove(p.C, p.Mnt(d)));
        SmbOps.WriteText(p.C, p.Mnt(d, "MixedCase.txt"), "mixed").Must("write MixedCase.txt");
        var exact = SmbOps.ReadText(p.C, p.Mnt(d, "MixedCase.txt"));
        p.Must(exact == "mixed", $"the exact name MixedCase.txt reads {exact}");

        foreach (var n in new[] { "mixedcase.txt", "MIXEDCASE.TXT" })
        {
            var text = SmbOps.ReadText(p.C, p.Mnt(d, n));
            p.Check(text == "mixed", $"MixedCase.txt as {n} reads {text}");
        }

        var upper = SmbOps.ReadText(p.C, p.Mnt(d.ToUpperInvariant(), "mixedcase.txt"));
        p.Check(upper == "mixed", $"the directory as {d.ToUpperInvariant()} reads {upper}");
        SmbOps.WriteText(p.C, p.Mnt(d, "MIXEDCASE.TXT"), "again").Must("write as MIXEDCASE.TXT");
        var one = SmbOps.ListNames(p.S, p.Backing(d));
        p.Check(one.Count == 1, $"the host now has [{string.Join(", ", one)}]: a write in another case made a second file");
    }

    /// <summary>
    /// Nested directories down to a path near Windows' 260-character limit (22 levels as the rig has them, fewer where the
    /// share's own path on the client is longer than its Z:\), a file at the bottom, listings at three levels, a recursive delete.
    /// </summary>
    private static void DeepTree(SmbPair p)
    {
        var top = SmbPair.Unique("deep");
        var levels = 22;
        if (p.C.IsWindows)
        {
            levels = Math.Min(levels, (248 - p.Root.Length - top.Length - 2 - "bottom.txt".Length) / "\\level-00".Length);
        }

        var parts = new List<string> { top };
        for (var i = 1; i <= levels; i++)
        {
            parts.Add($"level-{i:00}");
        }

        SmbOps.Mkdir(p.C, p.Mnt([.. parts])).Must($"mkdir {levels} levels");
        p.Cleanup(() => SmbOps.Remove(p.C, p.Mnt(top)));
        var bottom = p.Mnt([.. parts, "bottom.txt"]);
        p.Note($"{levels} levels; the bottom file's path on the client is {bottom.Length} characters");
        SmbOps.WriteText(p.C, bottom, "bottom").Must("write at the bottom");
        var text = SmbOps.ReadText(p.C, bottom);
        p.Check(text == "bottom", $"bottom.txt reads {text}");
        p.Check(SmbOps.Exists(p.S, p.Backing([.. parts, "bottom.txt"])), "bottom.txt is not in the host's folder");
        foreach (var level in new[] { 1, 11, parts.Count })
        {
            var names = SmbOps.ListNames(p.C, p.Mnt([.. parts.Take(level)]));
            var want = level < parts.Count ? parts[level] : "bottom.txt";
            p.Check(names.Count == 1 && names[0] == want, $"level {level} lists [{string.Join(", ", names)}], want [{want}]");
        }

        SmbOps.RemoveStrict(p.C, p.Mnt(top)).Must("recursive delete");
        p.Must(SmbOps.WaitForGone(p.S, p.Backing(top), 10), "gone on the host");
    }

    /// <summary>1000 small files made through the share (CREATE, WRITE, CLOSE churn), listed (QUERY_DIRECTORY's paging), read and deleted.</summary>
    private static void ManySmall(SmbPair p)
    {
        const int n = 1000;
        var d = SmbPair.Unique("many");
        SmbOps.Mkdir(p.C, p.Mnt(d)).Must("mkdir");
        p.Cleanup(() => SmbOps.Remove(p.C, p.Mnt(d)));
        var create = Stopwatch.StartNew();
        SmbOps.NumberedFiles(p.C, p.Mnt(d), n).Must("create 1000 files");
        create.Stop();
        p.Must(SmbOps.WaitForCount(p.C, p.Mnt(d), n, 30), "the client lists all 1000");
        foreach (var i in new[] { 1, n / 2, n })
        {
            var text = SmbOps.ReadText(p.C, p.Mnt(d, $"f_{i}.dat"));
            p.Check(text == $"content-{i}", $"f_{i}.dat reads {text}");
        }

        p.Must(SmbOps.WaitForCount(p.S, p.Backing(d), n, 30), "the host has all 1000");
        var delete = Stopwatch.StartNew();
        SmbOps.RemoveStrict(p.C, p.Mnt(d)).Must("delete the 1000");
        p.Note($"create {Seconds(create)}, delete {Seconds(delete)}");
        p.Must(SmbOps.WaitForGone(p.S, p.Backing(d), 10), "gone on the host");
    }

    /// <summary>
    /// A folder of 5000 entries made on the host, listed through the client: a bare enumeration, one with attributes (as
    /// Finder and Explorer list), and 200 lookups by name (what a client without directory shortcuts does for each entry).
    /// </summary>
    private static void BigFolder(SmbPair p)
    {
        const int n = 5000;
        var d = SmbPair.Unique("big");
        SmbOps.Mkdir(p.S, p.Backing(d)).Must("mkdir on the host");
        p.Cleanup(() => SmbOps.Remove(p.S, p.Backing(d)));
        SmbOps.NumberedFiles(p.S, p.Backing(d), n).Must("create 5000 files on the host");

        var enumerate = Stopwatch.StartNew();
        var count = SmbOps.CountEntries(p.C, p.Mnt(d));
        enumerate.Stop();
        p.Check(count == n, $"the client enumerates {count} entries, want {n}");
        var withAttributes = Stopwatch.StartNew();
        count = SmbOps.CountWithAttributes(p.C, p.Mnt(d));
        withAttributes.Stop();
        p.Check(count == n, $"the client lists {count} entries with attributes, want {n}");
        var names = Enumerable.Range(0, 200).Select(i => $"f_{1 + (i * (n / 200))}.dat").ToList();
        var lookups = Stopwatch.StartNew();
        SmbOps.LookupMany(p.C, p.Mnt(d), names).Must("200 lookups by name");
        lookups.Stop();
        p.Note($"enumerate {Seconds(enumerate)}, with attributes {Seconds(withAttributes)}, 200 lookups {Seconds(lookups)}");
        foreach (var (what, sw) in new[] { ("enumeration", enumerate), ("listing with attributes", withAttributes), ("200 lookups", lookups) })
        {
            p.Check(sw.Elapsed < TimeSpan.FromSeconds(90), $"{what} of {n} entries took {Seconds(sw)}");
        }
    }

    /// <summary>A modification time set through the share reads back on both ends; and one set on the host reads through the client.</summary>
    private static void Timestamps(SmbPair p)
    {
        var d = SmbPair.Unique("time");
        SmbOps.Mkdir(p.C, p.Mnt(d)).Must("mkdir");
        p.Cleanup(() => SmbOps.Remove(p.C, p.Mnt(d)));
        const long epoch = 981173106;    // 2001-02-03 04:05:06 UTC
        SmbOps.WriteText(p.C, p.Mnt(d, "stamp.txt"), "t").Must("write");
        SmbOps.SetMtime(p.C, p.Mnt(d, "stamp.txt"), epoch).Must("set the modification time through the share");
        var client = SmbOps.Mtime(p.C, p.Mnt(d, "stamp.txt"));
        p.Check(client is { } c && Math.Abs(c - epoch) <= 2, $"the modification time through the client is {client}, want {epoch}");
        var host = SmbOps.Mtime(p.S, p.Backing(d, "stamp.txt"));
        p.Check(host is { } h && Math.Abs(h - epoch) <= 2, $"the modification time on the host is {host}, want {epoch}");
        const long epoch2 = 1234567890;    // 2009-02-13 23:31:30 UTC
        SmbOps.WriteText(p.S, p.Backing(d, "server-stamp.txt"), "t").Must("write on the host");
        SmbOps.SetMtime(p.S, p.Backing(d, "server-stamp.txt"), epoch2).Must("set the modification time on the host");
        var through = SmbOps.Mtime(p.C, p.Mnt(d, "server-stamp.txt"));
        p.Check(through is { } t && Math.Abs(t - epoch2) <= 2, $"the time set on the host reads {through} through the client, want {epoch2}");
    }

    /// <summary>The free space the client shows for the share is the host volume's.</summary>
    private static void DiskSpace(SmbPair p)
    {
        var (total, free) = SmbOps.DiskSpace(p.C, p.Root);
        p.Must(total > 0 && free > 0 && free <= total, $"the client sees total {total}, free {free}");
        var (hostTotal, hostFree) = SmbOps.DiskSpace(p.S, p.Backing());
        p.Note($"the client sees {free >> 30}/{total >> 30} GiB free, the host {hostFree >> 30}/{hostTotal >> 30} GiB");
        p.Check(Math.Abs(total - hostTotal) / (double)hostTotal <= 0.05, $"the client sees a {total >> 30} GiB volume, the host's is {hostTotal >> 30} GiB");
    }

    /// <summary>A 4.5 GiB file made by extending it (a size past 32 bits), its last MiB written and read back, a MiB out of the hole.</summary>
    private static void Sparse(SmbPair p)
    {
        const long sizeMiB = 4608;
        const long size = sizeMiB << 20;
        var (_, hostFree) = SmbOps.DiskSpace(p.S, p.Backing());
        if (p.S.IsWindows && hostFree < size + (2L << 30))
        {
            // NTFS gives an extended file its clusters at once (it is not made sparse)
            Assert.Inconclusive($"{p.S.Name} has {hostFree >> 30} GiB free; NTFS needs 4.5 GiB and some for the extended file");
        }

        var name = SmbPair.Unique("sparse") + ".bin";
        var f = p.Mnt(name);
        p.Cleanup(() => SmbOps.Remove(p.C, f));
        SmbOps.WriteText(p.C, f, "").Must("create");
        SmbOps.Truncate(p.C, f, size).Must("extend to 4.5 GiB");
        var length = SmbOps.Size(p.C, f);
        p.Must(length == size, $"the size through the client is {length}, want {size}");
        var hostLength = SmbOps.Size(p.S, p.Backing(name));
        p.Must(hostLength == size, $"the size on the host is {hostLength}, want {size}");
        var (blob, blobHash) = p.RandomFile("tail-" + name, 1 << 20);
        var write = Stopwatch.StartNew();
        SmbOps.WriteAtMiB(p.C, f, sizeMiB - 1, blob).Must("write the last MiB");
        p.Note($"the last MiB written in {Seconds(write)}");
        length = SmbOps.Size(p.C, f);
        p.Check(length == size, $"the size after the tail write is {length}, want {size}");
        var tail = SmbOps.HashRangeMiB(p.C, f, sizeMiB - 1, 1);
        p.Check(tail == blobHash, $"the last MiB reads {tail}, want {blobHash}");
        var hole = SmbOps.HashRangeMiB(p.C, f, 2048, 1);
        p.Check(hole == SmbOps.ZeroHashMiB(1), $"a MiB out of the hole reads {hole}, want zeros");
        var hostTail = SmbOps.HashRangeMiB(p.S, p.Backing(name), sizeMiB - 1, 1);
        p.Check(hostTail == blobHash, $"on the host the last MiB reads {hostTail}, want {blobHash}");
        SmbOps.RemoveFile(p.C, f).Must("delete");
    }

    /// <summary>Eight processes writing at once.</summary>
    private static void ConcurrentWriters(SmbPair p)
    {
        const int n = 8;
        var d = SmbPair.Unique("writers");
        SmbOps.Mkdir(p.C, p.Mnt(d)).Must("mkdir");
        p.Cleanup(() => SmbOps.Remove(p.C, p.Mnt(d)));
        var (src, want) = p.RandomFile("writers-src.bin", 8 << 20);
        var scripts = Enumerable.Range(1, n).Select(i =>
        {
            var dst = p.Mnt(d, $"conc_{i}.dat");
            return p.C.IsWindows
                ? $"Copy-Item -LiteralPath {SmbOps.Q(p.C, src)} -Destination {SmbOps.Q(p.C, dst)} -Force"
                : $"cp {SmbOps.Q(p.C, src)} {SmbOps.Q(p.C, dst)}";
        }).ToList();
        var sw = Stopwatch.StartNew();
        var results = SmbOps.Scripts(p.C, p.ScratchPath("par-writers"), scripts, 600);
        p.Note($"8 x 8 MiB in {Seconds(sw)}");
        for (var i = 0; i < results.Count; i++)
        {
            p.Check(results[i].Ok, $"writer {i + 1}: exit {results[i].Code}: {SmbOps.Tail(results[i].Output, 500)}");
        }

        p.Must(SmbOps.WaitForCount(p.S, p.Backing(d), n, 30), "the host has all 8");
        for (var i = 1; i <= n; i++)
        {
            p.Check(SmbOps.WaitForHash(p.S, p.Backing(d, $"conc_{i}.dat"), want, 30), $"conc_{i}.dat");
        }
    }

    /// <summary>Eight processes reading the same 64 MiB file at once.</summary>
    private static void ConcurrentReaders(SmbPair p)
    {
        const int n = 8;
        var name = SmbPair.Unique("readers") + ".bin";
        var want = SmbOps.RandomFile(p.S, p.Backing(name), 64 << 20);
        p.Cleanup(() => SmbOps.Remove(p.S, p.Backing(name)));
        var path = SmbOps.Q(p.C, p.Mnt(name));
        var script = p.ClientOs switch
        {
            "windows" => $"(Get-FileHash -Algorithm SHA256 -LiteralPath {path}).Hash.ToLower()",
            "macos" => $"shasum -a 256 {path} | cut -c1-64",
            _ => $"sha256sum {path} | cut -c1-64",
        };
        var sw = Stopwatch.StartNew();
        var results = SmbOps.Scripts(p.C, p.ScratchPath("par-readers"), Enumerable.Repeat(script, n).ToList(), 600);
        p.Note($"8 x 64 MiB in {Seconds(sw)}");
        for (var i = 0; i < results.Count; i++)
        {
            var got = results[i].Output.Trim();
            p.Check(results[i].Ok && got == want, $"reader {i}: exit {results[i].Code}, hash {SmbOps.Tail(got, 200)}, want {want}");
        }
    }

    /// <summary>300 files made, renamed and deleted while another process lists the directory without pause: no listing may fail, nothing may be left.</summary>
    private static void Churn(SmbPair p)
    {
        var d = SmbPair.Unique("churn");
        SmbOps.Mkdir(p.C, p.Mnt(d)).Must("mkdir");
        p.Cleanup(() => SmbOps.Remove(p.C, p.Mnt(d)));
        var dir = SmbOps.Q(p.C, p.Mnt(d));
        string churn, lister;
        if (p.C.IsWindows)
        {
            churn = $$"""$d = {{dir}}; foreach ($i in 1..300) { [IO.File]::WriteAllText("$d\f_$i", 'x'); Move-Item -LiteralPath "$d\f_$i" -Destination "$d\g_$i"; [IO.File]::Delete("$d\g_$i") }; [IO.File]::WriteAllText("$d\done", 'done')""";
            lister = $$"""$d = {{dir}}; $n = 0; $deadline = (Get-Date).AddMinutes(10); while (-not (Test-Path -LiteralPath "$d\done")) { $null = @([IO.Directory]::EnumerateFileSystemEntries($d)); $n++; if ((Get-Date) -gt $deadline) { throw 'the churn never finished' } }; "listed $n times" """;
        }
        else
        {
            churn = $$"""d={{dir}}; for i in $(seq 1 300); do printf x > "$d/f_$i" && mv "$d/f_$i" "$d/g_$i" && rm "$d/g_$i" || exit 1; done; printf done > "$d/done" """;
            lister = $$"""d={{dir}}; n=0; end=$(( $(date +%s) + 600 )); while [ ! -e "$d/done" ]; do ls -A "$d" >/dev/null || exit 1; n=$((n+1)); [ "$(date +%s)" -gt "$end" ] && exit 2; done; echo "listed $n times" """;
        }

        var results = SmbOps.Scripts(p.C, p.ScratchPath("par-churn"), [churn, lister], 900);
        p.Check(results[0].Ok, $"churn: exit {results[0].Code}: {SmbOps.Tail(results[0].Output, 500)}");
        p.Check(results[1].Ok, $"lister: exit {results[1].Code}: {SmbOps.Tail(results[1].Output, 500)}");
        p.Note(results[1].Output.Trim());
        var names = SmbOps.ListNames(p.S, p.Backing(d));
        p.Check(names.Count == 1 && names[0] == "done", $"left on the host after the churn: [{string.Join(", ", names)}] (want [done])");
    }

    /// <summary>A copy killed half-way: the server carries on serving this client (the half-written file deletes, a round trip works) and anyfs is still up.</summary>
    private static void KillMidTransfer(SmbPair p)
    {
        var (src, _) = p.RandomFile("kill-src.bin", 512 << 20);
        var name = SmbPair.Unique("killed") + ".bin";
        var dst = p.Mnt(name);
        p.Cleanup(() =>
        {
            SmbOps.Remove(p.C, dst);
            SmbOps.Remove(p.S, p.Backing(name));
        });
        var r = p.C.IsWindows
            ? SmbOps.Run(p.C, $"$p = Start-Process cmd.exe -ArgumentList '/c','copy','/y',{SmbOps.Q(p.C, src)},{SmbOps.Q(p.C, dst)} -PassThru -WindowStyle Hidden; Start-Sleep -Milliseconds 1500; Stop-Process -Id $p.Id -Force; Start-Sleep -Milliseconds 500; if ($p.HasExited) {{ 'killed' }} else {{ 'still running' }}", 120)
            : SmbOps.Run(p.C, $"cp {SmbOps.Q(p.C, src)} {SmbOps.Q(p.C, dst)} & pid=$!; sleep 1.5; kill -9 $pid; wait $pid 2>/dev/null; echo killed", 120);
        r.Must("start and kill the copy");
        if (SmbOps.Exists(p.C, dst))
        {
            p.Note($"the copy was killed at {SmbOps.Size(p.C, dst) >> 20} MiB");
            SmbOps.RemoveFile(p.C, dst).Must("delete the half-written file");
        }
        else
        {
            p.Note("the copy was killed before the file appeared");
        }

        var running = p.S.IsWindows
            ? SmbOps.Run(p.S, $"@(Get-Process anyfs -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -eq '{p.AnyFs.Exe}' }}).Count").Output.Trim() != "0"
            : SmbOps.Run(p.S, $"pgrep -f '{p.AnyFs.Exe} [s]erve' >/dev/null && echo up || echo down").Output.Trim() == "up";
        p.Must(running, $"anyfs is no longer running on {p.S.Name}");
        RoundTrip(4 << 20)(p);
    }

    /// <summary>
    /// 50 listings at once on the mount: concurrent requests on one connection (or several, on Windows, which opens more as
    /// it sees fit). None may fail, and a listing once they settle must be exactly right. Windows' and Linux's counts under
    /// load are exact too; macOS's client caches directory reads on its Apple-extension path and can hand a concurrent
    /// listing a passing partial or merged count, its own doing rather than the server's, so there they are only recorded.
    /// </summary>
    private static void ParallelListings(SmbPair p)
    {
        const int n = 50;
        var d = SmbPair.Unique("plist");
        SmbOps.Mkdir(p.C, p.Mnt(d)).Must("mkdir");
        p.Cleanup(() => SmbOps.Remove(p.C, p.Mnt(d)));
        SmbOps.NumberedFiles(p.C, p.Mnt(d), n).Must("create 50 files");
        // each prints its count and exits 2 only when the listing itself failed (an I/O error, not a surprising count)
        var dir = SmbOps.Q(p.C, p.Mnt(d));
        var script = p.C.IsWindows
            ? $"try {{ @(Get-ChildItem -LiteralPath {dir} -File -Force).Count }} catch {{ exit 2 }}"
            : $"out=$(ls -lA {dir} 2>/dev/null) || exit 2; printf '%s\\n' \"$out\" | grep -c '^-' || true";
        var sw = Stopwatch.StartNew();
        var results = SmbOps.Scripts(p.C, p.ScratchPath("par-list"), Enumerable.Repeat(script, n).ToList(), 600);
        var errors = 0;
        var exact = 0;
        var spread = new SortedDictionary<int, int>();
        foreach (var r in results)
        {
            if (!r.Ok)
            {
                errors++;
                continue;
            }

            var c = int.TryParse(r.Output.Trim(), out var v) ? v : -1;
            spread[c] = spread.GetValueOrDefault(c) + 1;
            exact += c == n ? 1 : 0;
        }

        var counts = string.Join(", ", spread.Select(kv => $"{kv.Key}: {kv.Value}"));
        p.Note($"50 listings in {Seconds(sw)}: {exact} exact, {errors} failed");
        p.Check(errors == 0, $"{errors} of {n} concurrent listings failed with an I/O error");
        if (p.ClientOs == "macos")
        {
            if (exact != n)
            {
                p.Note($"macOS's concurrent listing counts (its client's directory cache): {counts}");
            }
        }
        else
        {
            p.Check(exact == n, $"{n - exact} of {n} concurrent listings saw a count other than {n}: {counts}");
        }

        p.Must(SmbOps.WaitForCount(p.C, p.Mnt(d), n, 30), "a settled listing through the client");
    }

    /// <summary>Two shares of one server don't see each other's files.</summary>
    private static void SecondShare(SmbPair p)
    {
        var secondMount = p.NewMount("smbsecond", "Y");
        var second = p.ShareFolder(secondMount, p.AnyFs.SmbSecondDir);
        var a = SmbPair.Unique("only-data") + ".txt";
        var b = SmbPair.Unique("only-second") + ".txt";
        p.Cleanup(() => SmbOps.Remove(p.C, p.Mnt(a)));
        SmbOps.WriteText(p.C, p.Mnt(a), "data").Must("write in smbdata");
        SmbOps.WriteText(p.C, SmbOps.Join(p.C, second, b), "second").Must("write in smbsecond");
        p.Check(!SmbOps.Exists(p.C, SmbOps.Join(p.C, second, a)), $"{a}, written to smbdata, shows in smbsecond");
        p.Check(!SmbOps.Exists(p.C, p.Mnt(b)), $"{b}, written to smbsecond, shows in smbdata");
        p.Check(SmbOps.Exists(p.S, SmbOps.Join(p.S, p.AnyFs.SmbSecondDir, b)), $"{b} is not in smbsecond's folder on the host");
        p.Check(!SmbOps.Exists(p.S, p.Backing(b)), $"{b} landed in smbdata's folder on the host");
        SmbOps.RemoveFile(p.C, SmbOps.Join(p.C, second, b)).Must("delete in smbsecond");
    }

    /// <summary>A wrong password, a wrong user and a share that doesn't exist are refused, promptly.</summary>
    private static void BadCredentials(SmbPair p)
    {
        foreach (var (what, mount) in new[]
        {
            ("wrong password", new SmbMount(p.C, p.ServerIp, "smbdata", AnyFsHost.SmbPort) { Password = "nope", Tag = "V" }),
            ("wrong user", new SmbMount(p.C, p.ServerIp, "smbdata", AnyFsHost.SmbPort) { User = "nobody", Tag = "V" }),
            ("no such share", new SmbMount(p.C, p.ServerIp, "nosuchshare", AnyFsHost.SmbPort) { Tag = "V" }),
        })
        {
            var sw = Stopwatch.StartNew();
            var r = mount.TryMount();
            if (!r.Ok && what != "no such share" && p.C.IsWindows && p.ServerIp != "127.0.0.1" && r.Output.Contains("System error 67", StringComparison.Ordinal))
            {
                // the refusal is the client's own (see SmbMount.Mount): it never reached the server, so this says nothing
                Assert.Inconclusive($"{what}: net use failed with error 67 before reaching the server (WinFsp.Np first in the network provider order)");
            }

            if (r.Ok)
            {
                p.Check(false, $"{what}: the mount succeeded");
                mount.Unmount();
            }
            else if (r.Code == -1)
            {
                p.Check(false, $"{what}: the mount hung ({SmbOps.Tail(r.Output, 300)})");
            }
            else
            {
                p.Note($"{what}: refused in {Seconds(sw)}: {SmbOps.Tail(r.Output.Replace('\n', ' '), 200)}");
            }
        }
    }

    /// <summary>A read-only share (smbro: smbdata as a read-only membership) reads, and refuses a write, a delete, a mkdir, a rename and a truncate.</summary>
    private static void ReadOnlyShare(SmbPair p)
    {
        var ro = SmbPair.Unique("ro") + ".txt";
        SmbOps.WriteText(p.S, p.Backing(ro), "look only").Must("put a file in the host's folder");
        p.Cleanup(() => SmbOps.Remove(p.S, p.Backing(ro)));
        var mount = p.NewMount("smbro", "X");
        var root = p.ShareFolder(mount, p.AnyFs.SmbDataDir);
        string M(string name) => SmbOps.Join(p.C, root, name);
        var text = SmbOps.ReadText(p.C, M(ro));
        p.Must(text == "look only", $"{ro} reads {text}");
        var made = SmbPair.Unique("new");
        foreach (var (what, op) in new (string, Func<SmbResult>)[]
        {
            ("write", () => SmbOps.WriteText(p.C, M(made + ".txt"), "x")),
            ("delete", () => SmbOps.RemoveFile(p.C, M(ro))),
            ("mkdir", () => SmbOps.MkdirStrict(p.C, M(made + "-dir"))),
            ("rename", () => SmbOps.Rename(p.C, M(ro), M(made + "-renamed.txt"))),
            ("truncate", () => SmbOps.Truncate(p.C, M(ro), 0)),
        })
        {
            p.Check(!op().Ok, $"a {what} on the read-only share succeeded");
        }

        var after = SmbOps.ReadText(p.S, p.Backing(ro));
        p.Check(after == "look only", $"the file on the host was damaged: {after}");
        foreach (var leftover in new[] { made + ".txt", made + "-dir", made + "-renamed.txt" })
        {
            p.Check(!SmbOps.Exists(p.S, p.Backing(leftover)), $"{leftover} landed in the host's folder");
        }
    }

    /// <summary>smbguest (smbdata to guests, on its own port) mounted anonymously: a file round trip.</summary>
    private static void Guest(SmbPair p)
    {
        var mount = p.NewMount("smbguest", "W", guest: true, port: AnyFsHost.SmbGuestPort);
        var root = p.ShareFolder(mount, p.AnyFs.SmbDataDir);
        var name = SmbPair.Unique("guest") + ".dat";
        var (src, want) = p.RandomFile("guest-src.dat", 1 << 20);
        var dst = SmbOps.Join(p.C, root, name);
        p.Cleanup(() => SmbOps.Remove(p.S, p.Backing(name)));
        SmbOps.Copy(p.C, src, dst).Must("copy as a guest");
        var readBack = p.ScratchPath("guest-rb.dat");
        SmbOps.Copy(p.C, dst, readBack).Must("copy back as a guest");
        var got = SmbOps.Hash(p.C, readBack);
        p.Check(got == want, $"read back {got}, want {want}");
        p.Must(SmbOps.WaitForHash(p.S, p.Backing(name), want, 30), "the host's folder");
        SmbOps.RemoveFile(p.C, dst).Must("delete as a guest");
    }

    /// <summary>Linux's client: a round trip on each dialect the server offers, signed when the client insists, and the default negotiation; a clean refusal of what it doesn't offer (3.1.1, encryption).</summary>
    private static void Dialects(SmbPair p)
    {
        void Try(string? vers, string? options, bool wantOk)
        {
            var mount = new SmbMount(p.C, p.ServerIp, "smbdata", AnyFsHost.SmbPort) { Tag = "dial", Vers = vers, Options = options };
            var label = (vers is null ? "default negotiation" : "vers=" + vers) + (options is null ? "" : " " + options);
            var r = mount.TryMount();
            try
            {
                if (!wantOk)
                {
                    if (r.Ok)
                    {
                        p.Check(false, $"{label}: mounted, but the server doesn't offer it");
                    }
                    else if (r.Code == -1)
                    {
                        p.Check(false, $"{label}: the mount hung");
                    }
                    else
                    {
                        p.Note($"{label}: refused as expected: {SmbOps.Tail(r.Output.Replace('\n', ' '), 150)}");
                    }

                    return;
                }

                if (!r.Ok)
                {
                    p.Check(false, $"{label}: mount failed (exit {r.Code}): {SmbOps.Tail(r.Output, 300)}");
                    return;
                }

                var f = SmbOps.Join(p.C, p.ShareFolder(mount, p.AnyFs.SmbDataDir), SmbPair.Unique("dial") + ".txt");
                var wrote = SmbOps.WriteText(p.C, f, label);
                if (!wrote.Ok)
                {
                    p.Check(false, $"{label}: write: {SmbOps.Tail(wrote.Output, 300)}");
                    return;
                }

                var text = SmbOps.ReadText(p.C, f);
                p.Check(text == label, $"{label}: read back {text}");
                SmbOps.RemoveFile(p.C, f);
            }
            finally
            {
                mount.Unmount();
            }
        }

        foreach (var v in new[] { "2.0", "2.1", "3.0", "3.02", null })
        {
            Try(v, null, true);
        }

        Try("2.1", "sign", true);     // HMAC-SHA256 signing, which the client insists on
        Try("3.02", "sign", true);    // AES-CMAC signing
        Try("3.1.1", null, false);    // not offered: the mount must fail, not hang
        Try("3.0", "seal", false);    // no encryption: the mount must fail, not hang
    }

    /// <summary>Samba's smbclient, a second client beside the kernel's: puts, gets, renames, makes and removes a directory, lists, deletes.</summary>
    private static void SmbClient(SmbPair p)
    {
        var (src, want) = p.RandomFile("smbc-src.dat", 2 << 20);
        var readBack = p.ScratchPath("smbc-rb.dat");
        var name = SmbPair.Unique("smbc") + ".dat";
        var folder = AnyFsHost.TreePrefixOfLocalPath(p.AnyFs.SmbDataDir);
        var dir = SmbPair.Unique("smbc-dir");
        p.Cleanup(() =>
        {
            SmbOps.Remove(p.S, p.Backing(name));
            SmbOps.Remove(p.S, p.Backing(name + ".renamed"));
            SmbOps.Remove(p.S, p.Backing(dir));
        });
        var script = $"cd \"{folder}\"; put \"{src}\" \"{name}\"; get \"{name}\" \"{readBack}\"; rename \"{name}\" \"{name}.renamed\"; mkdir {dir}; rmdir {dir}; ls; del \"{name}.renamed\"";
        var r = SmbOps.Run(p.C, $"smbclient //{p.ServerIp}/smbdata -p {AnyFsHost.SmbPort} -U {SmbOps.Q(p.C, AnyFsHost.SmbUser + "%" + AnyFsHost.SmbPassword)} -m SMB3 -c {SmbOps.Q(p.C, script)}", 300).Must("smbclient's script");
        var got = SmbOps.Exists(p.C, readBack) ? SmbOps.Hash(p.C, readBack) : "(no file)";
        p.Check(got == want, $"smbclient got back {got}, want {want}");
        p.Check(!SmbOps.Exists(p.S, p.Backing(name + ".renamed")), "the file is still there after smbclient's del");
        p.Check(r.Output.Contains(name + ".renamed", StringComparison.Ordinal), $"smbclient's ls doesn't show the renamed file:\n{SmbOps.Tail(r.Output, 1000)}");
    }

    /// <summary>50 SMB sessions at once from Linux, each signing in and listing.</summary>
    private static void ManySessions(SmbPair p)
    {
        var folder = AnyFsHost.TreePrefixOfLocalPath(p.AnyFs.SmbDataDir);
        // smbclient reports its errors on stdout, so that is kept
        var script = $"smbclient //{p.ServerIp}/smbdata -p {AnyFsHost.SmbPort} -U {SmbOps.Q(p.C, AnyFsHost.SmbUser + "%" + AnyFsHost.SmbPassword)} -m SMB3 -c {SmbOps.Q(p.C, $"cd \"{folder}\"; ls")} | tail -n 3";
        var sw = Stopwatch.StartNew();
        var results = SmbOps.Scripts(p.C, p.ScratchPath("par-sessions"), Enumerable.Repeat(script, 50).ToList(), 600);
        p.Note($"50 sessions in {Seconds(sw)}");
        var bad = results.Select((r, i) => (r, i)).Where(x => !x.r.Ok).ToList();
        foreach (var (r, i) in bad.Take(3))
        {
            p.Check(false, $"session {i}: exit {r.Code}: {SmbOps.Tail(r.Output, 300)}");
        }

        p.Check(bad.Count <= 3, $"{bad.Count} of 50 sessions failed");
    }

    /// <summary>
    /// Several clients at once (portable-smb-server's TestMultiClient): every reachable client machine mounts smbdata; each
    /// writes a file and a directory and sees every other's byte for byte; each renames its file and deletes its own, and the
    /// others see that too. Checks poll for up to 45 s, since Windows keeps directory and file-information caches for 10 s.
    /// </summary>
    public static void SeveralClientsAtOnce(AnyFsHost anyfs, IReadOnlyDictionary<string, Machine> clients)
    {
        if (clients.Count < 2)
        {
            Assert.Inconclusive($"{clients.Count} client(s) selected or reachable; this needs two or more");
        }

        var dir = SmbPair.Unique("mc");
        var hostDir = SmbOps.Join(anyfs.Host, anyfs.SmbDataDir, dir);
        SmbOps.Mkdir(anyfs.Host, hostDir).Must("make the shared folder on the host");
        var everyone = clients.Select(c => new Member(c.Key, c.Value, c.Key == "local")).ToList();
        var members = everyone;
        var failures = new List<string>();

        void Each(string what, Func<Member, string?> step)
        {
            var errors = new string?[members.Count];
            Parallel.For(0, members.Count, i =>
            {
                try
                {
                    errors[i] = step(members[i]);
                }
                catch (Exception ex) when (ex is SmbStepException or InvalidOperationException or IOException or FormatException)
                {
                    errors[i] = ex.Message;
                }
            });
            for (var i = 0; i < members.Count; i++)
            {
                if (errors[i] is { } e)
                {
                    failures.Add($"{members[i].Name}: {what}: {e}");
                }
            }
        }

        try
        {
            Each("mount", m =>
            {
                SmbMount.ForceUnmountAll(m.Machine);
                SmbOps.Mkdir(m.Machine, m.Scratch).Must("make the scratch folder");
                m.Mount = new SmbMount(m.Machine, m.Local ? "127.0.0.1" : anyfs.Host.Ip, "smbdata", AnyFsHost.SmbPort) { Tag = "T" };
                var r = m.Mount.TryMount();
                if (!r.Ok && m.Machine.IsWindows && !m.Local && r.Output.Contains("System error 67", StringComparison.Ordinal))
                {
                    // WinFsp's network provider first in the provider order (see SmbMount.Mount): it never reached the server
                    Console.WriteLine($"{m.Name} left out: net use failed with error 67 before reaching the server (WinFsp.Np first in the network provider order)");
                    return null;
                }

                if (!r.Ok)
                {
                    return $"exit {r.Code}: {SmbOps.Tail(r.Output, 300)}";
                }

                m.Root = SmbOps.Join(m.Machine, m.Mount.Root!, [.. AnyFsHost.TreePrefixOfLocalPath(anyfs.SmbDataDir).Split('/'), dir]);
                return null;
            });
            members = members.Where(m => m.Root is not null).ToList();
            Assert.IsTrue(members.Count >= 2, $"only {members.Count} client(s) mounted the share: {string.Join("; ", failures)}");
            Console.WriteLine($"{members.Count} clients mounted {anyfs.Host.Name}'s smbdata: {string.Join(", ", members.Select(m => m.Name))}");

            Each("write", m =>
            {
                var src = SmbOps.Join(m.Machine, m.Scratch, $"mc-{m.Name}.dat");
                m.Hash = SmbOps.RandomFile(m.Machine, src, 2 << 20);
                SmbOps.Copy(m.Machine, src, m.In($"from-{m.Name}.dat")).Must("copy");
                SmbOps.Mkdir(m.Machine, m.In($"dir-{m.Name}")).Must("mkdir");
                SmbOps.WriteText(m.Machine, m.In($"dir-{m.Name}", "hello.txt"), "from " + m.Name).Must("write");
                return null;
            });
            Each("see the others' writes", m =>
            {
                foreach (var o in members.Where(o => o != m))
                {
                    if (SmbOps.WaitForHash(m.Machine, m.In($"from-{o.Name}.dat"), o.Hash!, 45) is { } e)
                    {
                        return $"{o.Name}'s file: {e}";
                    }

                    var text = SmbOps.ReadText(m.Machine, m.In($"dir-{o.Name}", "hello.txt"));
                    if (text != "from " + o.Name)
                    {
                        return $"{o.Name}'s dir-/hello.txt reads {text}";
                    }
                }

                return null;
            });
            Each("rename", m => SmbOps.Rename(m.Machine, m.In($"from-{m.Name}.dat"), m.In($"moved-{m.Name}.dat")) is { Ok: false } r ? SmbOps.Tail(r.Output, 300) : null);
            Each("see the others' renames", m =>
            {
                foreach (var o in members.Where(o => o != m))
                {
                    if (SmbOps.WaitForGone(m.Machine, m.In($"from-{o.Name}.dat"), 45) is { } gone)
                    {
                        return $"{o.Name}'s old name: {gone}";
                    }

                    if (SmbOps.WaitForHash(m.Machine, m.In($"moved-{o.Name}.dat"), o.Hash!, 45) is { } moved)
                    {
                        return $"{o.Name}'s new name: {moved}";
                    }
                }

                return null;
            });
            Each("delete", m =>
            {
                SmbOps.RemoveFile(m.Machine, m.In($"moved-{m.Name}.dat")).Must("delete the file");
                SmbOps.RemoveStrict(m.Machine, m.In($"dir-{m.Name}")).Must("delete the directory");
                return null;
            });
            Each("see the others' deletes", m =>
            {
                foreach (var o in members.Where(o => o != m))
                {
                    if (SmbOps.WaitForGone(m.Machine, m.In($"moved-{o.Name}.dat"), 45) is { } file)
                    {
                        return $"{o.Name}'s file: {file}";
                    }

                    if (SmbOps.WaitForGone(m.Machine, m.In($"dir-{o.Name}"), 45) is { } folder)
                    {
                        return $"{o.Name}'s directory: {folder}";
                    }
                }

                return null;
            });

            var left = SmbOps.ListNames(anyfs.Host, hostDir);
            if (left.Count != 0)
            {
                failures.Add($"left in the host's folder: [{string.Join(", ", left)}]");
            }
        }
        finally
        {
            Parallel.ForEach(everyone, m =>
            {
                m.Mount?.Unmount();
                SmbOps.Remove(m.Machine, m.Scratch);
            });
            SmbOps.Remove(anyfs.Host, hostDir);
        }

        Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
    }

    private sealed class Member(string name, Machine machine, bool local)
    {
        public string Name { get; } = name;

        public Machine Machine { get; } = machine;

        public bool Local { get; } = local;

        public string Scratch { get; } = machine.Info.Os switch
        {
            "windows" => @"C:\anyfs-lab\smb-scratch-mc",
            "macos" => $"/Users/{machine.Info.User}/anyfs-lab/smb-scratch-mc",
            _ => $"/home/{machine.Info.User}/anyfs-lab/smb-scratch-mc",
        };

        public SmbMount? Mount { get; set; }

        public string? Root { get; set; }

        public string? Hash { get; set; }

        public string In(params string[] parts) => SmbOps.Join(Machine, Root!, parts);
    }
}
