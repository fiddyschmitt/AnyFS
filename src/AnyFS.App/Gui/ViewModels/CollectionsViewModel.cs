using System.Collections.ObjectModel;
using System.Windows.Input;
using AnyFS.App.Gui.Services;
using AnyFS.Core.Model;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Core.Status;
using AnyFS.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnyFS.App.Gui.ViewModels;

/// <summary>An "Existing source ▸" entry: a source not yet in the selected collection.</summary>
public sealed class ExistingSourceChoiceViewModel(Guid id, string name, string typeName, ICommand addCommand)
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public string Header { get; } = $"{name}  ({typeName})";

    public ICommand AddCommand { get; } = addCommand;
}

/// <summary>The Collections tab (spec 11.1): three tile lists, Add/Edit/Delete under each.</summary>
public sealed partial class CollectionsViewModel : ObservableObject
{
    private readonly AnyFsEngine _engine;
    private readonly IDialogs _dialogs;
    private readonly IOpener _opener;
    private readonly ITextClipboard _clipboard;
    private readonly Operations _ops;
    private StatusSnapshot? _lastSnapshot;
    private Guid? _selectAfterRefresh;

    public CollectionsViewModel(AnyFsEngine engine, IDialogs dialogs, IOpener opener, ITextClipboard clipboard, Operations ops)
    {
        _engine = engine;
        _dialogs = dialogs;
        _opener = opener;
        _clipboard = clipboard;
        _ops = ops;
    }

    public ObservableCollection<CollectionTileViewModel> Collections { get; } = [];

    public ObservableCollection<SourceTileViewModel> Sources { get; } = [];

    public ObservableCollection<MountTileViewModel> Mounts { get; } = [];

