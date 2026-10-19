using System.Diagnostics.CodeAnalysis;
using System.Net;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.SharePoint;

/// <summary>What a SharePoint address is, deduced from it, never asked (spec 7.3).</summary>
internal enum SharePointKind
{
    /// <summary>Microsoft 365: a site under .sharepoint.com, or .sharepoint.us in the US Government cloud.</summary>
    Online,

    /// <summary>On-premises: any other address, by name or by IP, reached with a Windows account.</summary>
    Server,

    /// <summary>SharePoint Embedded: no site, so the container type's id stands in for the address.</summary>
    Embedded,
}

/// <summary>How a SharePoint source signs in once Automatic has been resolved for its address.</summary>
internal enum SharePointSignIn
{
    /// <summary>A Microsoft sign-in as a registered app (AnyFS's own, or the organisation's), in the browser; then Microsoft Graph.</summary>
    AppBrowser,

    /// <summary>The same with a device code, for a machine without a browser.</summary>
    AppDeviceCode,

    /// <summary>The person signs in in a browser AnyFS opens; its session cookies then go to SharePoint's REST API. No app registration.</summary>
    WebSession,

    /// <summary>A Windows account over NTLM or Kerberos, to SharePoint's REST API on the person's own network.</summary>
    Windows,
}

public sealed class SharePointSettings
{
    public const string Automatic = "Automatic";
    public const string AppBrowser = "App sign-in, browser";
    public const string AppDeviceCode = "App sign-in, device code";
    public const string WebSession = "Web session, no app registration";
    public const string WindowsCurrent = "Windows account, as yourself";
    public const string Windows = "Windows account";

    [Setting("Site URLs (one per line)", Required = true, Primary = true, Order = 0, Kind = SettingKind.Multiline, Placeholder = "https://contoso.sharepoint.com/sites/Team\nhttps://contoso.sharepoint.com/sites/Sales",
        Hint = "The site's address: SharePoint Online (contoso.sharepoint.com, or .sharepoint.us in GCC High) or SharePoint Server on your own network (http://intranet/sites/Team). A subsite's address, or a library or page pasted after the site, is fine. Several sites, one per line, each show as a folder of their own, under the one sign-in. For SharePoint Embedded, the container type's id instead.")]
    public string SiteUrl { get; set; } = "";

    [Setting("Library", Order = 1, Placeholder = "Documents",
        Hint = "Leave empty to show every document library as a top-level folder (the site's pages among them, as Markdown), and the site's subsites in a Subsites folder beside them (at the tenant's root site also a Sites folder with every other site you can open), or name one library to serve just that, with one site only. For SharePoint Embedded, a container, by its name or its id.")]
    public string? Library { get; set; }

    [Setting("Sign in with", Order = 2, Group = "Sign-in", Kind = SettingKind.Choice, Choices = [Automatic, AppBrowser, AppDeviceCode, WebSession, WindowsCurrent, Windows],
        Hint = "Automatic picks for the address: a Windows account on your own network; for Microsoft 365, app sign-in where AnyFS's app (or your organisation's, under Advanced) can be used, the web session otherwise. App sign-in: AnyFS signs in as a registered app; an administrator may have to approve it once. Web session: you sign in in a browser window and AnyFS uses that session, for when no app registration is allowed. Windows account, as yourself: SharePoint Server, as you are signed in to Windows (a smartcard or Windows Hello sign-in too), with nothing to type. Windows account: SharePoint Server, with a user name and password.")]
    public string SignInMethod { get; set; } = Automatic;

    [Setting("Account", Order = 3, Group = "Sign-in", Placeholder = "name@contoso.com",
        Hint = "The work or school account to sign in with. Optional: it pre-fills the sign-in page and picks the saved sign-in.")]
    public string? Account { get; set; }

    [Setting("Username", Order = 4, Group = "Sign-in", Placeholder = @"CONTOSO\name or name@contoso.local",
        Hint = "A Windows account the server knows. Left empty, AnyFS signs in as you are signed in to Windows, as Windows account, as yourself does (Windows only).")]
    public string? Username { get; set; }

    [Setting("Password", Secret = true, Order = 5, Group = "Sign-in")]
    public string? Password { get; set; }

    [Setting("Cloud", Advanced = true, Order = 9, Group = "Sign-in", Kind = SettingKind.Choice,
        Choices = [MicrosoftCloud.AutomaticName, MicrosoftCloud.GlobalName, MicrosoftCloud.UsGovernmentName],
        Hint = "The Microsoft cloud the tenant is in. Automatic goes by the site's address (a site under .sharepoint.us is in the US Government cloud, GCC High) or, for SharePoint Embedded, the tenant's domain (.onmicrosoft.us). The US Government cloud needs your organisation's own app registration in Client ID.")]
    public string Cloud { get; set; } = MicrosoftCloud.AutomaticName;

    [Setting("Tenant", Advanced = true, Order = 10, Group = "Sign-in",
        Hint = "organizations (any work or school account), or your tenant's domain or id when the sign-in needs steering to it. In the US Government cloud, organizations means the site's own tenant; SharePoint Embedded there needs the tenant named.")]
    public string Tenant { get; set; } = "organizations";

    [Setting("Client ID", Advanced = true, Order = 11, Group = "Sign-in",
        Hint = "The app registration AnyFS signs in as. Leave the default unless your organisation registered its own for AnyFS; in the US Government cloud it must have, since AnyFS's own exists only in the global cloud. For SharePoint Embedded, an app registered on the container type.")]
    public string ClientId { get; set; } = SharePointPlugin.DefaultClientId;

    [Setting("Browser", Advanced = true, Order = 12, Kind = SettingKind.FilePath,
        Hint = "The Edge, Chrome or Chromium to open for the web session's sign-in. Leave empty to find one automatically. AnyFS opens it with a profile of its own, never your everyday browser's.")]
    public string? BrowserPath { get; set; }

    [Setting("Trust any certificate", Advanced = true, Order = 13,
        Hint = "Accept the server's HTTPS certificate whatever it is (a lab with a self-signed one). Off for a real server.")]
    public bool TrustAnyCertificate { get; set; }

    [Setting("Connections", Advanced = true, Order = 20, Hint = "Parallel downloads per open file.")]
    public int Connections { get; set; } = 4;

    [Setting("Spool files below (MiB)", Advanced = true, Order = 21,
        Hint = "Files this small are downloaded whole when opened and kept in the cache; larger ones are read in ranges as the client asks.")]
    public int SpoolBelowMiB { get; set; } = 8;

