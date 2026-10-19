using System.Net;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

[TestClass]
public class SharePointRestClientTests
{
    [TestMethod]
    public void EncodesPathsForDecodedUrlParameters()
    {
        Assert.AreEqual("%2Fsites%2Fanyfs%2FShared%20Documents%2Fa%20b.txt", SharePointRestClient.Encode("/sites/anyfs/Shared Documents/a b.txt"));
        Assert.AreEqual("%2Fx%2Fo%27%27brien.txt", SharePointRestClient.Encode("/x/o'brien.txt"), "a quote is doubled, as OData wants, then percent-encoded");
        Assert.AreEqual("web/GetFolderByServerRelativePath(decodedurl='%2Fsites%2Fanyfs%2FDocs')", SharePointRestClient.FolderApi("/sites/anyfs/Docs"));
        Assert.AreEqual("web/GetFileByServerRelativePath(decodedurl='%2Fsites%2Fanyfs%2FDocs%2Fr%C3%A9sum%C3%A9.pdf')", SharePointRestClient.FileApi("/sites/anyfs/Docs/résumé.pdf"));
    }

    [TestMethod]
    public void ReadsSharePointErrorBodies()
    {
        var (code, message) = SharePointRestClient.ErrorDetails("""{"odata.error":{"code":"-2147024894, System.IO.FileNotFoundException","message":{"lang":"en-US","value":"File Not Found."}}}""");
        Assert.AreEqual("-2147024894, System.IO.FileNotFoundException", code);
        Assert.AreEqual("File Not Found.", message);

        (code, message) = SharePointRestClient.ErrorDetails("""{"error":{"code":"-1, Microsoft.SharePoint.Client.InvalidClientQueryException","message":"The expression is not valid."}}""");
        Assert.AreEqual("-1, Microsoft.SharePoint.Client.InvalidClientQueryException", code);
        Assert.AreEqual("The expression is not valid.", message);

        (code, message) = SharePointRestClient.ErrorDetails("<html><head><title>401 UNAUTHORIZED</title></head></html>");
        Assert.AreEqual("", code);
        Assert.AreEqual("401 UNAUTHORIZED", message);
        Assert.AreEqual(("", ""), SharePointRestClient.ErrorDetails(""));
    }

    [TestMethod]
    public void MapsSharePointErrorsToVfsErrors()
    {
        static string Body(string code, string message) => "{\"odata.error\":{\"code\":\"" + code + "\",\"message\":{\"lang\":\"en-US\",\"value\":\"" + message + "\"}}}";
        Assert.AreEqual(VfsError.NotFound, SharePointRestClient.MapError(HttpStatusCode.InternalServerError, Body("-2147024894, System.IO.FileNotFoundException", "File Not Found."), "p").Error, "SharePoint says 500 for a missing file");
        Assert.AreEqual(VfsError.NotFound, SharePointRestClient.MapError(HttpStatusCode.NotFound, "", "p").Error);
        Assert.AreEqual(VfsError.NotFound, SharePointRestClient.MapError(HttpStatusCode.InternalServerError, Body("-2130575338, Microsoft.SharePoint.SPException", "Item does not exist. It may have been deleted by another user."), "p").Error);
        Assert.AreEqual(VfsError.AlreadyExists, SharePointRestClient.MapError(HttpStatusCode.InternalServerError, Body("-2130575257, Microsoft.SharePoint.SPException", "A file with the name Docs/a.txt already exists."), "p").Error);
        Assert.AreEqual(VfsError.AccessDenied, SharePointRestClient.MapError(HttpStatusCode.Forbidden, Body("-2147024891, System.UnauthorizedAccessException", "Access denied."), "p").Error);
        Assert.AreEqual(VfsError.AccessDenied, SharePointRestClient.MapError(HttpStatusCode.Unauthorized, "", "p").Error);
        Assert.AreEqual(VfsError.AccessDenied, SharePointRestClient.MapError(HttpStatusCode.InternalServerError, Body("-2130575245, Microsoft.SharePoint.SPFileLockException", "The file is checked out or locked for editing by someone."), "p").Error);
        Assert.AreEqual(VfsError.InvalidPath, SharePointRestClient.MapError(HttpStatusCode.BadRequest, Body("-2130575251, Microsoft.SharePoint.SPException", "The file or folder name contains characters that are not permitted."), "p").Error);
        Assert.AreEqual(VfsError.Unavailable, SharePointRestClient.MapError(HttpStatusCode.ServiceUnavailable, "", "p").Error);
        Assert.AreEqual(VfsError.Unavailable, SharePointRestClient.MapError(HttpStatusCode.InternalServerError, "", "p").Error);
        Assert.AreEqual(VfsError.IoError, SharePointRestClient.MapError(HttpStatusCode.InternalServerError, Body("-1, Microsoft.SharePoint.SPException", "Something else went wrong."), "p").Error);
    }

