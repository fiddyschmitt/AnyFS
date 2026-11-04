using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using AnyFS.App.Gui.Services;
using AnyFS.Core.Operations;
using AnyFS.Core.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnyFS.App.Gui.ViewModels;

/// <summary>The columns of the Operations tab, each sortable.</summary>
public enum OperationColumn
{
    Started,
    Source,
    Operation,
    Path,
    Duration,
    Detail,
    Result,
}

/// <summary>One row: an operation as it stands, its texts made once per refresh so the list only redraws what changed.</summary>
public sealed partial class OperationRowViewModel(long id) : ObservableObject
{
    private bool _ended;

    public long Id { get; } = id;

    [ObservableProperty]
    public partial string Started { get; set; } = "";

    [ObservableProperty]
    public partial string StartedFull { get; set; } = "";

    [ObservableProperty]
    public partial string Source { get; set; } = "";

    [ObservableProperty]
    public partial string Operation { get; set; } = "";

    [ObservableProperty]
    public partial string Path { get; set; } = "";

    [ObservableProperty]
    public partial string Duration { get; set; } = "";

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    [ObservableProperty]
    public partial string Result { get; set; } = "";

    [ObservableProperty]
    public partial OperationOutcome Outcome { get; set; }

    public void Update(OperationRecord record)
    {
        // an operation that has ended never changes again: its texts are not made anew four times a second for five seconds
        if (_ended)
        {
            return;
        }

        _ended = record.Outcome != OperationOutcome.Running;
        var started = record.Started.ToLocalTime();
        Started = started.ToString("HH:mm:ss.f", CultureInfo.CurrentCulture);
        StartedFull = started.ToString("d MMM yyyy HH:mm:ss.fff", CultureInfo.CurrentCulture);
        Source = record.Source;
        Operation = record.Kind.Describe();
        Path = PathText(record);
        Duration = DurationText(record);
        Detail = DetailText(record);
        Result = ResultText(record);
        Outcome = record.Outcome;
    }

    /// <summary>The path asked for; a count, asked for none, says which folder it is up to while it runs.</summary>
    public static string PathText(OperationRecord record)
    {
        var path = record.At ?? (record.Kind.HasPath() ? record.Path : null);
        return path is null ? "" : path.Length == 0 ? "(root)" : path;
    }

    public static string DurationText(OperationRecord record) => record.Duration.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture);

    /// <summary>What went by: bytes through a file handle, entries of a listing, files counted, else what the operation said.</summary>
    public static string DetailText(OperationRecord record) => record.Kind switch
    {
        OperationKind.ReadFile or OperationKind.WriteFile => record.Bytes > 0 ? Format.Bytes(record.Bytes) : record.Detail ?? "",
        OperationKind.List => record.Items > 0 || record.Outcome == OperationOutcome.Succeeded ? $"{record.Items:N0} {(record.Items == 1 ? "entry" : "entries")}" : record.Detail ?? "",
        OperationKind.Count => Format.Files(record.Items, complete: record.Outcome == OperationOutcome.Succeeded),
        _ => record.Detail ?? "",
    };

    public static string ResultText(OperationRecord record) => record.Outcome switch
    {
        OperationOutcome.Running => "Running",
        OperationOutcome.Succeeded => "OK",
        OperationOutcome.Cancelled => record.Error is null ? "Cancelled" : $"Cancelled: {record.Error}",
        _ => WithoutPath(record.Error, record.Path) ?? "Failed",
    };

    /// <summary>An error that begins with the path (as the file-backed sources' do) says it again beside the Path column: the rest alone reads better.</summary>
    private static string? WithoutPath(string? error, string path)
        => error is not null && path.Length > 0 && error.Length > path.Length + 2 && error.StartsWith(path, StringComparison.Ordinal) && error.AsSpan(path.Length).StartsWith(": ")
            ? error[(path.Length + 2)..]
            : error;
}

/// <summary>A source in the filter list: ticked, its operations are shown.</summary>
public sealed partial class SourceFilterViewModel(Guid id, Action<SourceFilterViewModel> changed) : ObservableObject
{
    public Guid Id { get; } = id;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = true;

    partial void OnIsCheckedChanged(bool value) => changed(this);
}

/// <summary>
/// The Operations tab (spec 11.5): every operation asked of a source, as it runs and for five seconds after it ends,
/// newest first, each column sortable; filtered by source, by how long they have taken, and to failures. The window
/// refreshes it four times a second while it is the tab shown, so the durations tick.
/// </summary>
public sealed partial class OperationsTabViewModel : ObservableObject
{
    /// <summary>Rows shown at most; the summary says how many there were.</summary>
    public const int MaxRows = 500;

    private const string NothingRunning = "Nothing running.";
    private const string NothingMatches = "No operations match the filters.";

