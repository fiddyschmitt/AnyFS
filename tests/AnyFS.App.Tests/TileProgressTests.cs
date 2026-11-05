using AnyFS.App.Gui.ViewModels;
using AnyFS.Core.Status;

namespace AnyFS.App.Tests;

/// <summary>A tile's bar: how far a count has got where its source can tell, and one that goes on and on where it cannot.</summary>
[TestClass]
public class TileProgressTests
{
    private static SourceStatus Source(double? progress, bool counting = true, bool enabled = true, string state = nameof(ComponentState.Enumerating)) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Work GitLab",
        Type = "gitlab",
        Enabled = enabled,
        State = state,
        Text = "Counting files",
        Progress = progress,
        Counting = counting,
        Files = 1234,
    };

    private static SourceTileViewModel SourceTile(SourceStatus status)
    {
        var tile = new SourceTileViewModel(status.Id, inCollection: true, _ => Task.CompletedTask, null, () => Task.CompletedTask);
        tile.Update(status, "GitLab", readOnlyLocked: true, [], []);
        return tile;
    }

    private static CollectionTileViewModel CollectionTile(params SourceStatus[] sources)
    {
        var tile = new CollectionTileViewModel(Guid.NewGuid(), _ => Task.CompletedTask);
        tile.Update(new CollectionStatus { Id = Guid.NewGuid(), Name = "work", Enabled = true, Colour = "Green", Text = "ready", Sources = [.. sources] }, []);
        return tile;
    }

    [TestMethod]
    public void ASourceThatCanTellHasItsBarInPlaceOfTheOneThatGoesOnAndOn()
    {
        var tile = SourceTile(Source(0.25));
        Assert.IsFalse(tile.Counting);
        Assert.IsTrue(tile.HasProgress);
        Assert.AreEqual(0.25, tile.Progress);
    }

    [TestMethod]
    public void ASourceThatCannotTellHasTheBarThatGoesOnAndOn()
    {
        var tile = SourceTile(Source(null));
        Assert.IsTrue(tile.Counting);
        Assert.IsFalse(tile.HasProgress);
    }

    [TestMethod]
    public void ASourceThatHasFinishedHasNeither()
    {
        var tile = SourceTile(Source(null, counting: false, state: nameof(ComponentState.Ready)));
        Assert.IsFalse(tile.Counting);
        Assert.IsFalse(tile.HasProgress);
    }

    [TestMethod]
    public void ATileFollowsItsSourceFromOneBarToTheOther()
    {
        var status = Source(null);
        var tile = SourceTile(status);
        Assert.IsTrue(tile.Counting);
        status.Progress = 0.5;
        tile.Update(status, "GitLab", readOnlyLocked: true, [], []);
        Assert.IsFalse(tile.Counting);
        Assert.AreEqual(0.5, tile.Progress);
        status.Progress = null;
        status.Counting = false;
        tile.Update(status, "GitLab", readOnlyLocked: true, [], []);
        Assert.IsFalse(tile.Counting);
        Assert.IsFalse(tile.HasProgress);
    }

    [TestMethod]
    public void ACollectionShowsHowFarItsSourcesHaveGotOnAverage()
    {
        var tile = CollectionTile(Source(0.25), Source(0.75));
        Assert.IsFalse(tile.Counting);
        Assert.IsTrue(tile.HasProgress);
        Assert.AreEqual(0.5, tile.Progress);
    }

    [TestMethod]
    public void ACollectionGoesOnAndOnWhileAnySourceCountingCannotTell()
    {
        var tile = CollectionTile(Source(0.25), Source(null));
        Assert.IsTrue(tile.Counting);
        Assert.IsFalse(tile.HasProgress);
    }

    [TestMethod]
    public void ACollectionLooksOnlyAtTheSourcesStillCounting()
    {
        var tile = CollectionTile(Source(0.5), Source(null, counting: false, state: nameof(ComponentState.Ready)), Source(null, enabled: false, state: nameof(ComponentState.Disabled)));
        Assert.IsFalse(tile.Counting);
        Assert.AreEqual(0.5, tile.Progress);
    }

    [TestMethod]
    public void ACollectionWithNothingCountingHasNeither()
    {
        var tile = CollectionTile(Source(null, counting: false, state: nameof(ComponentState.Ready)));
        Assert.IsFalse(tile.Counting);
        Assert.IsFalse(tile.HasProgress);
        var none = CollectionTile(Source(null, enabled: false, state: nameof(ComponentState.Disabled)));
        Assert.IsFalse(none.Counting);
        Assert.IsFalse(none.HasProgress);
        Assert.AreEqual("—", none.FilesText);
    }
}
