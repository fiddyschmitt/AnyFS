using System.Net;
using System.Text;
using System.Text.Json;
using AnyFS.Fixtures;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Microsoft365;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

[TestClass]
public class SharePointNamesTests
{
    [TestMethod]
    [DataRow("readme.txt")]
    [DataRow("Budget 2026 (final).xlsx")]
    [DataRow("über straße.docx")]
    [DataRow("a.b.c")]
    [DataRow("consultant.txt")]
    [DataRow("lpt10.txt")]
    public void AcceptsOrdinaryNames(string name) => Assert.IsNull(SharePointNames.Problem(name));

    [TestMethod]
    [DataRow("")]
    [DataRow("bad:name.txt")]
    [DataRow("what?.txt")]
    [DataRow("a|b")]
    [DataRow("quote\".txt")]
    [DataRow(" leading.txt")]
    [DataRow("trailing.txt ")]
    [DataRow("trailing.")]
    [DataRow("..")]
    [DataRow("~$lock.docx")]
    [DataRow("_vti_bin")]
    [DataRow(".lock")]
    [DataRow("CON")]
    [DataRow("con.txt")]
    [DataRow("LPT1.log")]
    [DataRow("desktop.ini")]
    public void RefusesWhatSharePointRefuses(string name) => Assert.IsNotNull(SharePointNames.Problem(name), name);

    [TestMethod]
    public void RequireThrowsInvalidPathWithTheReason()
    {
        var ex = Assert.ThrowsExactly<VfsException>(() => SharePointNames.Require("docs/bad:name.txt"));
        Assert.AreEqual(VfsError.InvalidPath, ex.Error);
        StringAssert.Contains(ex.Message, "':'");

        ex = Assert.ThrowsExactly<VfsException>(() => SharePointNames.Require(new string('a', 401)));
        Assert.AreEqual(VfsError.InvalidPath, ex.Error);
        SharePointNames.Require("docs/fine.txt");
    }

    [TestMethod]
    [DataRow("Documents", "Documents")]
    [DataRow("Health / Safety", "Health ／ Safety")]
    [DataRow("Q&A: what? *now*", "Q&A： what？ ＊now＊")]
    [DataRow("a\\b|c\"d<e>f", "a＼b｜c＂d＜e＞f")]
    [DataRow("Archive. ", "Archive．")]
    [DataRow("Notes...", "Notes．．．")]
    [DataRow("tab\there", "tabhere")]
    [DataRow("über straße", "über straße")]
    [DataRow("   ", "")]
    public void ATitleShowsAsAFolderNameAPathCanHold(string title, string expected)
    {
        Assert.AreEqual(expected, SharePointNames.FolderName(title));
        Assert.IsTrue(expected.Length == 0 || SharePointNames.Problem(expected) is null, $"{expected}: {SharePointNames.Problem(expected)}");
    }
}

