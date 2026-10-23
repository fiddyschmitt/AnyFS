using System.Collections.ObjectModel;
using System.Windows.Input;
using AnyFS.App.Gui.Services;
using AnyFS.Core.Status;
using AnyFS.Sdk;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnyFS.App.Gui.ViewModels;

/// <summary>One entry of an Open containing folder flyout: a mount and where to open it.</summary>
public sealed class OpenTargetViewModel
{
    public OpenTargetViewModel(string name, string detail, MountOpenAction action, string? subPath, IOpener opener)
    {
        Name = name;
        Detail = detail;
        Action = action;
        SubPath = subPath;
        OpenCommand = new RelayCommand(() => opener.Open(action, subPath));
    }

    public string Name { get; }

    public string Detail { get; }

    public string Header => Detail.Length == 0 ? Name : $"{Name}   {Detail}";

    public MountOpenAction Action { get; }

    public string? SubPath { get; }

    public ICommand OpenCommand { get; }

    public bool SameAs(OpenTargetViewModel other)
        => Name == other.Name && Detail == other.Detail && SubPath == other.SubPath && Action.Equals(other.Action);
}

/// <summary>What every tile has: a name, a status circle with text, and the Open containing folder button (spec 11.1).</summary>
public abstract partial class TileViewModel : ObservableObject
{
    protected TileViewModel(Guid id)
    {
        Id = id;
        OpenSingleCommand = new RelayCommand(() => OpenTargets.FirstOrDefault()?.OpenCommand.Execute(null), () => OpenTargets.Count == 1);
    }

    public Guid Id { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial StatusColour Colour { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    /// <summary>The whole message, for the tooltip (a Failed tile's text is the short reason, spec 11.4).</summary>
    [ObservableProperty]
    public partial string FullStatus { get; set; } = "";

    public ObservableCollection<OpenTargetViewModel> OpenTargets { get; } = [];

    public RelayCommand OpenSingleCommand { get; }

    public bool HasOneOpenTarget => OpenTargets.Count == 1;

    public bool HasManyOpenTargets => OpenTargets.Count > 1;

    public bool HasNoOpenTarget => OpenTargets.Count == 0;

    protected void SetOpenTargets(IReadOnlyList<OpenTargetViewModel> targets)
    {
        if (targets.Count == OpenTargets.Count && targets.Zip(OpenTargets).All(p => p.First.SameAs(p.Second)))
        {
            return;
        }

        OpenTargets.Clear();
        foreach (var target in targets)
        {
            OpenTargets.Add(target);
        }

        OpenSingleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasOneOpenTarget));
        OnPropertyChanged(nameof(HasManyOpenTargets));
        OnPropertyChanged(nameof(HasNoOpenTarget));
    }

    protected void SetStatus(string state, string text, string? extra = null)
    {
        var parsed = Enum.TryParse<ComponentState>(state, out var s) ? s : ComponentState.Failed;
        Colour = ComponentStatus.ColourOf(parsed);
        StatusText = text.Length == 0 ? state : text;
        FullStatus = (text.Length == 0 || text.Equals(state, StringComparison.OrdinalIgnoreCase) ? state : $"{state}: {text}") + (extra is null ? "" : "\n" + extra);
    }
}

public sealed partial class CollectionTileViewModel : TileViewModel
{
    private readonly Func<bool, Task> _setEnabled;
    private bool _syncing;

    public CollectionTileViewModel(Guid id, Func<bool, Task> setEnabled)
        : base(id)
    {
        _setEnabled = setEnabled;
    }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial string FilesText { get; set; } = "";

    [ObservableProperty]
    public partial bool Counting { get; set; }

    public void Update(CollectionStatus status, IReadOnlyList<OpenTargetViewModel> targets)
    {
        _syncing = true;
        try
        {
            Name = status.Name;
            Enabled = status.Enabled;
            Colour = Enum.TryParse<StatusColour>(status.Colour, out var c) ? c : StatusColour.Grey;
            StatusText = status.Text;
            FullStatus = status.Text;
            var enabledSources = status.Sources.Where(s => s.Enabled).ToList();
            if (enabledSources.Count == 0)
            {
                FilesText = "—";
                Counting = false;
            }
            else
            {
                FilesText = Format.Files(enabledSources.Sum(s => s.Files), enabledSources.All(s => s.FilesComplete));
                Counting = enabledSources.Any(s => s.Counting);
            }

            SetOpenTargets(targets);
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!_syncing)
        {
            _ = _setEnabled(value);
        }
    }
}

public sealed partial class SourceTileViewModel : TileViewModel
{
    private readonly Func<bool, Task> _setEnabled;
    private readonly Func<bool, Task>? _setReadOnly;
    private readonly Func<Task> _retry;
    private bool _syncing;

    public SourceTileViewModel(Guid id, bool showPath, Func<bool, Task> setEnabled, Func<bool, Task>? setReadOnly, Func<Task> retry)
        : base(id)
    {
        ShowPath = showPath;
        ShowReadOnly = setReadOnly is not null;
        _setEnabled = setEnabled;
        _setReadOnly = setReadOnly;
        _retry = retry;
        RetryCommand = new AsyncRelayCommand(() => _retry(), () => CanRetry);
    }

