using System.Diagnostics;
using System.Reflection;
using System.Text;
using AnyFS.Mounts.WinFsp;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using Microsoft.Extensions.Logging;

namespace AnyFS.Mounts.Tests;

/// <summary>Spec 8.1 and 16.1: the WinFsp provider through the real driver at \\anyfs\&lt;name&gt;, self-skipping where WinFsp isn't installed.</summary>
[TestClass]
public class WinFspMountTests
{
    private static readonly byte[] Medium = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 31 % 253)).ToArray();

    // longer than two read-ahead fills (1 MiB each), with a tail that is not a whole fill
    private static readonly byte[] Big = Enumerable.Range(0, 2_500_000).Select(i => (byte)((i * 7 + i / 1000) % 251)).ToArray();
    private readonly List<IAsyncDisposable> _cleanup = [];

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var d in Enumerable.Reverse(_cleanup))
        {
            await d.DisposeAsync();
        }
    }

    // case-insensitive like a Windows source; the real router also folds case when a source doesn't
    private static MemorySource Tree() => new MemorySource(caseInsensitive: true)
        .AddFile("readme.txt", "hello readme")
        .AddFile("docs/a.txt", "aaa")
        .AddFile("docs/sub/c.txt", "c")
        .AddFile("medium.bin", Medium, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero))
        .AddFile("big.bin", Big)
        .AddFile("unicode/ünïcödé ✓ 日本語.txt", "unicode names")
        .AddDirectory("emptydir");

    private sealed record Collection(string Name) : ICollectionInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private static void RequireWinFsp()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("WinFsp is a Windows driver");
        }

        if (new WinFspMountProvider().UnavailableReason is { } reason)
        {
            Assert.Inconclusive(reason);
        }
    }

    private async Task<(IMount Mount, string Unc, MemorySource Source)> MountAsync(bool readOnly = false)
    {
        var name = "anyfs-test-" + Guid.NewGuid().ToString("N")[..8];
        var source = Tree();
        await source.InitialiseAsync(new TestSourceContext(name), CancellationToken.None);
        var provider = new WinFspMountProvider();
        var settings = (WinFspMountSettings)provider.CreateDefaultSettings(new Collection(name));
        var mount = provider.CreateMount(settings, new Collection(name));
        var context = new TestMountContext(name, "winfsp", logger: new ConsoleLogger());
        await mount.StartAsync(new SingleSourceVfs(source, name, readOnly), context, CancellationToken.None);
        _cleanup.Add(mount);
        var unc = $@"\\anyfs\{name}";
        Assert.AreEqual(unc, mount.AccessPaths.Single().Value, "no drive letter: the UNC path is the only access path");
        Assert.IsInstanceOfType<MountOpenAction.FileManager>(mount.OpenAction);
        return (mount, unc, source);
    }

    [TestMethod]
    public async Task ListsReadsAndWritesThroughTheUncPath()
    {
        RequireWinFsp();
        var (_, unc, source) = await MountAsync();

        var names = Directory.GetFileSystemEntries(unc).Select(Path.GetFileName).ToList();
        CollectionAssert.AreEquivalent(new[] { "readme.txt", "docs", "medium.bin", "big.bin", "unicode", "emptydir" }, names);
        Assert.AreEqual("hello readme", File.ReadAllText(Path.Combine(unc, "readme.txt")));
        Assert.AreEqual("unicode names", File.ReadAllText(Path.Combine(unc, "unicode", "ünïcödé ✓ 日本語.txt")));
        Assert.AreEqual(100_000, new FileInfo(Path.Combine(unc, "medium.bin")).Length);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(Path.Combine(unc, "emptydir")).Length);

        using (var stream = new FileStream(Path.Combine(unc, "medium.bin"), FileMode.Open, FileAccess.Read))
        {
            stream.Seek(50_000, SeekOrigin.Begin);
            var window = new byte[1000];
            stream.ReadExactly(window);
            CollectionAssert.AreEqual(Medium[50_000..51_000], window, "a ranged read lands where it should");
        }

        CollectionAssert.AreEqual(Medium, File.ReadAllBytes(Path.Combine(unc, "medium.bin")));

        File.WriteAllText(Path.Combine(unc, "new.txt"), "written");
        Assert.AreEqual("written", Encoding.UTF8.GetString(source.GetFile("new.txt")!), "a write lands upstream");
        Directory.CreateDirectory(Path.Combine(unc, "made"));
        Assert.IsTrue(source.Exists("made"));
        File.Move(Path.Combine(unc, "new.txt"), Path.Combine(unc, "made", "moved.txt"));
        Assert.IsTrue(source.Exists("made/moved.txt"));
        Assert.IsFalse(source.Exists("new.txt"));
        File.AppendAllText(Path.Combine(unc, "made", "moved.txt"), " more");
        Assert.AreEqual("written more", Encoding.UTF8.GetString(source.GetFile("made/moved.txt")!));
        File.Delete(Path.Combine(unc, "made", "moved.txt"));
        Assert.IsFalse(source.Exists("made/moved.txt"));
        Directory.Delete(Path.Combine(unc, "made"));
        Assert.IsFalse(source.Exists("made"));
        Assert.ThrowsExactly<IOException>(() => Directory.Delete(Path.Combine(unc, "docs")), "a non-empty folder stays");
        Assert.IsTrue(source.Exists("docs/a.txt"));
    }

    [TestMethod]
    public async Task ReadOnlyMountRefusesWritesAndShowsTheAttribute()
    {
        RequireWinFsp();
        var (_, unc, source) = await MountAsync(readOnly: true);
        Assert.IsTrue(File.GetAttributes(Path.Combine(unc, "readme.txt")).HasFlag(FileAttributes.ReadOnly));
        Assert.AreEqual("hello readme", File.ReadAllText(Path.Combine(unc, "readme.txt")), "reads are fine");
        try
        {
            File.WriteAllText(Path.Combine(unc, "new.txt"), "x");
            Assert.Fail("a write on a read-only mount must fail");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            Directory.CreateDirectory(Path.Combine(unc, "made"));
            Assert.Fail("a folder on a read-only mount must fail");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        Assert.IsFalse(source.Exists("new.txt"));
        Assert.IsFalse(source.Exists("made"));
    }

    /// <summary>
    /// Small sequential reads are served from a per-handle read-ahead (WinFsp hands each application read to user
    /// mode as it is): the bytes must be right across the fills and at the tail, a seek must not be served from the
    /// wrong place, and a write through another handle must show to a fresh reader.
    /// </summary>
    [TestMethod]
    public async Task SmallSequentialReadsGoThroughTheReadAhead()
    {
        RequireWinFsp();
        var (_, unc, _) = await MountAsync();
        var path = Path.Combine(unc, "big.bin");

        // 1000-byte reads straight to the file system (no FileStream buffer): across two 1 MiB fills and a short tail
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1))
        {
            var all = new byte[Big.Length];
            var chunk = new byte[1000];
            var total = 0;
            int n;
            while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                Array.Copy(chunk, 0, all, total, n);
                total += n;
            }

            Assert.AreEqual(Big.Length, total, "the whole file arrives");
            CollectionAssert.AreEqual(Big, all, "sequential 1000-byte reads");
        }

        // seeks: back inside what was read ahead, far forward, back again, and across the end
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1))
        {
            var window = new byte[4096];
            stream.ReadExactly(window);
            stream.ReadExactly(window); // the second sequential read fills the read-ahead from 4096
            CollectionAssert.AreEqual(Big[4096..8192], window);
            stream.Seek(2_000_000, SeekOrigin.Begin);
            stream.ReadExactly(window);
            CollectionAssert.AreEqual(Big[2_000_000..2_004_096], window, "a forward seek");
            stream.Seek(5000, SeekOrigin.Begin);
            stream.ReadExactly(window);
            CollectionAssert.AreEqual(Big[5000..9096], window, "a seek back into the first fill");
            stream.Seek(-100, SeekOrigin.End);
            Assert.AreEqual(100, stream.Read(window, 0, window.Length), "a read across the end is short");
            CollectionAssert.AreEqual(Big[^100..], window[..100]);
            Assert.AreEqual(0, stream.Read(window, 0, window.Length), "and then the end");
        }

        // a write through one handle, then a fresh handle reads the region sequentially in small pieces
        var patch = Enumerable.Range(0, 3000).Select(i => (byte)(255 - (i % 256))).ToArray();
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 1))
        {
            writer.Seek(1_500_000, SeekOrigin.Begin);
            writer.Write(patch);
            writer.Flush();
        }

        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1))
        {
            reader.Seek(1_499_000, SeekOrigin.Begin);
            var got = new byte[5000];
            for (var done = 0; done < got.Length; done += 500)
            {
                reader.ReadExactly(got, done, 500);
            }

            CollectionAssert.AreEqual(Big[1_499_000..1_500_000], got[..1000], "before the patch");
            CollectionAssert.AreEqual(patch, got[1000..4000], "the patch");
            CollectionAssert.AreEqual(Big[1_503_000..1_504_000], got[4000..], "after the patch");
        }
    }

    [TestMethod]
    public async Task UnmountRemovesTheVolume()
    {
        RequireWinFsp();
        var (mount, unc, _) = await MountAsync();
        Assert.IsTrue(Directory.Exists(unc));
        await mount.StopAsync(CancellationToken.None);
        Assert.IsFalse(Directory.Exists(unc), "the prefix is gone once the mount stops");
        Assert.AreEqual(0, mount.AccessPaths.Count);
    }

    [TestMethod]
    public void SettingsAndDriveLetters()
    {
        Assert.AreEqual("X", WinFspMountProvider.NormaliseLetter("x"));
        Assert.AreEqual("X", WinFspMountProvider.NormaliseLetter(" X: "));
        Assert.IsNull(WinFspMountProvider.NormaliseLetter("C"), "C is taken");
        Assert.IsNull(WinFspMountProvider.NormaliseLetter(""));
        Assert.IsNull(WinFspMountProvider.NormaliseLetter("XY"));
        var provider = new WinFspMountProvider();
        Assert.AreEqual(0, provider.Validate(new WinFspMountSettings()).Count());
        Assert.IsTrue(provider.Validate(new WinFspMountSettings { DriveLetter = "1" }).Any(m => m.IsError));
        Assert.AreEqual(OperatingSystem.IsWindows() && WinFspRuntime.IsInstalled() ? "Upgrade WinFsp driver" : "Install WinFsp driver", provider.InstallText);
        Assert.IsTrue(provider.InstallDetail.Contains("winget install") || provider.InstallDetail.Contains("bundled"), provider.InstallDetail);
        CollectionAssert.AreEqual(new[] { "/i", @"C:\t\winfsp.msi", "/qn", "/norestart", "/l*v", @"C:\t\winfsp.log" }, WinFspInstaller.Arguments(@"C:\t\winfsp.msi", @"C:\t\winfsp.log").ToList());
        StringAssert.Contains(WinFspInstaller.Describe(1602, installed: false), "cancelled");
        StringAssert.Contains(WinFspInstaller.Describe(1618, installed: false), "in progress");
        StringAssert.Contains(WinFspInstaller.Describe(1638, installed: true), "already installed");
        StringAssert.Contains(WinFspInstaller.Describe(0, installed: false), "still not found");
        StringAssert.Contains(WinFspInstaller.Describe(1234, installed: false), "1234");
    }

    /// <summary>Fsp.Interop.Api.CheckVersion's rule: the driver's major equals the layer's, its minor is at least the layer's.</summary>
    [TestMethod]
    public void DriverVersionRuleIsWinFspsOwn()
    {
        var required = new Version(2, 1, 25156);
        Assert.AreEqual(required, WinFspRuntime.Required);
        Assert.IsTrue(WinFspRuntime.Accepts(new Version(2, 1, 25156), required));
        Assert.IsTrue(WinFspRuntime.Accepts(new Version(2, 2, 26286), required), "a newer 2.x driver serves an older layer");
        Assert.IsFalse(WinFspRuntime.Accepts(new Version(2, 0, 23075), required), "an older minor is 'incorrect dll version'");
        Assert.IsFalse(WinFspRuntime.Accepts(new Version(1, 12, 22339), required));
        Assert.IsFalse(WinFspRuntime.Accepts(new Version(3, 0, 1), required));
        StringAssert.Contains(WinFspRuntime.Explain(new TypeLoadException("incorrect dll version (need 2.2, have 2.1)")), "need 2.2, have 2.1");
        StringAssert.Contains(WinFspRuntime.Explain(new TypeInitializationException("Fsp.Interop.Api", new DllNotFoundException("cannot load winfsp-x64.dll"))), "not installed");
        StringAssert.Contains(WinFspRuntime.Explain(new TargetInvocationException(new TypeInitializationException("Fsp.Interop.Api", new ArgumentException("The path is empty.", "path")))), "file version");
        StringAssert.Contains(WinFspRuntime.Explain(new FileNotFoundException("Could not load file or assembly 'winfsp-msil'")), "winfsp-msil");
        if (OperatingSystem.IsWindows() && WinFspRuntime.InstalledVersion() is { } installed)
        {
            Console.WriteLine($"installed WinFsp {installed}; problem: {WinFspRuntime.Problem() ?? "none"}");
            Assert.AreEqual(WinFspRuntime.Accepts(installed, required), WinFspRuntime.Problem() is null);
        }
    }

    /// <summary>The copy the plugin carries for single-file hosts is the layer this build links, and the installer constant follows it.</summary>
    [TestMethod]
    public void InteropCopyIsTheLayerThisBuildLinks()
    {
        Assert.IsTrue(WinFspInterop.HasEmbedded(), "AnyFS.Mounts.WinFsp embeds winfsp-msil.dll");
        var dir = Path.Combine(Path.GetTempPath(), "anyfs-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = WinFspInterop.Extract(dir);
            Assert.AreEqual(path, WinFspInterop.Extract(dir), "the same content lands in the same file");
            Assert.AreEqual(WinFspInterop.AssemblyName, AssemblyName.GetAssemblyName(path).Name);
            var copy = FileVersionInfo.GetVersionInfo(path);
            var copyVersion = new Version(copy.FileMajorPart, copy.FileMinorPart, copy.FileBuildPart);
            Assert.AreEqual(Version.Parse(WinFspInstaller.Version), copyVersion, "WinFspInstaller.Version follows the winfsp.net package in Directory.Packages.props");
            var linked = typeof(Fsp.FileSystemHost).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
            Assert.IsNotNull(linked);
            Assert.AreEqual(Version.Parse(linked), copyVersion, "the embedded copy is the package the tests link");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>One copy per version: an older build's layer and a half-written temporary file go when the current one lands.</summary>
    [TestMethod]
    public void OtherInteropCopiesAreRemoved()
    {
        Assert.IsTrue(WinFspInterop.HasEmbedded(), "AnyFS.Mounts.WinFsp embeds winfsp-msil.dll");
        var dir = Path.Combine(Path.GetTempPath(), "anyfs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stale = Path.Combine(dir, WinFspInterop.AssemblyName + "-000000000000.dll");
            var leftover = Path.Combine(dir, WinFspInterop.AssemblyName + "-000000000000.dll.deadbeef.tmp");
            var unrelated = Path.Combine(dir, "notes.txt");
            File.WriteAllText(stale, "an older build's layer");
            File.WriteAllText(leftover, "a write that never finished");
            File.WriteAllText(unrelated, "not ours");

            var path = WinFspInterop.Extract(dir);

            Assert.IsTrue(File.Exists(path));
            Assert.IsFalse(File.Exists(stale), "the older copy is removed");
            Assert.IsFalse(File.Exists(leftover), "the temporary file is removed");
            Assert.IsTrue(File.Exists(unrelated), "other files are not ours to delete");
            CollectionAssert.AreEquivalent(new[] { Path.GetFileName(path), "notes.txt" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The provider's log lines into the test output.</summary>
    private sealed class ConsoleLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Console.WriteLine($"[server {logLevel}] {formatter(state, exception)}{(exception is null ? "" : " " + exception.Message)}");
    }
}