    [TestMethod]
    public void ParsesFilesAndFolders()
    {
        using var file = JsonDocument.Parse("""{"Name":"report.docx","ServerRelativeUrl":"/sites/anyfs/Shared Documents/report.docx","Length":"1234","TimeCreated":"2026-01-02T03:04:05Z","TimeLastModified":"2026-02-03T04:05:06Z","UniqueId":"7f1d0b3e-1111-2222-3333-444444444444","ETag":"\"{7F1D0B3E-1111-2222-3333-444444444444},3\""}""");
        var (entry, item) = RestDocumentStore.ParseFile(file.RootElement);
        Assert.AreEqual("report.docx", entry.Name);
        Assert.IsFalse(entry.IsDirectory);
        Assert.AreEqual(1234, entry.Length, "Length comes as a string");
        Assert.AreEqual(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero), entry.Modified);
        Assert.AreEqual("7f1d0b3e-1111-2222-3333-444444444444", item.Id);
        Assert.AreEqual(entry.ETag, item.Version);
        Assert.IsNull(item.DownloadUrl);

        using var folder = JsonDocument.Parse("""{"Name":"Design","ServerRelativeUrl":"/sites/anyfs/Shared Documents/Design","TimeCreated":"2026-01-02T03:04:05Z","TimeLastModified":"2026-02-03T04:05:06Z","UniqueId":"aaaa","ItemCount":3}""");
        var (dirEntry, dirItem) = RestDocumentStore.ParseFolder(folder.RootElement);
        Assert.IsTrue(dirEntry.IsDirectory);
        Assert.AreEqual(0, dirEntry.Length);
        Assert.IsTrue(dirItem.IsFolder);
        Assert.AreEqual(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), dirEntry.Created);
    }
}

[TestClass]
public class SharePointServerPluginTests
{
    [TestMethod]
    public void OnPremisesAddressesKeepTheirSchemeAndPort()
    {
        Assert.IsTrue(SiteAddress.TryParse("http://anyfs-sp-1/sites/anyfs", out var address, out _));
        Assert.AreEqual("http://anyfs-sp-1/sites/anyfs", address.Url);
        Assert.IsTrue(SiteAddress.TryParse("https://intranet.contoso.local:8443/dept/hr/SitePages/Home.aspx", out address, out _));
        Assert.AreEqual("https://intranet.contoso.local:8443/dept/hr", address.Url);
        Assert.IsTrue(SiteAddress.TryParse("http://192.168.0.92/sites/anyfs/", out address, out _));
        Assert.AreEqual("http://192.168.0.92/sites/anyfs", address.Url);
    }

    [TestMethod]
    public void CredentialsAreSplitTheWindowsWay()
    {
        var settings = new SharePointSettings { Username = @"ANYFS\sp_user", Password = "pw" };
        var credential = (NetworkCredential)settings.Credentials();
        Assert.AreEqual("sp_user", credential.UserName);
        Assert.AreEqual("ANYFS", credential.Domain);
        Assert.AreEqual("pw", credential.Password);

        settings.Username = "sp_user@anyfs.lab";
        credential = (NetworkCredential)settings.Credentials();
        Assert.AreEqual("sp_user@anyfs.lab", credential.UserName);
        Assert.AreEqual("", credential.Domain);

        settings.Username = null;
        Assert.IsTrue(settings.UsesCurrentLogon);
        Assert.AreSame(CredentialCache.DefaultNetworkCredentials, settings.Credentials());
    }

