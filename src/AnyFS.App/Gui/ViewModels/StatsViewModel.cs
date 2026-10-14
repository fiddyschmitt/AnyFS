using System.Collections.ObjectModel;
using AnyFS.App.Gui.Services;
using AnyFS.Core.Runtime;
using AnyFS.Core.Stats;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnyFS.App.Gui.ViewModels;

public sealed partial class StatsRowViewModel(string collection) : ObservableObject
{
    public string Collection { get; } = collection;

    [ObservableProperty]
    public partial string FilesTotal { get; set; } = "";

    [ObservableProperty]
    public partial string FilesSession { get; set; } = "";

    [ObservableProperty]
    public partial string ReadTotal { get; set; } = "";

    [ObservableProperty]
    public partial string ReadSession { get; set; } = "";

    [ObservableProperty]
    public partial string WrittenTotal { get; set; } = "";

    [ObservableProperty]
    public partial string WrittenSession { get; set; } = "";

    [ObservableProperty]
    public partial string Connections { get; set; } = "";

    public void Update(CollectionStats stats)
    {
        FilesTotal = stats.Total.FilesServed.ToString("N0");
        FilesSession = stats.Session.FilesServed.ToString("N0");
        ReadTotal = Format.Bytes(stats.Total.BytesRead);
        ReadSession = Format.Bytes(stats.Session.BytesRead);
        WrittenTotal = Format.Bytes(stats.Total.BytesWritten);
        WrittenSession = Format.Bytes(stats.Session.BytesWritten);
        Connections = stats.ActiveConnections.ToString("N0");
    }
}

/// <summary>The Stats tab (spec 11.3, 13): totals and session, a per-collection table, Reset totals.</summary>
public sealed partial class StatsViewModel : ObservableObject
{
    private static readonly CollectionStats Nothing = new();

    private readonly AnyFsEngine _engine;
    private readonly IDialogs _dialogs;
    private readonly Operations _ops;

    public StatsViewModel(AnyFsEngine engine, IDialogs dialogs, Operations ops)
    {
        _engine = engine;
        _dialogs = dialogs;
        _ops = ops;
    }

    [ObservableProperty]
    public partial string FilesServedTotal { get; set; } = "0";

    [ObservableProperty]
    public partial string FilesServedSession { get; set; } = "0";

    [ObservableProperty]
    public partial string BytesServedTotal { get; set; } = "0 B";

    [ObservableProperty]
    public partial string BytesServedSession { get; set; } = "0 B";

    [ObservableProperty]
    public partial string ActiveConnections { get; set; } = "0";

    [ObservableProperty]
    public partial string TotalsSince { get; set; } = "";

    [ObservableProperty]
    public partial string SessionSince { get; set; } = "";

    public ObservableCollection<StatsRowViewModel> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public void Refresh(StatsSnapshot stats)
    {
        FilesServedTotal = stats.Total.FilesServed.ToString("N0");
        FilesServedSession = stats.Session.FilesServed.ToString("N0");
        BytesServedTotal = Format.Bytes(stats.Total.BytesRead + stats.Total.BytesWritten);
        BytesServedSession = Format.Bytes(stats.Session.BytesRead + stats.Session.BytesWritten);
        ActiveConnections = stats.ActiveConnections.ToString("N0");
        TotalsSince = stats.TotalsSince is { } since ? $"since {since.ToLocalTime():d MMM yyyy HH:mm}" : "";
        SessionSince = $"since {stats.SessionStarted.ToLocalTime():d MMM yyyy HH:mm}";

        // every collection has a row, served or not (plus any the counters still know), so the table reads as a table
        var names = _engine.Settings.Collections.Select(c => c.Name)
            .Concat(stats.Collections.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!names.Contains(Rows[i].Collection))
            {
                Rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < names.Count; i++)
        {
            var row = Rows.FirstOrDefault(r => r.Collection == names[i]);
            if (row is null)
            {
                row = new StatsRowViewModel(names[i]);
                Rows.Insert(i, row);
            }
            else if (Rows.IndexOf(row) != i)
            {
                Rows.Move(Rows.IndexOf(row), i);
            }

            row.Update(stats.Collections.TryGetValue(names[i], out var counters) ? counters : Nothing);
        }

        OnPropertyChanged(nameof(HasRows));
    }

    [RelayCommand]
    private async Task ResetTotalsAsync()
    {
        var answer = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel("Reset totals", "Set the lifetime totals back to zero? The session counters keep counting.", "Reset"));
        if (answer.Button == ConfirmButton.Yes)
        {
            await _ops.Run("Reset totals", () =>
            {
                _engine.Stats.ResetTotals();
                _engine.FlushStats();
                Refresh(_engine.Stats.Snapshot());
                return Task.CompletedTask;
            });
        }
    }
}
