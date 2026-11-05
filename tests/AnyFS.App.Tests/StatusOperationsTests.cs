using AnyFS.App.Cli;
using AnyFS.Core.Operations;
using AnyFS.Sdk.Testing;

namespace AnyFS.App.Tests;

/// <summary><c>anyfs status --operations</c>'s table, from the counters status.json carries.</summary>
[TestClass]
public class StatusOperationsTests
{
    private static string Print(OperationStatsSnapshot stats)
    {
        using var text = new StringWriter();
        Commands.PrintOperations(text, stats);
        return text.ToString();
    }

    [TestMethod]
    public void NothingAskedYetSaysSo()
    {
        var printed = Print(new OperationStatsSnapshot());
        StringAssert.Contains(printed, "Operations this session:");
        StringAssert.Contains(printed, "none yet");
    }

    [TestMethod]
    public void ARowPerSourceAndKindWithWhatWentBy()
    {
        var time = new FakeTime();
        var tracker = new OperationTracker(time);
        var longName = new string('n', 40);
        var alpha = Guid.NewGuid();

        var read = tracker.Begin(alpha, "alpha", OperationKind.ReadFile, "a.txt");
        read.AddBytes(2048);
        time.Advance(TimeSpan.FromSeconds(3));
        read.Complete();
        var list = tracker.Begin(alpha, "alpha", OperationKind.List, "");
        list.AddItems(1200);
        time.Advance(TimeSpan.FromSeconds(1));
        list.Fail(new IOException("gone"));
        tracker.Begin(alpha, "alpha", OperationKind.Stat, "b");
        var count = tracker.Begin(Guid.NewGuid(), longName, OperationKind.Count, "");
        count.AddItems(7);
        count.Complete();
        var request = tracker.Begin(alpha, "alpha", OperationKind.Upstream, "https://server.test/tree", what: "GET");
        request.AddBytes(3 * 1024);
        request.Note("404 Not Found");
        request.Complete();

        var lines = Print(tracker.Stats()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.AreEqual(7, lines.Count, string.Join(" | ", lines));
        StringAssert.StartsWith(lines[1].Trim(), "Source");

        var requests = lines.Single(l => l.Contains("Asked of source", StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[] { "alpha", "Asked", "of", "source", "1", "1", "0" }, requests.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(7).ToArray(), "what was asked of what is behind them, however many verbs that took");
        StringAssert.EndsWith(requests, "3 KB");

        var reads = lines.Single(l => l.Contains("Read file", StringComparison.Ordinal));
        StringAssert.EndsWith(reads, "2 KB");
        StringAssert.Contains(reads, 3.ToString("0.00") + " s");

        var lists = lines.Single(l => l.Contains("List folder", StringComparison.Ordinal));
        StringAssert.EndsWith(lists, $"{1200:N0} entries");

        var stats = lines.Single(l => l.Contains("Get metadata", StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[] { "alpha", "Get", "metadata", "0", "0", "1" }, stats.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6).ToArray(), "source, operation, finished, failed, running");

        var counts = lines.Single(l => l.Contains("Count files", StringComparison.Ordinal));
        StringAssert.Contains(counts, new string('n', 21) + "...", "a long name is cut to its column");
        StringAssert.EndsWith(counts, "7 files");
        Assert.IsTrue(lines.All(l => l.All(c => c < 128)), "plain ASCII: a console's code page shows it whatever it is");
    }
}
