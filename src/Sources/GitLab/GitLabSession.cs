using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.GitLab;

/// <summary>
/// What a test (or the Test button) changes about a GitLab session: what sends its requests; for the web session, what
/// stands in for the browser (the cookies a sign-in in it would give; interactive or not); and whether a session kept
/// from before is used.
/// </summary>
internal sealed record GitLabStandIn(Func<HttpMessageHandler> Handler, Func<bool, CancellationToken, Task<IReadOnlyList<SessionCookie>>>? Browser = null, bool UseKept = true);

/// <summary>
/// A GitLab source's sign-in and the client its requests go through (spec 7.8). First it finds the installation behind
/// the address (where its <c>/api/v4</c> answers: the host, or a path for one installed at a relative URL); then it signs
/// in the way the settings say. An access token goes on every request as GitLab's API asks (PRIVATE-TOKEN); a refused
/// token is final. The web session takes the cookies of a browser of AnyFS's own signed in on the server's own page
/// (<see cref="GitLabSignInTarget"/>), and the API takes GitLab's session cookie as it takes a token (spike A); a refused
/// request renews it with the browser headless, once for every request refused with it. The cookies are kept in the
/// source's secret store for the next start, and handed from the settings dialog's Test to the source it starts. The
/// client follows no redirects itself, so a sign-in page in place of data is seen for what it is.
/// </summary>
internal sealed class GitLabSession : IDisposable
{
    public const string CookiesSecret = "gitlab-cookies";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan RecentFor = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinRenewalInterval = TimeSpan.FromSeconds(30);

    // the cookies of a sign-in just made, by installation: the Test's, for the source started from the same dialog
    private static readonly ConcurrentDictionary<string, (string Cookies, DateTime At)> Recent = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger _logger;
    private readonly ISecretStore? _secrets;
    private readonly GitLabStandIn? _standIn;
    private readonly HttpMessageHandler _handler;
    private readonly SemaphoreSlim _renewGate = new(1, 1);
    private BrowserSession? _browser;
    private DateTime _renewedAt;
    private int _generation;

    public GitLabSession(GitLabSettings settings, ILogger logger, GitLabStandIn? standIn = null, ISecretStore? secrets = null)
    {
        Settings = settings;
        _logger = logger;
        _secrets = secrets;
        _standIn = standIn;
        Url = settings.Url ?? throw new SourceFailedException("The address is not an http or https address.", permanent: true);
        Method = settings.EffectiveSignIn;
        Cookies = CookieJar.New();
        HttpMessageHandler inner;
        if (standIn is not null)
        {
            inner = standIn.Handler();
        }
        else
        {
            var sockets = new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 32,
            };
            if (settings.TrustAnyCertificate)
            {
                sockets.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            inner = sockets;
        }

        _handler = new CookieHandler(Cookies, inner);
        Http = new HttpClient(_handler, disposeHandler: false) { Timeout = RequestTimeout };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"AnyFS/{typeof(GitLabSession).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        Address = new GitLabAddress(new Uri($"{Url.Scheme}://{Url.Authority}"), "");
    }

    public GitLabSettings Settings { get; }

    /// <summary>The address as given, tidied.</summary>
    public Uri Url { get; }

    public GitLabSignIn Method { get; }

    /// <summary>The installation and the path below it, once <see cref="FindServerAsync"/> has found them.</summary>
    public GitLabAddress Address { get; private set; }

    /// <summary>Who is signed in, once signed in.</summary>
    public GlUser? User { get; private set; }

    /// <summary>For every request: no redirects followed, the session's cookies sent and kept.</summary>
    public HttpClient Http { get; }

    public CookieContainer Cookies { get; }

    /// <summary>How the source signed in, for messages ("with an access token", "with Microsoft Edge").</summary>
    public string How { get; private set; } = "";

    /// <summary>How many times the sign-in has been renewed: a request remembers it, so one renewal serves every request that lapsed with it.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Whether a refused request may be signed in again: not with a token, which only a person can replace.</summary>
    public bool CanRenew => Method != GitLabSignIn.AccessToken;