    /// <summary>The lines of <see cref="SiteUrl"/> that hold something, trimmed: one site's address, several, or a container type's id.</summary>
    internal IReadOnlyList<string> SiteLines => (SiteUrl ?? "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

    /// <summary>The first address given, which says what the source is (<see cref="Kind"/>).</summary>
    internal string FirstSite => SiteLines.FirstOrDefault() ?? "";

    /// <summary>The sites given, each once; lines that are no site's address are left out (validation names them).</summary>
    internal IReadOnlyList<SiteAddress> Sites
        => SiteLines.Select(line => SiteAddress.TryParse(line, out var site, out _) ? site : null).OfType<SiteAddress>().DistinctBy(site => site.Url, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Several sites were given: each shows as a folder of its own at the source's root.</summary>
    internal bool IsMultiSite => Kind is SharePointKind.Online or SharePointKind.Server && Sites.Count > 1;

    /// <summary>What the address is: a container type's id is Embedded, a Microsoft 365 host is Online, anything else that parses is Server; null while it is neither. The first address decides.</summary>
    internal SharePointKind? Kind => KindOf(FirstSite);

    internal static SharePointKind? KindOf(string? address)
    {
        var text = (address ?? "").Trim();
        if (Guid.TryParse(text, out _))
        {
            return SharePointKind.Embedded;
        }

        if (!SiteAddress.TryParse(text, out var site, out _))
        {
            return null;
        }

        return MicrosoftCloud.FromSiteHost(site.Host) is null ? SharePointKind.Server : SharePointKind.Online;
    }

    /// <summary>Whether the sign-in is left to <see cref="EffectiveSignIn"/> to pick.</summary>
    internal bool IsAutomatic => SignInFrom(SignInMethod) is null;

    /// <summary>
    /// The sign-in in use: the one chosen, else the one the address calls for. Online: app sign-in when AnyFS's
    /// registration exists in the site's cloud or a client ID of the organisation's own is given, the web session
    /// otherwise (the US Government cloud with no registration). Server: a Windows account. Embedded: app sign-in.
    /// </summary>
    internal SharePointSignIn EffectiveSignIn => SignInFrom(SignInMethod) ?? Kind switch
    {
        SharePointKind.Server => SharePointSignIn.Windows,
        SharePointKind.Online when !EffectiveCloud.HasShippedRegistration && IsShippedClientId => SharePointSignIn.WebSession,
        _ => SharePointSignIn.AppBrowser,
    };

    /// <summary>The Microsoft sign-in flavour for <see cref="EntraSignIn"/>: a device code when chosen, else the browser.</summary>
    internal SignInMethod Method => EffectiveSignIn == SharePointSignIn.AppDeviceCode ? SharePoint.SignInMethod.DeviceCode : SharePoint.SignInMethod.Browser;

    internal string? PinnedLibrary => string.IsNullOrWhiteSpace(Library) ? null : Library.Trim();

    /// <summary>Whether the one address given is the tenant's root site (no path after the host), where the tenant's other sites show.</summary>
    internal bool IsRootSite => !IsMultiSite && Kind is SharePointKind.Online or SharePointKind.Server && SiteAddress.TryParse(FirstSite, out var site, out _) && site.IsRoot;

    /// <summary>SharePoint Embedded's pinned container when it was given by id rather than by name.</summary>
    internal string? PinnedContainerId => Kind == SharePointKind.Embedded && PinnedLibrary is { } pinned && GraphDocumentStore.LooksLikeContainerId(pinned) ? pinned : null;

    internal string? LoginHint => string.IsNullOrWhiteSpace(Account) ? null : Account.Trim();

    internal string? Browser => string.IsNullOrWhiteSpace(BrowserPath) ? null : BrowserPath.Trim();

    internal string EffectiveClientId => string.IsNullOrWhiteSpace(ClientId) ? SharePointPlugin.DefaultClientId : ClientId.Trim();

    /// <summary>The cloud to sign in to: the one chosen, else the site's (a .sharepoint.us site is US Government), else the tenant's.</summary>
    internal MicrosoftCloud EffectiveCloud => MicrosoftCloud.Resolve(Cloud, Kind == SharePointKind.Online ? SiteHost : null, Tenant);

    /// <summary>
    /// The tenant the Microsoft sign-in goes to. For a site outside the global cloud, organizations becomes the
    /// site's own tenant (contoso.sharepoint.us is contoso.onmicrosoft.us): an organisation's own registration there
    /// is usually for its own tenant only, which the organizations endpoint refuses (AADSTS50194).
    /// </summary>
    internal string EffectiveTenant
    {
        get
        {
            var tenant = string.IsNullOrWhiteSpace(Tenant) ? "organizations" : Tenant.Trim();
            var cloud = EffectiveCloud;
            if (Kind == SharePointKind.Online && !cloud.IsGlobal && string.Equals(tenant, "organizations", StringComparison.OrdinalIgnoreCase) && cloud.TenantForSiteHost(SiteHost) is { } siteTenant)
            {
                return siteTenant;
            }

            return tenant;
        }
    }

    /// <summary>Windows account, as yourself was chosen: the user name and password are not asked for, or used.</summary>
    internal bool IsWindowsCurrent => string.Equals((SignInMethod ?? "").Trim(), WindowsCurrent, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Windows sign-in AnyFS runs as, not a user name and password: chosen outright, or no user name given.</summary>
    internal bool UsesCurrentLogon => IsWindowsCurrent || string.IsNullOrWhiteSpace(Username);

    /// <summary>The Windows account a Windows sign-in goes as, for messages.</summary>
    internal string WindowsAccount => UsesCurrentLogon ? $@"{Environment.UserDomainName}\{Environment.UserName} (your Windows sign-in)" : Username!.Trim();

    internal bool TryGetContainerType(out Guid id) => Guid.TryParse(FirstSite, out id);

    /// <summary>The credentials for the Windows sign-in: DOMAIN\name is split, name@domain is passed as a user principal name, none means the account AnyFS runs as.</summary>
    internal ICredentials Credentials()
    {
        if (UsesCurrentLogon)
        {
            return CredentialCache.DefaultNetworkCredentials;
        }

        var user = Username!.Trim();
        var slash = user.IndexOf('\\');
        return slash > 0
            ? new NetworkCredential(user[(slash + 1)..], Password ?? "", user[..slash])
            : new NetworkCredential(user, Password ?? "");
    }

    private bool IsShippedClientId => string.Equals(EffectiveClientId, SharePointPlugin.DefaultClientId, StringComparison.OrdinalIgnoreCase);

    private string? SiteHost => SiteAddress.TryParse(FirstSite, out var site, out _) ? site.Host : null;

    /// <summary>The sign-in a choice names, or null for Automatic (and anything not offered). "Browser" and "Device code" are the earlier values.</summary>
    internal static SharePointSignIn? SignInFrom(string? choice) => (choice ?? "").Trim() switch
    {
        var c when c.Equals(AppBrowser, StringComparison.OrdinalIgnoreCase) || c.Equals("Browser", StringComparison.OrdinalIgnoreCase) => SharePointSignIn.AppBrowser,
        var c when c.Equals(AppDeviceCode, StringComparison.OrdinalIgnoreCase) || c.Equals("Device code", StringComparison.OrdinalIgnoreCase) => SharePointSignIn.AppDeviceCode,
        var c when c.Equals(WebSession, StringComparison.OrdinalIgnoreCase) => SharePointSignIn.WebSession,
        var c when c.Equals(Windows, StringComparison.OrdinalIgnoreCase) || c.Equals(WindowsCurrent, StringComparison.OrdinalIgnoreCase) => SharePointSignIn.Windows,
        _ => null,
    };
}

/// <summary>
/// SharePoint (type <c>sharepoint</c>, folder <c>sharepoint</c>, spec 7.3): one source for every kind, told apart by
/// the address given. SharePoint Online and its US Government cloud through Microsoft Graph after an app sign-in,
/// or through SharePoint's REST API on a browser session for people whose organisation will not register an app;
/// SharePoint Server on the person's own network through the REST API with a Windows account; SharePoint Embedded's
/// containers through Graph, the container type's id standing in for the address. Every kind shares one tree,
/// cache, spool and write path (<see cref="SharePointSource"/>); the form shows only the fields the address and
/// the sign-in call for.
/// </summary>
public sealed class SharePointPlugin : ISourcePlugin, IValidatable, ITestable, ISettingVisibility
{
    /// <summary>The AnyFS app registration (provisioning/sharepoint/README.md): a multi-tenant public client with delegated Graph scopes, in the global cloud only.</summary>
    public const string DefaultClientId = "824dc5b9-f3c4-42b7-871d-6f0de8bc0a4a";

    /// <summary>The delegated Graph permissions an organisation's own registration needs for a site (<see cref="EntraSignIn.SiteScopes"/>), as messages list them.</summary>
    internal const string SitePermissions = "User.Read, Files.ReadWrite.All and Sites.ReadWrite.All";

    /// <summary>The same for SharePoint Embedded (<see cref="EntraSignIn.ContainerScopes"/>).</summary>
    internal const string ContainerPermissions = "User.Read and FileStorageContainer.Selected";

    /// <summary>What an organisation's own registration for SharePoint Embedded needs besides its permissions.</summary>
    internal const string ContainerRegistrationExtra = ", and registers it on the container type";

    public string TypeId => "sharepoint";

    public string DisplayName => "SharePoint";

    public string Description => "A SharePoint site's document libraries, subsites and pages (as Markdown), or several sites' in one source: SharePoint Online (Microsoft 365, GCC High too), SharePoint Server on your own network, or SharePoint Embedded's containers, told apart by the address. Reads, writes, renames.";

    public string TypeFolder => "sharepoint";

    public Stream? OpenIcon() => null;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(SharePointSettings);

    public object CreateDefaultSettings() => new SharePointSettings();

    public ISource CreateSource(object settings) => new SharePointSiteSource((SharePointSettings)settings);

    /// <summary>
    /// A new source's name from its address: the host as given and the site after it ("contoso.sharepoint.com Team",
    /// "intranet Team"; a root site is the host alone, "acme.sharepoint.us"), without a port, and for SharePoint
    /// Embedded the pinned container, else "Embedded". Several sites are named by the server they share (its root
    /// site's name), and get no name when they share none. Null until the address parses.
    /// </summary>
    public string? SuggestName(object settings)
    {
        var s = (SharePointSettings)settings;
        if (s.Kind is not { } kind)
        {
            return null;
        }

        if (kind == SharePointKind.Embedded)
        {
            return SafeName(s.PinnedLibrary ?? "Embedded");
        }

        if (s.IsMultiSite)
        {
            var sites = s.Sites;
            return sites.Select(site => (site.Scheme, site.Host)).Distinct().Count() == 1 ? SafeName(HostName(sites[0])) : null;
        }

        SiteAddress.TryParse(s.FirstSite, out var one, out _);
        var siteName = one.ServerRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return SafeName(siteName is null ? HostName(one) : $"{HostName(one)} {siteName}");
    }

    /// <summary>A site's host as a name: without its port.</summary>
    private static string HostName(SiteAddress site) => site.Host.StartsWith('[') ? site.Host[..(site.Host.IndexOf(']') + 1)] : site.Host.Split(':')[0];

    /// <summary>A name that is also a valid path segment: what no file system takes becomes a dash, and no leading or trailing dot or space.</summary>
    private static string? SafeName(string name)
    {
        var safe = string.Concat(name.Select(c => c < ' ' || "\\/:*?\"<>|".Contains(c) ? '-' : c)).Trim().Trim('.').Trim();
        return safe.Length == 0 ? null : safe;
    }

    /// <summary>
    /// The browser profile that keeps a web session's sign-in, one per site host, shared by every source for that
    /// site and by the Test button. It lives in the user's app data rather than the portable settings because a
    /// browser profile is bound to the machine; <c>ANYFS_SHAREPOINT_PROFILES</c> moves the root (tests).
    /// </summary>
    internal static string ProfileDir(string host)
    {
        var root = Environment.GetEnvironmentVariable("ANYFS_SHAREPOINT_PROFILES");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnyFS", "sharepoint-browser");
        }

        return Path.Combine(root, host);
    }

    /// <summary>The web session's message when there is no browser to sign in with at all.</summary>
    internal const string NoBrowser = "No Edge, Chrome, Chromium or Firefox was found for the web session's sign-in. Install one, or set the Browser path under Advanced.";

    /// <summary>
    /// The answer when SharePoint takes the Windows sign-in but refuses the account the site (its "Access denied", not a
    /// refused sign-in): who AnyFS went as, what SharePoint said, and at the server's root what that costs.
    /// </summary>
    internal static string Denied(string who, VfsException ex, bool rootSite)
        => $"Signed in as {who}, but {ex.Message.TrimEnd('.')}." + (rootSite
            ? " Everything AnyFS shows at the server's root (the root site's libraries, its Subsites, the other sites in Sites) is listed through the root site, so give the address of a site this account may open instead."
            : "");

    /// <summary>Why a sign-in cannot serve the kind of address, or null when it can.</summary>
    internal static string? Mismatch(SharePointKind kind, SharePointSignIn signIn) => (kind, signIn) switch
    {
        (SharePointKind.Online, SharePointSignIn.Windows) => "SharePoint Online takes a Microsoft sign-in, not a Windows password: choose Automatic, an app sign-in or the web session.",
        (SharePointKind.Server, not SharePointSignIn.Windows) => "SharePoint Server on your own network signs in with a Windows account: choose Automatic, Windows account, as yourself (as you are signed in to Windows, a smartcard too), or Windows account (a user name and password). (An address under .sharepoint.com or .sharepoint.us is SharePoint Online.)",
        (SharePointKind.Embedded, SharePointSignIn.WebSession or SharePointSignIn.Windows) => "SharePoint Embedded's containers take an app sign-in: choose Automatic or one of the app sign-ins.",
        _ => null,
    };

    public bool IsVisible(object settings, string propertyName)
    {
        var s = (SharePointSettings)settings;

        // nothing sign-in specific until the address says what it is, unless a sign-in was chosen outright
        SharePointSignIn? signIn = s.Kind is null && s.IsAutomatic ? null : s.EffectiveSignIn;
        var app = signIn is SharePointSignIn.AppBrowser or SharePointSignIn.AppDeviceCode;
        return propertyName switch
        {
            nameof(SharePointSettings.Account) => app,

            // under Automatic these decide which sign-in an Online site gets (a client ID of the organisation's own
            // turns the US Government cloud's web session into an app sign-in), so they stay in sight
            nameof(SharePointSettings.Cloud) or nameof(SharePointSettings.Tenant) or nameof(SharePointSettings.ClientId)
                => app || (s.IsAutomatic && s.Kind == SharePointKind.Online),
            nameof(SharePointSettings.Username) or nameof(SharePointSettings.Password) => signIn is SharePointSignIn.Windows && !s.IsWindowsCurrent,
            nameof(SharePointSettings.TrustAnyCertificate) => signIn is SharePointSignIn.Windows,
            nameof(SharePointSettings.BrowserPath) => signIn is SharePointSignIn.WebSession,
            _ => true,
        };
    }

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (SharePointSettings)settings;
        if (s.Kind is not { } kind)
        {
            if (!SiteAddress.TryParse(s.FirstSite, out _, out var reason))
            {
                yield return new ValidationMessage(reason, nameof(SharePointSettings.SiteUrl));
            }

            yield break;
        }

        var lines = s.SiteLines;
        if (kind == SharePointKind.Embedded && lines.Count > 1)
        {
            yield return new ValidationMessage("SharePoint Embedded takes one container type's id.", nameof(SharePointSettings.SiteUrl));
        }

        if (kind != SharePointKind.Embedded)
        {
            for (var i = 1; i < lines.Count; i++)
            {
                if (!SiteAddress.TryParse(lines[i], out _, out var reason))
                {
                    yield return new ValidationMessage($"{lines[i]}: {reason}", nameof(SharePointSettings.SiteUrl));
                }
                else if (SharePointSettings.KindOf(lines[i]) != kind)
                {
                    yield return new ValidationMessage($"{lines[i]} is SharePoint {SharePointSettings.KindOf(lines[i])}, the first site SharePoint {kind}: the sites share one sign-in, so they are all SharePoint Online or all SharePoint Server. Add a source for the others.", nameof(SharePointSettings.SiteUrl));
                }
            }
        }

        if (s.IsMultiSite)
        {
            if (s.PinnedLibrary is not null)
            {
                yield return new ValidationMessage("With several sites each shows all its libraries; name one library only with one site.", nameof(SharePointSettings.Library));
            }

            var servers = s.Sites.Select(site => $"{site.Scheme}://{site.Host}").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (s.EffectiveSignIn == SharePointSignIn.WebSession && servers.Count > 1)
            {
                yield return new ValidationMessage($"The web session signs in to one server, and these sites are on {servers.Count} ({string.Join(", ", servers)}): add a source for each server.", nameof(SharePointSettings.SiteUrl));
            }

            if (kind == SharePointKind.Online && s.Sites.Select(site => MicrosoftCloud.FromSiteHost(site.Host)?.Name).Distinct().Count() > 1)
            {
                yield return new ValidationMessage("These sites are in different Microsoft clouds (.sharepoint.com and .sharepoint.us), which one sign-in cannot reach: add a source for each.", nameof(SharePointSettings.SiteUrl));
            }
        }

        if (!s.IsAutomatic && SharePointSettings.SignInFrom(s.SignInMethod) is null)
        {
            yield return new ValidationMessage($"Sign in with one of: {SharePointSettings.Automatic}, {SharePointSettings.AppBrowser}, {SharePointSettings.AppDeviceCode}, {SharePointSettings.WebSession}, {SharePointSettings.WindowsCurrent}, {SharePointSettings.Windows}.", nameof(SharePointSettings.SignInMethod));
        }

        var signIn = s.EffectiveSignIn;
        if (Mismatch(kind, signIn) is { } mismatch)
        {
            yield return new ValidationMessage(mismatch, nameof(SharePointSettings.SignInMethod));
        }

        if (s.PinnedLibrary is { } library)
        {
            if (library.Contains('/') || library.Contains('\\'))
            {
                yield return new ValidationMessage(kind == SharePointKind.Embedded ? "The container is a name, as it shows in the application, or an id; not a path." : "The library is a name, like Documents, not a path.", nameof(SharePointSettings.Library));
            }
            else if (kind != SharePointKind.Embedded && SharePointNames.Problem(library) is { } problem)
            {
                yield return new ValidationMessage(problem, nameof(SharePointSettings.Library));
            }
        }

        if (signIn is SharePointSignIn.AppBrowser or SharePointSignIn.AppDeviceCode)
        {
            if (s.LoginHint is { } account && (!account.Contains('@') || account.Any(char.IsWhiteSpace)))
            {
                yield return new ValidationMessage("The account is a sign-in name like name@contoso.com.", nameof(SharePointSettings.Account));
            }

            var tenant = string.IsNullOrWhiteSpace(s.Tenant) ? "organizations" : s.Tenant.Trim();
            if (tenant.Any(char.IsWhiteSpace) || tenant.Contains('/'))
            {
                yield return new ValidationMessage("The tenant is organizations, a domain like contoso.onmicrosoft.com, or a tenant id.", nameof(SharePointSettings.Tenant));
            }

            if (!Guid.TryParse(s.EffectiveClientId, out _))
            {
                yield return new ValidationMessage("The client ID is the app registration's Application (client) ID, a GUID.", nameof(SharePointSettings.ClientId));
            }

            var siteHost = kind == SharePointKind.Online && SiteAddress.TryParse(s.FirstSite, out var site, out _) ? site.Host : null;
            var embedded = kind == SharePointKind.Embedded;
            foreach (var message in CloudSettings.Check(s.Cloud, s.EffectiveCloud, s.Tenant, s.EffectiveClientId, siteHost,
                         embedded ? ContainerPermissions : SitePermissions, tenantRequired: embedded, embedded ? ContainerRegistrationExtra : ""))
            {
                yield return message;
            }
        }

        if (signIn == SharePointSignIn.Windows)
        {
            if (s.IsWindowsCurrent)
            {
                // the user name and password are hidden and not used; one kept from an earlier choice is no error
                if (!OperatingSystem.IsWindows())
                {
                    yield return new ValidationMessage("Signing in as yourself works on Windows only: choose Windows account and give a user name and password.", nameof(SharePointSettings.SignInMethod));
                }
            }
            else if (s.UsesCurrentLogon)
            {
                if (!string.IsNullOrEmpty(s.Password))
                {
                    yield return new ValidationMessage("A password needs a user name.", nameof(SharePointSettings.Username));
                }

                if (!OperatingSystem.IsWindows())
                {
                    yield return new ValidationMessage("Give a user name and password: signing in as the current account works on Windows only.", nameof(SharePointSettings.Username));
                }
            }
            else if (s.Username!.Trim().Any(char.IsWhiteSpace))
            {
                yield return new ValidationMessage(@"The user name is DOMAIN\name or name@domain, with no spaces.", nameof(SharePointSettings.Username));
            }
        }

        if (signIn == SharePointSignIn.WebSession && s.Browser is { } browser && !File.Exists(browser))
        {
            yield return new ValidationMessage("No browser at that path; leave it empty to find Edge or Chrome automatically.", nameof(SharePointSettings.BrowserPath), IsError: false);
        }

        if (s.Connections is < 1 or > 16)
        {
            yield return new ValidationMessage("Connections must be between 1 and 16.", nameof(SharePointSettings.Connections));
        }

        if (s.SpoolBelowMiB is < 0 or > 1024)
        {
            yield return new ValidationMessage("Spool files below must be between 0 (never) and 1024 MiB.", nameof(SharePointSettings.SpoolBelowMiB));
        }
    }

    /// <summary>
    /// Signs in the way the source will (a browser window or a device code for an app sign-in or a web session, none
    /// for a Windows account) and lists what the source would show. A Microsoft sign-in is kept in memory and a web
    /// session in its browser profile, so the source started from the same dialog does not ask again.
    /// </summary>
    public async Task<TestResult> TestAsync(object settings, IProgress<string> progress, CancellationToken ct)
    {
        var s = (SharePointSettings)settings;
        if (s.Kind is not { } kind)
        {
            return new TestResult(false, SiteAddress.TryParse(s.FirstSite, out _, out var reason) ? "The address is not a SharePoint site or a container type id." : reason);
        }

        if (Mismatch(kind, s.EffectiveSignIn) is { } mismatch)
        {
            return new TestResult(false, mismatch);
        }

        try
        {
            return (kind, s.EffectiveSignIn) switch
            {
                (SharePointKind.Embedded, _) => await TestContainersAsync(s, progress, ct).ConfigureAwait(false),
                (_, SharePointSignIn.Windows) => await TestWindowsAsync(s, progress, ct).ConfigureAwait(false),
                (_, SharePointSignIn.WebSession) => await TestWebSessionAsync(s, progress, ct).ConfigureAwait(false),
                _ => await TestGraphSiteAsync(s, progress, ct).ConfigureAwait(false),
            };
        }
        catch (SignInRequiredException ex)
        {
            return new TestResult(false, ex.Message);
        }
        catch (VfsException ex)
        {
            return new TestResult(false, ex.Message);
        }
    }

    private static async Task<TestResult> TestGraphSiteAsync(SharePointSettings s, IProgress<string> progress, CancellationToken ct)
    {
        SiteAddress.TryParse(s.FirstSite, out var site, out _);
        var cloud = s.EffectiveCloud;
        if (CloudSettings.NeedsOwnRegistration(cloud, s.EffectiveClientId))
        {
            return new TestResult(false, CloudSettings.OwnRegistrationNeeded(cloud, SitePermissions));
        }

        var signIn = MicrosoftSignIn(s, EntraSignIn.SiteScopes, progress.Report, prompt: null, loadCache: () => EntraSignIn.RecentCache(s.EffectiveClientId, s.EffectiveTenant, s.LoginHint), saveCache: _ => { });
        progress.Report(cloud.IsGlobal ? "Signing in" : $"Signing in to the {cloud.Name} cloud");
        await signIn.SignInAsync(ct).ConfigureAwait(false);

        // the store is not disposed: it would dispose the client, which the using here does
        using var graph = new GraphClient((token, refresh) => signIn.GetAccessTokenAsync(token, refresh), NullLogger.Instance, cloud: cloud);
        if (s.IsMultiSite)
        {
            progress.Report($"Signed in as {signIn.Username}; looking up {s.Sites.Count} sites");
            var (sites, failed) = await GraphDocumentStore.ConnectSitesAsync(graph, s.Sites, ct).ConfigureAwait(false);
            return await SitesAsync($"Signed in as {signIn.Username}.", sites, failed, ct).ConfigureAwait(false);
        }

        progress.Report($"Signed in as {signIn.Username}; looking up {site.Url}");
        var notFound = new TestResult(false, $"Signed in as {signIn.Username}, but {site.Url} was not found, or this account has no access to it.");
        try
        {
            var store = await GraphDocumentStore.ConnectAsync(graph, site, ct).ConfigureAwait(false);
            return store is null ? notFound : await LibrariesAsync($"Signed in as {signIn.Username}. {store.Description}", s.PinnedLibrary, store, s.IsRootSite, ct).ConfigureAwait(false);
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            return notFound;
        }
    }

    private static async Task<TestResult> TestWebSessionAsync(SharePointSettings s, IProgress<string> progress, CancellationToken ct)
    {
        SiteAddress.TryParse(s.FirstSite, out var site, out _);
        var siteUri = new Uri(site.Url);
        var session = new BrowserSession(BrowserLocator.Find(s.Browser), ProfileDir(site.Host), NullLogger.Instance);
        if (!session.CanSignIn)
        {
            return new TestResult(false, NoBrowser);
        }

        var cookies = await session.SignInAsync(siteUri, interactive: true, progress.Report, ct).ConfigureAwait(false);
        using var handler = new BrowserSessionHandler(cookies, siteUri, c => session.SignInAsync(siteUri, interactive: false, progress.Report, c), NullLogger.Instance);
        using var rest = new SharePointRestClient(siteUri, credentials: null, trustAnyCertificate: false, NullLogger.Instance, handler: handler);
        if (s.IsMultiSite)
        {
            var (sites, failed) = await RestDocumentStore.ConnectSitesAsync(rest, s.Sites, ct).ConfigureAwait(false);
            return await SitesAsync($"Signed in with {session.BrowserName}.", sites, failed, ct).ConfigureAwait(false);
        }

        var store = await RestDocumentStore.ConnectAsync(rest, site, ct).ConfigureAwait(false);
        progress.Report($"Signed in to {store.Description}; listing the libraries");
        return await LibrariesAsync($"Signed in with {session.BrowserName}. {store.Description}", s.PinnedLibrary, store, s.IsRootSite, ct).ConfigureAwait(false);
    }

    private static async Task<TestResult> TestWindowsAsync(SharePointSettings s, IProgress<string> progress, CancellationToken ct)
    {
        SiteAddress.TryParse(s.FirstSite, out var site, out _);
        var who = s.WindowsAccount;
        progress.Report(s.IsMultiSite ? $"Connecting to {s.Sites.Count} sites as {who}" : $"Connecting to {site.Url} as {who}");
        using var rest = new SharePointRestClient(new Uri(site.Url), s.Credentials(), s.TrustAnyCertificate, NullLogger.Instance);
        if (s.IsMultiSite)
        {
            var (sites, failed) = await RestDocumentStore.ConnectSitesAsync(rest, s.Sites, ct).ConfigureAwait(false);
            return await SitesAsync($"Signed in as {who}.", sites, failed, ct).ConfigureAwait(false);
        }

        try
        {
            var store = await RestDocumentStore.ConnectAsync(rest, site, ct).ConfigureAwait(false);
            progress.Report($"Connected to {store.Description}; listing the libraries");
            return await LibrariesAsync($"Signed in as {who}. {store.Description}", s.PinnedLibrary, store, s.IsRootSite, ct).ConfigureAwait(false);
        }
        catch (VfsException ex) when (SharePointRestClient.IsDenial(ex))
        {
            return new TestResult(false, Denied(who, ex, s.IsRootSite));
        }
        catch (VfsException ex) when (ex.Error == VfsError.AccessDenied)
        {
            return new TestResult(false, $"{ex.Message.TrimEnd('.')}. AnyFS tried to sign in as {who}.");
        }
    }

    private static async Task<TestResult> TestContainersAsync(SharePointSettings s, IProgress<string> progress, CancellationToken ct)
    {
        s.TryGetContainerType(out var containerType);
        var cloud = s.EffectiveCloud;
        if (CloudSettings.NeedsOwnRegistration(cloud, s.EffectiveClientId))
        {
            return new TestResult(false, CloudSettings.OwnRegistrationNeeded(cloud, ContainerPermissions, ContainerRegistrationExtra));
        }

        var signIn = MicrosoftSignIn(s, EntraSignIn.ContainerScopes, progress.Report, prompt: null, loadCache: () => EntraSignIn.RecentCache(s.EffectiveClientId, s.EffectiveTenant, s.LoginHint), saveCache: _ => { });
        progress.Report(cloud.IsGlobal ? "Signing in" : $"Signing in to the {cloud.Name} cloud");
        await signIn.SignInAsync(ct).ConfigureAwait(false);
        using var graph = new GraphClient((token, refresh) => signIn.GetAccessTokenAsync(token, refresh), NullLogger.Instance, cloud: cloud);
        try
        {
            if (s.PinnedContainerId is { } id)
            {
                progress.Report($"Signed in as {signIn.Username}; looking up container {id}");
                await GraphDocumentStore.GetContainerAsync(graph, containerType, id, ct).ConfigureAwait(false);
                return new TestResult(true, $"Signed in as {signIn.Username}. Container {id} is there and active.");
            }

            progress.Report($"Signed in as {signIn.Username}; listing the containers");
            var containers = await GraphDocumentStore.ListContainersAsync(graph, containerType, ct).ConfigureAwait(false);
            if (containers.Count == 0)
            {
                return new TestResult(false, $"Signed in as {signIn.Username}, but Graph lists no container of type {containerType} for this account. It lists only containers the account is a direct member of, and nothing at all for an account without a OneDrive; a container's id in the Library setting works regardless.");
            }

            var names = string.Join(", ", containers.Select(c => c.Name));
            if (s.PinnedLibrary is { } pinned && !containers.Any(c => string.Equals(c.Name, pinned, StringComparison.OrdinalIgnoreCase)))
            {
                return new TestResult(false, $"Signed in as {signIn.Username}. No container named {pinned}; this account's containers of the type are: {names}.");
            }

            return new TestResult(true, $"Signed in as {signIn.Username}. {containers.Count} container{(containers.Count == 1 ? "" : "s")} of the type: {names}.");
        }
        catch (VfsException ex) when (ex.Error == VfsError.AccessDenied)
        {
            return new TestResult(false, $"Signed in as {signIn.Username}, but Graph refused: {ex.Message}. The application (client ID {s.EffectiveClientId}) must be registered on the container type with permission to read and write its containers, and the account must be a member of the container.");
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound && s.PinnedContainerId is { } id)
        {
            return new TestResult(false, $"Signed in as {signIn.Username}, but there is no container {id} that this account can see.");
        }
    }

    /// <summary>
    /// The Test button's answer for a site: its libraries and, unless one library is pinned, its subsites, and at the
    /// tenant's root site how many other sites search finds (the first ten named).
    /// </summary>
    private static async Task<TestResult> LibrariesAsync(string what, string? pinned, IDocumentStore store, bool rootSite, CancellationToken ct)
    {
        var names = (await store.ListLibrariesAsync(null, ct).ConfigureAwait(false)).Select(l => l.Name).ToList();
        if (pinned is not null && !names.Any(n => string.Equals(n, pinned, StringComparison.OrdinalIgnoreCase)))
        {
            return new TestResult(false, $"{what} has no library named {pinned}; it has: {string.Join(", ", names)}.");
        }

        return new TestResult(true, $"{what} has {await SummaryAsync(store, null, names, pinned is null, rootSite, ct).ConfigureAwait(false)}.");
    }

    /// <summary>
    /// The Test button's answer for several sites, a line each: what each site that answered has, then what each address
    /// that did not answered. A success only when every site answered, since the source starts only then.
    /// </summary>
    private static async Task<TestResult> SitesAsync(string what, IDocumentStore store, IReadOnlyList<(SiteAddress Site, VfsException Error)> failed, CancellationToken ct)
    {
        var lines = new List<string> { what };
        var ok = failed.Count == 0;
        foreach (var web in store.Sites ?? [])
        {
            try
            {
                var names = (await store.ListLibrariesAsync(web, ct).ConfigureAwait(false)).Select(l => l.Name).ToList();
                lines.Add($"{web.Name} ({web.Url}) has {await SummaryAsync(store, web, names, subsites: true, web.IsRoot, ct).ConfigureAwait(false)}.");
            }
            catch (VfsException ex)
            {
                ok = false;
                lines.Add($"{web.Name} ({web.Url}): {ex.Message}");
            }
        }

        foreach (var (site, error) in failed)
        {
            lines.Add($"{site.Url}: {error.Message.TrimEnd('.')}.");
        }

        return new TestResult(ok, string.Join("\n", lines));
    }

    /// <summary>"3 document libraries: a, b, c, and 2 subsites: x, y", and at a root site what search finds.</summary>
    private static async Task<string> SummaryAsync(IDocumentStore store, WebInfo? web, IReadOnlyList<string> names, bool subsites, bool rootSite, CancellationToken ct)
    {
        List<string> found = [];
        try
        {
            found = subsites ? (await store.ListSubsitesAsync(web, ct).ConfigureAwait(false)).Select(w => w.Name).ToList() : [];
        }
        catch (VfsException ex) when (ex.Error == VfsError.AccessDenied)
        {
            // as the source: an account that may not list the subsites still has the libraries
        }

        var also = found.Count == 0 ? "" : $", and {found.Count} subsite{(found.Count == 1 ? "" : "s")}: {string.Join(", ", found)}";
        if (subsites && rootSite)
        {
            try
            {
                var sites = (await store.ListSitesAsync(web, ct).ConfigureAwait(false)).Select(w => w.Name).ToList();
                if (sites.Count > 0)
                {
                    var shown = string.Join(", ", sites.Take(10)) + (sites.Count > 10 ? $" and {sites.Count - 10} more" : "");
                    also += $". Search finds {sites.Count} other site{(sites.Count == 1 ? "" : "s")} in the tenant, shown in a Sites folder: {shown}";
                }
            }
            catch (VfsException ex)
            {
                also += $". The tenant's other sites could not be listed through search: {ex.Message}";
            }
        }

        return $"{names.Count} document librar{(names.Count == 1 ? "y" : "ies")}: {string.Join(", ", names)}{also}";
    }

    /// <summary>The Microsoft sign-in for an app sign-in, as the settings describe it.</summary>
    internal static EntraSignIn MicrosoftSignIn(SharePointSettings s, IReadOnlyList<string> scopes, Action<string> progress, Func<string, string, Task>? prompt, Func<byte[]?> loadCache, Action<byte[]> saveCache, Microsoft.Extensions.Logging.ILogger? logger = null)
        => new(new SignInOptions(s.EffectiveClientId, s.EffectiveTenant, s.LoginHint, s.Method, scopes, progress, prompt, loadCache, saveCache, Cloud: s.EffectiveCloud), logger ?? NullLogger.Instance);
}

/// <summary>A SharePoint source of whichever kind its address is, signed in as its settings say (<see cref="SharePointPlugin"/>).</summary>
public sealed class SharePointSiteSource : SharePointSource
{
    private readonly SharePointSettings _settings;

