using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Common;
using AnyFS.Sources.ThreeDX;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Tests;

/// <summary>The names documents and files show as, as Mount3DX names them (spec 7.6).</summary>
[TestClass]
public class ThreeDXNamesTests
{
    [TestMethod]
    [DataRow("DOC-001", "A", "Design spec", "DOC-001 Rev A (Design spec)")]
    [DataRow("DOC-002", "B", "doc-002", "DOC-002 Rev B")]
    [DataRow("A/B", "1", "What? \"Yes\"", "A_B Rev 1 (What_ _Yes_)")]
    [DataRow("Trailing", "C", "dots...", "Trailing Rev C (dots...)")]
    [DataRow("Ends", "D", "Ends.", "Ends Rev D (Ends.)")]
    public void ADocumentIsNameRevisionAndTitle(string name, string revision, string title, string folder)
        => Assert.AreEqual(folder, ThreeDXNames.DocumentFolder(name, revision, title));

    [TestMethod]
    public void ALongDocumentNameIsCutAndNeverEndsInADot()
    {
        var folder = ThreeDXNames.DocumentFolder("X", "1", new string('a', 105) + ". " + new string('b', 40));
        Assert.IsTrue(folder.Length <= ThreeDXNames.MaxDocumentName, folder);
        Assert.IsFalse(folder.EndsWith('.') || folder.EndsWith(' '), folder);
        Assert.IsTrue(folder.StartsWith("X Rev 1 (aaa", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AFileKeepsItsTitleAndExtension()
    {
        Assert.AreEqual("report_v2_.pdf", ThreeDXNames.FileName("report<v2>.pdf"));
        Assert.AreEqual("_", ThreeDXNames.FileName("..."));
        var cut = ThreeDXNames.FileName(new string('n', 200) + ".docx");
        Assert.AreEqual(ThreeDXNames.MaxFileName, cut.Length);
        Assert.IsTrue(cut.EndsWith(".docx", StringComparison.Ordinal));
        Assert.AreEqual("report (2).pdf", ThreeDXNames.NumberedFile("report.pdf", 2));
        Assert.AreEqual("README (1)", ThreeDXNames.NumberedFile("README", 1));
    }

    [TestMethod]
    public void NamesTwoDocumentsShareAreNumberedWithTheirTypes()
    {
        var older = new DxDocument("A1", "DOC", "Spec", "1", "Document", DateTimeOffset.Parse("2020-01-01T00:00:00Z"), null, []);
        var newer = new DxDocument("B2", "doc", "spec", "1", "Controlled Document", DateTimeOffset.Parse("2021-01-01T00:00:00Z"), null, []);
        var other = new DxDocument("C3", "Other", "Other", "1", "Document", null, null, []);
        var tree = DocumentTree.Build([newer, other, older], space: null, DateTimeOffset.UtcNow);
        Assert.AreEqual("DOC Rev 1 (Spec) (1) (Document)", Of(tree, "A1").Name);
        Assert.AreEqual("doc Rev 1 (spec) (2) (Controlled Document)", Of(tree, "B2").Name);
        Assert.AreEqual("Other Rev 1", Of(tree, "C3").Name);
        Assert.IsNotNull(tree.Folder("doc rev 1 (spec) (1) (document)"), "names are found in either case");
    }

    /// <summary>The folder of the document with this id.</summary>
    private static DocumentFolder Of(DocumentTree tree, string id) => tree.Folders.Single(f => f.Document.Id == id);

    [TestMethod]
    public void FilesThatShareANameAndTheSourcesOwnNamesAreNumbered()
    {
        var files = new List<DxFile>
        {
            new("F1", "drawing.pdf", 10, DateTimeOffset.Parse("2020-01-01T00:00:00Z"), null),
            new("F2", "Drawing.pdf", 20, DateTimeOffset.Parse("2021-01-01T00:00:00Z"), null),
            new("F3", "_link.url", 5, null, null),
        };
        var tree = DocumentTree.Build([new DxDocument("D", "DOC", "Title", "A", "Document", null, null, files)], new Uri("https://3dx.contoso.com/3dspace/"), DateTimeOffset.UtcNow);
        var names = Of(tree, "D").Files.Select(f => f.Name).ToList();
        CollectionAssert.AreEquivalent(new[] { "drawing (1).pdf", "Drawing (2).pdf", "_link (1).url", "_link.url", "_metadata.json" }, names);
        Assert.AreEqual(5, tree.FileCount);
    }

    [TestMethod]
    public void TheLinkOpensTheDocumentIn3DSpace()
    {
        var link = Encoding.UTF8.GetString(DocumentTree.Link(new Uri("https://3dx.contoso.com/3dspace/"), "ABC123"));
        Assert.AreEqual("[InternetShortcut]\r\nURL=https://3dx.contoso.com/3dspace/common/emxTree.jsp?objectId=ABC123\r\n", link);
    }
}

/// <summary>3DX's dates, written for a locale (spec 7.6).</summary>
[TestClass]
public class ThreeDXDatesTests
{
    [TestMethod]
    [DataRow("03/07/2018 10:12:33 AM", "DayFirst", "2018-07-03T10:12:33Z")]
    [DataRow("03/07/2018 10:12:33 AM", "MonthFirst", "2018-03-07T10:12:33Z")]
    [DataRow("3/7/2018 4:05:06 PM", "DayFirst", "2018-07-03T16:05:06Z")]
    [DataRow("12/02/2024 12:00:00 AM", "DayFirst", "2024-02-12T00:00:00Z")]
    [DataRow("12/02/2024 12:30:00 PM", "MonthFirst", "2024-12-02T12:30:00Z")]
    [DataRow("25/12/2023 08:00", "MonthFirst", "2023-12-25T08:00:00Z")]
    [DataRow("2023/12/25 08:00:01", "MonthFirst", "2023-12-25T08:00:01Z")]
    [DataRow("2018-07-03T10:12:33Z", "MonthFirst", "2018-07-03T10:12:33Z")]
    [DataRow("2018-07-03T10:12:33+10:00", "DayFirst", "2018-07-03T00:12:33Z")]
    [DataRow("Tue, 03 Jul 2018 10:12:33 GMT", "DayFirst", "2018-07-03T10:12:33Z")]
    [DataRow("03.07.18", "DayFirst", "2018-07-03T00:00:00Z")]
    public void DatesAreReadInTheOrderGiven(string text, string order, string expected)
        => Assert.AreEqual(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ThreeDXDates.Parse(text, Enum.Parse<DateOrder>(order)));

    [TestMethod]
    [DataRow((string?)null)]
    [DataRow("")]
    [DataRow("never")]
    [DataRow("31/02/2020")]
    [DataRow("13/13/2020")]
    [DataRow("01/01/0001 00:00:00")]
    public void NoDateIsNull(string? text) => Assert.IsNull(ThreeDXDates.Parse(text, DateOrder.DayFirst));

    [TestMethod]
    [DataRow("d/MM/yyyy", "DayFirst")]
    [DataRow("M/d/yyyy", "MonthFirst")]
    [DataRow("dd.MM.yyyy", "DayFirst")]
    [DataRow("yyyy-MM-dd", "DayFirst")]
    [DataRow("", "MonthFirst")]
    public void TheComputersOrderComesFromItsDateFormat(string pattern, string order) => Assert.AreEqual(Enum.Parse<DateOrder>(order), ThreeDXDates.OrderOfPattern(pattern));

    [TestMethod]
    [DataRow("en_AU.UTF-8", "DayFirst")]
    [DataRow("en_US.UTF-8", "MonthFirst")]
    [DataRow("de_DE@euro", "DayFirst")]
    [DataRow("C", "MonthFirst")]
    [DataRow(null, "MonthFirst")]
    public void ALocalesOrderIsKnown(string? locale, string order) => Assert.AreEqual(Enum.Parse<DateOrder>(order), ThreeDXDates.OrderOfLocale(locale));

    [TestMethod]
    public void TheSettingNamesTheOrder()
    {
        Assert.AreEqual(DateOrder.DayFirst, ThreeDXDates.Resolve(ThreeDXDates.DayFirst));
        Assert.AreEqual(DateOrder.MonthFirst, ThreeDXDates.Resolve(ThreeDXDates.MonthFirst));
        Assert.AreEqual(ThreeDXDates.MachineOrder(), ThreeDXDates.Resolve(ThreeDXDates.ThisComputer));
    }
}

/// <summary>The settings form, validation and naming (spec 7.6).</summary>
[TestClass]
public class ThreeDXPluginTests
{
    private readonly ThreeDXPlugin _plugin = new();

    [TestMethod]
    [DataRow("https://3dx.contoso.com/3dspace", "https://3dx.contoso.com/3dspace/")]
    [DataRow("https://3dx.contoso.com/3dspace/", "https://3dx.contoso.com/3dspace/")]
    [DataRow("https://3dx.contoso.com:8443/3dspace/common/emxNavigator.jsp?x=1", "https://3dx.contoso.com:8443/3dspace/")]
    [DataRow("https://3dx.contoso.com/enovia/resources/v1/modeler/documents", "https://3dx.contoso.com/enovia/")]
    [DataRow("  http://plm/3dspace  ", "http://plm/3dspace/")]
    public void TheServiceIsCutFromAPage(string given, string space) => Assert.AreEqual(space, ThreeDXSettings.SpaceOf(given)!.AbsoluteUri);

    [TestMethod]
    [DataRow("")]
    [DataRow("3dx.contoso.com")]
    [DataRow("ftp://3dx/3dspace")]
    public void NoAddressIsNoService(string given) => Assert.IsNull(ThreeDXSettings.SpaceOf(given));

    [TestMethod]
    public void TheIconIsThe3DExperienceCompassAsA64PixelPng()
    {
        using var icon = _plugin.OpenIcon();
        Assert.IsNotNull(icon, "embedded in the plugin");
        var header = new byte[24];
        icon.ReadExactly(header);
        CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header[..8], "a PNG");
        Assert.AreEqual(64, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), "width");
        Assert.AreEqual(64, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)), "height");
    }

    [TestMethod]
    public void TheAddressIsStoredAsTheServicesAndNamesTheSource()
    {
        var settings = new ThreeDXSettings { ServerUrl = " https://aus-3dx-01.contoso.com:444/3dspace/common/emxNavigator.jsp " };
        _plugin.NormaliseSettings(settings);
        Assert.AreEqual("https://aus-3dx-01.contoso.com:444/3dspace", settings.ServerUrl);
        Assert.AreEqual("aus-3dx-01.contoso.com", _plugin.SuggestName(settings));
        Assert.IsNull(_plugin.SuggestName(new ThreeDXSettings { ServerUrl = "not a url" }));
    }

    [TestMethod]
    public void TheUserNameAndPasswordShowOnlyForAWindowsAccountAndTheBrowserForTheWebSession()
    {
        var settings = new ThreeDXSettings();
        Assert.AreEqual(ThreeDXSettings.WebSession, settings.SignInMethod, "the web session is the default, as Mount3DX signs in");
        Assert.IsFalse(_plugin.IsVisible(settings, nameof(ThreeDXSettings.Username)));
        Assert.IsTrue(_plugin.IsVisible(settings, nameof(ThreeDXSettings.BrowserPath)));
        settings.SignInMethod = ThreeDXSettings.Windows;
        Assert.IsTrue(_plugin.IsVisible(settings, nameof(ThreeDXSettings.Username)));
        Assert.IsTrue(_plugin.IsVisible(settings, nameof(ThreeDXSettings.Password)));
        Assert.IsFalse(_plugin.IsVisible(settings, nameof(ThreeDXSettings.BrowserPath)));
        settings.SignInMethod = ThreeDXSettings.WindowsCurrent;
        Assert.IsFalse(_plugin.IsVisible(settings, nameof(ThreeDXSettings.Password)));
    }

    [TestMethod]
    public void ValidationNamesWhatIsWrong()
    {
        var errors = _plugin.Validate(new ThreeDXSettings { ServerUrl = "nope", SignInMethod = "Carrier pigeon", QueryThreads = 0, Connections = 17, SpoolBelowMiB = -1, RefreshMinutes = -5, Tenant = "two words" })
            .Where(m => m.IsError).Select(m => m.Property).ToList();
        CollectionAssert.IsSubsetOf(new[] { nameof(ThreeDXSettings.ServerUrl), nameof(ThreeDXSettings.SignInMethod), nameof(ThreeDXSettings.QueryThreads), nameof(ThreeDXSettings.Connections), nameof(ThreeDXSettings.SpoolBelowMiB), nameof(ThreeDXSettings.RefreshMinutes), nameof(ThreeDXSettings.Tenant) }, errors);

        var fine = _plugin.Validate(new ThreeDXSettings { ServerUrl = "https://3dx.contoso.com/3dspace", SignInMethod = ThreeDXSettings.Windows, Username = @"CONTOSO\name", Password = "x" }).ToList();
        Assert.AreEqual(0, fine.Count(m => m.IsError), string.Join("; ", fine.Select(m => m.Message)));
        Assert.IsTrue(_plugin.Validate(new ThreeDXSettings { ServerUrl = "https://3dx.contoso.com" }).Any(m => !m.IsError && m.Property == nameof(ThreeDXSettings.ServerUrl)), "a server without a path is likely not 3DSpace's");
        Assert.IsTrue(_plugin.Validate(new ThreeDXSettings { ServerUrl = "https://3dx.contoso.com/3dspace", SignInMethod = ThreeDXSettings.Windows, Username = "two words" }).Any(m => m.IsError && m.Property == nameof(ThreeDXSettings.Username)));
    }

    [TestMethod]
    public void AUserNameGoesToASignInPageWithoutItsDomain()
    {
        var settings = new ThreeDXSettings { ServerUrl = "https://3dx/3dspace", SignInMethod = ThreeDXSettings.Windows, Username = @"CONTOSO\jdoe", Password = "pw" };
        Assert.AreEqual("jdoe", settings.FormCredentials()!.UserName);
        var windows = (System.Net.NetworkCredential)settings.Credentials();
        Assert.AreEqual(("jdoe", "CONTOSO"), (windows.UserName, windows.Domain));
        Assert.IsNull(new ThreeDXSettings { SignInMethod = ThreeDXSettings.WebSession }.FormCredentials(), "the web session gives no password");
        Assert.AreSame(System.Net.CredentialCache.DefaultNetworkCredentials, new ThreeDXSettings { SignInMethod = ThreeDXSettings.WindowsCurrent }.Credentials());
    }

    [TestMethod]
    public void TheSearchBodyIsMount3DXs()
    {
        using var first = JsonDocument.Parse(ThreeDXClient.SearchBody(0, 100, null, "q", "OnPremise", "label"));
        Assert.AreEqual(0, first.RootElement.GetProperty("start").GetInt32());
        Assert.AreEqual(100, first.RootElement.GetProperty("nresults").GetInt32());
        Assert.AreEqual("3dspace", first.RootElement.GetProperty("source")[0].GetString());
        Assert.IsFalse(first.RootElement.TryGetProperty("next_start", out _));
        using var third = JsonDocument.Parse(ThreeDXClient.SearchBody(2, 100, "S123", "q", "OnPremise", "label"));
        Assert.AreEqual("200 0 2 S123", third.RootElement.GetProperty("next_start").GetString());
        Assert.IsFalse(third.RootElement.TryGetProperty("start", out _));
    }

    [TestMethod]
    public void TheBadgeSignInsAddressIsMount3DXs()
    {
        var page = new Uri("https://gas.contoso.com/gas/?resumeUrl=https%3A%2F%2Fsso.contoso.com%2Fidp%2Fresume&resumePath=%2Fidp%2FAbc%2Fresume&instanceId=x&assuranceLevel=2&a=b+c");
        var query = SingleSignOn.Query(page);
        Assert.AreEqual("/idp/Abc/resume", query["resumePath"]);
        Assert.AreEqual("b c", query["a"]);

        // as Mount3DX reads them: after a '#' too (an application routed by its fragment), names in any case
        var routed = SingleSignOn.Query(new Uri("https://gas.contoso.com/GAS/#/login?resumeUrl=https%3A%2F%2Fsso%2Fr&ResumePath=%2Fidp%2Fx&instanceId=i&assuranceLevel=2"));
        Assert.AreEqual("https://sso/r", routed["resumeUrl"]);
        Assert.AreEqual("/idp/x", routed["resumePath"]);
        StringAssert.StartsWith(SingleSignOn.Shape(new Uri("https://gas.contoso.com/GAS/#/login?resumeUrl=https%3A%2F%2Fsso%2Fr&ResumePath=%2Fidp%2Fx&instanceId=i&assuranceLevel=2")),
            "parameters resumeUrl, ResumePath, instanceId, assuranceLevel; a fragment of ");
        Assert.AreEqual("no parameters", SingleSignOn.Shape(new Uri("https://igas.contoso.com/GAS/")));
        CollectionAssert.AreEqual(new[] { "https://gas.contoso.com/gas/" }, SingleSignOn.Bases(page).ToArray());
        CollectionAssert.AreEqual(new[] { "https://gas.contoso.com/gas/", "https://gas.contoso.com:9031/gas/" }, SingleSignOn.Bases(new Uri("https://gas.contoso.com:9031/gas/?a=b")).ToArray(),
            "Mount3DX's address of the page has no port; a port of the page's own is asked too");
        Assert.AreEqual(
            "https://gas.contoso.com:50123/badge/auth?resumeUrl=https%3A%2F%2Fsso.contoso.com%2Fidp%2Fresume&resumePath=%2Fidp%2FAbc%2Fresume&instanceId=x&pingfedDropoff=true&assuranceLevel=2"
                + "&errorUrl=https%3A%2F%2Fgas.contoso.com%2Fgas%2F&gasWebClient=true&coordinates=null&countryCodeRequested=false&usaItarGeoRequested=false",
            SingleSignOn.Address(page, 50123, "/badge/auth", query, "https://gas.contoso.com/gas/").AbsoluteUri);
        StringAssert.StartsWith(SingleSignOn.Address(page, null, "badge/auth", query, "x").AbsoluteUri, "https://gas.contoso.com/badge/auth?resumeUrl=", "no port configured: the scheme's own, as Mount3DX's address has it");
        StringAssert.StartsWith(SingleSignOn.Address(page, 443, "/b?k=v", query, "x").AbsoluteUri, "https://gas.contoso.com/b?k=v&resumeUrl=");
    }

    [TestMethod]
    public void TheCertificatesOfferedAreMount3DXsBadgeFirst()
    {
        var now = DateTime.Now;
        X509Certificate2 Make(string subject, bool smartcard, int days = 30)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
            var usages = new OidCollection { new("1.3.6.1.5.5.7.3.2") };
            if (smartcard)
            {
                usages.Add(new Oid("1.3.6.1.4.1.311.20.2.2"));
            }

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: false));
            return request.CreateSelfSigned(now.AddDays(days - 60), now.AddDays(days));
        }

        // self-signed, so each is its own issuer
        using var badge = Make("CN=Contoso Hardware Issuing CA 01", smartcard: true);
        using var hello = Make("CN=Windows Hello", smartcard: true);
        using var vpn = Make("CN=VPN", smartcard: false);
        using var expired = Make("CN=Contoso Hardware Issuing CA 00", smartcard: true, days: -1);
        using var keyless = X509CertificateLoader.LoadCertificate(badge.RawData);
        CollectionAssert.AreEqual(new[] { badge }, ClientCertificates.Choose([hello, vpn, keyless, expired, badge], now).ToArray(), "the badge: Mount3DX's issuer, with its key, valid now");
        CollectionAssert.AreEqual(new[] { hello }, ClientCertificates.Choose([vpn, hello, expired], now).ToArray(), "else a smartcard's");
        CollectionAssert.AreEqual(new[] { vpn }, ClientCertificates.Choose([vpn, keyless], now).ToArray(), "else any for client authentication");
        Assert.AreEqual(0, ClientCertificates.Choose([expired, keyless], now).Count);
    }

    [TestMethod]
    public void ARequestSentToTheSignInIsALapse()
    {
        var space = new Uri("https://3dx.contoso.com/3dspace/");
        HttpResponseMessage Redirect(string to) => new(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) }, RequestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri(space, "resources/v1/x")) };
        Assert.IsTrue(ThreeDXSession.IsLapsed(Redirect("https://3dx.contoso.com/3dpassport/login?service=x"), space, expectsJson: true));
        Assert.IsTrue(ThreeDXSession.IsLapsed(Redirect("/3dspace/emxLogin.jsp"), space, expectsJson: true), "a login page of 3DSpace's own");
        Assert.IsFalse(ThreeDXSession.IsLapsed(Redirect("/3dspace/resources/v1/y"), space, expectsJson: true), "a redirect within the service is an answer");
        Assert.IsTrue(ThreeDXSession.IsLapsed(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized), space, expectsJson: false));
        var html = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html/>", Encoding.UTF8, "text/html") };
        Assert.IsTrue(ThreeDXSession.IsLapsed(html, space, expectsJson: true), "a sign-in page where data was asked for");
        Assert.IsFalse(ThreeDXSession.IsLapsed(html, space, expectsJson: false));
        Assert.IsFalse(ThreeDXSession.IsLapsed(new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden), space, expectsJson: true), "a refusal of the account is no lapse");
    }
}