    [TestMethod]
    public void WindowsAccountAsYourselfAsksForNothing()
    {
        var plugin = new SharePointPlugin();
        var settings = new SharePointSettings { SiteUrl = "http://anyfs-sp-1/sites/anyfs", SignInMethod = SharePointSettings.WindowsCurrent, Username = @"ANYFS\left-over", Password = "left-over" };
        Assert.AreEqual(SharePointSignIn.Windows, settings.EffectiveSignIn);
        Assert.IsTrue(settings.UsesCurrentLogon);
        Assert.AreSame(CredentialCache.DefaultNetworkCredentials, settings.Credentials(), "the Windows sign-in AnyFS runs as, whatever is left in the boxes");
        Assert.IsFalse(plugin.IsVisible(settings, nameof(SharePointSettings.Username)));
        Assert.IsFalse(plugin.IsVisible(settings, nameof(SharePointSettings.Password)));
        Assert.IsTrue(plugin.IsVisible(settings, nameof(SharePointSettings.TrustAnyCertificate)));
        Assert.AreEqual(OperatingSystem.IsWindows() ? 0 : 1, plugin.Validate(settings).Count(), "the hidden user name and password are no error; elsewhere than Windows the choice is");
        StringAssert.Contains(settings.WindowsAccount, Environment.UserName);

        settings.SignInMethod = SharePointSettings.Windows;
        Assert.IsFalse(settings.UsesCurrentLogon, "Windows account takes the user name and password");
        Assert.IsTrue(plugin.IsVisible(settings, nameof(SharePointSettings.Username)));
        Assert.AreEqual(@"ANYFS\left-over", settings.WindowsAccount);

        settings.SignInMethod = SharePointSettings.WebSession;
        StringAssert.Contains(plugin.Validate(settings).Single().Message, SharePointSettings.WindowsCurrent, "the refusal names the choice");
    }

    [TestMethod]
    public void ValidationCatchesTheUsualMistakes()
    {
        var plugin = new SharePointPlugin();
        var settings = (SharePointSettings)plugin.CreateDefaultSettings();
        var properties = plugin.Validate(settings).Select(m => m.Property).ToList();
        CollectionAssert.Contains(properties, nameof(SharePointSettings.SiteUrl));

        settings.SiteUrl = "http://anyfs-sp-1/sites/anyfs";
        settings.Username = @"ANYFS\sp_user";
        settings.Password = "pw";
        Assert.AreEqual(0, plugin.Validate(settings).Count());

        settings.Username = "two words";
        settings.Library = "a/b";
        settings.Connections = 99;
        properties = plugin.Validate(settings).Select(m => m.Property).ToList();
        CollectionAssert.AreEquivalent(new[] { nameof(SharePointSettings.Username), nameof(SharePointSettings.Library), nameof(SharePointSettings.Connections) }, properties);

        settings.Username = null;
        settings.Library = null;
        settings.Connections = 4;
        properties = plugin.Validate(settings).Select(m => m.Property).ToList();
        CollectionAssert.Contains(properties, nameof(SharePointSettings.Username), "a password without a user name");
    }
}

/// <summary>
/// Where the REST live tests run: ANYFS_TEST_SPS_SITE (http://192.168.0.92/sites/anyfs in the lab), _USER
/// (ANYFS\sp_user), _PASSWORD and _LIBRARY (Documents). Without a site the live tests are inconclusive. The
/// fixture tree is made by the tests through the source, as for SharePoint Online.
///
/// With ANYFS_TEST_SPS_SIGNIN=web the same tests run against SharePoint Online on a web session (the browser
/// sign-in that needs no app registration): the site is a Microsoft 365 one, no user or password, and
/// ANYFS_SHAREPOINT_PROFILES names where the signed-in browser profiles are kept, so a profile signed in once
/// serves every run without a window.
/// </summary>
internal static class SpsTestServer
{
    public static readonly byte[] ReadmeBytes = Encoding.UTF8.GetBytes("hello readme from sharepoint server\n");

    /// <summary>
    /// On SharePoint Online the Graph suite keeps its own anyfs-fixture in the same library, with a readme of the
    /// same length ("online" and "server"), which the length-only fixture check would take for this one's.
    /// </summary>
    public static string FixtureFolder => WebSession ? "anyfs-fixture-rest" : "anyfs-fixture";

    public static string? Site => Env("ANYFS_TEST_SPS_SITE");

    public static string? User => Env("ANYFS_TEST_SPS_USER");

    public static string? Password => Env("ANYFS_TEST_SPS_PASSWORD");

    public static string Library => Env("ANYFS_TEST_SPS_LIBRARY") ?? "Documents";

    public static bool WebSession => string.Equals(Env("ANYFS_TEST_SPS_SIGNIN"), "web", StringComparison.OrdinalIgnoreCase);