[TestClass]
public class SiteAddressTests
{
    [TestMethod]
    [DataRow("https://contoso.sharepoint.com/sites/Team", "contoso.sharepoint.com", "/sites/Team")]
    [DataRow("https://contoso.sharepoint.com/sites/Team/", "contoso.sharepoint.com", "/sites/Team")]
    [DataRow("contoso.sharepoint.com/sites/Team", "contoso.sharepoint.com", "/sites/Team")]
    [DataRow("https://Contoso.SharePoint.com/teams/Ops/Shared%20Documents/Forms/AllItems.aspx", "contoso.sharepoint.com", "/teams/Ops")]
    [DataRow("https://contoso.sharepoint.com", "contoso.sharepoint.com", "")]
    [DataRow("https://contoso.sharepoint.com/", "contoso.sharepoint.com", "")]
    [DataRow("https://contoso.sharepoint.com/Shared%20Documents/Forms/AllItems.aspx", "contoso.sharepoint.com", "")]
    [DataRow("https://contoso.sharepoint.com/SitePages/Home.aspx", "contoso.sharepoint.com", "")]
    [DataRow("https://intranet.contoso.com/dept/hr", "intranet.contoso.com", "/dept/hr")]
    [DataRow("https://intranet.contoso.com/dept/hr/SitePages/Home.aspx", "intranet.contoso.com", "/dept/hr")]
    [DataRow("https://contoso.sharepoint.com/sites/Team/Plans", "contoso.sharepoint.com", "/sites/Team/Plans")]
    [DataRow("https://contoso.sharepoint.com/sites/Team/Plans/SitePages/News.aspx", "contoso.sharepoint.com", "/sites/Team/Plans")]
    [DataRow("https://contoso.sharepoint.com/sites/Team/Shared%20Documents/Reports/2026", "contoso.sharepoint.com", "/sites/Team")]
    [DataRow("https://contoso.sharepoint.com/sites/Team/Projects/Forms/AllItems.aspx", "contoso.sharepoint.com", "/sites/Team")]
    [DataRow("https://contoso.sharepoint.com/sites/Team/Lists/Tasks/AllItems.aspx", "contoso.sharepoint.com", "/sites/Team")]
    [DataRow("https://acme.sharepoint.us/_layouts/15/viewlsts.aspx?view=15", "acme.sharepoint.us", "")]
    [DataRow("https://acme.sharepoint.us/FirstOne/default.aspx", "acme.sharepoint.us", "/FirstOne")]
    [DataRow("http://192.168.0.92:8080/sites/anyfs/_api/web", "192.168.0.92:8080", "/sites/anyfs")]
    public void ParsesWhatPeoplePaste(string text, string host, string path)
    {
        Assert.IsTrue(SiteAddress.TryParse(text, out var address, out var reason), reason);
        Assert.AreEqual(host, address.Host);
        Assert.AreEqual(path, address.ServerRelativePath);
    }

    [TestMethod]
    public void AnAddressThatGoesDeeperStepsBackToItsSite()
    {
        static List<string> Paths(string text) => SiteAddress.TryParse(text, out var a, out _) ? a.Candidates().Select(c => c.ServerRelativePath).ToList() : [];
        CollectionAssert.AreEqual(new[] { "/sites/Team/Projects/2026", "/sites/Team/Projects", "/sites/Team" }, Paths("https://contoso.sharepoint.com/sites/Team/Projects/2026"));
        CollectionAssert.AreEqual(new[] { "/sites/Team" }, Paths("https://contoso.sharepoint.com/sites/Team"));
        CollectionAssert.AreEqual(new[] { "/dept/hr", "/dept" }, Paths("http://intranet/dept/hr"), "not past the first name to the root site, which is not what was asked for");
        CollectionAssert.AreEqual(new[] { "" }, Paths("http://intranet"));
    }

    [TestMethod]
    public void GraphPathForms()
    {
        Assert.AreEqual("sites/contoso.sharepoint.com:/sites/Team", new SiteAddress("contoso.sharepoint.com", "/sites/Team").GraphPath);
        Assert.AreEqual("sites/contoso.sharepoint.com", new SiteAddress("contoso.sharepoint.com", "").GraphPath);
        Assert.AreEqual("sites/contoso.sharepoint.com:/Shared%20Documents", new SiteAddress("contoso.sharepoint.com", "/Shared Documents").GraphPath);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("ftp://contoso.sharepoint.com/sites/x")]
    [DataRow("https://")]
    public void RefusesNonAddresses(string text)
    {
        Assert.IsFalse(SiteAddress.TryParse(text, out _, out var reason));
        Assert.IsFalse(string.IsNullOrEmpty(reason));
    }
}

[TestClass]
public class SharePointPathsTests
{
    [TestMethod]
    public void ItemAddressesAreEscapedPerSegment()
    {
        Assert.AreEqual("drives/d1/root", SharePointPaths.ItemAddress("d1", ""));
        Assert.AreEqual("drives/d1/root:/a%20b/c%23d%25e.txt:", SharePointPaths.ItemAddress("d1", "a b/c#d%e.txt"));
        Assert.AreEqual("drives/d1/root:/%C3%BCber/stra%C3%9Fe:", SharePointPaths.ItemAddress("d1", "über/straße"));
    }
}