    /// <summary>
    /// Finds the installation behind the address: the first of its candidates (the host, then each longer path) whose
    /// <c>/api/v4/version</c> answers as GitLab's API does, with data or with a 401 in JSON. Where none answers so (a
    /// server behind a sign-in proxy redirects them all), the host itself.
    /// </summary>
    public async Task FindServerAsync(CancellationToken ct)
    {
        foreach (var candidate in GitLabAddress.Candidates(Url))
        {
            try
            {
                using var response = await Http.GetAsync(new Uri(candidate.Api, "version"), ct).ConfigureAwait(false);
                var json = response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
                if (json && (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized))
                {
                    Address = candidate;
                    _logger.LogInformation("gitlab: the installation at {Server}, {Path}", candidate.Server, candidate.Path.Length == 0 ? "the whole server" : candidate.Path);
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new SourceFailedException($"GitLab ({Url.Host}) did not answer: {ex.Message}", inner: ex);
            }
        }

        Address = GitLabAddress.Candidates(Url).First();
        _logger.LogInformation("gitlab: no GitLab API answered below {Url} without a sign-in; taking {Server} as the installation", Url, Address.Server);
    }

    /// <summary>
    /// Signs in for the source's start or the Test: a web session's cookies kept from before (or just made by the Test) while
    /// GitLab takes them, else the way the settings say, with a browser window only when <paramref name="interactive"/>.
    /// Throws <see cref="SourceFailedException"/> (permanent for a refusal only new settings can change) when no sign-in
    /// can be had.
    /// </summary>
    public async Task SignInAsync(bool interactive, Action<string> progress, CancellationToken ct)
    {
        progress($"Signing in to {Url.Host}");
        await FindServerAsync(ct).ConfigureAwait(false);
        switch (Method)
        {
            case GitLabSignIn.AccessToken:
                if (Settings.Token is null)
                {
                    throw new SourceFailedException("Give an access token (Sign in with: Access token), or choose another way to sign in.", permanent: true);
                }

                User = await WhoAsync(ct).ConfigureAwait(false)
                    ?? throw new SourceFailedException($"GitLab ({Url.Host}) refused the access token: it may have expired or been revoked, or lack the read_api scope.", permanent: true);
                How = "with an access token";
                break;

            case GitLabSignIn.WebSession:
                if (await KeptAsync(ct).ConfigureAwait(false))
                {
                    How = "with the session kept from before";
                    break;
                }

                try
                {
                    await BrowserSignInAsync(interactive, progress, ct).ConfigureAwait(false);
                }
                catch (SignInRequiredException ex)
                {
                    throw new SourceFailedException(ex.Message, ex.Permanent, ex);
                }

                break;

            default:
                throw new SourceFailedException($"Signing in with {Settings.SignInMethod} is not available yet: choose Access token or Web session.", permanent: true);
        }

        _logger.LogInformation("gitlab: signed in to {Host} as {User} {How}", Url.Host, User!.Username, How);
        Save();
    }

    /// <summary>The cookies of a sign-in just made, or kept from the last run, while GitLab takes them.</summary>
    private async Task<bool> KeptAsync(CancellationToken ct)
    {
        if (_standIn is { UseKept: false })
        {
            return false;
        }

        // a sign-in just made (the Test's) first, then the one kept from the last run
        var recent = Recent.TryGetValue(Address.Server.AbsoluteUri, out var r) && DateTime.UtcNow - r.At < RecentFor ? r.Cookies : null;
        foreach (var kept in new[] { recent, _secrets?.Get(CookiesSecret) }.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            CookieJar.Clear(Cookies);
            if (CookieJar.Import(Cookies, kept, Address.Server.Host) <= 0)
            {
                continue;
            }

            if (await WhoAsync(ct).ConfigureAwait(false) is { } user)
            {
                User = user;
                return true;
            }
        }

        _logger.LogInformation("gitlab: no session kept from before for {Host} that GitLab still takes", Url.Host);
        CookieJar.Clear(Cookies);
        return false;
    }

    /// <summary>The web session's sign-in in a browser of AnyFS's own: its cookies, then GitLab asked who they are.</summary>
    private async Task BrowserSignInAsync(bool interactive, Action<string> progress, CancellationToken ct)
    {
        var cookies = await BrowserCookiesAsync(interactive, progress, ct).ConfigureAwait(false);
        CookieJar.Clear(Cookies);
        CookieJar.Add(Cookies, cookies);
        User = await WhoAsync(ct).ConfigureAwait(false)
            ?? throw new SignInRequiredException($"{BrowserName} signed in to {Url.Host}, but GitLab's API does not take its session from AnyFS");
        How = $"with {BrowserName}";
        _logger.LogInformation("gitlab: signed in to {Host} with {Browser} ({Count} cookies)", Url.Host, BrowserName, cookies.Count);
    }

    private Task<IReadOnlyList<SessionCookie>> BrowserCookiesAsync(bool interactive, Action<string> progress, CancellationToken ct)
    {
        if (_standIn?.Browser is { } browser)
        {
            return browser(interactive, ct);
        }

        var session = Browser();
        if (!session.CanSignIn)
        {
            throw new SignInRequiredException("No Edge, Chrome, Chromium or Firefox was found for the web session's sign-in. Install one, set the Browser path under Advanced, or sign in with an access token.", permanent: true);
        }

        return session.SignInAsync(interactive, progress, ct);
    }

    private string BrowserName => _standIn?.Browser is not null ? "the browser" : Browser().BrowserName;

    private BrowserSession Browser()
        => _browser ??= new BrowserSession(BrowserLocator.Find(Settings.Browser, "ANYFS_GITLAB_BROWSER"), new GitLabSignInTarget(Address.Server, Settings.TrustAnyCertificate), _logger);

    /// <summary>The signed-in account, or null where GitLab refuses the sign-in (a 401, or a redirect to its sign-in page).</summary>
    private async Task<GlUser?> WhoAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Address.Api, "user"));
        await AuthorizeAsync(request, ct).ConfigureAwait(false);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || (int)response.StatusCode is >= 300 and < 400)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new SourceFailedException($"GitLab ({Url.Host}) answered {(int)response.StatusCode} when asked who is signed in.");
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        return GlUser.Parse(doc.RootElement);
    }

    /// <summary>Adds what the sign-in needs to a request: the token's header (a session's cookies go by themselves).</summary>
    public ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Method == GitLabSignIn.AccessToken && Settings.Token is { } token)
        {
            request.Headers.Remove("PRIVATE-TOKEN");
            request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// After a request seen at <paramref name="seen"/> (its <see cref="Generation"/>) was refused: signs in again once for
    /// every request refused with it, without anyone (the browser headless, on the profile that keeps the person's
    /// sign-in). A token cannot be renewed; a renewal that needs a person, or a server that refuses the session again at
    /// once, throws a <see cref="VfsException"/> saying so (the source shows Degraded; Retry signs in with a window).
    /// </summary>
    public async Task RenewAsync(int seen, CancellationToken ct)
    {
        if (!CanRenew)
        {
            throw new VfsException(VfsError.Unavailable, $"GitLab ({Url.Host}) no longer takes {Settings.Who}: give a new one and Retry the source.");
        }

        await _renewGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Generation != seen)
            {
                return;
            }

            if (DateTime.UtcNow - _renewedAt < MinRenewalInterval)
            {
                throw new VfsException(VfsError.Unavailable, $"GitLab ({Url.Host}) refused the session again just after it was renewed; Retry the source to sign in again.");
            }

            _renewedAt = DateTime.UtcNow;
            _logger.LogInformation("gitlab: the session for {Host} was refused; signing in again without a window", Url.Host);
            try
            {
                await BrowserSignInAsync(interactive: false, _ => { }, ct).ConfigureAwait(false);
            }
            catch (SignInRequiredException ex)
            {
                throw new VfsException(VfsError.Unavailable, $"The GitLab sign-in has lapsed and AnyFS could not sign in again without you ({ex.Message}); Retry the source to sign in.", ex);
            }

            Interlocked.Increment(ref _generation);
            Save();
        }
        finally
        {
            _renewGate.Release();
        }
    }

    /// <summary>Keeps a web session's cookies: in the secret store for the next start, and for a source started from the dialog whose Test made them.</summary>
    public void Save()
    {
        if (Method == GitLabSignIn.AccessToken)
        {
            return;
        }

        var json = CookieJar.Export(Cookies);
        Recent[Address.Server.AbsoluteUri] = (json, DateTime.UtcNow);
        try
        {
            _secrets?.Set(CookiesSecret, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning("gitlab: the session could not be kept for the next start: {Error}", ex.Message);
        }
    }

    public void Dispose()
    {
        Http.Dispose();
        _handler.Dispose();
        _renewGate.Dispose();
    }

    /// <summary>
    /// The session's cookies on every request and every answer's kept, whatever sends them (a socket, a test's stand-in), so
    /// one jar holds the browser's or the sign-in's cookies and GitLab's renewals of them.
    /// </summary>
    private sealed class CookieHandler(CookieContainer jar, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!;
            var header = jar.GetCookieHeader(url);
            if (header.Length > 0)
            {
                request.Headers.Remove("Cookie");
                request.Headers.TryAddWithoutValidation("Cookie", header);
            }

            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (response.Headers.TryGetValues("Set-Cookie", out var set))
            {
                foreach (var cookie in set)
                {
                    try
                    {
                        jar.SetCookies(url, cookie);
                    }
                    catch (CookieException)
                    {
                        // a cookie .NET will not hold: the session goes without it
                    }
                }
            }

            return response;
        }
    }
}
