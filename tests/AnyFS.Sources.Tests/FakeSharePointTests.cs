using System.Diagnostics;
using System.Text.Json.Nodes;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The fake SharePoint itself, and what only it can show: what the lab and the sandbox do not do on demand (throttling,
/// a lapsed session or token, a server that ignores ranges, an answer that is not SharePoint's), each as the real servers
/// were seen to answer, and the faults those showed when the fakes were made (24-Oct-2026), kept fixed.
/// </summary>
[TestClass]
public class FakeSharePointTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The farm's test site (or <paramref name="site"/>) as the lab's test account; listings not kept, so nothing walks behind a test's back.</summary>
    private static SharePointSettings FarmSettings(FakeSharePoint fake, string? site = null, int spoolBelowMiB = 8) => new()
    {
        SiteUrl = site ?? fake.Origin + FakeSharePoint.TestSitePath,
        Username = FakeSharePoint.FarmUser,
        Password = FakeSharePoint.FakePassword,
        SpoolBelowMiB = spoolBelowMiB,
        KeepListings = false,
    };

    /// <summary>The tenant's test site (or <paramref name="site"/>) as the sandbox's test account, signed in with <paramref name="signIn"/>.</summary>
    private static SharePointSettings TenantSettings(FakeSharePoint fake, string signIn, string? site = null, int spoolBelowMiB = 8) => new()
    {
        SiteUrl = site ?? fake.Origin + FakeSharePoint.TestSitePath,
        SignInMethod = signIn,
        Account = FakeSharePoint.TenantUser,
        SpoolBelowMiB = spoolBelowMiB,
        KeepListings = false,
    };

    /// <summary>A source of the settings, started; a tenant's signs in through the fake's stand-in.</summary>
    private static async Task<SharePointSiteSource> StartAsync(FakeSharePoint fake, SharePointSettings settings, TestSourceContext context)
    {
        var source = new SharePointSiteSource(settings, fake.Flavour == FakeSharePoint.Kind.Online ? fake.StandIn() : null);
        await source.InitialiseAsync(context, None);
        return source;
    }

    [TestMethod]
    public async Task TheFarmAnswersThePluginAsTheLabDoes()
    {
        using var fake = FakeSharePoint.LabFarm();
        var result = await new SharePointPlugin().TestAsync(FarmSettings(fake), new Progress<string>(), None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.StartsWith(result.Message, $"Signed in as {FakeSharePoint.FarmUser}. anyfs ({fake.Origin}{FakeSharePoint.TestSitePath})");

        await using var source = (SharePointSiteSource)new SharePointPlugin().CreateSource(FarmSettings(fake));
        using var context = new TestSourceContext("sharepoint");
        await source.InitialiseAsync(context, None);
        var names = await SpsTestServer.NamesAsync(source, "");
        CollectionAssert.IsSubsetOf(new[] { "Documents", "Site Assets", "Site Pages", "Style Library", "Form Templates" }, names, string.Join(", ", names));
        CollectionAssert.DoesNotContain(names, "Master Page Gallery", "only document libraries and pages");

        // a wrong password: the farm's 401 with no body
        var wrong = FarmSettings(fake);
        wrong.Password = "not it";
        result = await new SharePointPlugin().TestAsync(wrong, new Progress<string>(), None);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "refused the sign-in (401");
    }

    /// <summary>
    /// The listings kept, a library's stamp is its list's last item change and item count, read from one <c>web/lists</c>
    /// call a site: a restart's walk asks for the lists and keeps every library whose stamp holds without listing a folder
    /// of it, and a library a file was added to is walked again.
    /// </summary>
    [TestMethod]
    public async Task ALibrarysStampIsItsListsLastChangeAndTheWalkKeepsALibraryWhoseStampHolds()
    {
        using var fake = FakeSharePoint.LabFarm();
        var settings = FarmSettings(fake);
        settings.KeepListings = true;
        using var context = new TestSourceContext("sharepoint");
        static bool Names(FakeSharePoint.Seen s, string text) => s.Path.Contains(text, StringComparison.OrdinalIgnoreCase) || Uri.UnescapeDataString(s.RawQuery).Contains(text, StringComparison.OrdinalIgnoreCase);
        var first = await StartAsync(fake, settings, context);
        await first.CountFilesAsync(None);
        Assert.IsTrue(fake.Count(s => Names(s, "Shared Documents")) > 0, "the library's folders listed by the first walk");
        await first.DisposeAsync();

        // a file added to Style Library: its stamp moves, Documents' holds
        fake.WebAt(FakeSharePoint.TestSitePath)!.Library("Style Library").Root.AddFile("new.css", System.Text.Encoding.UTF8.GetBytes("a {}"));
        var documents = fake.Count(s => Names(s, "Shared Documents"));
        var styles = fake.Count(s => Names(s, "Style Library"));
        var lists = fake.Count(s => s.Path.EndsWith("/web/lists", StringComparison.OrdinalIgnoreCase));
        await using var second = await StartAsync(fake, settings, context);
        await second.CountFilesAsync(None);
        Assert.IsTrue(fake.Count(s => s.Path.EndsWith("/web/lists", StringComparison.OrdinalIgnoreCase)) > lists, "the lists asked for their stamps");
        Assert.AreEqual(documents, fake.Count(s => Names(s, "Shared Documents")), "Documents kept by its stamp: no folder of it listed");
        Assert.IsTrue(fake.Count(s => Names(s, "Style Library")) > styles, "Style Library walked again");
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(second, "Style Library"), "new.css");
    }

    /// <summary>
    /// The farm's Windows sign-in as the farms have it (Negotiate or NTLM, here through HTTP.sys): "Windows account, as
    /// yourself" signs in as this PC's user, whom the fake takes for the lab's test account (Windows only).
    /// </summary>
    [TestMethod]
    public async Task AFarmSignedInToAsYourselfTakesThisPCsUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows' own sign-in, through HTTP.sys");
        }

        using var fake = FakeSharePoint.LabFarm(signIn: FakeSharePoint.FarmSignIn.Windows);
        var settings = new SharePointSettings { SiteUrl = fake.Origin + FakeSharePoint.TestSitePath, SignInMethod = SharePointSettings.WindowsCurrent, KeepListings = false };
        var result = await new SharePointPlugin().TestAsync(settings, new Progress<string>(), None);
        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(fake.Requests.Any(r => r.Account == FakeSharePoint.FarmUser), "signed in, and taken for the test account");
    }

    /// <summary>
    /// A server, or a proxy before it, that ignores a Range sends the whole file: what is read from an offset is still the
    /// file from there (it was the file from its start).
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "farm, REST")]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task AServerThatIgnoresRangesIsReadPastTheStart(bool tenant)
    {
        using var fake = tenant ? FakeSharePoint.Sandbox() : FakeSharePoint.LabFarm();
        var bytes = new byte[300_000];
        new Random(3).NextBytes(bytes);
        fake.Seed(fake.TestSite.Library("Documents"), "ranges/whole.bin", bytes);
        fake.HonoursRanges = false;
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant ? TenantSettings(fake, SharePointSettings.AppDeviceCode, spoolBelowMiB: 0) : FarmSettings(fake, spoolBelowMiB: 0), context);

        await using var file = await source.OpenAsync("Documents/ranges/whole.bin", VfsOpenMode.Read, None);
        var middle = new byte[10_000];
        Assert.AreEqual(middle.Length, await file.ReadFullyAsync(200_000, middle, None));
        CollectionAssert.AreEqual(bytes[200_000..210_000], middle, "the file from the offset, not from its start");
    }

    /// <summary>
    /// A token Graph stops taking before its time is renewed once and the call goes on; one refused again straight after is
    /// the sign-in's answer, said, not a new token asked for five times over.
    /// </summary>
    [TestMethod]
    public async Task ATokenGraphRefusesIsRenewedOnce()
    {
        using var fake = FakeSharePoint.Sandbox();
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.AppDeviceCode), context);
        Assert.AreEqual(0, fake.TokensRefreshed);

        fake.ExpireTokens();
        await source.CreateDirectoryAsync("Documents/after-expiry", None);
        Assert.AreEqual(1, fake.TokensRefreshed, "a new token, once");
        Assert.IsNotNull(fake.TestSite.Library("Documents").Root.Folder("after-expiry"), "and the folder made with it");

        var refusals = fake.Count(s => s.Status == 401);
        fake.Fail(r => r.IsGraph, 100, _ => FakeSharePoint.Response.Json(401, FakeSharePoint.GraphError("InvalidAuthenticationToken", "Access token validation failure. Invalid audience.")));
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.CreateDirectoryAsync("Documents/refused", None));
        StringAssert.Contains(ex.Message, "no longer accepted");
        Assert.AreEqual(2, fake.TokensRefreshed, "one more new token");
        Assert.AreEqual(refusals + 2, fake.Count(s => s.Status == 401), "the request, and once more with the new token");
    }

    /// <summary>SharePoint's search answering without its results (PrimaryQueryResult null) leaves the root's Sites out, not the root's listing.</summary>
    [TestMethod]
    public async Task ASearchAnswerWithoutResultsLeavesTheRootListed()
    {
        using var fake = FakeSharePoint.Sandbox();
        fake.Fail(r => r.RawPath.Contains("/_api/search/query", StringComparison.OrdinalIgnoreCase), 100, _ => FakeSharePoint.Response.Json(200, new JsonObject
        {
            ["ElapsedTime"] = 12,
            ["PrimaryQueryResult"] = null,
            ["Properties"] = new JsonArray(),
            ["SecondaryQueryResults"] = new JsonArray(),
        }));
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.WebSession, site: fake.Origin), context);

        var names = await SpsTestServer.NamesAsync(source, "");
        CollectionAssert.Contains(names, "Documents", string.Join(", ", names));
        Assert.IsTrue(fake.Count(s => s.Path.Contains("/_api/search/query", StringComparison.OrdinalIgnoreCase)) > 0, "search was asked");
    }

    /// <summary>A page that is not SharePoint's (a proxy's, a captive portal's: HTML with a 200) is said as such, by both APIs.</summary>
    [TestMethod]
    [DataRow(false, DisplayName = "farm, REST")]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task AnAnswerThatIsNotJsonIsSaidAsSuch(bool tenant)
    {
        using var fake = tenant ? FakeSharePoint.Sandbox() : FakeSharePoint.LabFarm();
        const string Portal = "<!DOCTYPE html><html><head><title>Sign in to the guest network</title></head><body><form method=\"post\"></form></body></html>";
        fake.Fail(r => tenant ? r.IsGraph : r.RawPath.Contains("/_api/web", StringComparison.OrdinalIgnoreCase), 100, _ => FakeSharePoint.Response.Text(200, Portal, "text/html; charset=utf-8"));
        var settings = tenant ? TenantSettings(fake, SharePointSettings.AppDeviceCode) : FarmSettings(fake);
        var result = await new SharePointPlugin().TestAsync(settings, new Progress<string>(), tenant ? fake.StandIn() : null, None);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "with something other than JSON (text/html");
        if (!tenant)
        {
            StringAssert.Contains(result.Message, "Sign in to the guest network", "the page's title says whose it is");
        }
    }

    /// <summary>A site with more libraries than Graph gives in one answer has them all, a page at a time.</summary>
    [TestMethod]
    public async Task ASiteWithManyLibrariesHasThemAll()
    {
        using var fake = FakeSharePoint.Sandbox();
        fake.GraphPageSize = 2;
        for (var i = 1; i <= 4; i++)
        {
            fake.AddLibrary(fake.TestSite, $"Library {i}", $"Library{i}");
        }

        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.AppDeviceCode), context);
        var names = await SpsTestServer.NamesAsync(source, "");
        CollectionAssert.IsSubsetOf(new[] { "Documents", "Library 1", "Library 2", "Library 3", "Library 4", "Site Pages" }, names, string.Join(", ", names));
    }

    /// <summary>A web session that lapses is renewed without a window, and the write it refused goes through.</summary>
    [TestMethod]
    public async Task ALapsedWebSessionIsRenewedWithoutAWindow()
    {
        using var fake = FakeSharePoint.Sandbox();
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.WebSession), context);
        Assert.AreEqual(1, fake.SessionSignIns);

        fake.ExpireSessions();
        await source.CreateDirectoryAsync("Documents/after-lapse", None);
        Assert.AreEqual(2, fake.SessionSignIns, "renewed once");
        Assert.IsNotNull(fake.TestSite.Library("Documents").Root.Folder("after-lapse"));
    }

    /// <summary>
    /// SharePoint Online refuses a reader's write as it refuses a lapsed session (403), but sets the session cookie again on
    /// the answer: the refusal is the account's, said as such, and the session is not renewed for it (21-Oct-2026's storm).
    /// </summary>
    [TestMethod]
    public async Task AReadersRefusedWriteKeepsTheSession()
    {
        using var fake = FakeSharePoint.Sandbox();
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.WebSession, site: fake.Origin), context);

        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.CreateDirectoryAsync("Documents/not-mine", None));
        Assert.AreEqual(VfsError.AccessDenied, ex.Error, ex.Message);
        Assert.IsTrue(fake.Count(s => s.Status == 403) > 0, "SharePoint refused it");
        Assert.AreEqual(1, fake.SessionSignIns, "no renewal");
        Assert.AreNotEqual(ComponentHealth.Degraded, context.LastHealth);
    }

    /// <summary>
    /// A web session that lapses when the browser's own sign-in has lapsed too cannot be renewed without a person: the source
    /// turns Degraded with the sign-in's reason (its next start signs in with a window), rather than calling everything
    /// "access denied".
    /// </summary>
    [TestMethod]
    public async Task AWebSessionThatNeedsAPersonTurnsTheSourceDegraded()
    {
        using var fake = FakeSharePoint.Sandbox();
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.WebSession), context);

        fake.BrowserLapsed = true;
        fake.ExpireSessions();
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.CreateDirectoryAsync("Documents/after-lapse", None));
        Assert.AreEqual(VfsError.Unavailable, ex.Error, ex.Message);
        StringAssert.Contains(ex.Message, "lapsed");
        Assert.AreEqual(ComponentHealth.Degraded, context.LastHealth);

        // and the next refusal says so too, without a browser started again so soon
        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.CreateDirectoryAsync("Documents/again", None));
        StringAssert.Contains(ex.Message, "lapsed");
        Assert.AreEqual(1, fake.SessionSignIns, "the renewals failed, none made a session");
    }

    /// <summary>Throttled (429 with Retry-After) at SharePoint Online's pace: the requests wait it out, and the write goes through.</summary>
    [TestMethod]
    public async Task AThrottledWriteWaitsAndGoesThrough()
    {
        using var fake = FakeSharePoint.Sandbox("anyfs-paced", paced: true);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.WebSession), context);

        fake.Throttle(2, retryAfterSeconds: 1, when: r => r.Method == "POST" && r.RawPath.Contains("/Folders/", StringComparison.OrdinalIgnoreCase));
        var watch = Stopwatch.StartNew();
        await source.CreateDirectoryAsync("Documents/after-throttle", None);
        Assert.IsTrue(watch.Elapsed >= TimeSpan.FromSeconds(1.8), $"held for each Retry-After ({watch.Elapsed.TotalSeconds:F1} s)");
        Assert.AreEqual(2, fake.Count(s => s.Status == 429));
        Assert.IsNotNull(fake.TestSite.Library("Documents").Root.Folder("after-throttle"));
    }

    [TestMethod]
    public async Task AFolderWhoseListingFailedStaysSoUntilTheNextWalk()
    {
        // SharePoint's listings kept no failure, as six other sources' do: a folder the account may not list was asked
        // again at every visit and every pass of a search, a throttled one with each of its waits (the review of
        // 21-Nov-2026, R12)
        using var fake = FakeSharePoint.Sandbox();
        var documents = fake.TestSite.Library("Documents");
        fake.Seed(documents, "refused/a.txt", "a"u8.ToArray());
        var folder = documents.Root.Folder("refused")!;
        var refusing = true;
        var asked = 0;
        fake.Fail(r => refusing && r.IsGraph && r.RawPath.EndsWith("/children", StringComparison.OrdinalIgnoreCase)
            && (r.RawPath.Contains("refused", StringComparison.OrdinalIgnoreCase) || r.RawPath.Contains(folder.GraphId, StringComparison.OrdinalIgnoreCase)) && Interlocked.Increment(ref asked) > 0,
            1000, _ => FakeSharePoint.Response.Json(403, FakeSharePoint.GraphError("accessDenied", "Access denied")));
        using var context = new TestSourceContext("sharepoint");
        var settings = TenantSettings(fake, SharePointSettings.AppDeviceCode);
        settings.KeepListings = true;
        settings.WalkMinutes = 0;
        await using var source = await StartAsync(fake, settings, context);

        // the start's own walk done with, whatever it reached
        await source.WalkAsync(new SourceCounters(), None);
        var first = await Assert.ThrowsExactlyAsync<VfsException>(async () => await SpsTestServer.NamesAsync(source, "Documents/refused"));
        Assert.AreEqual(VfsError.AccessDenied, first.Error);
        var before = asked;
        Assert.IsTrue(before > 0);

        refusing = false;
        var again = await Assert.ThrowsExactlyAsync<VfsException>(async () => await SpsTestServer.NamesAsync(source, "Documents/refused"));
        Assert.AreEqual(first.Message, again.Message, "answered at once as it failed");
        Assert.AreEqual(before, asked, "nothing asked");

        await source.WalkAsync(new SourceCounters(), None);
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(source, "Documents/refused"), "a.txt", "the walk asked again, and it answered");
    }
    /// <summary>Graph's download addresses expire: a read after that asks for the file's address again.</summary>
    [TestMethod]
    public async Task ADownloadAddressThatExpiredIsAskedForAgain()
    {
        using var fake = FakeSharePoint.Sandbox();
        var bytes = new byte[3 << 20];
        new Random(5).NextBytes(bytes);
        fake.Seed(fake.TestSite.Library("Documents"), "expiry/big.bin", bytes);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.AppDeviceCode, spoolBelowMiB: 0), context);

        await using var file = await source.OpenAsync("Documents/expiry/big.bin", VfsOpenMode.Read, None);
        var piece = new byte[4096];
        Assert.AreEqual(piece.Length, await file.ReadFullyAsync(0, piece, None));
        fake.ExpireDownloadUrls();
        Assert.AreEqual(piece.Length, await file.ReadFullyAsync(5 << 19, piece, None));
        CollectionAssert.AreEqual(bytes[(5 << 19)..((5 << 19) + 4096)], piece);
        Assert.AreNotEqual(ComponentHealth.Degraded, context.LastHealth);
    }

    /// <summary>Graph asked for one item alone, by its path: what a file's download address was asked with.</summary>
    private static bool IsItemAsked(FakeSharePoint.Seen s)
        => s.Method == "GET" && s.Host.StartsWith("graph.microsoft.", StringComparison.OrdinalIgnoreCase) && s.RawPath.Contains("/root:/", StringComparison.Ordinal) && s.RawPath.EndsWith(':');

    /// <summary>A file's bytes asked for at a download address.</summary>
    private static bool IsDownload(FakeSharePoint.Seen s) => s.RawPath.StartsWith("/_fakegraph/download/", StringComparison.Ordinal);

    /// <summary>
    /// A folder's listing was asked to leave each file's download address out, so every file opened after its folder was
    /// listed asked Graph for the item first, for its address alone: two requests a file, at the pace a walk goes at
    /// too (the review of 21-Nov-2026, N22). The listing a person asks for brings the addresses now, kept a few minutes.
    /// </summary>
    [TestMethod]
    public async Task FilesOpenedAfterTheirFolderWasListedAreReadWithoutAskingForEachOnesAddress()
    {
        const int Files = 20;
        using var fake = FakeSharePoint.Sandbox();
        var documents = fake.TestSite.Library("Documents");
        for (var i = 0; i < Files; i++)
        {
            fake.Seed(documents, $"read/file-{i:00}.txt", System.Text.Encoding.UTF8.GetBytes($"file {i}\n"));
        }

        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.AppDeviceCode), context);
        var names = await SpsTestServer.NamesAsync(source, "Documents/read");
        Assert.AreEqual(Files, names.Count);

        var before = fake.Requests.Count;
        for (var i = 0; i < Files; i++)
        {
            await using var file = await source.OpenAsync($"Documents/read/file-{i:00}.txt", VfsOpenMode.Read, None);
            var bytes = new byte[file.Length];
            Assert.AreEqual(bytes.Length, await file.ReadFullyAsync(0, bytes, None));
            Assert.AreEqual($"file {i}\n", System.Text.Encoding.UTF8.GetString(bytes));
        }

        var asked = fake.Requests.Skip(before).ToList();
        Console.WriteLine($"{Files} files read after their folder was listed: {asked.Count(IsItemAsked)} requests for an address, {asked.Count(IsDownload)} for bytes");
        Assert.AreEqual(Files, asked.Count(IsDownload), "each file's bytes");
        Assert.AreEqual(0, asked.Count(IsItemAsked), "and no request for an address: " + string.Join("; ", asked.Where(IsItemAsked).Take(3).Select(s => s.Path)));
    }

    /// <summary>
    /// A big file is read through streams opened where it is read, and each asked Graph for the file's address first
    /// where the folder's listing was a walk's, which brings none: a request to Graph at every place a reader went to.
    /// The address asked for is kept a few minutes, and serves the places that follow.
    /// </summary>
    [TestMethod]
    public async Task ABigFileReadHereAndThereAsksForItsAddressOnceNotAtEachPlace()
    {
        using var fake = FakeSharePoint.Sandbox();
        var bytes = new byte[8 << 20];
        new Random(7).NextBytes(bytes);
        fake.Seed(fake.TestSite.Library("Documents"), "big/video.bin", bytes);
        using var context = new TestSourceContext("sharepoint");
        var settings = TenantSettings(fake, SharePointSettings.AppDeviceCode, spoolBelowMiB: 0);
        settings.KeepListings = true;
        settings.WalkMinutes = 0;
        await using var source = await StartAsync(fake, settings, context);
        await source.CountFilesAsync(None);
        static bool IsListing(FakeSharePoint.Seen s) => s.RawPath.EndsWith("/children", StringComparison.Ordinal);
        Assert.IsTrue(fake.Count(IsListing) > 0);
        Assert.AreEqual(0, fake.Count(s => IsListing(s) && Uri.UnescapeDataString(s.RawQuery).Contains("downloadUrl", StringComparison.OrdinalIgnoreCase)),
            "a walk's listings ask for no address: it would be one for every file of every folder, and none of them wanted");

        // from its end towards its start, so that no stream open at one place can be read on to the next
        var before = fake.Requests.Count;
        await using var file = await source.OpenAsync("Documents/big/video.bin", VfsOpenMode.Read, None);
        var piece = new byte[4096];
        foreach (var at in new[] { 15 << 19, 6 << 20, 9 << 19, 3 << 20, 3 << 19, 0 })
        {
            Assert.AreEqual(piece.Length, await file.ReadFullyAsync(at, piece, None));
            CollectionAssert.AreEqual(bytes[at..(at + piece.Length)], piece);
        }

        var asked = fake.Requests.Skip(before).ToList();
        Console.WriteLine($"a file read at 6 places: {asked.Count(IsItemAsked)} requests for its address, {asked.Count(IsDownload)} for bytes");
        Assert.AreEqual(6, asked.Count(IsDownload), "a stream opened at each place");
        Assert.AreEqual(1, asked.Count(IsItemAsked), "and its address asked for once");
    }

    /// <summary>An address works for about an hour: one Graph gave more than a few minutes ago is not tried, and the file's is asked for.</summary>
    [TestMethod]
    public async Task ADownloadAddressIsUsedForAFewMinutesAndAskedForAgainAfterThat()
    {
        using var fake = FakeSharePoint.Sandbox();
        fake.Seed(fake.TestSite.Library("Documents"), "aged/a.txt", "aged"u8.ToArray());
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.AppDeviceCode), context);
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(source, "Documents/aged"), "a.txt");
        var store = (GraphDocumentStore)source.Store;
        Assert.AreEqual(TimeSpan.FromMinutes(10), store.AddressesKeptFor);

        // as if those minutes had passed since the listing: what it brought is too old to use
        store.AddressesKeptFor = TimeSpan.Zero;
        var before = fake.Requests.Count;
        await using var file = await source.OpenAsync("Documents/aged/a.txt", VfsOpenMode.Read, None);
        var bytes = new byte[file.Length];
        Assert.AreEqual(bytes.Length, await file.ReadFullyAsync(0, bytes, None));
        CollectionAssert.AreEqual("aged"u8.ToArray(), bytes);
        var asked = fake.Requests.Skip(before).ToList();
        Assert.AreEqual(1, asked.Count(IsItemAsked), "the address asked for");
        Assert.AreEqual(1, asked.Count(IsDownload));
    }

    /// <summary>
    /// A file asked for by itself is remembered for the run with the address it came with, and that address stops working
    /// within the hour. It was tried first at every place the file was read at after that, and failed there, with the one
    /// Graph had just given in its place set aside: three requests a place. The one Graph gave last is tried first.
    /// </summary>
    [TestMethod]
    public async Task AnAddressThatStoppedWorkingIsAskedForAgainOnceNotAtEachPlaceTheFileIsReadAt()
    {
        using var fake = FakeSharePoint.Sandbox();
        var bytes = new byte[1 << 20];
        new Random(11).NextBytes(bytes);
        fake.Seed(fake.TestSite.Library("Documents"), "big/video.bin", bytes);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.AppDeviceCode), context);
        var store = (GraphDocumentStore)source.Store;
        var library = (await store.ListLibrariesAsync(null, None)).First(l => l.Name == "Documents");

        // asked for by itself, as the source asks for a file opened before its folder was listed in the run; the source
        // keeps what it is given for the run, and opens the file's content with it each time
        var item = await store.GetItemAsync(library, "big/video.bin", None);
        Assert.IsNotNull(item?.DownloadUrl, "an item asked for by itself comes with its address");
        async Task ReadAsync(int at)
        {
            using var response = await store.OpenContentAsync(library, "big/video.bin", item, at, None);
            var piece = new byte[4096];
            await using var content = await response.Content.ReadAsStreamAsync(None);
            await content.ReadExactlyAsync(piece, None);
            CollectionAssert.AreEqual(bytes[at..(at + piece.Length)], piece);
        }

        await ReadAsync(0);

        // the hour is over
        fake.ExpireDownloadUrls();
        var before = fake.Requests.Count;
        foreach (var at in new[] { 1 << 19, 1 << 18, 1 << 17, 1 << 16, 0 })
        {
            await ReadAsync(at);
        }

        var asked = fake.Requests.Skip(before).ToList();
        Console.WriteLine($"a file read at 5 places after its address stopped working: {asked.Count(IsItemAsked)} requests for its address, {asked.Count(IsDownload)} for bytes, {asked.Count(s => IsDownload(s) && s.Status >= 400)} of those refused");
        Assert.AreEqual(1, asked.Count(IsItemAsked), "its address asked for once");
        Assert.AreEqual(1, asked.Count(s => IsDownload(s) && s.Status >= 400), "and the one that stopped working tried once");
    }

    /// <summary>
    /// A download address is the permission to read its file, for an hour: none is written down with the listings a run
    /// keeps, and the next run, which answers a folder from those, asks for a file's address when the file is opened.
    /// </summary>
    [TestMethod]
    public async Task NoDownloadAddressIsKeptWithTheListingsOrUsedByTheNextRun()
    {
        using var fake = FakeSharePoint.Sandbox();
        fake.Seed(fake.TestSite.Library("Documents"), "kept/a.txt", "kept"u8.ToArray());
        using var context = new TestSourceContext("sharepoint");
        var settings = TenantSettings(fake, SharePointSettings.AppDeviceCode);
        settings.KeepListings = true;
        settings.WalkMinutes = 0;
        static bool BroughtAddresses(FakeSharePoint.Seen s) => s.RawPath.EndsWith("/children", StringComparison.Ordinal) && Uri.UnescapeDataString(s.RawQuery).Contains("downloadUrl", StringComparison.Ordinal);
        var first = await StartAsync(fake, settings, context);

        // The walk that began with the source is let end first. It lists every folder, without the addresses, and a
        // listing asked for after it is answered from what it kept: where the walk came to the folder before the test
        // did, no listing brought an address (met here once, 28-Nov-2026). What is listed for the addresses is a folder
        // the source made since, which no walk has listed, with a file put in it behind the source's back
        await first.WalkAsync(new SourceCounters(), None);
        await first.CreateDirectoryAsync("Documents/since", None);
        fake.Seed(fake.TestSite.Library("Documents"), "since/b.txt", "since"u8.ToArray());
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(first, "Documents/since"), "b.txt");
        Assert.IsTrue(fake.Count(BroughtAddresses) > 0, "the listing someone asked for brought the addresses");
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(first, "Documents/kept"), "a.txt");
        await first.DisposeAsync();

        foreach (var kept in Directory.EnumerateFiles(context.StateDirectory, "*", SearchOption.AllDirectories))
        {
            Assert.IsFalse(System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(kept)).Contains("_fakegraph/download", StringComparison.Ordinal), $"an address in {Path.GetFileName(kept)}");
        }

        // the next run: the start's own walk done with, the folder is answered from what was kept, and its file read
        await using var second = await StartAsync(fake, settings, context);
        await second.WalkAsync(new SourceCounters(), None);
        var before = fake.Requests.Count;
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(second, "Documents/kept"), "a.txt");
        Assert.AreEqual(0, fake.Requests.Skip(before).Count(BroughtAddresses), "no listing asked for it");
        await using var file = await second.OpenAsync("Documents/kept/a.txt", VfsOpenMode.Read, None);
        var bytes = new byte[file.Length];
        Assert.AreEqual(bytes.Length, await file.ReadFullyAsync(0, bytes, None));
        CollectionAssert.AreEqual("kept"u8.ToArray(), bytes);
        var asked = fake.Requests.Skip(before).ToList();
        Assert.AreEqual(1, asked.Count(IsItemAsked), "the file's address asked for by this run");
        Assert.AreEqual(1, asked.Count(IsDownload));
    }

    /// <summary>
    /// Should a Graph refuse a listing asked for with the addresses, the folder is listed as it was before, and that
    /// Graph is not asked for them again: a folder is never left unlisted for the sake of its files' addresses.
    /// </summary>
    [TestMethod]
    public async Task AGraphThatWillNotListWithTheAddressesListsWithoutAndIsNotAskedForThemAgain()
    {
        using var fake = FakeSharePoint.Sandbox();
        var documents = fake.TestSite.Library("Documents");
        fake.Seed(documents, "plain/a.txt", "plain"u8.ToArray());
        fake.Seed(documents, "plain/sub/b.txt", "b"u8.ToArray());
        fake.Fail(r => r.IsGraph && r.RawPath.EndsWith("/children", StringComparison.OrdinalIgnoreCase) && Uri.UnescapeDataString(r.RawQuery).Contains("downloadUrl", StringComparison.Ordinal),
            1000, _ => FakeSharePoint.Response.Json(400, FakeSharePoint.GraphError("invalidRequest", "Could not find a property named 'downloadUrl' on type 'microsoft.graph.driveItem'.")));
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.AppDeviceCode), context);
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(source, "Documents/plain"), "a.txt");
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(source, "Documents/plain/sub"), "b.txt");
        Assert.AreEqual(1, fake.Count(s => s.Status == 400), "asked for with the addresses once, and not after that was refused");

        // and a file is read as it was before: its address asked for at its open
        var before = fake.Requests.Count;
        await using var file = await source.OpenAsync("Documents/plain/a.txt", VfsOpenMode.Read, None);
        var bytes = new byte[file.Length];
        Assert.AreEqual(bytes.Length, await file.ReadFullyAsync(0, bytes, None));
        CollectionAssert.AreEqual("plain"u8.ToArray(), bytes);
        var asked = fake.Requests.Skip(before).ToList();
        Assert.AreEqual(1, asked.Count(IsItemAsked));
        Assert.AreEqual(1, asked.Count(IsDownload));
        Assert.AreNotEqual(ComponentHealth.Degraded, context.LastHealth);
    }

    /// <summary>
    /// A folder is deleted only when empty, and the listing that says it is may be hours old, while SharePoint deletes a
    /// folder with all it holds: the server is asked for the folder's own count for the delete. Before, a folder a
    /// colleague had put files in since it was listed went with their files (review of 21-Nov-2026, R3).
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "farm, REST")]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task AFolderFilledSinceItWasListedIsNotDeletedWithWhatItHolds(bool tenant)
    {
        using var fake = tenant ? FakeSharePoint.Sandbox() : FakeSharePoint.LabFarm();
        var documents = fake.TestSite.Library("Documents");
        fake.Seed(documents, "handover/old/gone.txt", "x"u8.ToArray());
        documents.Root.Folder("handover")!.Folder("old")!.Children.Clear();
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant ? TenantSettings(fake, SharePointSettings.AppDeviceCode) : FarmSettings(fake), context);
        Assert.AreEqual(0, (await SpsTestServer.NamesAsync(source, "Documents/handover/old")).Count, "listed while empty");

        fake.Seed(documents, "handover/old/theirs.docx", "a colleague's"u8.ToArray());
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.DeleteAsync("Documents/handover/old", None));
        Assert.AreEqual(VfsError.DirectoryNotEmpty, ex.Error);
        Assert.IsNotNull(documents.Root.Folder("handover")?.Folder("old")?.File("theirs.docx"), "the colleague's file is still there");
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(source, "Documents/handover/old"), "theirs.docx", "and the folder is listed afresh");

        // emptied, it goes as before
        await source.DeleteAsync("Documents/handover/old/theirs.docx", None);
        await source.DeleteAsync("Documents/handover/old", None);
        Assert.IsNull(documents.Root.Folder("handover")!.Folder("old"));
    }

    /// <summary>
    /// A farm takes a file past ten megabytes in pieces, against a file that is there. An empty one was made for that
    /// whatever was there, so an upload that failed part way left the file that had been there empty; and a new file's
    /// stayed behind empty. One that is there now keeps what it holds until the last piece lands, and what was made for
    /// an upload that failed is taken away (review of 21-Nov-2026, R3).
    /// </summary>
    [TestMethod]
    public async Task AnUploadInPiecesThatFailsPartWayLeavesTheFileAsItWas()
    {
        using var fake = FakeSharePoint.LabFarm();
        var documents = fake.TestSite.Library("Documents");
        var old = "the version that was there"u8.ToArray();
        fake.Seed(documents, "pieces/report.bin", old);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, FarmSettings(fake), context);
        var bytes = new byte[SharePointRestClient.SimpleUploadLimit + 1];
        new Random(7).NextBytes(bytes);

        fake.Fail(r => r.RawPath.Contains("FinishUpload", StringComparison.OrdinalIgnoreCase), 2,
            _ => FakeSharePoint.Response.Json(500, FakeSharePoint.RestError("-2147024864, Microsoft.SharePoint.SPFileLockException", "The file is locked for shared use.")));
        source.UploadRetryAfter = _ => TimeSpan.FromHours(1);
        var over = await source.OpenAsync("Documents/pieces/report.bin", VfsOpenMode.Create, None);
        await over.WriteAsync(0, bytes, None);
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await over.DisposeAsync());
        CollectionAssert.AreEqual(old, documents.Root.Folder("pieces")!.File("report.bin")!.Content, "what was there before the upload that failed");

        var fresh = await source.OpenAsync("Documents/pieces/new.bin", VfsOpenMode.CreateNew, None);
        await fresh.WriteAsync(0, bytes, None);
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await fresh.DisposeAsync());
        Assert.IsNull(documents.Root.Folder("pieces")!.File("new.bin"), "no empty file left where the upload failed");

        // and with the server taking the pieces, both land (written again, over the ones that wait)
        await using (var file = await source.OpenAsync("Documents/pieces/report.bin", VfsOpenMode.Create, None))
        {
            await file.WriteAsync(0, bytes, None);
        }

        await using (var file = await source.OpenAsync("Documents/pieces/new.bin", VfsOpenMode.Create, None))
        {
            await file.WriteAsync(0, bytes, None);
        }

        CollectionAssert.AreEqual(bytes, documents.Root.Folder("pieces")!.File("report.bin")!.Content);
        CollectionAssert.AreEqual(bytes, documents.Root.Folder("pieces")!.File("new.bin")!.Content);
    }

    /// <summary>
    /// The local copy of a file goes once the file has been uploaded. It stayed until the next start: .NET leaves a
    /// request's content open for the collector, the copy could not be deleted while its stream was, and nothing came back
    /// for it. So every file written in a run stayed in the cache for the rest of that run.
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "farm, REST")]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task AnUploadedFilesLocalCopyGoesOnceItHasLanded(bool tenant)
    {
        using var fake = tenant ? FakeSharePoint.Sandbox() : FakeSharePoint.LabFarm();
        var documents = fake.TestSite.Library("Documents");
        fake.Seed(documents, "copies/there.txt", "x"u8.ToArray());
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant ? TenantSettings(fake, SharePointSettings.AppDeviceCode) : FarmSettings(fake), context);
        var bytes = new byte[100_000];
        new Random(9).NextBytes(bytes);

        await using (var file = await source.OpenAsync("Documents/copies/new.bin", VfsOpenMode.CreateNew, None))
        {
            await file.WriteAsync(0, bytes, None);
        }

        CollectionAssert.AreEqual(bytes, documents.Root.Folder("copies")!.File("new.bin")!.Content);
        var left = Directory.GetFiles(Path.Combine(context.CacheDirectory, "uploads"));
        Assert.AreEqual(0, left.Length, string.Join(", ", left.Select(Path.GetFileName)));
    }

    /// <summary>
    /// An upload runs when the file closes, after the client was told its write was taken. One that failed (a colleague
    /// has the file open, a sign-in that lapsed, a server that did not answer) threw the bytes away, with "the client's
    /// write is lost" in the log and a Degraded tile that the next call that went through cleared (review of 21-Nov-2026,
    /// R3). The bytes are kept now: who closed the file is told, the source says which file waits until it has landed
    /// whatever its other calls do, the file reads as it was written, and the upload is tried again until it lands.
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "farm, REST")]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task AnUploadThatFailsIsKeptAndTriedAgainUntilItLands(bool tenant)
    {
        using var fake = tenant ? FakeSharePoint.Sandbox() : FakeSharePoint.LabFarm();
        var documents = fake.TestSite.Library("Documents");
        var theirs = "the version a colleague has open"u8.ToArray();
        var upstream = fake.Seed(documents, "kept/report.docx", theirs);
        upstream.Locked = true;
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant ? TenantSettings(fake, SharePointSettings.AppDeviceCode) : FarmSettings(fake), context);
        source.UploadRetryAfter = _ => TimeSpan.FromMilliseconds(200);
        var uploads = Path.Combine(context.CacheDirectory, "uploads");
        var mine = "what I saved over it, which is longer"u8.ToArray();

        var file = await source.OpenAsync("Documents/kept/report.docx", VfsOpenMode.Create, None);
        await file.WriteAsync(0, mine, None);
        var refused = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.DisposeAsync());
        StringAssert.Contains(refused.Message, "locked", "who closed the file is told why it did not go");

        Assert.AreEqual(ComponentHealth.Degraded, context.LastHealth);
        StringAssert.Contains(context.HealthReports[^1].Detail, "Documents/kept/report.docx is not uploaded yet");
        await source.CreateDirectoryAsync("Documents/kept/meanwhile", None);
        Assert.AreEqual(ComponentHealth.Degraded, context.LastHealth, "a call that goes through does not end it");
        Assert.AreEqual(mine.Length, (await source.StatAsync("Documents/kept/report.docx", None))!.Length);
        CollectionAssert.AreEqual(mine, await ReadAllAsync(source, "Documents/kept/report.docx"), "read from here, as written");
        CollectionAssert.AreEqual(theirs, upstream.Content, "SharePoint's own is as it was");
        Assert.AreEqual(2, Directory.GetFiles(uploads).Length, "the bytes and their note");

        upstream.Locked = false;
        await UntilAsync(() => upstream.Content.AsSpan().SequenceEqual(mine), "the upload lands once the file is free");
        await UntilAsync(() => context.LastHealth == ComponentHealth.Ready, "and the source is well again");
        Assert.AreEqual(0, Directory.GetFiles(uploads).Length);
        CollectionAssert.AreEqual(mine, await ReadAllAsync(source, "Documents/kept/report.docx"), "read from SharePoint now");
    }

    /// <summary>
    /// The bytes of an upload that has not landed outlast the run: a note beside them says where they go, and the next
    /// start tries them at once. (Every start emptied the folder.) A file with no note was still being written when its
    /// run ended, half a file, and goes as before.
    /// </summary>
    [TestMethod]
    public async Task AnUploadThatHadNotLandedWhenTheSourceStoppedLandsAtTheNextStart()
    {
        using var fake = FakeSharePoint.LabFarm();
        var documents = fake.TestSite.Library("Documents");
        fake.Seed(documents, "kept/other.txt", "x"u8.ToArray());
        using var context = new TestSourceContext("sharepoint");
        var uploads = Path.Combine(context.CacheDirectory, "uploads");
        var mine = new byte[300_000];
        new Random(11).NextBytes(mine);
        var written = new DateTimeOffset(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);
        await using (var source = await StartAsync(fake, FarmSettings(fake), context))
        {
            source.UploadRetryAfter = _ => TimeSpan.FromHours(1);
            fake.Fail(r => r.Method == "POST" && r.RawPath.Contains("/Files/AddUsingPath", StringComparison.OrdinalIgnoreCase), 1,
                _ => FakeSharePoint.Response.Json(500, FakeSharePoint.RestError("-2147467259, Microsoft.SharePoint.SPException", "The file could not be saved.")));
            var file = await source.OpenAsync("Documents/kept/new.bin", VfsOpenMode.CreateNew, None);
            await file.WriteAsync(0, mine, None);
            await source.SetTimesAsync("Documents/kept/new.bin", null, written, None);
            await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.DisposeAsync());
            Assert.IsNull(documents.Root.Folder("kept")!.File("new.bin"));
        }

        Assert.AreEqual(2, Directory.GetFiles(uploads).Length, "the bytes and their note, left by the source that stopped");
        File.WriteAllBytes(Path.Combine(uploads, Guid.NewGuid().ToString("N") + ".upload"), new byte[10]);


        await using var again = await StartAsync(fake, FarmSettings(fake), context);
        again.UploadRetryAfter = _ => TimeSpan.FromMilliseconds(200);
        await UntilAsync(() => documents.Root.Folder("kept")!.File("new.bin") is { } landed && landed.Content.AsSpan().SequenceEqual(mine), "the upload lands at the next start");
        await UntilAsync(() => Directory.GetFiles(uploads).Length == 0, "and nothing is left of it here, nor of the half-written one");
        await UntilAsync(() => context.LastHealth != ComponentHealth.Degraded, "the source is well");
        Assert.AreEqual(mine.Length, (await again.StatAsync("Documents/kept/new.bin", None))!.Length);
    }

    /// <summary>
    /// A file that waits to be uploaded is the file here: it cannot be made anew or renamed, a later write of it takes
    /// its place (starting from its bytes where the open keeps what is there), a write given up leaves it waiting, and
    /// deleting it gives the upload up.
    /// </summary>
    [TestMethod]
    public async Task AFileThatWaitsToBeUploadedIsTheFileUntilItIsWrittenAgainOrDeleted()
    {
        using var fake = FakeSharePoint.LabFarm();
        var documents = fake.TestSite.Library("Documents");
        var upstream = fake.Seed(documents, "kept/notes.txt", "old"u8.ToArray());
        upstream.Locked = true;
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, FarmSettings(fake), context);
        source.UploadRetryAfter = _ => TimeSpan.FromHours(1);
        var uploads = Path.Combine(context.CacheDirectory, "uploads");
        const string path = "Documents/kept/notes.txt";

        var first = await source.OpenAsync(path, VfsOpenMode.Create, None);
        await first.WriteAsync(0, "first"u8.ToArray(), None);
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await first.DisposeAsync());

        Assert.AreEqual(VfsError.AlreadyExists, (await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.OpenAsync(path, VfsOpenMode.CreateNew, None))).Error);
        var rename = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.RenameAsync(path, "Documents/kept/renamed.txt", false, None));
        Assert.AreEqual(VfsError.AccessDenied, rename.Error);
        StringAssert.Contains(rename.Message, "not uploaded yet");
        Assert.AreEqual(VfsError.AccessDenied, (await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.RenameAsync("Documents/kept", "Documents/moved", false, None))).Error, "nor the folder it is in");

        // written again, keeping what is there: the later bytes begin from the ones that wait, and take their place
        var second = await source.OpenAsync(path, VfsOpenMode.ReadWrite, None);
        Assert.AreEqual(5, second.Length);
        await second.WriteAsync(5, ", second"u8.ToArray(), None);
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await second.DisposeAsync());
        CollectionAssert.AreEqual("first, second"u8.ToArray(), await ReadAllAsync(source, path));
        Assert.AreEqual(2, Directory.GetFiles(uploads).Length, "one file waits, with its note: " + string.Join(", ", Directory.GetFiles(uploads).Select(Path.GetFileName)));

        // a write of it that is given up leaves the one that waits
        var third = await source.OpenAsync(path, VfsOpenMode.Create, None);
        await third.WriteAsync(0, "half"u8.ToArray(), None);
        await third.AbandonAsync();
        CollectionAssert.AreEqual("first, second"u8.ToArray(), await ReadAllAsync(source, path));
        Assert.AreEqual(ComponentHealth.Degraded, context.LastHealth);

        // deleted: the upload is given up, and SharePoint's own goes as the delete asks
        upstream.Locked = false;
        await source.DeleteAsync(path, None);
        Assert.IsNull(documents.Root.Folder("kept")!.File("notes.txt"));
        Assert.IsNull(await source.StatAsync(path, None));
        Assert.AreEqual(0, Directory.GetFiles(uploads).Length);
        Assert.AreEqual(ComponentHealth.Ready, context.LastHealth);
    }

    /// <summary>
    /// A write that is given up (a mount's upload whose body broke off, a file deleted as it closes) is not uploaded: what
    /// SharePoint had stays, and a file that was new is not there. Closed, the part that came went up as the file.
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "farm, REST")]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task AWriteGivenUpIsNotUploadedAndWhatWasThereStays(bool tenant)
    {
        using var fake = tenant ? FakeSharePoint.Sandbox() : FakeSharePoint.LabFarm();
        var documents = fake.TestSite.Library("Documents");
        var old = "what was there"u8.ToArray();
        var upstream = fake.Seed(documents, "given/old.txt", old);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant ? TenantSettings(fake, SharePointSettings.AppDeviceCode) : FarmSettings(fake), context);

        var over = await source.OpenAsync("Documents/given/old.txt", VfsOpenMode.Create, None);
        await over.WriteAsync(0, "half of what was to replace it"u8.ToArray(), None);
        await over.AbandonAsync();
        CollectionAssert.AreEqual(old, upstream.Content);
        Assert.AreEqual(old.Length, (await source.StatAsync("Documents/given/old.txt", None))!.Length);
        CollectionAssert.AreEqual(old, await ReadAllAsync(source, "Documents/given/old.txt"));

        var fresh = await source.OpenAsync("Documents/given/new.txt", VfsOpenMode.CreateNew, None);
        await fresh.WriteAsync(0, "half a new file"u8.ToArray(), None);
        Assert.IsNotNull(await source.StatAsync("Documents/given/new.txt", None), "there while it is being written");
        await fresh.AbandonAsync();
        Assert.IsNull(documents.Root.Folder("given")!.File("new.txt"));
        Assert.IsNull(await source.StatAsync("Documents/given/new.txt", None));
        CollectionAssert.DoesNotContain(await SpsTestServer.NamesAsync(source, "Documents/given"), "new.txt");
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(context.CacheDirectory, "uploads")).Length);
        Assert.AreNotEqual(ComponentHealth.Degraded, context.LastHealth);
    }

    /// <summary>
    /// A file that changed upstream since its folder was last listed is read whole, as SharePoint has it now. After a
    /// start whose walk kept the library by its stamp, an open asks SharePoint for the item (its id and version) and took
    /// the length from the listing held here: a file a colleague had made longer was downloaded to its old length, and
    /// that copy was kept under the new version's key, so it went on being served cut short after the walk had put the
    /// listing right (review of 21-Nov-2026, R4; shown by a run on 22-Nov-2026). The length is the item's own now.
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "farm, REST")]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task AFileChangedSinceItWasListedIsReadWholeNotCutAtTheLengthListed(bool tenant)
    {
        using var fake = tenant ? FakeSharePoint.Sandbox() : FakeSharePoint.LabFarm();
        var documents = fake.TestSite.Library("Documents");
        var old = Enumerable.Range(0, 100).Select(i => (byte)('a' + (i % 26))).ToArray();
        var longer = Enumerable.Range(0, 300).Select(i => (byte)('A' + (i % 26))).ToArray();
        fake.Seed(documents, "grown/report.txt", old);
        const string path = "Documents/grown/report.txt";
        using var context = new TestSourceContext("sharepoint");
        SharePointSettings Kept()
        {
            var settings = tenant ? TenantSettings(fake, SharePointSettings.AppDeviceCode) : FarmSettings(fake);
            settings.KeepListings = true;
            return settings;
        }

        await using (var first = await StartAsync(fake, Kept(), context))
        {
            await first.CountFilesAsync(None);
        }

        // the next start holds the listings of the one before, and its walk keeps the library by its stamp
        await using var source = await StartAsync(fake, Kept(), context);
        await source.CountFilesAsync(None);
        Assert.AreEqual(old.Length, (await source.StatAsync(path, None))!.Length);

        fake.Seed(documents, "grown/report.txt", longer);   // a colleague saves a longer version
        CollectionAssert.AreEqual(longer, await ReadAllAsync(source, path), "read before the next walk: whole, as SharePoint has it now");
        Assert.AreEqual(longer.Length, (await source.StatAsync(path, None))!.Length, "and listed at the length it has");

        await source.WalkAsync(new SourceCounters(), None);
        CollectionAssert.AreEqual(longer, await ReadAllAsync(source, path), "and after the walk");
    }
    /// <summary>
    /// A rename over a file that fails leaves the file that was there. The rename deleted the target and then moved, in
    /// two requests: a move that failed after the delete (a lock, a throttle, a dropped connection) left the name empty,
    /// the old file in the recycle bin and the new one under the name it was written to. Editors save this way (the
    /// review's second pass, 22-Nov-2026, N13). SharePoint replaces in the one move now.
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "farm, REST")]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task ARenameOverAFileThatFailsLeavesTheFileThatWasThere(bool tenant)
    {
        using var fake = tenant ? FakeSharePoint.Sandbox() : FakeSharePoint.LabFarm();
        var documents = fake.TestSite.Library("Documents");
        var old = "the document as it was"u8.ToArray();
        var saved = "the document as the editor saved it, beside the old one"u8.ToArray();
        var document = fake.Seed(documents, "edited/report.docx", old);
        fake.Seed(documents, "edited/~report.tmp", saved);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant ? TenantSettings(fake, SharePointSettings.AppDeviceCode) : FarmSettings(fake), context);
        CollectionAssert.AreEquivalent(new[] { "report.docx", "~report.tmp" }, await SpsTestServer.NamesAsync(source, "Documents/edited"));

        // the move is refused once, as for a file someone has open
        fake.Fail(r => tenant ? r.Method == "PATCH" && r.Host.StartsWith("graph.microsoft.", StringComparison.OrdinalIgnoreCase) : r.RawPath.Contains("MoveToUsingPath", StringComparison.OrdinalIgnoreCase), 1, _ => tenant
            ? FakeSharePoint.Response.Json(423, FakeSharePoint.GraphError("resourceLocked", "The resource you are attempting to access is locked"))
            : FakeSharePoint.Response.Json(423, FakeSharePoint.RestError("-2130575245, Microsoft.SharePoint.SPFileLockException", "The file is locked for shared use.")));
        await Assert.ThrowsExactlyAsync<VfsException>(() => source.RenameAsync("Documents/edited/~report.tmp", "Documents/edited/report.docx", replace: true, None));
        var there = documents.Root.Folder("edited")!.File("report.docx");
        Assert.IsNotNull(there, "the document is still under its name");
        CollectionAssert.AreEqual(old, there.Content, "as it was");
        CollectionAssert.AreEquivalent(new[] { "report.docx", "~report.tmp" }, await SpsTestServer.NamesAsync(source, "Documents/edited"));

        await source.RenameAsync("Documents/edited/~report.tmp", "Documents/edited/report.docx", replace: true, None);
        CollectionAssert.AreEqual(saved, documents.Root.Folder("edited")!.File("report.docx")!.Content, "replaced in the one move");
        CollectionAssert.AreEqual(new[] { "report.docx" }, await SpsTestServer.NamesAsync(source, "Documents/edited"));
        CollectionAssert.AreEqual(saved, await ReadAllAsync(source, "Documents/edited/report.docx"));
        GC.KeepAlive(document);
    }

    /// <summary>
    /// A gateway that stops waiting (504) says nothing of what the server behind it went on to do. A request that changes
    /// something was sent again all the same: a file that had been moved was not found by its own second try, and "not
    /// found" was what a rename that had been made came to. Such a request is sent once, and its answer says the outcome
    /// is not known; the folder, listed again, shows which it was. A request that only reads is asked again, as before
    /// (the review of 21-Nov-2026, R16).
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "farm, REST")]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task AChangeAGatewayStoppedWaitingForIsNotSentAgain(bool tenant)
    {
        using var fake = tenant ? FakeSharePoint.Sandbox() : FakeSharePoint.LabFarm();
        fake.Seed(fake.TestSite.Library("Documents"), "gateway/draft.txt", "a draft"u8.ToArray());
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant ? TenantSettings(fake, SharePointSettings.AppDeviceCode) : FarmSettings(fake), context);
        CollectionAssert.AreEqual(new[] { "draft.txt" }, await SpsTestServer.NamesAsync(source, "Documents/gateway"));

        // the move is made, and the gateway has stopped waiting for the server's word on it
        bool IsMove(string method, string host, string rawPath) => tenant
            ? method == "PATCH" && host.StartsWith("graph.microsoft.", StringComparison.OrdinalIgnoreCase)
            : rawPath.Contains("MoveToUsingPath", StringComparison.OrdinalIgnoreCase);
        fake.TimeOutAtTheGateway(r => IsMove(r.Method, r.Host, r.RawPath), 1);
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(() => source.RenameAsync("Documents/gateway/draft.txt", "Documents/gateway/final.txt", replace: false, None));
        Assert.AreEqual(VfsError.Unavailable, ex.Error, ex.Message);
        StringAssert.Contains(ex.Message, "it is not known whether the change was made");
        Assert.AreEqual(1, fake.Count(s => IsMove(s.Method, s.Host, s.RawPath)), "sent once");

        // a listing the gateway gives no answer to in time is asked again, and shows the move was made
        fake.Fail(r => r.Method == "GET", 1, _ => FakeSharePoint.Response.Empty(504));
        CollectionAssert.AreEqual(new[] { "final.txt" }, await SpsTestServer.NamesAsync(source, "Documents/gateway"));
        Assert.AreEqual(1, fake.Count(s => s.Method == "GET" && s.Status == 504), "the read was answered 504 once, and asked again");
    }

    /// <summary>
    /// The same for a new file: its upload makes the file, and the gateway has stopped waiting by then. Sent again as new
    /// only, the upload was refused by the file its own first try had made: who closed the file was told one was there
    /// already, and what they wrote was not kept. It is sent once: they are told the outcome is not known, the bytes are
    /// kept, and the source's own next try, which is not as new, lands them (the review of 21-Nov-2026, R16).
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "farm, REST")]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task ANewFileAGatewayStoppedWaitingForIsNotSentAgainAsNew(bool tenant)
    {
        using var fake = tenant ? FakeSharePoint.Sandbox() : FakeSharePoint.LabFarm();
        var documents = fake.TestSite.Library("Documents");
        fake.Seed(documents, "gateway/there.txt", "x"u8.ToArray());
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant ? TenantSettings(fake, SharePointSettings.AppDeviceCode) : FarmSettings(fake), context);
        source.UploadRetryAfter = _ => TimeSpan.FromMilliseconds(200);
        CollectionAssert.AreEqual(new[] { "there.txt" }, await SpsTestServer.NamesAsync(source, "Documents/gateway"));

        fake.TimeOutAtTheGateway(r => IsUpload(tenant, r.Method, r.Host, r.RawPath), 1);
        var mine = "what I wrote"u8.ToArray();
        var file = await source.OpenAsync("Documents/gateway/new.txt", VfsOpenMode.CreateNew, None);
        await file.WriteAsync(0, mine, None);
        var told = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.DisposeAsync());
        Assert.AreEqual(VfsError.Unavailable, told.Error, told.Message);
        StringAssert.Contains(told.Message, "it is not known whether the change was made");

        // the source's next try is not as new: it lands over what the first made, and the wait is over
        await UntilAsync(() => Directory.GetFiles(Path.Combine(context.CacheDirectory, "uploads")).Length == 0, "what was kept here goes once it has landed");
        await UntilAsync(() => context.LastHealth == ComponentHealth.Ready, "and the source is well again");
        var sent = fake.Requests.Where(s => IsUpload(tenant, s.Method, s.Host, s.RawPath)).Select(s => s.Status).ToList();
        Assert.AreEqual(2, sent.Count, "once as new, and once more by the source: " + string.Join(", ", sent));
        Assert.AreEqual(504, sent[0]);
        Assert.IsTrue(sent[1] is 200 or 201, $"which landed ({sent[1]})");
        CollectionAssert.AreEqual(mine, documents.Root.Folder("gateway")!.File("new.txt")!.Content);
    }

    /// <summary>
    /// A delete may be sent again, and Graph's is. One a gateway stopped waiting for had deleted the item by then, and its
    /// second try found nothing: "not found" was what a delete that had been made came to. A delete that finds nothing
    /// on a later try has been done.
    /// </summary>
    [TestMethod]
    public async Task ADeleteAGatewayStoppedWaitingForIsDone()
    {
        using var fake = FakeSharePoint.Sandbox();
        var documents = fake.TestSite.Library("Documents");
        fake.Seed(documents, "gateway/old.txt", "x"u8.ToArray());
        fake.Seed(documents, "gateway/stays.txt", "y"u8.ToArray());
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.AppDeviceCode), context);
        CollectionAssert.AreEqual(new[] { "old.txt", "stays.txt" }, await SpsTestServer.NamesAsync(source, "Documents/gateway"));

        static bool IsDelete(string method, string host) => method == "DELETE" && host.StartsWith("graph.microsoft.", StringComparison.OrdinalIgnoreCase);
        fake.TimeOutAtTheGateway(r => IsDelete(r.Method, r.Host), 1);
        await source.DeleteAsync("Documents/gateway/old.txt", None);
        var sent = fake.Requests.Where(s => IsDelete(s.Method, s.Host)).Select(s => s.Status).ToList();
        CollectionAssert.AreEqual(new[] { 504, 404 }, sent, "made behind the gateway, then found gone: " + string.Join(", ", sent));
        CollectionAssert.AreEqual(new[] { "stays.txt" }, await SpsTestServer.NamesAsync(source, "Documents/gateway"));
        Assert.IsNull(documents.Root.Folder("gateway")!.File("old.txt"));
    }

    private static bool IsUpload(bool tenant, string method, string host, string rawPath) => tenant
        ? host.StartsWith("graph.microsoft.", StringComparison.OrdinalIgnoreCase) && (method == "PUT" && rawPath.Contains("content", StringComparison.OrdinalIgnoreCase) || rawPath.Contains("createUploadSession", StringComparison.OrdinalIgnoreCase))
        : method == "POST" && rawPath.Contains("/Files/AddUsingPath", StringComparison.OrdinalIgnoreCase);

    private static int Uploads(FakeSharePoint fake, bool tenant) => fake.Count(s => IsUpload(tenant, s.Method, s.Host, s.RawPath));

    /// <summary>
    /// An upload SharePoint will never take is not kept. Every failed upload was kept and tried again (22-Nov-2026), so
    /// one into a library the account may only read was tried every five minutes for good, the source Degraded with it,
    /// until the person deleted the file through the mount (the review's second pass, N3). Who closes the file is told,
    /// as before; nothing of it stays here, it is not tried again, and the source is not held Degraded for it.
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "farm, REST")]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task AnUploadThatCanNeverLandIsToldAtTheCloseAndNotKept(bool tenant)
    {
        using var fake = tenant ? FakeSharePoint.Sandbox() : FakeSharePoint.LabFarm();
        var documents = fake.TestSite.Library("Documents");
        fake.Seed(documents, "readonly/there.txt", "x"u8.ToArray());
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant ? TenantSettings(fake, SharePointSettings.AppDeviceCode) : FarmSettings(fake), context);
        source.UploadRetryAfter = _ => TimeSpan.FromMilliseconds(50);
        var uploads = Path.Combine(context.CacheDirectory, "uploads");

        // the account may read the library and not write to it, as each server words that
        fake.Fail(r => IsUpload(tenant, r.Method, r.Host, r.RawPath), int.MaxValue, _ => tenant
            ? FakeSharePoint.Response.Json(403, FakeSharePoint.GraphError("accessDenied", "Access denied"))
            : FakeSharePoint.Response.Json(403, FakeSharePoint.RestError("-2147024891, System.UnauthorizedAccessException", "Access denied. You do not have permission to perform this action or access this resource.")));

        var file = await source.OpenAsync("Documents/readonly/new.txt", VfsOpenMode.CreateNew, None);
        await file.WriteAsync(0, "what I wrote"u8.ToArray(), None);
        var refused = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.DisposeAsync());
        Assert.AreEqual(VfsError.AccessDenied, refused.Error, "who closed the file is told");

        var tried = Uploads(fake, tenant);
        await Task.Delay(600);
        Assert.AreEqual(tried, Uploads(fake, tenant), "it is not tried again: no later try can go better");
        Assert.AreEqual(0, Directory.GetFiles(uploads).Length, "nothing of it is kept");
        Assert.AreNotEqual(ComponentHealth.Degraded, context.LastHealth, "nor is it held against the source");
        Assert.IsNull(await source.StatAsync("Documents/readonly/new.txt", None), "the name it had here while it was open is gone");
        CollectionAssert.DoesNotContain(await SpsTestServer.NamesAsync(source, "Documents/readonly"), "new.txt");
    }

    /// <summary>
    /// A file that waits is given up once its place has gone, and can be deleted meanwhile. With its library renamed
    /// upstream it was tried every five minutes for good, a delete of it and a write over it both failed NotFound, and
    /// the next start took it up again: there was no way to be rid of it (the review's second pass, N3).
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "deleted through the mount")]
    [DataRow(true, DisplayName = "tried again")]
    public async Task AFileThatWaitsEndsWhenItsPlaceHasGone(bool triedAgain)
    {
        using var fake = FakeSharePoint.LabFarm();
        var documents = fake.TestSite.Library("Documents");
        fake.Seed(documents, "gone/there.txt", "x"u8.ToArray());
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, FarmSettings(fake), context);
        source.UploadRetryAfter = _ => triedAgain ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromHours(1);
        var uploads = Path.Combine(context.CacheDirectory, "uploads");
        fake.Fail(r => r.Method == "POST" && r.RawPath.Contains("/Files/AddUsingPath", StringComparison.OrdinalIgnoreCase), int.MaxValue,
            _ => FakeSharePoint.Response.Json(500, FakeSharePoint.RestError("-2147467259, Microsoft.SharePoint.SPException", "The file could not be saved.")));

        var file = await source.OpenAsync("Documents/gone/new.txt", VfsOpenMode.CreateNew, None);
        await file.WriteAsync(0, "what I wrote"u8.ToArray(), None);
        await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.DisposeAsync());
        Assert.AreEqual(2, Directory.GetFiles(uploads).Length, "it waits: a failure that may pass");
        Assert.AreEqual(ComponentHealth.Degraded, context.LastHealth);

        // the library gets another name upstream; the source sees it once its site's libraries are looked up again
        documents.Title = "Renamed";
        await Task.Delay(2500);
        CollectionAssert.DoesNotContain(await SpsTestServer.NamesAsync(source, ""), "Documents");

        if (!triedAgain)
        {
            await source.DeleteAsync("Documents/gone/new.txt", None);
        }

        await UntilAsync(() => Directory.GetFiles(uploads).Length == 0, "nothing of it is left here");
        await UntilAsync(() => context.LastHealth == ComponentHealth.Ready, "and the source is well again");
    }

    /// <summary>
    /// A file made as new does not replace one a colleague has made under that name since the folder was listed. "New"
    /// went by the listing kept here, which may be as old as the last walk, and the upload replaced whatever was there
    /// (the review's second pass, N2). The upload now asks SharePoint for a new file only: one that is there stays, who
    /// closed the file is told it exists, and the folder is listed again.
    /// </summary>
    [TestMethod]
    [DataRow(false, false, DisplayName = "farm, REST")]
    [DataRow(false, true, DisplayName = "farm, REST, in pieces")]
    [DataRow(true, false, DisplayName = "tenant, Graph")]
    [DataRow(true, true, DisplayName = "tenant, Graph, an upload session")]
    public async Task ANewFileDoesNotReplaceOneMadeUpstreamSinceTheFolderWasListed(bool tenant, bool large)
    {
        using var fake = tenant ? FakeSharePoint.Sandbox() : FakeSharePoint.LabFarm();
        var documents = fake.TestSite.Library("Documents");
        fake.Seed(documents, "shared/there.txt", "x"u8.ToArray());
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant ? TenantSettings(fake, SharePointSettings.AppDeviceCode) : FarmSettings(fake), context);
        source.UploadRetryAfter = _ => TimeSpan.FromMilliseconds(50);
        CollectionAssert.AreEqual(new[] { "there.txt" }, await SpsTestServer.NamesAsync(source, "Documents/shared"));

        var theirs = "what a colleague saved here a minute ago"u8.ToArray();
        var colleague = fake.Seed(documents, "shared/report.txt", theirs);
        Assert.IsNull(await source.StatAsync("Documents/shared/report.txt", None), "the listing held here is older than the colleague's file");

        var mine = new byte[large ? (tenant ? GraphDrives.SimpleUploadLimit : SharePointRestClient.SimpleUploadLimit) + 1 : 11];
        new Random(5).NextBytes(mine);
        var file = await source.OpenAsync("Documents/shared/report.txt", VfsOpenMode.CreateNew, None);
        await file.WriteAsync(0, mine, None);
        var refused = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.DisposeAsync());
        Assert.AreEqual(VfsError.AlreadyExists, refused.Error, "who closed the file is told there is one");

        CollectionAssert.AreEqual(theirs, colleague.Content, "the colleague's file is as they saved it");
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(context.CacheDirectory, "uploads")).Length, "what was written here is not kept to be tried again");
        Assert.AreNotEqual(ComponentHealth.Degraded, context.LastHealth);
        Assert.AreEqual(theirs.Length, (await source.StatAsync("Documents/shared/report.txt", None))!.Length, "the folder is listed again, and shows theirs");

        // a file the person means to replace still replaces
        var over = await source.OpenAsync("Documents/shared/report.txt", VfsOpenMode.Create, None);
        await over.WriteAsync(0, mine, None);
        await over.DisposeAsync();
        CollectionAssert.AreEqual(mine, colleague.Content);
    }

    /// <summary>
    /// A file a source that was replaced still has open is left alone by the source that starts in its place. The new
    /// start removes what the last run left half-written, and told those apart from a file still open only by Windows
    /// refusing the delete: on Linux and macOS the open file went (the review's second pass, N4).
    /// </summary>
    [TestMethod]
    public async Task AFileStillOpenInTheSourceBeforeIsNotRemovedByTheOneThatStartsInItsPlace()
    {
        using var fake = FakeSharePoint.LabFarm();
        var documents = fake.TestSite.Library("Documents");
        fake.Seed(documents, "open/there.txt", "x"u8.ToArray());
        using var context = new TestSourceContext("sharepoint");
        var uploads = Path.Combine(context.CacheDirectory, "uploads");
        var first = await StartAsync(fake, FarmSettings(fake), context);
        var file = await first.OpenAsync("Documents/open/new.txt", VfsOpenMode.CreateNew, None);
        await file.WriteAsync(0, "half of what I mean to write"u8.ToArray(), None);
        File.WriteAllBytes(Path.Combine(uploads, Guid.NewGuid().ToString("N") + ".upload"), new byte[10]);
        Assert.AreEqual(2, Directory.GetFiles(uploads).Length);

        await using (await StartAsync(fake, FarmSettings(fake), context))
        {
            Assert.AreEqual(1, Directory.GetFiles(uploads).Length, "what the last run left half-written goes; the file still open stays");
        }

        await file.AbandonAsync();
        await first.DisposeAsync();
    }

    private static async Task<byte[]> ReadAllAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, None);
        var bytes = new byte[file.Length];
        Assert.AreEqual(bytes.Length, await file.ReadFullyAsync(0, bytes, None));
        return bytes;
    }

    private static async Task UntilAsync(Func<bool> met, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!met())
        {
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(20), what);
            await Task.Delay(50);
        }
    }
}