/// <summary>Signing in without a browser, against the fake platform (spec 7.6.2).</summary>
[TestClass]
public class ThreeDXSignInTests
{
    internal static ThreeDXSettings Settings(Fake3DX fake, string signIn = ThreeDXSettings.Windows, string? password = null) => new()
    {
        ServerUrl = fake.SpaceUrl,
        SignInMethod = signIn,
        Username = signIn == ThreeDXSettings.Windows ? fake.User : null,
        Password = signIn == ThreeDXSettings.Windows ? password ?? fake.Password : null,
        DateOrder = ThreeDXDates.DayFirst,
        KeepAliveMinutes = 0,
        RefreshMinutes = 60,
    };

    private static async Task<(bool SignedIn, string? Problem, ThreeDXSession Session)> SignInAsync(ThreeDXSettings settings, ILogger? logger = null)
    {
        var session = new ThreeDXSession(settings, logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, secrets: null, new SessionOptions(Browser: false));
        try
        {
            await session.SignInAsync(interactive: false, _ => { }, CancellationToken.None);
            return (true, null, session);
        }
        catch (SignInRequiredException ex)
        {
            return (false, ex.Message, session);
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("the Windows sign-in to a local HttpListener needs Windows");
        }
    }

    [TestMethod]
    public async Task AUserNameAndPasswordSignInTo3DPassportByItsLoginTicket()
    {
        using var fake = new Fake3DX(Fake3DX.Passport.Form);
        var (signedIn, problem, session) = await SignInAsync(Settings(fake));
        using (session)
        {
            Assert.IsTrue(signedIn, problem);
            Assert.AreEqual(1, fake.SignIns);
            StringAssert.Contains(session.How, fake.User);
        }
    }

