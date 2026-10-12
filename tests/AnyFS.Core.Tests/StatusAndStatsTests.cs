using AnyFS.Core.Stats;
using AnyFS.Core.Status;

namespace AnyFS.Core.Tests;

[TestClass]
public class StatusRollupTests
{
    [TestMethod]
    [DataRow(ComponentState.Disabled, StatusColour.Grey)]
    [DataRow(ComponentState.Starting, StatusColour.Yellow)]
    [DataRow(ComponentState.Enumerating, StatusColour.Yellow)]
    [DataRow(ComponentState.Ready, StatusColour.Green)]
    [DataRow(ComponentState.Degraded, StatusColour.Yellow)]
    [DataRow(ComponentState.Failed, StatusColour.Red)]
    [DataRow(ComponentState.Stopping, StatusColour.Yellow)]
    public void ColourTable(ComponentState state, StatusColour colour)
    {
        Assert.AreEqual(colour, new ComponentStatus(state).Colour);
    }

    [TestMethod]
    public void CollectionIsTheWorstOfItsParts()
    {
        var sources = new List<ComponentStatus> { ComponentStatus.ReadyStatus, ComponentStatus.ReadyStatus, new(ComponentState.Starting, "Logging in") };
        var mounts = new List<ComponentStatus> { new(ComponentState.Failed, "port in use") };
        var (colour, text) = StatusRollup.ForCollection(true, sources, mounts);
        Assert.AreEqual(StatusColour.Red, colour);
        Assert.AreEqual("1 source starting, 2 sources ready, 1 mount failed", text);

        (colour, text) = StatusRollup.ForCollection(true, [ComponentStatus.ReadyStatus], [ComponentStatus.ReadyStatus]);
        Assert.AreEqual(StatusColour.Green, colour);
        Assert.AreEqual("1 source ready, 1 mount ready", text);

        (colour, _) = StatusRollup.ForCollection(true, [ComponentStatus.ReadyStatus, new(ComponentState.Degraded, "x")], []);
        Assert.AreEqual(StatusColour.Yellow, colour);
    }

    [TestMethod]
    public void DisabledAndEmptyCollectionsAreGrey()
    {
        Assert.AreEqual((StatusColour.Grey, "Disabled"), StatusRollup.ForCollection(false, [new(ComponentState.Failed, "x")], []));
        Assert.AreEqual((StatusColour.Grey, "No sources"), StatusRollup.ForCollection(true, [], []));
        Assert.AreEqual((StatusColour.Grey, "Nothing enabled"), StatusRollup.ForCollection(true, [ComponentStatus.DisabledStatus], [ComponentStatus.DisabledStatus]));
    }

    [TestMethod]
    public void UsableStates()
    {
        Assert.IsTrue(ComponentStatus.ReadyStatus.IsUsable);
        Assert.IsTrue(new ComponentStatus(ComponentState.Degraded).IsUsable);
        Assert.IsTrue(new ComponentStatus(ComponentState.Enumerating).IsUsable);
        Assert.IsFalse(new ComponentStatus(ComponentState.Starting).IsUsable);
        Assert.IsFalse(new ComponentStatus(ComponentState.Failed).IsUsable);
    }
}

[TestClass]
public class StatsCollectorTests
{
    [TestMethod]
    public void SessionAndLifetimeAreKeptApart()
    {
        var stats = new StatsCollector();
        stats.LoadLifetime(new StatsFile
        {
            Since = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            FilesServed = 10,
            BytesRead = 1000,
            BytesWritten = 50,
            Collections = { ["ben"] = new CollectionCounters { FilesServed = 10, BytesRead = 1000, BytesWritten = 50 } },
        });

        stats.FileServed("ben");
        stats.BytesRead("ben", 300);
        stats.BytesWritten("ben", 20);
        stats.FileServed("other");
        stats.BytesRead("other", 7);

        var s = stats.Snapshot();
        Assert.AreEqual(2, s.Session.FilesServed);
        Assert.AreEqual(12, s.Total.FilesServed);
        Assert.AreEqual(307, s.Session.BytesRead);
        Assert.AreEqual(1307, s.Total.BytesRead);
        Assert.AreEqual(20, s.Session.BytesWritten);
        Assert.AreEqual(70, s.Total.BytesWritten);
        Assert.AreEqual(11, s.Collections["ben"].Total.FilesServed);
        Assert.AreEqual(1, s.Collections["other"].Session.FilesServed);
        Assert.AreEqual(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), s.TotalsSince);

        var file = stats.ToFile();
        Assert.AreEqual(12, file.FilesServed);
        Assert.AreEqual(1307, file.BytesRead);
        Assert.AreEqual(70, file.BytesWritten);
        Assert.AreEqual(1, file.Collections["other"].FilesServed);
    }

    [TestMethod]
    public void ConnectionsAreCountedPerCollectionAndOverall()
    {
        var stats = new StatsCollector();
        stats.ConnectionOpened("ben");
        stats.ConnectionOpened("ben");
        stats.ConnectionOpened("bob");
        stats.ConnectionClosed("ben");
        var s = stats.Snapshot();
        Assert.AreEqual(2, s.ActiveConnections);
        Assert.AreEqual(1, s.Collections["ben"].ActiveConnections);
        Assert.AreEqual(1, s.Collections["bob"].ActiveConnections);
    }

    [TestMethod]
    public void ResetTotalsKeepsTheSession()
    {
        var stats = new StatsCollector();
        stats.LoadLifetime(new StatsFile { FilesServed = 100, BytesRead = 5000 });
        stats.FileServed("ben");
        stats.BytesRead("ben", 10);
        stats.ResetTotals();
        var s = stats.Snapshot();
        Assert.AreEqual(1, s.Session.FilesServed);
        Assert.AreEqual(0, s.Total.FilesServed);
        Assert.AreEqual(0, s.Total.BytesRead);
        stats.FileServed("ben");
        s = stats.Snapshot();
        Assert.AreEqual(2, s.Session.FilesServed);
        Assert.AreEqual(1, s.Total.FilesServed);
        Assert.AreEqual(1, stats.ToFile().FilesServed);
    }

    [TestMethod]
    public void NothingLoadedStartsTotalsNow()
    {
        var stats = new StatsCollector();
        stats.LoadLifetime(null);
        var s = stats.Snapshot();
        Assert.AreEqual(stats.SessionStarted, s.TotalsSince);
        Assert.AreEqual(0, s.Total.FilesServed);
    }
}
