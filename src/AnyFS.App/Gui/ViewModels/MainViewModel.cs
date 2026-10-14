using AnyFS.App.Gui.Services;
using AnyFS.Core.Runtime;
using AnyFS.Core.Status;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnyFS.App.Gui.ViewModels;

/// <summary>The window: three tabs and the status bar (spec 11).</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AnyFsEngine _engine;
    private readonly IOpener _opener;

    public MainViewModel(AnyFsEngine engine, IDialogs dialogs, IOpener opener, ITextClipboard clipboard)
    {
        _engine = engine;
        _opener = opener;
        Operations = new Operations(dialogs);
        Collections = new CollectionsViewModel(engine, dialogs, opener, clipboard, Operations);
        AllSources = new AllSourcesViewModel(engine, dialogs, Operations);
        Stats = new StatsViewModel(engine, dialogs, Operations);
        Title = $"AnyFS {AnyFsEngine.Version}";
    }

    public Operations Operations { get; }

    public CollectionsViewModel Collections { get; }

    public AllSourcesViewModel AllSources { get; }

    public StatsViewModel Stats { get; }

    public string Title { get; }

    [ObservableProperty]
    public partial StatusColour OverallColour { get; set; } = StatusColour.Grey;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Starting";

    [ObservableProperty]
    public partial string FilesServedText { get; set; } = "0 files served this session";

    /// <summary>Everything from one snapshot, so the tabs agree with each other.</summary>
    public void Refresh()
    {
        var snapshot = _engine.Snapshot();
        Collections.Refresh(snapshot);
        AllSources.Refresh();
        Stats.Refresh(snapshot.Stats);

        var enabled = snapshot.Collections.Where(c => c.Enabled).ToList();
        OverallColour = enabled.Count == 0 ? StatusColour.Grey : enabled.Max(c => Enum.TryParse<StatusColour>(c.Colour, out var colour) ? colour : StatusColour.Grey);
        StatusText = Summarise(snapshot.Collections);
        FilesServedText = $"{snapshot.Stats.Session.FilesServed:N0} files served this session";
    }

    [RelayCommand]
    private void OpenLog() => _opener.OpenFile(_engine.Paths.LogFile);

    private static string Summarise(IReadOnlyList<CollectionStatus> collections)
    {
        if (collections.Count == 0)
        {
            return "No collections";
        }

        var enabled = collections.Where(c => c.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return "All collections disabled";
        }

        var red = enabled.Count(c => c.Colour == nameof(StatusColour.Red));
        var yellow = enabled.Count(c => c.Colour == nameof(StatusColour.Yellow));
        var green = enabled.Count(c => c.Colour == nameof(StatusColour.Green));
        if (red == 0 && yellow == 0)
        {
            return green == enabled.Count ? (enabled.Count == 1 ? $"{enabled[0].Name} ready" : "All collections ready") : "Nothing to serve yet";
        }

        var parts = new List<string>();
        if (red > 0)
        {
            parts.Add($"{red} {(red == 1 ? "collection has" : "collections have")} a failure");
        }

        if (yellow > 0)
        {
            parts.Add($"{yellow} {(yellow == 1 ? "collection is" : "collections are")} starting or degraded");
        }

        return string.Join(", ", parts);
    }
}