    [TestMethod]
    public async Task AnAPIThatRefusesWithoutASessionIsSignedInToFromThe3DSpacePage()
    {
        using var fake = new Fake3DX(Fake3DX.Passport.Form) { ApiRefusesWithoutSession = true };
        fake.Add("DOC-401", "A", "Refuses", "Document", ("r.txt", "refused at first"u8.ToArray()));
        var log = new LineLogger();
        var (signedIn, problem, session) = await SignInAsync(Settings(fake), log);
        using (session)
        {
            Assert.IsTrue(signedIn, problem + "\n" + log);
            Assert.IsTrue(log.Lines.Any(l => l.Contains("signing in from /3dspace/ instead", StringComparison.Ordinal)), log.ToString());

            // a lapse shows as a 401 too, and is renewed the same way
            fake.ExpireSessions();
            var client = new ThreeDXClient(session, DateOrder.DayFirst, log);
            var (count, _) = await client.CountAsync(ThreeDXSettings.DefaultQuery, "OnPremise", CancellationToken.None);
            Assert.AreEqual(1, count, log.ToString());
        }
    }

    [TestMethod]
    public async Task AWrongPasswordIsGivenOnceAndRefusedForGood()
    {
        using var fake = new Fake3DX(Fake3DX.Passport.Form);
        var (signedIn, problem, session) = await SignInAsync(Settings(fake, password: "wrong"));
        using (session)
        {
            Assert.IsFalse(signedIn);
            StringAssert.Contains(problem, "refused the user name and password");
            Assert.AreEqual(0, fake.SignIns);
            var ex = await Assert.ThrowsExactlyAsync<SignInRequiredException>(() => session.SignInAsync(interactive: false, _ => { }, CancellationToken.None));
            Assert.IsTrue(ex.Permanent);
            StringAssert.Contains(ex.Message, "not tried again", "a refused account is not offered again: a lockout");
        }
    }

    [TestMethod]
    public async Task AsYourselfSignsInBy3DPassportsWindowsSignIn()
    {
        RequireWindows();
        using var fake = new Fake3DX(Fake3DX.Passport.Negotiate);
        var (signedIn, problem, session) = await SignInAsync(Settings(fake, ThreeDXSettings.WindowsCurrent));
        using (session)
        {
            Assert.IsTrue(signedIn, problem);
            Assert.AreEqual(1, fake.SignIns);
        }
    }

    [TestMethod]
    public async Task TheWebSessionSignsOnThroughTheIdentityProvidersWindowsSignIn()
    {
        RequireWindows();
        using var fake = new Fake3DX(Fake3DX.Passport.Saml, Fake3DX.Idp.Negotiate);
        var (signedIn, problem, session) = await SignInAsync(Settings(fake, ThreeDXSettings.WebSession));
        using (session)
        {
            Assert.IsTrue(signedIn, problem);
            Assert.AreEqual("by single sign-on", session.How);
        }
    }

    [TestMethod]
    public async Task TheWebSessionSignsOnWithTheBadgeAsMount3DXDoes()
    {
        using var fake = new Fake3DX(Fake3DX.Passport.Saml, Fake3DX.Idp.Badge);
        var log = new LineLogger();
        var (signedIn, problem, session) = await SignInAsync(Settings(fake, ThreeDXSettings.WebSession), log);
        using (session)
        {
            Assert.IsTrue(signedIn, problem + "\n" + log);
            Assert.AreEqual("by single sign-on", session.How);
            Assert.AreEqual(1, fake.BadgeAttempts);
            Assert.AreEqual(1, fake.SignIns, "the badge's SAML answer signed in to 3DPassport");

            // Mount3DX's requests: its first (3DSpace's page) with Firefox's user agent, which the redirects it follows carry;
            // none after it with a user agent; the SAML request posted, the badge asked for, its answer posted back
            var requests = fake.Requests.ToList();
            var signOn = requests.TakeWhile(r => r.Path != "/3dspace/resources/v1/application/CSRF").ToList();
            var first = signOn.TakeWhile(r => r.UserAgent is not null).ToList();
            var trail = string.Join("\n", signOn.Select(r => $"{r.Method} {r.Path} {r.UserAgent}"));
            Assert.AreEqual("GET /3dspace", $"{first[0].Method} {first[0].Path}", trail);
            Assert.IsTrue(first.Count >= 2 && first.All(r => r.Method == "GET" && r.UserAgent == SingleSignOn.FirefoxUserAgent), trail);
            Assert.IsTrue(signOn.Skip(first.Count).All(r => r.UserAgent is null), trail);
            var steps = signOn.Skip(first.Count).Select(r => $"{r.Method} {r.Path}").ToList();
            CollectionAssert.IsSubsetOf(new[] { "POST /idp/sso", "GET /gas/", "GET /gas/js/app.js", "GET /gas/configuration/internal", $"GET :{fake.BadgePort}/badge/auth", "POST /3dpassport/saml/acs" }, steps, trail);
        }
    }

