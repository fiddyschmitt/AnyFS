using AnyFS.App.Gui.ViewModels;
using AnyFS.Core.Operations;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;

namespace AnyFS.App.Tests;

/// <summary>The Operations tab over a tracker fed by hand, with a clock the test moves (spec 11.5).</summary>
[TestClass]
public class OperationsTabTests
{
    private static readonly Guid Alpha = Guid.NewGuid();
    private static readonly Guid Beta = Guid.NewGuid();

    private FakeTime _time = null!;
    private OperationTracker _tracker = null!;
    private FakeClipboard _clipboard = null!;
    private List<(Guid Id, string Name)> _sources = null!;
    private OperationsTabViewModel _tab = null!;

    [TestInitialize]
    public void Init()
    {
        _time = new FakeTime();
        _tracker = new OperationTracker(_time);
        _clipboard = new FakeClipboard();
        _sources = [(Alpha, "alpha"), (Beta, "beta")];
        _tab = new OperationsTabViewModel(_tracker, () => _sources, _clipboard) { IsActive = true };
    }

    private Operation Begin(Guid source, OperationKind kind, string path, TimeSpan? ago = null)
    {
        var op = _tracker.Begin(source, source == Alpha ? "alpha" : "beta", kind, path);
        if (ago is { } by)
        {
            _time.Advance(by);
        }

        return op;
    }

    private List<string> Paths() => _tab.Rows.Select(r => r.Path).ToList();

    [TestMethod]
    public void RowsAreNewestFirstAndFinishedOnesStayFiveSeconds()
    {
        var list = Begin(Alpha, OperationKind.List, "docs", TimeSpan.FromSeconds(1));
        var stat = Begin(Beta, OperationKind.Stat, "docs/a.txt", TimeSpan.FromMilliseconds(250));
        _tab.Refresh();
        CollectionAssert.AreEqual(new[] { "docs/a.txt", "docs" }, Paths(), "newest at the top");
        var top = _tab.Rows[0];
        Assert.AreEqual("beta", top.Source);
        Assert.AreEqual("Get metadata", top.Operation);
        Assert.AreEqual("Running", top.Result);
        Assert.AreEqual(OperationOutcome.Running, top.Outcome);
        Assert.AreEqual(0.25.ToString("0.00"), top.Duration);
        Assert.AreEqual(1.25.ToString("0.00"), _tab.Rows[1].Duration);
        Assert.AreEqual("List folder", _tab.Rows[1].Operation);
        StringAssert.StartsWith(_tab.Summary, "2 running · 0 finished this session");
        Assert.IsTrue(_tab.HasRows);

        list.AddItems(12);
        list.Complete();
        stat.Fail(VfsException.AccessDenied("docs/a.txt"));
        _tab.Refresh();
        Assert.AreEqual(OperationOutcome.Succeeded, _tab.Rows[1].Outcome);
        Assert.AreEqual("OK", _tab.Rows[1].Result);
        Assert.AreEqual("12 entries", _tab.Rows[1].Detail);
        Assert.AreEqual(OperationOutcome.Failed, _tab.Rows[0].Outcome);
        Assert.AreEqual("access to 'docs/a.txt' was denied", _tab.Rows[0].Result);
        Assert.AreEqual("0 running · 2 finished this session, 1 failed", _tab.Summary);

        _time.Advance(TimeSpan.FromSeconds(4.9));
        _tab.Refresh();
        Assert.AreEqual(2, _tab.Rows.Count, "still shown just under five seconds later");
        _time.Advance(TimeSpan.FromMilliseconds(200));
        _tab.Refresh();
        Assert.AreEqual(0, _tab.Rows.Count, "cleared five seconds after they ended");
        Assert.IsFalse(_tab.HasRows);
        Assert.AreEqual("0 running · 2 finished this session, 1 failed", _tab.Summary, "the session's counts stay");
    }

    [TestMethod]
    public void TheSourceListFiltersAndFollowsTheSources()
    {
        Begin(Alpha, OperationKind.List, "a");
        Begin(Beta, OperationKind.List, "b");
        _tab.Refresh();
        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, _tab.Sources.Select(s => s.Name).ToList());
        Assert.IsTrue(_tab.Sources.All(s => s.IsChecked), "every source is shown to begin with");
        Assert.IsTrue(_tab.HasSources);

