using System.Net;
using System.Reflection;
using System.Text;
using AnyFS.Sdk;
using AnyFS.Sources.SharePoint;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Client;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The Microsoft clouds (spec 7.3): which one a site or tenant is in, and what that changes, the sign-in endpoint,
/// the scopes, Graph's address and the app registration. Nothing here talks to a cloud; the US Government endpoints
/// were checked by hand on 18-Oct-2026 (device code issued for qualified scopes; AnyFS's own client ID unknown there).
/// </summary>
[TestClass]
public class MicrosoftCloudTests
{
    [TestMethod]
    [DataRow("contoso.sharepoint.com", MicrosoftCloud.GlobalName)]
    [DataRow("contoso-my.sharepoint.com", MicrosoftCloud.GlobalName)]
    [DataRow("acme.sharepoint.us", MicrosoftCloud.UsGovernmentName)]
    [DataRow("ACME.SharePoint.US", MicrosoftCloud.UsGovernmentName)]
    [DataRow("acme-my.sharepoint.us", MicrosoftCloud.UsGovernmentName)]
    [DataRow("sharepoint.us.example.com", null)]
    [DataRow("notsharepoint.us", null)]
    [DataRow("anyfs-sp-1", null)]
    [DataRow("192.168.0.92", null)]
    public void TheCloudComesFromTheSiteAddress(string host, string? cloud)
    {
        Assert.AreEqual(cloud, MicrosoftCloud.FromSiteHost(host)?.Name);
    }

    [TestMethod]
    public void TheTenantComesFromTheSiteAddress()
    {
        var gov = MicrosoftCloud.UsGovernment;
        Assert.AreEqual("acme.onmicrosoft.us", gov.TenantForSiteHost("acme.sharepoint.us"));
        Assert.AreEqual("acme.onmicrosoft.us", gov.TenantForSiteHost("ACME.sharepoint.us"));
        Assert.AreEqual("acme.onmicrosoft.us", gov.TenantForSiteHost("acme-my.sharepoint.us"), "a OneDrive is in the same tenant");
        Assert.AreEqual("acme.onmicrosoft.us", gov.TenantForSiteHost("acme-admin.sharepoint.us"));
        Assert.IsNull(gov.TenantForSiteHost("contoso.sharepoint.com"), "another cloud's site");
        Assert.IsNull(gov.TenantForSiteHost("a.b.sharepoint.us"), "not a tenant's host");
        Assert.IsNull(gov.TenantForSiteHost(null));
        Assert.AreEqual("contoso.onmicrosoft.com", MicrosoftCloud.Global.TenantForSiteHost("contoso.sharepoint.com"));
    }

    [TestMethod]
    public void ScopesAreQualifiedOutsideTheGlobalCloud()
    {
        string[] scopes = ["User.Read", "Files.ReadWrite.All", "offline_access"];
        CollectionAssert.AreEqual(scopes, MicrosoftCloud.Global.QualifyScopes(scopes).ToArray(), "bare names mean the global Graph");
        CollectionAssert.AreEqual(
            new[] { "https://graph.microsoft.us/User.Read", "https://graph.microsoft.us/Files.ReadWrite.All", "offline_access" },
            MicrosoftCloud.UsGovernment.QualifyScopes(scopes).ToArray(),
            "the identity platform's own scopes stay as they are");
        CollectionAssert.AreEqual(
            new[] { "https://graph.microsoft.us/.default" },
            MicrosoftCloud.UsGovernment.QualifyScopes(["https://graph.microsoft.us/.default"]).ToArray(),
            "a qualified scope is left alone");
    }

    [TestMethod]
    public void EachCloudHasItsOwnAddresses()
    {
        Assert.AreEqual("https://graph.microsoft.com/v1.0/", MicrosoftCloud.Global.GraphBaseUrl);
        Assert.AreEqual(AzureCloudInstance.AzurePublic, MicrosoftCloud.Global.Instance);
        Assert.IsTrue(MicrosoftCloud.Global.HasShippedRegistration);

        Assert.AreEqual("https://graph.microsoft.us/v1.0/", MicrosoftCloud.UsGovernment.GraphBaseUrl);
        Assert.AreEqual(AzureCloudInstance.AzureUsGovernment, MicrosoftCloud.UsGovernment.Instance);
        Assert.AreEqual("login.microsoftonline.us", MicrosoftCloud.UsGovernment.LoginHost);
        Assert.IsFalse(MicrosoftCloud.UsGovernment.HasShippedRegistration, "AnyFS's registration exists only in the global cloud");
    }

