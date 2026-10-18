using System.Diagnostics.CodeAnalysis;
using System.Net;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.SharePoint;

public sealed class SharePointServerSettings
{
    [Setting("Site", Required = true, Order = 0, Placeholder = "http://sharepoint.contoso.local/sites/Team",
        Hint = "The site whose document libraries to serve, by name or address. A library or page pasted after the site is fine.")]
    public string SiteUrl { get; set; } = "";

    [Setting("Library", Order = 1, Placeholder = "Documents",
        Hint = "Leave empty to show every document library of the site as a top-level folder, or name one to serve just that.")]
    public string? Library { get; set; }

    [Setting("Username", Order = 2, Group = "Sign-in", Placeholder = @"CONTOSO\name or name@contoso.local",
        Hint = "A Windows account the server knows. Leave empty to use the account AnyFS runs as (Windows only).")]
    public string? Username { get; set; }

    [Setting("Password", Secret = true, Order = 3, Group = "Sign-in")]
    public string? Password { get; set; }

    [Setting("Connections", Advanced = true, Order = 20, Hint = "Parallel downloads per open file.")]
    public int Connections { get; set; } = 4;

    [Setting("Spool files below (MiB)", Advanced = true, Order = 21,
        Hint = "Files this small are downloaded whole when opened and kept in the cache; larger ones are read in ranges as the client asks.")]
    public int SpoolBelowMiB { get; set; } = 8;

    [Setting("Trust any certificate", Advanced = true, Order = 22,
        Hint = "Accept the server's HTTPS certificate whatever it is (a lab with a self-signed one). Off for a real server.")]
    public bool TrustAnyCertificate { get; set; }

    internal string? PinnedLibrary => string.IsNullOrWhiteSpace(Library) ? null : Library.Trim();

    internal bool UsesCurrentLogon => string.IsNullOrWhiteSpace(Username);

    /// <summary>The credentials for the handler: DOMAIN\name is split, name@domain is passed as a user principal name.</summary>
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
}

/// <summary>
/// SharePoint Server, on-premises (type <c>sharepoint-server</c>, folder <c>sharepoint</c>, spec 7.4): a site's
/// document libraries through SharePoint's REST API with Windows authentication, reached by name or by address.
/// The same tree, caching, spooling and write path as SharePoint Online (<see cref="SharePointSource"/>).
/// </summary>
public sealed class SharePointServerPlugin : ISourcePlugin, IValidatable, ITestable
{
    public string TypeId => "sharepoint-server";

    public string DisplayName => "SharePoint Server";

    public string Description => "An on-premises SharePoint site's document libraries, through its REST API with your Windows account: reads, writes, renames.";

    public string TypeFolder => "sharepoint";

    public Stream? OpenIcon() => null;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(SharePointServerSettings);

    public object CreateDefaultSettings() => new SharePointServerSettings();

    public ISource CreateSource(object settings) => new SharePointServerSource((SharePointServerSettings)settings);

    /// <summary>
    /// Why a SharePoint Online address is refused here, or null for any other: Microsoft 365 takes no Windows
    /// password, so this source could only fail there, and how it fails (a proxy's error, a 401) does not say that
    /// the type is wrong (18-Oct-2026: a .sharepoint.us site was set up as SharePoint Server).
    /// </summary>
    internal static string? OnlineSiteProblem(SiteAddress site)
        => MicrosoftCloud.FromSiteHost(site.Host) is null
            ? null
            : $"{site.Host} is SharePoint Online, in Microsoft 365, which takes a work-account sign-in rather than a Windows password: use the SharePoint Online source type for it. This type is for SharePoint Server on your own network.";

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (SharePointServerSettings)settings;
        if (!SiteAddress.TryParse(s.SiteUrl, out var site, out var reason))
        {
            yield return new ValidationMessage(reason, nameof(SharePointServerSettings.SiteUrl));
        }
        else if (OnlineSiteProblem(site) is { } online)
        {
            yield return new ValidationMessage(online, nameof(SharePointServerSettings.SiteUrl));
        }

        if (s.PinnedLibrary is { } library)
        {
            if (library.Contains('/') || library.Contains('\\'))
            {
                yield return new ValidationMessage("The library is a name, like Documents, not a path.", nameof(SharePointServerSettings.Library));
            }
            else if (SharePointNames.Problem(library) is { } problem)
            {
                yield return new ValidationMessage(problem, nameof(SharePointServerSettings.Library));
            }
        }