    public SharePointSiteSource(SharePointSettings settings)
    {
        _settings = settings;
    }

    /// <summary>The site's id in Graph, for an app-signed-in Online site (for tests).</summary>
    public string SiteId => (Store as GraphDocumentStore)?.SiteId ?? "";

    protected override string? PinnedLibrary => _settings.PinnedLibrary;

    protected override int Connections => _settings.Connections;

    protected override long SpoolBelowBytes => (long)_settings.SpoolBelowMiB << 20;

    protected override string Target => _settings.Kind == SharePointKind.Embedded ? $"container type {_settings.FirstSite}"
        : _settings.IsMultiSite ? string.Join(", ", _settings.Sites.Select(s => s.Url)) : _settings.FirstSite;

    /// <summary>The tenant's root site (https://contoso.sharepoint.com, http://intranet) shows the tenant's other sites in a Sites folder.</summary>
    protected override bool ShowsOtherSites => _settings.IsRootSite;

    private protected override Task<IDocumentStore> ConnectAsync(CancellationToken ct)
    {
        if (_settings.Kind is not { } kind)
        {
            throw new SourceFailedException(SiteAddress.TryParse(_settings.FirstSite, out _, out var reason) ? "The address is not a SharePoint site or a container type id." : reason, permanent: true);
        }

        var signIn = _settings.EffectiveSignIn;
        if (SharePointPlugin.Mismatch(kind, signIn) is { } mismatch)
        {
            throw new SourceFailedException(mismatch, permanent: true);
        }

        // the settings dialog checks this too, but a source edited by hand gets here unchecked
        if (_settings.IsMultiSite && _settings.PinnedLibrary is not null)
        {
            throw new SourceFailedException("With several sites each shows all its libraries; name one library only with one site.", permanent: true);
        }

        return (kind, signIn) switch
        {
            (SharePointKind.Embedded, _) => ConnectContainersAsync(ct),
            (_, SharePointSignIn.Windows) => ConnectWindowsAsync(ct),
            (_, SharePointSignIn.WebSession) => ConnectWebSessionAsync(ct),
            _ => ConnectGraphSiteAsync(ct),
        };
    }

