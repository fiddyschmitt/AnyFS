using AnyFS.Core.Operations;
using AnyFS.Core.Runtime;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;

namespace AnyFS.Core.Tests;

[TestClass]
public class OperationTrackerTests
{
    private static readonly Guid Alpha = Guid.NewGuid();
    private static readonly Guid Beta = Guid.NewGuid();

    [TestMethod]
    public void ARunningOperationIsListedAndStaysFiveSecondsAfterItEnds()
    {
        var time = new FakeTime();
        var tracker = new OperationTracker(time);
        var finished = new List<OperationRecord>();
        tracker.Finished += finished.Add;

        var op = tracker.Begin(Alpha, "alpha", OperationKind.List, "docs");
        time.Advance(TimeSpan.FromMilliseconds(300));
        var row = tracker.Recent().Single();
        Assert.AreEqual(OperationOutcome.Running, row.Outcome);
        Assert.AreEqual(TimeSpan.FromMilliseconds(300), row.Duration);
        Assert.AreEqual("alpha", row.Source);
        Assert.AreEqual(Alpha, row.SourceId);
        Assert.AreEqual("docs", row.Path);
        Assert.AreEqual(time.GetUtcNow() - TimeSpan.FromMilliseconds(300), row.Started);
        Assert.AreEqual(0, finished.Count);

        op.AddItems(3);
        time.Advance(TimeSpan.FromMilliseconds(200));
        op.Complete();
        op.Fail(new IOException("too late"));
        row = tracker.Recent().Single();
        Assert.AreEqual(OperationOutcome.Succeeded, row.Outcome, "a second end is ignored");
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), row.Duration);
        Assert.AreEqual(3, row.Items);
        Assert.IsNull(row.Error);
        Assert.AreEqual(1, finished.Count);
        Assert.AreEqual(row, finished[0]);

        time.Advance(TimeSpan.FromSeconds(4.9));
        Assert.AreEqual(1, tracker.Recent().Count, "still listed just under five seconds later");
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.AreEqual(0, tracker.Recent().Count, "gone after five seconds");
        Assert.AreEqual(1, tracker.Stats().Finished, "the counters keep it");
    }

    [TestMethod]
    public void ACountShowsItsRunningTotalAndItsFolderUntilItEnds()
    {
        var tracker = new OperationTracker(new FakeTime());
        var finished = new List<OperationRecord>();
        tracker.Finished += finished.Add;
        var counters = new SourceCounters();
        var op = tracker.Begin(Alpha, "alpha", OperationKind.Count, "");
        op.Follow(counters);
        var row = tracker.Recent().Single();
        Assert.AreEqual(0, row.Items);
        Assert.IsNull(row.At, "in no folder yet");

        counters.Add(1200, 30);
        using (counters.EnterFolder("group/project/files"))
        {
            row = tracker.Recent().Single();
            Assert.AreEqual(1200, row.Items, "the total so far");
            Assert.AreEqual("group/project/files", row.At);
            Assert.AreEqual("", row.Path, "what was asked for has not changed");
        }

        using var stillIn = counters.EnterFolder("group/other");
        counters.Add(34, 0);
        op.Complete();
        counters.Add(99, 0);
        row = tracker.Recent().Single();
        Assert.AreEqual(1234, row.Items, "the total as it ended stays");
        Assert.IsNull(row.At, "a finished count is in no folder");
        Assert.AreEqual(row, finished.Single());
        Assert.AreEqual(1234, tracker.Stats().Sources.Single().Operations.Single(o => o.Kind == OperationKind.Count).Items, "counted once");
    }

    [TestMethod]
    public void ACountThatFailsKeepsHowFarItGot()
    {
        var tracker = new OperationTracker(new FakeTime());
        var counters = new SourceCounters();
        var op = tracker.Begin(Alpha, "alpha", OperationKind.Count, "");
        op.Follow(counters);
        counters.Add(17, 2);
        using var stillIn = counters.EnterFolder("group");
        op.Fail(new IOException("gone"));
        var row = tracker.Recent().Single();
        Assert.AreEqual((OperationOutcome.Failed, "gone", 17L), (row.Outcome, row.Error, row.Items));
        Assert.IsNull(row.At);
    }

    [TestMethod]
    public void OutcomesFollowTheException()
    {
        var tracker = new OperationTracker(new FakeTime());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        tracker.Begin(Alpha, "alpha", OperationKind.Stat, "a").Fail(VfsException.AccessDenied("a"));
        tracker.Begin(Alpha, "alpha", OperationKind.Stat, "b").Fail(new OperationCanceledException(cancelled.Token), cancelled.Token);
        tracker.Begin(Alpha, "alpha", OperationKind.Stat, "c").Fail(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException()), CancellationToken.None);
        tracker.Begin(Alpha, "alpha", OperationKind.List, "d").Cancel("stopped early");
        tracker.Begin(Alpha, "alpha", OperationKind.Stat, "e").Fail(new InvalidOperationException("outer", new IOException("disk gone")));
        tracker.Begin(Alpha, "alpha", OperationKind.Stat, "f").Fail(new OperationCanceledException("gave up inside"), CancellationToken.None);

        var rows = tracker.Recent().ToDictionary(r => r.Path);
        Assert.AreEqual((OperationOutcome.Failed, "access to 'a' was denied"), (rows["a"].Outcome, rows["a"].Error));
        Assert.AreEqual((OperationOutcome.Cancelled, (string?)null), (rows["b"].Outcome, rows["b"].Error), "the caller asked for it");
        Assert.AreEqual((OperationOutcome.Failed, "timed out"), (rows["c"].Outcome, rows["c"].Error), "an HTTP timeout is a cancellation inside");
        Assert.AreEqual((OperationOutcome.Cancelled, "stopped early"), (rows["d"].Outcome, rows["d"].Error));
        Assert.AreEqual((OperationOutcome.Failed, "disk gone"), (rows["e"].Outcome, rows["e"].Error), "the innermost message");
        Assert.AreEqual((OperationOutcome.Failed, "gave up inside"), (rows["f"].Outcome, rows["f"].Error), "a cancellation the caller did not ask for is a failure");

        var stats = tracker.Stats().Sources.Single().Operations.Single(o => o.Kind == OperationKind.Stat);
        Assert.AreEqual(5, stats.Finished);
        Assert.AreEqual(4, stats.Failed);
        Assert.AreEqual(1, stats.Cancelled);
        Assert.AreEqual(0, stats.Running);
    }

    [TestMethod]
    public void CountersArePerSourceAndKind()
    {
        var time = new FakeTime();
        var tracker = new OperationTracker(time);
        var list = tracker.Begin(Alpha, "alpha", OperationKind.List, "");
        time.Advance(TimeSpan.FromSeconds(2));
        list.AddItems(10);
        list.Complete();
        var again = tracker.Begin(Alpha, "alpha", OperationKind.List, "x");
        time.Advance(TimeSpan.FromSeconds(1));
        again.Complete();
        var read = tracker.Begin(Alpha, "alpha", OperationKind.ReadFile, "x/f");
        read.AddBytes(4096);
        tracker.Begin(Beta, "beta", OperationKind.Stat, "y").Complete();

        var stats = tracker.Stats();
        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, stats.Sources.Select(s => s.Name).ToList());
        var alpha = stats.Sources[0];
        Assert.AreEqual(Alpha, alpha.Id);
        CollectionAssert.AreEqual(new[] { OperationKind.List, OperationKind.ReadFile }, alpha.Operations.Select(o => o.Kind).ToList(), "kinds never asked are left out");
        var lists = alpha.Operations[0];
        Assert.AreEqual(2, lists.Finished);
        Assert.AreEqual(0, lists.Running);
        Assert.AreEqual(3.0, lists.TotalSeconds, 0.001);
        Assert.AreEqual(1.5, lists.AverageSeconds, 0.001);
        Assert.AreEqual(2.0, lists.MaxSeconds, 0.001);
        Assert.AreEqual(10, lists.Items);
        var reads = alpha.Operations[1];
        Assert.AreEqual(1, reads.Running);
        Assert.AreEqual(0, reads.Finished);
        Assert.AreEqual(0, reads.Bytes, "bytes are counted when the operation ends");
        Assert.AreEqual(1, stats.Running);
        Assert.AreEqual(3, stats.Finished);
        Assert.AreEqual(0, stats.Failed);

        read.Complete();
        Assert.AreEqual(4096, tracker.Stats().Sources[0].Operations[1].Bytes);

        tracker.Forget(Alpha);
        stats = tracker.Stats();
        Assert.AreEqual("beta", stats.Sources.Single().Name);
        Assert.IsTrue(tracker.Recent().All(r => r.SourceId == Beta), "a forgotten source's finished operations leave the list");
    }

    [TestMethod]
    public void TheNewestNameOfASourceIsTheOneTheStatsShow()
    {
        var tracker = new OperationTracker(new FakeTime());
        tracker.Begin(Alpha, "alpha", OperationKind.Stat, "a").Complete();
        tracker.Begin(Alpha, "alpha renamed", OperationKind.Stat, "b").Complete();
        Assert.AreEqual("alpha renamed", tracker.Stats().Sources.Single().Name);
        CollectionAssert.AreEquivalent(new[] { "alpha", "alpha renamed" }, tracker.Recent().Select(r => r.Source).ToList(), "each operation keeps the name it began under");
    }

    [TestMethod]
    public void TheCapDropsTheOldestFinished()
    {
        var tracker = new OperationTracker(new FakeTime(), keepFinishedAtMost: 3);
        for (var i = 0; i < 5; i++)
        {
            tracker.Begin(Alpha, "alpha", OperationKind.Stat, i.ToString()).Complete();
        }

        tracker.Begin(Alpha, "alpha", OperationKind.Stat, "running");
        CollectionAssert.AreEquivalent(new[] { "2", "3", "4", "running" }, tracker.Recent().Select(r => r.Path).ToList());
        Assert.AreEqual(5, tracker.Stats().Finished);
    }

    [TestMethod]
    public void AStormOfQuickCallsLeavesWhatTheServerWasAskedInSight()
    {
        var tracker = new OperationTracker(new FakeTime(), keepFinishedAtMost: 3);
        tracker.Begin(Alpha, "alpha", OperationKind.Upstream, "https://server.test/one", what: "GET").Complete();
        for (var i = 0; i < 50; i++)
        {
            tracker.Begin(Alpha, "alpha", OperationKind.Stat, i.ToString()).Complete();
        }

        CollectionAssert.AreEquivalent(new[] { "https://server.test/one", "47", "48", "49" }, tracker.Recent().Select(r => r.Path).ToList());

        for (var i = 0; i < 5; i++)
        {
            tracker.Begin(Alpha, "alpha", OperationKind.Upstream, $"https://server.test/{i}", what: "GET").Complete();
        }

        CollectionAssert.AreEquivalent(new[] { "https://server.test/2", "https://server.test/3", "https://server.test/4", "47", "48", "49" }, tracker.Recent().Select(r => r.Path).ToList(), "a storm of requests has a cap of its own too");
    }

    [TestMethod]
    public void WhatAServerWasAskedGoesAfterFiveSecondsAndWithItsSource()
    {
        var time = new FakeTime();
        var tracker = new OperationTracker(time);
        tracker.Begin(Alpha, "alpha", OperationKind.Upstream, "https://server.test/a", what: "GET").Complete();
        tracker.Begin(Beta, "beta", OperationKind.Upstream, "https://server.test/b", what: "GET").Complete();
        Assert.AreEqual(2, tracker.Recent().Count);
        tracker.Forget(Beta);
        Assert.AreEqual("alpha", tracker.Recent().Single().Source);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, tracker.Recent().Count);
        Assert.AreEqual(1, tracker.Stats().Finished, "the session's count stays");
    }

    [TestMethod]
    public void ANotedFailureEndsTheOperationFailedWhenItCompletes()
    {
        var tracker = new OperationTracker(new FakeTime());
        var op = tracker.Begin(Alpha, "alpha", OperationKind.ReadFile, "f");
        op.AddBytes(10);
        op.Note(new IOException("the connection dropped"));
        op.Note(new IOException("later"));
        Assert.AreEqual(OperationOutcome.Running, op.Outcome, "a note does not end it");
        op.Complete();
        var row = tracker.Recent().Single();
        Assert.AreEqual(OperationOutcome.Failed, row.Outcome);
        Assert.AreEqual("the connection dropped", row.Error, "the first note counts");
        Assert.AreEqual(10, row.Bytes);
    }

    [TestMethod]
    public void IdsAscendInTheOrderOperationsBegan()
    {
        var tracker = new OperationTracker(new FakeTime());
        var first = tracker.Begin(Alpha, "alpha", OperationKind.Stat, "1");
        var second = tracker.Begin(Alpha, "alpha", OperationKind.Stat, "2");
        Assert.IsTrue(second.Id > first.Id);
    }

    [TestMethod]
    public void KindsHaveWordsAndPaths()
    {
        Assert.AreEqual("List folder", OperationKind.List.Describe());
        Assert.AreEqual("Get metadata", OperationKind.Stat.Describe());
        Assert.AreEqual("Count files", OperationKind.Count.Describe());
        Assert.IsTrue(OperationKind.ReadFile.HasPath());
        Assert.IsFalse(OperationKind.Start.HasPath());
        Assert.IsFalse(OperationKind.Count.HasPath());
        Assert.IsFalse(OperationKind.Stop.HasPath());
        Assert.AreEqual("Asked of source", OperationKind.Upstream.Describe());
        Assert.IsTrue(OperationKind.Upstream.HasPath());
    }

    [TestMethod]
    public void OnlyARequestOfAServerIsAskedOfTheServer()
    {
        foreach (var kind in Enum.GetValues<OperationKind>())
        {
            Assert.AreEqual(kind == OperationKind.Upstream ? OperationAskedOf.Source : OperationAskedOf.AnyFS, kind.AskedOf(), kind.ToString());
        }
    }
}

