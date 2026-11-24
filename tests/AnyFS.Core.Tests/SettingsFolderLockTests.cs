using AnyFS.Core.Plugins;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Core.Tests;

/// <summary>One AnyFS at a time on a state folder (the review of 21-Nov-2026, R7f).</summary>
[TestClass]
public class SettingsFolderLockTests
{
    private string _root = "";
    private SettingsPaths _paths = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "lock-" + Guid.NewGuid().ToString("N"));
        _paths = SettingsPaths.Resolve(Path.Combine(_root, "settings"));
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

    [TestMethod]
    public void TheFolderIsOnesAtATimeAndFreeAgainWhenThatOneHasLetGo()
    {
        using (SettingsFolderLock.Take(_paths))
        {
            var refused = Assert.ThrowsExactly<SettingsInUseException>(() => SettingsFolderLock.Take(_paths));
            StringAssert.Contains(refused.Message, "already running");
            StringAssert.Contains(refused.Message, _paths.Settings);
        }

        using (SettingsFolderLock.Take(_paths))
        {
        }

        Assert.IsFalse(File.Exists(SettingsFolderLock.FileOf(_paths)), "nothing is left behind");
    }

    [TestMethod]
    public void TwoSettingsFoldersThatShareAStateFolderAreOne()
    {
        // with --settings-dir the state, the cache and the log are the settings folder's siblings: two settings folders
        // side by side have one secrets.json between them
        var beside = SettingsPaths.Resolve(Path.Combine(_root, "other-settings"));
        Assert.AreEqual(_paths.State, beside.State);
        using (SettingsFolderLock.Take(_paths))
        {
            Assert.ThrowsExactly<SettingsInUseException>(() => SettingsFolderLock.Take(beside));
        }
    }

    [TestMethod]
    public void TheRefusalSaysWhichAnyFsRuns()
    {
        using var held = SettingsFolderLock.Take(_paths);
        new AnyFsEngine(_paths, new PluginRegistry(), NullLoggerFactory.Instance).WriteStatus();
        var refused = Assert.ThrowsExactly<SettingsInUseException>(() => SettingsFolderLock.Take(_paths));
        StringAssert.Contains(refused.Message, $"process {Environment.ProcessId}");
    }
}