[TestClass]
public class GraphClientTests
{
    [TestMethod]
    public void MapsGraphErrorsToVfsErrors()
    {
        static string Body(string code) => "{\"error\":{\"code\":\"" + code + "\",\"message\":\"m\"}}";
        Assert.AreEqual(VfsError.NotFound, GraphClient.MapError(HttpStatusCode.NotFound, Body("itemNotFound"), "p", SharePointPlugin.Service).Error);
        Assert.AreEqual(VfsError.AlreadyExists, GraphClient.MapError(HttpStatusCode.Conflict, Body("nameAlreadyExists"), "p", SharePointPlugin.Service).Error);
        Assert.AreEqual(VfsError.AccessDenied, GraphClient.MapError(HttpStatusCode.Forbidden, Body("accessDenied"), "p", SharePointPlugin.Service).Error);
        Assert.AreEqual(VfsError.AccessDenied, GraphClient.MapError(HttpStatusCode.Locked, Body("resourceLocked"), "p", SharePointPlugin.Service).Error);
        Assert.AreEqual(VfsError.InvalidPath, GraphClient.MapError(HttpStatusCode.BadRequest, Body("invalidRequest"), "p", SharePointPlugin.Service).Error);
        Assert.AreEqual(VfsError.Unavailable, GraphClient.MapError(HttpStatusCode.TooManyRequests, Body("activityLimitReached"), "p", SharePointPlugin.Service).Error);
        Assert.AreEqual(VfsError.Unavailable, GraphClient.MapError(HttpStatusCode.InternalServerError, Body("generalException"), "p", SharePointPlugin.Service).Error);
        Assert.AreEqual(VfsError.Unavailable, GraphClient.MapError(HttpStatusCode.Unauthorized, "", "p", SharePointPlugin.Service).Error);
        Assert.AreEqual(VfsError.IoError, GraphClient.MapError(HttpStatusCode.InsufficientStorage, Body("quotaLimitReached"), "p", SharePointPlugin.Service).Error);
        Assert.AreEqual(VfsError.NotSupported, GraphClient.MapError(HttpStatusCode.BadRequest, Body("notSupported"), "p", SharePointPlugin.Service).Error);
        Assert.AreEqual(VfsError.IoError, GraphClient.MapError(HttpStatusCode.Gone, "not json", "p", SharePointPlugin.Service).Error);
        Assert.AreEqual("itemNotFound", GraphClient.ErrorCode(Body("itemNotFound")));
        Assert.AreEqual("", GraphClient.ErrorCode("<html>"));

        // the messages name the service the client works for
        StringAssert.Contains(GraphClient.MapError(HttpStatusCode.TooManyRequests, "", "p", SharePointPlugin.Service).Message, "SharePoint is throttling this app");
        StringAssert.Contains(GraphClient.MapError(HttpStatusCode.InsufficientStorage, Body("quotaLimitReached"), "p", SharePointPlugin.Service).Message, "the site is out of storage");
        StringAssert.Contains(GraphClient.MapError(HttpStatusCode.ServiceUnavailable, "", "p", new Microsoft365Service("Exchange Online", "outlook", "the mailbox")).Message, "Exchange Online answered 503");
    }