[TestClass]
public class TrackedSourceTests
{
    private static async Task<(TrackedSource Source, OperationTracker Tracker, FakeTime Time)> TrackedAsync(ISource inner, string name = "mem")
    {
        var time = new FakeTime();
        var tracker = new OperationTracker(time);
        var handle = new SourceHandle(Guid.NewGuid(), name, "memory");
        var source = new TrackedSource(inner, handle, tracker);
        await source.InitialiseAsync(new TestSourceContext(name), CancellationToken.None);
        return (source, tracker, time);
    }

    [TestMethod]
    public async Task WhatASourceAsksItsServerIsARowOfItsOwn()
    {
        var (source, tracker, time) = await TrackedAsync(new MemorySource(), "lab");
        var server = source.UpstreamRows();

        var asked = server.Asked("GET", "https://server.test/api/a?page=2");
        asked.AddBytes(1000);
        time.Advance(TimeSpan.FromSeconds(2));
        var row = tracker.Recent().Single(r => r.Kind == OperationKind.Upstream);
        Assert.AreEqual(OperationOutcome.Running, row.Outcome);
        Assert.AreEqual(OperationAskedOf.Source, row.AskedOf);
        Assert.AreEqual("lab", row.Source, "under the name its source has now");
        Assert.AreEqual("GET", row.Name);
        Assert.AreEqual("https://server.test/api/a?page=2", row.Path);
        Assert.AreEqual(1000, row.Bytes);

        asked.Answered("200 OK", refused: false);
        asked.AddBytes(24);
        asked.Done();
        asked.Done();
        row = tracker.Recent().Single(r => r.Kind == OperationKind.Upstream);
        Assert.AreEqual(OperationOutcome.Succeeded, row.Outcome);
        Assert.AreEqual("200 OK", row.Answer);
        Assert.IsNull(row.Error);
        Assert.AreEqual(1024, row.Bytes);
        Assert.AreEqual(TimeSpan.FromSeconds(2), row.Duration);

        var refused = server.Asked("GET", "https://server.test/gone");
        refused.Answered("404 Not Found", refused: true);
        Assert.AreEqual(OperationOutcome.Running, tracker.Recent().Single(r => r.Path.EndsWith("gone", StringComparison.Ordinal)).Outcome, "its body may yet come");
        refused.Done();
        row = tracker.Recent().Single(r => r.Path.EndsWith("gone", StringComparison.Ordinal));
        Assert.AreEqual((OperationOutcome.Failed, "404 Not Found"), (row.Outcome, row.Error));

        var broke = server.Asked("GET", "https://server.test/broke");
        broke.Answered("200 OK", refused: false);
        broke.Failed(new IOException("the connection dropped"), CancellationToken.None);
        broke.Done();
        row = tracker.Recent().Single(r => r.Path.EndsWith("broke", StringComparison.Ordinal));
        Assert.AreEqual((OperationOutcome.Failed, "the connection dropped"), (row.Outcome, row.Error));

        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();
        var gaveUp = server.Asked("SFTP list", "/home/x");
        gaveUp.AddEntries(3);
        gaveUp.Failed(new OperationCanceledException(stopped.Token), stopped.Token);
        gaveUp.Done();
        row = tracker.Recent().Single(r => r.Path == "/home/x");
        Assert.AreEqual((OperationOutcome.Cancelled, "SFTP list", 3L), (row.Outcome, row.Name, row.Items));

        var stats = tracker.Stats().Sources.Single().Operations.Single(o => o.Kind == OperationKind.Upstream);
        Assert.AreEqual((4L, 2L, 1L, 0L), (stats.Finished, stats.Failed, stats.Cancelled, stats.Running));
        Assert.AreEqual(1024, stats.Bytes);
    }