    [TestMethod]
    public void AChoiceWinsThenTheSiteThenTheTenant()
    {
        Assert.AreSame(MicrosoftCloud.UsGovernment, MicrosoftCloud.Resolve(MicrosoftCloud.AutomaticName, "acme.sharepoint.us", null));
        Assert.AreSame(MicrosoftCloud.UsGovernment, MicrosoftCloud.Resolve(null, null, "acme.onmicrosoft.us"));
        Assert.AreSame(MicrosoftCloud.UsGovernment, MicrosoftCloud.Resolve("us government (gcc high)", "contoso.sharepoint.com", null), "a choice is matched case-insensitively and wins");
        Assert.AreSame(MicrosoftCloud.Global, MicrosoftCloud.Resolve(MicrosoftCloud.GlobalName, "acme.sharepoint.us", null), "validation reports the contradiction");
        Assert.AreSame(MicrosoftCloud.Global, MicrosoftCloud.Resolve("", "contoso.sharepoint.com", "organizations"));
        Assert.AreSame(MicrosoftCloud.Global, MicrosoftCloud.Resolve("Mars", null, null), "an unknown choice counts as Automatic");

        Assert.IsTrue(MicrosoftCloud.IsChoice(null));
        Assert.IsTrue(MicrosoftCloud.IsChoice("automatic"));
        Assert.IsTrue(MicrosoftCloud.IsChoice(MicrosoftCloud.UsGovernmentName));
        Assert.IsFalse(MicrosoftCloud.IsChoice("Mars"));
    }

    [TestMethod]
    public void RelativeAddressesGoToTheCloudsGraph()
    {
        var handler = new RecordingHandler();
        using var graph = new GraphClient((_, _) => Task.FromResult("token"), NullLogger.Instance, handler: handler, cloud: MicrosoftCloud.UsGovernment);
        using var site = graph.GetJsonAsync("sites/acme.sharepoint.us:/sites/Bar", "Bar", CancellationToken.None).GetAwaiter().GetResult();
        Assert.AreEqual("https://graph.microsoft.us/v1.0/sites/acme.sharepoint.us:/sites/Bar", handler.Requests.Single().RequestUri!.AbsoluteUri);
        Assert.AreEqual("token", handler.Requests.Single().Headers.Authorization?.Parameter);

        using var next = graph.GetJsonAsync("https://graph.microsoft.us/v1.0/drives/b!x/items?$skiptoken=1", "x", CancellationToken.None).GetAwaiter().GetResult();
        Assert.AreEqual("https://graph.microsoft.us/v1.0/drives/b!x/items?$skiptoken=1", handler.Requests[1].RequestUri!.AbsoluteUri, "a nextLink is followed as it is");

        using var global = new GraphClient((_, _) => Task.FromResult("token"), NullLogger.Instance, handler: new RecordingHandler());
        Assert.AreEqual("https://graph.microsoft.com/v1.0/", global.BaseUrl);
    }

    [TestMethod]
    public void AUsGovernmentSignInAsksForQualifiedScopes()
    {
        var signIn = new EntraSignIn(Options("00000000-0000-0000-0000-000000000001", "acme.onmicrosoft.us", MicrosoftCloud.UsGovernment), NullLogger.Instance);
        CollectionAssert.AreEqual(
            new[] { "https://graph.microsoft.us/User.Read", "https://graph.microsoft.us/Files.ReadWrite.All", "https://graph.microsoft.us/Sites.ReadWrite.All" },
            signIn.Scopes.ToArray());

        var global = new EntraSignIn(Options(SharePointPlugin.DefaultClientId, "organizations", null), NullLogger.Instance);
        CollectionAssert.AreEqual(EntraSignIn.SiteScopes, global.Scopes.ToArray());
    }

