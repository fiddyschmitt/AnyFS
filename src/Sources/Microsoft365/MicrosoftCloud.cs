using System.Text.Json;
using AnyFS.Sdk;
using Microsoft.Identity.Client;

namespace AnyFS.Sources.Microsoft365;

/// <summary>
/// The Microsoft cloud a Microsoft 365 tenant lives in (spec 7.3): where its accounts sign in, where its Microsoft
/// Graph and its Outlook on the web answer, and which domains its SharePoint sites and tenants are under. The clouds do not mix: an account
/// signs in only at its own cloud's endpoint, an app registration exists only in the cloud it was made in (so
/// AnyFS's own registration serves the global cloud alone), and a token is good only for its cloud's Graph. GCC
/// ("moderate") tenants are in the global cloud; GCC High is the US Government cloud, Microsoft's "L4". DoD and the
/// China cloud are not offered: nobody has asked, and they can't be tried from here.
/// </summary>
public sealed class MicrosoftCloud
{
    /// <summary>The settings choice that goes by the site's address or the tenant's domain.</summary>
    public const string AutomaticName = "Automatic";

    public const string GlobalName = "Global";

    public const string UsGovernmentName = "US Government (GCC High)";

    public static readonly MicrosoftCloud Global = new(GlobalName, AzureCloudInstance.AzurePublic, "login.microsoftonline.com", "graph.microsoft.com", "outlook.office.com", "sharepoint.com", "onmicrosoft.com");

    public static readonly MicrosoftCloud UsGovernment = new(UsGovernmentName, AzureCloudInstance.AzureUsGovernment, "login.microsoftonline.us", "graph.microsoft.us", "outlook.office365.us", "sharepoint.us", "onmicrosoft.us");

    private static readonly MicrosoftCloud[] All = [Global, UsGovernment];

    /// <summary>The identity platform's own scopes, which every cloud takes as they are.</summary>
    private static readonly HashSet<string> PlatformScopes = new(StringComparer.OrdinalIgnoreCase) { "openid", "profile", "offline_access", "email" };

    private MicrosoftCloud(string name, AzureCloudInstance instance, string loginHost, string graphHost, string outlookHost, string sharePointDomain, string tenantDomain)
    {
        Name = name;
        Instance = instance;
        LoginHost = loginHost;
        GraphHost = graphHost;
        OutlookHost = outlookHost;
        SharePointDomain = sharePointDomain;
        TenantDomain = tenantDomain;
    }

    /// <summary>The name the settings show and store.</summary>
    public string Name { get; }

    /// <summary>The sign-in endpoint's host, login.microsoftonline.us for the US Government cloud.</summary>
    public string LoginHost { get; }

    /// <summary>Microsoft Graph's host, graph.microsoft.us for the US Government cloud.</summary>
    public string GraphHost { get; }

    /// <summary>Outlook on the web's host, outlook.office365.us for the US Government cloud (the web session's mailbox store).</summary>
    public string OutlookHost { get; }

    /// <summary>The domain the cloud's SharePoint sites are under: contoso.sharepoint.us.</summary>
    public string SharePointDomain { get; }

    /// <summary>The domain of the cloud's initial tenant domains: contoso.onmicrosoft.us.</summary>
    public string TenantDomain { get; }

    public bool IsGlobal => ReferenceEquals(this, Global);

    /// <summary>Whether AnyFS's own app registration exists here; anywhere else the organisation's own client ID is needed.</summary>
    public bool HasShippedRegistration => IsGlobal;

    /// <summary>Microsoft Graph's v1.0 root in this cloud, with the trailing slash.</summary>
    public string GraphBaseUrl => $"https://{GraphHost}/v1.0/";

    /// <summary>Outlook on the web's origin in this cloud: https://outlook.office.com.</summary>
    public Uri OutlookOrigin => new($"https://{OutlookHost}");

    internal AzureCloudInstance Instance { get; }