    [TestMethod]
    public async Task EveryCallIsOneOperation()
    {
        var inner = new MemorySource().AddFile("docs/a.txt", "hello").AddDirectory("empty");
        var (source, tracker, _) = await TrackedAsync(inner);
        Assert.AreSame(inner, source.Inner);
        Assert.AreSame(inner, TrackedSource.Unwrap(source));
        Assert.AreSame(inner, TrackedSource.Unwrap(inner));
        Assert.AreEqual(inner.Capabilities, source.Capabilities);
        Assert.AreSame(inner.Counters, source.Counters);

        Assert.IsNotNull(await source.StatAsync("docs/a.txt", CancellationToken.None));
        Assert.IsNull(await source.StatAsync("docs/missing", CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "docs/", "empty/" }, await Handles.NamesAsync(source, ""));
        Assert.AreEqual("hello", await Handles.ReadTextAsync(source, "docs/a.txt"));
        await Handles.WriteTextAsync(source, "docs/b.txt", "written");
        await source.CreateDirectoryAsync("made", CancellationToken.None);
        await source.RenameAsync("docs/b.txt", "docs/c.txt", false, CancellationToken.None);
        await source.SetTimesAsync("docs/c.txt", null, DateTimeOffset.UnixEpoch, CancellationToken.None);
        await source.DeleteAsync("docs/c.txt", CancellationToken.None);
        await source.GetSpaceAsync("", CancellationToken.None);
        await Handles.ExpectAsync(VfsError.NotFound, () => Handles.NamesAsync(source, "nowhere"));
        await source.DisposeAsync();

        var rows = tracker.Recent().OrderBy(r => r.Id).ToList();
        CollectionAssert.AreEqual(
            new[]
            {
                OperationKind.Start, OperationKind.Stat, OperationKind.Stat, OperationKind.List, OperationKind.ReadFile, OperationKind.WriteFile, OperationKind.CreateDirectory,
                OperationKind.Rename, OperationKind.SetTimes, OperationKind.Delete, OperationKind.GetSpace, OperationKind.List, OperationKind.Stop,
            },
            rows.Select(r => r.Kind).ToList());
        Assert.IsTrue(rows.All(r => r.Source == "mem"));
        var failed = rows.Where(r => r.Outcome != OperationOutcome.Succeeded).ToList();
        Assert.AreEqual(1, failed.Count, string.Join("; ", failed.Select(r => $"{r.Kind} {r.Path}: {r.Error}")));
        Assert.AreEqual((OperationKind.List, "nowhere", OperationOutcome.Failed, "'nowhere' was not found"), (failed[0].Kind, failed[0].Path, failed[0].Outcome, failed[0].Error));

        Assert.IsNull(rows[1].Detail);
        Assert.AreEqual("not found", rows[2].Detail, "a stat that found nothing says so");
        Assert.AreEqual(2, rows[3].Items, "a listing counts its entries");
        Assert.AreEqual(5, rows[4].Bytes, "a read counts its bytes");
        Assert.AreEqual("docs/a.txt", rows[4].Path);
        Assert.AreEqual(7, rows[5].Bytes, "a write counts its bytes");
        Assert.AreEqual("docs/b.txt", rows[7].Path);
        Assert.AreEqual("to docs/c.txt", rows[7].Detail);
        Assert.AreEqual("", rows[0].Path);
        Assert.AreEqual("", rows[^1].Path);
    }

