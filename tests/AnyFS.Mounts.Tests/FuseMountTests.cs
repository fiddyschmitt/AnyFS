using System.Text;
using AnyFS.Mounts.Fuse;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;

namespace AnyFS.Mounts.Tests;

/// <summary>Spec 8.3 and 16.1: the FUSE provider through libfuse3 where it exists (Linux with the fuse3 package); settings everywhere.</summary>
[TestClass]
public class FuseMountTests
{
    private readonly List<IAsyncDisposable> _cleanup = [];

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var d in Enumerable.Reverse(_cleanup))
        {
            await d.DisposeAsync();
        }
    }

    private sealed record Collection(string Name) : ICollectionInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    [TestMethod]
    public void SettingsDefaultUnderHomeAndValidate()
    {
        var provider = new FuseMountProvider();
        var defaults = (FuseMountSettings)provider.CreateDefaultSettings(new Collection("work"));
        Assert.AreEqual("~/anyfs/work", defaults.MountPoint);
        Assert.IsTrue(defaults.AllowOther);
        Assert.AreEqual(0, provider.Validate(defaults).Count());
        Assert.IsTrue(provider.Validate(new FuseMountSettings { MountPoint = "" }).Any(m => m.IsError));
        Assert.IsTrue(provider.Validate(new FuseMountSettings { MountPoint = "relative/path" }).Any(m => m.IsError));
        Assert.AreEqual(0, provider.Validate(new FuseMountSettings { MountPoint = "/mnt/work" }).Count());

        var home = (Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).TrimEnd('/');
        Assert.AreEqual(home + "/anyfs/work", FuseMountProvider.ExpandMountPoint("~/anyfs/work/"));
        Assert.AreEqual("/mnt/work", FuseMountProvider.ExpandMountPoint(" /mnt/work "));
        if (!OperatingSystem.IsLinux())
        {
            StringAssert.Contains(provider.UnavailableReason!, "Linux");
        }
    }

    [TestMethod]
    public async Task MountsListsReadsAndWritesOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("FUSE is Linux only");
        }

        var provider = new FuseMountProvider();
        if (provider.UnavailableReason is { } reason)
        {
            Assert.Inconclusive(reason);
        }

        var name = "anyfs-test-" + Guid.NewGuid().ToString("N")[..8];
        var source = new MemorySource().AddFile("readme.txt", "hello readme").AddFile("docs/a.txt", "aaa").AddDirectory("emptydir");
        await source.InitialiseAsync(new TestSourceContext(name), CancellationToken.None);
        var settings = new FuseMountSettings { MountPoint = Path.Combine(Path.GetTempPath(), name), AllowOther = false };
        var mount = provider.CreateMount(settings, new Collection(name));
        await mount.StartAsync(new SingleSourceVfs(source, name), new TestMountContext(name, "fuse"), CancellationToken.None);
        _cleanup.Add(mount);
        var root = mount.AccessPaths.Single().Value;
        Assert.AreEqual(settings.MountPoint, root);

        CollectionAssert.AreEquivalent(new[] { "readme.txt", "docs", "emptydir" }, Directory.GetFileSystemEntries(root).Select(Path.GetFileName).ToList());
        Assert.AreEqual("hello readme", File.ReadAllText(Path.Combine(root, "readme.txt")));
        File.WriteAllText(Path.Combine(root, "new.txt"), "written");
        Assert.AreEqual("written", Encoding.UTF8.GetString(source.GetFile("new.txt")!));
        Directory.CreateDirectory(Path.Combine(root, "made"));
        File.Move(Path.Combine(root, "new.txt"), Path.Combine(root, "made", "moved.txt"));
        Assert.IsTrue(source.Exists("made/moved.txt"));
        File.Delete(Path.Combine(root, "made", "moved.txt"));
        Directory.Delete(Path.Combine(root, "made"));
        Assert.IsFalse(source.Exists("made"));

        await mount.StopAsync(CancellationToken.None);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(root).Length, "unmounted: the empty folder is back");
    }
}
