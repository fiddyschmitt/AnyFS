using AnyFS.Core.Model;
using AnyFS.Core.Plugins;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Core.Stats;
using AnyFS.Sdk.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Core.Tests;

/// <summary>
/// What goes wrong with the settings files as they are loaded is kept for the person to be told, and costs them nothing
/// they had not lost (the review of 21-Nov-2026, R7g and N23). It was in the log only.
/// </summary>
[TestClass]
public class SettingsNoticesTests
{
    private string _root = "";
    private SettingsPaths _paths = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "notices-" + Guid.NewGuid().ToString("N"));
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

    private SettingsStore Loaded()
    {
        var store = new SettingsStore(_paths, NullLogger.Instance);
        store.Load();
        return store;
    }

    /// <summary>The files a run leaves: two collections, a source in one of them, and stats for both.</summary>
    private (Guid Work, Guid Alpha) AnEarlierRun()
    {
        var store = Loaded();
        var alpha = new SourceDefinition { Name = "alpha", Type = "memory" };
        store.Sources.Add(alpha);
        var work = new CollectionDefinition { Name = "work" };
        work.Sources.Add(new SourceMembership { Id = alpha.Id, Path = "mem/alpha" });
        store.Collections.Add(work);
        store.Collections.Add(new CollectionDefinition { Name = "home" });
        store.SaveSources();
        store.SaveCollections();
        var stats = new StatsCollector();
        stats.FileServed("work");
        stats.FileServed("home");
        JsonFiles.WriteAtomic(_paths.StatsFile, stats.ToFile(), CoreJsonContext.Default.StatsFile);
        return (work.Id, alpha.Id);
    }

    [TestMethod]
    public void AFreshFolderHasNothingToSay()
    {
        var store = Loaded();
        Assert.AreEqual(0, store.Notices.Count);
        Assert.IsTrue(store.FirstStart);
        Assert.IsFalse(store.CollectionsSetAside);
    }

    [TestMethod]
    public void CollectionsThatWillNotParseAreSetAsideAndSaidAndNothingIsPutInTheirPlace()
    {
        // It gave "Collection 1" in their place, written where the file goes back, and their stats were dropped
        AnEarlierRun();

        // a comma too many, as an edit by hand leaves
        File.WriteAllText(_paths.CollectionsFile, File.ReadAllText(_paths.CollectionsFile).Replace("\"collections\": [", "\"collections\": [,"));
        var registry = new PluginRegistry();
        registry.AddBuiltIn(new MemoryPlugin());
        registry.AddBuiltIn(new FakeMountProvider());
        var engine = new AnyFsEngine(_paths, registry, NullLoggerFactory.Instance);
        engine.Load();

        var aside = Directory.GetFiles(_paths.Settings, "collections.json.bad-*").Single();
        var notice = engine.Settings.Notices.Single();
        StringAssert.Contains(notice, "collections.json could not be read");
        StringAssert.Contains(notice, Path.GetFileName(aside), "where it is now");
        StringAssert.Contains(notice, "LineNumber", "and what is wrong with it, for whoever edited it");
        Assert.IsTrue(engine.Settings.CollectionsSetAside);
        Assert.AreEqual(0, engine.Settings.Collections.Count, "no Collection 1 in their place");
        Assert.IsFalse(File.Exists(_paths.CollectionsFile), "nothing is written where the file goes back");
        var kept = engine.Stats.ToFile().Collections;
        Assert.IsTrue(kept.ContainsKey("work") && kept.ContainsKey("home"), "their stats wait for them");
        Assert.AreEqual("alpha", engine.Settings.Sources.Single().Name, "the sources, in a file of their own, are as they were");

        // the file put right and given its name again, all is as it was
        File.WriteAllText(_paths.CollectionsFile, File.ReadAllText(aside).Replace("\"collections\": [,", "\"collections\": ["));
        var again = Loaded();
        CollectionAssert.AreEquivalent(new[] { "work", "home" }, again.Collections.Select(c => c.Name).ToList());
        Assert.AreEqual(0, again.Notices.Count);
    }

    [TestMethod]
    public void ASourceNamedTwiceInACollectionKeepsItsFirstPlace()
    {
        // N23: the window's lists are kept by the source's id, and with two of one id every refresh of them threw
        var (work, alpha) = AnEarlierRun();
        var edited = Loaded();
        edited.Collections.Single(c => c.Id == work).Sources.Add(new SourceMembership { Id = alpha, Path = "again" });
        edited.SaveCollections();

        var store = Loaded();
        Assert.AreEqual("mem/alpha", store.Collections.Single(c => c.Id == work).Sources.Single().Path);
        var notice = store.Notices.Single();
        StringAssert.Contains(notice, "'alpha' twice in the collection 'work'");
        StringAssert.Contains(notice, "'again'");
    }

    [TestMethod]
    public void TwoSourcesWithOneIdAreSaid()
    {
        AnEarlierRun();
        var edited = Loaded();
        edited.Sources.Add(new SourceDefinition { Id = edited.Sources.Single().Id, Name = "beta", Type = "memory" });
        edited.SaveSources();

        var store = Loaded();
        Assert.AreEqual("alpha", store.Sources.Single().Name);
        StringAssert.Contains(store.Notices.Single(), "'alpha' and 'beta'");
    }

    [TestMethod]
    public async Task AFileAnotherProgramHoldsForAMomentIsWaitedFor()
    {
        // an editor saving it, an antivirus scanning it: at a start that was no window at all
        AnEarlierRun();
        var held = new FileStream(_paths.SourcesFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var letGo = Task.Run(async () =>
        {
            await Task.Delay(150);
            held.Dispose();
        });
        var store = Loaded();
        await letGo;
        Assert.AreEqual("alpha", store.Sources.Single().Name);
        Assert.AreEqual(0, store.Notices.Count);
    }

    [TestMethod]
    public void AFileStillHeldIsAFailureAndNothingIsSetAside()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("a file held against its readers is Windows' way");
        }

        AnEarlierRun();
        using var held = new FileStream(_paths.SourcesFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var store = new SettingsStore(_paths, NullLogger.Instance);
        var failure = Assert.ThrowsExactly<IOException>(store.Load);
        StringAssert.Contains(failure.Message, "sources.json", "the failure names the file: it is what the person is shown");
        Assert.AreEqual(0, Directory.GetFiles(_paths.Settings, "*.bad-*").Length, "a file that could not be opened is not one that would not parse");
    }
}
