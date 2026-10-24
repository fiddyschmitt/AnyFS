using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
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
    public void ARefusalSaysWhatTheServerAsksFor()
    {
        using var windows = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        windows.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Negotiate"));
        windows.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("NTLM"));
        Assert.AreEqual("Negotiate, NTLM", SharePointRestClient.Challenge(windows));
        StringAssert.Contains(SharePointRestClient.MapError(HttpStatusCode.Unauthorized, "", "p", "Negotiate, NTLM").Message, "(401; it asks for Negotiate, NTLM)");

        using var forms = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        forms.Headers.TryAddWithoutValidation("X-Forms_Based_Auth_Required", "https://sp.contoso.local/_login/default.aspx");
        StringAssert.Contains(SharePointRestClient.Challenge(forms)!, "browser");

        using var bounced = new HttpResponseMessage(HttpStatusCode.Found);
        bounced.Headers.Location = new Uri("https://adfs.contoso.local/adfs/ls/?wa=wsignin1.0");
        var challenge = SharePointRestClient.Challenge(bounced);
        Assert.AreEqual("https://adfs.contoso.local/adfs/ls/", challenge, "where, without the query");
        var ex = SharePointRestClient.MapError(HttpStatusCode.Found, "", "p", challenge);
        Assert.AreEqual(VfsError.AccessDenied, ex.Error);
        StringAssert.Contains(ex.Message, "adfs.contoso.local");

        using var ok = new HttpResponseMessage(HttpStatusCode.OK);
        Assert.IsNull(SharePointRestClient.Challenge(ok));
    }

    [TestMethod]
    public void ADenialIsNotARefusedSignIn()
    {
        // as the lab farm answers: an account signed in at a site it may not open (a 401 with SharePoint's own error,
        // for a Windows sign-in), and a wrong password (a 401 with no body)
        const string Body = """{"odata.error":{"code":"-2147024891, System.UnauthorizedAccessException","message":{"lang":"en-US","value":"Access denied. You do not have permission to perform this action or access this resource."}}}""";
        var denied = SharePointRestClient.MapError(HttpStatusCode.Unauthorized, Body, "http://sp/", "NTLM");
        Assert.AreEqual(VfsError.AccessDenied, denied.Error);
        Assert.IsTrue(SharePointRestClient.IsDenial(denied));
        Assert.AreEqual("access to 'http://sp/' was denied: Access denied. You do not have permission to perform this action or access this resource.", denied.Message);
        Assert.IsTrue(SharePointRestClient.IsDenial(SharePointRestClient.MapError(HttpStatusCode.Forbidden, Body, "p")), "SharePoint Online says 403 for the same");

        var refused = SharePointRestClient.MapError(HttpStatusCode.Unauthorized, "", "http://sp/", "NTLM");
        Assert.AreEqual(VfsError.AccessDenied, refused.Error);
        Assert.IsFalse(SharePointRestClient.IsDenial(refused));
        StringAssert.Contains(refused.Message, "refused the sign-in (401; it asks for NTLM)");

        var atRoot = SharePointPlugin.Denied(@"ANYFS\sp_user", denied, rootSite: true);
        StringAssert.StartsWith(atRoot, @"Signed in as ANYFS\sp_user, but access to 'http://sp/' was denied: Access denied. You do not have permission to perform this action or access this resource. ");
        StringAssert.Contains(atRoot, "give the address of a site this account may open instead");
        Assert.AreEqual(@"Signed in as ANYFS\sp_user, but access to 'http://sp/' was denied: Access denied. You do not have permission to perform this action or access this resource.", SharePointPlugin.Denied(@"ANYFS\sp_user", denied, rootSite: false));
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
/// Where the REST suite runs, and what its tests share. Two targets, each the lab's or the sandbox's when the
/// environment names it and the fake standing in otherwise (<see cref="SharePointFakes"/>):
/// <list type="bullet">
/// <item><see cref="Farm"/>, a farm with a Windows sign-in: ANYFS_TEST_SPS_SITE (http://192.168.0.92/sites/anyfs in the
/// lab), _USER (ANYFS\sp_user), _PASSWORD, _LIBRARY (Documents), _SITE2 (the other farm's test site);</item>
/// <item><see cref="OnlineRest"/>, SharePoint Online on a web session (the browser sign-in that needs no app
/// registration): the same variables with ANYFS_TEST_SPS_SIGNIN=web and a Microsoft 365 site, no user or password;
/// ANYFS_SHAREPOINT_PROFILES names where the signed-in browser profiles are kept, so a profile signed in once serves
/// every run without a window.</item>
/// </list>
/// The fixture tree is made by the tests through the source, as for SharePoint Online.
/// </summary>
internal static class SpsTestServer
{
    public static readonly byte[] ReadmeBytes = Encoding.UTF8.GetBytes("hello readme from sharepoint server\n");

    private static readonly Lazy<SharePointTarget> LazyFarm = new(FarmTarget);
    private static readonly Lazy<SharePointTarget> LazyOnline = new(OnlineTarget);

    /// <summary>The farm: the lab's, or the fake farm.</summary>
    public static SharePointTarget Farm => LazyFarm.Value;

    /// <summary>SharePoint Online on a web session: the sandbox, or the fake tenant.</summary>
    public static SharePointTarget OnlineRest => LazyOnline.Value;

    private static bool LabWebSession => string.Equals(Env("ANYFS_TEST_SPS_SIGNIN"), "web", StringComparison.OrdinalIgnoreCase);

    private static SharePointTarget FarmTarget()
    {
        if (Env("ANYFS_TEST_SPS_SITE") is { } site && !LabWebSession)
        {
            return new SharePointTarget
            {
                Description = $"the farm at {site}",
                Site = site,
                User = Env("ANYFS_TEST_SPS_USER"),
                Password = Env("ANYFS_TEST_SPS_PASSWORD"),
                Library = Env("ANYFS_TEST_SPS_LIBRARY") ?? "Documents",
                SignInMethod = SharePointSettings.Automatic,
                ReadmeBytes = ReadmeBytes,
                OtherServerSite = Env("ANYFS_TEST_SPS_SITE2"),
                LogVariable = "ANYFS_TEST_SPS_LOG",
            };
        }

        if (!SharePointFakes.Enabled)
        {
            return new SharePointTarget { Description = "no farm", Site = "", SignInMethod = SharePointSettings.Automatic, ReadmeBytes = ReadmeBytes, Missing = "no SharePoint REST test site: set ANYFS_TEST_SPS_SITE (and _USER, _PASSWORD, _LIBRARY), or let the fakes stand in" };
        }

        var fake = SharePointFakes.Farm;
        return new SharePointTarget
        {
            Description = "the fake farm",
            Site = fake.Origin + FakeSharePoint.TestSitePath,
            User = FakeSharePoint.FarmUser,
            Password = FakeSharePoint.FakePassword,
            SignInMethod = SharePointSettings.Automatic,
            ReadmeBytes = ReadmeBytes,
            Fake = fake,
            OtherServerSite = SharePointFakes.OtherFarm.Origin + FakeSharePoint.TestSitePath,
            LogVariable = "ANYFS_TEST_SPS_LOG",
        };
    }

    /// <summary>
    /// On SharePoint Online the Graph suite keeps its own anyfs-fixture in the same library, with a readme of the same
    /// length ("online" and "server"), which the length-only fixture check would take for this one's: this suite's is
    /// anyfs-fixture-rest.
    /// </summary>
    private static SharePointTarget OnlineTarget()
    {
        if (Env("ANYFS_TEST_SPS_SITE") is { } site && LabWebSession)
        {
            return new SharePointTarget
            {
                Description = $"SharePoint Online at {site}, on a web session",
                Site = site,
                Library = Env("ANYFS_TEST_SPS_LIBRARY") ?? "Documents",
                SignInMethod = SharePointSettings.WebSession,
                FixtureFolder = "anyfs-fixture-rest",
                ReadmeBytes = ReadmeBytes,
                LogVariable = "ANYFS_TEST_SPS_LOG",
            };
        }

        if (!SharePointFakes.Enabled)
        {
            return new SharePointTarget { Description = "no SharePoint Online", Site = "", SignInMethod = SharePointSettings.WebSession, ReadmeBytes = ReadmeBytes, Missing = "no SharePoint Online site for the web session: set ANYFS_TEST_SPS_SITE with ANYFS_TEST_SPS_SIGNIN=web, or let the fakes stand in" };
        }

        var fake = SharePointFakes.Tenant;
        return new SharePointTarget
        {
            Description = "the fake tenant, on a web session",
            Site = fake.Origin + FakeSharePoint.TestSitePath,
            Account = FakeSharePoint.TenantUser,
            SignInMethod = SharePointSettings.WebSession,
            FixtureFolder = "anyfs-fixture-rest",
            ReadmeBytes = ReadmeBytes,
            Fake = fake,
            LogVariable = "ANYFS_TEST_SPS_LOG",
        };
    }

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

    /// <summary>The site's pages library, as it shows.</summary>
    public const string PagesLibrary = "Site Pages";

    /// <summary>The test page's title; its file name SharePoint makes from it (AnyFS-test-page.aspx).</summary>
    public const string PageTitle = "AnyFS test page";

    /// <summary>The test page's one text web part, and the Markdown the page reads as: its address first.</summary>
    public const string PageText = "<h2>Heading two</h2><p>Hello <strong>world</strong> from <a href=\"https://example.com/x\">AnyFS</a>.</p><ul><li>one</li><li>two</li></ul>";

    public static string PageMarkdown(string pageUrl) => $"{pageUrl}\n\n# {PageTitle}\n\n## Heading two\n\nHello **world** from [AnyFS](https://example.com/x).\n\n- one\n- two\n";

    /// <summary>The classic page put in the test site's own folder (<see cref="EnsureClassicPageAsync"/>), and the Markdown file it shows as.</summary>
    public const string ClassicPage = "anyfs-classic-page.aspx";

    public const string ClassicPageShown = "anyfs-classic-page.aspx.md";

    /// <summary>
    /// Puts a classic page in the test site's own folder (not a library), as a classic site keeps its default.aspx: the
    /// site's master page around a title and a main area of plain HTML, no server code. SharePoint Online refuses it on a
    /// site that does not allow custom script (its modern sites).
    /// </summary>
    public static async Task EnsureClassicPageAsync(SharePointSiteSource source)
    {
        var rest = ((RestDocumentStore)source.Store).Rest;
        var page = Encoding.UTF8.GetBytes("""
            <%@ Page language="C#" MasterPageFile="~masterurl/default.master" Inherits="Microsoft.SharePoint.WebPartPages.WebPartPage,Microsoft.SharePoint,Version=16.0.0.0,Culture=neutral,PublicKeyToken=71e9bce111e9429c" %>
            <asp:Content ContentPlaceHolderId="PlaceHolderPageTitleInTitleArea" runat="server">AnyFS classic page</asp:Content>
            <asp:Content ContentPlaceHolderId="PlaceHolderMain" runat="server"><div><h2>A classic page</h2><p>Text in the page's main area, with a <a href="Lists/anyfs-nowhere">link</a>.</p></div></asp:Content>
            """);
        using (await rest.PostBinaryAsync($"web/RootFolder/Files/add(url='{ClassicPage}',overwrite=true)", () => new MemoryStream(page), page.Length, ClassicPage, CancellationToken.None))
        {
        }
    }

    /// <summary>
    /// The test page in the site's pages, made through the site pages API when there is none with its title: created,
    /// saved with a canvas of one text web part (the API takes the canvas as JSON), published. The source shows pages
    /// read-only, so the test makes it beside it, as it makes the subsite. Returns its file name (AnyFS-test-page.aspx).
    /// </summary>
    public static async Task<string> EnsurePageAsync(SharePointSiteSource source)
    {
        var rest = ((RestDocumentStore)source.Store).Rest;
        var none = CancellationToken.None;
        using (var pages = await rest.GetJsonAsync("sitepages/pages?$select=Id,Title,FileName", "the site's pages", none))
        {
            foreach (var page in pages.RootElement.GetProperty("value").EnumerateArray())
            {
                if (page.GetProperty("Title").GetString() == PageTitle)
                {
                    return page.GetProperty("FileName").GetString()!;
                }
            }
        }

        int id;
        using (var created = await rest.PostJsonAsync("sitepages/pages", "{\"PageLayoutType\":\"Article\"}", "a new page", none))
        {
            id = created.RootElement.GetProperty("Id").GetInt32();
        }

        var canvas = "[{\"controlType\":4,\"displayMode\":2,\"id\":\"1212fc8d-dd6b-408a-8d5d-9f1cc787efbb\",\"position\":{\"zoneIndex\":1,\"sectionIndex\":1,\"controlIndex\":1,\"sectionFactor\":12,\"layoutIndex\":1},\"emphasis\":{},\"innerHTML\":" + Json.Quote(PageText) + "}]";
        using (await rest.PostJsonAsync($"sitepages/pages({id})/checkoutpage", null, "the new page", none))
        using (await rest.PostJsonAsync($"sitepages/pages({id})/savepage", "{\"Title\":" + Json.Quote(PageTitle) + ",\"CanvasContent1\":" + Json.Quote(canvas) + "}", "the new page", none))
        using (await rest.PostJsonAsync($"sitepages/pages({id})/publish", null, "the new page", none))
        using (var saved = await rest.GetJsonAsync($"sitepages/pages({id})?$select=FileName", "the new page", none))
        {
            return saved.RootElement.GetProperty("FileName").GetString()!;
        }
    }

    /// <summary>
    /// The test page (<paramref name="fileName"/>, in the Site Pages of <paramref name="siteUrl"/>) shows as its name with
    /// .md after it and reads as its Markdown, its address first; it is read-only, and only pages show there.
    /// </summary>
    public static async Task CheckPageAsync(ISource source, string siteUrl, string fileName)
    {
        var none = CancellationToken.None;
        var names = await NamesAsync(source, PagesLibrary);
        var page = fileName + ".md";
        CollectionAssert.Contains(names, page, string.Join(", ", names));
        Assert.IsFalse(names.Any(n => n.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)), string.Join(", ", names));
        var path = VfsPath.Join(PagesLibrary, page);
        var entry = await source.StatAsync(path, none);
        Assert.IsNotNull(entry);
        Assert.IsTrue(entry.Attributes.HasFlag(VfsAttributes.ReadOnly));
        await using (var file = await source.OpenAsync(path, VfsOpenMode.Read, none))
        {
            // the whole of the size a listing gives, and that is the whole Markdown
            var back = new byte[entry.Length];
            Assert.AreEqual(back.Length, await file.ReadFullyAsync(0, back, none));
            CheckMarkdown(PageMarkdown($"{siteUrl.TrimEnd('/')}/SitePages/{fileName}"), Encoding.UTF8.GetString(back));
        }

        var ex = await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync(path, VfsOpenMode.ReadWrite, none));
        Assert.AreEqual(VfsError.ReadOnly, ex.Error);
    }

    /// <summary>A page's Markdown as expected: its address on the first line (as the server spells it, so case aside), the rest exactly.</summary>
    public static void CheckMarkdown(string expected, string actual)
    {
        var expectedLines = expected.Split('\n', 2);
        var actualLines = actual.Split('\n', 2);
        Assert.AreEqual(expectedLines[0], actualLines[0], true, "the page's address on the first line: " + actual);
        Assert.AreEqual(expectedLines.ElementAtOrDefault(1), actualLines.ElementAtOrDefault(1), actual);
    }

    /// <summary>The name a site shows as among several: the last part of its address.</summary>
    public static string LastName(string siteUrl) => new Uri(siteUrl).AbsolutePath.TrimEnd('/').Split('/')[^1];

    /// <summary>
    /// A source of the tenant's root site and the test site: the root site is the source's own (no folder named after the
    /// server), and the test site, a site collection, is in its Sites folder, where the fixture's readme reads.
    /// </summary>
    public static async Task CheckRootAmongSitesAsync(ISource source, string root, string siteUrl, string library, string fixtureFolder, byte[] readme)
    {
        var names = await NamesAsync(source, "");
        CollectionAssert.DoesNotContain(names, new Uri(root).Host, "no folder named after the server");
        CollectionAssert.Contains(names, SharePointSource.SitesFolder, string.Join(", ", names));
        CollectionAssert.Contains(await NamesAsync(source, SharePointSource.SitesFolder), LastName(siteUrl), "the test site, a site collection, in the root site's Sites folder");
        if (names.Contains(SharePointSource.SubsitesFolder))
        {
            CollectionAssert.DoesNotContain(await NamesAsync(source, SharePointSource.SubsitesFolder), LastName(siteUrl), "once: given, and found by the root's search");
        }
        await CheckReadmeAsync(source, $"{SharePointSource.SitesFolder}/{LastName(siteUrl)}", library, fixtureFolder, readme);
    }

    /// <summary>The test site's fixture readme reads through its folder among several.</summary>
    public static async Task CheckReadmeAsync(ISource source, string siteFolder, string library, string fixtureFolder, byte[] readme)
    {
        await using var file = await source.OpenAsync($"{siteFolder}/{library}/{fixtureFolder}/readme.txt", VfsOpenMode.Read, CancellationToken.None);
        var back = new byte[readme.Length];
        Assert.AreEqual(back.Length, await file.ReadFullyAsync(0, back, CancellationToken.None));
        CollectionAssert.AreEqual(readme, back);
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

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

/// <summary>The SDK's contract on the farm (the lab's, or the fake), libraries at the top level.</summary>
[TestClass]
public class SharePointServerContractTests : SourceContractTests
{
    protected override Task<SourceContractSetup> SetUpAsync() => SpsTestServer.Farm.ContractSetupAsync(pinned: false);
}

/// <summary>The same contract with the library pinned and nothing spooled, so reads go through ranges.</summary>
[TestClass]
public class SharePointServerPinnedLibraryContractTests : SourceContractTests
{
    protected override Task<SourceContractSetup> SetUpAsync() => SpsTestServer.Farm.ContractSetupAsync(pinned: true);
}

/// <summary>The SDK's contract on SharePoint Online through a web session (the sandbox, or the fake tenant), libraries at the top level.</summary>
[TestClass]
public class SharePointOnlineRestContractTests : SourceContractTests
{
    protected override Task<SourceContractSetup> SetUpAsync() => SpsTestServer.OnlineRest.ContractSetupAsync(pinned: false);
}

/// <summary>The same contract with the library pinned and nothing spooled.</summary>
[TestClass]
public class SharePointOnlineRestPinnedLibraryContractTests : SourceContractTests
{
    protected override Task<SourceContractSetup> SetUpAsync() => SpsTestServer.OnlineRest.ContractSetupAsync(pinned: true);
}

/// <summary>The REST suite on a farm with a Windows sign-in: the lab's, or the fake farm.</summary>
[TestClass]
public class SharePointServerLiveTests : SharePointRestLiveTests
{
    internal override SharePointTarget Target => SpsTestServer.Farm;
}

/// <summary>The REST suite on SharePoint Online through a web session: the sandbox, or the fake tenant.</summary>
[TestClass]
public class SharePointOnlineRestLiveTests : SharePointRestLiveTests
{
    internal override SharePointTarget Target => SpsTestServer.OnlineRest;
}

/// <summary>What the REST suite checks, on whichever target (<see cref="SharePointTarget"/>).</summary>
public abstract class SharePointRestLiveTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    internal abstract SharePointTarget Target { get; }

    [TestMethod]
    public async Task LibrariesAreTheTopLevelFoldersAndFormsIsHidden()
    {
        Target.Require();
        await using var source = await Target.StartWithFixtureAsync(Target.Settings());
        var names = new List<string>();
        await foreach (var entry in source.ListAsync("", None))
        {
            // the libraries are folders; beside them only the pages in the site's own folder, as Markdown (the classic test page)
            Assert.IsTrue(entry.IsDirectory || entry.Name.EndsWith(".aspx.md", StringComparison.OrdinalIgnoreCase), $"{entry.Name} should be a folder, or a page");
            names.Add(entry.Name);
        }

        CollectionAssert.Contains(names, Target.Library);
        var top = new List<string>();
        await foreach (var entry in source.ListAsync(Target.Library, None))
        {
            top.Add(entry.Name);
        }

        CollectionAssert.DoesNotContain(top, "Forms", "the library's own Forms folder stays hidden");
        CollectionAssert.Contains(top, Target.FixtureFolder);
    }

    [TestMethod]
    public async Task ASubsiteIsAFolderWithItsOwnLibraries()
    {
        Target.Require();
        await using (var maker = await Target.StartWithFixtureAsync(Target.Settings()))
        {
            await SpsTestServer.EnsureSubsiteAsync(maker);
        }

        // a source started now lists the site with the subsite in it (one made since the start shows at the next walk)
        await using var source = await Target.StartWithFixtureAsync(Target.Settings());
        await SpsTestServer.CheckSubsiteAsync(source);
    }

    [TestMethod]
    public async Task TheSitesPagesAreReadOnlyMarkdown()
    {
        Target.Require();
        string page;
        await using (var maker = await Target.StartWithFixtureAsync(Target.Settings()))
        {
            page = await SpsTestServer.EnsurePageAsync(maker);
        }

        await using var source = await Target.StartWithFixtureAsync(Target.Settings());
        await SpsTestServer.CheckPageAsync(source, Target.Site, page);
    }

    [TestMethod]
    public async Task APageInTheSitesOwnFolderShowsAsMarkdown()
    {
        Target.Require();
        await using (var single = await Target.StartWithFixtureAsync(Target.Settings()))
        {
            try
            {
                await SpsTestServer.EnsureClassicPageAsync(single);
            }
            catch (VfsException ex) when (Target.WebSession)
            {
                Assert.Inconclusive($"SharePoint Online took no classic page in the site's own folder: {ex.Message}");
            }
        }

        // a source started now lists the site's folder with the page in it
        await using var source = await Target.StartWithFixtureAsync(Target.Settings());
        var names = await SpsTestServer.NamesAsync(source, "");
        CollectionAssert.Contains(names, SpsTestServer.ClassicPageShown, string.Join(", ", names));
        var entry = await source.StatAsync(SpsTestServer.ClassicPageShown, None);
        Assert.IsTrue(entry!.Attributes.HasFlag(VfsAttributes.ReadOnly));
        await using var file = await source.OpenAsync(SpsTestServer.ClassicPageShown, VfsOpenMode.Read, None);
        var back = new byte[entry.Length];
        Assert.AreEqual(back.Length, await file.ReadFullyAsync(0, back, None));
        var markdown = Encoding.UTF8.GetString(back);
        var lines = markdown.Split('\n', 3);
        Assert.AreEqual($"{Target.Site.TrimEnd('/')}/{SpsTestServer.ClassicPage}", lines[0], true, "the page's address on the first line: " + markdown);
        StringAssert.StartsWith(lines.ElementAtOrDefault(2), "# AnyFS classic page\n", markdown);
        StringAssert.Contains(markdown, "## A classic page");
        StringAssert.Contains(markdown, $"[link]({Target.Site}/Lists/anyfs-nowhere)", "an address made absolute");
        Assert.IsFalse(markdown.Contains("__VIEWSTATE", StringComparison.Ordinal), markdown);
    }

    [TestMethod]
    public async Task SeveralSitesAreTopLevelFolders()
    {
        Target.Require();
        bool subsite;
        await using (var single = await Target.StartWithFixtureAsync(Target.Settings()))
        {
            // SharePoint Online makes no new subsites (EnsureSubsiteAsync); the rest holds without one
            subsite = await SpsTestServer.HasSubsiteAsync(single);
            if (!subsite && !Target.WebSession)
            {
                await SpsTestServer.EnsureSubsiteAsync(single);
                subsite = true;
            }
        }

        // the test site, its subsite, an address below the test site that is no site (found by stepping back: the test site
        // again), and a site that is not there, which is left out
        var site = Target.Site;
        var missing = $"{SpsTestServer.RootOf(site)}/sites/anyfs-no-such-site";
        string[] sites = subsite ? [site, $"{site}/{SpsTestServer.Subsite}", $"{site}/anyfs-not-a-site", missing] : [site, $"{site}/anyfs-not-a-site", missing];
        var settings = Target.Settings();
        settings.SiteUrl = string.Join("\n", sites);
        var result = await Target.TestAsync(settings);
        Assert.IsTrue(result.Success, result.Message);
        if (subsite)
        {
            StringAssert.Contains(result.Message, $"{SpsTestServer.Subsite} ({site}/{SpsTestServer.Subsite}) has ");
        }

        StringAssert.Contains(result.Message, "\nLeft out, as SharePoint does not open it (tried again every few minutes):\n");
        StringAssert.Contains(result.Message[result.Message.IndexOf("\nLeft out", StringComparison.Ordinal)..], missing);

        using var context = new TestSourceContext("sharepoint");
        await using var source = await Target.StartSitesAsync(Target.Settings(), context, sites);
        var top = SpsTestServer.LastName(site);
        CollectionAssert.AreEqual(new[] { top }, await SpsTestServer.NamesAsync(source, ""), "the same site given twice is one folder, the subsite given shows among the site's subsites, and the site not there is left out");
        if (subsite)
        {
            StringAssert.Contains(result.Message, $"It shows below {top}, among its subsites.");
            var below = $"{top}/{SharePointSource.SubsitesFolder}/{SpsTestServer.Subsite}";
            CollectionAssert.Contains(await SpsTestServer.NamesAsync(source, below), "Documents", "the subsite once, where its site lists it");
        }

        await SpsTestServer.CheckReadmeAsync(source, top, Target.Library, Target.FixtureFolder, SpsTestServer.ReadmeBytes);
    }

    [TestMethod]
    public async Task ASiteWhoseServerDoesNotAnswerIsLeftOut()
    {
        Target.Require();
        if (Target.WebSession)
        {
            Assert.Inconclusive("a Windows sign-in, which reaches sites on several servers");
        }

        // 192.0.2.1 is TEST-NET-1 (RFC 5737), which nothing answers: neither Test nor the start waits on it for long, and
        // the source's health is none of its business
        var site = Target.Site;
        const string silent = "http://192.0.2.1/sites/anyfs";
        var settings = Target.Settings();
        settings.SiteUrl = $"{site}\n{silent}";
        var watch = Stopwatch.StartNew();
        var result = await Target.TestAsync(settings);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message[result.Message.IndexOf("\nLeft out", StringComparison.Ordinal)..], silent);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(45), $"Test took {watch.Elapsed}");

        using var context = new TestSourceContext("sharepoint");
        watch.Restart();
        await using var source = await Target.StartSitesAsync(Target.Settings(), context, site, silent);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(45), $"the start took {watch.Elapsed}");
        CollectionAssert.AreEqual(new[] { SpsTestServer.LastName(site) }, await SpsTestServer.NamesAsync(source, ""));
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(source, SpsTestServer.LastName(site)), Target.Library);
        Assert.AreNotEqual(ComponentHealth.Degraded, context.LastHealth, string.Join("; ", context.HealthReports));
    }

    [TestMethod]
    public async Task SitesOnTwoServersAreNamedByTheirServers()
    {
        Target.Require();
        var other = Target.OtherServerSite;
        if (other is null || Target.WebSession)
        {
            Assert.Inconclusive("a Windows sign-in with a second farm's test site in ANYFS_TEST_SPS_SITE2");
            return;
        }

        var site = Target.Site;
        using var context = new TestSourceContext("sharepoint");
        await using var source = await Target.StartSitesAsync(Target.Settings(), context, site, other);
        var names = await SpsTestServer.NamesAsync(source, "");
        var first = SpsTestServer.LastName(site) == SpsTestServer.LastName(other) ? $"{SpsTestServer.LastName(site)} ({new Uri(site).Host})" : SpsTestServer.LastName(site);
        Assert.AreEqual(2, names.Count, string.Join(", ", names));
        CollectionAssert.Contains(names, first);
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(source, names.Single(n => n != first)), Target.Library, "the other server's site has its own libraries");
        await SpsTestServer.CheckReadmeAsync(source, first, Target.Library, Target.FixtureFolder, SpsTestServer.ReadmeBytes);
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
        using var context = new TestSourceContext("sharepoint");
        await using var source = await Target.StartSitesAsync(Target.Settings(), context, root, site);
        var names = await SpsTestServer.NamesAsync(source, "");
        if (names.SequenceEqual([SpsTestServer.LastName(site)]))
        {
            // the lab farms: the test account may open its test site but not the farm's root site, which is left out
            Assert.Inconclusive($"the test account may not open {root}, so it is left out (ARootSiteTheAccountMayNotOpenSaysSo)");
        }

        await SpsTestServer.CheckRootAmongSitesAsync(source, root, site, Target.Library, Target.FixtureFolder, SpsTestServer.ReadmeBytes);
    }

    [TestMethod]
    public async Task ASiteGivenIsKnownAsASiteCollectionOrASubsite()
    {
        Target.Require();
        bool subsite;
        await using (var single = await Target.StartWithFixtureAsync(Target.Settings()))
        {
            subsite = await SpsTestServer.HasSubsiteAsync(single);
        }

        var site = Target.Site;
        using var context = new TestSourceContext("sharepoint");
        await using var source = await Target.StartSitesAsync(Target.Settings(), context, site, $"{site}/{SpsTestServer.Subsite}");
        Assert.IsTrue(SiteAddress.TryParse(site, out var address, out _));
        Assert.IsTrue((await source.Store.FindSiteAsync(address, None)).SiteCollection, "the test site is a site collection's own");
        if (!subsite)
        {
            Assert.Inconclusive($"no subsite {SpsTestServer.Subsite} here (SharePoint Online makes none)");
        }

        Assert.IsTrue(SiteAddress.TryParse($"{site}/{SpsTestServer.Subsite}", out var below, out _));
        Assert.IsFalse((await source.Store.FindSiteAsync(below, None)).SiteCollection, "its subsite is not");
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
        using var context = new TestSourceContext("sharepoint");
        try
        {
            await root.InitialiseAsync(context, None);
        }
        catch (SourceFailedException ex) when (ex.InnerException is VfsException inner && SharePointRestClient.IsDenial(inner))
        {
            // the lab farms: the test account may open its test site but not the farm's root site (ARootSiteTheAccountMayNotOpenSaysSo)
            Assert.Inconclusive($"the test account may not open {settings.SiteUrl}: {ex.Message}");
        }

        await SpsTestServer.CheckOtherSitesAsync(root, Target.Site, Target.Library, Target.FixtureFolder, SpsTestServer.ReadmeBytes);
    }

    [TestMethod]
    public async Task ARootSiteTheAccountMayNotOpenSaysSo()
    {
        Target.Require();
        if (Target.WebSession)
        {
            Assert.Inconclusive("the Windows sign-in's answer; a Microsoft 365 root site is open to its members");
        }

        // the lab farms' root site belongs to the setup account; the test account may open only its test site
        var settings = Target.Settings();
        settings.SiteUrl = SpsTestServer.RootOf(settings.SiteUrl);
        var result = await Target.TestAsync(settings);
        if (result.Success)
        {
            Assert.Inconclusive($"{Target.User} may open {settings.SiteUrl} here: {result.Message}");
        }

        StringAssert.StartsWith(result.Message, $"Signed in as {Target.User}, but access to '{settings.SiteUrl}/' was denied: ");
        StringAssert.Contains(result.Message, "give the address of a site this account may open instead");
        Assert.IsFalse(result.Message.Contains("refused the sign-in", StringComparison.Ordinal), result.Message);

        await using var root = Target.CreateSource(settings);
        using var context = new TestSourceContext("sharepoint");
        var failed = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => root.InitialiseAsync(context, None));
        Assert.AreEqual(result.Message, failed.Message, "the source says what Test said");
        Assert.IsTrue(failed.Permanent, "no point retrying until someone changes the site's permissions or the address");

        // among several sites it is left out, and the others serve
        var site = Target.Site;
        var several = Target.Settings();
        several.SiteUrl = $"{settings.SiteUrl}\n{site}";
        result = await Target.TestAsync(several);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "\nLeft out, as SharePoint does not open it (tried again every few minutes):\n");
        StringAssert.Contains(result.Message[result.Message.IndexOf("\nLeft out", StringComparison.Ordinal)..], settings.SiteUrl);
        using var context2 = new TestSourceContext("sharepoint");
        await using var both = await Target.StartSitesAsync(Target.Settings(), context2, settings.SiteUrl, site);
        CollectionAssert.AreEqual(new[] { SpsTestServer.LastName(site) }, await SpsTestServer.NamesAsync(both, ""));
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(both, SpsTestServer.LastName(site)), Target.Library);
    }

    [TestMethod]
    public async Task RangedReadsAgreeWithTheWholeFileSpooledAndNot()
    {
        Target.Require();
        var expected = SpoTestServer.MediumBytes();
        foreach (var spoolBelowMiB in new[] { 8, 0 })
        {
            await using var source = await Target.StartWithFixtureAsync(Target.Settings(spoolBelowMiB: spoolBelowMiB));
            var path = VfsPath.Join(VfsPath.Join(Target.Library, Target.FixtureFolder), "medium.bin");
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
        Target.Require();
        await using var source = await Target.StartWithFixtureAsync(Target.Settings());
        var fixture = VfsPath.Join(Target.Library, Target.FixtureFolder);
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

    /// <summary>
    /// Files at the edges of the one-request upload and of its pieces. One of 8 to 10 MiB was left empty while the limit was
    /// 8 MiB and the pieces 10: it went as StartUpload alone, which never makes the file (found by the fakes, 24-Oct-2026).
    /// </summary>
    [TestMethod]
    public Task FilesAtTheUploadLimitsRoundTrip() => Target.UploadsRoundTripAsync(
        9L << 20, SharePointRestClient.SimpleUploadLimit, SharePointRestClient.SimpleUploadLimit + 1, 2L * SharePointRestClient.UploadChunkSize + 1);
}