    [TestMethod]
    public void ParsesItemsAndDrives()
    {
        using var file = JsonDocument.Parse("""
            {"id":"01ABC","name":"report.docx","size":1234,"eTag":"\"{E1},2\"","cTag":"\"c:{E1},3\"",
             "file":{"mimeType":"application/x"},"@microsoft.graph.downloadUrl":"https://x.sharepoint.com/dl?tempauth=1",
             "createdDateTime":"2026-01-02T03:04:05Z","lastModifiedDateTime":"2026-02-03T04:05:06Z",
             "fileSystemInfo":{"createdDateTime":"2025-12-31T00:00:00Z","lastModifiedDateTime":"2026-02-01T00:00:00Z"}}
            """);
        var (entry, item) = GraphDrives.ParseItem(file.RootElement);
        Assert.AreEqual("report.docx", entry.Name);
        Assert.IsFalse(entry.IsDirectory);
        Assert.AreEqual(1234, entry.Length);
        Assert.AreEqual(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), entry.Modified, "fileSystemInfo wins over the item's own stamps");
        Assert.AreEqual(new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero), entry.Created);
        Assert.AreEqual("\"c:{E1},3\"", entry.ETag);
        Assert.AreEqual("01ABC", item.Id);
        Assert.AreEqual("https://x.sharepoint.com/dl?tempauth=1", item.DownloadUrl);

        using var folder = JsonDocument.Parse("""{"id":"01DEF","name":"Design","size":99,"folder":{"childCount":3},"lastModifiedDateTime":"2026-02-03T04:05:06Z"}""");
        var (dirEntry, dirItem) = GraphDrives.ParseItem(folder.RootElement);
        Assert.IsTrue(dirEntry.IsDirectory);
        Assert.AreEqual(0, dirEntry.Length, "folders report no size");
        Assert.IsTrue(dirItem.IsFolder);
        Assert.IsNull(dirEntry.Created);

        using var drive = JsonDocument.Parse("""{"id":"b!x","name":"Documents","driveType":"documentLibrary","quota":{"total":27487790694400,"remaining":27487700000000,"used":90694400}}""");
        var info = GraphDrives.ParseDrive(drive.RootElement);
        Assert.AreEqual("Documents", info.Name);
        Assert.AreEqual(27487790694400, info.QuotaTotal);
        Assert.AreEqual(27487700000000, info.QuotaRemaining);
    }
}

[TestClass]
public class SharePointOnlinePluginTests
{
    [TestMethod]
    public void DefaultsNeedOnlyASite()
    {
        var plugin = new SharePointPlugin();
        var settings = (SharePointSettings)plugin.CreateDefaultSettings();
        var messages = plugin.Validate(settings).ToList();
        Assert.AreEqual(1, messages.Count);
        Assert.AreEqual(nameof(SharePointSettings.SiteUrl), messages[0].Property);

        settings.SiteUrl = "https://contoso.sharepoint.com/sites/Team";
        Assert.AreEqual(0, plugin.Validate(settings).Count());
        Assert.AreEqual(EntraSignIn.DefaultClientId, settings.EffectiveClientId);
        Assert.AreEqual("organizations", settings.EffectiveTenant);
        Assert.AreEqual(SignInMethod.Browser, settings.Method);
    }

    [TestMethod]
    public void ValidationCatchesTheUsualMistakes()
    {
        var plugin = new SharePointPlugin();
        var settings = new SharePointSettings
        {
            SiteUrl = "https://contoso.sharepoint.com/sites/Team",
            Library = "Shared/Documents",
            Account = "not an email",
            Tenant = "two words",
            ClientId = "not-a-guid",
            Connections = 0,
            SpoolBelowMiB = -1,
            SignInMethod = SharePointSettings.AppDeviceCode,
        };
        var properties = plugin.Validate(settings).Select(m => m.Property).ToList();
        CollectionAssert.AreEquivalent(new[]
        {
            nameof(SharePointSettings.Library), nameof(SharePointSettings.Account), nameof(SharePointSettings.Tenant),
            nameof(SharePointSettings.ClientId), nameof(SharePointSettings.Connections), nameof(SharePointSettings.SpoolBelowMiB),
        }, properties);
        Assert.AreEqual(SignInMethod.DeviceCode, settings.Method);
    }
}

/// <summary>
/// Where the Graph suite (an app sign-in) runs: the sandbox when ANYFS_TEST_SPO_SITE names a site in a tenant the tests
/// may write to, with _LIBRARY (Documents), _ACCOUNT (the account the sign-in was made with) and _CACHE (the file
/// tests/fixtures/SharePointSignIn wrote; its default location when unset), and without a sign-in file the tests say to
/// run SharePointSignIn; otherwise the fake tenant (<see cref="SharePointFakes"/>). The fixture (anyfs-fixture/ in the
/// library) is made by the tests themselves through the source, so no other tool is needed on the SharePoint side.
/// </summary>
internal static class SpoTestServer
{
    public const string FixtureFolder = "anyfs-fixture";
    public static readonly byte[] ReadmeBytes = Encoding.UTF8.GetBytes("hello readme from sharepoint online\n");

