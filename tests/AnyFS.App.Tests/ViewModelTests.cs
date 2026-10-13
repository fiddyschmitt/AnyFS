using AnyFS.App.Gui.Services;
using AnyFS.App.Gui.ViewModels;
using AnyFS.Core.Plugins;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Core.Status;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.App.Tests;

/// <summary>The window's view models over the real engine with the fake plugins; dialogs are scripted.</summary>
[TestClass]
public class ViewModelTests
{
    private string _root = "";
    private SettingsPaths _paths = null!;
    private AnyFsEngine _engine = null!;
    private FakeDialogs _dialogs = null!;
    private FakeOpener _opener = null!;
    private FakeClipboard _clipboard = null!;
    private MainViewModel _vm = null!;

    [TestInitialize]
    public async Task Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "gui-" + Guid.NewGuid().ToString("N"));
        _paths = SettingsPaths.Resolve(Path.Combine(_root, "settings"));
        lock (FakeMountProvider.Mounts)
        {
            FakeMountProvider.Mounts.Clear();
        }

        var registry = new PluginRegistry();
        registry.AddBuiltIn(new MemoryPlugin());
        registry.AddBuiltIn(new FakeMountProvider());
        _engine = new AnyFsEngine(_paths, registry, NullLoggerFactory.Instance);
        _engine.Load();
        await _engine.StartAsync(null, CancellationToken.None);
        _dialogs = new FakeDialogs();
        _opener = new FakeOpener();
        _clipboard = new FakeClipboard();
        _vm = new MainViewModel(_engine, _dialogs, _opener, _clipboard);
        _vm.Refresh();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _engine.DisposeAsync();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private CollectionsViewModel Collections => _vm.Collections;

    private async Task<CollectionTileViewModel> AddCollectionAsync(string name)
    {
        _dialogs.OnCollection = d =>
        {
            d.Name = name;
            return Task.FromResult(true);
        };
        await Collections.AddCollectionCommand.ExecuteAsync(null);
        _vm.Refresh();
        return Collections.Collections.Single(c => c.Name == name);
    }

    private async Task<SourceTileViewModel> AddSourceAsync(string name, string files, string? path = null)
    {
        _dialogs.OnSource = d =>
        {
            d.SelectedPlugin = d.PluginChoices.Single(p => p.TypeId == "memory");
            d.Name = name;
            if (path is not null)
            {
                d.Path = path;
            }

            d.Form!.Fields.Single(f => f.PropertyName == "Files").Text = files;
            return Task.FromResult(true);
        };
        await Collections.AddNewSourceCommand.ExecuteAsync(null);
        _vm.Refresh();
        var tile = Collections.Sources.Single(s => s.Name == name);
        await Wait.UntilAsync(() => _engine.FindSourceRunner(tile.Id)!.Handle.IsUsable, what: $"{name} ready");
        _vm.Refresh();
        return tile;
    }

    private async Task<MountTileViewModel> AddMountAsync(int port)
    {
        _dialogs.OnMount = d =>
        {
            d.SelectedProvider = d.Providers.Single(p => p.TypeId == "fake");
            d.Form!.Fields.Single(f => f.PropertyName == "Port").Text = port.ToString();
            return Task.FromResult(true);
        };
        await Collections.AddMountCommand.ExecuteAsync(null);
        _vm.Refresh();
        return Collections.Mounts.Single(m => m.Name == "Fake");
    }

    [TestMethod]
    public void FirstStartShowsCollectionOneWithItsNativeMountTile()
    {
        var tile = Collections.Collections.Single();
        Assert.AreEqual("Collection 1", tile.Name);
        Assert.AreSame(tile, Collections.SelectedCollection);
        Assert.AreEqual("Collection: Collection 1", Collections.SelectedHeader);
        Assert.IsTrue(tile.Enabled);
        Assert.AreEqual("\u2014", tile.FilesText, "no sources: no count");
        Assert.IsTrue(tile.HasNoOpenTarget);
        Assert.AreEqual(0, Collections.Sources.Count);

        var mount = Collections.Mounts.Single();
        Assert.AreEqual(SettingsStore.DefaultMountName(SettingsStore.NativeProviderForThisOs()), mount.Name);
        Assert.AreEqual(StatusColour.Red, mount.Colour, "the native provider isn't in this registry");
        StringAssert.Contains(mount.StatusText, "not available");
        Assert.AreEqual("Fake is for compatibility.", Collections.MountGuidance);
        Assert.AreEqual(StatusColour.Red, _vm.OverallColour);
        StringAssert.Contains(_vm.StatusText, "failure");
        StringAssert.Contains(_vm.FilesServedText, "0 files served");
    }

    [TestMethod]
    public async Task AddingACollectionSelectsItAndARejectedNameStaysInTheDialog()
    {
        var ben = await AddCollectionAsync("ben");
        Assert.AreSame(ben, Collections.SelectedCollection);
        Assert.IsTrue(_engine.Settings.Collections.Any(c => c.Name == "ben"));

        CollectionDialogViewModel? dialog = null;
        _dialogs.OnCollection = d =>
        {
            dialog = d;
            d.Name = "BEN";
            return Task.FromResult(true);
        };
        await Collections.AddCollectionCommand.ExecuteAsync(null);
        StringAssert.Contains(dialog!.Error!, "already a collection");
        Assert.AreEqual(2, _engine.Settings.Collections.Count);
    }

    [TestMethod]
    public async Task AddingANewSourceFromACollectionCreatesItAndItsMembership()
    {
        await AddCollectionAsync("ben");
        SourceDialogViewModel? seen = null;
        _dialogs.OnSource = d =>
        {
            seen = d;
            d.SelectedPlugin = d.PluginChoices.Single(p => p.TypeId == "memory");
            d.Name = "alpha";
            Assert.AreEqual("mem/alpha", d.Path, "the path follows the name");
            d.Path = "mem";
            d.Form!.Fields.Single(f => f.PropertyName == "Files").Text = "a.txt=A";
            return Task.FromResult(true);
        };
        await Collections.AddNewSourceCommand.ExecuteAsync(null);
        Assert.IsNull(seen!.Error);
        _vm.Refresh();

        var tile = Collections.Sources.Single();
        Assert.AreEqual("alpha", tile.Name);
        Assert.AreEqual("mem", tile.Path);
        Assert.AreEqual("Memory", tile.TypeName);
        Assert.AreEqual("M", tile.Initial);
        Assert.IsTrue(tile.ShowPath);
        await Wait.UntilAsync(() => _engine.FindSourceRunner(tile.Id)!.Handle.IsUsable, what: "alpha ready");
        _vm.Refresh();
        Assert.AreEqual(StatusColour.Green, tile.Colour);
        Assert.AreEqual("0+ files", tile.FilesText, "a lazy source: nothing listed yet (spec 6.8)");
        Assert.IsFalse(tile.ReadOnlyLocked, "the memory source can write");

        var membership = _engine.Settings.Collections.Single(c => c.Name == "ben").Sources.Single();
        Assert.AreEqual("mem", membership.Path);
        Assert.AreEqual("alpha", _engine.Settings.Sources.Single().Name);

        await ListAsync(_engine.Collections.Single(c => c.Name == "ben").Vfs, "mem");
        _vm.Refresh();
        Assert.AreEqual("1+ files", tile.FilesText, "what the router has seen go by");
        Assert.AreEqual("1+ files", Collections.SelectedCollection!.FilesText);
    }

    [TestMethod]
    public async Task TileCheckboxesDriveTheEngineAndPersist()
    {
        await AddCollectionAsync("ben");
        var tile = await AddSourceAsync("alpha", "a.txt=A");

        tile.Enabled = false;
        await _vm.Operations.Last;
        Assert.IsFalse(_engine.Settings.Sources.Single().Enabled);
        Assert.IsFalse(_engine.FindSourceRunner(tile.Id)!.IsRunning);
        _vm.Refresh();
        Assert.AreEqual(StatusColour.Grey, tile.Colour);

        tile.ReadOnly = true;
        await _vm.Operations.Last;
        Assert.IsTrue(_engine.Settings.Collections.Single(c => c.Name == "ben").Sources.Single().ReadOnly);

        var collection = Collections.SelectedCollection!;
        collection.Enabled = false;
        await _vm.Operations.Last;
        Assert.IsFalse(_engine.Settings.Collections.Single(c => c.Name == "ben").Enabled);
        _vm.Refresh();
        Assert.AreEqual("Disabled", collection.StatusText);
        Assert.AreEqual(0, _dialogs.Messages.Count, "no error dialogs");
    }

    [TestMethod]
    public async Task ExistingSourceFlyoutAddsAMembershipWithTheDefaultPath()
    {
        await AddCollectionAsync("ben");
        var alpha = await AddSourceAsync("alpha", "a.txt=A");
        await AddCollectionAsync("bob");
        Assert.AreEqual(0, Collections.Sources.Count);
        Assert.IsTrue(Collections.HasExistingSources);
        var choice = Collections.ExistingSources.Single();
        Assert.AreEqual(alpha.Id, choice.Id);
        Assert.AreEqual("alpha  (Memory)", choice.Header);

        _dialogs.OnExisting = d =>
        {
            Assert.AreEqual("mem/alpha", d.Path);
            d.ReadOnly = true;
            return Task.FromResult(true);
        };
        choice.AddCommand.Execute(null);
        await _vm.Operations.Last;
        await Wait.UntilAsync(() => _engine.Settings.Collections.Single(c => c.Name == "bob").Sources.Count == 1, what: "membership");
        _vm.Refresh();
        var tile = Collections.Sources.Single();
        Assert.AreEqual("mem/alpha", tile.Path);
        Assert.IsTrue(tile.ReadOnly);
        Assert.IsFalse(Collections.HasExistingSources, "alpha is in bob now");

        _vm.AllSources.Refresh();
        var all = _vm.AllSources.Sources.Single();
        Assert.IsTrue(all.ShowUses);
        CollectionAssert.AreEqual(new[] { "ben: mem/alpha", "bob: mem/alpha  [read-only]" }, all.Uses.ToList());
    }

    [TestMethod]
    public async Task RemovingTheLastUseOffersToDeleteTheDefinition()
    {
        await AddCollectionAsync("ben");
        var tile = await AddSourceAsync("alpha", "a.txt=A");
        Collections.SelectedSource = tile;
        _dialogs.OnConfirm = c =>
        {
            StringAssert.Contains(c.Message, "Remove 'alpha'");
            Assert.IsNotNull(c.CheckboxText, "no other collection uses it, so the checkbox is offered");
            return new ConfirmAnswer(ConfirmButton.Yes, Checked: true);
        };
        await Collections.DeleteSourceCommand.ExecuteAsync(null);
        await _vm.Operations.Last;
        Assert.AreEqual(0, _engine.Settings.Sources.Count, "definition deleted too");
        _vm.Refresh();
        Assert.AreEqual(0, Collections.Sources.Count);
        Assert.IsNull(Collections.SelectedSource);
    }

    [TestMethod]
    public async Task EditingASourceAppliesSettingsEverywhereAndPathHere()
    {
        await AddCollectionAsync("ben");
        var tile = await AddSourceAsync("alpha", "a.txt=A");
        Collections.SelectedSource = tile;
        _dialogs.OnSource = d =>
        {
            Assert.IsFalse(d.IsNew);
            Assert.AreEqual("alpha", d.Name);
            Assert.AreEqual("mem/alpha", d.Path);
            Assert.AreEqual("a.txt=A", d.Form!.Fields.Single(f => f.PropertyName == "Files").Text, "the dialog is filled in");
            d.Name = "alpha2";
            d.Path = "files";
            d.Form.Fields.Single(f => f.PropertyName == "Files").Text = "b.txt=B";
            return Task.FromResult(true);
        };
        await Collections.EditSourceCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => _engine.FindSourceRunner(tile.Id)!.Handle.IsUsable, what: "restarted");
        _vm.Refresh();
        Assert.AreEqual("alpha2", tile.Name);
        Assert.AreEqual("files", tile.Path);
        CollectionAssert.AreEqual(new[] { "b.txt" }, (await ListAsync(_engine.Collections.Single(c => c.Name == "ben").Vfs, "files")));
    }

    [TestMethod]
    public async Task MountsShowAccessPathsAndGiveEveryTileAnOpenTarget()
    {
        await AddCollectionAsync("ben");
        var source = await AddSourceAsync("alpha", "a.txt=A", path: "mem");
        var mount = await AddMountAsync(5);
        Assert.AreEqual("Fake", mount.ProviderName);
        Assert.AreEqual(StatusColour.Green, mount.Colour);
        Assert.AreEqual("fake://5/ben/", mount.AccessPaths.Single().Value);
        Assert.AreEqual("URL", mount.AccessPaths.Single().Label);
        await mount.AccessPaths.Single().CopyCommand.ExecuteAsync(null);
        CollectionAssert.AreEqual(new[] { "fake://5/ben/" }, _clipboard.Texts);

        Assert.IsTrue(mount.HasOneOpenTarget);
        Assert.IsTrue(Collections.SelectedCollection!.HasOneOpenTarget);
        Assert.IsTrue(source.HasOneOpenTarget);
        source.OpenSingleCommand.Execute(null);
        var (action, subPath) = _opener.Opened.Single();
        Assert.AreEqual("mem", subPath, "a source opens at its path inside the mount");
        Assert.AreEqual("fake://5/ben/", ((MountOpenAction.Browser)action).Url);

        await AddMountAsync2(6);
        _vm.Refresh();
        Assert.IsTrue(Collections.SelectedCollection!.HasManyOpenTargets, "two mounts: a flyout");
        Assert.AreEqual(2, source.OpenTargets.Count);

        Collections.SelectedMount = mount;
        _dialogs.OnConfirm = _ => new ConfirmAnswer(ConfirmButton.Yes, false);
        await Collections.DeleteMountCommand.ExecuteAsync(null);
        await _vm.Operations.Last;
        _vm.Refresh();
        Assert.AreEqual(1, Collections.Mounts.Count);
    }

    private async Task AddMountAsync2(int port)
    {
        _dialogs.OnMount = d =>
        {
            d.SelectedProvider = d.Providers.Single(p => p.TypeId == "fake");
            d.Name = "Second";
            d.Form!.Fields.Single(f => f.PropertyName == "Port").Text = port.ToString();
            return Task.FromResult(true);
        };
        await Collections.AddMountCommand.ExecuteAsync(null);
    }

    [TestMethod]
    public async Task DeletingACollectionListsMountsAndOrphans()
    {
        var ben = await AddCollectionAsync("ben");
        await AddSourceAsync("alpha", "a.txt=A");
        await AddMountAsync(7);
        Collections.SelectedCollection = ben;
        _dialogs.OnConfirm = c =>
        {
            StringAssert.Contains(c.Message, "Fake");
            StringAssert.Contains(c.CheckboxText!, "alpha");
            return new ConfirmAnswer(ConfirmButton.Yes, Checked: false);
        };
        await Collections.DeleteCollectionCommand.ExecuteAsync(null);
        await _vm.Operations.Last;
        _vm.Refresh();
        Assert.IsFalse(Collections.Collections.Any(c => c.Name == "ben"));
        Assert.AreEqual(1, _engine.Settings.Sources.Count, "the orphan was kept (checkbox off)");
        Assert.AreEqual("Collection 1", Collections.SelectedCollection!.Name, "selection falls back");
    }

    [TestMethod]
    public async Task StatsTabReflectsServedFiles()
    {
        await AddCollectionAsync("ben");
        await AddSourceAsync("alpha", "a.txt=A", path: "");
        await AddMountAsync(8);
        var fs = FakeMountProvider.Mounts.Single(m => m.Collection.Name == "ben" && m.Fs is not null).Fs!;
        await using (var file = await fs.OpenAsync("a.txt", VfsOpenMode.Read, CancellationToken.None))
        {
            var buffer = new byte[8];
            await file.ReadAsync(0, buffer, CancellationToken.None);
        }

        _vm.Refresh();
        Assert.AreEqual("1", _vm.Stats.FilesServedSession);
        Assert.AreEqual("1 B", _vm.Stats.BytesServedSession);
        Assert.AreEqual("1 files served this session", _vm.FilesServedText);
        var row = _vm.Stats.Rows.Single(r => r.Collection == "ben");
        Assert.AreEqual("1", row.FilesSession);

        _dialogs.OnConfirm = _ => new ConfirmAnswer(ConfirmButton.Yes, false);
        await _vm.Stats.ResetTotalsCommand.ExecuteAsync(null);
        Assert.AreEqual("0", _vm.Stats.FilesServedTotal);
        Assert.AreEqual("1", _vm.Stats.FilesServedSession, "the session keeps counting");
    }

    [TestMethod]
    public void SourceDialogPathFollowsTheNameUntilEditedByHand()
    {
        var dialog = new SourceDialogViewModel(_engine, _dialogs, null, null, askPath: true);
        Assert.AreEqual("memory", dialog.SelectedPlugin!.TypeId);
        Assert.IsTrue(dialog.HasForm);
        dialog.Name = "aus";
        Assert.AreEqual("mem/aus", dialog.Path);
        dialog.Name = "aus-files-01";
        Assert.AreEqual("mem/aus-files-01", dialog.Path);
        dialog.Path = "files";
        dialog.Name = "other";
        Assert.AreEqual("files", dialog.Path, "edited by hand: stops following");
        Assert.IsTrue(dialog.Validate());
        dialog.Path = "../x";
        Assert.IsFalse(dialog.Validate());
        StringAssert.Contains(dialog.Error!, "path");
        dialog.Path = "";
        Assert.IsTrue(dialog.Validate(), "an empty path is allowed (spec 4.1)");
    }

    [TestMethod]
    public void MountDialogNameFollowsTheProviderAndTheFormComesFromIt()
    {
        var runner = _engine.Collections.Single();
        var dialog = new MountDialogViewModel(_engine, _dialogs, runner, null);
        Assert.AreEqual("fake", dialog.SelectedProvider!.TypeId);
        Assert.AreEqual("Fake", dialog.Name);
        Assert.IsFalse(dialog.ReadOnlyLocked);
        Assert.IsTrue(dialog.HasForm);
        Assert.IsTrue(dialog.Form!.Fields.Any(f => f.PropertyName == "Port"));
        dialog.Name = "";
        Assert.IsFalse(dialog.Validate());
        dialog.Name = "LAN";
        Assert.IsTrue(dialog.Validate());

        var existing = runner.Definition.Mounts.Single();
        var edit = new MountDialogViewModel(_engine, _dialogs, runner, existing);
        Assert.IsFalse(edit.IsNew);
        Assert.IsNull(edit.SelectedProvider, "the native provider isn't in this registry");
        StringAssert.Contains(edit.Error!, "not available");
    }

    [TestMethod]
    public void MountGuidanceIsWordedFromTheTiers()
    {
        var text = CollectionsViewModel.BuildMountGuidance([new FakeMountProvider(), new TierProvider("WinFsp", PerformanceTier.Native), new TierProvider("SMB", PerformanceTier.Network), new TierProvider("NFS", PerformanceTier.Network), new TierProvider("HTTP", PerformanceTier.Protocol)]);
        Assert.AreEqual("WinFsp gives the best performance; NFS and SMB serve other machines; Fake and HTTP are for compatibility.", text);
    }

    [TestMethod]
    public void OpenerBuildsPathsAndUrls()
    {
        Assert.AreEqual("http://h/ben/mem/alpha/", ShellOpener.AppendUrl("http://h/ben/", "mem/alpha"));
        Assert.AreEqual("http://h/ben/", ShellOpener.AppendUrl("http://h/ben/", null));
        Assert.AreEqual("http://h/ben/a%20b/", ShellOpener.AppendUrl("http://h/ben", "a b"));
        var expected = @"\\anyfs\ben" + Path.DirectorySeparatorChar + "mem" + Path.DirectorySeparatorChar + "alpha";
        Assert.AreEqual(expected, ShellOpener.AppendPath(@"\\anyfs\ben", "mem/alpha"));
    }

    private static async Task<List<string>> ListAsync(IVfsOperations ops, string path)
    {
        var names = new List<string>();
        await foreach (var entry in ops.ListAsync(path, CancellationToken.None))
        {
            names.Add(entry.IsDirectory ? entry.Name + "/" : entry.Name);
        }

        return names;
    }

    private sealed class TierProvider(string name, PerformanceTier tier) : IMountProviderPlugin
    {
        public string TypeId => name.ToLowerInvariant();

        public string DisplayName => name;

        public string Description => name;

        public Stream? OpenIcon() => null;

        public IReadOnlyList<System.Runtime.InteropServices.OSPlatform> SupportedPlatforms { get; } = [System.Runtime.InteropServices.OSPlatform.Windows, System.Runtime.InteropServices.OSPlatform.Linux, System.Runtime.InteropServices.OSPlatform.OSX];

        public string? UnavailableReason => null;

        public PerformanceTier Tier => tier;

        public bool SupportsWrites => true;

        public Type SettingsType => typeof(FakeMountSettings);

        public object CreateDefaultSettings(ICollectionInfo collection) => new FakeMountSettings();

        public IMount CreateMount(object settings, ICollectionInfo collection) => throw new NotSupportedException();
    }
}
