using System.Net;
using System.Text.Json;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.GitLab;

/// <summary>What a test (or the Test button) changes about a GitLab session: what sends its requests, whether a session kept from before is used.</summary>
internal sealed record GitLabStandIn(Func<HttpMessageHandler> Handler);

/// <summary>
/// A GitLab source's sign-in and the client its requests go through (spec 7.8). First it finds the installation behind
/// the address (where its <c>/api/v4</c> answers: the host, or a path for one installed at a relative URL); then it signs
/// in the way the settings say. An access token goes on every request as GitLab's API asks (PRIVATE-TOKEN); a refused
/// token is final. The client follows no redirects itself, so a sign-in page in place of data is seen for what it is.
/// </summary>
internal sealed class GitLabSession : IDisposable
{
    /// <summary>Marks a request as GraphQL's (a POST), which a browser session must send with its CSRF token.</summary>
    public static readonly HttpRequestOptionsKey<bool> GraphQlRequest = new("anyfs.gitlab.graphql");

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private readonly ILogger _logger;
    private readonly HttpMessageHandler _handler;

    public GitLabSession(GitLabSettings settings, ILogger logger, GitLabStandIn? standIn = null)
    {
        Settings = settings;
        _logger = logger;
        Url = settings.Url ?? throw new SourceFailedException("The address is not an http or https address.", permanent: true);
        Method = settings.EffectiveSignIn;
        Cookies = new CookieContainer(capacity: 300, perDomainCapacity: 100, maxCookieSize: 16 * 1024);
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

    /// <summary>How the source signed in, for messages ("with an access token").</summary>
    public string How { get; private set; } = "";

    /// <summary>How many times the sign-in has been renewed: a request remembers it, so one renewal serves every request that lapsed with it.</summary>
    public int Generation { get; private set; }

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
    /// Signs in for the source's start or the Test; throws <see cref="SourceFailedException"/> (permanent for a refusal that
    /// only new settings can change) when no sign-in can be had.
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

            default:
                throw new SourceFailedException($"Signing in with {Settings.SignInMethod} is not available yet: choose Access token.", permanent: true);
        }

        _logger.LogInformation("gitlab: signed in to {Host} as {User} {How}", Url.Host, User.Username, How);
    }

    /// <summary>The signed-in account, or null where GitLab refuses the sign-in (401).</summary>
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

    /// <summary>Adds what the sign-in needs to a request: the token's header.</summary>
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
    /// every request that was refused with it. A token cannot be renewed.
    /// </summary>
    public Task RenewAsync(int seen, CancellationToken ct)
    {
        _ = seen;
        _ = ct;
        throw new VfsException(VfsError.Unavailable, $"GitLab ({Url.Host}) no longer takes {Settings.Who}: give a new one and Retry the source.");
    }

    public void Dispose()
    {
        Http.Dispose();
        _handler.Dispose();
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
