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

    /// <summary>The site's pages library, as it shows.</summary>
    public const string PagesLibrary = "Site Pages";

    /// <summary>The test page's title; its file name SharePoint makes from it (AnyFS-test-page.aspx).</summary>
    public const string PageTitle = "AnyFS test page";

    /// <summary>The test page's one text web part, and the Markdown the page reads as.</summary>
    public const string PageText = "<h2>Heading two</h2><p>Hello <strong>world</strong> from <a href=\"https://example.com/x\">AnyFS</a>.</p><ul><li>one</li><li>two</li></ul>";

    public static readonly byte[] PageMarkdown = Encoding.UTF8.GetBytes($"# {PageTitle}\n\n## Heading two\n\nHello **world** from [AnyFS](https://example.com/x).\n\n- one\n- two\n");

    /// <summary>
    /// The test page in the site's pages, made through the site pages API when there is none with its title: created,
    /// saved with a canvas of one text web part (the API takes the canvas as JSON), published. The source shows pages
    /// read-only, so the test makes it beside it, as it makes the subsite. Returns the name it shows as.
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
                    return SharePointPages.Shown(page.GetProperty("FileName").GetString()!);
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
            return SharePointPages.Shown(saved.RootElement.GetProperty("FileName").GetString()!);
        }
    }

    /// <summary>The test page reads as its Markdown, is read-only, and only pages show there (as .md, no .aspx).</summary>
    public static async Task CheckPageAsync(ISource source, string page)
    {
        var none = CancellationToken.None;
        var names = await NamesAsync(source, PagesLibrary);
        CollectionAssert.Contains(names, page);
        Assert.IsFalse(names.Any(n => n.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)), string.Join(", ", names));
        var path = VfsPath.Join(PagesLibrary, page);
        var entry = await source.StatAsync(path, none);
        Assert.IsNotNull(entry);
        Assert.AreEqual(PageMarkdown.Length, entry.Length, "the size a listing gives is the Markdown's");
        Assert.IsTrue(entry.Attributes.HasFlag(VfsAttributes.ReadOnly));
        await using (var file = await source.OpenAsync(path, VfsOpenMode.Read, none))
        {
            var back = new byte[PageMarkdown.Length];
            Assert.AreEqual(back.Length, await file.ReadFullyAsync(0, back, none));
            Assert.AreEqual(Encoding.UTF8.GetString(PageMarkdown), Encoding.UTF8.GetString(back));
        }

        var ex = await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync(path, VfsOpenMode.ReadWrite, none));
        Assert.AreEqual(VfsError.ReadOnly, ex.Error);
    }

    /// <summary>A second site on another server for the several-sites test (the other lab farm's test site), or null.</summary>
    public static string? OtherServerSite => Env("ANYFS_TEST_SPS_SITE2");

    /// <summary>The name a site shows as among several: the last part of its address.</summary>
    public static string LastName(string siteUrl) => new Uri(siteUrl).AbsolutePath.TrimEnd('/').Split('/')[^1];

    /// <summary>Starts a source of the settings with these sites, one per line, in the context given (the test context, or one holding a Microsoft sign-in).</summary>
    public static async Task<SharePointSiteSource> StartSitesAsync(SharePointSettings settings, TestSourceContext context, params string[] sites)
    {
        settings.SiteUrl = string.Join("\n", sites);
        var source = (SharePointSiteSource)new SharePointPlugin().CreateSource(settings);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await source.InitialiseAsync(context, timeout.Token);
        return source;
    }

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
    public async Task TheSitesPagesAreReadOnlyMarkdown()
    {
        SpsTestServer.Require();
        await using var source = await SpsTestServer.StartWithFixtureAsync(SpsTestServer.Settings());
        var page = await SpsTestServer.EnsurePageAsync(source);
        await SpsTestServer.CheckPageAsync(source, page);
    }

    [TestMethod]
    public async Task SeveralSitesAreTopLevelFolders()
    {
        SpsTestServer.Require();
        bool subsite;
        await using (var single = await SpsTestServer.StartWithFixtureAsync(SpsTestServer.Settings()))
        {
            // SharePoint Online makes no new subsites (EnsureSubsiteAsync); the rest holds without one
            subsite = await SpsTestServer.HasSubsiteAsync(single);
            if (!subsite && !SpsTestServer.WebSession)
            {
                await SpsTestServer.EnsureSubsiteAsync(single);
                subsite = true;
            }
        }

        // the test site, its subsite, an address below the test site that is no site (found by stepping back: the test site
        // again), and a site that is not there, which is left out
        var site = SpsTestServer.Site!;
        var missing = $"{SpsTestServer.RootOf(site)}/sites/anyfs-no-such-site";
        string[] sites = subsite ? [site, $"{site}/{SpsTestServer.Subsite}", $"{site}/anyfs-not-a-site", missing] : [site, $"{site}/anyfs-not-a-site", missing];
        var settings = SpsTestServer.Settings();
        settings.SiteUrl = string.Join("\n", sites);
        var result = await new SharePointPlugin().TestAsync(settings, new Progress<string>(), None);
        Assert.IsTrue(result.Success, result.Message);
        if (subsite)
        {
            StringAssert.Contains(result.Message, $"{SpsTestServer.Subsite} ({site}/{SpsTestServer.Subsite}) has ");
        }

        StringAssert.Contains(result.Message, "\nLeft out, as SharePoint does not open it (tried again every few minutes):\n");
        StringAssert.Contains(result.Message[result.Message.IndexOf("\nLeft out", StringComparison.Ordinal)..], missing);

        using var context = new TestSourceContext("sharepoint");
        await using var source = await SpsTestServer.StartSitesAsync(SpsTestServer.Settings(), context, sites);
        var top = SpsTestServer.LastName(site);
        CollectionAssert.AreEqual(subsite ? new[] { top, SpsTestServer.Subsite } : new[] { top }, await SpsTestServer.NamesAsync(source, ""), "the same site given twice is one folder; the site not there is left out");
        if (subsite)
        {
            CollectionAssert.Contains(await SpsTestServer.NamesAsync(source, SpsTestServer.Subsite), "Documents");
        }

        await SpsTestServer.CheckReadmeAsync(source, top, SpsTestServer.Library, SpsTestServer.FixtureFolder, SpsTestServer.ReadmeBytes);
    }

    [TestMethod]
    public async Task ASiteWhoseServerDoesNotAnswerIsLeftOut()
    {
        SpsTestServer.Require();
        if (SpsTestServer.WebSession)
        {
            Assert.Inconclusive("a Windows sign-in, which reaches sites on several servers");
        }

        // 192.0.2.1 is TEST-NET-1 (RFC 5737), which nothing answers: neither Test nor the start waits on it for long, and
        // the source's health is none of its business
        var site = SpsTestServer.Site!;
        const string silent = "http://192.0.2.1/sites/anyfs";
        var settings = SpsTestServer.Settings();
        settings.SiteUrl = $"{site}\n{silent}";
        var watch = Stopwatch.StartNew();
        var result = await new SharePointPlugin().TestAsync(settings, new Progress<string>(), None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message[result.Message.IndexOf("\nLeft out", StringComparison.Ordinal)..], silent);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(45), $"Test took {watch.Elapsed}");

        using var context = new TestSourceContext("sharepoint");
        watch.Restart();
        await using var source = await SpsTestServer.StartSitesAsync(SpsTestServer.Settings(), context, site, silent);
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(45), $"the start took {watch.Elapsed}");
        CollectionAssert.AreEqual(new[] { SpsTestServer.LastName(site) }, await SpsTestServer.NamesAsync(source, ""));
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(source, SpsTestServer.LastName(site)), SpsTestServer.Library);
        Assert.AreNotEqual(ComponentHealth.Degraded, context.LastHealth, string.Join("; ", context.HealthReports));
    }

    [TestMethod]
    public async Task SitesOnTwoServersAreNamedByTheirServers()
    {
        SpsTestServer.Require();
        var other = SpsTestServer.OtherServerSite;
        if (other is null || SpsTestServer.WebSession)
        {
            Assert.Inconclusive("a Windows sign-in with a second farm's test site in ANYFS_TEST_SPS_SITE2");
            return;
        }

        var site = SpsTestServer.Site!;
        using var context = new TestSourceContext("sharepoint");
        await using var source = await SpsTestServer.StartSitesAsync(SpsTestServer.Settings(), context, site, other);
        var names = await SpsTestServer.NamesAsync(source, "");
        var first = SpsTestServer.LastName(site) == SpsTestServer.LastName(other) ? $"{SpsTestServer.LastName(site)} ({new Uri(site).Host})" : SpsTestServer.LastName(site);
        Assert.AreEqual(2, names.Count, string.Join(", ", names));
        CollectionAssert.Contains(names, first);
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(source, names.Single(n => n != first)), SpsTestServer.Library, "the other server's site has its own libraries");
        await SpsTestServer.CheckReadmeAsync(source, first, SpsTestServer.Library, SpsTestServer.FixtureFolder, SpsTestServer.ReadmeBytes);
    }

    [TestMethod]
    public async Task ARootSiteAmongSeveralShowsTheOtherSites()
    {
        SpsTestServer.Require();
        await using (await SpsTestServer.StartWithFixtureAsync(SpsTestServer.Settings()))
        {
            // the fixture is in place in the test site
        }

        var site = SpsTestServer.Site!;
        var root = SpsTestServer.RootOf(site);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await SpsTestServer.StartSitesAsync(SpsTestServer.Settings(), context, root, site);
        var names = await SpsTestServer.NamesAsync(source, "");
        if (names.SequenceEqual([SpsTestServer.LastName(site)]))
        {
            // the lab farms: the test account may open its test site but not the farm's root site, which is left out
            Assert.Inconclusive($"the test account may not open {root}, so it is left out (ARootSiteTheAccountMayNotOpenSaysSo)");
        }

        await SpsTestServer.CheckRootAmongSitesAsync(source, root, site, SpsTestServer.Library, SpsTestServer.FixtureFolder, SpsTestServer.ReadmeBytes);
    }

    [TestMethod]
    public async Task ASiteGivenIsKnownAsASiteCollectionOrASubsite()
    {
        SpsTestServer.Require();
        bool subsite;
        await using (var single = await SpsTestServer.StartWithFixtureAsync(SpsTestServer.Settings()))
        {
            subsite = await SpsTestServer.HasSubsiteAsync(single);
        }

        var site = SpsTestServer.Site!;
        using var context = new TestSourceContext("sharepoint");
        await using var source = await SpsTestServer.StartSitesAsync(SpsTestServer.Settings(), context, site, $"{site}/{SpsTestServer.Subsite}");
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
        SpsTestServer.Require();
        await using (await SpsTestServer.StartWithFixtureAsync(SpsTestServer.Settings()))
        {
            // the fixture is in place in the test site
        }

        var settings = SpsTestServer.Settings();
        settings.SiteUrl = SpsTestServer.RootOf(settings.SiteUrl);
        await using var root = (SharePointSiteSource)new SharePointPlugin().CreateSource(settings);
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

        await SpsTestServer.CheckOtherSitesAsync(root, SpsTestServer.Site!, SpsTestServer.Library, SpsTestServer.FixtureFolder, SpsTestServer.ReadmeBytes);
    }

    [TestMethod]
    public async Task ARootSiteTheAccountMayNotOpenSaysSo()
    {
        SpsTestServer.Require();
        if (SpsTestServer.WebSession)
        {
            Assert.Inconclusive("the Windows sign-in's answer; a Microsoft 365 root site is open to its members");
        }

        // the lab farms' root site belongs to the setup account; the test account may open only its test site
        var settings = SpsTestServer.Settings();
        settings.SiteUrl = SpsTestServer.RootOf(settings.SiteUrl);
        var result = await new SharePointPlugin().TestAsync(settings, new Progress<string>(), None);
        if (result.Success)
        {
            Assert.Inconclusive($"{SpsTestServer.User} may open {settings.SiteUrl} here: {result.Message}");
        }

        StringAssert.StartsWith(result.Message, $"Signed in as {SpsTestServer.User}, but access to '{settings.SiteUrl}/' was denied: ");
        StringAssert.Contains(result.Message, "give the address of a site this account may open instead");
        Assert.IsFalse(result.Message.Contains("refused the sign-in", StringComparison.Ordinal), result.Message);

        await using var root = (SharePointSiteSource)new SharePointPlugin().CreateSource(settings);
        using var context = new TestSourceContext("sharepoint");
        var failed = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => root.InitialiseAsync(context, None));
        Assert.AreEqual(result.Message, failed.Message, "the source says what Test said");
        Assert.IsTrue(failed.Permanent, "no point retrying until someone changes the site's permissions or the address");

        // among several sites it is left out, and the others serve
        var site = SpsTestServer.Site!;
        var several = SpsTestServer.Settings();
        several.SiteUrl = $"{settings.SiteUrl}\n{site}";
        result = await new SharePointPlugin().TestAsync(several, new Progress<string>(), None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "\nLeft out, as SharePoint does not open it (tried again every few minutes):\n");
        StringAssert.Contains(result.Message[result.Message.IndexOf("\nLeft out", StringComparison.Ordinal)..], settings.SiteUrl);
        using var context2 = new TestSourceContext("sharepoint");
        await using var both = await SpsTestServer.StartSitesAsync(SpsTestServer.Settings(), context2, settings.SiteUrl, site);
        CollectionAssert.AreEqual(new[] { SpsTestServer.LastName(site) }, await SpsTestServer.NamesAsync(both, ""));
        CollectionAssert.Contains(await SpsTestServer.NamesAsync(both, SpsTestServer.LastName(site)), SpsTestServer.Library);
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