    [TestMethod]
    public void RefusalsAreExplainedInTheSettingsTerms()
    {
        var gov = MicrosoftCloud.UsGovernment;
        const string own = "11111111-2222-3333-4444-555555555555";
        static MsalServiceException Refusal(string text) => new("invalid_request", text);

        // what the US Government endpoint answers AnyFS's own client ID with (checked by hand, 18-Oct-2026)
        var unknownApp = Refusal("AADSTS50059: No tenant-identifying information found in either the request or implied by any provided credentials. Trace ID: x");
        var message = EntraSignIn.Describe(unknownApp, gov, SharePointPlugin.DefaultClientId, "organizations");
        StringAssert.Contains(message, "global cloud");
        StringAssert.Contains(message, "client ID");
        Assert.IsTrue(EntraSignIn.IsPermanent(unknownApp), "retrying cannot help, and would open the browser again");

        message = EntraSignIn.Describe(Refusal("AADSTS700016: Application with identifier '" + own + "' was not found in the directory 'acme'."), gov, own, "acme.onmicrosoft.us");
        StringAssert.Contains(message, own);
        StringAssert.Contains(message, "for tenant acme.onmicrosoft.us");

        StringAssert.Contains(EntraSignIn.Describe(Refusal("AADSTS50194: Application is not configured as a multi-tenant application."), gov, own, "organizations"), "Tenant setting");
        StringAssert.Contains(EntraSignIn.Describe(Refusal("AADSTS90002: Tenant 'acme.onmicrosoft.us' not found."), gov, own, "acme.onmicrosoft.us"), "tenant acme.onmicrosoft.us was not found in the US Government (GCC High) cloud");
        StringAssert.Contains(EntraSignIn.Describe(Refusal("AADSTS7000218: The request body must contain the following parameter: 'client_assertion' or 'client_secret'."), gov, own, "acme.onmicrosoft.us"), "Allow public client flows");
        StringAssert.Contains(EntraSignIn.Describe(Refusal("AADSTS65001: The user or administrator has not consented to use the application."), gov, own, "acme.onmicrosoft.us"), "administrator must consent");
        StringAssert.Contains(EntraSignIn.Describe(Refusal("AADSTS530035: Access has been blocked by security defaults."), MicrosoftCloud.Global, SharePointPlugin.DefaultClientId, "organizations"), "Conditional Access");

        var other = Refusal("AADSTS12345: Something new.\nmore detail");
        Assert.AreEqual("the sign-in failed: AADSTS12345: Something new.", EntraSignIn.Describe(other, gov, own, "organizations"));
        Assert.IsFalse(EntraSignIn.IsPermanent(other));
        Assert.IsFalse(EntraSignIn.IsPermanent(new MsalServiceException("authorization_declined", "The user declined.")), "a decline is the user's, not the settings'");
    }

    [TestMethod]
    public void AadstsCodesAreReadFromTheMessage()
    {
        Assert.AreEqual(50059, EntraSignIn.AadstsCode("AADSTS50059: No tenant-identifying information"));
        Assert.AreEqual(530035, EntraSignIn.AadstsCode("blocked: AADSTS530035: Access has been blocked"));
        Assert.AreEqual(7000218, EntraSignIn.AadstsCode("AADSTS7000218: The request body"));
        Assert.IsNull(EntraSignIn.AadstsCode("no code here"));
        Assert.IsNull(EntraSignIn.AadstsCode("AADSTS: nothing after it"));
    }

    private static SignInOptions Options(string clientId, string tenant, MicrosoftCloud? cloud)
        => new(clientId, tenant, null, SignInMethod.DeviceCode, EntraSignIn.SiteScopes, _ => { }, null, () => null, _ => { }, Cloud: cloud);

    /// <summary>Answers every request with an empty JSON object and keeps the requests.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }
}

/// <summary>The cloud, the sign-in and the name a SharePoint source gets for its address, and the combinations it refuses.</summary>
[TestClass]
public class SharePointCloudSettingsTests
{
    private const string OwnClientId = "11111111-2222-3333-4444-555555555555";
    private const string ContainerType = "91710488-5756-407f-9046-fbe5f0b4de73";

