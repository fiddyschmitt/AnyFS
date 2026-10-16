using AnyFS.Core.Settings;
using AnyFS.Sdk.Testing;

namespace AnyFS.Core.Tests;

/// <summary>The cache folder every mount of one provider shares (spec 9.1), in the host's paths and in the SDK's test context.</summary>
[TestClass]
public class ProviderCacheTests
{
    [TestMethod]
    public void TheFolderIsUnderCacheByProviderId()
    {
        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", Guid.NewGuid().ToString("N"));
        var paths = SettingsPaths.Resolve(Path.Combine(root, "settings"));

        Assert.AreEqual(Path.Combine(root, "cache", "mount-providers", "smb"), paths.MountProviderCache("smb"));

        var odd = paths.MountProviderCache(@"a/b\c:d*e");
        Assert.AreEqual(Path.Combine(root, "cache", "mount-providers"), Path.GetDirectoryName(odd), "a third-party id cannot escape the folder");
        Assert.IsFalse(Path.GetFileName(odd).Any(c => c is '/' or '\\' || Path.GetInvalidFileNameChars().Contains(c)));
        Assert.AreEqual("_", Path.GetFileName(paths.MountProviderCache("...")), "an id that is only dots gets a stand-in name");
    }

    [TestMethod]
    public void TheTestContextSharesItWithTheContextsItIsBuiltFrom()
    {
        using var first = new TestMountContext("a", "m1");
        using var second = new TestMountContext("b", "m2", shareWith: first);
        using var other = new TestMountContext("c", "m3");

        Assert.AreEqual(first.ProviderCacheDirectory, second.ProviderCacheDirectory, "mounts of one provider share the folder");
        Assert.AreNotEqual(first.StateDirectory, second.StateDirectory, "state stays per mount");
        Assert.AreNotEqual(first.ProviderCacheDirectory, other.ProviderCacheDirectory);
        Assert.IsTrue(Directory.Exists(first.ProviderCacheDirectory));
    }
}
