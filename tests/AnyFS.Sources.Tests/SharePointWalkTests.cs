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

    // --- a changed library is walked by what changed in it (the review of 21-Nov-2026, P7) -----------------------------
    // A library whose stamp had moved was listed whole, a request a folder, whatever had changed in it: one new file in a
    // library of 200,000 folders cost 200,000 requests. The store says what changed since the last walk (Graph's delta,
    // SharePoint's change log), and the walk lists the folders the changes are in and those on the way down to them.

    private const string Documents = "/Shared Documents";

    /// <summary>A store that says what changed, its Documents holding three areas of four folders, two files in each, and one folder two deeper: 17 folders, 25 files.</summary>
    private static MemoryDocumentStore ChangingLibrary()
    {
        var store = MemoryDocumentStore.Sample();
        store.TellsChanges = true;
        for (var area = 0; area < 3; area++)
        {
            for (var folder = 0; folder < 4; folder++)
            {
                store.Seed(Documents, $"area{area}/folder{folder}/one.txt", Encoding.UTF8.GetBytes("one"));
                store.Seed(Documents, $"area{area}/folder{folder}/two.txt", Encoding.UTF8.GetBytes("two"));
            }
        }

        store.Seed(Documents, "area1/folder2/deep/deeper/old.txt", Encoding.UTF8.GetBytes("old"));
        return store;
    }

    /// <summary>A walk that keeps what the stamps say has not changed: how many folders it listed, and what it counted.</summary>
    private static async Task<(int Listed, long Files, long Folders)> WalkAsync(MemorySharePointSource source, MemoryDocumentStore store)
    {
        var before = store.FolderListings;
        var counters = new SourceCounters();
        Assert.IsTrue(await source.WalkAsync(counters, full: false, None), "the walk went everywhere");
        return (store.FolderListings - before, counters.FilesKnown, counters.DirectoriesKnown);
    }

    [TestMethod]
    public async Task AWalkAfterAChangeListsTheFoldersOnTheWayToItAndNoOthers()
    {
        var store = ChangingLibrary();
        await using var source = await MemorySharePointSource.StartAsync(store, walkEvery: TimeSpan.FromHours(1));
        await source.CountFilesAsync(None);
        var (files, folders) = (source.Counters.FilesKnown, source.Counters.DirectoriesKnown);
        Assert.AreEqual(25, files);

        // nothing changed: nothing of any library is listed
        Assert.AreEqual((0, files, folders), await WalkAsync(source, store));

        // one new file, four folders down: the library's own folder and the four on the way, of its 18
        store.Seed(Documents, "area1/folder2/deep/deeper/new.txt", Encoding.UTF8.GetBytes("new"));
        Assert.AreEqual((5, files + 1, folders), await WalkAsync(source, store));
        Assert.AreEqual(3, (await source.StatAsync("Documents/area1/folder2/deep/deeper/new.txt", None))?.Length);

        // one changed where it is
        store.Seed(Documents, "area0/folder1/two.txt", Encoding.UTF8.GetBytes("two, and more"));
        Assert.AreEqual((3, files + 1, folders), await WalkAsync(source, store));
        Assert.AreEqual(13, (await source.StatAsync("Documents/area0/folder1/two.txt", None))?.Length);

        // and nothing again
        Assert.AreEqual((0, files + 1, folders), await WalkAsync(source, store));
    }

    [TestMethod]
    public async Task WhatWasDeletedOrMovedOutsideIsFoundByItsIdAndLeavesNothingBehind()
    {
        var store = ChangingLibrary();
        await using var source = await MemorySharePointSource.StartAsync(store, walkEvery: TimeSpan.FromHours(1));
        await source.CountFilesAsync(None);
        var (files, folders) = (source.Counters.FilesKnown, source.Counters.DirectoriesKnown);

        // a file deleted: the change says its id and nothing of where it was
        store.Remove(Documents, "area2/folder3/two.txt");
        Assert.AreEqual((3, files - 1, folders), await WalkAsync(source, store));
        Assert.IsNull(await source.StatAsync("Documents/area2/folder3/two.txt", None));

        // a file moved to another folder, with another name: listed where it is, and gone from where it was
        store.Move(Documents, "area0/folder0/one.txt", "area2/folder1/moved.txt");
        Assert.AreEqual((5, files - 1, folders), await WalkAsync(source, store));
        Assert.IsNull(await source.StatAsync("Documents/area0/folder0/one.txt", None), "nothing left where it was");
        Assert.IsNotNull(await source.StatAsync("Documents/area2/folder1/moved.txt", None));

        // a folder moved with what it holds: the folders it left and came to, and the folder itself where it is now
        store.Move(Documents, "area0/folder3", "area2/folder3b");
        Assert.AreEqual((4, files - 1, folders), await WalkAsync(source, store));
        Assert.IsNull(await source.StatAsync("Documents/area0/folder3", None));
        Assert.IsNull(await source.StatAsync("Documents/area0/folder3/one.txt", None));
        Assert.IsNotNull(await source.StatAsync("Documents/area2/folder3b/one.txt", None));

        // a folder renamed where it is
        store.Move(Documents, "area1/folder0", "area1/folder0 renamed");
        Assert.AreEqual((3, files - 1, folders), await WalkAsync(source, store));
        Assert.IsNull(await source.StatAsync("Documents/area1/folder0/one.txt", None));
        Assert.IsNotNull(await source.StatAsync("Documents/area1/folder0 renamed/one.txt", None));

        // a folder deleted with all it holds: its folder is listed, and what was held below it is let go
        store.Remove(Documents, "area1/folder2");
        Assert.AreEqual((2, files - 1 - 3, folders - 3), await WalkAsync(source, store));
        Assert.IsNull(await source.StatAsync("Documents/area1/folder2/deep/deeper/old.txt", None));
        Assert.AreEqual((0, files - 4, folders - 3), await WalkAsync(source, store));
    }

    [TestMethod]
    public async Task WhatWasWrittenThroughTheSourceIsFoundByItsIdToo()
    {
        // the entry put into a listing for a file uploaded or a folder made here carries the item's id, as one listed does
        var store = ChangingLibrary();
        await using var source = await MemorySharePointSource.StartAsync(store, walkEvery: TimeSpan.FromHours(1));
        await source.CountFilesAsync(None);
        var files = source.Counters.FilesKnown;
        await using (var file = await source.OpenAsync("Documents/area0/folder2/mine.txt", VfsOpenMode.CreateNew, None))
        {
            await file.WriteAsync(0, "mine"u8.ToArray(), None);
        }

        await source.CreateDirectoryAsync("Documents/area0/made here", None);
        Assert.IsNotNull(await source.StatAsync("Documents/area0/folder2/mine.txt", None));

        // both gone again, outside, before any walk has listed them
        store.Remove(Documents, "area0/folder2/mine.txt");
        store.Remove(Documents, "area0/made here");
        var (_, counted, _) = await WalkAsync(source, store);
        Assert.AreEqual(files, counted);
        Assert.IsNull(await source.StatAsync("Documents/area0/folder2/mine.txt", None));
        Assert.IsNull(await source.StatAsync("Documents/area0/made here", None));
    }

    [TestMethod]
    public async Task TheNextStartAsksWhatChangedSinceTheLastWalkOfTheOneBefore()
    {
        var store = ChangingLibrary();
        using var context = new TestSourceContext("sharepoint");
        long files;
        await using (var first = await MemorySharePointSource.StartAsync(store, walkEvery: TimeSpan.FromHours(1), context: context))
        {
            await first.CountFilesAsync(None);
            files = first.Counters.FilesKnown;
        }

        store.Seed(Documents, "area2/folder0/while away.txt", Encoding.UTF8.GetBytes("away"));
        var before = store.FolderListings;
        await using var source = await MemorySharePointSource.StartAsync(store, walkEvery: TimeSpan.FromHours(1), context: context);
        await source.CountFilesAsync(None);
        Assert.AreEqual(files + 1, source.Counters.FilesKnown);
        Assert.AreEqual(3, store.FolderListings - before, "the library's own folder and the two on the way: the token came with the listings kept");
        Assert.IsNotNull(await source.StatAsync("Documents/area2/folder0/while away.txt", None));
    }

    [TestMethod]
    public async Task WhereTheStoreNoLongerKnowsTheTokenEveryFolderIsListed()
    {
        var store = ChangingLibrary();
        await using var source = await MemorySharePointSource.StartAsync(store, walkEvery: TimeSpan.FromHours(1));
        await source.CountFilesAsync(None);
        var (files, folders) = (source.Counters.FilesKnown, source.Counters.DirectoriesKnown);
        store.Seed(Documents, "area0/folder0/three.txt", Encoding.UTF8.GetBytes("three"));
        store.ForgetTokens();
        var (listed, counted, _) = await WalkAsync(source, store);
        Assert.AreEqual(files + 1, counted);
        Assert.IsTrue(listed >= 18, $"every folder of the library, and of the others: {listed}");

        // and from the token that walk was given, by what changed again
        store.Seed(Documents, "area0/folder0/four.txt", Encoding.UTF8.GetBytes("four"));
        Assert.AreEqual((3, files + 2, folders), await WalkAsync(source, store));
    }

    [TestMethod]
    public async Task WhereWhatChangedCannotBeAskedTheLibraryStaysAsItIsUntilItCan()
    {
        var store = ChangingLibrary();
        await using var source = await MemorySharePointSource.StartAsync(store, walkEvery: TimeSpan.FromHours(1));
        await source.CountFilesAsync(None);
        var (files, folders) = (source.Counters.FilesKnown, source.Counters.DirectoriesKnown);
        store.Seed(Documents, "area0/folder0/three.txt", Encoding.UTF8.GetBytes("three"));
        store.ChangesUnavailable = true;
        Assert.AreEqual((0, files, folders), await WalkAsync(source, store), "kept as it is: not listed whole, and the token not lost");
        Assert.IsNull(await source.StatAsync("Documents/area0/folder0/three.txt", None));

        store.ChangesUnavailable = false;
        Assert.AreEqual((3, files + 1, folders), await WalkAsync(source, store));
    }

    [TestMethod]
    public async Task AListingAPersonAsksForBetweenWalksLosesNoToken()
    {
        // the library's own folder is forgotten (a write into it that failed) and listed again for a person: the token
        // the last walk left with it must still be there for the next walk, or that walk lists the library whole
        var store = ChangingLibrary();
        store.Seed(Documents, "top.txt", Encoding.UTF8.GetBytes("top"));
        await using var source = await MemorySharePointSource.StartAsync(store, walkEvery: TimeSpan.FromHours(1));
        await source.CountFilesAsync(None);
        var (files, folders) = (source.Counters.FilesKnown, source.Counters.DirectoriesKnown);
        store.FailSetTimes = true;
        await Assert.ThrowsExactlyAsync<VfsException>(() => source.SetTimesAsync("Documents/top.txt", null, DateTimeOffset.UtcNow, None));
        store.FailSetTimes = false;
        Assert.IsNotNull(await source.StatAsync("Documents/top.txt", None), "listed again for the one who asked");

        // and a folder further down, forgotten and listed again the same way
        store.FailSetTimes = true;
        await Assert.ThrowsExactlyAsync<VfsException>(() => source.SetTimesAsync("Documents/area2/folder2/one.txt", null, DateTimeOffset.UtcNow, None));
        store.FailSetTimes = false;
        Assert.IsNotNull(await source.StatAsync("Documents/area2/folder2/one.txt", None));

        store.Seed(Documents, "area1/folder2/deep/deeper/new.txt", Encoding.UTF8.GetBytes("new"));
        var (listed, counted, _) = await WalkAsync(source, store);
        Assert.AreEqual(files + 1, counted);
        Assert.IsTrue(listed <= 7, $"the folders on the way to what changed, and the one a person had listed: {listed} of the library's 18");
        Assert.AreEqual((0, files + 1, folders), await WalkAsync(source, store));
    }

    [TestMethod]
    public async Task ASourceOfOneLibraryListsTheFoldersOnTheWayToAChangeToo()
    {
        // the library is the source's root: its own folder has the token, and paths have no library's name before them
        var store = ChangingLibrary();
        await using var source = await MemorySharePointSource.StartAsync(store, pinned: "Documents", walkEvery: TimeSpan.FromHours(1));
        await source.CountFilesAsync(None);
        var (files, folders) = (source.Counters.FilesKnown, source.Counters.DirectoriesKnown);
        Assert.AreEqual((0, files, folders), await WalkAsync(source, store));

        store.Seed(Documents, "area1/folder2/deep/deeper/new.txt", Encoding.UTF8.GetBytes("new"));
        Assert.AreEqual((5, files + 1, folders), await WalkAsync(source, store));
        Assert.AreEqual(3, (await source.StatAsync("area1/folder2/deep/deeper/new.txt", None))?.Length);

        store.Move(Documents, "area0/folder0/one.txt", "area2/folder1/moved.txt");
        Assert.AreEqual((5, files + 1, folders), await WalkAsync(source, store));
        Assert.IsNull(await source.StatAsync("area0/folder0/one.txt", None), "nothing left where it was");
        Assert.IsNotNull(await source.StatAsync("area2/folder1/moved.txt", None));

        store.Remove(Documents, "area2/folder3");
        Assert.AreEqual((2, files - 1, folders - 1), await WalkAsync(source, store));
        Assert.IsNull(await source.StatAsync("area2/folder3/one.txt", None));
        Assert.AreEqual((0, files - 1, folders - 1), await WalkAsync(source, store));
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