    [TestMethod]
    public async Task TheResumeParametersAfterTheSignInPagesHashAreReadAsMount3DXReadsThem()
    {
        // the identity provider sends the client to /gas/#/?resumeUrl=...: the parameters are in the fragment, not the query
        using var fake = new Fake3DX(Fake3DX.Passport.Saml, Fake3DX.Idp.Badge) { ResumeInFragment = true };
        var log = new LineLogger();
        var (signedIn, problem, session) = await SignInAsync(Settings(fake, ThreeDXSettings.WebSession), log);
        using (session)
        {
            Assert.IsTrue(signedIn, problem + "\n" + log);
            Assert.AreEqual("by single sign-on", session.How);
            Assert.AreEqual(1, fake.BadgeAttempts);
        }
    }

    [TestMethod]
    public async Task TheBadgesPathInAnInlineScriptAndItsPortAsTextAreFoundToo()
    {
        using var fake = new Fake3DX(Fake3DX.Passport.Saml, Fake3DX.Idp.Badge) { BadgePathInline = true, BadgePortAsText = true };
        var log = new LineLogger();
        var (signedIn, problem, session) = await SignInAsync(Settings(fake, ThreeDXSettings.WebSession), log);
        using (session)
        {
            Assert.IsTrue(signedIn, problem + "\n" + log);
            Assert.AreEqual(1, fake.BadgeAttempts);
        }
    }

    [TestMethod]
    public async Task ASingleSignOnThatEndsAt3DPassportOpens3DSpaceThroughIt()
    {
        using var fake = new Fake3DX(Fake3DX.Passport.Saml, Fake3DX.Idp.Badge) { StaysWithoutRelayState = true };
        var log = new LineLogger();
        var (signedIn, problem, session) = await SignInAsync(Settings(fake, ThreeDXSettings.WebSession), log);
        using (session)
        {
            Assert.IsTrue(signedIn, problem + "\n" + log);
            Assert.AreEqual("by single sign-on", session.How);
            Assert.AreEqual(1, fake.BadgeAttempts);
            Assert.AreEqual(1, fake.SignIns, "3DSpace opened through 3DPassport's new sign-in, as Mount3DX's client is sent there");
        }
    }

    [TestMethod]
    public async Task AKeptSessionOpens3DSpaceWithoutAPinWhile3DPassportHoldsItAndTheSingleSignOnStartsAfresh()
    {
        using var fake = new Fake3DX(Fake3DX.Passport.Saml, Fake3DX.Idp.Badge);
        var settings = Settings(fake, ThreeDXSettings.WebSession);
        var (signedIn, problem, first) = await SignInAsync(settings);
        first.Dispose();
        Assert.IsTrue(signedIn, problem);
        Assert.AreEqual(1, fake.BadgeAttempts);

        // 3DSpace's session timed out, 3DPassport's sign-in stands: the cookies kept open 3DSpace again, no PIN
        fake.ExpireSessions();
        var (again, why, second) = await SignInAsync(settings);
        second.Dispose();
        Assert.IsTrue(again, why);
        Assert.AreEqual(1, fake.BadgeAttempts, "no PIN while 3DPassport's sign-in stands");

        // everything gone: Mount3DX's single sign-on, on a client of its own that starts without the cookies of before
        fake.ExpireAll();
        fake.Requests.Clear();
        var log = new LineLogger();
        var (third, whyNot, session) = await SignInAsync(settings, log);
        using (session)
        {
            Assert.IsTrue(third, whyNot + "\n" + log);
            Assert.AreEqual(2, fake.BadgeAttempts);
            var start = fake.Requests.First(r => r.UserAgent == SingleSignOn.FirefoxUserAgent);
            Assert.AreEqual("/3dspace", start.Path);
            Assert.IsFalse(start.HadCookies, "the single sign-on starts with an empty cookie jar, as Mount3DX's");
            Assert.IsTrue(fake.Requests.Any(r => r.HadCookies && r.UserAgent != SingleSignOn.FirefoxUserAgent && r.Path.StartsWith("/3dspace", StringComparison.Ordinal)), "the kept cookies were tried first");
        }
    }

    [TestMethod]
    public async Task ABadgeSignInInTheBackgroundIsTriedOnceUntilASignInSucceeds()
    {
        using var fake = new Fake3DX(Fake3DX.Passport.Saml, Fake3DX.Idp.Badge);
        fake.Add("DOC-PIN", "A", "Pin", "Document");
        var settings = Settings(fake, ThreeDXSettings.WebSession);
        using var session = new ThreeDXSession(settings, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, secrets: null, new SessionOptions(Browser: false, MinRenewalInterval: TimeSpan.Zero));
        await session.SignInAsync(interactive: true, _ => { }, CancellationToken.None);
        Assert.AreEqual(1, fake.BadgeAttempts, "signed in with the badge");
        var client = new ThreeDXClient(session, DateOrder.DayFirst, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        // overnight: 3DX restarted (every session and 3DPassport's sign-in gone) and nobody gives the PIN
        fake.ExpireAll();
        fake.BadgeRefuses = true;
        await Assert.ThrowsExactlyAsync<SignInRequiredException>(() => client.PingAsync(CancellationToken.None));
        Assert.AreEqual(2, fake.BadgeAttempts, "one PIN prompt for the renewal");
        await Assert.ThrowsExactlyAsync<SignInRequiredException>(() => client.PingAsync(CancellationToken.None));
        await Assert.ThrowsExactlyAsync<SignInRequiredException>(() => client.PingAsync(CancellationToken.None));
        Assert.AreEqual(2, fake.BadgeAttempts, "no more PIN prompts piling up while nobody answers");

        // the person signs in again (the source's Retry): the badge again, and renewals may use it again after
        fake.BadgeRefuses = false;
        await session.SignInAsync(interactive: true, _ => { }, CancellationToken.None);
        Assert.AreEqual(3, fake.BadgeAttempts);
        await client.PingAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task AWindowsAccountFillsInTheIdentityProvidersPasswordPage()
    {
        using var fake = new Fake3DX(Fake3DX.Passport.Saml, Fake3DX.Idp.Password);
        var (signedIn, problem, session) = await SignInAsync(Settings(fake));
        session.Dispose();
        Assert.IsTrue(signedIn, problem);

        using var wrong = new Fake3DX(Fake3DX.Passport.Saml, Fake3DX.Idp.Password);
        var (no, why, again) = await SignInAsync(Settings(wrong, password: "wrong"));
        again.Dispose();
        Assert.IsFalse(no);
        StringAssert.Contains(why, "Signing in to 127.0.0.1");
    }

    [TestMethod]
    public async Task ASignInOnlyABrowserCanDoStopsTheFlowWithWhereItStopped()
    {
        using var fake = new Fake3DX(Fake3DX.Passport.Saml, Fake3DX.Idp.Script);
        using var http = new HttpClient(new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false, CookieContainer = new System.Net.CookieContainer(), UseCookies = true });
        var space = new Uri(fake.SpaceUrl + "/");
        var result = await new SignInFlow(http, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).RunAsync(ThreeDXSession.SpaceProbe(space), (r, body) => ThreeDXSession.IsAnswer(r, body, space), CancellationToken.None);
        Assert.IsFalse(result.SignedIn);
        StringAssert.Contains(result.Problem, "/idp/js-done");
        StringAssert.Contains(result.Problem, "a browser would need");
        StringAssert.Contains(result.Route, "/3dpassport/login 200");
    }
}

/// <summary>The 3DX source against the fake platform: the tree, kept lists, the spool and ranges, lapses (spec 7.6).</summary>
[TestClass]
public class ThreeDXSourceTests
{
    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static async Task<ThreeDXSource> StartAsync(ThreeDXSettings settings, TestSourceContext context)
    {
        var source = new ThreeDXSource(settings, new SessionOptions(Browser: false, MinRenewalInterval: TimeSpan.Zero));
        await source.InitialiseAsync(context, CancellationToken.None);
        return source;
    }