    private static readonly Lazy<SharePointTarget> LazyTarget = new(MakeTarget);

    /// <summary>The sandbox, or the fake tenant.</summary>
    public static SharePointTarget Target => LazyTarget.Value;

    public static string? Account => Env("ANYFS_TEST_SPO_ACCOUNT");

    public static string CachePath => Env("ANYFS_TEST_SPO_CACHE") ?? SignInCacheFile.DefaultPath;

    /// <summary>
    /// The sign-in method is the device code: the tests sign in silently from the saved cache, and when that has
    /// lapsed a device-code attempt fails at once in a tenant that blocks it (or sits unanswered until the timeout),
    /// where the browser flow would open a browser on the test machine and wait for a person.
    /// </summary>
    private static SharePointTarget MakeTarget()
    {
        if (Env("ANYFS_TEST_SPO_SITE") is { } site)
        {
            return new SharePointTarget
            {
                Description = $"SharePoint Online at {site}, signed in as an app",
                Site = site,
                Library = Env("ANYFS_TEST_SPO_LIBRARY") ?? "Documents",
                Account = Account,
                SignInMethod = SharePointSettings.AppDeviceCode,
                ReadmeBytes = ReadmeBytes,
                CachePath = CachePath,
                LogVariable = "ANYFS_TEST_SPO_LOG",
                Missing = File.Exists(CachePath) ? null : $"no sign-in at {CachePath}: run tests/fixtures/SharePointSignIn once (dotnet run --project tests/fixtures/SharePointSignIn -- --account {Account})",
            };
        }

        if (!SharePointFakes.Enabled)
        {
            return new SharePointTarget { Description = "no SharePoint Online", Site = "", SignInMethod = SharePointSettings.AppDeviceCode, ReadmeBytes = ReadmeBytes, Missing = "no SharePoint Online test site: set ANYFS_TEST_SPO_SITE (and _LIBRARY, _ACCOUNT, _CACHE), or let the fakes stand in" };
        }

        var fake = SharePointFakes.Tenant;
        return new SharePointTarget
        {
            Description = "the fake tenant, signed in as an app",
            Site = fake.Origin + FakeSharePoint.TestSitePath,
            Account = FakeSharePoint.TenantUser,
            SignInMethod = SharePointSettings.AppDeviceCode,
            ReadmeBytes = ReadmeBytes,
            Fake = fake,
            LogVariable = "ANYFS_TEST_SPO_LOG",
        };
    }

    /// <summary>
    /// The test page (<see cref="SpsTestServer.PageTitle"/>) made through Graph's pages API when the site has none with
    /// its title: one text web part, published. Either suite may have made it; it reads the same. Returns its file name
    /// (AnyFS-test-page.aspx).
    /// </summary>
    public static async Task<string> EnsurePageAsync(SharePointSiteSource source)
    {
        var store = (GraphDocumentStore)source.Store;
        var none = CancellationToken.None;
        using (var pages = await store.Graph.GetJsonAsync($"sites/{store.SiteId}/pages?$select=id,name,title", "the site's pages", none))
        {
            foreach (var page in pages.RootElement.GetProperty("value").EnumerateArray())
            {
                if (page.TryGetProperty("title", out var title) && title.GetString() == SpsTestServer.PageTitle)
                {
                    return page.GetProperty("name").GetString()!;
                }
            }
        }

        var body = "{\"@odata.type\":\"#microsoft.graph.sitePage\",\"name\":\"AnyFS-test-page.aspx\",\"title\":" + Json.Quote(SpsTestServer.PageTitle)
            + ",\"pageLayout\":\"article\",\"showComments\":false,\"showRecommendedPages\":false,\"canvasLayout\":{\"horizontalSections\":[{\"layout\":\"oneColumn\",\"id\":\"1\",\"emphasis\":\"none\",\"columns\":[{\"id\":\"1\",\"width\":12,\"webparts\":["
            + "{\"@odata.type\":\"#microsoft.graph.textWebPart\",\"innerHtml\":" + Json.Quote(SpsTestServer.PageText) + "}]}]}]}}";
        using var created = await store.Graph.SendJsonAsync(HttpMethod.Post, $"sites/{store.SiteId}/pages", body, "a new page", none);
        var id = created.RootElement.GetProperty("id").GetString();
        using (await store.Graph.SendJsonAsync(HttpMethod.Post, $"sites/{store.SiteId}/pages/{id}/microsoft.graph.sitePage/publish", "{}", "the new page", none))
        {
        }

        return created.RootElement.GetProperty("name").GetString()!;
    }

