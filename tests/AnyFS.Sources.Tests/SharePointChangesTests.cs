using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

/// <summary>
/// What changed in a library since the last walk, asked of the services as they answer it (the fake farm's change log,
/// the fake tenant's delta; both as the sandbox tenant answered on 24-Nov-2026): a walk lists the folders the changes
/// are in and those on the way to them, and no others (the review of 21-Nov-2026, P7). It listed a changed library
/// whole.
/// </summary>
[TestClass]
public class SharePointChangesTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static FakeSharePoint Fake(bool tenant) => tenant ? FakeSharePoint.Sandbox() : FakeSharePoint.LabFarm();

    /// <summary>The test site with its listings kept: the tenant's over Graph, the farm's over REST.</summary>
    private static SharePointSettings Settings(FakeSharePoint fake, bool tenant) => tenant
        ? new() { SiteUrl = fake.Origin + FakeSharePoint.TestSitePath, SignInMethod = SharePointSettings.AppDeviceCode, Account = FakeSharePoint.TenantUser }
        : new() { SiteUrl = fake.Origin + FakeSharePoint.TestSitePath, Username = FakeSharePoint.FarmUser, Password = FakeSharePoint.FakePassword };

    private static async Task<SharePointSiteSource> StartAsync(FakeSharePoint fake, bool tenant, TestSourceContext context)
    {
        var source = new SharePointSiteSource(Settings(fake, tenant), fake.Flavour == FakeSharePoint.Kind.Online ? fake.StandIn() : null);
        await source.InitialiseAsync(context, None);
        return source;
    }

    private static FakeSharePoint.Library Documents(FakeSharePoint fake) => fake.WebAt(FakeSharePoint.TestSitePath)!.Library("Documents");

    /// <summary>Three areas of four folders with two files each, and one file four folders down: 25 files in 17 folders.</summary>
    private static FakeSharePoint.Library Seeded(FakeSharePoint fake)
    {
        var documents = Documents(fake);
        for (var area = 0; area < 3; area++)
        {
            for (var folder = 0; folder < 4; folder++)
            {
                fake.Seed(documents, $"area{area}/folder{folder}/one.txt", Encoding.UTF8.GetBytes("one"));
                fake.Seed(documents, $"area{area}/folder{folder}/two.txt", Encoding.UTF8.GetBytes("two"));
            }
        }

        fake.Seed(documents, "area1/folder2/deep/deeper/old.txt", Encoding.UTF8.GetBytes("old"));
        return documents;
    }

    /// <summary>Whether a request lists a folder of one of the site's libraries (whichever, when none is named).</summary>
    private static bool Lists(FakeSharePoint.Seen seen, FakeSharePoint.Library? library = null)
    {
        if (seen.Method != "GET")
        {
            return false;
        }

        if (seen.Path.EndsWith("/children", StringComparison.Ordinal))
        {
            return seen.Path.Contains("/drives/", StringComparison.Ordinal) && (library is null || seen.Path.Contains($"/drives/{library.DriveId}/", StringComparison.Ordinal));
        }

        return Uri.UnescapeDataString(seen.RawQuery).Contains("$expand=Folders,Files", StringComparison.Ordinal)
            && (library is null
                ? !seen.Path.Contains("decodedurl='/sites/anyfs')", StringComparison.OrdinalIgnoreCase)
                : seen.Path.Contains($"decodedurl='{library.Url}'", StringComparison.OrdinalIgnoreCase) || seen.Path.Contains($"decodedurl='{library.Url}/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a request asks what changed in a library (whichever, when none is named).</summary>
    private static bool Asks(FakeSharePoint.Seen seen, FakeSharePoint.Library? library = null)
        => (seen.Path.EndsWith("/getchanges", StringComparison.OrdinalIgnoreCase) && (library is null || seen.Path.Contains($"lists(guid'{library.Id}')", StringComparison.OrdinalIgnoreCase)))
            || (seen.Path.EndsWith("/root/delta", StringComparison.Ordinal) && (library is null || seen.Path.Contains($"/drives/{library.DriveId}/", StringComparison.Ordinal)));

    /// <summary>A walk that keeps what the stamps say has not changed: how many folders of the site's libraries it listed, and how many of those were Documents'.</summary>
    private static async Task<(int Listed, int OfDocuments)> WalkAsync(SharePointSiteSource source, FakeSharePoint fake)
    {
        var documents = Documents(fake);
        var (all, mine) = (fake.Count(s => Lists(s)), fake.Count(s => Lists(s, documents)));
        Assert.IsTrue(await source.WalkAsync(new SourceCounters(), full: false, None), "the walk went everywhere");
        return (fake.Count(s => Lists(s)) - all, fake.Count(s => Lists(s, documents)) - mine);
    }

    private static async Task<long?> LengthAsync(ISource source, string path) => (await source.StatAsync(path, None))?.Length;

    [TestMethod]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task AWalkAfterAChangeListsTheFoldersOnTheWayToItAndNoOthers(bool tenant)
    {
        using var fake = Fake(tenant);
        var documents = Seeded(fake);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant, context);
        await source.CountFilesAsync(None);
        Assert.AreEqual(3, await LengthAsync(source, "Documents/area1/folder2/deep/deeper/old.txt"));

        // nothing changed: no folder of any library is listed
        Assert.AreEqual((0, 0), await WalkAsync(source, fake));

        // one new file, four folders down: the library's own folder and the four on the way, of its 18
        fake.Seed(documents, "area1/folder2/deep/deeper/new.txt", Encoding.UTF8.GetBytes("new!"));
        Assert.AreEqual((5, 5), await WalkAsync(source, fake));
        Assert.AreEqual(4, await LengthAsync(source, "Documents/area1/folder2/deep/deeper/new.txt"));

        // one changed where it is
        fake.Seed(documents, "area0/folder1/two.txt", Encoding.UTF8.GetBytes("two, and more"));
        Assert.AreEqual((3, 3), await WalkAsync(source, fake));
        Assert.AreEqual(13, await LengthAsync(source, "Documents/area0/folder1/two.txt"));

        // and nothing again
        Assert.AreEqual((0, 0), await WalkAsync(source, fake));
    }

    [TestMethod]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task WhatWasDeletedOrMovedOutsideIsFoundByItsIdAndLeavesNothingBehind(bool tenant)
    {
        using var fake = Fake(tenant);
        var documents = Seeded(fake);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant, context);
        await source.CountFilesAsync(None);

        // a file deleted: the services say its id, and nothing of where it was
        fake.Remove(documents, "area2/folder3/two.txt");
        Assert.AreEqual((3, 3), await WalkAsync(source, fake));
        Assert.IsNull(await source.StatAsync("Documents/area2/folder3/two.txt", None));
        Assert.IsNotNull(await source.StatAsync("Documents/area2/folder3/one.txt", None));

        // a file moved to another folder, with another name: listed where it is, and gone from where it was
        fake.Move(documents, "area0/folder0/one.txt", "area2/folder1/moved.txt");
        Assert.AreEqual((5, 5), await WalkAsync(source, fake));
        Assert.IsNull(await source.StatAsync("Documents/area0/folder0/one.txt", None), "nothing left where it was");
        Assert.AreEqual(3, await LengthAsync(source, "Documents/area2/folder1/moved.txt"));

        // a folder moved with what it holds, which the services say nothing of: the folders it left and came to, and the
        // folder itself where it is now
        fake.Move(documents, "area0/folder3", "area2/folder3b");
        Assert.AreEqual((4, 4), await WalkAsync(source, fake));
        Assert.IsNull(await source.StatAsync("Documents/area0/folder3", None));
        Assert.IsNull(await source.StatAsync("Documents/area0/folder3/one.txt", None));
        Assert.AreEqual(3, await LengthAsync(source, "Documents/area2/folder3b/one.txt"));

        // a folder deleted with all it holds: the folder it was in
        fake.Remove(documents, "area1/folder2");
        Assert.AreEqual((2, 2), await WalkAsync(source, fake));
        Assert.IsNull(await source.StatAsync("Documents/area1/folder2", None));
        Assert.IsNull(await source.StatAsync("Documents/area1/folder2/deep/deeper/old.txt", None));

        // a file renamed where it is
        fake.Move(documents, "area1/folder0/two.txt", "area1/folder0/second.txt");
        Assert.AreEqual((3, 3), await WalkAsync(source, fake));
        Assert.IsNull(await source.StatAsync("Documents/area1/folder0/two.txt", None));
        Assert.AreEqual(3, await LengthAsync(source, "Documents/area1/folder0/second.txt"));

        Assert.AreEqual((0, 0), await WalkAsync(source, fake));
    }

    /// <summary>What the source itself wrote is in the services' record of changes as anything else is: its folder is listed once more, and no other.</summary>
    [TestMethod]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task WhatWasWrittenThroughTheSourceIsToldAsAChangeAndItsFolderListed(bool tenant)
    {
        using var fake = Fake(tenant);
        Seeded(fake);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant, context);
        await source.CountFilesAsync(None);

        await using (var write = await source.OpenAsync("Documents/area2/folder0/written.txt", VfsOpenMode.CreateNew, None))
        {
            await write.WriteAsync(0, Encoding.UTF8.GetBytes("written"), None);
        }

        Assert.AreEqual(7, await LengthAsync(source, "Documents/area2/folder0/written.txt"));
        Assert.AreEqual((3, 3), await WalkAsync(source, fake));
        Assert.AreEqual(7, await LengthAsync(source, "Documents/area2/folder0/written.txt"));
        Assert.AreEqual((0, 0), await WalkAsync(source, fake));
    }

    /// <summary>
    /// What was written through the source and deleted by someone else before any walk listed it: the entry the source
    /// put into its listing carries the item's id, as one listed does, and the services tell of an item made and deleted
    /// between two asks as deleted (both did, asked on 24-Nov-2026).
    /// </summary>
    [TestMethod]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task WhatWasWrittenThroughTheSourceAndDeletedOutsideLeavesNothingBehind(bool tenant)
    {
        using var fake = Fake(tenant);
        var documents = Seeded(fake);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant, context);
        await source.CountFilesAsync(None);

        await using (var write = await source.OpenAsync("Documents/area0/folder2/mine.txt", VfsOpenMode.CreateNew, None))
        {
            await write.WriteAsync(0, Encoding.UTF8.GetBytes("mine"), None);
        }

        await source.CreateDirectoryAsync("Documents/area0/made here", None);
        await source.RenameAsync("Documents/area1/folder1/one.txt", "Documents/area1/folder1/renamed here.txt", false, None);
        Assert.IsNotNull(await source.StatAsync("Documents/area0/folder2/mine.txt", None));
        Assert.IsNotNull(await source.StatAsync("Documents/area0/made here", None));
        Assert.IsNotNull(await source.StatAsync("Documents/area1/folder1/renamed here.txt", None));

        // all three gone again, outside, before any walk has listed them
        fake.Remove(documents, "area0/folder2/mine.txt");
        fake.Remove(documents, "area0/made here");
        fake.Remove(documents, "area1/folder1/renamed here.txt");
        Assert.AreEqual((5, 5), await WalkAsync(source, fake), "the library's own folder, area0 and its folder2, area1 and its folder1");
        Assert.IsNull(await source.StatAsync("Documents/area0/folder2/mine.txt", None));
        Assert.IsNull(await source.StatAsync("Documents/area0/made here", None));
        Assert.IsNull(await source.StatAsync("Documents/area1/folder1/renamed here.txt", None));
        Assert.IsNull(await source.StatAsync("Documents/area1/folder1/one.txt", None));
        Assert.AreEqual((0, 0), await WalkAsync(source, fake));
    }

    [TestMethod]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task TheNextStartAsksWhatChangedSinceTheLastWalkOfTheOneBefore(bool tenant)
    {
        using var fake = Fake(tenant);
        var documents = Seeded(fake);
        using var context = new TestSourceContext("sharepoint");
        var first = await StartAsync(fake, tenant, context);
        await first.CountFilesAsync(None);
        await first.DisposeAsync();

        // while nothing ran: one file changed, one deleted
        fake.Seed(documents, "area2/folder2/one.txt", Encoding.UTF8.GetBytes("one, changed"));
        fake.Remove(documents, "area0/folder2/two.txt");
        var (listed, asked) = (fake.Count(s => Lists(s, documents)), fake.Count(s => Asks(s)));
        await using var second = await StartAsync(fake, tenant, context);
        await second.CountFilesAsync(None);
        Assert.IsTrue(fake.Count(s => Asks(s)) > asked, "what changed was asked");
        Assert.AreEqual(5, fake.Count(s => Lists(s, documents)) - listed, "the library's own folder, and the two on the way to each change");
        Assert.AreEqual(12, await LengthAsync(second, "Documents/area2/folder2/one.txt"));
        Assert.IsNull(await second.StatAsync("Documents/area0/folder2/two.txt", None));
    }

    /// <summary>A token the tenant no longer takes (Graph answers 410): every folder is listed once, from a token for now, and the walk after it lists none.</summary>
    [TestMethod]
    public async Task WhereGraphNoLongerTakesTheTokenEveryFolderIsListedOnce()
    {
        using var fake = Fake(tenant: true);
        var documents = Seeded(fake);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant: true, context);
        await source.CountFilesAsync(None);
        Assert.AreEqual((0, 0), await WalkAsync(source, fake));

        fake.ForgetChangeTokens();
        fake.Seed(documents, "area0/folder0/more.txt", Encoding.UTF8.GetBytes("more"));
        var (_, mine) = await WalkAsync(source, fake);
        Assert.AreEqual(Folders(documents.Root, forms: true), mine, "every folder of the library, and its own");
        Assert.AreEqual(4, await LengthAsync(source, "Documents/area0/folder0/more.txt"));
        Assert.AreEqual((0, 0), await WalkAsync(source, fake));
    }

    /// <summary>The folders in a folder and below, and the folder itself; over REST a library's own forms folder is no folder of its listing.</summary>
    private static int Folders(FakeSharePoint.FolderItem folder, bool forms) => 1 + folder.Children.OfType<FakeSharePoint.FolderItem>().Where(f => forms || folder.Parent is not null || f.Name != "Forms").Sum(f => Folders(f, forms));

    /// <summary>What changed comes a page at a time, and is all taken.</summary>
    [TestMethod]
    public async Task WhatChangedInATenantIsReadThroughEveryPage()
    {
        using var fake = Fake(tenant: true);
        var documents = Seeded(fake);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant: true, context);
        await source.CountFilesAsync(None);

        for (var area = 0; area < 3; area++)
        {
            for (var folder = 0; folder < 4; folder++)
            {
                fake.Seed(documents, $"area{area}/folder{folder}/three.txt", Encoding.UTF8.GetBytes("three"));
            }
        }

        fake.GraphPageSize = 4;
        var asked = fake.Count(s => Asks(s, documents));
        Assert.IsTrue(await source.WalkAsync(new SourceCounters(), full: false, None));
        Assert.IsTrue(fake.Count(s => Asks(s, documents)) - asked > 3, "several pages of changes");
        for (var area = 0; area < 3; area++)
        {
            for (var folder = 0; folder < 4; folder++)
            {
                Assert.AreEqual(5, await LengthAsync(source, $"Documents/area{area}/folder{folder}/three.txt"));
            }
        }
    }

    /// <summary>
    /// The services give another token at every call. With nothing changed the one held stands, and no folder is
    /// listed for the new one; once it is old it is exchanged, with the library's own folder listed and none below: a
    /// record of changes goes back only so far.
    /// </summary>
    [TestMethod]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task AnOldTokenIsExchangedForANewOneWhereNothingChanged(bool tenant)
    {
        using var fake = Fake(tenant);
        var documents = Seeded(fake);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant, context);
        await source.CountFilesAsync(None);
        if (!tenant)
        {
            // last changed ten minutes ago, so that the lists' stamps are believed
            foreach (var library in fake.WebAt(FakeSharePoint.TestSitePath)!.Libraries)
            {
                fake.Age(library, TimeSpan.FromMinutes(10));
            }

            await WalkAsync(source, fake);
        }

        Assert.AreEqual((0, 0), await WalkAsync(source, fake));
        Assert.AreEqual((0, 0), await WalkAsync(source, fake));

        source.TokenRenewedAfter = TimeSpan.Zero;
        var (_, mine) = await WalkAsync(source, fake);
        Assert.AreEqual(1, mine, "the library's own folder, for the new token");

        // and the new one is what the next change is asked from
        source.TokenRenewedAfter = TimeSpan.FromHours(12);
        Assert.AreEqual((0, 0), await WalkAsync(source, fake));
        fake.Seed(documents, "area2/folder2/more.txt", Encoding.UTF8.GetBytes("more"));
        Assert.AreEqual((3, 3), await WalkAsync(source, fake));
        Assert.AreEqual(4, await LengthAsync(source, "Documents/area2/folder2/more.txt"));
    }

    /// <summary>Where Graph does not say which folder a changed item is in, nothing says what to list: everything is, from a token for now.</summary>
    [TestMethod]
    public async Task ADeltaWithoutPathsSaysEverythingIsToBeListed()
    {
        using var fake = Fake(tenant: true);
        var documents = Seeded(fake);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant: true, context);
        var library = (await source.Store.ListLibrariesAsync(null, None)).First(l => l.Name == "Documents");
        var now = await source.Store.ChangesAsync(library, null, None);
        Assert.IsTrue(now!.Everything);

        fake.Seed(documents, "area0/folder0/more.txt", Encoding.UTF8.GetBytes("more"));
        var told = await source.Store.ChangesAsync(library, now.Token, None);
        Assert.IsFalse(told!.Everything);
        CollectionAssert.Contains(told.Items.Select(i => i.Folder).ToList(), "area0/folder0");
        Assert.IsTrue(told.Items.All(i => i.MayBeElsewhere), "Graph says what an item is now, not whether it was somewhere else");

        fake.DeltaPaths = false;
        fake.Seed(documents, "area0/folder0/still more.txt", Encoding.UTF8.GetBytes("more"));
        var blind = await source.Store.ChangesAsync(library, told.Token, None);
        Assert.IsTrue(blind!.Everything, blind.Why);
        StringAssert.Contains(blind.Why, "did not say where");
    }
}
