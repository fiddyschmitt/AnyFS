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

    private static async Task<MirrorSource> StartAsync(string paths, TestSourceContext? context = null, int interval = 60)
    {
        var source = (MirrorSource)new MirrorPlugin().CreateSource(new MirrorSettings { Paths = paths, CheckIntervalSeconds = interval });
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
        Assert.IsTrue(space is null || space.TotalBytes > 0);
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