    /// <summary>
    /// The scopes to ask the identity platform for. A bare Graph scope name means the global Graph, so in any other
    /// cloud each is qualified with that cloud's Graph: https://graph.microsoft.us/Files.ReadWrite.All. (The US
    /// Government endpoint was checked to issue a device code for scopes in that form, 18-Oct-2026.)
    /// </summary>
    public IReadOnlyList<string> QualifyScopes(IEnumerable<string> scopes)
        => IsGlobal
            ? scopes.ToList()
            : scopes.Select(s => PlatformScopes.Contains(s) || s.Contains("://", StringComparison.Ordinal) ? s : $"https://{GraphHost}/{s}").ToList();

    /// <summary>
    /// The tenant a site belongs to, by its initial domain: contoso.sharepoint.us (or contoso-my.sharepoint.us, a
    /// OneDrive) belongs to contoso.onmicrosoft.us, since a tenant's SharePoint name is its initial domain's first
    /// label. Null for a host that is not one of this cloud's SharePoint hosts.
    /// </summary>
    public string? TenantForSiteHost(string? host)
    {
        if (host is null || !IsUnder(host, SharePointDomain))
        {
            return null;
        }

        var label = host[..^(SharePointDomain.Length + 1)];
        if (label.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var suffix in new[] { "-my", "-admin" })
        {
            if (label.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                label = label[..^suffix.Length];
                break;
            }
        }

        return label.Length == 0 ? null : $"{label.ToLowerInvariant()}.{TenantDomain}";
    }

    /// <summary>The cloud whose SharePoint domain <paramref name="host"/> is under, or null.</summary>
    public static MicrosoftCloud? FromSiteHost(string? host) => host is null ? null : All.FirstOrDefault(c => IsUnder(host, c.SharePointDomain));

    /// <summary>The cloud of a tenant given by its initial domain (contoso.onmicrosoft.us); null for an id, a custom domain or organizations.</summary>
    public static MicrosoftCloud? FromTenant(string? tenant) => string.IsNullOrWhiteSpace(tenant) ? null : All.FirstOrDefault(c => IsUnder(tenant.Trim(), c.TenantDomain));

    /// <summary>The cloud a settings choice names; null for Automatic or anything not offered.</summary>
    public static MicrosoftCloud? FromName(string? name) => All.FirstOrDefault(c => string.Equals(c.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a settings value is one of the choices offered (empty counts as Automatic).</summary>
    public static bool IsChoice(string? name)
        => string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), AutomaticName, StringComparison.OrdinalIgnoreCase) || FromName(name) is not null;