    [TestMethod]
    public async Task AFileIsOneOperationFromOpenToClose()
    {
        var inner = new MemorySource().AddFile("big.bin", new byte[10_000]);
        var (source, tracker, time) = await TrackedAsync(inner);
        var file = await source.OpenAsync("big.bin", VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[4000];
        await file.ReadAsync(0, buffer, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        var row = tracker.Recent().Single(r => r.Kind == OperationKind.ReadFile);
        Assert.AreEqual(OperationOutcome.Running, row.Outcome, "the operation runs until the handle is closed");
        Assert.AreEqual(4000, row.Bytes);
        Assert.AreEqual(TimeSpan.FromSeconds(1), row.Duration);
        Assert.AreEqual(10_000, file.Length);

        await file.ReadAsync(4000, buffer, CancellationToken.None);
        await file.DisposeAsync();
        row = tracker.Recent().Single(r => r.Kind == OperationKind.ReadFile);
        Assert.AreEqual(OperationOutcome.Succeeded, row.Outcome);
        Assert.AreEqual(8000, row.Bytes);
    }

    [TestMethod]
    public async Task AFailedReadOrCloseEndsTheFileOperationFailed()
    {
        var inner = new FailingFiles();
        var (source, tracker, _) = await TrackedAsync(inner);
        var file = await source.OpenAsync("f", VfsOpenMode.Read, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<IOException>(async () => await file.ReadAsync(0, new byte[10], CancellationToken.None));
        Assert.AreEqual(OperationOutcome.Running, tracker.Recent().Single(r => r.Kind == OperationKind.ReadFile).Outcome, "the handle is still open");
        await file.DisposeAsync();
        var read = tracker.Recent().Single(r => r.Kind == OperationKind.ReadFile);
        Assert.AreEqual((OperationOutcome.Failed, "the connection dropped"), (read.Outcome, read.Error));

        inner.FailOnClose = true;
        var upload = await source.OpenAsync("g", VfsOpenMode.Create, CancellationToken.None);
        await upload.WriteAsync(0, new byte[3], CancellationToken.None);
        await Handles.ExpectAsync(VfsError.IoError, () => upload.DisposeAsync().AsTask());
        var write = tracker.Recent().Single(r => r.Kind == OperationKind.WriteFile);
        Assert.AreEqual((OperationOutcome.Failed, "upload refused", 3L), (write.Outcome, write.Error, write.Bytes), "a write-on-close upload that fails ends the operation failed");

        await Handles.ExpectAsync(VfsError.NotFound, () => source.OpenAsync("missing", VfsOpenMode.Read, CancellationToken.None));
        var open = tracker.Recent().Single(r => r.Path == "missing");
        Assert.AreEqual((OperationKind.ReadFile, OperationOutcome.Failed), (open.Kind, open.Outcome), "an open that fails is a failed operation");
    }

    [TestMethod]
    public async Task AListingStoppedEarlyIsCancelled()
    {
        var inner = new MemorySource().AddFile("a", "1").AddFile("b", "2").AddFile("c", "3");
        var (source, tracker, _) = await TrackedAsync(inner);
        await foreach (var entry in source.ListAsync("", CancellationToken.None))
        {
            Assert.IsNotNull(entry);
            break;
        }

        var row = tracker.Recent().Single(r => r.Kind == OperationKind.List);
        Assert.AreEqual((OperationOutcome.Cancelled, "stopped early", 1L), (row.Outcome, row.Error, row.Items));
    }

    [TestMethod]
    public async Task AListingThatThrowsAtOnceDoesNotRunForever()
    {
        var inner = new FailingFiles { ListThrowsAtOnce = true };
        var (source, tracker, _) = await TrackedAsync(inner);
        await Handles.ExpectAsync(VfsError.Unavailable, () => Handles.NamesAsync(source, "x"));
        var row = tracker.Recent().Single(r => r.Kind == OperationKind.List);
        Assert.AreEqual((OperationOutcome.Failed, "the server is away"), (row.Outcome, row.Error), "a source that throws before its iterator begins");
    }

    [TestMethod]
    public async Task AListingWhoseEnumeratorFailsToCloseDoesNotRunForever()
    {
        var inner = new FailingFiles { ListFailsToClose = true };
        var (source, tracker, _) = await TrackedAsync(inner);
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            await foreach (var entry in source.ListAsync("", CancellationToken.None))
            {
                Assert.IsNotNull(entry);
                break;
            }
        });

        var row = tracker.Recent().Single(r => r.Kind == OperationKind.List);
        Assert.AreEqual(OperationOutcome.Cancelled, row.Outcome);
    }

    [TestMethod]
    public async Task AStartThatFailsIsAFailedStart()
    {
        var inner = new MemorySource { OnInitialise = (_, _) => throw new SourceFailedException("no such server") };
        var tracker = new OperationTracker(new FakeTime());
        var source = new TrackedSource(inner, new SourceHandle(Guid.NewGuid(), "mem", "memory"), tracker);
        await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(new TestSourceContext("mem"), CancellationToken.None));
        var row = tracker.Recent().Single();
        Assert.AreEqual((OperationKind.Start, OperationOutcome.Failed, "no such server"), (row.Kind, row.Outcome, row.Error));
    }

