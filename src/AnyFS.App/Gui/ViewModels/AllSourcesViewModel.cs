using System.Collections.ObjectModel;
using AnyFS.App.Gui.Services;
using AnyFS.Core.Runtime;
using AnyFS.Core.Status;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnyFS.App.Gui.ViewModels;

/// <summary>The All sources tab (spec 11.2): every source, with the collections using it. Import/Export arrive with milestone 4.</summary>
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
