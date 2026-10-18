using System.Diagnostics.CodeAnalysis;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.SharePoint;

public sealed class SharePointOnlineSettings
{
    [Setting("Site", Required = true, Order = 0, Placeholder = "https://contoso.sharepoint.com/sites/Team",
        Hint = "The site whose document libraries to serve. The root site is https://contoso.sharepoint.com; a library or page pasted after the site is fine.")]
    public string SiteUrl { get; set; } = "";

    [Setting("Library", Order = 1, Placeholder = "Documents",
        Hint = "Leave empty to show every document library of the site as a top-level folder, or name one to serve just that.")]
    public string? Library { get; set; }

    [Setting("Account", Order = 2, Group = "Sign-in", Placeholder = "name@contoso.com",
        Hint = "The work or school account to sign in with. Optional: it pre-fills the sign-in page and picks the saved sign-in.")]
    public string? Account { get; set; }

    [Setting("Sign in with", Order = 3, Group = "Sign-in", Kind = SettingKind.Choice, Choices = [BrowserMethod, DeviceCodeMethod],
        Hint = "Browser: a sign-in page opens in your browser, the usual way. Device code: a code to type at microsoft.com/devicelogin from any device, for machines without a browser; some organisations block it.")]
    public string SignInMethod { get; set; } = BrowserMethod;

    [Setting("Tenant", Advanced = true, Order = 10, Group = "Sign-in",
        Hint = "organizations (any work or school account), or your tenant's domain or id when the sign-in needs steering to it.")]
    public string Tenant { get; set; } = "organizations";

    [Setting("Client ID", Advanced = true, Order = 11, Group = "Sign-in",
        Hint = "The app registration AnyFS signs in as. Leave the default unless your organisation registered its own for AnyFS.")]
    public string ClientId { get; set; } = SharePointOnlinePlugin.DefaultClientId;

    [Setting("Connections", Advanced = true, Order = 20, Hint = "Parallel downloads per open file.")]
    public int Connections { get; set; } = 4;

    [Setting("Spool files below (MiB)", Advanced = true, Order = 21,
        Hint = "Files this small are downloaded whole when opened and kept in the cache; larger ones are read in ranges as the client asks.")]
    public int SpoolBelowMiB { get; set; } = 8;

    public const string BrowserMethod = "Browser";
    public const string DeviceCodeMethod = "Device code";

    internal SignInMethod Method => string.Equals(SignInMethod, DeviceCodeMethod, StringComparison.OrdinalIgnoreCase) ? SharePoint.SignInMethod.DeviceCode : SharePoint.SignInMethod.Browser;

    internal string? PinnedLibrary => string.IsNullOrWhiteSpace(Library) ? null : Library.Trim();

    internal string? LoginHint => string.IsNullOrWhiteSpace(Account) ? null : Account.Trim();

    internal string EffectiveTenant => string.IsNullOrWhiteSpace(Tenant) ? "organizations" : Tenant.Trim();

    internal string EffectiveClientId => string.IsNullOrWhiteSpace(ClientId) ? SharePointOnlinePlugin.DefaultClientId : ClientId.Trim();
}

/// <summary>
/// SharePoint Online (type <c>sharepoint-online</c>, folder <c>sharepoint</c>, spec 7.3): a Microsoft 365 site's
/// document libraries, through Microsoft Graph. The user signs in once with their work account (the system
/// browser, or a device code) as the app registration AnyFS ships; the sign-in is kept in the source's secret
/// store and renewed silently. Each library is a top-level folder, or one library is the root. Reads are
/// ranged requests on SharePoint's pre-authenticated download URLs, small files spooled whole; writes are
/// spooled locally and uploaded when the file is closed. Names follow SharePoint's rules, case-insensitively.
/// </summary>
public sealed class SharePointOnlinePlugin : ISourcePlugin, IValidatable, ITestable
{
    /// <summary>The AnyFS app registration (provisioning/sharepoint/README.md): a multi-tenant public client with delegated Graph scopes.</summary>
    public const string DefaultClientId = "824dc5b9-f3c4-42b7-871d-6f0de8bc0a4a";

    public string TypeId => "sharepoint-online";

    public string DisplayName => "SharePoint Online";

    public string Description => "A Microsoft 365 site's document libraries, through Microsoft Graph: sign in with your work account; reads, writes, renames.";

    public string TypeFolder => "sharepoint";

    public Stream? OpenIcon() => null;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(SharePointOnlineSettings);

    public object CreateDefaultSettings() => new SharePointOnlineSettings();

    public ISource CreateSource(object settings) => new SharePointOnlineSource((SharePointOnlineSettings)settings);

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (SharePointOnlineSettings)settings;
        if (!SiteAddress.TryParse(s.SiteUrl, out _, out var reason))
        {
            yield return new ValidationMessage(reason, nameof(SharePointOnlineSettings.SiteUrl));
        }

