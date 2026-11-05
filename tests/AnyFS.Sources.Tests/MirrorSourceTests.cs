using System.Text;
using AnyFS.Fixtures;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Mirror;

namespace AnyFS.Sources.Tests;

[TestClass]
public class MirrorSourceTests
{
    private string _root = "";

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "mirror-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static string Chain(string fullPath)
    {
        Assert.IsTrue(MirrorRoot.TryParse(fullPath, OperatingSystem.IsWindows(), out var root, out var error), error);
        return root.ChainPath;
    }

    private static async Task<MirrorSource> StartAsync(string paths, TestSourceContext? context = null, int interval = 60, int threads = 8, int pathsInParallel = 8)
    {
        var settings = new MirrorSettings { Paths = paths, CheckIntervalSeconds = interval, CountThreadsPerPath = threads, CountPathsInParallel = pathsInParallel };
        var source = (MirrorSource)new MirrorPlugin().CreateSource(settings);
        await source.InitialiseAsync(context ?? new TestSourceContext("mirror"), CancellationToken.None);
        return source;
    }

    private static async Task<List<string>> NamesAsync(ISource source, string path)
    {
        var names = new List<string>();
        await foreach (var e in source.ListAsync(path, CancellationToken.None))
        {
            names.Add(e.IsDirectory ? e.Name + "/" : e.Name);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static async Task<string> ReadAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length];
        var n = await file.ReadFullyAsync(0, buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, n);
    }