    private void Progress(string message) => Context.ReportProgress(message);

    /// <summary>The Microsoft sign-in for an app sign-in, its cache in the source's secret store.</summary>
    private EntraSignIn MicrosoftSignIn(IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var secrets = Context.Secrets;
        return SharePointPlugin.MicrosoftSignIn(_settings, scopes, Progress,
            prompt: (title, message) => Context.PromptAsync(new PromptRequest(PromptKind.YesNo, title, message), ct),
            loadCache: () => secrets.Get("msal-cache") is { } saved ? Convert.FromBase64String(saved) : null,
            saveCache: bytes => secrets.Set("msal-cache", Convert.ToBase64String(bytes)),
            Logger);
    }

    private async Task SignInMicrosoftAsync(EntraSignIn signIn, MicrosoftCloud cloud, CancellationToken ct)
    {
        Progress(cloud.IsGlobal ? "Signing in to Microsoft 365" : $"Signing in to Microsoft 365 in the {cloud.Name} cloud");
        try
        {
            await signIn.SignInAsync(ct).ConfigureAwait(false);
        }
        catch (SignInRequiredException ex)
        {
            throw new SourceFailedException(ex.Message, permanent: ex.Permanent, ex);
        }
    }

    private void RefuseWithoutOwnRegistration(MicrosoftCloud cloud, string permissions, string extra = "")
    {
        if (!Guid.TryParse(_settings.EffectiveClientId, out _))
        {
            throw new SourceFailedException("The client ID is not a GUID.", permanent: true);
        }

        // the settings dialog checks this too, but a source saved before (or edited by hand) gets here unchecked
        if (CloudSettings.NeedsOwnRegistration(cloud, _settings.EffectiveClientId))
        {
            throw new SourceFailedException(CloudSettings.OwnRegistrationNeeded(cloud, permissions, extra), permanent: true);
        }
    }

