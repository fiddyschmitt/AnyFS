using System.Collections.ObjectModel;
using AnyFS.App.Gui.Services;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Core.Status;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnyFS.App.Gui.ViewModels;

/// <summary>The All sources tab (spec 11.2): every source, with the collections using it; Import... and Export... (spec 9.5).</summary>
public sealed partial class AllSourcesViewModel : ObservableObject
{
    private readonly AnyFsEngine _engine;
    private readonly IDialogs _dialogs;
    private readonly Operations _ops;

    public AllSourcesViewModel(AnyFsEngine engine, IDialogs dialogs, Operations ops)
    {
        _engine = engine;
        _dialogs = dialogs;
        _ops = ops;
    }

    public ObservableCollection<SourceTileViewModel> Sources { get; } = [];

    public bool HasSources => Sources.Count > 0;

    /// <summary>Export... : tick sources, pick a file, write it (never with secrets).</summary>
    [RelayCommand(CanExecute = nameof(HasSources))]
    private async Task ExportSourcesAsync()
    {
        var dialog = new ExportSourcesDialogViewModel(_engine.Settings.Sources
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => (s.Id, s.Name, TypeNameOf(s.Type))));
        dialog.OnAccept = async () =>
        {
            if (!dialog.Validate())
            {
                return false;
            }

            var path = await _dialogs.PickSaveFileAsync("sources.json");
            if (path is null)
            {
                return false;
            }

            _engine.ExportSources(dialog.ChosenIds, path);
            return true;
        };
        await _dialogs.ShowAsync(dialog);
    }

    /// <summary>Import... : a sources file, merged by id; a clash asks replace, keep or copy.</summary>
    [RelayCommand]
    private async Task ImportSourcesAsync()
    {
        var path = await _dialogs.PickFileAsync(null);
        if (path is null)
        {
            return;
        }

        await _ops.Run("Import sources", async () =>
        {
            var incoming = SourceTransfer.Read(path);
            if (incoming.Count == 0)
            {
                await _dialogs.ShowMessageAsync("Import sources", "There are no sources in that file.");
                return;
            }

            var summary = await _engine.ImportSourcesAsync(incoming, async (existing, imported) =>
            {
                var choice = await _dialogs.ChooseAsync(
                    $"'{imported.Name}' is already here",
                    $"A source with the same id exists: '{existing.Name}' ({TypeNameOf(existing.Type)}). The file has '{imported.Name}' ({TypeNameOf(imported.Type)}).",
                    ["Replace mine with the file's", "Keep mine", "Import the file's as a copy"]);
                return choice switch
                {
                    0 => ImportConflict.Replace,
                    2 => ImportConflict.Copy,
                    _ => ImportConflict.Keep,
                };
            });

            var needsSecrets = incoming
                .Select(s => _engine.Registry.FindSource(s.Type))
                .Any(plugin => plugin is not null && PluginSettingsBinder.Describe(plugin.SettingsType).Any(p => p.IsSecret));
            var message = $"{summary.Describe()}.";
            if (needsSecrets && summary.Added + summary.Replaced + summary.Copied > 0)
            {
                message += "\n\nPasswords and keys are never in a sources file: edit an imported source to add them, or answer its prompt when it starts.";
            }

            await _dialogs.ShowMessageAsync("Import sources", message);
        });
    }

    private string TypeNameOf(string typeId) => _engine.Registry.FindSource(typeId)?.DisplayName ?? typeId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSource))]
    [NotifyCanExecuteChangedFor(nameof(EditSourceCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSourceCommand))]
    public partial SourceTileViewModel? SelectedSource { get; set; }

    public bool HasSelectedSource => SelectedSource is not null;

    public bool IsEmpty => Sources.Count == 0;

    public void Refresh()
    {
        var definitions = _engine.Settings.Sources.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var statuses = definitions.Select(d =>
        {
            var runner = _engine.FindSourceRunner(d.Id);
            return runner is null
                ? new SourceStatus { Id = d.Id, Name = d.Name, Type = d.Type, Enabled = d.Enabled, State = nameof(ComponentState.Disabled), Text = d.Enabled ? "Not started" : "Disabled" }
                : AnyFsEngine.SourceStatusOf(runner.Handle, d, null);
        }).ToList();

        ListSync.Sync(
            Sources,
            statuses,
            s => s.Id,
            vm => vm.Id,
            s => new SourceTileViewModel(
                s.Id,
                showPath: false,
                enabled => _ops.Run("Enable source", () => _engine.SetSourceEnabledAsync(s.Id, enabled)),
                setReadOnly: null,
                () => _ops.Run("Retry", () => _engine.RetrySourceAsync(s.Id))),
            (vm, s) =>
            {
                var handle = _engine.FindSourceRunner(s.Id)?.Handle;
                var locked = handle is { Current: not null } && !handle.Capabilities.CanWrite;
                var uses = _engine.Settings.UsesOf(s.Id)
                    .OrderBy(u => u.Collection.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(u => $"{u.Collection.Name}: {(u.Membership.Path.Length == 0 ? "(root)" : u.Membership.Path)}{(u.Membership.ReadOnly ? "  [read-only]" : "")}")
                    .ToList();
                vm.Update(s, _engine.Registry.FindSource(s.Type)?.DisplayName ?? s.Type, locked, uses.Count == 0 ? ["Not in any collection"] : uses, []);
            });

        if (SelectedSource is not null && !Sources.Contains(SelectedSource))
        {
            SelectedSource = null;
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSources));
        ExportSourcesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task AddSourceAsync()
    {
        var dialog = new SourceDialogViewModel(_engine, _dialogs, null, null, askPath: false);
        dialog.OnAccept = async () =>
        {
            if (!dialog.Validate())
            {
                return false;
            }

            await _engine.AddSourceAsync(dialog.Name, dialog.SelectedPlugin!.TypeId, dialog.Enabled, dialog.CountFilesOnStart, dialog.Settings!);
            return true;
        };
        await _dialogs.ShowAsync(dialog);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSource))]
    private async Task EditSourceAsync()
    {
        if (SelectedSource is not { } selected || _engine.Settings.FindSource(selected.Id) is not { } definition)
        {
            return;
        }

        var dialog = new SourceDialogViewModel(_engine, _dialogs, definition, null, askPath: false, readOnlyLocked: selected.ReadOnlyLocked);
        dialog.OnAccept = async () =>
        {
            if (!dialog.Validate())
            {
                return false;
            }

            await _engine.UpdateSourceAsync(definition.Id, dialog.Name, dialog.Enabled, dialog.CountFilesOnStart, dialog.Settings!);
            return true;
        };
        await _dialogs.ShowAsync(dialog);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSource))]
    private async Task DeleteSourceAsync()
    {
        if (SelectedSource is not { } selected || _engine.Settings.FindSource(selected.Id) is not { } definition)
        {
            return;
        }

        var uses = _engine.Settings.UsesOf(definition.Id).Select(u => u.Collection.Name).ToList();
        var message = $"Delete the source '{definition.Name}' everywhere?";
        message += uses.Count == 0 ? "\n\nNo collection uses it." : $"\n\nIt is removed from: {string.Join(", ", uses)}.";
        message += "\n\nIts settings and secrets are deleted too.";
        var answer = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel("Delete source", message, "Delete"));
        if (answer.Button == ConfirmButton.Yes)
        {
            await _ops.Run("Delete source", () => _engine.DeleteSourceAsync(definition.Id));
        }
    }
}