    private readonly OperationTracker _operations;
    private readonly Func<IEnumerable<(Guid Id, string Name)>> _sources;
    private readonly ITextClipboard _clipboard;
    private readonly HashSet<Guid> _hidden = [];
    private IReadOnlyList<OperationRecord> _shown = [];
    private int _matching;
    private double? _longerThan;
    private bool _syncing;

    /// <summary>The sources from the runners, an array replaced whole when one changes: this runs four times a second, and the settings' own list changes off this thread.</summary>
    public OperationsTabViewModel(AnyFsEngine engine, ITextClipboard clipboard)
        : this(engine.Operations, () => engine.Sources.Select(s => (s.Definition.Id, s.Definition.Name)), clipboard)
    {
    }

    internal OperationsTabViewModel(OperationTracker operations, Func<IEnumerable<(Guid Id, string Name)>> sources, ITextClipboard clipboard)
    {
        _operations = operations;
        _sources = sources;
        _clipboard = clipboard;
        UpdateHeaders();
    }

    public ObservableCollection<OperationRowViewModel> Rows { get; } = [];

    public ObservableCollection<SourceFilterViewModel> Sources { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public bool HasSources => Sources.Count > 0;

    /// <summary>The tab shown: the rows are kept up to date only then.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Paused, the rows stay as they are (operations go on being recorded).</summary>
    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    public partial bool FailuresOnly { get; set; }

    /// <summary>"Operations longer than x seconds": empty, or not a number, shows them all.</summary>
    [ObservableProperty]
    public partial string LongerThanText { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    /// <summary>What the list says when it has no rows: nothing is running, or the filters hide what there is.</summary>
    [ObservableProperty]
    public partial string EmptyText { get; set; } = NothingRunning;

    public OperationColumn SortColumn { get; private set; } = OperationColumn.Started;

    /// <summary>Newest first by default (the user, 2-Nov-2026).</summary>
    public bool SortDescending { get; private set; } = true;

    [ObservableProperty]
    public partial string StartedHeader { get; set; } = "";

    [ObservableProperty]
    public partial string SourceHeader { get; set; } = "";

    [ObservableProperty]
    public partial string OperationHeader { get; set; } = "";

    [ObservableProperty]
    public partial string PathHeader { get; set; } = "";

    [ObservableProperty]
    public partial string DurationHeader { get; set; } = "";

    [ObservableProperty]
    public partial string DetailHeader { get; set; } = "";

    [ObservableProperty]
    public partial string ResultHeader { get; set; } = "";

    /// <summary>The source ids whose operations are hidden (unticked in the list).</summary>
    public IReadOnlySet<Guid> HiddenSources => _hidden;

    public void Refresh()
    {
        SyncSources();
        if (IsActive && !IsPaused)
        {
            var records = _operations.Recent();
            var shown = new List<OperationRecord>(records.Count);
            foreach (var record in records)
            {
                if (Passes(record))
                {
                    shown.Add(record);
                }
            }

            _matching = shown.Count;
            shown.Sort(Comparison(SortColumn, SortDescending));
            if (shown.Count > MaxRows)
            {
                shown.RemoveRange(MaxRows, shown.Count - MaxRows);
            }

            _shown = shown;
            ListSync.Sync(Rows, shown, r => r.Id, vm => vm.Id, r => new OperationRowViewModel(r.Id), (vm, r) => vm.Update(r));
            OnPropertyChanged(nameof(HasRows));
            EmptyText = records.Count > 0 ? NothingMatches : NothingRunning;
        }

        Summary = Summarise(_operations.Stats());
    }

    /// <summary>Click a header: that column, ascending; the same header again turns it round (Started and Duration begin with the latest and the longest).</summary>
    [RelayCommand]
    private void Sort(string column)
    {
        if (!Enum.TryParse<OperationColumn>(column, out var parsed))
        {
            return;
        }

        if (parsed == SortColumn)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = parsed;
            SortDescending = parsed is OperationColumn.Started or OperationColumn.Duration;
        }

        UpdateHeaders();
        Refresh();
    }

    [RelayCommand]
    private void ShowAllSources() => SetAll(true);

    [RelayCommand]
    private void ShowNoSources() => SetAll(false);

    /// <summary>The rows as shown, tab-separated with a header line, for a bug report or a spreadsheet.</summary>
    [RelayCommand]
    private Task CopyAsync() => _clipboard.SetTextAsync(AsText());

    public string AsText()
    {
        var text = new StringBuilder();
        text.AppendLine("Started\tSource\tOperation\tPath\tDuration (s)\tDetail\tResult");
        foreach (var record in _shown)
        {
            text.Append(record.Started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append('\t')
                .Append(record.Source).Append('\t')
                .Append(record.Kind.Describe()).Append('\t')
                .Append(OperationRowViewModel.PathText(record)).Append('\t')
                .Append(record.Duration.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)).Append('\t')
                .Append(OperationRowViewModel.DetailText(record)).Append('\t')
                .AppendLine(OperationRowViewModel.ResultText(record));
        }

        return text.ToString();
    }

    partial void OnLongerThanTextChanged(string value)
    {
        _longerThan = double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var seconds) || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
            ? Math.Max(0, seconds)
            : null;
        Refresh();
    }