    private async Task<IDocumentStore> ConnectGraphSiteAsync(CancellationToken ct)
    {
        SiteAddress.TryParse(_settings.FirstSite, out var site, out _);
        var cloud = _settings.EffectiveCloud;
        RefuseWithoutOwnRegistration(cloud, SharePointPlugin.SitePermissions);
        var signIn = MicrosoftSignIn(EntraSignIn.SiteScopes, ct);
        await SignInMicrosoftAsync(signIn, cloud, ct).ConfigureAwait(false);

        var graph = new GraphClient((token, refresh) => signIn.GetAccessTokenAsync(token, refresh), Logger, MarkDegraded, MarkHealthy, cloud: cloud);
        if (_settings.IsMultiSite)
        {
            Progress($"Looking up {_settings.Sites.Count} sites");
            return await AllSitesAsync(() => GraphDocumentStore.ConnectSitesAsync(graph, _settings.Sites, ct), graph, account: null).ConfigureAwait(false);
        }

        Progress($"Looking up {site.Url}");
        GraphDocumentStore? store;
        try
        {
            store = await GraphDocumentStore.ConnectAsync(graph, site, ct).ConfigureAwait(false);
        }
        catch (SignInRequiredException ex)
        {
            graph.Dispose();
            throw new SourceFailedException(ex.Message, permanent: false, ex);
        }
        catch (VfsException ex) when (ex.Error == VfsError.AccessDenied)
        {
            graph.Dispose();
            throw new SourceFailedException($"{signIn.Username} has no access to {site.Url}", permanent: true, ex);
        }
        catch (VfsException ex)
        {
            graph.Dispose();
            throw new SourceFailedException($"{site.Url}: {ex.Message}", permanent: false, ex);
        }

        if (store is null)
        {
            graph.Dispose();
            throw new SourceFailedException($"{site.Url} was not found, or {signIn.Username} has no access to it", permanent: true);
        }

        Progress($"Signed in as {signIn.Username}");
        return store;
    }