    /// <summary>The collection's lists show the source path; the All sources tab shows where it's used instead.</summary>
    public bool ShowPath { get; }

    public bool ShowReadOnly { get; }

    public bool ShowUses => !ShowPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconKey))]
    public partial string TypeId { get; set; } = "";

    /// <summary>Key for the plugin icon cache ("source:mirror").</summary>
    public string IconKey => "source:" + TypeId;

    [ObservableProperty]
    public partial string TypeName { get; set; } = "";

    /// <summary>
    /// A collection's list is grouped by type: the type's name, shown above the first tile of each type when the collection
    /// has more than one; null for the rest (and on the All sources tab).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroupHeader))]
    public partial string? GroupHeader { get; set; }

    public bool HasGroupHeader => GroupHeader is not null;

    /// <summary>The badge letter when the plugin has no icon.</summary>
    [ObservableProperty]
    public partial string Initial { get; set; } = "?";

    [ObservableProperty]
    public partial string Path { get; set; } = "";

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial bool ReadOnly { get; set; }

    /// <summary>The plugin can't write at all: shown ticked and disabled with a tooltip (spec 11.1).</summary>
    [ObservableProperty]
    public partial bool ReadOnlyLocked { get; set; }

    public string ReadOnlyTooltip => ReadOnlyLocked ? $"{TypeName} sources can't be written to" : "This collection presents the source read-only; other collections may still write to it";

    [ObservableProperty]
    public partial string FilesText { get; set; } = "";

    [ObservableProperty]
    public partial bool Counting { get; set; }

    [ObservableProperty]
    public partial double? Progress { get; set; }

    public bool HasProgress => Progress is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial bool CanRetry { get; set; }

    public AsyncRelayCommand RetryCommand { get; }

    /// <summary>All sources tab: one line per collection using it, with the path there and a read-only mark (spec 11.2).</summary>
    public ObservableCollection<string> Uses { get; } = [];

    public string PathCaption => Path.Length == 0 ? "(collection root)" : Path;

    public void Update(SourceStatus status, string typeName, bool readOnlyLocked, IReadOnlyList<string> uses, IReadOnlyList<OpenTargetViewModel> targets)
    {
        _syncing = true;
        try
        {
            Name = status.Name;
            TypeId = status.Type;
            TypeName = typeName;
            Initial = typeName.Length > 0 ? typeName[..1].ToUpperInvariant() : "?";
            Path = status.Path;
            OnPropertyChanged(nameof(PathCaption));
            Enabled = status.Enabled;
            ReadOnlyLocked = readOnlyLocked;
            ReadOnly = status.ReadOnly || readOnlyLocked;
            OnPropertyChanged(nameof(ReadOnlyTooltip));
            SetStatus(status.State, status.Text);
            Progress = status.Progress;
            OnPropertyChanged(nameof(HasProgress));
            var running = status.State is not (nameof(ComponentState.Disabled) or nameof(ComponentState.Failed) or nameof(ComponentState.Starting));
            FilesText = status.Enabled && (running || status.Files > 0) ? Format.Files(status.Files, status.FilesComplete) : "—";
            Counting = status.Counting;
            CanRetry = status.Enabled && status.State is nameof(ComponentState.Failed) or nameof(ComponentState.Degraded);
            if (!uses.SequenceEqual(Uses))
            {
                Uses.Clear();
                foreach (var use in uses)
                {
                    Uses.Add(use);
                }
            }

            SetOpenTargets(targets);
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!_syncing)
        {
            _ = _setEnabled(value);
        }
    }

    partial void OnReadOnlyChanged(bool value)
    {
        if (!_syncing && _setReadOnly is not null && !ReadOnlyLocked)
        {
            _ = _setReadOnly(value);
        }
    }
}

public sealed class AccessPathViewModel(string label, string value, ITextClipboard clipboard)
{
    public string Label { get; } = label;

    public string Value { get; } = value;

    public IAsyncRelayCommand CopyCommand { get; } = new AsyncRelayCommand(() => clipboard.SetTextAsync(value));
}

/// <summary>What the tile's Install button offers when a provider can install what it is missing (N6).</summary>
public sealed record InstallOffer(string Text, string Detail);

public sealed partial class MountTileViewModel : TileViewModel
{
    private readonly Func<bool, Task> _setEnabled;
    private readonly Func<Task> _retry;
    private readonly Func<IProgress<string>, Task> _install;
    private readonly ITextClipboard _clipboard;
    private bool _syncing;