    [TestMethod]
    public void AUsGovernmentSiteSignsInThereToItsOwnTenant()
    {
        var settings = new SharePointSettings { SiteUrl = "https://acme.sharepoint.us/sites/Bar", ClientId = OwnClientId };
        Assert.AreSame(MicrosoftCloud.UsGovernment, settings.EffectiveCloud);
        Assert.AreEqual("acme.onmicrosoft.us", settings.EffectiveTenant, "an organisation's own registration is often for its own tenant only");

        settings.Tenant = "acme.gov";
        Assert.AreEqual("acme.gov", settings.EffectiveTenant, "a tenant given is kept");

        var global = new SharePointSettings { SiteUrl = "https://contoso.sharepoint.com/sites/Team" };
        Assert.AreSame(MicrosoftCloud.Global, global.EffectiveCloud);
        Assert.AreEqual("organizations", global.EffectiveTenant, "the global cloud keeps signing in as before");
    }

    [TestMethod]
    public void AUsGovernmentSiteWithoutARegistrationGetsTheWebSession()
    {
        var plugin = new SharePointPlugin();
        var settings = new SharePointSettings { SiteUrl = "https://acme.sharepoint.us/sites/Bar" };
        Assert.AreEqual(SharePointSignIn.WebSession, settings.EffectiveSignIn, "AnyFS's own registration cannot exist in that cloud");
        Assert.AreEqual(0, plugin.Validate(settings).Count());

        settings.ClientId = OwnClientId;
        Assert.AreEqual(SharePointSignIn.AppBrowser, settings.EffectiveSignIn, "the organisation's own registration makes it an app sign-in");
        Assert.AreEqual(0, plugin.Validate(settings).Count());

        // an app sign-in chosen by hand without the organisation's registration is refused, before any sign-in
        settings = new SharePointSettings { SiteUrl = "https://acme.sharepoint.us/sites/Bar", SignInMethod = SharePointSettings.AppBrowser };
        var message = plugin.Validate(settings).Single();
        Assert.AreEqual(nameof(SharePointSettings.ClientId), message.Property);
        StringAssert.Contains(message.Message, "global cloud");
        StringAssert.Contains(message.Message, "http://localhost");
        StringAssert.Contains(message.Message, SharePointPlugin.SitePermissions);

        var test = plugin.TestAsync(settings, new Progress<string>(), CancellationToken.None).GetAwaiter().GetResult();
        Assert.IsFalse(test.Success);
        StringAssert.Contains(test.Message, "global cloud", "the Test button says so before any sign-in");
    }

    [TestMethod]
    public void ACloudChoiceThatContradictsTheSiteOrTenantIsCaught()
    {
        var plugin = new SharePointPlugin();
        var settings = new SharePointSettings { SiteUrl = "https://acme.sharepoint.us/sites/Bar", ClientId = OwnClientId, Cloud = MicrosoftCloud.GlobalName };
        var message = plugin.Validate(settings).Single();
        Assert.AreEqual(nameof(SharePointSettings.Cloud), message.Property);
        StringAssert.Contains(message.Message, "acme.sharepoint.us is a site in the US Government (GCC High) cloud");

        settings.Cloud = "Mars";
        Assert.AreEqual(nameof(SharePointSettings.Cloud), plugin.Validate(settings).Single().Property);

        settings.Cloud = MicrosoftCloud.AutomaticName;
        settings.Tenant = "contoso.onmicrosoft.com";
        message = plugin.Validate(settings).Single();
        Assert.AreEqual(nameof(SharePointSettings.Tenant), message.Property);
        StringAssert.Contains(message.Message, "contoso.onmicrosoft.com is a tenant in the Global cloud");
    }

    [TestMethod]
    public void EmbeddedGoesByTheTenantsDomain()
    {
        var plugin = new SharePointPlugin();
        var settings = new SharePointSettings { SiteUrl = ContainerType, Tenant = "acme.onmicrosoft.us" };
        Assert.AreSame(MicrosoftCloud.UsGovernment, settings.EffectiveCloud);
        var message = plugin.Validate(settings).Single();
        Assert.AreEqual(nameof(SharePointSettings.ClientId), message.Property);
        StringAssert.Contains(message.Message, SharePointPlugin.ContainerPermissions);
        StringAssert.Contains(message.Message, "container type");

        settings.ClientId = OwnClientId;
        Assert.AreEqual(0, plugin.Validate(settings).Count());

        // chosen by hand, the cloud needs the tenant named: Embedded has no site to take it from
        settings = new SharePointSettings { SiteUrl = ContainerType, Cloud = MicrosoftCloud.UsGovernmentName, ClientId = OwnClientId };
        message = plugin.Validate(settings).Single();
        Assert.AreEqual(nameof(SharePointSettings.Tenant), message.Property);
        StringAssert.Contains(message.Message, "contoso.onmicrosoft.us");

        var defaults = new SharePointSettings { SiteUrl = ContainerType };
        Assert.AreSame(MicrosoftCloud.Global, defaults.EffectiveCloud);
        Assert.AreEqual(0, plugin.Validate(defaults).Count(), "the global cloud is unchanged");

        var test = plugin.TestAsync(new SharePointSettings { SiteUrl = ContainerType, Tenant = "acme.onmicrosoft.us" }, new Progress<string>(), CancellationToken.None).GetAwaiter().GetResult();
        Assert.IsFalse(test.Success);
        StringAssert.Contains(test.Message, "global cloud");
    }

