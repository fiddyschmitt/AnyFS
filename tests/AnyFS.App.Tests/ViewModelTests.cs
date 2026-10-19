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
        registry.AddBuiltIn(new QuickPlugin());
        registry.AddBuiltIn(new NamedPlugin());
        registry.AddBuiltIn(new FakeMountProvider());
        _installable = new InstallableProvider();
        registry.AddBuiltIn(_installable);
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
        Assert.IsTrue(tile.Enabled);
        Assert.AreEqual("\u2014", tile.FilesText, "no sources: no count");
        Assert.IsTrue(tile.HasNoOpenTarget);
        Assert.AreEqual(0, Collections.Sources.Count);

        var mount = Collections.Mounts.Single();
        Assert.AreEqual(SettingsStore.DefaultMountName(SettingsStore.NativeProviderForThisOs()), mount.Name);
        Assert.AreEqual(StatusColour.Red, mount.Colour, "the native provider isn't in this registry");
        StringAssert.Contains(mount.StatusText, "not available");
        Assert.IsFalse(mount.HasStatusAccessPath, "nothing to copy from a failed mount");
        Assert.AreEqual(StatusColour.Red, _vm.OverallColour);
        StringAssert.Contains(_vm.StatusText, "failure");
        StringAssert.Contains(_vm.FilesServedText, "0 files served");
    }

    [TestMethod]
    public async Task AddingACollectionSelectsItAndARejectedNameStaysInTheDialog()
    {
        var work = await AddCollectionAsync("work");
        Assert.AreSame(work, Collections.SelectedCollection);
        Assert.IsTrue(_engine.Settings.Collections.Any(c => c.Name == "work"));

        CollectionDialogViewModel? dialog = null;
        _dialogs.OnCollection = d =>
        {
            dialog = d;
            d.Name = "WORK";
            return Task.FromResult(true);
        };
        await Collections.AddCollectionCommand.ExecuteAsync(null);
        StringAssert.Contains(dialog!.Error!, "already a collection");
        Assert.AreEqual(2, _engine.Settings.Collections.Count);
    }

    [TestMethod]
    public async Task AddingANewSourceFromACollectionCreatesItAndItsMembership()
    {
        await AddCollectionAsync("work");
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

        var membership = _engine.Settings.Collections.Single(c => c.Name == "work").Sources.Single();
        Assert.AreEqual("mem", membership.Path);
        Assert.AreEqual("alpha", _engine.Settings.Sources.Single().Name);

        await ListAsync(_engine.Collections.Single(c => c.Name == "work").Vfs, "mem");
        _vm.Refresh();
        Assert.AreEqual("1+ files", tile.FilesText, "what the router has seen go by");
        Assert.AreEqual("1+ files", Collections.SelectedCollection!.FilesText);
    }

    [TestMethod]
    public async Task TileCheckboxesDriveTheEngineAndPersist()
    {
        await AddCollectionAsync("work");
        var tile = await AddSourceAsync("alpha", "a.txt=A");

        tile.Enabled = false;
        await _vm.Operations.Last;
        Assert.IsFalse(_engine.Settings.Sources.Single().Enabled);
        Assert.IsFalse(_engine.FindSourceRunner(tile.Id)!.IsRunning);
        _vm.Refresh();
        Assert.AreEqual(StatusColour.Grey, tile.Colour);

        tile.ReadOnly = true;
        await _vm.Operations.Last;
        Assert.IsTrue(_engine.Settings.Collections.Single(c => c.Name == "work").Sources.Single().ReadOnly);

        var collection = Collections.SelectedCollection!;
        collection.Enabled = false;
        await _vm.Operations.Last;
        Assert.IsFalse(_engine.Settings.Collections.Single(c => c.Name == "work").Enabled);
        _vm.Refresh();
        Assert.AreEqual("Disabled", collection.StatusText);
        Assert.AreEqual(0, _dialogs.Messages.Count, "no error dialogs");
    }

    [TestMethod]
    public async Task ExistingSourceFlyoutAddsAMembershipWithTheDefaultPath()
    {
        await AddCollectionAsync("work");
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
        CollectionAssert.AreEqual(new[] { "bob: mem/alpha  [read-only]", "work: mem/alpha" }, all.Uses.ToList());
    }

    [TestMethod]
    public async Task RemovingTheLastUseOffersToDeleteTheDefinition()
    {
        await AddCollectionAsync("work");
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
        await AddCollectionAsync("work");
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
        CollectionAssert.AreEqual(new[] { "b.txt" }, (await ListAsync(_engine.Collections.Single(c => c.Name == "work").Vfs, "files")));
    }

    [TestMethod]
    public async Task MountsShowAccessPathsAndGiveEveryTileAnOpenTarget()
    {
        await AddCollectionAsync("work");
        var source = await AddSourceAsync("alpha", "a.txt=A", path: "mem");
        var mount = await AddMountAsync(5);
        Assert.AreEqual("Fake", mount.ProviderName);
        Assert.IsFalse(mount.ShowProvider, "named after its provider: no provider line");
        Assert.AreEqual(StatusColour.Green, mount.Colour);
        Assert.AreEqual("fake://5/work/", mount.AccessPaths.Single().Value);
        Assert.AreEqual("URL", mount.AccessPaths.Single().Label);
        Assert.AreEqual("fake://5/work/", mount.StatusText, "a running mount's status line is its access path");
        Assert.AreSame(mount.AccessPaths.Single(), mount.StatusAccessPath, "so the Copy button sits beside the status line");
        Assert.AreEqual("Copy the URL", mount.StatusCopyTooltip);
        Assert.AreEqual(0, mount.OtherAccessPaths.Count, "and no separate box repeats it");
        await mount.StatusAccessPath!.CopyCommand.ExecuteAsync(null);
        CollectionAssert.AreEqual(new[] { "fake://5/work/" }, _clipboard.Texts);

        Assert.IsTrue(mount.HasOneOpenTarget);
        Assert.IsTrue(Collections.SelectedCollection!.HasOneOpenTarget);
        Assert.IsTrue(source.HasOneOpenTarget);
        source.OpenSingleCommand.Execute(null);
        var (action, subPath) = _opener.Opened.Single();
        Assert.AreEqual("mem", subPath, "a source opens at its path inside the mount");
        Assert.AreEqual("fake://5/work/", ((MountOpenAction.Browser)action).Url);

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
        var work = await AddCollectionAsync("work");
        await AddSourceAsync("alpha", "a.txt=A");
        await AddMountAsync(7);
        Collections.SelectedCollection = work;
        _dialogs.OnConfirm = c =>
        {
            StringAssert.Contains(c.Message, "Fake");
            StringAssert.Contains(c.CheckboxText!, "alpha");
            return new ConfirmAnswer(ConfirmButton.Yes, Checked: false);
        };
        await Collections.DeleteCollectionCommand.ExecuteAsync(null);
        await _vm.Operations.Last;
        _vm.Refresh();
        Assert.IsFalse(Collections.Collections.Any(c => c.Name == "work"));
        Assert.AreEqual(1, _engine.Settings.Sources.Count, "the orphan was kept (checkbox off)");
        Assert.AreEqual("Collection 1", Collections.SelectedCollection!.Name, "selection falls back");
    }

    [TestMethod]
    public async Task StatsTabReflectsServedFiles()
    {
        await AddCollectionAsync("work");
        await AddSourceAsync("alpha", "a.txt=A", path: "");
        await AddMountAsync(8);
        var fs = FakeMountProvider.Mounts.Single(m => m.Collection.Name == "work" && m.Fs is not null).Fs!;
        await using (var file = await fs.OpenAsync("a.txt", VfsOpenMode.Read, CancellationToken.None))
        {
            var buffer = new byte[8];
            await file.ReadAsync(0, buffer, CancellationToken.None);
        }

        _vm.Refresh();
        Assert.AreEqual("1", _vm.Stats.FilesServedSession);
        Assert.AreEqual("1 B", _vm.Stats.BytesServedSession);
        Assert.AreEqual("1 files served this session", _vm.FilesServedText);
        var row = _vm.Stats.Rows.Single(r => r.Collection == "work");
        Assert.AreEqual("1", row.FilesSession);
        var idle = _vm.Stats.Rows.Single(r => r.Collection == "Collection 1");
        Assert.AreEqual("0", idle.FilesSession, "every collection has a row in the table, served or not");

        _dialogs.OnConfirm = _ => new ConfirmAnswer(ConfirmButton.Yes, false);
        await _vm.Stats.ResetTotalsCommand.ExecuteAsync(null);
        Assert.AreEqual("0", _vm.Stats.FilesServedTotal);
        Assert.AreEqual("1", _vm.Stats.FilesServedSession, "the session keeps counting");
    }

    [TestMethod]
    public async Task ExportWritesTheChosenSourcesAndImportMergesById()
    {
        await AddCollectionAsync("work");
        await AddSourceAsync("alpha", "a.txt=A", path: "");
        var beta = await AddSourceAsync("beta", "b.txt=B", path: "beta");
        var all = _vm.AllSources;
        all.Refresh();
        Assert.IsTrue(all.ExportSourcesCommand.CanExecute(null));

        // export beta alone
        var file = Path.Combine(_root, "export.json");
        _dialogs.SaveFileToPick = file;
        _dialogs.OnExport = d =>
        {
            foreach (var item in d.Items)
            {
                item.IsChecked = item.Name == "beta";
            }

            return Task.FromResult(true);
        };
        await all.ExportSourcesCommand.ExecuteAsync(null);
        var exported = SourceTransfer.Read(file);
        Assert.AreEqual(1, exported.Count);
        Assert.AreEqual("beta", exported[0].Name);
        Assert.AreEqual(beta.Id, exported[0].Id);
        Assert.IsNotNull(exported[0].Settings);

        // the same id again, imported as a copy: a new id and a distinct name
        _dialogs.FileToPick = file;
        _dialogs.OnChoose = (_, _, options) => options.ToList().FindIndex(o => o.Contains("copy"));
        await all.ImportSourcesCommand.ExecuteAsync(null);
        _vm.Refresh();
        Assert.AreEqual(1, _dialogs.Choices.Count, "one clash was asked about");
        var copy = _engine.Settings.Sources.Single(s => s.Name == "beta (2)");
        Assert.AreNotEqual(beta.Id, copy.Id);
        StringAssert.Contains(_dialogs.Messages.Last().Message, "1 imported as cop");

        // keep mine: nothing changes
        var count = _engine.Settings.Sources.Count;
        _dialogs.OnChoose = (_, _, options) => options.ToList().FindIndex(o => o.Contains("Keep"));
        await all.ImportSourcesCommand.ExecuteAsync(null);
        Assert.AreEqual(count, _engine.Settings.Sources.Count);
        StringAssert.Contains(_dialogs.Messages.Last().Message, "1 kept");

        // replace mine: the file's name takes over the id, and the source restarts
        var edited = SourceTransfer.Read(file).Single();
        edited.Name = "beta-renamed";
        SourceTransfer.Write(file, [edited]);
        _dialogs.OnChoose = (_, _, options) => options.ToList().FindIndex(o => o.Contains("Replace"));
        await all.ImportSourcesCommand.ExecuteAsync(null);
        _vm.Refresh();
        Assert.AreEqual("beta-renamed", _engine.Settings.FindSource(beta.Id)!.Name);
        Assert.IsTrue(all.Sources.Any(s => s.Name == "beta-renamed"));
        await Wait.UntilAsync(() => _engine.FindSourceRunner(beta.Id)?.Handle.IsUsable == true, what: "beta-renamed running again");

        // a new id is simply added and started
        var fresh = SourceTransfer.Read(file).Single();
        fresh.Id = Guid.NewGuid();
        fresh.Name = "gamma";
        SourceTransfer.Write(file, [fresh]);
        await all.ImportSourcesCommand.ExecuteAsync(null);
        Assert.IsNotNull(_engine.Settings.FindSource(fresh.Id));
        StringAssert.Contains(_dialogs.Messages.Last().Message, "1 added");
        await Wait.UntilAsync(() => _engine.FindSourceRunner(fresh.Id)?.Handle.IsUsable == true, what: "gamma started");

        // a file that isn't a sources file says so
        File.WriteAllText(file, "not json at all");
        await all.ImportSourcesCommand.ExecuteAsync(null);
        StringAssert.Contains(_dialogs.Messages.Last().Message, "not a sources file");
    }

    [TestMethod]
    public void SettingsFormLeavesOutFieldsForOtherOperatingSystems()
    {
        var form = new SettingsFormViewModel(typeof(PlatformSettings), new PlatformSettings(), null, null, null, _dialogs);
        var names = form.Fields.Select(f => f.PropertyName).ToList();
        CollectionAssert.Contains(names, nameof(PlatformSettings.Everywhere));
        CollectionAssert.Contains(names, nameof(PlatformSettings.Here));
        CollectionAssert.DoesNotContain(names, nameof(PlatformSettings.Elsewhere));
    }

    public sealed class PlatformSettings
    {
        [Setting("Everywhere")]
        public string? Everywhere { get; set; }

        [Setting("Here", Platforms = new[] { "windows", "linux", "macos", "freebsd" })]
        public string? Here { get; set; }

        [Setting("Elsewhere", Platforms = new[] { "plan9" })]
        public string? Elsewhere { get; set; }
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
    public void SourceDialogNamesTheSourceFromTheSettingAskedFirst()
    {
        var dialog = new SourceDialogViewModel(_engine, _dialogs, null, null, askPath: true);
        dialog.SelectedPlugin = dialog.PluginChoices.Single(p => p.TypeId == "named");
        var server = dialog.Form!.PrimaryField!;
        Assert.AreEqual(nameof(NamedServerSettings.Server), server.PropertyName, "asked above the name");
        StringAssert.Contains(dialog.NameHint, "Server");
        Assert.AreEqual("", dialog.Name, "nothing to name it from yet");

        server.Text = "aus-files-01.contoso.local";
        Assert.AreEqual("aus-files-01", dialog.Name);
        Assert.AreEqual("named/aus-files-01", dialog.Path, "the path follows the name, as before");

        server.Text = "";
        Assert.AreEqual("", dialog.Name, "a suggestion that lapses empties the box again");

        server.Text = "syd-files-02";
        dialog.Name = "Sydney";
        server.Text = "syd-files-03";
        Assert.AreEqual("Sydney", dialog.Name, "typed by hand: the suggestions stop");
        Assert.AreEqual("named/Sydney", dialog.Path);

        dialog.Name = "";
        server.Text = "syd-files-04";
        Assert.AreEqual("syd-files-04", dialog.Name, "an emptied box follows again");
        Assert.IsTrue(dialog.Validate());
        Assert.AreEqual("syd-files-04", ((NamedServerSettings)dialog.Settings!).Server);

        dialog.SelectedPlugin = dialog.PluginChoices.Single(p => p.TypeId == "memory");
        Assert.AreEqual("", dialog.Name, "another type: the old type's suggestion goes");
        Assert.IsNull(dialog.Form!.PrimaryField, "Memory marks no setting to ask first");
        Assert.AreEqual(SourceDialogViewModel.NameTip, dialog.NameHint);
    }

    [TestMethod]
    public async Task SourceDialogKeepsANameTypedFirstAndAnExistingSourcesName()
    {
        var dialog = new SourceDialogViewModel(_engine, _dialogs, null, null, askPath: true);
        dialog.Name = "mine";
        dialog.SelectedPlugin = dialog.PluginChoices.Single(p => p.TypeId == "named");
        dialog.Form!.PrimaryField!.Text = "aus-files-01";
        Assert.AreEqual("mine", dialog.Name, "a name typed before the type was chosen is kept");

        var definition = await _engine.AddSourceAsync("Sydney office", "named", true, false, new NamedServerSettings { Server = "syd-files-02" });
        var edit = new SourceDialogViewModel(_engine, _dialogs, definition, null, askPath: false);
        Assert.AreEqual("syd-files-02", edit.Form!.PrimaryField!.Text, "asked first when editing too");
        edit.Form.PrimaryField.Text = "syd-files-03";
        Assert.AreEqual("Sydney office", edit.Name, "a name of the person's own is kept");
        Assert.AreEqual(SourceDialogViewModel.NameTip, edit.NameHint);
    }

    [TestMethod]
    public async Task SourceDialogEditFollowsANameThePluginMade()
    {
        var collection = _engine.Collections.Single();
        var definition = await _engine.AddSourceAsync("syd-files-02", "named", true, false, new NamedServerSettings { Server = "syd-files-02.contoso.local" });
        await _engine.AddMembershipAsync(collection.Id, definition.Id, "named/syd-files-02", false);
        var edit = new SourceDialogViewModel(_engine, _dialogs, definition, collection.Definition.Sources.Single(m => m.Id == definition.Id), askPath: true);
        StringAssert.Contains(edit.NameHint, "Server", "the name was the plugin's when the edit began");

        edit.Form!.PrimaryField!.Text = "syd-files-03.contoso.local";
        Assert.AreEqual("syd-files-03", edit.Name, "so it goes on following the settings");
        Assert.AreEqual("named/syd-files-03", edit.Path, "and the path, the default one for the name, follows the name");

        edit.Name = "Sydney";
        edit.Form.PrimaryField.Text = "syd-files-04";
        Assert.AreEqual("Sydney", edit.Name, "typed by hand: the suggestions stop");
        Assert.AreEqual("named/Sydney", edit.Path);

        // a path of the person's own stays put while the name follows
        await _engine.UpdateMembershipAsync(collection.Id, definition.Id, "servers/sydney", false);
        var moved = new SourceDialogViewModel(_engine, _dialogs, definition, collection.Definition.Sources.Single(m => m.Id == definition.Id), askPath: true);
        moved.Form!.PrimaryField!.Text = "syd-files-05";
        Assert.AreEqual("syd-files-05", moved.Name);
        Assert.AreEqual("servers/sydney", moved.Path);

        var fromAllSources = new SourceDialogViewModel(_engine, _dialogs, definition, null, askPath: false);
        fromAllSources.Form!.PrimaryField!.Text = "syd-files-06";
        Assert.AreEqual("syd-files-06", fromAllSources.Name, "the same from the All sources tab, which has no path");
    }

    [TestMethod]
    public void SourceDialogCountsOnStartByDefaultForQuickSourcesUntilToggledByHand()
    {
        var dialog = new SourceDialogViewModel(_engine, _dialogs, null, null, askPath: true);
        Assert.AreEqual("memory", dialog.SelectedPlugin!.TypeId);
        Assert.IsFalse(dialog.CountFilesOnStart, "Memory doesn't say it's quick");
        dialog.SelectedPlugin = dialog.PluginChoices.Single(p => p.TypeId == "quick");
        Assert.IsTrue(dialog.CountFilesOnStart, "Quick does");
        dialog.CountFilesOnStart = false;
        dialog.SelectedPlugin = dialog.PluginChoices.Single(p => p.TypeId == "memory");
        dialog.SelectedPlugin = dialog.PluginChoices.Single(p => p.TypeId == "quick");
        Assert.IsFalse(dialog.CountFilesOnStart, "toggled by hand: stops following the plugin");

        _engine.Settings.App.CountFilesOnStart = false;
        var forced = new SourceDialogViewModel(_engine, _dialogs, null, null, askPath: true);
        forced.SelectedPlugin = forced.PluginChoices.Single(p => p.TypeId == "quick");
        Assert.IsFalse(forced.CountFilesOnStart, "app.json's countFilesOnStart overrides the plugin");
        _engine.Settings.App.CountFilesOnStart = null;
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
    public void OpenerBuildsPathsAndUrls()
    {
        Assert.AreEqual("http://h/work/mem/alpha/", ShellOpener.AppendUrl("http://h/work/", "mem/alpha"));
        Assert.AreEqual("http://h/work/", ShellOpener.AppendUrl("http://h/work/", null));
        Assert.AreEqual("http://h/work/a%20b/", ShellOpener.AppendUrl("http://h/work", "a b"));
        var expected = @"\\anyfs\work" + Path.DirectorySeparatorChar + "mem" + Path.DirectorySeparatorChar + "alpha";
        Assert.AreEqual(expected, ShellOpener.AppendPath(@"\\anyfs\work", "mem/alpha"));
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

    private InstallableProvider _installable = null!;

    [TestMethod]
    public async Task InstallButtonInstallsTheProviderAndRetriesTheMount()
    {
        await AddCollectionAsync("work");
        _dialogs.OnMount = d =>
        {
            d.SelectedProvider = d.Providers.Single(p => p.TypeId == "needy");
            Assert.IsTrue(d.ProviderUnavailable, "the dialog shows why");
            Assert.IsTrue(d.CanInstall, "and offers the install");
            d.Form!.Fields.Single(f => f.PropertyName == "Port").Text = "7701";
            return Task.FromResult(true);
        };
        await Collections.AddMountCommand.ExecuteAsync(null);
        _vm.Refresh();
        var tile = Collections.Mounts.Single(m => m.ProviderId == "needy");
        Assert.AreEqual(StatusColour.Red, tile.Colour);
        StringAssert.Contains(tile.StatusText, "not installed");
        Assert.IsTrue(tile.CanInstall);
        Assert.AreEqual("Install Needy", tile.InstallText);
        Assert.IsTrue(tile.CanRetry, "a failed mount can be retried");

        await tile.InstallCommand.ExecuteAsync(null);
        await _vm.Operations.Last;
        Assert.AreEqual(1, _installable.Installs);
        Assert.IsTrue(_installable.Installed);
        await Wait.UntilAsync(() => { _vm.Refresh(); return tile.Colour == StatusColour.Green; }, what: "the mount restarted after the install");
        Assert.IsFalse(tile.CanInstall, "nothing left to install");
        Assert.IsFalse(tile.Installing);
    }

    [TestMethod]
    public async Task MountDialogInstallsFromTheUnavailablePanel()
    {
        var runner = _engine.Collections.Single();
        var dialog = new MountDialogViewModel(_engine, _dialogs, runner, null);
        dialog.SelectedProvider = dialog.Providers.Single(p => p.TypeId == "needy");
        Assert.IsTrue(dialog.ProviderUnavailable);
        Assert.IsTrue(dialog.CanInstall);
        Assert.AreEqual("Install Needy", dialog.SelectedProvider!.InstallText);
        await dialog.InstallCommand.ExecuteAsync(null);
        Assert.IsNull(dialog.Error);
        Assert.IsFalse(dialog.ProviderUnavailable, "installed: the reason is gone");
        Assert.IsFalse(dialog.CanInstall);
        Assert.AreEqual(1, _installable.Installs);
    }

    [TestMethod]
    public void SourceDialogFiltersTheTypesByText()
    {
        var dialog = new SourceDialogViewModel(_engine, _dialogs, null, null, askPath: true);
        CollectionAssert.AreEqual(new[] { "memory", "named", "quick" }, dialog.VisiblePlugins.Select(p => p.TypeId).ToList());
        dialog.Filter = "QUICK";
        CollectionAssert.AreEqual(new[] { "quick" }, dialog.VisiblePlugins.Select(p => p.TypeId).ToList(), "name, case-insensitively");
        Assert.AreEqual("quick", dialog.SelectedPlugin!.TypeId, "a filtered-out selection moves to the first visible type");
        dialog.Filter = "in-memory";
        CollectionAssert.AreEqual(new[] { "memory" }, dialog.VisiblePlugins.Select(p => p.TypeId).ToList(), "description");
        var form = dialog.Form;
        dialog.Filter = "";
        Assert.AreEqual(3, dialog.VisiblePlugins.Count);
        Assert.AreSame(form, dialog.Form, "clearing the filter keeps the form of the selected type");
        dialog.Filter = "nothing like this";
        Assert.AreEqual(0, dialog.VisiblePlugins.Count);
        Assert.IsNull(dialog.SelectedPlugin);
        Assert.IsFalse(dialog.Validate());
    }

    [TestMethod]
    public void MountDialogFiltersByTextAndPills()
    {
        var runner = _engine.Collections.Single();
        var dialog = new MountDialogViewModel(_engine, _dialogs, runner, null);
        CollectionAssert.AreEquivalent(new[] { "fake", "needy" }, dialog.VisibleProviders.Select(p => p.TypeId).ToList());
        dialog.HighPerformanceOnly = true;
        CollectionAssert.AreEqual(new[] { "needy" }, dialog.VisibleProviders.Select(p => p.TypeId).ToList(), "Needy is Native, Fake is Protocol");
        dialog.NoAdminOnly = true;
        Assert.AreEqual(1, dialog.VisibleProviders.Count, "Needy says it needs no administrator");
        dialog.HighPerformanceOnly = false;
        dialog.Filter = "records";
        CollectionAssert.AreEqual(new[] { "fake" }, dialog.VisibleProviders.Select(p => p.TypeId).ToList(), "description text");
        Assert.AreEqual("fake", dialog.SelectedProvider!.TypeId);
        dialog.Filter = "";
        dialog.NoAdminOnly = false;
        Assert.AreEqual(2, dialog.VisibleProviders.Count);
    }

    /// <summary>A Fake mount provider that is "not installed" until its Install runs (the WinFsp shape, N6).</summary>
    private sealed class InstallableProvider : IMountProviderPlugin, IInstallableProvider
    {
        public bool Installed { get; set; }

        public int Installs { get; private set; }

        public string TypeId => "needy";

        public string DisplayName => "Needy";

        public string Description => "needs installing";

        public Stream? OpenIcon() => null;

        public IReadOnlyList<System.Runtime.InteropServices.OSPlatform> SupportedPlatforms { get; } = [System.Runtime.InteropServices.OSPlatform.Windows, System.Runtime.InteropServices.OSPlatform.Linux, System.Runtime.InteropServices.OSPlatform.OSX];

        public string? UnavailableReason => Installed ? null : "Needy is not installed";

        public PerformanceTier Tier => PerformanceTier.Native;

        public bool SupportsWrites => true;

        public Type SettingsType => typeof(FakeMountSettings);

        public object CreateDefaultSettings(ICollectionInfo collection) => new FakeMountSettings();

        public IMount CreateMount(object settings, ICollectionInfo collection) => new FakeMount((FakeMountSettings)settings, collection);

        public string InstallText => "Install Needy";

        public string InstallDetail => "Pretends to install";

        public bool CanInstall => true;

        public Task<InstallResult> InstallAsync(IProgress<string> progress, CancellationToken ct)
        {
            progress.Report("installing");
            Installed = true;
            Installs++;
            return Task.FromResult(new InstallResult(true, "done"));
        }
    }

    /// <summary>A Memory source that says it is quick to count, so "Count files on start" defaults on for it.</summary>
    private sealed class QuickPlugin : ISourcePlugin
    {
        private readonly MemoryPlugin _inner = new();

        public string TypeId => "quick";

        public string DisplayName => "Quick";

        public string Description => "quick to count";

        public string TypeFolder => "quick";

        public Stream? OpenIcon() => null;

        public Type SettingsType => _inner.SettingsType;

        public bool IsQuickToCount => true;

        public object CreateDefaultSettings() => _inner.CreateDefaultSettings();

        public ISource CreateSource(object settings) => _inner.CreateSource(settings);
    }

    /// <summary>A Memory source with a server to name it after, which the Add source dialog asks first.</summary>
    private sealed class NamedPlugin : ISourcePlugin
    {
        private readonly MemoryPlugin _inner = new();

        public string TypeId => "named";

        public string DisplayName => "Named";

        public string Description => "named after its server";

        public string TypeFolder => "named";

        public Stream? OpenIcon() => null;

        public Type SettingsType => typeof(NamedServerSettings);

        public object CreateDefaultSettings() => new NamedServerSettings();

        public ISource CreateSource(object settings) => _inner.CreateSource(_inner.CreateDefaultSettings());

        public string? SuggestName(object settings) => ((NamedServerSettings)settings).Server is { Length: > 0 } server ? server.Split('.')[0] : null;
    }
}

public sealed class NamedServerSettings
{
    [Setting("Share", Order = 0)]
    public string? Share { get; set; }

    [Setting("Server", Required = true, Primary = true, Order = 1)]
    public string Server { get; set; } = "";
}