    partial void OnFailuresOnlyChanged(bool value) => Refresh();

    partial void OnIsPausedChanged(bool value) => Refresh();

    partial void OnIsActiveChanged(bool value) => Refresh();

    private bool Passes(OperationRecord record)
    {
        if (_hidden.Contains(record.SourceId))
        {
            return false;
        }

        if (_longerThan is { } seconds && record.Duration.TotalSeconds < seconds)
        {
            return false;
        }

        return !FailuresOnly || record.Outcome == OperationOutcome.Failed;
    }

    /// <summary>
    /// The column's order, then newest first among equals, so a sort by Source still reads top-down in time. Started goes by
    /// the id, which ascends in the order the operations began: two begun in one tick of the clock still have an order.
    /// </summary>
    private static Comparison<OperationRecord> Comparison(OperationColumn column, bool descending)
    {
        Comparison<OperationRecord> key = column switch
        {
            OperationColumn.Started => (a, b) => a.Id.CompareTo(b.Id),
            OperationColumn.Source => (a, b) => string.Compare(a.Source, b.Source, StringComparison.CurrentCultureIgnoreCase),
            OperationColumn.Operation => (a, b) => string.Compare(a.Kind.Describe(), b.Kind.Describe(), StringComparison.CurrentCultureIgnoreCase),
            OperationColumn.Path => (a, b) => string.Compare(OperationRowViewModel.PathText(a), OperationRowViewModel.PathText(b), StringComparison.CurrentCultureIgnoreCase),
            OperationColumn.Duration => (a, b) => a.Duration.CompareTo(b.Duration),
            OperationColumn.Detail => (a, b) => string.Compare(OperationRowViewModel.DetailText(a), OperationRowViewModel.DetailText(b), StringComparison.CurrentCultureIgnoreCase),
            _ => (a, b) => string.Compare(OperationRowViewModel.ResultText(a), OperationRowViewModel.ResultText(b), StringComparison.CurrentCultureIgnoreCase),
        };
        return (a, b) =>
        {
            var c = key(a, b);
            if (descending)
            {
                c = -c;
            }

            return c != 0 ? c : b.Id.CompareTo(a.Id);
        };
    }

    private string Summarise(OperationStatsSnapshot stats)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.CurrentCulture, $"{stats.Running:N0} running");
        text.Append(CultureInfo.CurrentCulture, $" · {stats.Finished:N0} finished this session");
        if (stats.Failed > 0)
        {
            text.Append(CultureInfo.CurrentCulture, $", {stats.Failed:N0} failed");
        }

        if (_matching > MaxRows)
        {
            text.Append(CultureInfo.CurrentCulture, $" · showing {MaxRows:N0} of {_matching:N0}");
        }

        if (IsPaused)
        {
            text.Append(" · paused");
        }

        return text.ToString();
    }

    private void UpdateHeaders()
    {
        StartedHeader = Header("Started", OperationColumn.Started);
        SourceHeader = Header("Source", OperationColumn.Source);
        OperationHeader = Header("Operation", OperationColumn.Operation);
        PathHeader = Header("Path", OperationColumn.Path);
        DurationHeader = Header("Duration (s)", OperationColumn.Duration);
        DetailHeader = Header("Detail", OperationColumn.Detail);
        ResultHeader = Header("Result", OperationColumn.Result);
    }

    private string Header(string title, OperationColumn column) => column != SortColumn ? title : title + (SortDescending ? " ▼" : " ▲");

    /// <summary>The filter list follows the configured sources (by name), each new one ticked; what was unticked stays so.</summary>
    private void SyncSources()
    {
        var sources = _sources().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _syncing = true;
        try
        {
            ListSync.Sync(Sources, sources, s => s.Id, vm => vm.Id, s => new SourceFilterViewModel(s.Id, OnFilterChanged) { IsChecked = !_hidden.Contains(s.Id) }, (vm, s) => vm.Name = s.Name);
        }
        finally
        {
            _syncing = false;
        }

        _hidden.RemoveWhere(id => sources.All(s => s.Id != id));
        OnPropertyChanged(nameof(HasSources));
    }

    private void OnFilterChanged(SourceFilterViewModel filter)
    {
        if (filter.IsChecked)
        {
            _hidden.Remove(filter.Id);
        }
        else
        {
            _hidden.Add(filter.Id);
        }

        if (!_syncing)
        {
            Refresh();
        }
    }

    private void SetAll(bool shown)
    {
        _syncing = true;
        try
        {
            foreach (var filter in Sources)
            {
                filter.IsChecked = shown;
            }
        }
        finally
        {
            _syncing = false;
        }

        Refresh();
    }
}
