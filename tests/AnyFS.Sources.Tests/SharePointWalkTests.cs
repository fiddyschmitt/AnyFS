using System.Text;
using AnyFS.Sdk;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The walk of a SharePoint source's whole tree (every site at once, a few folders of each at a time) and the listings kept
/// from it: answered from what the source last saw, listed afresh on a refresh (a folder listed twice within a few seconds)
/// and by the next walk. Against the in-memory store.
/// </summary>
[TestClass]
public class SharePointWalkTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The tenant of <see cref="SharePointOtherSitesTests.Tenant"/> with a dozen folders of two files in each of three sites' Documents and a few in a subsite's.</summary>
    private static MemoryDocumentStore BusyTenant()
    {
        var store = SharePointOtherSitesTests.Tenant();
        foreach (var library in new[] { "/Shared Documents", "/sites/Axle/Shared Documents", "/teams/Ops/Shared Documents" })
        {
            for (var i = 0; i < 12; i++)
            {
                store.Seed(library, $"folder{i}/a.txt", Encoding.UTF8.GetBytes("a"));
                store.Seed(library, $"folder{i}/b.txt", Encoding.UTF8.GetBytes("b"));
            }
        }

        store.Seed("/Foo/Shared Documents", "plans/plan.txt", Encoding.UTF8.GetBytes("plan"));
        store.ListingDelay = TimeSpan.FromMilliseconds(30);
        return store;
    }

    [TestMethod]
    public async Task TheCountWalksEverySiteAtOnceAFewFoldersOfEachAtATime()
    {
        var store = BusyTenant();
        await using var source = await MemorySharePointSource.StartAsync(store, otherSites: true);
        await source.CountFilesAsync(None);

        // the same numbers as the host's generic walk of the same tree
        var expected = new SourceCounters();
        await TreeCount.CountAsync(source, expected, TreeCount.DefaultParallelism, null, None);
        Assert.AreEqual(expected.FilesKnown, source.Counters.FilesKnown);
        Assert.AreEqual(expected.DirectoriesKnown, source.Counters.DirectoriesKnown);
        Assert.AreEqual(3 * 24 + 1, source.Counters.FilesKnown);

        foreach (var site in new[] { "", "/sites/Axle", "/teams/Ops", "/Foo" })
        {
            Assert.IsTrue(store.MostAtOnce(site) <= SharePointSource.WalkPerSite, $"{site}: {store.MostAtOnce(site)} at once");
        }

        Assert.IsTrue(store.MostAtOnce("") > 1, "a site's folders are listed several at a time");
        Assert.IsTrue(store.MostSitesAtOnce >= 3, $"the sites are walked at the same time ({store.MostSitesAtOnce} at most)");
    }

    [TestMethod]
    public async Task KeptListingsAreWhatTheSourceLastSawUntilRefreshed()
    {
        var store = MemoryDocumentStore.Sample();
        store.Seed("/Shared Documents", "fixture/readme.txt", Encoding.UTF8.GetBytes("readme"));
        await using var source = await MemorySharePointSource.StartAsync(store, walkEvery: TimeSpan.FromHours(1));
        await source.CountFilesAsync(None);
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
        var listed = store.FolderListings;

        // changed outside AnyFS: not seen until asked for
        store.Seed("/Shared Documents", "fixture/outside.txt", Encoding.UTF8.GetBytes("outside"));
        CollectionAssert.AreEqual(new[] { "readme.txt" }, await SpsTestServer.NamesAsync(source, "Documents/fixture"), "from what the walk saw");
        Assert.IsNull(await source.StatAsync("Documents/fixture/outside.txt", None));
        Assert.AreEqual(listed, store.FolderListings, "no request");

        CollectionAssert.AreEquivalent(new[] { "readme.txt", "outside.txt" }, await SpsTestServer.NamesAsync(source, "Documents/fixture"), "listed again at once: refresh");
        Assert.IsNotNull(await source.StatAsync("Documents/fixture/outside.txt", None));

        // what is written through the source shows at once
        await using (var file = await source.OpenAsync("Documents/fixture/made.txt", VfsOpenMode.CreateNew, None))
        {
            await file.WriteAsync(0, "made"u8.ToArray(), None);
        }

        Assert.AreEqual(4, (await source.StatAsync("Documents/fixture/made.txt", None))?.Length);
    }

    [TestMethod]
    public async Task TheNextWalkBringsInWhatChangedOutside()
    {
        var store = MemoryDocumentStore.Sample();
        store.Seed("/Shared Documents", "fixture/readme.txt", Encoding.UTF8.GetBytes("readme"));
        await using var source = await MemorySharePointSource.StartAsync(store, walkEvery: TimeSpan.FromMilliseconds(300));
        await source.CountFilesAsync(None);
        store.Seed("/Shared Documents", "fixture/later.txt", Encoding.UTF8.GetBytes("later"));
        store.Seed("/Foo/Bar/Shared", "deep/deeper.txt", Encoding.UTF8.GetBytes("deeper"));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while ((await source.StatAsync("Documents/fixture/later.txt", None) is null || await source.StatAsync("Subsites/Foo/Subsites/Bar/Shared/deep/deeper.txt", None) is null) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.IsNotNull(await source.StatAsync("Documents/fixture/later.txt", None));
        Assert.IsNotNull(await source.StatAsync("Subsites/Foo/Subsites/Bar/Shared/deep/deeper.txt", None), "a subsite's subsite too");
    }
}
