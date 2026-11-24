using AnyFS.App.Gui;
using AnyFS.App.Gui.ViewModels;
using AnyFS.App.Gui.Views;
using AnyFS.Core.Model;
using AnyFS.Core.Plugins;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Sdk.Testing;
using Avalonia.Headless;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.App.Tests;

/// <summary>What went wrong with the settings files as AnyFS started is said in the window (the review of 21-Nov-2026, R7g and N23).</summary>
[TestClass]
public class StartupNoticesTests
{
    private string _root = "";
    private SettingsPaths _paths = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "notices-gui-" + Guid.NewGuid().ToString("N"));
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

    private AnyFsEngine Engine()
    {
        var registry = new PluginRegistry();
        registry.AddBuiltIn(new MemoryPlugin());
        registry.AddBuiltIn(new FakeMountProvider());
        var engine = new AnyFsEngine(_paths, registry, NullLoggerFactory.Instance);
        engine.Load();
        return engine;
    }

    [TestMethod]
    public async Task AFileThatWasSetAsideIsSaidOnceAsTheWindowOpens()
    {
        Directory.CreateDirectory(_paths.Settings);
        File.WriteAllText(_paths.SourcesFile, "{ \"version\": 1, \"sources\": [ { \"name\": \"alpha\" \"type\": \"memory\" } ] }");
        await using var engine = Engine();
        var dialogs = new FakeDialogs();
        var window = new MainViewModel(engine, dialogs, new FakeOpener(), new FakeClipboard());

        await window.SayStartupNoticesAsync();
        await window.SayStartupNoticesAsync();
        var (title, message) = dialogs.Messages.Single();
        Assert.AreEqual("Settings", title);
        StringAssert.Contains(message, "sources.json could not be read");
        StringAssert.Contains(message, "sources.json.bad-");
    }

    [TestMethod]
    public async Task WithNothingWrongNothingIsSaid()
    {
        await using var engine = Engine();
        var dialogs = new FakeDialogs();
        await new MainViewModel(engine, dialogs, new FakeOpener(), new FakeClipboard()).SayStartupNoticesAsync();
        Assert.AreEqual(0, dialogs.Messages.Count);
    }

    [TestMethod]
    public async Task ASourceNamedTwiceInACollectionShowsOnceAndTheWindowGoesOnRefreshing()
    {
        // N23: every refresh of the collection's sources threw, "An item with the same key has already been added"
        var store = new SettingsStore(_paths, NullLogger.Instance);
        store.Load();
        var alpha = new SourceDefinition { Name = "alpha", Type = "memory" };
        store.Sources.Add(alpha);
        var work = new CollectionDefinition { Name = "work" };
        work.Sources.Add(new SourceMembership { Id = alpha.Id, Path = "mem/alpha" });
        work.Sources.Add(new SourceMembership { Id = alpha.Id, Path = "again" });
        store.Collections.Add(work);
        store.SaveSources();
        store.SaveCollections();

        await using var engine = Engine();
        await engine.StartAsync(null, CancellationToken.None);
        var dialogs = new FakeDialogs();
        var window = new MainViewModel(engine, dialogs, new FakeOpener(), new FakeClipboard());
        for (var i = 0; i < 3; i++)
        {
            window.Refresh();
            window.Collections.SelectedCollection = window.Collections.Collections.Single(c => c.Name == "work");
        }

        window.Refresh();
        Assert.AreEqual("alpha", window.Collections.Sources.Single().Name);
        await window.SayStartupNoticesAsync();
        StringAssert.Contains(dialogs.Messages.Single().Message, "'alpha' twice in the collection 'work'");
    }
    [TestMethod]
    public async Task TheWindowSaysItOverItselfAsItOpens()
    {
        Directory.CreateDirectory(_paths.Settings);
        File.WriteAllText(_paths.SourcesFile, "{ \"version\": 1, \"sources\": [ { \"name\": \"alpha\" \"type\": \"memory\" } ] }");
        await HeadlessUnitTestSession.GetOrStartForAssembly(typeof(StartupNoticesTests).Assembly).Dispatch(async () =>
        {
            var registry = new PluginRegistry();
            registry.AddBuiltIn(new MemoryPlugin());
            registry.AddBuiltIn(new FakeMountProvider());
            var host = GuiHost.Create(new GuiOptions(_paths.Settings), registry);
            host.Start();
            var window = new MainWindow(host);
            window.Show();
            await Wait.UntilAsync(() => window.OwnedWindows.Count == 1, what: "the message over the window");
            var said = (ConfirmDialogViewModel)window.OwnedWindows.Single().DataContext!;
            StringAssert.Contains(said.Message, "sources.json could not be read");
            window.OwnedWindows.Single().Close();
            await host.ShutdownAsync();
            window.Close();
            return true;
        }, CancellationToken.None);
    }
}