    public ObservableCollection<ExistingSourceChoiceViewModel> ExistingSources { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCollection))]
    [NotifyCanExecuteChangedFor(nameof(EditCollectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCollectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddNewSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddMountCommand))]
    public partial CollectionTileViewModel? SelectedCollection { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSource))]
    [NotifyCanExecuteChangedFor(nameof(EditSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSourceCommand))]
    public partial SourceTileViewModel? SelectedSource { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedMount))]
    [NotifyCanExecuteChangedFor(nameof(EditMountCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteMountCommand))]
    public partial MountTileViewModel? SelectedMount { get; set; }

    public bool HasSelectedCollection => SelectedCollection is not null;

    public bool HasSelectedSource => SelectedSource is not null;

    public bool HasSelectedMount => SelectedMount is not null;

    public bool HasExistingSources => ExistingSources.Count > 0;

    /// <summary>Brings every list in step with the engine. Called by the window on a timer and after changes.</summary>
    public void Refresh(StatusSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        ListSync.Sync(
            Collections,
            snapshot.Collections,
            c => c.Id,
            vm => vm.Id,
            c => new CollectionTileViewModel(c.Id, enabled => _ops.Run("Enable collection", () => _engine.SetCollectionEnabledAsync(c.Id, enabled))),
            (vm, c) => vm.Update(c, CollectionOpenTargets(c.Id, null)));

        if (_selectAfterRefresh is { } wanted && Collections.FirstOrDefault(c => c.Id == wanted) is { } chosen)
        {
            _selectAfterRefresh = null;
            SelectedCollection = chosen;
        }
        else if (SelectedCollection is null || !Collections.Contains(SelectedCollection))
        {
            SelectedCollection = Collections.FirstOrDefault();
        }

        RefreshSelected();
    }

    partial void OnSelectedCollectionChanged(CollectionTileViewModel? value) => RefreshSelected();

    private void RefreshSelected()
    {
        var status = SelectedCollection is null ? null : _lastSnapshot?.Collections.FirstOrDefault(c => c.Id == SelectedCollection.Id);
        var collectionId = status?.Id ?? Guid.Empty;
        var runner = status is null ? null : _engine.FindCollectionRunner(status.Id);

        ListSync.Sync(
            Sources,
            status?.Sources ?? [],
            s => s.Id,
            vm => vm.Id,
            s => new SourceTileViewModel(
                s.Id,
                showPath: true,
                enabled => _ops.Run("Enable source", () => _engine.SetSourceEnabledAsync(s.Id, enabled)),
                readOnly => _ops.Run("Read-only", () => SetMembershipReadOnlyAsync(collectionId, s.Id, readOnly)),
                () => _ops.Run("Retry", () => _engine.RetrySourceAsync(s.Id))),
            (vm, s) =>
            {
                var handle = runner?.Sources.FirstOrDefault(x => x.Handle.Id == s.Id)?.Handle;
                var locked = handle is { Current: not null } && !handle.Capabilities.CanWrite;
                vm.Update(s, TypeNameOf(s.Type), locked, [], CollectionOpenTargets(collectionId, s.Path));
            });

        ListSync.Sync(
            Mounts,
            status?.Mounts ?? [],
            m => m.Id,
            vm => vm.Id,
            m => new MountTileViewModel(m.Id, enabled => _ops.Run("Enable mount", () => _engine.SetMountEnabledAsync(collectionId, m.Id, enabled)), _clipboard),
            (vm, m) => vm.Update(m, ProviderNameOf(m.Provider), MountOpenTargets(runner, m.Id, null)));

        if (SelectedSource is not null && !Sources.Contains(SelectedSource))
        {
            SelectedSource = null;
        }

        if (SelectedMount is not null && !Mounts.Contains(SelectedMount))
        {
            SelectedMount = null;
        }

        var members = status?.Sources.Select(s => s.Id).ToHashSet() ?? [];
        var candidates = status is null
            ? []
            : _engine.Settings.Sources.Where(s => !members.Contains(s.Id)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (!candidates.Select(c => c.Id).SequenceEqual(ExistingSources.Select(c => c.Id)))
        {
            ExistingSources.Clear();
            foreach (var candidate in candidates)
            {
                var id = candidate.Id;
                ExistingSources.Add(new ExistingSourceChoiceViewModel(id, candidate.Name, TypeNameOf(candidate.Type), new AsyncRelayCommand(() => AddExistingSourceAsync(id))));
            }

            OnPropertyChanged(nameof(HasExistingSources));
        }
    }

    // ----- collections -----

    [RelayCommand]
    private async Task AddCollectionAsync()
    {
        var dialog = new CollectionDialogViewModel("Add collection", "");
        dialog.OnAccept = async () =>
        {
            var definition = await _engine.AddCollectionAsync(dialog.Name);
            _selectAfterRefresh = definition.Id;
            return true;
        };
        await _dialogs.ShowAsync(dialog);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedCollection))]
    private async Task EditCollectionAsync()
    {
        if (SelectedCollection is not { } selected)
        {
            return;
        }

        var dialog = new CollectionDialogViewModel("Rename collection", selected.Name);
        dialog.OnAccept = async () =>
        {
            await _engine.RenameCollectionAsync(selected.Id, dialog.Name);
            return true;
        };
        await _dialogs.ShowAsync(dialog);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedCollection))]
    private async Task DeleteCollectionAsync()
    {
        if (SelectedCollection is not { } selected || _engine.Settings.Collections.FirstOrDefault(c => c.Id == selected.Id) is not { } definition)
        {
            return;
        }

        var orphans = definition.Sources
            .Select(m => _engine.Settings.FindSource(m.Id))
            .Where(s => s is not null && !_engine.Settings.UsesOf(s.Id).Any(u => u.Collection.Id != definition.Id))
            .Select(s => s!)
            .ToList();
        var message = $"Delete the collection '{definition.Name}'?";
        if (definition.Mounts.Count > 0)
        {
            message += $"\n\nIts mounts stop and are removed: {string.Join(", ", definition.Mounts.Select(m => m.Name))}.";
        }

        message += definition.Sources.Count == 0 ? "\n\nIt has no sources." : "\n\nSources stay defined and keep their place in other collections.";
        var checkbox = orphans.Count == 0 ? null : $"Also delete {Plural(orphans.Count, "source definition")} no other collection uses: {string.Join(", ", orphans.Select(s => s.Name))}";
        var answer = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel("Delete collection", message, "Delete", checkboxText: checkbox));
        if (answer.Button != ConfirmButton.Yes)
        {
            return;
        }

        await _ops.Run("Delete collection", async () =>
        {
            await _engine.DeleteCollectionAsync(definition.Id);
            if (answer.Checked)
            {
                foreach (var orphan in orphans)
                {
                    await _engine.DeleteSourceAsync(orphan.Id);
                }
            }
        });
    }

    // ----- sources -----

    [RelayCommand(CanExecute = nameof(HasSelectedCollection))]
    private async Task AddNewSourceAsync()
    {
        if (SelectedCollection is not { } selected)
        {
            return;
        }

        var dialog = new SourceDialogViewModel(_engine, _dialogs, null, null, askPath: true);
        dialog.OnAccept = async () =>
        {
            if (!dialog.Validate())
            {
                return false;
            }

            var definition = await _engine.AddSourceAsync(dialog.Name, dialog.SelectedPlugin!.TypeId, dialog.Enabled, dialog.CountFilesOnStart, dialog.Settings!);
            await _engine.AddMembershipAsync(selected.Id, definition.Id, dialog.Path, dialog.ReadOnly);
            return true;
        };
        await _dialogs.ShowAsync(dialog);
    }

    public async Task AddExistingSourceAsync(Guid sourceId)
    {
        if (SelectedCollection is not { } selected || _engine.Settings.FindSource(sourceId) is not { } source)
        {
            return;
        }

        var plugin = _engine.Registry.FindSource(source.Type);
        var defaultPath = plugin is null ? source.Name : SettingsStore.DefaultSourcePath(plugin, source.Name);
        var dialog = new ExistingSourceDialogViewModel(source.Name, defaultPath, selected.Name);
        dialog.OnAccept = async () =>
        {
            if (!dialog.Validate())
            {
                return false;
            }

            await _engine.AddMembershipAsync(selected.Id, sourceId, dialog.Path, dialog.ReadOnly);
            return true;
        };
        await _dialogs.ShowAsync(dialog);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSource))]
    private async Task EditSourceAsync()
    {
        if (SelectedCollection is not { } collection || SelectedSource is not { } selected
            || _engine.Settings.FindSource(selected.Id) is not { } definition
            || _engine.Settings.Collections.FirstOrDefault(c => c.Id == collection.Id)?.Sources.FirstOrDefault(m => m.Id == selected.Id) is not { } membership)
        {
            return;
        }

        var dialog = new SourceDialogViewModel(_engine, _dialogs, definition, membership, askPath: true, readOnlyLocked: selected.ReadOnlyLocked);
        dialog.OnAccept = async () =>
        {
            if (!dialog.Validate())
            {
                return false;
            }

            await _engine.UpdateSourceAsync(definition.Id, dialog.Name, dialog.Enabled, dialog.CountFilesOnStart, dialog.Settings!);
            await _engine.UpdateMembershipAsync(collection.Id, definition.Id, dialog.Path, dialog.ReadOnly);
            return true;
        };
        await _dialogs.ShowAsync(dialog);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSource))]
    private async Task DeleteSourceAsync()
    {
        if (SelectedCollection is not { } collection || SelectedSource is not { } selected || _engine.Settings.FindSource(selected.Id) is not { } definition)
        {
            return;
        }

        var usedElsewhere = _engine.Settings.UsesOf(definition.Id).Any(u => u.Collection.Id != collection.Id);
        var message = $"Remove '{definition.Name}' from the collection '{collection.Name}'?";
        var checkbox = usedElsewhere ? null : "Also delete the source definition (no other collection uses it)";
        if (usedElsewhere)
        {
            message += "\n\nIt stays in: " + string.Join(", ", _engine.Settings.UsesOf(definition.Id).Where(u => u.Collection.Id != collection.Id).Select(u => u.Collection.Name)) + ".";
        }

        var answer = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel("Remove source", message, "Remove", checkboxText: checkbox));
        if (answer.Button != ConfirmButton.Yes)
        {
            return;
        }

        await _ops.Run("Remove source", async () =>
        {
            await _engine.RemoveMembershipAsync(collection.Id, definition.Id);
            if (answer.Checked && !usedElsewhere)
            {
                await _engine.DeleteSourceAsync(definition.Id);
            }
        });
    }

    private Task SetMembershipReadOnlyAsync(Guid collectionId, Guid sourceId, bool readOnly)
    {
        var membership = _engine.Settings.Collections.FirstOrDefault(c => c.Id == collectionId)?.Sources.FirstOrDefault(m => m.Id == sourceId);
        return membership is null ? Task.CompletedTask : _engine.UpdateMembershipAsync(collectionId, sourceId, membership.Path, readOnly);
    }

    // ----- mounts -----

    [RelayCommand(CanExecute = nameof(HasSelectedCollection))]
    private async Task AddMountAsync()
    {
        if (SelectedCollection is not { } selected || _engine.FindCollectionRunner(selected.Id) is not { } runner)
        {
            return;
        }

        var dialog = new MountDialogViewModel(_engine, _dialogs, runner, null);
        dialog.OnAccept = async () =>
        {
            if (!dialog.Validate())
            {
                return false;
            }

            await _engine.AddMountAsync(selected.Id, dialog.Name, dialog.SelectedProvider!.TypeId, dialog.Enabled, dialog.ReadOnly, dialog.Settings!);
            return true;
        };
        await _dialogs.ShowAsync(dialog);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMount))]
    private async Task EditMountAsync()
    {
        if (SelectedCollection is not { } collection || SelectedMount is not { } selected
            || _engine.FindCollectionRunner(collection.Id) is not { } runner
            || runner.Definition.Mounts.FirstOrDefault(m => m.Id == selected.Id) is not { } definition)
        {
            return;
        }

        var dialog = new MountDialogViewModel(_engine, _dialogs, runner, definition);
        dialog.OnAccept = async () =>
        {
            if (!dialog.Validate())
            {
                return false;
            }

            await _engine.UpdateMountAsync(collection.Id, definition.Id, dialog.Name, dialog.Enabled, dialog.ReadOnly, dialog.Settings!);
            return true;
        };
        await _dialogs.ShowAsync(dialog);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedMount))]
    private async Task DeleteMountAsync()
    {
        if (SelectedCollection is not { } collection || SelectedMount is not { } selected)
        {
            return;
        }

        var answer = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel("Delete mount", $"Delete the mount '{selected.Name}' from '{collection.Name}'? It stops at once.", "Delete"));
        if (answer.Button == ConfirmButton.Yes)
        {
            await _ops.Run("Delete mount", () => _engine.DeleteMountAsync(collection.Id, selected.Id));
        }
    }

    // ----- helpers -----

    /// <summary>Open containing folder for a collection (its root) or a source (its path inside every running mount).</summary>
    private List<OpenTargetViewModel> CollectionOpenTargets(Guid collectionId, string? sourcePath)
    {
        var runner = _engine.FindCollectionRunner(collectionId);
        return runner is null ? [] : runner.Mounts.SelectMany(m => MountOpenTargets(runner, m.Definition.Id, sourcePath)).ToList();
    }

    private List<OpenTargetViewModel> MountOpenTargets(CollectionRunner? runner, Guid mountId, string? sourcePath)
    {
        var mount = runner?.Mounts.FirstOrDefault(m => m.Definition.Id == mountId);
        if (mount?.Mount?.OpenAction is not { } action)
        {
            return [];
        }

        var detail = mount.AccessPaths.Count > 0 ? mount.AccessPaths[0].Value : "";
        return [new OpenTargetViewModel(mount.Definition.Name, detail, action, string.IsNullOrEmpty(sourcePath) ? null : sourcePath, _opener)];
    }

    private string TypeNameOf(string typeId) => _engine.Registry.FindSource(typeId)?.DisplayName ?? typeId;

    private string ProviderNameOf(string providerId) => _engine.Registry.FindMountProvider(providerId)?.DisplayName ?? SettingsStore.DefaultMountName(providerId);

    private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