    private static async Task<byte[]> ReadAllAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length];
        Assert.AreEqual(buffer.Length, await file.ReadFullyAsync(0, buffer, CancellationToken.None));
        return buffer;
    }

    private static async Task<List<VfsEntry>> ListAsync(ISource source, string path)
    {
        var list = new List<VfsEntry>();
        await foreach (var entry in source.ListAsync(path, CancellationToken.None))
        {
            list.Add(entry);
        }

        return list;
    }

    [TestMethod]
    public async Task EveryDocumentIsAFolderOfItsFilesAsMount3DXShowsThem()
    {
        using var fake = new Fake3DX();
        var spec = Bytes(5000, 1);
        fake.Add("DOC-001", "A", "Design spec", "Document", ("spec.pdf", spec), ("notes.txt", "hello"u8.ToArray()));
        fake.Add("DOC-002", "B", "DOC-002", "Document", ("drawing.dwg", Bytes(100, 2)));
        fake.Add("DOC-003", "A", null, "Document", ("hidden.txt", Bytes(10, 3)));
        using var context = new TestSourceContext("3dx");
        await using var source = await StartAsync(ThreeDXSignInTests.Settings(fake), context);

        var root = await ListAsync(source, "");
        CollectionAssert.AreEquivalent(new[] { "DOC-001 Rev A (Design spec)", "DOC-002 Rev B" }, root.Select(e => e.Name).ToList(), "a document without a title is left out, as Mount3DX leaves it");
        Assert.IsTrue(root.All(e => e.IsDirectory));
        Assert.AreEqual(new DateTimeOffset(2024, 2, 14, 16, 5, 6, TimeSpan.Zero), root[0].Modified, "dates read day-first, as set");

        var files = await ListAsync(source, "DOC-001 Rev A (Design spec)");
        CollectionAssert.AreEquivalent(new[] { "spec.pdf", "notes.txt", "_link.url", "_metadata.json" }, files.Select(e => e.Name).ToList());
        Assert.AreEqual(5000, files.Single(f => f.Name == "spec.pdf").Length);
        CollectionAssert.AreEqual(spec, await ReadAllAsync(source, "DOC-001 Rev A (Design spec)/spec.pdf"));
        CollectionAssert.AreEqual("hello"u8.ToArray(), await ReadAllAsync(source, "doc-001 rev a (design spec)/NOTES.TXT"), "names are found in either case");
        Assert.AreEqual(2, context.Progress.Count(p => p.Message.StartsWith("Listing the documents", StringComparison.Ordinal)), "the first listing shows its progress");
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
        Assert.AreEqual(2, source.Counters.DirectoriesKnown);
        Assert.AreEqual(7, source.Counters.FilesKnown, "three files and two of the source's own per document");

        var ex = await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync("DOC-002 Rev B/new.txt", VfsOpenMode.Create, CancellationToken.None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error, "read-only");
    }

    [TestMethod]
    public async Task TheLinkAndMetadataAreMadeAsMount3DXMakesThem()
    {
        using var fake = new Fake3DX();
        var doc = fake.Add("DOC-010", "A", "Plan", "Document", ("plan.pdf", Bytes(100, 4)));
        using var context = new TestSourceContext("3dx");
        await using var source = await StartAsync(ThreeDXSignInTests.Settings(fake), context);
        var folder = "DOC-010 Rev A (Plan)";

        var link = Encoding.UTF8.GetString(await ReadAllAsync(source, $"{folder}/_link.url"));
        StringAssert.Contains(link, $"URL={fake.SpaceUrl}/common/emxTree.jsp?objectId={doc.Id}");
        Assert.AreEqual(DocumentTree.MetadataPlaceholderLength, (await ListAsync(source, folder)).Single(f => f.Name == "_metadata.json").Length, "a listing does not ask 3DX for it");

        var stat = await source.StatAsync($"{folder}/_metadata.json", CancellationToken.None);
        var metadata = await ReadAllAsync(source, $"{folder}/_metadata.json");
        Assert.AreEqual(metadata.Length, stat!.Length, "a stat gives the length the read gets");
        using var json = JsonDocument.Parse(metadata);
        Assert.AreEqual(doc.Id, json.RootElement.GetProperty("resources/v1/collabServices/attributes/op/read").GetProperty("results")[0].GetProperty("busID").GetString());
        Assert.AreEqual(doc.Id, json.RootElement.GetProperty("resources/v1/modeler/documents/ids")[0].GetProperty("id").GetString());

        using var off = new TestSourceContext("3dx");
        var settings = ThreeDXSignInTests.Settings(fake);
        settings.MetadataFiles = false;
        await using var plain = await StartAsync(settings, off);
        CollectionAssert.AreEqual(new[] { "plan.pdf" }, (await ListAsync(plain, folder)).Select(f => f.Name).ToList());
    }

    [TestMethod]
    public async Task ManyDocumentsAreListedAPageAtATime()
    {
        using var fake = new Fake3DX();
        for (var i = 0; i < 250; i++)
        {
            fake.Add($"DOC-{i:000}", "1", $"Doc {i}", "Document", ("f.txt", Bytes(16, i)));
        }

        using var context = new TestSourceContext("3dx");
        var settings = ThreeDXSignInTests.Settings(fake);
        settings.QueryThreads = 3;
        await using var source = await StartAsync(settings, context);
        Assert.AreEqual(250, (await ListAsync(source, "")).Count);
        Assert.AreEqual(4, fake.SearchPosts, "the count, then three pages of a hundred");
        Assert.AreEqual(3, fake.DocumentPosts);
    }

    [TestMethod]
    public async Task LargeFilesAreReadInRangesAndAServerThatIgnoresThemStillServes()
    {
        using var fake = new Fake3DX();
        var big = Bytes(3 << 20, 9);
        fake.Add("BIG", "1", "Big file", "Document", ("big.bin", big));
        using var context = new TestSourceContext("3dx");
        var settings = ThreeDXSignInTests.Settings(fake);
        settings.SpoolBelowMiB = 1;
        await using var source = await StartAsync(settings, context);

        var path = "BIG Rev 1 (Big file)/big.bin";
        await using (var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None))
        {
            Assert.IsInstanceOfType<RandomAccessOverStream>(file);
            var piece = new byte[4096];
            Assert.AreEqual(piece.Length, await file.ReadFullyAsync(2_000_000, piece, CancellationToken.None));
            CollectionAssert.AreEqual(big.AsSpan(2_000_000, 4096).ToArray(), piece);
            Assert.IsTrue(fake.RangedDownloads >= 1, "the file server was asked for a range");
        }

        fake.HonoursRanges = false;
        await using (var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None))
        {
            var piece = new byte[1000];
            Assert.AreEqual(piece.Length, await file.ReadFullyAsync(3_000_000, piece, CancellationToken.None));
            CollectionAssert.AreEqual(big.AsSpan(3_000_000, 1000).ToArray(), piece);
        }

        CollectionAssert.AreEqual(big, await ReadAllAsync(source, path));
    }

    [TestMethod]
    public async Task AFileWithoutASizeHasItAskedOfTheFileServer()
    {
        using var fake = new Fake3DX();
        var doc = fake.Add("NOSIZE", "1", "No size", "Document");
        var content = Bytes(777, 5);
        doc.Files.Add(new FakeFile(Fake3DX.NewId(), "unsized.bin", content, "03/07/2018 10:12:33 AM", "03/07/2018 10:12:33 AM", SizeGiven: false));
        using var context = new TestSourceContext("3dx");
        await using var source = await StartAsync(ThreeDXSignInTests.Settings(fake), context);
        var path = "NOSIZE Rev 1 (No size)/unsized.bin";
        Assert.AreEqual(0, (await ListAsync(source, "NOSIZE Rev 1 (No size)")).Single(f => f.Name == "unsized.bin").Length, "3DX gives none");
        Assert.AreEqual(777, (await source.StatAsync(path, CancellationToken.None))!.Length);
        CollectionAssert.AreEqual(content, await ReadAllAsync(source, path));
    }

    [TestMethod]
    public async Task ALapsedSessionIsRenewedThrough3DPassportAndAnExpiredSignInWithThePassword()
    {
        using var fake = new Fake3DX();
        var content = Bytes(300, 6);
        fake.Add("DOC-L", "1", "Lapse", "Document", ("a.bin", content), ("b.bin", content));
        using var context = new TestSourceContext("3dx");
        await using var source = await StartAsync(ThreeDXSignInTests.Settings(fake), context);
        var signIns = fake.SignIns;

        fake.ExpireSessions();
        CollectionAssert.AreEqual(content, await ReadAllAsync(source, "DOC-L Rev 1 (Lapse)/a.bin"));
        Assert.AreEqual(signIns, fake.SignIns, "3DPassport's own cookie opened 3DSpace again: no password given");
        Assert.AreEqual(1, source.Session.GenerationOf(source.Session.Space));

        fake.ExpireAll();
        CollectionAssert.AreEqual(content, await ReadAllAsync(source, "DOC-L Rev 1 (Lapse)/b.bin"));
        Assert.AreEqual(signIns + 1, fake.SignIns, "signed in again with the password");
        Assert.AreNotEqual(ComponentHealth.Degraded, context.LastHealth);
    }

    [TestMethod]
    public async Task ARestartServesTheKeptListAndSessionAtOnce()
    {
        using var fake = new Fake3DX();
        fake.Add("DOC-R", "1", "Restart", "Document", ("r.txt", Bytes(20, 7)));
        using var context = new TestSourceContext("3dx");
        var settings = ThreeDXSignInTests.Settings(fake);
        await using (var first = await StartAsync(settings, context))
        {
            Assert.AreEqual(1, (await ListAsync(first, "")).Count);
        }

        var (searches, signIns) = (fake.SearchPosts, fake.SignIns);
        fake.Add("DOC-S", "1", "Second", "Document", ("s.txt", Bytes(20, 8)));
        await using var second = await StartAsync(settings, context);
        Assert.AreEqual(searches, fake.SearchPosts, "the kept list is served: 3DX not asked");
        Assert.AreEqual(signIns, fake.SignIns, "the kept session is used: no sign-in");
        Assert.AreEqual(1, (await ListAsync(second, "")).Count);

        // listed again: still the kept list; the refresh brings the new one
        Assert.AreEqual(1, (await ListAsync(second, "")).Count);
        Assert.AreEqual(searches, fake.SearchPosts);
        await second.RefreshAsync(report: false, CancellationToken.None);
        Assert.AreEqual(2, (await ListAsync(second, "")).Count);
    }

    [TestMethod]
    public async Task TheTestSaysWhoAndHowManyAndHandsItsSessionToTheSource()
    {
        using var fake = new Fake3DX();
        fake.Add("DOC-T1", "A", "First", "Document", ("t.txt", Bytes(20, 12)));
        fake.Add("DOC-T2", "B", "Second", "Document", ("u.txt", Bytes(20, 13)));
        var settings = ThreeDXSignInTests.Settings(fake);
        var result = await new ThreeDXPlugin().TestAsync(settings, new Progress<string>(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, $"as {fake.User}");
        StringAssert.Contains(result.Message, "VPLMProjectLeader.Company Name.Common Space");
        StringAssert.Contains(result.Message, "finds 2 documents");
        StringAssert.Contains(result.Message, "DOC-T1 Rev A (First)");
        Assert.AreEqual(1, fake.SignIns);

        using var context = new TestSourceContext("3dx");
        await using var source = await StartAsync(settings, context);
        Assert.AreEqual(1, fake.SignIns, "the source took the Test's session");
        Assert.AreEqual(2, (await ListAsync(source, "")).Count);

        var refused = await new ThreeDXPlugin().TestAsync(ThreeDXSignInTests.Settings(fake, password: "wrong"), new Progress<string>(), CancellationToken.None);
        Assert.IsFalse(refused.Success, "the Test signs in afresh, so it tries the password given");
        StringAssert.Contains(refused.Message, "refused the user name and password");
    }

    [TestMethod]
    public async Task AFolderListedAgainIsServedFromTheTreeUntilTheNextRefresh()
    {
        using var fake = new Fake3DX();
        var doc = fake.Add("DOC-F", "1", "Fresh", "Document", ("one.txt", Bytes(20, 10)));
        using var context = new TestSourceContext("3dx");
        await using var source = await StartAsync(ThreeDXSignInTests.Settings(fake), context);
        var folder = "DOC-F Rev 1 (Fresh)";
        Assert.AreEqual(3, (await ListAsync(source, folder)).Count);

        // programs list a folder over and over (opening a file among them): no sign that anything changed
        doc.Files.Add(new FakeFile(Fake3DX.NewId(), "two.txt", Bytes(30, 11), "03/07/2018 10:12:33 AM", "03/07/2018 10:12:33 AM"));
        var (posts, searches) = (fake.DocumentPosts, fake.SearchPosts);
        Assert.AreEqual(3, (await ListAsync(source, folder)).Count, "listed again at once: from the tree");
        Assert.AreEqual(posts, fake.DocumentPosts, "3DX not asked");
        Assert.AreEqual(searches, fake.SearchPosts);

        await source.RefreshAsync(report: false, CancellationToken.None);
        CollectionAssert.Contains((await ListAsync(source, folder)).Select(e => e.Name).ToList(), "two.txt");
        Assert.AreEqual(30, (await source.StatAsync($"{folder}/two.txt", CancellationToken.None))!.Length);
    }
}

