using System.Net;
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

        var global = new EntraSignIn(Options(SharePointOnlinePlugin.DefaultClientId, "organizations", null), NullLogger.Instance);
        CollectionAssert.AreEqual(EntraSignIn.SiteScopes, global.Scopes.ToArray());
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

/// <summary>The Cloud setting of the Online and Embedded sources.</summary>
[TestClass]
public class SharePointCloudSettingsTests
{
    private const string OwnClientId = "11111111-2222-3333-4444-555555555555";

    [TestMethod]
    public void AUsGovernmentSiteSignsInThereToItsOwnTenant()
    {
        var settings = new SharePointOnlineSettings { SiteUrl = "https://acme.sharepoint.us/sites/Bar", ClientId = OwnClientId };
        Assert.AreSame(MicrosoftCloud.UsGovernment, settings.EffectiveCloud);
        Assert.AreEqual("acme.onmicrosoft.us", settings.EffectiveTenant, "an organisation's own registration is often for its own tenant only");

        settings.Tenant = "acme.gov";
        Assert.AreEqual("acme.gov", settings.EffectiveTenant, "a tenant given is kept");

        var global = new SharePointOnlineSettings { SiteUrl = "https://contoso.sharepoint.com/sites/Team" };
        Assert.AreSame(MicrosoftCloud.Global, global.EffectiveCloud);
        Assert.AreEqual("organizations", global.EffectiveTenant, "the global cloud keeps signing in as before");
    }

    [TestMethod]
    public void AUsGovernmentSiteNeedsTheOrganisationsOwnRegistration()
    {
        var plugin = new SharePointOnlinePlugin();
        var settings = new SharePointOnlineSettings { SiteUrl = "https://acme.sharepoint.us/sites/Bar" };
        var message = plugin.Validate(settings).Single();
        Assert.AreEqual(nameof(SharePointOnlineSettings.ClientId), message.Property);
        StringAssert.Contains(message.Message, "global cloud");
        StringAssert.Contains(message.Message, "http://localhost");
        StringAssert.Contains(message.Message, SharePointOnlinePlugin.RegistrationPermissions);

        settings.ClientId = OwnClientId;
        Assert.AreEqual(0, plugin.Validate(settings).Count());

        var test = plugin.TestAsync(new SharePointOnlineSettings { SiteUrl = "https://acme.sharepoint.us/sites/Bar" }, new Progress<string>(), CancellationToken.None).GetAwaiter().GetResult();
        Assert.IsFalse(test.Success);
        StringAssert.Contains(test.Message, "global cloud", "the Test button says so before any sign-in");
    }

    [TestMethod]
    public void ACloudChoiceThatContradictsTheSiteOrTenantIsCaught()
    {
        var plugin = new SharePointOnlinePlugin();
        var settings = new SharePointOnlineSettings { SiteUrl = "https://acme.sharepoint.us/sites/Bar", ClientId = OwnClientId, Cloud = MicrosoftCloud.GlobalName };
        var message = plugin.Validate(settings).Single();
        Assert.AreEqual(nameof(SharePointOnlineSettings.Cloud), message.Property);
        StringAssert.Contains(message.Message, "acme.sharepoint.us is a site in the US Government (GCC High) cloud");

        settings.Cloud = "Mars";
        Assert.AreEqual(nameof(SharePointOnlineSettings.Cloud), plugin.Validate(settings).Single().Property);

        settings.Cloud = MicrosoftCloud.AutomaticName;
        settings.Tenant = "contoso.onmicrosoft.com";
        message = plugin.Validate(settings).Single();
        Assert.AreEqual(nameof(SharePointOnlineSettings.Tenant), message.Property);
        StringAssert.Contains(message.Message, "contoso.onmicrosoft.com is a tenant in the Global cloud");
    }

    [TestMethod]
    public void EmbeddedGoesByTheTenantsDomain()
    {
        var plugin = new SharePointEmbeddedPlugin();
        var settings = new SharePointEmbeddedSettings { ContainerTypeId = "91710488-5756-407f-9046-fbe5f0b4de73", Tenant = "acme.onmicrosoft.us" };
        Assert.AreSame(MicrosoftCloud.UsGovernment, settings.EffectiveCloud);
        var message = plugin.Validate(settings).Single();
        Assert.AreEqual(nameof(SharePointEmbeddedSettings.ClientId), message.Property);
        StringAssert.Contains(message.Message, SharePointEmbeddedPlugin.RegistrationPermissions);
        StringAssert.Contains(message.Message, "container type");

        settings.ClientId = OwnClientId;
        Assert.AreEqual(0, plugin.Validate(settings).Count());

        // chosen by hand, the cloud needs the tenant named: Embedded has no site to take it from
        settings = new SharePointEmbeddedSettings { ContainerTypeId = "91710488-5756-407f-9046-fbe5f0b4de73", Cloud = MicrosoftCloud.UsGovernmentName, ClientId = OwnClientId };
        message = plugin.Validate(settings).Single();
        Assert.AreEqual(nameof(SharePointEmbeddedSettings.Tenant), message.Property);
        StringAssert.Contains(message.Message, "contoso.onmicrosoft.us");

        var defaults = new SharePointEmbeddedSettings { ContainerTypeId = "91710488-5756-407f-9046-fbe5f0b4de73" };
        Assert.AreSame(MicrosoftCloud.Global, defaults.EffectiveCloud);
        Assert.AreEqual(0, plugin.Validate(defaults).Count(), "the global cloud is unchanged");

        var test = plugin.TestAsync(new SharePointEmbeddedSettings { ContainerTypeId = "91710488-5756-407f-9046-fbe5f0b4de73", Tenant = "acme.onmicrosoft.us" }, new Progress<string>(), CancellationToken.None).GetAwaiter().GetResult();
        Assert.IsFalse(test.Success);
        StringAssert.Contains(test.Message, "global cloud");
    }
}