    private async Task<IDocumentStore> ConnectContainersAsync(CancellationToken ct)
    {
        _settings.TryGetContainerType(out var containerType);
        var cloud = _settings.EffectiveCloud;
        RefuseWithoutOwnRegistration(cloud, SharePointPlugin.ContainerPermissions, SharePointPlugin.ContainerRegistrationExtra);
        var signIn = MicrosoftSignIn(EntraSignIn.ContainerScopes, ct);
        await SignInMicrosoftAsync(signIn, cloud, ct).ConfigureAwait(false);

        var graph = new GraphClient((token, refresh) => signIn.GetAccessTokenAsync(token, refresh), Logger, MarkDegraded, MarkHealthy, cloud: cloud);
        Progress($"Listing the containers of type {containerType}");
        try
        {
            var store = await GraphDocumentStore.ForContainersAsync(graph, containerType, _settings.PinnedContainerId, ct).ConfigureAwait(false);
            Progress($"Signed in as {signIn.Username}");
            return store;
        }
        catch (SignInRequiredException ex)
        {
            graph.Dispose();
            throw new SourceFailedException(ex.Message, permanent: false, ex);
        }
        catch (VfsException ex) when (ex.Error is VfsError.AccessDenied or VfsError.InvalidPath or VfsError.NotFound)
        {
            graph.Dispose();
            throw new SourceFailedException($"{signIn.Username} cannot reach the containers of type {containerType}: {ex.Message}. The application (client ID {_settings.EffectiveClientId}) must be registered on the container type, and the account must be a member of the container.", permanent: true, ex);
        }
        catch (VfsException ex)
        {
            graph.Dispose();
            throw new SourceFailedException($"container type {containerType}: {ex.Message}", permanent: false, ex);
        }
    }