/// <summary>
/// The web session's browser against the fake platform, whose identity provider signs in only by running its script (spec
/// 7.6.2): single sign-on stops there, and the browser AnyFS opens (headless, on a profile of the test's own) signs in by
/// itself. Over DevTools, every cookie of the profile comes back, 3DPassport's too, which opens 3DSearch afterwards; through
/// WebDriver, 3DSearch is opened in the browser for its cookies. They open Edge or Chrome: ANYFS_TEST_3DX_BROWSER=1.
/// </summary>
[TestClass]
public class ThreeDXBrowserTests
{
    private static void Require()
    {
        if (Environment.GetEnvironmentVariable("ANYFS_TEST_3DX_BROWSER") is not ("1" or "true"))
        {
            Assert.Inconclusive("the browser's live tests open Edge or Chrome: ANYFS_TEST_3DX_BROWSER=1");
        }
    }

    /// <param name="driven">The browser WebDriver drives (edge, firefox); null for DevTools.</param>
    /// <param name="elsewhere">Whether the identity provider is a site of its own that asks to be kept apart (Cross-Origin-Opener-Policy), as an organisation's sign-in can be.</param>
    /// <param name="alert">Whether the identity provider's page shows a notice in a dialog of the browser's on the way.</param>
    private static async Task SignInAndSearchAsync(string? driven, bool elsewhere = false, bool alert = false)
    {
        Require();
        var webDriver = driven is not null;
        using var fake = new Fake3DX(Fake3DX.Passport.Saml, Fake3DX.Idp.Script, elsewhere ? "localhost" : "127.0.0.1") { IdpOpenerPolicy = elsewhere, IdpAlert = alert };
        fake.Add("DOC-B", "1", "Browser", "Document", ("b.txt", "browser"u8.ToArray()));
        var profile = Path.Combine(Path.GetTempPath(), "anyfs-tests", "3dx-browser-" + Guid.NewGuid().ToString("N"));
        var forced = Environment.GetEnvironmentVariable("ANYFS_3DX_WEBDRIVER");
        Environment.SetEnvironmentVariable("ANYFS_3DX_WEBDRIVER", driven);
        var log = new LineLogger();
        try
        {
            using var session = new ThreeDXSession(ThreeDXSignInTests.Settings(fake, ThreeDXSettings.WebSession), log, secrets: null, new SessionOptions(Profile: profile));
            try
            {
                await session.SignInAsync(interactive: false, _ => { }, CancellationToken.None);
            }
            catch (SignInRequiredException ex)
            {
                throw new AssertFailedException($"{ex.Message}\n{log}", ex);
            }

            StringAssert.StartsWith(session.How, "with ", "signed in with a browser:\n" + log);
            Assert.IsTrue(log.Lines.Any(l => l.Contains(webDriver ? "through WebDriver" : "DevTools on port", StringComparison.Ordinal)), log.ToString());
            if (alert && webDriver)
            {
                Assert.IsTrue(log.Lines.Any(l => l.Contains("did not answer for a moment, its window still open", StringComparison.Ordinal)), "the dialog came between AnyFS's questions:\n" + log);
            }

            var opened = fake.SessionsOpened;
            var client = new ThreeDXClient(session, DateOrder.DayFirst, log);
            int count;
            try
            {
                (count, _) = await client.CountAsync(ThreeDXSettings.DefaultQuery, "OnPremise", CancellationToken.None);
            }
            catch (Exception ex) when (ex is SignInRequiredException or VfsException)
            {
                throw new AssertFailedException($"{ex.Message}\n{log}\ncookies: {string.Join(", ", session.Cookies.GetAllCookies().Select(c => $"{c.Name}@{c.Domain}{c.Path}"))}", ex);
            }

            Assert.AreEqual(1, count);
            if (webDriver)
            {
                Assert.AreEqual(opened, fake.SessionsOpened, "3DSearch's session came from the browser:\n" + log);
            }
            else
            {
                Assert.AreEqual(opened + 1, fake.SessionsOpened, "3DSearch opened through 3DPassport with the browser's cookie, no browser:\n" + log);
                Assert.AreEqual(1, fake.SignIns, "one sign-in, in the browser");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANYFS_3DX_WEBDRIVER", forced);
            foreach (var dir in Directory.GetDirectories(Path.GetDirectoryName(profile)!, Path.GetFileName(profile) + "*"))
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    [TestMethod]
    public Task OverDevToolsTheBrowsersCookiesOpenTheServices() => SignInAndSearchAsync(driven: null);

    [TestMethod]
    public Task ThroughWebDriverThreeDSearchIsOpenedInTheBrowser() => SignInAndSearchAsync(driven: "edge");

    [TestMethod]
    public Task ThroughWebDriverFirefoxSignsInAndOpensThreeDSearch() => SignInAndSearchAsync(driven: "firefox");

    [TestMethod]
    public Task ThroughWebDriverFirefoxSignsInThroughAnIdentityProviderElsewhere() => SignInAndSearchAsync(driven: "firefox", elsewhere: true);

    [TestMethod]
    public Task ThroughWebDriverEdgeSignsInThroughAnIdentityProviderElsewhere() => SignInAndSearchAsync(driven: "edge", elsewhere: true);

    [TestMethod]
    public Task ThroughWebDriverFirefoxADialogOnTheWayIsNoClosedWindow() => SignInAndSearchAsync(driven: "firefox", elsewhere: true, alert: true);

    [TestMethod]
    public Task OverDevToolsTheSignInGoesThroughAnIdentityProviderElsewhere() => SignInAndSearchAsync(driven: null, elsewhere: true);
}

/// <summary>
/// A published anyfs (trimmed, single file) serving a 3DX source of the fake platform over its HTTP mount, signed in as
/// yourself through 3DPassport's Windows sign-in: the plugin registered, its settings read from sources.json, the tree,
/// a file, a range and the metadata through the trimmed build. ANYFS_TEST_3DX_EXE names the executable (Windows only:
/// the sign-in is NTLM to HttpListener).
/// </summary>
[TestClass]
public class ThreeDXPublishedTests
{
    [TestMethod]
    public async Task APublishedAnyFSServesThe3DXSourceOverHttp()
    {
        var exe = Environment.GetEnvironmentVariable("ANYFS_TEST_3DX_EXE");
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe) || !OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("set ANYFS_TEST_3DX_EXE to a published anyfs.exe (scripts/publish.ps1), on Windows");
        }

        using var fake = new Fake3DX(Fake3DX.Passport.Negotiate);
        var content = new byte[200_000];
        new Random(3).NextBytes(content);
        var doc = fake.Add("PUB-1", "A", "Published", "Document", ("data.bin", content));
        var work = Path.Combine(Path.GetTempPath(), "anyfs-tests", "3dx-published-" + Guid.NewGuid().ToString("N"));
        var settings = Directory.CreateDirectory(Path.Combine(work, "settings")).FullName;
        var port = 11300 + Random.Shared.Next(500);
        await File.WriteAllTextAsync(Path.Combine(settings, "sources.json"), $$"""
            { "version": 1, "sources": [
              { "id": "aaaaaaaa-0000-0000-0000-000000000001", "name": "fake", "type": "3dx", "enabled": true,
                "settings": { "serverUrl": "{{fake.SpaceUrl}}", "signInMethod": "{{ThreeDXSettings.WindowsCurrent}}", "dateOrder": "{{ThreeDXDates.DayFirst}}", "keepAliveMinutes": 0 } } ] }
            """);
        await File.WriteAllTextAsync(Path.Combine(settings, "collections.json"), $$"""
            { "version": 1, "collections": [ { "id": "aaaaaaaa-0000-0000-0000-000000000002", "name": "pub", "enabled": true,
              "sources": [ { "id": "aaaaaaaa-0000-0000-0000-000000000001", "path": "3dx", "readOnly": true } ],
              "mounts": [ { "id": "aaaaaaaa-0000-0000-0000-000000000003", "name": "HTTP", "provider": "http", "enabled": true, "readOnly": true, "settings": { "bindAddress": "127.0.0.1", "port": {{port}} } } ] } ] }
            """);
        var info = new System.Diagnostics.ProcessStartInfo(exe!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "serve", "--settings-dir", settings, "--log-level", "debug" })
        {
            info.ArgumentList.Add(arg);
        }

        using var serve = System.Diagnostics.Process.Start(info)!;
        var output = new StringBuilder();
        serve.OutputDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        serve.ErrorDataReceived += (_, e) => { lock (output) { output.AppendLine(e.Data); } };
        serve.BeginOutputReadLine();
        serve.BeginErrorReadLine();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var folder = Uri.EscapeDataString("PUB-1 Rev A (Published)");
            var file = $"http://127.0.0.1:{port}/pub/3dx/{folder}/data.bin";
            HttpResponseMessage? answer = null;
            for (var i = 0; i < 120 && answer is not { IsSuccessStatusCode: true }; i++)
            {
                await Task.Delay(500);
                Assert.IsFalse(serve.HasExited, "anyfs serve stopped:\n" + output);
                try
                {
                    answer = await http.GetAsync(file);
                }
                catch (HttpRequestException)
                {
                }
            }

            Assert.IsTrue(answer is { IsSuccessStatusCode: true }, $"the file was served ({answer?.StatusCode}):\n{output}");
            CollectionAssert.AreEqual(content, await answer!.Content.ReadAsByteArrayAsync());

            using var ranged = new HttpRequestMessage(HttpMethod.Get, file);
            ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(150_000, 150_999);
            using var part = await http.SendAsync(ranged);
            Assert.AreEqual(System.Net.HttpStatusCode.PartialContent, part.StatusCode);
            CollectionAssert.AreEqual(content.AsSpan(150_000, 1000).ToArray(), await part.Content.ReadAsByteArrayAsync());

            using var metadata = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/pub/3dx/{folder}/_metadata.json"));
            Assert.AreEqual(doc.Id, metadata.RootElement.GetProperty("resources/v1/modeler/documents/ids")[0].GetProperty("id").GetString());
            Assert.AreEqual(1, fake.SignIns, "signed in once, as yourself");
        }
        finally
        {
            if (!serve.HasExited)
            {
                serve.Kill(entireProcessTree: true);
                serve.WaitForExit(10000);
            }

            try
            {
                Directory.Delete(work, true);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>
/// The published window while a 3DX source lists a big server at its start (the user, 23-Oct-2026: "When enumerating at
/// startup, the UI becomes less responsive"): how long the window takes to answer a message (SendMessageTimeout with
/// WM_NULL, what Windows itself asks a window that may hang) every 50 ms, while the source lists and after. Its WinFsp drive
/// mounted, as at the user's. ANYFS_TEST_3DX_WINDOW names the published anyfs.exe (Windows only: it opens the window);
/// ANYFS_TEST_3DX_WINDOW_DOCS the documents (50,000), ANYFS_TEST_3DX_WINDOW_DELAY_MS how long each listing call takes (150). The
/// numbers go to the test's output.
/// </summary>
[TestClass]
public class ThreeDXWindowTests
{
    public TestContext TestContext { get; set; } = null!;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);

    [TestMethod]
    public async Task TheWindowAnswersWhileThe3DXSourceListsAtItsStart()
    {
        var exe = Environment.GetEnvironmentVariable("ANYFS_TEST_3DX_WINDOW");
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe) || !OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("set ANYFS_TEST_3DX_WINDOW to a published anyfs.exe (it opens its window), on Windows");
        }

        var count = int.Parse(Environment.GetEnvironmentVariable("ANYFS_TEST_3DX_WINDOW_DOCS") ?? "50000", System.Globalization.CultureInfo.InvariantCulture);
        var delay = int.Parse(Environment.GetEnvironmentVariable("ANYFS_TEST_3DX_WINDOW_DELAY_MS") ?? "150", System.Globalization.CultureInfo.InvariantCulture);
        using var fake = new Fake3DX(Fake3DX.Passport.Negotiate) { ListingDelay = TimeSpan.FromMilliseconds(delay) };
        for (var i = 0; i < count; i++)
        {
            fake.Add($"DOC-{i:D6}", "A", $"Document {i}", "Document", ($"file-{i}.pdf", "x"u8.ToArray()), ($"file-{i}.docx", "y"u8.ToArray()));
        }

        var work = Path.Combine(Path.GetTempPath(), "anyfs-tests", "3dx-window-" + Guid.NewGuid().ToString("N"));
        var settings = Directory.CreateDirectory(Path.Combine(work, "settings")).FullName;
        await File.WriteAllTextAsync(Path.Combine(settings, "sources.json"), $$"""
            { "version": 1, "sources": [
              { "id": "aaaaaaaa-0000-0000-0000-000000000001", "name": "big", "type": "3dx", "enabled": true,
                "settings": { "serverUrl": "{{fake.SpaceUrl}}", "signInMethod": "{{ThreeDXSettings.WindowsCurrent}}", "dateOrder": "{{ThreeDXDates.DayFirst}}", "keepAliveMinutes": 0 } } ] }
            """);
        await File.WriteAllTextAsync(Path.Combine(settings, "collections.json"), """
            { "version": 1, "collections": [ { "id": "aaaaaaaa-0000-0000-0000-000000000002", "name": "work", "enabled": true,
              "sources": [ { "id": "aaaaaaaa-0000-0000-0000-000000000001", "path": "3dx", "readOnly": true } ],
              "mounts": [ { "id": "aaaaaaaa-0000-0000-0000-000000000003", "name": "WinFsp", "provider": "winfsp", "enabled": true, "readOnly": true, "settings": { } } ] } ] }
            """);
        var info = new System.Diagnostics.ProcessStartInfo(exe!) { UseShellExecute = false };
        info.ArgumentList.Add("--settings-dir");
        info.ArgumentList.Add(settings);
        using var window = System.Diagnostics.Process.Start(info)!;
        try
        {
            for (var i = 0; i < 100 && window.MainWindowHandle == IntPtr.Zero; i++)
            {
                await Task.Delay(200);
                window.Refresh();
            }

            Assert.AreNotEqual(IntPtr.Zero, window.MainWindowHandle, "the window opened");

            // what the window costs while a source is starting (its tile's progress bars moving) and once it is ready
            if (Environment.GetEnvironmentVariable("ANYFS_TEST_3DX_WINDOW_CPU") is "1" or "true")
            {
                fake.ListingDelay = TimeSpan.FromMinutes(10);
                await Task.Delay(TimeSpan.FromSeconds(10));
                var starting = await CpuAsync(window, TimeSpan.FromSeconds(20));
                fake.ListingDelay = TimeSpan.Zero;
                for (var i = 0; i < 600 && SourceState(Path.Combine(work, "state", "status.json")) != "Ready"; i++)
                {
                    await Task.Delay(500);
                }

                await Task.Delay(TimeSpan.FromSeconds(5));
                var ready = await CpuAsync(window, TimeSpan.FromSeconds(20));
                TestContext.WriteLine($"window CPU while the source starts: {starting:F1}% of one core; once it is ready: {ready:F1}%");
                return;
            }

            var listing = new List<double>();
            var after = new List<double>();
            var status = Path.Combine(work, "state", "status.json");
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            DateTime? listed = null;
            while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromMinutes(5) && (listed is null || DateTime.UtcNow - listed < TimeSpan.FromSeconds(15)))
            {
                var asked = System.Diagnostics.Stopwatch.GetTimestamp();
                SendMessageTimeout(window.MainWindowHandle, 0 /* WM_NULL */, UIntPtr.Zero, IntPtr.Zero, 0x2 /* SMTO_ABORTIFHUNG */, 10_000, out _);
                (listed is null ? listing : after).Add(System.Diagnostics.Stopwatch.GetElapsedTime(asked).TotalMilliseconds);
                if (listed is null && SourceState(status) == "Ready")
                {
                    listed = DateTime.UtcNow;
                    TestContext.WriteLine($"listed {count:N0} documents in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F0} s");
                }

                await Task.Delay(50);
            }

            TestContext.WriteLine($"while listing: {Describe(listing)}");
            TestContext.WriteLine($"after: {Describe(after)}");
            Assert.IsNotNull(listed, "the source listed its documents");

            // then something walking the drive (Explorer, an indexer, an antivirus): every document's folder listed
            if (Environment.GetEnvironmentVariable("ANYFS_TEST_3DX_WINDOW_CRAWL") is "1" or "true")
            {
                var crawling = new List<double>();
                var root = @"\\anyfs\work\3dx";
                var folders = 0;
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var all = Directory.EnumerateDirectories(root).ToList();
                var entries = 0L;
                var walk = Parallel.ForEachAsync(all, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stop.Token }, (folder, token) =>
                {
                    Interlocked.Add(ref entries, Directory.EnumerateFileSystemEntries(folder).Count());
                    Interlocked.Increment(ref folders);
                    return ValueTask.CompletedTask;
                });
                while (!walk.IsCompleted)
                {
                    var asked = System.Diagnostics.Stopwatch.GetTimestamp();
                    SendMessageTimeout(window.MainWindowHandle, 0, UIntPtr.Zero, IntPtr.Zero, 0x2, 10_000, out _);
                    crawling.Add(System.Diagnostics.Stopwatch.GetElapsedTime(asked).TotalMilliseconds);
                    await Task.Delay(50);
                }

                TestContext.WriteLine($"walking the drive ({folders:N0} of {all.Count:N0} folders listed): {Describe(crawling)}");
            }
        }
        finally
        {
            if (!window.HasExited)
            {
                window.Kill(entireProcessTree: true);
                window.WaitForExit(10000);
            }

            try
            {
                Directory.Delete(work, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The process's CPU over <paramref name="period"/>, in percent of one core.</summary>
    private static async Task<double> CpuAsync(System.Diagnostics.Process process, TimeSpan period)
    {
        process.Refresh();
        var before = process.TotalProcessorTime;
        await Task.Delay(period);
        process.Refresh();
        return (process.TotalProcessorTime - before).TotalMilliseconds / period.TotalMilliseconds * 100;
    }

    private static string? SourceState(string statusFile)
    {
        try
        {
            using var stream = new FileStream(statusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var json = JsonDocument.Parse(stream);
            return json.RootElement.GetProperty("collections")[0].GetProperty("sources")[0].GetProperty("state").GetString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static string Describe(List<double> times)
    {
        if (times.Count == 0)
        {
            return "none";
        }

        var sorted = times.OrderBy(t => t).ToList();
        return $"{sorted.Count} asks, median {sorted[sorted.Count / 2]:F1} ms, 95th {sorted[(int)(sorted.Count * 0.95)]:F1} ms, 99th {sorted[(int)(sorted.Count * 0.99)]:F1} ms, worst {sorted[^1]:F0} ms, over 100 ms {sorted.Count(t => t > 100)}, over 500 ms {sorted.Count(t => t > 500)}";
    }
}

/// <summary>
/// The tree at the user's scale (200,000 documents, which Mount3DX lists in about five minutes): how long it takes to build,
/// keep and read back, and how much memory it holds. ANYFS_TEST_3DX_SCALE=1; the numbers go to the test's output.
/// </summary>
[TestClass]
public class ThreeDXScaleTests
{
    public TestContext TestContext { get; set; } = null!;

    internal static List<DxDocument> Documents(int count)
    {
        var random = new Random(7);
        var types = new[] { "Document", "Controlled Document", "Specification" };
        var documents = new List<DxDocument>(count);
        for (var i = 0; i < count; i++)
        {
            var name = $"DOC-{i:0000000}";
            var made = new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(random.Next(5_000_000));
            var files = Enumerable.Range(0, 1 + random.Next(3))
                .Select(f => new DxFile(Guid.NewGuid().ToString("N").ToUpperInvariant(), $"{name} part {f} of the drawing set.pdf", random.Next(10_000, 50_000_000), made, made.AddDays(f)))
                .ToList();
            documents.Add(new DxDocument(Guid.NewGuid().ToString("N").ToUpperInvariant(), name, $"Assembly {i % 977} technical description", ((char)('A' + random.Next(5))).ToString(), types[random.Next(types.Length)], made, made.AddDays(3), files));
        }

        return documents;
    }

    [TestMethod]
    public void TwoHundredThousandDocuments()
    {
        if (Environment.GetEnvironmentVariable("ANYFS_TEST_3DX_SCALE") is not ("1" or "true"))
        {
            Assert.Inconclusive("the scale test builds a tree of 200,000 documents: ANYFS_TEST_3DX_SCALE=1");
        }

        var space = new Uri("https://3dx.contoso.com/3dspace/");
        var start = GC.GetTotalMemory(forceFullCollection: true);
        var documents = Documents(200_000);
        var before = GC.GetTotalMemory(forceFullCollection: true);
        TestContext.WriteLine($"the documents as 3DX describes them: {(before - start) / (1 << 20)} MiB");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tree = DocumentTree.Build(documents, space, DateTimeOffset.UtcNow);
        var build = clock.Elapsed;
        var held = GC.GetTotalMemory(forceFullCollection: true) - before;

        clock.Restart();
        var listed = 0L;
        foreach (var folder in tree.Folders)
        {
            listed += folder.Entry.Name.Length;
        }

        var root = clock.Elapsed;
        var path = Path.Combine(Path.GetTempPath(), "anyfs-tests", "3dx-scale-" + Guid.NewGuid().ToString("N") + ".json.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            clock.Restart();
            IndexFile.Save(path, new SavedIndex(IndexFile.Version, space.AbsoluteUri, "q", "OnPremise", "DayFirst", tree.Taken, tree.Documents));
            var save = clock.Elapsed;
            var size = new FileInfo(path).Length;
            clock.Restart();
            var loaded = IndexFile.Load(path, space.AbsoluteUri, "q", "OnPremise", "DayFirst");
            var load = clock.Elapsed;
            Assert.AreEqual(200_000, loaded!.Documents.Count);

            TestContext.WriteLine($"build {build.TotalMilliseconds:F0} ms, held {held / (1 << 20)} MiB for {tree.Folders.Count:N0} folders and {tree.FileCount:N0} files; root walk {root.TotalMilliseconds:F0} ms; save {save.TotalMilliseconds:F0} ms ({size / (1 << 20)} MiB); load {load.TotalMilliseconds:F0} ms");
        }
        finally
        {
            File.Delete(path);
        }

        GC.KeepAlive(tree);
    }
}

/// <summary>A log kept in lines, for a test to show what happened.</summary>
internal sealed class LineLogger : ILogger
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lines)
        {
            _lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception.Message));
        }
    }

    public override string ToString() => string.Join("\n", Lines);
}

/// <summary>The SDK's contract for a read-only source, against the fake platform.</summary>
[TestClass]
public class ThreeDXContractTests : SourceContractTests
{
    private Fake3DX? _fake;
    private TestSourceContext? _context;

    protected override async Task<SourceContractSetup> SetUpAsync()
    {
        _fake = new Fake3DX();
        var content = Encoding.UTF8.GetBytes("The contract's file: more than sixteen bytes of it.\n");
        _fake.Add("CONTRACT", "A", "The contract", "Document", ("contract.txt", content));
        _context = new TestSourceContext("3dx");
        var source = new ThreeDXSource(ThreeDXSignInTests.Settings(_fake), new SessionOptions(Browser: false));
        await source.InitialiseAsync(_context, CancellationToken.None);
        return new SourceContractSetup(source, "CONTRACT Rev A (The contract)", "CONTRACT Rev A (The contract)/contract.txt", content);
    }

    [TestCleanup]
    public void StopFake()
    {
        _fake?.Dispose();
        _context?.Dispose();
    }
}
