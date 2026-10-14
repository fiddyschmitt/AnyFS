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
        CollectionAssert.AreEquivalent(new[] { "readme.txt", "docs", "medium.bin", "unicode", "emptydir" }, names);
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
        Assert.AreEqual("Install WinFsp driver", provider.InstallText);
        Assert.IsTrue(provider.InstallDetail.Contains("winget install") || provider.InstallDetail.Contains("bundled"), provider.InstallDetail);
        CollectionAssert.AreEqual(new[] { "/i", @"C:\t\winfsp.msi", "/qn", "/norestart", "/l*v", @"C:\t\winfsp.log" }, WinFspInstaller.Arguments(@"C:\t\winfsp.msi", @"C:\t\winfsp.log").ToList());
        StringAssert.Contains(WinFspInstaller.Describe(1602, installed: false), "cancelled");
        StringAssert.Contains(WinFspInstaller.Describe(1618, installed: false), "in progress");
        StringAssert.Contains(WinFspInstaller.Describe(0, installed: false), "still not found");
        StringAssert.Contains(WinFspInstaller.Describe(1234, installed: false), "1234");
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