    /// <summary>Files whose reads fail, and whose close fails when asked: what a dropped connection and a refused upload look like.</summary>
    private sealed class FailingFiles : SourceBase
    {
        public override SourceCapabilities Capabilities => SourceCapabilities.Writable;

        public bool FailOnClose { get; set; }

        public bool ListThrowsAtOnce { get; set; }

        public bool ListFailsToClose { get; set; }

        public override IAsyncEnumerable<VfsEntry> ListAsync(string path, CancellationToken ct)
            => ListThrowsAtOnce ? throw VfsException.Unavailable("the server is away")
                : ListFailsToClose ? new ListingThatFailsToClose()
                : AsyncEnumerable.Empty<VfsEntry>();

        private sealed class ListingThatFailsToClose : IAsyncEnumerable<VfsEntry>, IAsyncEnumerator<VfsEntry>
        {
            public VfsEntry Current => VfsEntry.Directory("d");

            public IAsyncEnumerator<VfsEntry> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

            public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(true);

            public ValueTask DisposeAsync() => throw new IOException("the connection dropped");
        }

        public override Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
            => path == "missing" ? throw VfsException.NotFound(path) : Task.FromResult<IVfsFile>(new File(this));

        private sealed class File(FailingFiles owner) : IVfsFile
        {
            public long Length => 100;

            public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) => throw new IOException("the connection dropped");

            public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;

            public ValueTask SetLengthAsync(long length, CancellationToken ct) => ValueTask.CompletedTask;

            public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

            public ValueTask DisposeAsync() => owner.FailOnClose ? throw new VfsException(VfsError.IoError, "upload refused") : ValueTask.CompletedTask;
        }
    }
}