    [TestMethod]
    [DataRow("https://acme.sharepoint.us/sites/Bar")]
    [DataRow("https://contoso.sharepoint.com/sites/Team")]
    public void AMicrosoft365AddressNeverTakesAWindowsPassword(string url)
    {
        var plugin = new SharePointPlugin();
        var settings = new SharePointSettings { SiteUrl = url, Username = @"ACME\someone", Password = "x" };
        Assert.AreEqual(SharePointKind.Online, settings.Kind);
        Assert.AreNotEqual(SharePointSignIn.Windows, settings.EffectiveSignIn, "Automatic never picks a Windows password for Microsoft 365");

        settings.SignInMethod = SharePointSettings.Windows;
        var message = plugin.Validate(settings).Single();
        Assert.AreEqual(nameof(SharePointSettings.SignInMethod), message.Property);
        StringAssert.Contains(message.Message, "Microsoft sign-in");

        var test = plugin.TestAsync(settings, new Progress<string>(), CancellationToken.None).GetAwaiter().GetResult();
        Assert.IsFalse(test.Success, "refused before any request, so no proxy or 401 confuses it");
        StringAssert.Contains(test.Message, "Microsoft sign-in");

        settings.SiteUrl = "http://anyfs-sp-1/sites/anyfs";
        Assert.AreEqual(SharePointKind.Server, settings.Kind);
        Assert.AreEqual(0, plugin.Validate(settings).Count(), "an on-premises address takes the Windows account");
    }

    [TestMethod]
    [DataRow("https://contoso.sharepoint.com/sites/Team", null, "contoso.sharepoint.com Team")]
    [DataRow("https://contoso.sharepoint.com/teams/Ops/Shared%20Documents/Forms/AllItems.aspx", null, "contoso.sharepoint.com Ops")]
    [DataRow("https://contoso.sharepoint.com", null, "contoso.sharepoint.com")]
    [DataRow("https://acme.sharepoint.us", null, "acme.sharepoint.us")]
    [DataRow("https://acme.sharepoint.us/", null, "acme.sharepoint.us")]
    [DataRow("acme.sharepoint.us/sites/Bar", null, "acme.sharepoint.us Bar")]
    [DataRow("http://anyfs-sp-1/sites/anyfs", null, "anyfs-sp-1 anyfs")]
    [DataRow("http://192.168.0.92/sites/anyfs/", null, "192.168.0.92 anyfs")]
    [DataRow("https://intranet.contoso.local:8443/dept/hr", null, "intranet.contoso.local hr")]
    [DataRow("https://[fe80::1]:8443/sites/x", null, "[fe80--1] x")]
    [DataRow(ContainerType, null, "Embedded")]
    [DataRow(ContainerType, "anyfs-test", "anyfs-test")]
    [DataRow("", null, null)]
    [DataRow("ftp://files", null, null)]
    public void TheNameComesFromTheAddress(string address, string? library, string? name)
    {
        var suggested = new SharePointPlugin().SuggestName(new SharePointSettings { SiteUrl = address, Library = library });
        Assert.AreEqual(name, suggested);
        if (suggested is not null)
        {
            Assert.IsTrue(VfsPath.IsValidSegment(suggested, out var reason), $"the default path is made from it: {reason}");
        }

        var site = typeof(SharePointSettings).GetProperty(nameof(SharePointSettings.SiteUrl))!.GetCustomAttribute<SettingAttribute>()!;
        Assert.IsTrue(site.Primary, "the Add source dialog asks the address first, above the name");
    }
}