        _tab.Sources.Single(s => s.Name == "alpha").IsChecked = false;
        CollectionAssert.AreEqual(new[] { "b" }, Paths(), "unticking a source hides its rows at once");
        _tab.ShowNoSourcesCommand.Execute(null);
        Assert.AreEqual(0, _tab.Rows.Count);
        _tab.ShowAllSourcesCommand.Execute(null);
        CollectionAssert.AreEqual(new[] { "b", "a" }, Paths());
        _tab.Sources.Single(s => s.Name == "beta").IsChecked = false;

        _sources.Add((Guid.NewGuid(), "gamma"));
        _sources[0] = (Alpha, "alpha renamed");
        _tab.Refresh();
        CollectionAssert.AreEqual(new[] { "alpha renamed", "beta", "gamma" }, _tab.Sources.Select(s => s.Name).ToList(), "a new source joins ticked, a renamed one keeps its place");
        CollectionAssert.AreEqual(new[] { true, false, true }, _tab.Sources.Select(s => s.IsChecked).ToList(), "what was unticked stays unticked");
        CollectionAssert.AreEqual(new[] { "a" }, Paths());

        _sources.RemoveAll(s => s.Id == Beta);
        _tab.Refresh();
        CollectionAssert.AreEqual(new[] { "alpha renamed", "gamma" }, _tab.Sources.Select(s => s.Name).ToList(), "a deleted source leaves the list");
        Assert.AreEqual(0, _tab.HiddenSources.Count, "and is no longer remembered as hidden");
    }

    [TestMethod]
    public void LongerThanAndFailuresOnlyFilter()
    {
        var slow = Begin(Alpha, OperationKind.ReadFile, "slow", TimeSpan.FromSeconds(3));
        var quick = Begin(Alpha, OperationKind.Stat, "quick", TimeSpan.FromMilliseconds(100));
        _tab.LongerThanText = "2";
        CollectionAssert.AreEqual(new[] { "slow" }, Paths(), "a running operation counts by how long it has run so far");
        _tab.LongerThanText = "2.5";
        CollectionAssert.AreEqual(new[] { "slow" }, Paths());
        _tab.LongerThanText = "abc";
        CollectionAssert.AreEqual(new[] { "quick", "slow" }, Paths(), "not a number: no filter");
        _tab.LongerThanText = "";
        CollectionAssert.AreEqual(new[] { "quick", "slow" }, Paths());

        quick.Fail(new IOException("boom"));
        slow.Complete();
        _tab.FailuresOnly = true;
        CollectionAssert.AreEqual(new[] { "quick" }, Paths());
        _tab.LongerThanText = "1";
        Assert.AreEqual(0, _tab.Rows.Count, "both filters apply");
        Assert.AreEqual("No operations match the filters.", _tab.EmptyText, "the list does not claim that nothing is running");
        _tab.FailuresOnly = false;
        CollectionAssert.AreEqual(new[] { "slow" }, Paths());

        _time.Advance(TimeSpan.FromSeconds(6));
        _tab.Refresh();
        Assert.AreEqual(0, _tab.Rows.Count);
        Assert.AreEqual("Nothing running.", _tab.EmptyText, "they have gone: nothing to filter");
    }

    [TestMethod]
    public void ARowThatHasEndedIsNotWorkedOutAgain()
    {
        var op = Begin(Alpha, OperationKind.Stat, "a", TimeSpan.FromSeconds(1));
        _tab.Refresh();
        var row = _tab.Rows.Single();
        Assert.AreEqual(1.ToString("0.00"), row.Duration);

        _time.Advance(TimeSpan.FromSeconds(1));
        _tab.Refresh();
        Assert.AreEqual(2.ToString("0.00"), row.Duration, "a running row's duration runs");

        op.Complete();
        _time.Advance(TimeSpan.FromSeconds(1));
        var changes = 0;
        row.PropertyChanged += (_, _) => changes++;
        _tab.Refresh();
        Assert.AreEqual(OperationOutcome.Succeeded, row.Outcome);
        Assert.AreEqual(2.ToString("0.00"), row.Duration, "the duration stops where the operation ended");
        var seen = changes;
        Assert.IsTrue(seen > 0, "the first refresh after the end shows it");

        _time.Advance(TimeSpan.FromSeconds(1));
        _tab.Refresh();
        Assert.AreEqual(seen, changes, "after that the row is left alone");
    }

    [TestMethod]
    public void OperationsBegunInOneTickSortByTheOrderTheyBegan()
    {
        Begin(Alpha, OperationKind.Stat, "first");
        Begin(Alpha, OperationKind.Stat, "second");
        Begin(Alpha, OperationKind.Stat, "third");
        _tab.Refresh();
        CollectionAssert.AreEqual(new[] { "third", "second", "first" }, Paths());
        _tab.SortCommand.Execute("Started");
        CollectionAssert.AreEqual(new[] { "first", "second", "third" }, Paths(), "turned round, all the way");
    }

    [TestMethod]
    public void EveryColumnSortsAndTheSameHeaderTurnsItRound()
    {
        var a = Begin(Beta, OperationKind.ReadFile, "zebra", TimeSpan.FromSeconds(2));
        a.AddBytes(2048);
        var b = Begin(Alpha, OperationKind.List, "apple", TimeSpan.FromSeconds(1));
        b.AddItems(3);
        b.Complete();
        _tab.Refresh();
        Assert.AreEqual("Started ▼", _tab.StartedHeader);
        Assert.AreEqual("Path", _tab.PathHeader);
        CollectionAssert.AreEqual(new[] { "apple", "zebra" }, Paths());

        _tab.SortCommand.Execute("Started");
        Assert.AreEqual("Started ▲", _tab.StartedHeader);
        CollectionAssert.AreEqual(new[] { "zebra", "apple" }, Paths(), "the same header again turns it round");

        _tab.SortCommand.Execute("Path");
        Assert.AreEqual("Path ▲", _tab.PathHeader);
        Assert.AreEqual("Started", _tab.StartedHeader);
        CollectionAssert.AreEqual(new[] { "apple", "zebra" }, Paths());
        _tab.SortCommand.Execute("Path");
        CollectionAssert.AreEqual(new[] { "zebra", "apple" }, Paths());

        _tab.SortCommand.Execute("Source");
        CollectionAssert.AreEqual(new[] { "apple", "zebra" }, Paths(), "alpha before beta");
        _tab.SortCommand.Execute("Operation");
        CollectionAssert.AreEqual(new[] { "apple", "zebra" }, Paths(), "List folder before Read file");
        _tab.SortCommand.Execute("Duration");
        Assert.AreEqual("Duration (s) ▼", _tab.DurationHeader);
        CollectionAssert.AreEqual(new[] { "zebra", "apple" }, Paths(), "longest first");
        _tab.SortCommand.Execute("Detail");
        CollectionAssert.AreEqual(new[] { "zebra", "apple" }, Paths(), "'2 KB' before '3 entries'");
        _tab.SortCommand.Execute("Result");
        CollectionAssert.AreEqual(new[] { "apple", "zebra" }, Paths(), "OK before Running");
        _tab.SortCommand.Execute("Nonsense");
        CollectionAssert.AreEqual(new[] { "apple", "zebra" }, Paths(), "an unknown column changes nothing");
    }

    [TestMethod]
    public void PausedTheRowsStayStill()
    {
        Begin(Alpha, OperationKind.List, "before");
        _tab.Refresh();
        _tab.IsPaused = true;
        Begin(Alpha, OperationKind.List, "during");
        _tab.Refresh();
        CollectionAssert.AreEqual(new[] { "before" }, Paths());
        StringAssert.EndsWith(_tab.Summary, "· paused");
        Assert.AreEqual("2 running · 0 finished this session · paused", _tab.Summary, "the summary still counts");
        _tab.IsPaused = false;
        CollectionAssert.AreEqual(new[] { "during", "before" }, Paths());
    }

    [TestMethod]
    public void InactiveTheRowsAreLeftAlone()
    {
        _tab.IsActive = false;
        Begin(Alpha, OperationKind.List, "x");
        _tab.Refresh();
        Assert.AreEqual(0, _tab.Rows.Count);
        _tab.IsActive = true;
        CollectionAssert.AreEqual(new[] { "x" }, Paths(), "shown as soon as the tab is");
    }

    [TestMethod]
    public async Task CopyGivesTheRowsShownAsText()
    {
        var read = Begin(Alpha, OperationKind.ReadFile, "docs/a.txt", TimeSpan.FromMilliseconds(1500));
        read.AddBytes(3 * 1024);
        read.Complete();
        Begin(Beta, OperationKind.Rename, "old.txt");
        _tab.Sources.Single(s => s.Name == "beta").IsChecked = false;
        await _tab.CopyCommand.ExecuteAsync(null);
        var lines = _clipboard.Texts.Single().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.AreEqual(2, lines.Count, "a header and the one row shown");
        Assert.AreEqual("Started\tSource\tOperation\tPath\tDuration (s)\tDetail\tResult", lines[0]);
        var cells = lines[1].Split('\t');
        Assert.AreEqual(7, cells.Length);
        Assert.AreEqual("alpha", cells[1]);
        Assert.AreEqual("Read file", cells[2]);
        Assert.AreEqual("docs/a.txt", cells[3]);
        Assert.AreEqual("1.500", cells[4]);
        Assert.AreEqual("3 KB", cells[5]);
        Assert.AreEqual("OK", cells[6]);
    }

    [TestMethod]
    public void TheListIsCappedAndTheSummarySaysSo()
    {
        for (var i = 0; i < OperationsTabViewModel.MaxRows + 20; i++)
        {
            Begin(Alpha, OperationKind.Stat, $"f{i}");
        }

        _tab.Refresh();
        Assert.AreEqual(OperationsTabViewModel.MaxRows, _tab.Rows.Count);
        Assert.AreEqual($"f{OperationsTabViewModel.MaxRows + 19}", _tab.Rows[0].Path, "the newest are the ones shown");
        StringAssert.Contains(_tab.Summary, $"showing {OperationsTabViewModel.MaxRows:N0} of {OperationsTabViewModel.MaxRows + 20:N0}");
    }

    [TestMethod]
    public void ACountSaysWhereItIsUpToOnItsOneRow()
    {
        var counters = new SourceCounters();
        var count = Begin(Alpha, OperationKind.Count, "");
        count.Follow(counters);
        _tab.Refresh();
        var row = _tab.Rows.Single();
        Assert.AreEqual("", row.Path, "in no folder yet");
        Assert.AreEqual("0+ files", row.Detail, "nothing yet, and not the end of it");

        using (counters.EnterFolder(""))
        {
            _tab.Refresh();
            Assert.AreEqual("(root)", row.Path);
        }

        counters.Add(1200, 7);
        using (counters.EnterFolder("group/app/files"))
        {
            _tab.Refresh();
            Assert.AreSame(row, _tab.Rows.Single(), "the same row, moving");
            Assert.AreEqual("group/app/files", row.Path);
            Assert.AreEqual($"{1200:N0}+ files", row.Detail, "as the source's tile says it");
            Assert.AreEqual("Running", row.Result);
            StringAssert.Contains(_tab.AsText(), "group/app/files", "what is shown is what is copied");
        }

        count.Complete();
        _tab.Refresh();
        Assert.AreEqual("", row.Path, "a count that is over is in no folder");
        Assert.AreEqual($"{1200:N0} files", row.Detail);
        Assert.AreEqual("OK", row.Result);
    }

    [TestMethod]
    public void ACountStaysAboveTheFoldersItGoesThroughWhateverTheSort()
    {
        var count = Begin(Alpha, OperationKind.Count, "");
        count.Follow(new SourceCounters());
        var folders = Enumerable.Range(0, OperationsTabViewModel.MaxRows + 5).Select(i => Begin(Alpha, OperationKind.CountFolder, $"group/p{i:0000}", TimeSpan.FromMilliseconds(1))).ToList();
        _tab.Refresh();
        Assert.AreEqual("Count files", _tab.Rows[0].Operation, "at the top, newest first or not, and not what the cap on rows cuts");
        Assert.AreEqual($"group/p{OperationsTabViewModel.MaxRows + 4:0000}", _tab.Rows[1].Path, "then the newest");
        Assert.AreEqual(OperationsTabViewModel.MaxRows, _tab.Rows.Count);

        foreach (var column in Enum.GetNames<OperationColumn>())
        {
            _tab.SortCommand.Execute(column);
            Assert.AreEqual("Count files", _tab.Rows[0].Operation, $"sorted by {column}");
            _tab.SortCommand.Execute(column);
            Assert.AreEqual("Count files", _tab.Rows[0].Operation, $"sorted by {column}, the other way");
        }

        foreach (var folder in folders)
        {
            folder.Complete();
        }

        _tab.Refresh();
        Assert.AreEqual("Count files", _tab.Rows[0].Operation);
    }

    [TestMethod]
    public void AFolderOfACountSaysHowItWent()
    {
        var listed = Begin(Alpha, OperationKind.CountFolder, "group/a/files");
        listed.AddItems(12);
        listed.Complete();
        var one = Begin(Alpha, OperationKind.CountFolder, "group/b");
        one.AddItems(1);
        one.Complete();
        var kept = Begin(Alpha, OperationKind.CountFolder, "group/c");
        kept.Detail = "kept as it was";
        kept.Complete();
        var running = Begin(Alpha, OperationKind.CountFolder, "group/d");
        var refused = Begin(Alpha, OperationKind.CountFolder, "group/e");
        refused.Fail(VfsException.AccessDenied("group/e"));
        var root = Begin(Alpha, OperationKind.CountFolder, "");
        _tab.Refresh();
        var rows = _tab.Rows.ToDictionary(r => r.Path);
        Assert.AreEqual("Count folder", rows["group/a/files"].Operation);
        Assert.AreEqual("12 entries", rows["group/a/files"].Detail);
        Assert.AreEqual("OK", rows["group/a/files"].Result);
        Assert.AreEqual("1 entry", rows["group/b"].Detail);
        Assert.AreEqual("kept as it was", rows["group/c"].Detail);
        Assert.AreEqual("", rows["group/d"].Detail);
        Assert.AreEqual("Running", rows["group/d"].Result);
        Assert.AreEqual("access to 'group/e' was denied", rows["group/e"].Result);
        Assert.AreEqual(OperationOutcome.Failed, rows["group/e"].Outcome);
        Assert.IsTrue(rows.ContainsKey("(root)"), "the root is named as for any path");
        running.Complete();
        root.Complete();
    }

    [TestMethod]
    public void ACountThatDidNotFinishSaysItsTotalWasNotTheEndOfIt()
    {
        var counters = new SourceCounters();
        var count = Begin(Alpha, OperationKind.Count, "");
        count.Follow(counters);
        counters.Add(1, 0);
        count.Fail(new IOException("gone"));
        _tab.Refresh();
        Assert.AreEqual("1+ files", _tab.Rows.Single().Detail);
        Assert.AreEqual("gone", _tab.Rows.Single().Result);

        var empty = Begin(Beta, OperationKind.Count, "");
        empty.Follow(new SourceCounters());
        empty.Complete();
        _tab.Refresh();
        Assert.AreEqual("0 files", _tab.Rows.Single(r => r.Source == "beta").Detail, "a count that found nothing says so");
    }

    [TestMethod]
    public void RowTextsReadWell()
    {
        var root = Begin(Alpha, OperationKind.List, "");
        root.Complete();
        var start = Begin(Alpha, OperationKind.Start, "");
        start.Complete();
        var missing = Begin(Alpha, OperationKind.Stat, "nope");
        missing.Detail = "not found";
        missing.Complete();
        var rename = _tracker.Begin(Alpha, "alpha", OperationKind.Rename, "a.txt", "to b.txt");
        rename.Cancel("stopped early");
        var count = Begin(Alpha, OperationKind.Count, "");
        count.AddItems(1200);
        count.Complete();
        var locked = Begin(Alpha, OperationKind.ReadFile, "docs/a.txt");
        locked.Fail(new VfsException(VfsError.IoError, "docs/a.txt: the file is in use"));
        var quoted = Begin(Alpha, OperationKind.Delete, "docs/b.txt");
        quoted.Fail(VfsException.AccessDenied("docs/b.txt"));
        _tab.Refresh();
        var rows = _tab.Rows.ToDictionary(r => r.Operation);
        Assert.AreEqual("the file is in use", rows["Read file"].Result, "an error that begins with the path loses it: the Path column has it");
        Assert.AreEqual("access to 'docs/b.txt' was denied", rows["Delete"].Result, "an error that merely mentions the path keeps it");
        Assert.AreEqual("(root)", rows["List folder"].Path);
        Assert.AreEqual("0 entries", rows["List folder"].Detail);
        Assert.AreEqual("", rows["Start"].Path, "a start has no path");
        Assert.AreEqual("not found", rows["Get metadata"].Detail);
        Assert.AreEqual("to b.txt", rows["Rename"].Detail);
        Assert.AreEqual("Cancelled: stopped early", rows["Rename"].Result);
        Assert.AreEqual(OperationOutcome.Cancelled, rows["Rename"].Outcome);
        Assert.AreEqual($"{1200:N0} files", rows["Count files"].Detail);
        Assert.AreEqual("", rows["Count files"].Path);
        Assert.AreEqual(_time.GetUtcNow().ToLocalTime().ToString("HH:mm:ss.f"), rows["Start"].Started);
    }
}