    /// <summary>50,000 bytes that are the same every run (so a stale fixture is still right).</summary>
    public static byte[] MediumBytes()
    {
        var bytes = new byte[50_000];
        var state = 0x9E3779B9u;
        for (var i = 0; i < bytes.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            bytes[i] = (byte)state;
        }

        return bytes;
    }

    internal static async Task EnsureDirectoryAsync(ISource source, string path)
    {
        if (await source.StatAsync(path, CancellationToken.None) is null)
        {
            await source.CreateDirectoryAsync(path, CancellationToken.None);
        }
    }

    internal static async Task EnsureFileAsync(ISource source, string path, byte[] contents)
    {
        var existing = await source.StatAsync(path, CancellationToken.None);
        if (existing is { IsDirectory: false } && existing.Length == contents.Length)
        {
            return;
        }

        await using var file = await source.OpenAsync(path, VfsOpenMode.Create, CancellationToken.None);
        await file.WriteAsync(0, contents, CancellationToken.None);
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Appends every log line to a file, with a timestamp: for seeing where a live test got to.</summary>
    internal sealed class FileLogger(string path) : Microsoft.Extensions.Logging.ILogger
    {
        private static readonly object Gate = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {formatter(state, exception)}{(exception is null ? "" : " :: " + exception)}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(path, line);
            }
        }
    }
}

/// <summary>The SDK's contract through Graph (the sandbox, or the fake tenant), libraries at the top level.</summary>
[TestClass]
public class SharePointOnlineContractTests : SourceContractTests
{
    protected override Task<SourceContractSetup> SetUpAsync() => SpoTestServer.Target.ContractSetupAsync(pinned: false);
}

/// <summary>The same contract with the library pinned, so the fixture is at the source's root.</summary>
[TestClass]
public class SharePointOnlinePinnedLibraryContractTests : SourceContractTests
{
    protected override Task<SourceContractSetup> SetUpAsync() => SpoTestServer.Target.ContractSetupAsync(pinned: true);
}