        if (s.PinnedLibrary is { } library)
        {
            if (library.Contains('/') || library.Contains('\\'))
            {
                yield return new ValidationMessage("The library is a name, like Documents, not a path.", nameof(SharePointOnlineSettings.Library));
            }
            else if (SharePointNames.Problem(library) is { } problem)
            {
                yield return new ValidationMessage(problem, nameof(SharePointOnlineSettings.Library));
            }
        }

        if (s.LoginHint is { } account && (!account.Contains('@') || account.Any(char.IsWhiteSpace)))
        {
            yield return new ValidationMessage("The account is a sign-in name like name@contoso.com.", nameof(SharePointOnlineSettings.Account));
        }

        if (s.EffectiveTenant.Any(char.IsWhiteSpace) || s.EffectiveTenant.Contains('/'))
        {
            yield return new ValidationMessage("The tenant is organizations, a domain like contoso.onmicrosoft.com, or a tenant id.", nameof(SharePointOnlineSettings.Tenant));
        }

        if (!Guid.TryParse(s.EffectiveClientId, out _))
        {
            yield return new ValidationMessage("The client ID is the app registration's Application (client) ID, a GUID.", nameof(SharePointOnlineSettings.ClientId));
        }

        if (s.Connections is < 1 or > 16)
        {
            yield return new ValidationMessage("Connections must be between 1 and 16.", nameof(SharePointOnlineSettings.Connections));
        }

        if (s.SpoolBelowMiB is < 0 or > 1024)
        {
            yield return new ValidationMessage("Spool files below must be between 0 (never) and 1024 MiB.", nameof(SharePointOnlineSettings.SpoolBelowMiB));
        }
    }

    /// <summary>
    /// Signs in (the browser or a device code, as set) and lists the site's libraries. The sign-in is kept in
    /// memory for this process, so the source started from the same dialog does not ask again.
    /// </summary>
    public async Task<TestResult> TestAsync(object settings, IProgress<string> progress, CancellationToken ct)
    {
        var s = (SharePointOnlineSettings)settings;
        if (!SiteAddress.TryParse(s.SiteUrl, out var site, out var reason))
        {
            return new TestResult(false, reason);
        }

        var signIn = new EntraSignIn(new SignInOptions(
            s.EffectiveClientId, s.EffectiveTenant, s.LoginHint, s.Method, EntraSignIn.SiteScopes,
            Progress: progress.Report,
            Prompt: null,
            LoadCache: () => EntraSignIn.RecentCache(s.EffectiveClientId, s.EffectiveTenant, s.LoginHint),
            SaveCache: _ => { }), NullLogger.Instance);
        try
        {
            progress.Report("Signing in");
            await signIn.SignInAsync(ct).ConfigureAwait(false);
            using var graph = new GraphClient((token, refresh) => signIn.GetAccessTokenAsync(token, refresh), NullLogger.Instance);
            progress.Report($"Signed in as {signIn.Username}; looking up {site.Url}");
            using var siteDoc = await graph.GetJsonAsync($"{site.GraphPath}?$select=id,displayName,webUrl", site.Url, ct).ConfigureAwait(false);
            var siteId = siteDoc.RootElement.GetProperty("id").GetString()!;
            var siteName = siteDoc.RootElement.TryGetProperty("displayName", out var dn) ? dn.GetString() : site.Url;
            using var drivesDoc = await graph.GetJsonAsync($"sites/{siteId}/drives?$select=id,name,driveType", site.Url, ct).ConfigureAwait(false);
            var drives = drivesDoc.RootElement.GetProperty("value").EnumerateArray().Select(GraphClient.ParseDrive).ToList();
            if (s.PinnedLibrary is { } pinned && !drives.Any(d => string.Equals(d.Name, pinned, StringComparison.OrdinalIgnoreCase)))
            {
                return new TestResult(false, $"Signed in as {signIn.Username}. The site \"{siteName}\" has no library named {pinned}; it has: {string.Join(", ", drives.Select(d => d.Name))}.");
            }

            return new TestResult(true, $"Signed in as {signIn.Username}. The site \"{siteName}\" has {drives.Count} document librar{(drives.Count == 1 ? "y" : "ies")}: {string.Join(", ", drives.Select(d => d.Name))}.");
        }
        catch (SignInRequiredException ex)
        {
            return new TestResult(false, ex.Message);
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            return new TestResult(false, $"Signed in as {signIn.Username}, but {site.Url} was not found, or this account has no access to it.");
        }
        catch (VfsException ex)
        {
            return new TestResult(false, ex.Message);
        }
    }
}