    private async Task<IDocumentStore> ConnectWindowsAsync(CancellationToken ct)
    {
        SiteAddress.TryParse(_settings.FirstSite, out var site, out _);
        if (_settings.UsesCurrentLogon && !OperatingSystem.IsWindows())
        {
            throw new SourceFailedException("Give a user name and password: signing in as the current account works on Windows only.", permanent: true);
        }

        var rest = new SharePointRestClient(new Uri(site.Url), _settings.Credentials(), _settings.TrustAnyCertificate, Logger, MarkDegraded, MarkHealthy);
        if (_settings.IsMultiSite)
        {
            Progress($"Connecting to {_settings.Sites.Count} sites as {_settings.WindowsAccount}");
            return await AllSitesAsync(() => RestDocumentStore.ConnectSitesAsync(rest, _settings.Sites, ct), rest, _settings.WindowsAccount).ConfigureAwait(false);
        }

        Progress($"Connecting to {site.Url} as {_settings.WindowsAccount}");
        try
        {
            return await RestDocumentStore.ConnectAsync(rest, site, ct).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            rest.Dispose();
            if (SharePointRestClient.IsDenial(ex))
            {
                throw new SourceFailedException(SharePointPlugin.Denied(_settings.WindowsAccount, ex, _settings.IsRootSite), permanent: true, ex);
            }

            var who = ex.Error == VfsError.AccessDenied ? $" (AnyFS tried to sign in as {_settings.WindowsAccount})" : "";
            throw new SourceFailedException($"{site.Url}: {ex.Message}{who}", permanent: ex.Error is VfsError.AccessDenied or VfsError.NotFound, ex);
        }
    }