    public MountTileViewModel(Guid id, Func<bool, Task> setEnabled, Func<Task> retry, Func<IProgress<string>, Task> install, ITextClipboard clipboard)
        : base(id)
    {
        _setEnabled = setEnabled;
        _retry = retry;
        _install = install;
        _clipboard = clipboard;
        RetryCommand = new AsyncRelayCommand(() => _retry(), () => CanRetry);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => CanInstall && !Installing);
    }

    /// <summary>A failed or degraded mount can be started again now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial bool CanRetry { get; set; }

    public AsyncRelayCommand RetryCommand { get; }

    /// <summary>The provider is missing something it can install (WinFsp through winget).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool CanInstall { get; set; }

    [ObservableProperty]
    public partial string InstallText { get; set; } = "";

    [ObservableProperty]
    public partial string InstallDetail { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial bool Installing { get; set; }

    [ObservableProperty]
    public partial string? InstallProgress { get; set; }

    public AsyncRelayCommand InstallCommand { get; }

    private async Task InstallAsync()
    {
        Installing = true;
        InstallProgress = "Starting the installer...";
        try
        {
            await _install(new Progress<string>(line => InstallProgress = line));
        }
        finally
        {
            Installing = false;
            InstallProgress = null;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconKey))]
    public partial string ProviderId { get; set; } = "";

    public string IconKey => "mount:" + ProviderId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProvider))]
    public partial string ProviderName { get; set; } = "";

    /// <summary>The provider is worth a line only when the mount isn't simply named after it ("HTTP" under "HTTP" says nothing).</summary>
    public bool ShowProvider => !Name.Equals(ProviderName, StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    public partial string Initial { get; set; } = "?";

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial bool ReadOnly { get; set; }

    /// <summary>Every access path of the running mount, in the provider's order.</summary>
    public ObservableCollection<AccessPathViewModel> AccessPaths { get; } = [];

    /// <summary>The access paths not already shown as the status line (<see cref="StatusAccessPath"/>), each with its own box and Copy button.</summary>
    public ObservableCollection<AccessPathViewModel> OtherAccessPaths { get; } = [];

    /// <summary>The access path the status line shows (a Ready mount's first one), copied by the button beside it; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusAccessPath))]
    [NotifyPropertyChangedFor(nameof(StatusCopyTooltip))]
    public partial AccessPathViewModel? StatusAccessPath { get; set; }

    public bool HasStatusAccessPath => StatusAccessPath is not null;

    public string StatusCopyTooltip => StatusAccessPath is null ? "" : $"Copy the {StatusAccessPath.Label}";

    public bool HasAccessPaths => AccessPaths.Count > 0;

    public void Update(MountStatus status, string providerName, IReadOnlyList<OpenTargetViewModel> targets, InstallOffer? install = null)
    {
        _syncing = true;
        try
        {
            Name = status.Name;
            ProviderId = status.Provider;
            ProviderName = providerName;
            OnPropertyChanged(nameof(ShowProvider)); // Name lives on the base class, so a rename alone wouldn't notify it
            Initial = providerName.Length > 0 ? providerName[..1].ToUpperInvariant() : "?";
            Enabled = status.Enabled;
            ReadOnly = status.ReadOnly;
            SetStatus(status.State, status.Text);
            CanRetry = status.Enabled && status.State is nameof(ComponentState.Failed) or nameof(ComponentState.Degraded);
            CanInstall = install is not null && status.Enabled;
            InstallText = install?.Text ?? "";
            InstallDetail = install?.Detail ?? "";
            if (!status.AccessPaths.Select(p => (p.Label, p.Value)).SequenceEqual(AccessPaths.Select(p => (p.Label, p.Value))))
            {
                AccessPaths.Clear();
                foreach (var path in status.AccessPaths)
                {
                    AccessPaths.Add(new AccessPathViewModel(path.Label, path.Value, _clipboard));
                }

                OnPropertyChanged(nameof(HasAccessPaths));
            }

            SplitAccessPaths();
            SetOpenTargets(targets);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SplitAccessPaths()
    {
        var shown = AccessPaths.FirstOrDefault(p => p.Value == StatusText);
        if (!ReferenceEquals(shown, StatusAccessPath))
        {
            StatusAccessPath = shown;
        }

        var others = AccessPaths.Where(p => !ReferenceEquals(p, shown)).ToList();
        if (!others.SequenceEqual(OtherAccessPaths))
        {
            OtherAccessPaths.Clear();
            foreach (var path in others)
            {
                OtherAccessPaths.Add(path);
            }
        }
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!_syncing)
        {
            _ = _setEnabled(value);
        }
    }
}

/// <summary>Keeps an observable list in step with a fresh snapshot, by id and in the snapshot's order, touching as little as possible.</summary>
internal static class ListSync
{
    public static void Sync<TVm, TItem>(ObservableCollection<TVm> target, IReadOnlyList<TItem> items, Func<TItem, Guid> key, Func<TVm, Guid> vmKey, Func<TItem, TVm> create, Action<TVm, TItem> update)
        where TVm : class
    {
        var byId = target.ToDictionary(vmKey);
        var desired = new List<TVm>(items.Count);
        foreach (var item in items)
        {
            if (!byId.TryGetValue(key(item), out var vm))
            {
                vm = create(item);
            }

            update(vm, item);
            desired.Add(vm);
        }

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
            {
                continue;
            }

            var existing = target.IndexOf(desired[i]);
            if (existing >= 0)
            {
                target.Move(existing, i);
            }
            else
            {
                target.Insert(i, desired[i]);
            }
        }
    }
}
