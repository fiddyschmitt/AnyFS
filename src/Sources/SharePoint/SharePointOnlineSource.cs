using AnyFS.Sdk;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// SharePoint Online: the shared <see cref="SharePointSource"/> over a <see cref="GraphDocumentStore"/> for the
/// site, after a Microsoft identity platform sign-in kept in the source's secret store.
/// </summary>
public sealed class SharePointOnlineSource : SharePointSource
{
    private readonly SharePointOnlineSettings _settings;
    private SiteAddress _site = null!;

    public SharePointOnlineSource(SharePointOnlineSettings settings)
    {
        _settings = settings;
    }

    /// <summary>The site's id in Graph (for tests).</summary>
    public string SiteId => (Store as GraphDocumentStore)?.SiteId ?? "";

    protected override string? PinnedLibrary => _settings.PinnedLibrary;

    protected override int Connections => _settings.Connections;

    protected override long SpoolBelowBytes => (long)_settings.SpoolBelowMiB << 20;

    protected override string Target => _settings.SiteUrl;

    private protected override async Task<IDocumentStore> ConnectAsync(CancellationToken ct)
    {
        if (!SiteAddress.TryParse(_settings.SiteUrl, out _site, out var reason))
        {
            throw new SourceFailedException(reason, permanent: true);
        }

        if (!Guid.TryParse(_settings.EffectiveClientId, out _))
        {
            throw new SourceFailedException("The client ID is not a GUID.", permanent: true);
        }

        var secrets = Context.Secrets;
        var signIn = new EntraSignIn(new SignInOptions(
            _settings.EffectiveClientId, _settings.EffectiveTenant, _settings.LoginHint, _settings.Method, EntraSignIn.SiteScopes,
            Progress: message => Context.ReportProgress(message),
            Prompt: (title, message) => Context.PromptAsync(new PromptRequest(PromptKind.YesNo, title, message), ct),
            LoadCache: () => secrets.Get("msal-cache") is { } saved ? Convert.FromBase64String(saved) : null,
            SaveCache: bytes => secrets.Set("msal-cache", Convert.ToBase64String(bytes))), Logger);
        Context.ReportProgress("Signing in to Microsoft 365");
        try
        {
            await signIn.SignInAsync(ct).ConfigureAwait(false);
        }
        catch (SignInRequiredException ex)
        {
            throw new SourceFailedException(ex.Message, permanent: ex.Message.Contains("Conditional Access", StringComparison.Ordinal), ex);
        }

        var graph = new GraphClient((token, refresh) => signIn.GetAccessTokenAsync(token, refresh), Logger, MarkDegraded, MarkHealthy);
        Context.ReportProgress($"Looking up {_site.Url}");
        GraphDocumentStore? store;
        try
        {
            store = await GraphDocumentStore.ConnectAsync(graph, _site, ct).ConfigureAwait(false);
        }
        catch (SignInRequiredException ex)
        {
            graph.Dispose();
            throw new SourceFailedException(ex.Message, permanent: false, ex);
        }
        catch (VfsException ex) when (ex.Error == VfsError.AccessDenied)
        {
            graph.Dispose();
            throw new SourceFailedException($"{signIn.Username} has no access to {_site.Url}", permanent: true, ex);
        }
        catch (VfsException ex)
        {
            graph.Dispose();
            throw new SourceFailedException($"{_site.Url}: {ex.Message}", permanent: false, ex);
        }

        if (store is null)
        {
            graph.Dispose();
            throw new SourceFailedException($"{_site.Url} was not found, or {signIn.Username} has no access to it", permanent: true);
        }

        Context.ReportProgress($"Signed in as {signIn.Username}");
        return store;
    }
}