    private async Task<IDocumentStore> ConnectWebSessionAsync(CancellationToken ct)
    {
        SiteAddress.TryParse(_settings.FirstSite, out var site, out _);
        var siteUri = new Uri(site.Url);
        var session = new BrowserSession(BrowserLocator.Find(_settings.Browser), SharePointPlugin.ProfileDir(site.Host), Logger);
        if (!session.CanSignIn)
        {
            throw new SourceFailedException(SharePointPlugin.NoBrowser, permanent: true);
        }

        Progress($"Signing in to {site.Host}");
        IReadOnlyList<SessionCookie> cookies;
        try
        {
            cookies = await session.SignInAsync(siteUri, interactive: true, Progress, ct).ConfigureAwait(false);
        }
        catch (SignInRequiredException ex)
        {
            throw new SourceFailedException(ex.Message, permanent: ex.Permanent, ex);
        }

        var handler = new BrowserSessionHandler(cookies, siteUri, c => session.SignInAsync(siteUri, interactive: false, Progress, c), Logger);
        var rest = new SharePointRestClient(siteUri, credentials: null, trustAnyCertificate: false, Logger, MarkDegraded, MarkHealthy, handler);
        if (_settings.IsMultiSite)
        {
            return await AllSitesAsync(() => RestDocumentStore.ConnectSitesAsync(rest, _settings.Sites, ct), rest, account: null).ConfigureAwait(false);
        }

        try
        {
            var store = await RestDocumentStore.ConnectAsync(rest, site, ct).ConfigureAwait(false);
            Progress($"Signed in to {site.Host}");
            return store;
        }
        catch (VfsException ex)
        {
            rest.Dispose();
            throw new SourceFailedException($"{site.Url}: {ex.Message}", permanent: ex.Error is VfsError.NotFound, ex);
        }
    }

    /// <summary>
    /// A source of several sites starts when every one of them answers, as the Test button says; otherwise it says which
    /// did not and why, for good when each was refused or is not there. <paramref name="client"/> is the connection the
    /// store would have taken over, given back when there is no store.
    /// </summary>
    private static async Task<IDocumentStore> AllSitesAsync<TStore>(Func<Task<(TStore Store, IReadOnlyList<(SiteAddress Site, VfsException Error)> Failed)>> connect, IDisposable client, string? account)
        where TStore : IDocumentStore
    {
        TStore store;
        IReadOnlyList<(SiteAddress Site, VfsException Error)> failed;
        try
        {
            (store, failed) = await connect().ConfigureAwait(false);
        }
        catch (SignInRequiredException ex)
        {
            client.Dispose();
            throw new SourceFailedException(ex.Message, permanent: false, ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        if (failed.Count == 0)
        {
            return store;
        }

        store.Dispose();
        var who = account is null ? "" : $" (signed in as {account})";
        var permanent = failed.All(f => f.Error.Error is VfsError.AccessDenied or VfsError.NotFound);
        throw new SourceFailedException($"{failed.Count} of the sites could not be opened{who}: " + string.Join("; ", failed.Select(f => $"{f.Site.Url}: {f.Error.Message.TrimEnd('.')}")), permanent, failed[0].Error);
    }
}
