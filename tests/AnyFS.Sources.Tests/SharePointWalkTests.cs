using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The walk of a SharePoint source's whole tree (every site at once, a few folders of each at a time) and the listings kept
/// from it: answered from what the source last saw until the next walk lists them afresh. Against the in-memory store.
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
            Assert.IsTrue(store.MostAtOnce(site) <= SharePointSettings.DefaultWalkPerSite, $"{site}: {store.MostAtOnce(site)} at once");
        }

        Assert.IsTrue(store.MostAtOnce("") > 1, "a site's folders are listed several at a time");
        Assert.IsTrue(store.MostSitesAtOnce >= 3, $"the sites are walked at the same time ({store.MostSitesAtOnce} at most)");
    }

    [TestMethod]
    public async Task AfterTheCountAPassOverEverythingAsksTheServerNothing()
    {
        var store = BusyTenant();
        await using var source = await MemorySharePointSource.StartAsync(store, otherSites: true, walkEvery: TimeSpan.FromHours(1));
        await source.CountFilesAsync(None);
        var before = store.FolderListings;
        var folders = await ListEverything.OfAsync(source);
        Assert.IsTrue(folders > 10, folders.ToString());
        Assert.AreEqual(before, store.FolderListings, "folders listed from the store after the walk");
    }

    [TestMethod]
    public async Task KeptListingsAreWhatTheSourceLastSawUntilTheNextWalk()
    {
        var store = MemoryDocumentStore.Sample();
        store.Seed("/Shared Documents", "fixture/readme.txt", Encoding.UTF8.GetBytes("readme"));
        await using var source = await MemorySharePointSource.StartAsync(store, walkEvery: TimeSpan.FromHours(1));
        await source.CountFilesAsync(None);
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
        var listed = store.FolderListings;

        // changed outside AnyFS: not seen until the next walk, however often the folder is listed (programs list a folder
        // over and over, opening a file among them)
        store.Seed("/Shared Documents", "fixture/outside.txt", Encoding.UTF8.GetBytes("outside"));
        CollectionAssert.AreEqual(new[] { "readme.txt" }, await SpsTestServer.NamesAsync(source, "Documents/fixture"), "from what the walk saw");
        CollectionAssert.AreEqual(new[] { "readme.txt" }, await SpsTestServer.NamesAsync(source, "Documents/fixture"), "listed again at once: still from what the walk saw");
        Assert.IsNull(await source.StatAsync("Documents/fixture/outside.txt", None));
        Assert.AreEqual(listed, store.FolderListings, "no request");

        // what is written through the source shows at once
        await using (var file = await source.OpenAsync("Documents/fixture/made.txt", VfsOpenMode.CreateNew, None))
        {
            await file.WriteAsync(0, "made"u8.ToArray(), None);
        }

        Assert.AreEqual(4, (await source.StatAsync("Documents/fixture/made.txt", None))?.Length);
    }

    private static async Task<string> ReadAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, None);
        var bytes = new byte[file.Length];
        Assert.AreEqual(bytes.Length, await file.ReadFullyAsync(0, bytes, None));
        return Encoding.UTF8.GetString(bytes);
    }

    [TestMethod]
    public async Task WhatAListingToldOfEachItemIsNotHeldASecondTime()
    {
        // the source remembered every item every listing had named, by its path, for as long as it ran: half a gigabyte
        // for a million (the review of 21-Nov-2026, P7). What an open needs of one is in the listing kept: its version
        // and its length
        var store = BusyTenant();
        await using var source = await MemorySharePointSource.StartAsync(store, otherSites: true, walkEvery: TimeSpan.FromHours(1));
        await source.CountFilesAsync(None);
        Assert.AreEqual(3 * 24 + 1, source.Counters.FilesKnown);
        Assert.AreEqual(0, source.ItemsRemembered, "after a walk of every folder");

        // and each opens as before, nothing asked of SharePoint but its bytes: its folder was listed in this run
        var lookups = store.ItemLookups;
        Assert.AreEqual("a", await ReadAsync(source, "Documents/folder3/a.txt"));
        Assert.AreEqual("b", await ReadAsync(source, "Sites/Axle/Documents/folder7/b.txt"));
        Assert.AreEqual("plan", await ReadAsync(source, "Subsites/Foo/Documents/plans/plan.txt"));
        Assert.AreEqual(lookups, store.ItemLookups);
        Assert.AreEqual(0, source.ItemsRemembered);
    }

    [TestMethod]
    public async Task AnItemAskedForOrWrittenIsRememberedUpToANumberOfThem()
    {
        var store = MemoryDocumentStore.Sample();
        for (var i = 0; i < 6; i++)
        {
            store.Seed("/Shared Documents", $"kept/file{i}.txt", Encoding.UTF8.GetBytes($"file {i}"));
        }

        using var context = new TestSourceContext("sharepoint");
        store.Stamps["/Shared Documents"] = "s1";
        await using (var first = await MemorySharePointSource.StartAsync(store, walkEvery: TimeSpan.FromHours(1), context: context))
        {
            await first.CountFilesAsync(None);
        }

        // the next start's walk keeps the library by its stamp: no folder of it is listed in this run, so a file's first
        // open asks SharePoint for it, and remembers the answer
        var source = new MemorySharePointSource(store, null, false, null, null, null, TimeSpan.FromHours(1), context) { Remembers = 4 };
        await source.InitialiseAsync(context, None);
        await using (source)
        {
            await source.CountFilesAsync(None);
            var listed = store.FolderListings;
            var lookups = store.ItemLookups;
            for (var i = 0; i < 3; i++)
            {
                Assert.AreEqual($"file {i}", await ReadAsync(source, $"Documents/kept/file{i}.txt"));
            }

            Assert.AreEqual(lookups + 3, store.ItemLookups, "each asked for once");
            Assert.AreEqual($"file 1", await ReadAsync(source, "Documents/kept/file1.txt"));
            Assert.AreEqual(lookups + 3, store.ItemLookups, "and remembered");
            Assert.AreEqual(3, source.ItemsRemembered);

            // one written is remembered as one asked for is
            await using (var file = await source.OpenAsync("Documents/kept/made.txt", VfsOpenMode.CreateNew, None))
            {
                await file.WriteAsync(0, "made"u8.ToArray(), None);
            }

            Assert.AreEqual("made", await ReadAsync(source, "Documents/kept/made.txt"));
            Assert.AreEqual(lookups + 3, store.ItemLookups);
            Assert.AreEqual(4, source.ItemsRemembered);

            // past the number, they start again: one let go is asked for again when it is next opened
            Assert.AreEqual("file 3", await ReadAsync(source, "Documents/kept/file3.txt"));
            Assert.AreEqual(1, source.ItemsRemembered);
            Assert.AreEqual("file 0", await ReadAsync(source, "Documents/kept/file0.txt"));
            Assert.AreEqual(lookups + 5, store.ItemLookups);
            Assert.AreEqual(listed, store.FolderListings, "no folder listed for any of it");

            // a folder listed in this run needs none of them
            await source.WalkAsync(new SourceCounters(), None);
            Assert.AreEqual(0, source.ItemsRemembered, "what a listing has told since is in the listing");
            lookups = store.ItemLookups;
            Assert.AreEqual("file 5", await ReadAsync(source, "Documents/kept/file5.txt"));
            Assert.AreEqual(lookups, store.ItemLookups);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TimesSetOnAFileAfterItIsClosedDoNotListItsFolderAgain(bool kept)
    {
        // rsync and cp -p set a file's times after they close it. Each time the folder's listing was thrown away, and the
        // next file's open listed the folder again, whole: a copy of N files into a folder listed it N times (the review
        // of 21-Nov-2026, P7)
        var store = MemoryDocumentStore.Sample();
        store.Seed("/Shared Documents", "in/there.txt", Encoding.UTF8.GetBytes("there"));
        await using var source = await MemorySharePointSource.StartAsync(store, walkEvery: kept ? TimeSpan.FromHours(1) : null);
        if (kept)
        {
            await source.CountFilesAsync(None);
        }

        Assert.AreEqual(1, (await SpsTestServer.NamesAsync(source, "Documents/in")).Count);
        var before = store.FolderListings;
        var when = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        for (var i = 0; i < 10; i++)
        {
            var path = $"Documents/in/copy{i}.txt";
            await using (var file = await source.OpenAsync(path, VfsOpenMode.CreateNew, None))
            {
                await file.WriteAsync(0, "copied"u8.ToArray(), None);
            }

            await source.SetTimesAsync(path, when.AddHours(-i), when.AddMinutes(i), None);
            var entry = await source.StatAsync(path, None);
            Assert.AreEqual(when.AddMinutes(i), entry?.Modified, "the time set shows at once");
            Assert.AreEqual(when.AddHours(-i), entry?.Created);
            Assert.AreEqual(6, entry?.Length);
        }

        Assert.AreEqual(11, (await SpsTestServer.NamesAsync(source, "Documents/in")).Count);
        Assert.AreEqual(before, store.FolderListings, "the folder is not listed again for the times of what was written into it");

        // a folder's too, and what SharePoint will not do forgets the listing as before
        await source.CreateDirectoryAsync("Documents/in/sub", None);
        await source.SetTimesAsync("Documents/in/sub", null, when, None);
        Assert.AreEqual(when, (await source.StatAsync("Documents/in/sub", None))?.Modified);
        Assert.AreEqual(before, store.FolderListings);
        store.Seed("/Shared Documents", "in/made outside.txt", Encoding.UTF8.GetBytes("outside"));
        store.FailSetTimes = true;
        await Assert.ThrowsExactlyAsync<VfsException>(() => source.SetTimesAsync("Documents/in/there.txt", null, when, None));
        Assert.IsNotNull(await source.StatAsync("Documents/in/made outside.txt", None), "listed afresh after a failure: what was made outside shows");
        Assert.AreEqual(before + 1, store.FolderListings);
    }

    [TestMethod]
    public async Task ALibraryWhoseStampHoldsIsKeptAcrossARestartAndOneWhoseStampMovedIsWalkedAgain()
    {
        var store = MemoryDocumentStore.Sample();
        store.Seed("/Shared Documents", "fixture/readme.txt", Encoding.UTF8.GetBytes("readme"));
        store.Seed("/SiteAssets", "img/logo.png", Encoding.UTF8.GetBytes("png"));
        store.Stamps["/Shared Documents"] = "d1";
        store.Stamps["/SiteAssets"] = "a1";
        using var context = new TestSourceContext("sharepoint");
        var first = await MemorySharePointSource.StartAsync(store, walkEvery: TimeSpan.FromHours(1), context: context);
        await first.CountFilesAsync(None);
        var files = first.Counters.FilesKnown;
        await first.DisposeAsync();

        // Site Assets changed, and its stamp with it; Documents changed too, but its stamp did not: the kept listing stands
        store.Seed("/SiteAssets", "img/new.png", Encoding.UTF8.GetBytes("png"));
        store.Stamps["/SiteAssets"] = "a2";
        store.Seed("/Shared Documents", "fixture/unseen.txt", Encoding.UTF8.GetBytes("unseen"));
        var listed = store.FolderListings;
        await using var second = await MemorySharePointSource.StartAsync(store, walkEvery: TimeSpan.FromHours(1), context: context);
        await second.CountFilesAsync(None);
        Assert.AreEqual(EnumerationState.Complete, second.Counters.State);
        Assert.AreEqual(files + 1, second.Counters.FilesKnown, "the kept library counted with the listed");
        Assert.IsNotNull(await second.StatAsync("Site Assets/img/new.png", None), "its stamp moved: walked again");
        Assert.IsNull(await second.StatAsync("Documents/fixture/unseen.txt", None), "its stamp holds: kept as the last run saw it");
        Assert.IsTrue(store.FolderListings < listed + 6, $"the sites' own folders and Site Assets listed, not Documents: {store.FolderListings - listed} listings");
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