[TestClass]
public class SharePointOnlineLiveTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static SharePointTarget Target => SpoTestServer.Target;

    [TestMethod]
    public async Task LibrariesAreTheTopLevelFolders()
    {
        Target.Require();
        await using var source = await Target.StartWithFixtureAsync(Target.Settings());
        var names = new List<string>();
        await foreach (var entry in source.ListAsync("", None))
        {
            Assert.IsTrue(entry.IsDirectory, $"{entry.Name} should be a folder");
            names.Add(entry.Name);
        }

        CollectionAssert.Contains(names, Target.Library);
        var library = await source.StatAsync(Target.Library, None);
        Assert.IsNotNull(library);
        Assert.IsTrue(library.IsDirectory);
        Assert.IsNotNull(await source.StatAsync(Target.Library.ToUpperInvariant(), None), "library names are case-insensitive");

        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.OpenAsync("new-library-as-file.txt", VfsOpenMode.Create, None));
        Assert.AreEqual(VfsError.InvalidPath, ex.Error, "a file can't be created beside the libraries");
    }

    /// <summary>The library's stamp (what the walks keep it by): there, and another once a file is added, through Graph's drive list.</summary>
    [TestMethod]
    public async Task TheLibrarysStampMovesWhenAFileIsAdded()
    {
        Target.Require();
        await using var source = await Target.StartWithFixtureAsync(Target.Settings());
        var library = (await source.Store.ListLibrariesAsync(null, None)).Single(l => l.Name == Target.Library);
        var before = await source.Store.LibraryStampAsync(library, None);
        Assert.IsNotNull(before, "the store stamps its libraries");

        // into another second: the list's time is whole seconds
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await SpoTestServer.EnsureFileAsync(source, VfsPath.Join(VfsPath.Join(Target.Library, Target.FixtureFolder), $"scratch/stamp-{Guid.NewGuid():N}.txt"), "stamp"u8.ToArray());
        var after = await source.Store.LibraryStampAsync(library, None);
        Assert.IsNotNull(after);
        Assert.AreNotEqual(before, after, "a file added moves it");
    }

    [TestMethod]
    public async Task ASubsiteIsAFolderWithItsOwnLibraries()
    {
        Target.Require();
        await using var source = await Target.StartWithFixtureAsync(Target.Settings());
        if (!await SpsTestServer.HasSubsiteAsync(source))
        {
            Assert.Inconclusive($"the site has no subsite {SpsTestServer.Subsite}: the REST suite makes it (ASubsiteIsAFolderWithItsOwnLibraries on a web session against this site), since Graph cannot");
        }

        await SpsTestServer.CheckSubsiteAsync(source);
    }

    [TestMethod]
    public async Task TheSitesPagesAreReadOnlyMarkdown()
    {
        Target.Require();
        string page;
        await using (var maker = await Target.StartWithFixtureAsync(Target.Settings()))
        {
            page = await SpoTestServer.EnsurePageAsync(maker);
        }

        // a source started now lists the page (one made since the start shows at the next walk)
        await using var source = await Target.StartWithFixtureAsync(Target.Settings());
        await SpsTestServer.CheckPageAsync(source, Target.Site, page);
    }

    [TestMethod]
    public async Task ARootSiteAmongSeveralShowsTheOtherSites()
    {
        Target.Require();
        await using (await Target.StartWithFixtureAsync(Target.Settings()))
        {
            // the fixture is in place in the test site
        }

        var site = Target.Site;
        var root = SpsTestServer.RootOf(site);
        using var context = Target.Context();
        await using var source = await Target.StartSitesAsync(Target.Settings(), context, root, site, $"{root}/sites/anyfs-no-such-site");
        await SpsTestServer.CheckRootAmongSitesAsync(source, root, site, Target.Library, SpoTestServer.FixtureFolder, SpoTestServer.ReadmeBytes);
        CollectionAssert.DoesNotContain(await SpsTestServer.NamesAsync(source, SharePointSource.SitesFolder), "anyfs-no-such-site", "the site not there is left out");

        Assert.IsTrue(SiteAddress.TryParse(site, out var address, out _));
        Assert.IsTrue((await source.Store.FindSiteAsync(address, None)).SiteCollection, "Graph says the test site is a site collection's own");
    }

    [TestMethod]
    public async Task TheTenantsRootSiteShowsItsOtherSites()
    {
        Target.Require();
        await using (await Target.StartWithFixtureAsync(Target.Settings()))
        {
            // the fixture is in place in the test site
        }

        var settings = Target.Settings();
        settings.SiteUrl = SpsTestServer.RootOf(settings.SiteUrl);
        await using var root = Target.CreateSource(settings);
        using var context = Target.Context();
        await root.InitialiseAsync(context, None);
        await SpsTestServer.CheckOtherSitesAsync(root, Target.Site, Target.Library, SpoTestServer.FixtureFolder, SpoTestServer.ReadmeBytes);
    }

    [TestMethod]
    public async Task RangedReadsAgreeWithTheWholeFileSpooledAndNot()
    {
        Target.Require();
        var expected = SpoTestServer.MediumBytes();
        foreach (var spoolBelowMiB in new[] { 8, 0 })
        {
            await using var source = await Target.StartWithFixtureAsync(Target.Settings(spoolBelowMiB: spoolBelowMiB));
            var path = VfsPath.Join(VfsPath.Join(Target.Library, SpoTestServer.FixtureFolder), "medium.bin");
            await using var file = await source.OpenAsync(path, VfsOpenMode.Read, None);
            Assert.AreEqual(expected.Length, file.Length);

            var whole = new byte[expected.Length];
            Assert.AreEqual(whole.Length, await file.ReadFullyAsync(0, whole, None));
            CollectionAssert.AreEqual(expected, whole, $"spoolBelowMiB={spoolBelowMiB}");

            var parts = await Task.WhenAll(new long[] { 0, 12_345, 25_000, 49_990 }.Select(async offset =>
            {
                var buffer = new byte[Math.Min(1000, expected.Length - (int)offset)];
                var n = await file.ReadFullyAsync(offset, buffer, None);
                return (offset, buffer, n);
            }));
            foreach (var (offset, buffer, n) in parts)
            {
                Assert.AreEqual(buffer.Length, n);
                CollectionAssert.AreEqual(expected[(int)offset..((int)offset + buffer.Length)], buffer, $"range at {offset}, spoolBelowMiB={spoolBelowMiB}");
            }
        }
    }

    [TestMethod]
    public async Task NamesSharePointRefusesAreRefusedHere()
    {
        Target.Require();
        await using var source = await Target.StartWithFixtureAsync(Target.Settings());
        var scratch = VfsPath.Join(VfsPath.Join(Target.Library, SpoTestServer.FixtureFolder), "scratch");
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.CreateDirectoryAsync(VfsPath.Join(scratch, "bad:name"), None));
        Assert.AreEqual(VfsError.InvalidPath, ex.Error);
        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.OpenAsync(VfsPath.Join(scratch, "~$lock.docx"), VfsOpenMode.Create, None));
        Assert.AreEqual(VfsError.InvalidPath, ex.Error);
    }

    /// <summary>Files at the edges of Graph's one-request upload and of an upload session's pieces (the last a single byte).</summary>
    [TestMethod]
    public Task FilesAtTheUploadLimitsRoundTrip() => Target.UploadsRoundTripAsync(
        GraphDrives.SimpleUploadLimit, GraphDrives.SimpleUploadLimit + 1, GraphDrives.UploadChunkSize, GraphDrives.UploadChunkSize + 1);

    [TestMethod]
    public async Task CaseInsensitiveLookupsAndLargeUpload()
    {
        Target.Require();
        await using var source = await Target.StartWithFixtureAsync(Target.Settings());
        var fixture = VfsPath.Join(Target.Library, SpoTestServer.FixtureFolder);
        var upper = await source.StatAsync(VfsPath.Join(fixture, "README.TXT"), None);
        Assert.IsNotNull(upper);
        Assert.AreEqual("readme.txt", upper.Name, "the entry keeps SharePoint's spelling");

        // 5 MiB: above the one-request limit, so it goes through an upload session
        var big = new byte[5 * 1024 * 1024];
        new Random(42).NextBytes(big);
        var path = VfsPath.Join(VfsPath.Join(fixture, "scratch"), $"big-{Guid.NewGuid():N}.bin");
        await using (var file = await source.OpenAsync(path, VfsOpenMode.CreateNew, None))
        {
            await file.WriteAsync(0, big, None);
        }

        var stat = await source.StatAsync(path, None);
        Assert.IsNotNull(stat);
        Assert.AreEqual(big.Length, stat.Length);
        await using (var file = await source.OpenAsync(path, VfsOpenMode.Read, None))
        {
            var tail = new byte[4096];
            Assert.AreEqual(tail.Length, await file.ReadFullyAsync(big.Length - tail.Length, tail, None));
            CollectionAssert.AreEqual(big[^4096..], tail);
        }

        await source.DeleteAsync(path, None);
        Assert.IsNull(await source.StatAsync(path, None));
    }

    [TestMethod]
    public async Task SpaceComesFromTheLibraryQuota()
    {
        Target.Require();
        await using var source = await Target.StartWithFixtureAsync(Target.Settings());
        var space = await source.GetSpaceAsync(Target.Library, None);
        Assert.IsNotNull(space);
        Assert.IsTrue(space.TotalBytes > 0 && space.FreeBytes <= space.TotalBytes);
    }
}