        if (s.UsesCurrentLogon)
        {
            if (!string.IsNullOrEmpty(s.Password))
            {
                yield return new ValidationMessage("A password needs a user name.", nameof(SharePointServerSettings.Username));
            }

            if (!OperatingSystem.IsWindows())
            {
                yield return new ValidationMessage("Give a user name and password: signing in as the current account works on Windows only.", nameof(SharePointServerSettings.Username));
            }
        }
        else if (s.Username!.Trim().Any(char.IsWhiteSpace))
        {
            yield return new ValidationMessage(@"The user name is DOMAIN\name or name@domain, with no spaces.", nameof(SharePointServerSettings.Username));
        }

        if (s.Connections is < 1 or > 16)
        {
            yield return new ValidationMessage("Connections must be between 1 and 16.", nameof(SharePointServerSettings.Connections));
        }

        if (s.SpoolBelowMiB is < 0 or > 1024)
        {
            yield return new ValidationMessage("Spool files below must be between 0 (never) and 1024 MiB.", nameof(SharePointServerSettings.SpoolBelowMiB));
        }
    }

    public async Task<TestResult> TestAsync(object settings, IProgress<string> progress, CancellationToken ct)
    {
        var s = (SharePointServerSettings)settings;
        if (!SiteAddress.TryParse(s.SiteUrl, out var site, out var reason))
        {
            return new TestResult(false, reason);
        }

        if (OnlineSiteProblem(site) is { } online)
        {
            return new TestResult(false, online);
        }

        progress.Report($"Connecting to {site.Url}");
        using var rest = new SharePointRestClient(new Uri(site.Url), s.Credentials(), s.TrustAnyCertificate, NullLogger.Instance);
        try
        {
            var store = await RestDocumentStore.ConnectAsync(rest, ct).ConfigureAwait(false);
            progress.Report($"Connected to {store.Description}; listing the libraries");
            var libraries = await store.ListLibrariesAsync(ct).ConfigureAwait(false);
            if (s.PinnedLibrary is { } pinned && !libraries.Any(l => string.Equals(l.Name, pinned, StringComparison.OrdinalIgnoreCase)))
            {
                return new TestResult(false, $"{store.Description} has no library named {pinned}; it has: {string.Join(", ", libraries.Select(l => l.Name))}.");
            }

            return new TestResult(true, $"{store.Description} has {libraries.Count} document librar{(libraries.Count == 1 ? "y" : "ies")}: {string.Join(", ", libraries.Select(l => l.Name))}.");
        }
        catch (VfsException ex)
        {
            return new TestResult(false, ex.Message);
        }
    }
}

/// <summary>SharePoint Server: the shared <see cref="SharePointSource"/> over a <see cref="RestDocumentStore"/>.</summary>
public sealed class SharePointServerSource : SharePointSource
{
    private readonly SharePointServerSettings _settings;

    public SharePointServerSource(SharePointServerSettings settings)
    {
        _settings = settings;
    }

    protected override string? PinnedLibrary => _settings.PinnedLibrary;

    protected override int Connections => _settings.Connections;

    protected override long SpoolBelowBytes => (long)_settings.SpoolBelowMiB << 20;

    protected override string Target => _settings.SiteUrl;

    private protected override async Task<IDocumentStore> ConnectAsync(CancellationToken ct)
    {
        if (!SiteAddress.TryParse(_settings.SiteUrl, out var site, out var reason))
        {
            throw new SourceFailedException(reason, permanent: true);
        }

        if (SharePointServerPlugin.OnlineSiteProblem(site) is { } online)
        {
            throw new SourceFailedException(online, permanent: true);
        }

        if (_settings.UsesCurrentLogon && !OperatingSystem.IsWindows())
        {
            throw new SourceFailedException("Give a user name and password: signing in as the current account works on Windows only.", permanent: true);
        }

        Context.ReportProgress($"Connecting to {site.Url}");
        var rest = new SharePointRestClient(new Uri(site.Url), _settings.Credentials(), _settings.TrustAnyCertificate, Logger, MarkDegraded, MarkHealthy);
        try
        {
            return await RestDocumentStore.ConnectAsync(rest, ct).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            rest.Dispose();
            throw new SourceFailedException($"{site.Url}: {ex.Message}", permanent: ex.Error is VfsError.AccessDenied or VfsError.NotFound, ex);
        }
    }
}