    public static void Require()
    {
        if (Site is null)
        {
            Assert.Inconclusive("no SharePoint REST test site: set ANYFS_TEST_SPS_SITE (and _USER, _PASSWORD, _LIBRARY; or _SIGNIN=web for a Microsoft 365 site)");
        }
    }

    public static SharePointSettings Settings(string? library = null, int spoolBelowMiB = 8) => new()
    {
        SiteUrl = Site!,
        Library = library,
        SignInMethod = WebSession ? SharePointSettings.WebSession : SharePointSettings.Automatic,
        Username = WebSession ? null : User,
        Password = WebSession ? null : Password,
        SpoolBelowMiB = spoolBelowMiB,
        TrustAnyCertificate = true,
    };

    /// <summary>The subsite of the test site that the subsite tests work in (a team site with its Documents library).</summary>
    public const string Subsite = "anyfs-subsite";

    /// <summary>
    /// Makes <see cref="Subsite"/> through the REST API when the site has none by that name yet, as the fixture folders
    /// are made: Graph cannot make a subsite, so on SharePoint Online this suite's web-session run makes it for both.
    /// </summary>
    public static async Task EnsureSubsiteAsync(SharePointSiteSource source)
    {
        var store = (RestDocumentStore)source.Store;
        if ((await store.ListSubsitesAsync(null, CancellationToken.None)).Any(w => string.Equals(w.Name, Subsite, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var body = "{\"parameters\":{\"Url\":\"" + Subsite + "\",\"Title\":\"AnyFS subsite\",\"Description\":\"For AnyFS's live tests\",\"Language\":1033,\"WebTemplate\":\"STS#3\",\"UseUniquePermissions\":false}}";
        try
        {
            using var _ = await store.Rest.PostJsonAsync("web/webinfos/add", body, Subsite, CancellationToken.None);
        }
        catch (VfsException ex) when (ex.Message.Contains("subsites are not available", StringComparison.OrdinalIgnoreCase))
        {
            // a tenant made since 2024 or so refuses new subsites unless its administrator allows them (the sandbox does)
            Assert.Inconclusive($"SharePoint will not make the test subsite here: {ex.Message}");
        }
    }

    /// <summary>Whether the site's Subsites folder holds <see cref="Subsite"/>.</summary>
    public static async Task<bool> HasSubsiteAsync(ISource source)
        => (await NamesAsync(source, "")).Contains(SharePointSource.SubsitesFolder) && (await NamesAsync(source, SharePointSource.SubsitesFolder)).Contains(Subsite);

    /// <summary>The subsite in the Subsites folder beside the libraries, its own Documents inside it, a file written there, read back and deleted.</summary>
    public static async Task CheckSubsiteAsync(ISource source)
    {
        var none = CancellationToken.None;
        var subsite = VfsPath.Join(SharePointSource.SubsitesFolder, Subsite);
        CollectionAssert.Contains(await NamesAsync(source, ""), SharePointSource.SubsitesFolder);
        CollectionAssert.Contains(await NamesAsync(source, SharePointSource.SubsitesFolder), Subsite);
        CollectionAssert.Contains(await NamesAsync(source, subsite), "Documents");
        var path = VfsPath.Join(subsite, $"Documents/anyfs-subsite-{Guid.NewGuid():N}.txt");
        await using (var file = await source.OpenAsync(path, VfsOpenMode.CreateNew, none))
        {
            await file.WriteAsync(0, ReadmeBytes, none);
        }

        Assert.AreEqual(ReadmeBytes.Length, (await source.StatAsync(path, none))?.Length);
        await using (var file = await source.OpenAsync(path, VfsOpenMode.Read, none))
        {
            var back = new byte[ReadmeBytes.Length];
            Assert.AreEqual(back.Length, await file.ReadFullyAsync(0, back, none));
            CollectionAssert.AreEqual(ReadmeBytes, back);
        }

        await source.DeleteAsync(path, none);
        Assert.IsNull(await source.StatAsync(path, none));
    }

    /// <summary>The tenant's root site of a site's address: its scheme and host.</summary>
    public static string RootOf(string siteUrl)
    {
        Assert.IsTrue(SiteAddress.TryParse(siteUrl, out var site, out var reason), reason);
        return $"{site.Scheme}://{site.Host}";
    }

    /// <summary>
    /// A source on the tenant's root site shows the test site in its Sites folder, and the fixture's readme reads through
    /// it (a request to another site collection's own address); Inconclusive where search finds no other site (a farm
    /// without a search service).
    /// </summary>
    public static async Task CheckOtherSitesAsync(ISource root, string siteUrl, string library, string fixtureFolder, byte[] readme)
    {
        if (!(await NamesAsync(root, "")).Contains(SharePointSource.SitesFolder))
        {
            Assert.Inconclusive($"search finds no other site from {RootOf(siteUrl)} (no search service, or nothing indexed yet)");
        }

        Assert.IsTrue(SiteAddress.TryParse(siteUrl, out var site, out _));
        var name = site.ServerRelativePath[(site.ServerRelativePath.LastIndexOf('/') + 1)..];
        CollectionAssert.Contains(await NamesAsync(root, SharePointSource.SitesFolder), name);
        var path = $"{SharePointSource.SitesFolder}/{name}/{library}/{fixtureFolder}/readme.txt";
        await using var file = await root.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var back = new byte[readme.Length];
        Assert.AreEqual(back.Length, await file.ReadFullyAsync(0, back, CancellationToken.None));
        CollectionAssert.AreEqual(readme, back);
    }

    public static async Task<List<string>> NamesAsync(ISource source, string path)
    {
        var names = new List<string>();
        await foreach (var entry in source.ListAsync(path, CancellationToken.None))
        {
            names.Add(entry.Name);
        }

        return names;
    }

    public static async Task<SharePointSiteSource> StartWithFixtureAsync(SharePointSettings settings)
    {
        var source = (SharePointSiteSource)new SharePointPlugin().CreateSource(settings);
        var logPath = Environment.GetEnvironmentVariable("ANYFS_TEST_SPS_LOG");
        var context = new TestSourceContext("sharepoint", string.IsNullOrWhiteSpace(logPath) ? null : new FileLogger(logPath));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await source.InitialiseAsync(context, timeout.Token);
        var root = settings.Library is null ? Library : "";
        var fixture = VfsPath.Join(root, FixtureFolder);
        await EnsureDirectoryAsync(source, fixture);
        await EnsureDirectoryAsync(source, VfsPath.Join(fixture, "docs"));
        await EnsureDirectoryAsync(source, VfsPath.Join(fixture, "scratch"));
        await EnsureFileAsync(source, VfsPath.Join(fixture, "readme.txt"), ReadmeBytes);
        await EnsureFileAsync(source, VfsPath.Join(fixture, "docs/a.txt"), "aaa"u8.ToArray());
        await EnsureFileAsync(source, VfsPath.Join(fixture, "medium.bin"), SpoTestServer.MediumBytes());
        return source;
    }

    private static async Task EnsureDirectoryAsync(ISource source, string path)
    {
        if (await source.StatAsync(path, CancellationToken.None) is null)
        {
            await source.CreateDirectoryAsync(path, CancellationToken.None);
        }
    }

    private static async Task EnsureFileAsync(ISource source, string path, byte[] contents)
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

    private sealed class FileLogger(string path) : Microsoft.Extensions.Logging.ILogger
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

/// <summary>The SDK's contract against the lab farm, libraries at the top level.</summary>
[TestClass]
public class SharePointServerContractTests : SourceContractTests
{
    protected override async Task<SourceContractSetup> SetUpAsync()
    {
        SpsTestServer.Require();
        var source = await SpsTestServer.StartWithFixtureAsync(SpsTestServer.Settings());
        var fixture = VfsPath.Join(SpsTestServer.Library, SpsTestServer.FixtureFolder);
        return new SourceContractSetup(source, fixture, VfsPath.Join(fixture, "readme.txt"), SpsTestServer.ReadmeBytes, WritableDirectory: VfsPath.Join(fixture, "scratch"));
    }
}

/// <summary>The same contract with the library pinned and nothing spooled, so reads go through ranges.</summary>
[TestClass]
public class SharePointServerPinnedLibraryContractTests : SourceContractTests
{
    protected override async Task<SourceContractSetup> SetUpAsync()
    {
        SpsTestServer.Require();
        var source = await SpsTestServer.StartWithFixtureAsync(SpsTestServer.Settings(library: SpsTestServer.Library, spoolBelowMiB: 0));
        return new SourceContractSetup(source, SpsTestServer.FixtureFolder, VfsPath.Join(SpsTestServer.FixtureFolder, "readme.txt"), SpsTestServer.ReadmeBytes, WritableDirectory: VfsPath.Join(SpsTestServer.FixtureFolder, "scratch"));
    }
}

[TestClass]
public class SharePointServerLiveTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [TestMethod]
    public async Task LibrariesAreTheTopLevelFoldersAndFormsIsHidden()
    {
        SpsTestServer.Require();
        await using var source = await SpsTestServer.StartWithFixtureAsync(SpsTestServer.Settings());
        var names = new List<string>();
        await foreach (var entry in source.ListAsync("", None))
        {
            Assert.IsTrue(entry.IsDirectory, $"{entry.Name} should be a folder");
            names.Add(entry.Name);
        }

        CollectionAssert.Contains(names, SpsTestServer.Library);
        var top = new List<string>();
        await foreach (var entry in source.ListAsync(SpsTestServer.Library, None))
        {
            top.Add(entry.Name);
        }

        CollectionAssert.DoesNotContain(top, "Forms", "the library's own Forms folder stays hidden");
        CollectionAssert.Contains(top, SpsTestServer.FixtureFolder);
    }

    [TestMethod]
    public async Task ASubsiteIsAFolderWithItsOwnLibraries()
    {
        SpsTestServer.Require();
        await using var source = await SpsTestServer.StartWithFixtureAsync(SpsTestServer.Settings());
        await SpsTestServer.EnsureSubsiteAsync(source);
        await SpsTestServer.CheckSubsiteAsync(source);
    }

    [TestMethod]
    public async Task TheTenantsRootSiteShowsItsOtherSites()
    {
        SpsTestServer.Require();
        await using (await SpsTestServer.StartWithFixtureAsync(SpsTestServer.Settings()))
        {
            // the fixture is in place in the test site
        }

        var settings = SpsTestServer.Settings();
        settings.SiteUrl = SpsTestServer.RootOf(settings.SiteUrl);
        await using var root = (SharePointSiteSource)new SharePointPlugin().CreateSource(settings);
        using var context = new TestSourceContext("sharepoint");
        await root.InitialiseAsync(context, None);
        await SpsTestServer.CheckOtherSitesAsync(root, SpsTestServer.Site!, SpsTestServer.Library, SpsTestServer.FixtureFolder, SpsTestServer.ReadmeBytes);
    }

    [TestMethod]
    public async Task RangedReadsAgreeWithTheWholeFileSpooledAndNot()
    {
        SpsTestServer.Require();
        var expected = SpoTestServer.MediumBytes();
        foreach (var spoolBelowMiB in new[] { 8, 0 })
        {
            await using var source = await SpsTestServer.StartWithFixtureAsync(SpsTestServer.Settings(spoolBelowMiB: spoolBelowMiB));
            var path = VfsPath.Join(VfsPath.Join(SpsTestServer.Library, SpsTestServer.FixtureFolder), "medium.bin");
            await using var file = await source.OpenAsync(path, VfsOpenMode.Read, None);
            Assert.AreEqual(expected.Length, file.Length);
            var whole = new byte[expected.Length];
            Assert.AreEqual(whole.Length, await file.ReadFullyAsync(0, whole, None));
            CollectionAssert.AreEqual(expected, whole, $"spoolBelowMiB={spoolBelowMiB}");
            var tail = new byte[1000];
            Assert.AreEqual(tail.Length, await file.ReadFullyAsync(expected.Length - 1000, tail, None));
            CollectionAssert.AreEqual(expected[^1000..], tail, $"tail range, spoolBelowMiB={spoolBelowMiB}");
        }
    }

    [TestMethod]
    public async Task LargeUploadGoesInPieces()
    {
        SpsTestServer.Require();
        await using var source = await SpsTestServer.StartWithFixtureAsync(SpsTestServer.Settings());
        var fixture = VfsPath.Join(SpsTestServer.Library, SpsTestServer.FixtureFolder);
        var big = new byte[12 * 1024 * 1024];
        new Random(7).NextBytes(big);
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
            var middle = new byte[4096];
            Assert.AreEqual(middle.Length, await file.ReadFullyAsync(11 * 1024 * 1024 - 100, middle, None));
            CollectionAssert.AreEqual(big[(11 * 1024 * 1024 - 100)..(11 * 1024 * 1024 - 100 + 4096)], middle);
        }

        await source.DeleteAsync(path, None);
        Assert.IsNull(await source.StatAsync(path, None));
    }
}