    [TestMethod]
    public async Task AFolderBecomesItsChainAndTheRealTreeHangsUnderIt()
    {
        var folder = Path.Combine(_root, "Photos");
        Directory.CreateDirectory(Path.Combine(folder, "2024"));
        File.WriteAllText(Path.Combine(folder, "2024", "a.jpg"), "jpeg");
        File.WriteAllText(Path.Combine(folder, "top.txt"), "top");
        await using var source = await StartAsync(folder);
        var chain = Chain(folder);
        var segments = chain.Split('/');

        // the chain levels are synthetic, read-only folders
        CollectionAssert.AreEqual(new[] { segments[0] + "/" }, await NamesAsync(source, ""));
        var level = await source.StatAsync(segments[0], CancellationToken.None);
        Assert.IsTrue(level!.IsDirectory);
        Assert.IsTrue(level.Attributes.HasFlag(VfsAttributes.ReadOnly));

        CollectionAssert.AreEqual(new[] { "2024/", "top.txt" }, await NamesAsync(source, chain));
        Assert.AreEqual("jpeg", await ReadAsync(source, chain + "/2024/a.jpg"));
        var stat = await source.StatAsync(chain + "/top.txt", CancellationToken.None);
        Assert.AreEqual(3, stat!.Length);
        Assert.IsNotNull(stat.Modified);
        Assert.IsNull(await source.StatAsync(chain + "/nope", CancellationToken.None));
        Assert.IsNull(await source.StatAsync("zzz/nope", CancellationToken.None));
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await NamesAsync(source, "zzz"));
        Assert.AreEqual(VfsError.NotFound, ex.Error);
        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.CreateDirectoryAsync(segments[0] + "/new", CancellationToken.None));
        Assert.AreEqual(VfsError.ReadOnly, ex.Error, "chain levels can't be written into");
        Assert.AreEqual(ComponentHealth.Ready, ((TestSourceContext)source.GetType().GetProperty("Context", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(source)!).LastHealth);
    }

    [TestMethod]
    public async Task TwoRootsMergeAtSharedChainLevelsAndNestedRootsOverlay()
    {
        var a = Path.Combine(_root, "alpha");
        var b = Path.Combine(_root, "beta");
        var nested = Path.Combine(a, "inner");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "a.txt"), "a");
        File.WriteAllText(Path.Combine(b, "b.txt"), "b");
        File.WriteAllText(Path.Combine(nested, "n.txt"), "n");
        await using var source = await StartAsync(a + "\n" + b + "\n" + nested);
        Assert.AreEqual(3, source.Roots.Count);

        var rootChain = Chain(_root);
        var names = await NamesAsync(source, rootChain);
        CollectionAssert.AreEqual(new[] { "alpha/", "beta/" }, names, "the shared level lists both chains");
        CollectionAssert.AreEqual(new[] { "a.txt", "inner/" }, await NamesAsync(source, Chain(a)));
        Assert.AreEqual("n", await ReadAsync(source, Chain(nested) + "/n.txt"));
        Assert.AreEqual("n", await ReadAsync(source, Chain(a) + "/inner/n.txt"), "the same file through the shorter root");
    }

    [TestMethod]
    public async Task WritesGoThrough()
    {
        var folder = Path.Combine(_root, "rw");
        Directory.CreateDirectory(folder);
        await using var source = await StartAsync(folder);
        var chain = Chain(folder);
        Assert.IsTrue(source.Capabilities.CanWrite);

        await source.CreateDirectoryAsync(chain + "/d", CancellationToken.None);
        Assert.IsTrue(Directory.Exists(Path.Combine(folder, "d")));
        await using (var f = await source.OpenAsync(chain + "/d/x.txt", VfsOpenMode.CreateNew, CancellationToken.None))
        {
            await f.WriteAsync(0, "hello world"u8.ToArray(), CancellationToken.None);
            await f.SetLengthAsync(5, CancellationToken.None);
            await f.FlushAsync(CancellationToken.None);
            Assert.AreEqual(5, f.Length);
        }

        Assert.AreEqual("hello", File.ReadAllText(Path.Combine(folder, "d", "x.txt")));
        await source.RenameAsync(chain + "/d/x.txt", chain + "/d/y.txt", false, CancellationToken.None);
        Assert.IsTrue(File.Exists(Path.Combine(folder, "d", "y.txt")));
        var when = new DateTimeOffset(2021, 2, 3, 4, 5, 6, TimeSpan.Zero);
        await source.SetTimesAsync(chain + "/d/y.txt", null, when, CancellationToken.None);
        Assert.AreEqual(when.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(folder, "d", "y.txt")));

        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.DeleteAsync(chain + "/d", CancellationToken.None));
        Assert.AreEqual(VfsError.DirectoryNotEmpty, ex.Error);
        await source.DeleteAsync(chain + "/d/y.txt", CancellationToken.None);
        await source.DeleteAsync(chain + "/d", CancellationToken.None);
        Assert.IsFalse(Directory.Exists(Path.Combine(folder, "d")));

        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.OpenAsync(chain, VfsOpenMode.Read, CancellationToken.None));
        Assert.AreEqual(VfsError.IsADirectory, ex.Error);
        var space = await source.GetSpaceAsync(chain, CancellationToken.None);
        Assert.IsNotNull(space);
        Assert.IsTrue(space.TotalBytes > 0);
        var top = await source.GetSpaceAsync("", CancellationToken.None);
        Assert.AreEqual(space.TotalBytes, top?.TotalBytes, "the chain's top folder: the mirrored path's volume, so a share of the mirror shows its disk");
    }

    [TestMethod]
    public async Task RangedReadsMatchTheGenerator()
    {
        var folder = Path.Combine(_root, "gen");
        TreeGenerator.Write(folder, largeSize: 3L << 20, manyCount: 10);
        await using var source = await StartAsync(folder);
        var chain = Chain(folder);
        var expected = TreeGenerator.Bytes(TreeGenerator.SeedOf("large.bin"), 3L << 20);
        await using var file = await source.OpenAsync(chain + "/large.bin", VfsOpenMode.Read, CancellationToken.None);
        Assert.AreEqual(expected.Length, file.Length);
        foreach (var offset in new long[] { 0, 1_000_000, expected.Length - 4096 })
        {
            var buffer = new byte[4096];
            Assert.AreEqual(4096, await file.ReadFullyAsync(offset, buffer, CancellationToken.None));
            CollectionAssert.AreEqual(expected[(int)offset..(int)(offset + 4096)], buffer, $"at {offset}");
        }

        Assert.AreEqual(0, await file.ReadAsync(expected.Length, new byte[16], CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "file0000.txt", "file0001.txt" }, (await NamesAsync(source, chain + "/many")).Take(2).ToArray());
        var unicode = await NamesAsync(source, chain + "/unicode");
        Assert.AreEqual("ünïcödé ✓ 日本語.txt", unicode.Single());
        Assert.AreEqual("unicode names\n", await ReadAsync(source, chain + "/unicode/ünïcödé ✓ 日本語.txt"));
    }

    [TestMethod]
    public async Task OneMissingRootIsDegradedAllMissingFails()
    {
        var present = Path.Combine(_root, "present");
        var missing = Path.Combine(_root, "missing");
        Directory.CreateDirectory(present);
        File.WriteAllText(Path.Combine(present, "p.txt"), "p");
        using var context = new TestSourceContext("mirror");
        await using var source = await StartAsync(present + "\n" + missing, context);
        Assert.AreEqual(ComponentHealth.Degraded, context.LastHealth);
        StringAssert.Contains(context.HealthReports[^1].Detail, "missing");
        Assert.AreEqual(1, source.Unreachable.Count);
        Assert.AreEqual("p", await ReadAsync(source, Chain(present) + "/p.txt"), "the reachable root still works");
        var missingNode = await source.StatAsync(Chain(missing), CancellationToken.None);
        Assert.IsTrue(missingNode!.IsDirectory, "a missing root's chain node still appears as a folder");
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await NamesAsync(source, Chain(missing)));
        Assert.AreEqual(VfsError.Unavailable, ex.Error, "but listing it says the path isn't reachable");
        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.StatAsync(Chain(missing) + "/x.txt", CancellationToken.None));
        Assert.AreEqual(VfsError.Unavailable, ex.Error);

        Directory.CreateDirectory(missing);
        source.CheckRoots();
        Assert.AreEqual(ComponentHealth.Ready, context.LastHealth, "a root coming back makes the source Ready again");
        Directory.Delete(present, true);
        source.CheckRoots();
        Assert.AreEqual(ComponentHealth.Degraded, context.LastHealth);

        var failing = (MirrorSource)new MirrorPlugin().CreateSource(new MirrorSettings { Paths = Path.Combine(_root, "nope1") + "\n" + Path.Combine(_root, "nope2") });
        var failure = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => failing.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        Assert.IsFalse(failure.Permanent, "unreachable paths are retried");
        StringAssert.Contains(failure.Message, "none of the 2 paths");
    }

    [TestMethod]
    public async Task BadSettingsFailPermanently()
    {
        var bad = (MirrorSource)new MirrorPlugin().CreateSource(new MirrorSettings { Paths = "relative/path" });
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => bad.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        Assert.IsTrue(ex.Permanent);
        var empty = (MirrorSource)new MirrorPlugin().CreateSource(new MirrorSettings { Paths = "# nothing\n" });
        ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => empty.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        Assert.IsTrue(ex.Permanent);
    }

    [TestMethod]
    public void CaseSensitivityFollowsTheOs()
    {
        var source = (MirrorSource)new MirrorPlugin().CreateSource(new MirrorSettings());
        Assert.AreEqual(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), source.Capabilities.CaseInsensitive);
    }

    [TestMethod]
    public async Task CountFilesWalksEveryPathAtOnceAndAgreesWithAWalkOfTheListings()
    {
        var a = Path.Combine(_root, "A");
        var b = Path.Combine(_root, "B");
        Directory.CreateDirectory(Path.Combine(a, "sub", "deep"));
        Directory.CreateDirectory(Path.Combine(a, "empty"));
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "x.txt"), "x");
        File.WriteAllText(Path.Combine(a, "sub", "y.txt"), "y");
        File.WriteAllText(Path.Combine(a, "sub", "deep", "z.txt"), "z");
        File.WriteAllText(Path.Combine(b, "q.txt"), "q");

        var source = await StartAsync(a + "\n" + b);
        Assert.IsTrue(source is ICountable, "Mirror counts its own tree");
        await ((ICountable)source).CountFilesAsync(CancellationToken.None);
        var (files, directories) = await WalkAsync(source);
        Assert.AreEqual(4, files);
        Assert.AreEqual(files, source.Counters.FilesKnown);
        Assert.AreEqual(directories, source.Counters.DirectoriesKnown, "chain levels plus the real folders, each once");
        Assert.AreEqual(EnumerationState.InProgress, source.Counters.State, "the host marks it Complete");
    }

    [TestMethod]
    public async Task CountFilesSaysWhichFolderItIsIn()
    {
        var a = Path.Combine(_root, "A");
        Directory.CreateDirectory(Path.Combine(a, "sub", "deep"));
        File.WriteAllText(Path.Combine(a, "sub", "deep", "z.txt"), "z");
        var source = await StartAsync(a, threads: 1, pathsInParallel: 1);
        var seen = new List<string?>();
        var chain = "";
        source.CountListing = (root, _) =>
        {
            chain = root.ChainPath;
            seen.Add(source.Counters.CurrentFolder);
        };
        Assert.IsNull(source.Counters.CurrentFolder);
        await ((ICountable)source).CountFilesAsync(CancellationToken.None);
        CollectionAssert.AreEqual(new[] { chain, VfsPath.Join(chain, "sub"), VfsPath.Join(chain, "sub/deep") }, seen, "each folder of the source, as it is listed");
        Assert.IsNull(source.Counters.CurrentFolder, "the count is over");
    }

    [TestMethod]
    public async Task WhatTheFileSystemIsAskedIsARowByItsRealPath()
    {
        var a = Path.Combine(_root, "A");
        Directory.CreateDirectory(Path.Combine(a, "sub"));
        var x = Path.Combine(a, "x.txt");
        File.WriteAllText(x, "hello");
        using var context = new TestSourceContext("mirror");
        var source = await StartAsync(a, context);
        await using var _s = source;
        var chain = Chain(a);
        var asked = context.UpstreamRequests;
        var seen = asked.Requests.Count;
        Assert.AreEqual("Check path " + a, asked.Requests.Single().ToString(), "its start asks whether the path is there");

        // what has been asked since the last look
        List<string> Since()
        {
            var since = asked.Requests.Skip(seen).Select(r => r.ToString()).ToList();
            seen = asked.Requests.Count;
            return since;
        }

        var check = "Check path " + a;

        Assert.AreEqual(5, (await source.StatAsync(VfsPath.Join(chain, "x.txt"), CancellationToken.None))?.Length);
        CollectionAssert.AreEqual(new[] { check, "Get metadata " + x }, Since(), "a file's metadata");

        Assert.IsNull(await source.StatAsync(VfsPath.Join(chain, "nothing"), CancellationToken.None));
        CollectionAssert.AreEqual(new[] { check, "Get metadata " + Path.Combine(a, "nothing") }, Since(), "asking is asking, whatever the answer");
        Assert.AreEqual("not found", asked.Requests[^1].Answer, "and it says what the answer was");
        Assert.IsFalse(asked.Requests[^1].Refused, "which is no failure");

        CollectionAssert.AreEqual(new[] { "sub/", "x.txt" }, await NamesAsync(source, chain));
        CollectionAssert.AreEqual(new[] { check, "Get metadata " + a, "List folder " + a }, Since(), "a folder listing");
        Assert.AreEqual(2, asked.Requests[^1].Entries);
        Assert.IsTrue(asked.Requests[^1].IsDone);

        // a level of the chain above the mirrored path is AnyFS's own: nothing is asked for it
        if (chain.Contains('/', StringComparison.Ordinal))
        {
            Assert.IsNotNull(await source.StatAsync(VfsPath.Parent(chain), CancellationToken.None));
            await NamesAsync(source, VfsPath.Parent(chain));
            CollectionAssert.AreEqual(Array.Empty<string>(), Since());
        }

        await using (var file = await source.OpenAsync(VfsPath.Join(chain, "x.txt"), VfsOpenMode.Read, CancellationToken.None))
        {
            CollectionAssert.AreEqual(new[] { check, "Get metadata " + x, "Read file " + x }, Since());
            var read = asked.Requests[^1];
            Assert.IsFalse(read.IsDone, "an open file is one request until it is closed");
            var buffer = new byte[5];
            Assert.AreEqual(5, await file.ReadAsync(0, buffer, CancellationToken.None));
            Assert.AreEqual(5, read.Bytes, "its bytes go by on it");
        }

        Assert.IsTrue(asked.Requests[^1].IsDone);

        var written = Path.Combine(a, "new.txt");
        await using (var file = await source.OpenAsync(VfsPath.Join(chain, "new.txt"), VfsOpenMode.CreateNew, CancellationToken.None))
        {
            await file.WriteAsync(0, "abc"u8.ToArray(), CancellationToken.None);
            Assert.AreEqual(3, asked.Requests[^1].Bytes);
        }

        CollectionAssert.AreEqual(new[] { check, "Get metadata " + written, "Write file " + written }, Since());

        var moved = Path.Combine(a, "moved.txt");
        await source.RenameAsync(VfsPath.Join(chain, "new.txt"), VfsPath.Join(chain, "moved.txt"), replace: false, CancellationToken.None);
        var since = Since();
        StringAssert.Contains(since[^1], $"Rename {written} to {moved}");
        Assert.IsTrue(since.Take(since.Count - 1).All(s => s == check || s.StartsWith("Get metadata ", StringComparison.Ordinal)), string.Join("; ", since));

        await source.SetTimesAsync(VfsPath.Join(chain, "moved.txt"), null, DateTimeOffset.UnixEpoch, CancellationToken.None);
        Assert.AreEqual("Set times " + moved, Since()[^1]);

        await source.DeleteAsync(VfsPath.Join(chain, "moved.txt"), CancellationToken.None);
        Assert.AreEqual("Delete " + moved, Since()[^1]);

        var made = Path.Combine(a, "made");
        await source.CreateDirectoryAsync(VfsPath.Join(chain, "made"), CancellationToken.None);
        Assert.AreEqual("Create folder " + made, Since()[^1]);

        await source.DeleteAsync(VfsPath.Join(chain, "made"), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { check, "Get metadata " + made, "List folder " + made, "Delete " + made }, Since(), "whether a folder is empty is asked of it");

        Assert.IsNotNull(await source.GetSpaceAsync(chain, CancellationToken.None));
        Assert.AreEqual("Get space " + a, Since()[^1]);

        Assert.AreEqual(0, asked.Running.Count, string.Join("; ", asked.Running));
    }

    [TestMethod]
    public async Task WhatTheFileSystemRefusesIsAFailedRow()
    {
        var a = Path.Combine(_root, "A");
        Directory.CreateDirectory(a);
        using var context = new TestSourceContext("mirror");
        var source = await StartAsync(a, context);
        await using var _s = source;
        var chain = Chain(a);
        await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync(VfsPath.Join(chain, "nothing.txt"), VfsOpenMode.Read, CancellationToken.None));
        var open = context.UpstreamRequests.Requests.Single(r => r.What == "Read file");
        Assert.AreEqual(Path.Combine(a, "nothing.txt"), open.Target);
        Assert.IsInstanceOfType<IOException>(open.Error);
        Assert.IsTrue(open.IsDone);
    }

    [TestMethod]
    public async Task ThePathsAreCheckedAsTheyAlwaysWereAndEachCheckIsARow()
    {
        var a = Path.Combine(_root, "A");
        var missing = Path.Combine(_root, "missing");
        Directory.CreateDirectory(a);
        using var context = new TestSourceContext("mirror");
        var source = await StartAsync(a + "\n" + missing, context);
        await using var _s = source;
        CollectionAssert.AreEquivalent(new[] { "Check path " + a, "Check path " + missing }, context.UpstreamRequests.Requests.Select(r => r.ToString()).ToList());
        var before = context.UpstreamRequests.Requests.Count;
        source.CheckRoots();
        Assert.AreEqual(before + 2, context.UpstreamRequests.Requests.Count, "of its own accord, as well as for a client");
    }

    [TestMethod]
    public async Task ACountAsksTheFileSystemForEachFolder()
    {
        var a = Path.Combine(_root, "A");
        Directory.CreateDirectory(Path.Combine(a, "sub"));
        File.WriteAllText(Path.Combine(a, "x.txt"), "x");
        File.WriteAllText(Path.Combine(a, "sub", "y.txt"), "y");
        File.WriteAllText(Path.Combine(a, "sub", "z.txt"), "z");
        using var context = new TestSourceContext("mirror");
        var source = await StartAsync(a, context, threads: 2, pathsInParallel: 1);
        await using var _s = source;
        var before = context.UpstreamRequests.Requests.Count;
        await ((ICountable)source).CountFilesAsync(CancellationToken.None);
        var asked = context.UpstreamRequests.Requests.Skip(before).Where(r => r.What == "List folder").ToDictionary(r => r.Target);
        Assert.AreEqual(2, asked.Count);
        Assert.AreEqual(2, asked[a].Entries);
        Assert.AreEqual(2, asked[Path.Combine(a, "sub")].Entries);
        Assert.AreEqual(0, context.UpstreamRequests.Running.Count);
    }

    [TestMethod]
    public async Task CountFilesShowsEachFolderItGoesThrough()
    {
        var a = Path.Combine(_root, "A");
        Directory.CreateDirectory(Path.Combine(a, "sub"));
        File.WriteAllText(Path.Combine(a, "x.txt"), "x");
        File.WriteAllText(Path.Combine(a, "sub", "y.txt"), "y");
        var source = await StartAsync(a, threads: 1, pathsInParallel: 1);
        var chain = "";
        source.CountListing = (root, _) => chain = root.ChainPath;
        var watcher = new RecordingFolderWatcher();
        source.Counters.FolderWatcher = watcher;
        await ((ICountable)source).CountFilesAsync(CancellationToken.None);
        var top = chain.Length == 0 ? "(root)" : chain;
        CollectionAssert.AreEqual(new[] { $"in {top}", $"listed {top} 2", $"done {top}", $"in {VfsPath.Join(chain, "sub")}", $"listed {VfsPath.Join(chain, "sub")} 1", $"done {VfsPath.Join(chain, "sub")}" }, watcher.Lines.ToList(), string.Join("; ", watcher.Lines));
    }

    [TestMethod]
    public async Task CountFilesCountsAPathInsideAnotherOnceAndSkipsAnUnreachableOne()
    {
        var a = Path.Combine(_root, "A");
        Directory.CreateDirectory(Path.Combine(a, "sub", "deep"));
        File.WriteAllText(Path.Combine(a, "x.txt"), "x");
        File.WriteAllText(Path.Combine(a, "sub", "y.txt"), "y");
        File.WriteAllText(Path.Combine(a, "sub", "deep", "z.txt"), "z");
        var missing = Path.Combine(_root, "missing");

        var source = await StartAsync(a + "\n" + Path.Combine(a, "sub") + "\n" + missing);
        Assert.AreEqual(1, source.Unreachable.Count);
        await ((ICountable)source).CountFilesAsync(CancellationToken.None);
        var (files, directories) = await WalkAsync(source);
        Assert.AreEqual(3, files);
        Assert.AreEqual(files, source.Counters.FilesKnown);
        Assert.AreEqual(directories, source.Counters.DirectoriesKnown);
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(3, 2)]
    [DataRow(8, 8)]
    public async Task CountFilesAgreesWithAWalkWhateverTheThreadsAndPaths(int threads, int pathsInParallel)
    {
        // three paths, each 4 folders wide and 3 deep with two files in every folder: 2 x (4 + 16 + 64) files a path
        var paths = new List<string>();
        foreach (var name in new[] { "A", "B", "C" })
        {
            var path = Path.Combine(_root, name);
            paths.Add(path);
            MakeTree(path, depth: 3);
        }

        var source = await StartAsync(string.Join('\n', paths), threads: threads, pathsInParallel: pathsInParallel);
        await ((ICountable)source).CountFilesAsync(CancellationToken.None);
        var (files, directories) = await WalkAsync(source);
        Assert.AreEqual(3 * 2 * (4 + 16 + 64), files);
        Assert.AreEqual(files, source.Counters.FilesKnown);
        Assert.AreEqual(directories, source.Counters.DirectoriesKnown);

        static void MakeTree(string folder, int depth)
        {
            Directory.CreateDirectory(folder);
            if (depth == 0)
            {
                return;
            }

            for (var i = 0; i < 4; i++)
            {
                var sub = Path.Combine(folder, $"d{i}");
                Directory.CreateDirectory(sub);
                File.WriteAllText(Path.Combine(sub, "one.txt"), "1");
                File.WriteAllText(Path.Combine(sub, "two.txt"), "2");
                MakeTree(sub, depth - 1);
            }
        }
    }

    [TestMethod]
    public async Task CountFilesListsAsManyFoldersOfAPathAndAsManyPathsAtOnceAsTheSettingsSay()
    {
        // four paths of ten folders each, every listing held a moment so that the counting threads overlap
        var paths = new List<string>();
        foreach (var name in new[] { "A", "B", "C", "D" })
        {
            var path = Path.Combine(_root, name);
            paths.Add(path);
            for (var i = 0; i < 10; i++)
            {
                Directory.CreateDirectory(Path.Combine(path, $"d{i}"));
            }
        }

        var source = await StartAsync(string.Join('\n', paths), threads: 3, pathsInParallel: 2);
        var gate = new object();
        var listing = new Dictionary<string, int>();
        int mostOfOnePath = 0, mostPaths = 0;
        source.CountListing = (root, _) =>
        {
            lock (gate)
            {
                listing[root.Original] = listing.GetValueOrDefault(root.Original) + 1;
                mostOfOnePath = Math.Max(mostOfOnePath, listing[root.Original]);
                mostPaths = Math.Max(mostPaths, listing.Count(p => p.Value > 0));
            }

            Thread.Sleep(60);
            lock (gate)
            {
                listing[root.Original]--;
            }
        };

        await ((ICountable)source).CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(3, mostOfOnePath, "three folders of one path listed at once");
        Assert.AreEqual(2, mostPaths, "two paths counted at once");
        var (_, directories) = await WalkAsync(source);
        Assert.AreEqual(directories, source.Counters.DirectoriesKnown, "every folder counted once");
    }

    /// <summary>What the host's generic count would find: every listing, directory by directory.</summary>
    private static async Task<(long Files, long Directories)> WalkAsync(ISource source)
    {
        long files = 0, directories = 0;
        var queue = new Queue<string>();
        queue.Enqueue("");
        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            try
            {
                await foreach (var entry in source.ListAsync(dir, CancellationToken.None))
                {
                    if (entry.IsDirectory)
                    {
                        directories++;
                        queue.Enqueue(VfsPath.Join(dir, entry.Name));
                    }
                    else
                    {
                        files++;
                    }
                }
            }
            catch (VfsException)
            {
            }
        }

        return (files, directories);
    }
}

/// <summary>The SDK contract suite against a real temp tree.</summary>
[TestClass]
public class MirrorContractTests : SourceContractTests
{
    private string _root = "";

    protected override async Task<SourceContractSetup> SetUpAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "mirror-contract-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(folder, "docs"));
        Directory.CreateDirectory(Path.Combine(folder, "scratch"));
        var contents = Encoding.ASCII.GetBytes("mirror contract file contents, long enough for ranges");
        File.WriteAllBytes(Path.Combine(folder, "docs", "contract.txt"), contents);
        var source = new MirrorPlugin().CreateSource(new MirrorSettings { Paths = folder });
        await source.InitialiseAsync(new TestSourceContext("mirror"), CancellationToken.None);
        Assert.IsTrue(MirrorRoot.TryParse(folder, OperatingSystem.IsWindows(), out var root, out _));
        var chain = root.ChainPath;
        return new SourceContractSetup(source, chain + "/docs", chain + "/docs/contract.txt", contents, WritableDirectory: chain + "/scratch");
    }

    [TestCleanup]
    public void CleanupTree()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
        }
    }
}