    /// <summary>
    /// The cloud an account is in, by its address: its tenant's initial domain says so outright (jane@contoso.onmicrosoft.us),
    /// else Microsoft's public realm lookup (GetUserRealm, no sign-in needed; an account whose cloud instance is
    /// microsoftonline.us is in the US Government cloud); the global cloud when the lookup says nothing or cannot be made.
    /// </summary>
    /// <param name="handler">The handler the lookup goes through: a test's; null for the network.</param>
    public static async Task<MicrosoftCloud> FindAsync(string address, HttpMessageHandler? handler, CancellationToken ct)
    {
        if (FromTenant(address[(address.LastIndexOf('@') + 1)..]) is { } known)
        {
            return known;
        }

        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
            http.Timeout = TimeSpan.FromSeconds(15);
            using var response = await http.GetAsync($"https://{Global.LoginHost}/GetUserRealm.srf?login={Uri.EscapeDataString(address)}&json=1", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Global;
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var instance = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("CloudInstanceName", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            return instance is not null && instance.EndsWith("microsoftonline.us", StringComparison.OrdinalIgnoreCase) ? UsGovernment : Global;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Global;
        }
    }

    /// <summary>The cloud to sign in to: the one chosen, else the one the site's address or the tenant's domain is in, else the global cloud.</summary>
    public static MicrosoftCloud Resolve(string? choice, string? siteHost, string? tenant)
        => FromName(choice) ?? FromSiteHost(siteHost) ?? FromTenant(tenant) ?? Global;

    public override string ToString() => Name;

    private static bool IsUnder(string host, string domain) => host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The checks an app sign-in's settings get: the tenant and the client ID as written, then the cloud, the tenant and the
/// client ID together, for a SharePoint Online site, SharePoint Embedded's containers or an Exchange Online mailbox. The
/// messages point at the settings' properties by the names every Microsoft 365 source gives them
/// (<see cref="CloudProperty"/>, <see cref="TenantProperty"/>, <see cref="ClientIdProperty"/>).
/// </summary>
public static class CloudSettings
{
    public const string CloudProperty = "Cloud";

    public const string TenantProperty = "Tenant";

    public const string ClientIdProperty = "ClientId";

    /// <param name="choice">The Cloud setting as stored.</param>
    /// <param name="cloud">The cloud the source will sign in to.</param>
    /// <param name="tenant">The Tenant setting as stored.</param>
    /// <param name="clientId">The client ID the sign-in goes as: the setting, or AnyFS's own when it is empty.</param>
    /// <param name="siteHost">The site's host, for a SharePoint Online site; null otherwise.</param>
    /// <param name="permissions">The delegated Microsoft Graph permissions an organisation's own registration needs, as the message lists them.</param>
    /// <param name="tenantRequired">Whether a cloud other than the global one needs the tenant named (Embedded, which has no site or address to take it from).</param>
    /// <param name="extra">What else the registration needs, after its permissions (", and registers it on the container type").</param>
    public static IEnumerable<ValidationMessage> Check(string? choice, MicrosoftCloud cloud, string? tenant, string clientId, string? siteHost, string permissions, bool tenantRequired, string extra = "")
    {
        var written = string.IsNullOrWhiteSpace(tenant) ? "organizations" : tenant.Trim();
        if (written.Any(char.IsWhiteSpace) || written.Contains('/'))
        {
            yield return new ValidationMessage("The tenant is organizations, a domain like contoso.onmicrosoft.com, or a tenant id.", TenantProperty);
        }

        if (!Guid.TryParse(clientId, out _))
        {
            yield return new ValidationMessage("The client ID is the app registration's Application (client) ID, a GUID.", ClientIdProperty);
        }

        if (!MicrosoftCloud.IsChoice(choice))
        {
            yield return new ValidationMessage($"The cloud is {MicrosoftCloud.AutomaticName}, {MicrosoftCloud.GlobalName} or {MicrosoftCloud.UsGovernmentName}.", CloudProperty);
            yield break;
        }

        if (MicrosoftCloud.FromName(choice) is { } chosen && MicrosoftCloud.FromSiteHost(siteHost) is { } siteCloud && !ReferenceEquals(chosen, siteCloud))
        {
            yield return new ValidationMessage($"{siteHost} is a site in the {siteCloud.Name} cloud: choose {MicrosoftCloud.AutomaticName} or {siteCloud.Name}.", CloudProperty);
        }

        if (MicrosoftCloud.FromTenant(tenant) is { } tenantCloud && !ReferenceEquals(tenantCloud, cloud))
        {
            yield return new ValidationMessage($"{tenant!.Trim()} is a tenant in the {tenantCloud.Name} cloud, not the {cloud.Name} cloud this source signs in to.", TenantProperty);
        }
        else if (tenantRequired && !cloud.IsGlobal && (string.IsNullOrWhiteSpace(tenant) || string.Equals(tenant.Trim(), "organizations", StringComparison.OrdinalIgnoreCase)))
        {
            yield return new ValidationMessage($"In the {cloud.Name} cloud, give your tenant: its domain, like contoso.{cloud.TenantDomain}, or its id.", TenantProperty);
        }

        if (NeedsOwnRegistration(cloud, clientId))
        {
            yield return new ValidationMessage(OwnRegistrationNeeded(cloud, permissions, extra), ClientIdProperty);
        }
    }

    /// <summary>Whether the client ID is AnyFS's own in a cloud where it does not exist.</summary>
    public static bool NeedsOwnRegistration(MicrosoftCloud cloud, string clientId)
        => !cloud.HasShippedRegistration && string.Equals(clientId, EntraSignIn.DefaultClientId, StringComparison.OrdinalIgnoreCase);

    public static string OwnRegistrationNeeded(MicrosoftCloud cloud, string permissions, string extra = "")
        => $"AnyFS's own app registration exists only in Microsoft's global cloud. In the {cloud.Name} cloud, an administrator registers an app in your tenant " +
           "(a public client: the Mobile and desktop applications platform with the redirect URI http://localhost, and Allow public client flows on) " +
           $"with the delegated Microsoft Graph permissions {permissions}, consented for the organisation{extra}. Its Application (client) ID goes in Client ID.";
}
