using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Artifactory;

/// <summary>
/// What a test (or the Test button) changes about an Artifactory session: what sends its requests; for the web session,
/// what stands in for the browser (the cookies a sign-in in it would give; interactive or not); and whether a session kept
/// from before is used.
/// </summary>
internal sealed record ArtifactoryStandIn(Func<HttpMessageHandler> Handler, Func<bool, CancellationToken, Task<IReadOnlyList<SessionCookie>>>? Browser = null, bool UseKept = true);

/// <summary>
/// An Artifactory source's sign-in and the client its requests go through (spec 7.13). First it finds Artifactory behind
/// the address (where its ping answers: <c>/artifactory</c> on the host, the host's root, or a path of its own); then it
/// signs in the way the settings say. A token goes on every request as a bearer token, an API key
/// in the header Artifactory reads one from, a user name and password as Basic; a refusal of any of them is final. The web
/// session takes the cookies of a browser of AnyFS's own signed in on the platform's own page
/// (<see cref="ArtifactorySignInTarget"/>): the platform keeps the session in two of them, a short-lived access token,
/// which Artifactory's REST API takes as any bearer token, and what renews it, which the platform's own page calls do for
/// AnyFS as for the page. They are kept in the source's secret store for the next start. The client follows no redirects
/// itself: a file's download sent on to a storage service's address goes there without the sign-in.
/// </summary>
internal sealed class ArtifactorySession : IDisposable
{
    public const string CookiesSecret = "artifactory-cookies";

    /// <summary>The cookie the platform's pages keep the signed-in account's access token in.</summary>
    public const string AccessCookie = "ACCESSTOKEN";

    /// <summary>The cookie that renews it.</summary>
    public const string RefreshCookie = "REFRESHTOKEN";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RecentFor = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinRenewalInterval = TimeSpan.FromSeconds(30);

    // the cookies of a sign-in just made, by installation: the Test's, for the source started from the same dialog
    private static readonly ConcurrentDictionary<string, (string Cookies, DateTime At)> Recent = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger _logger;
    private readonly ISecretStore? _secrets;
    private readonly ArtifactoryStandIn? _standIn;
    private readonly HttpMessageHandler _handler;
    private readonly SemaphoreSlim _renewGate = new(1, 1);
    private BrowserSession? _browser;
    private DateTime _renewedAt;
    private int _generation;
    private bool _signedIn;

    // the web session's access token as it stands (the cookie's value), and when it runs out
    private string? _bearer;
    private DateTimeOffset _bearerUntil;

    // how far the server's clock is ahead of this computer's, from the Date of its answers
    private long _clockAheadTicks;

    /// <param name="watcher">Who hears of each request made (<see cref="ISourceContext.UpstreamWatcher"/>).</param>
    public ArtifactorySession(ArtifactorySettings settings, ILogger logger, ArtifactoryStandIn? standIn = null, ISecretStore? secrets = null, IUpstreamWatcher? watcher = null)
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
            };
            if (settings.TrustAnyCertificate)
            {
                sockets.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            inner = sockets;
        }

        _handler = new ClockHandler(this, UpstreamRequestsHandler.Around(inner, watcher));
        Http = new HttpClient(_handler, disposeHandler: false) { Timeout = RequestTimeout };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"AnyFS/{typeof(ArtifactorySession).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        Address = ArtifactoryAddress.Candidates(Url)[0];
    }

    public ArtifactorySettings Settings { get; }

    /// <summary>The address as given, tidied.</summary>
    public Uri Url { get; }

    public ArtifactorySignIn Method { get; }

    /// <summary>Artifactory's address and what the rest of the address names, once <see cref="FindServerAsync"/> has found it.</summary>
    public ArtifactoryAddress Address { get; private set; }

    /// <summary>The platform's address (its pages at <c>ui/</c>, its access service at <c>access/</c>): what Artifactory's own is below.</summary>
    public Uri Platform => new(Address.Base, "../");

    /// <summary>Artifactory's version, where it told it.</summary>
    public string? Version { get; private set; }

    /// <summary>Who is signed in, once signed in, where the server or the sign-in tells: null for a token that does not name its account.</summary>
    public string? User { get; private set; }

    /// <summary>For every request: no redirects followed.</summary>
    public HttpClient Http { get; }

    /// <summary>The web session's cookies.</summary>
    public CookieContainer Cookies { get; }

    /// <summary>How the source signed in, for messages ("with an access token", "with Microsoft Edge").</summary>
    public string How { get; private set; } = "";

    /// <summary>The account for messages: its name, or what it signs in with where that does not name it.</summary>
    public string Who => User ?? Settings.Who;

    /// <summary>
    /// What tells one account's kept listings from another's: its name, or, for a token that names none, a few characters of
    /// the token's hash (another token may be another account's, and sees other files).
    /// </summary>
    public string Account => User
        ?? (Method == ArtifactorySignIn.Token && Settings.Token is { } token
            ? "token:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..12]
            : Method.ToString());

    /// <summary>How many times the sign-in has been renewed: a request remembers it, so one renewal serves every request that lapsed with it.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Whether a refused request may be signed in again: only the web session, whose access token lasts an hour or so.</summary>
    public bool CanRenew => Method == ArtifactorySignIn.WebSession;

    /// <summary>The time on the server's clock now, as its answers' dates tell it: what its files' times are compared with.</summary>
    public DateTimeOffset ServerNow => DateTimeOffset.UtcNow.AddTicks(Volatile.Read(ref _clockAheadTicks));

    /// <summary>
    /// Finds Artifactory behind the address: the first of its candidates whose ping answers <c>OK</c>, as Artifactory's does
    /// to anyone, signed in or not. Where none does (a proxy before the server that lets nothing through unsigned), the first
    /// whose version call answers the sign-in with a version; else the likeliest, and the sign-in says what it met there.
    /// </summary>
    public async Task FindServerAsync(CancellationToken ct)
    {
        var candidates = ArtifactoryAddress.Candidates(Url);
        try
        {
            foreach (var candidate in candidates)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Api("system/ping"));
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim() == "OK")
                {
                    Found(candidate);
                    return;
                }
            }

            foreach (var candidate in candidates)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Api("system/version"));
                await AuthorizeAsync(request, ct).ConfigureAwait(false);
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                if (await IsJsonOfAsync(response, e => e.ValueKind == JsonValueKind.Object && e.Str("version") is { Length: > 0 }, ct).ConfigureAwait(false))
                {
                    Found(candidate);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SourceFailedException($"Artifactory ({Url.Host}) did not answer: {ex.Message}", inner: ex);
        }

        Address = candidates[0];
        _logger.LogInformation("artifactory: nothing below {Url} answered as Artifactory does; taking {Base}", Url, Address.Base);
    }

    private void Found(ArtifactoryAddress address)
    {
        Address = address;
        _logger.LogInformation("artifactory: Artifactory {Version} at {Base}, {Scope}", Version ?? "(version not told)", address.Base,
            address.Scope == ArtifactoryScope.Server ? "the whole server" : address.Repository + (address.Path.Length == 0 ? "" : "/" + address.Path));
    }

    /// <summary>Whether an answer is JSON that <paramref name="holds"/> what was asked for.</summary>
    private static async Task<bool> IsJsonOfAsync(HttpResponseMessage response, Func<JsonElement, bool> holds, CancellationToken ct)
    {
        using var doc = await JsonOfAsync(response, ct).ConfigureAwait(false);
        return doc is not null && holds(doc.RootElement);
    }

    /// <summary>A successful answer's JSON; null where the answer failed or is not JSON.</summary>
    private static async Task<JsonDocument?> JsonOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Signs in for the source's start or the Test: the way the settings say; a web session's cookies kept from before (or
    /// just made by the Test) while the platform takes them, else a browser, with a window only when
    /// <paramref name="interactive"/>. Throws <see cref="SourceFailedException"/> (permanent for a refusal only new settings
    /// can change) when no sign-in can be had.
    /// </summary>
    public async Task SignInAsync(bool interactive, Action<string> progress, CancellationToken ct)
    {
        progress($"Signing in to {Url.Host}");
        await FindServerAsync(ct).ConfigureAwait(false);
        switch (Method)
        {
            case ArtifactorySignIn.Token:
                if (Settings.Token is null)
                {
                    throw new SourceFailedException("Give an access token (Sign in with: Access token), or choose another way to sign in.", permanent: true);
                }

                await CheckAsync("the access token: it may have expired or been revoked", ct).ConfigureAwait(false);
                User = NameIn(Settings.Token) ?? await NameFromPlatformAsync(ct).ConfigureAwait(false);
                How = IsApiKey(Settings.Token) ? "with an API key" : "with an access token";
                break;

            case ArtifactorySignIn.Password:
                if (Settings.User is null)
                {
                    throw new SourceFailedException("Give a user name and password (Sign in with: User name and password), or choose another way to sign in.", permanent: true);
                }

                await CheckAsync($"the user name and password of {Settings.User}", ct).ConfigureAwait(false);
                User = Settings.User;
                How = "with a user name and password";
                break;

            case ArtifactorySignIn.Anonymous:
                await CheckAsync("a request with no sign-in: it lets no one read without one", ct).ConfigureAwait(false);
                How = "without a sign-in";
                break;

            default:
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
        }

        _signedIn = true;
        _logger.LogInformation("artifactory: signed in to {Host} as {User} {How}", Url.Host, Who, How);
        Save();
    }

    /// <summary>
    /// Asks Artifactory for its repositories with the sign-in: what every account may ask, anyone where the server lets anyone
    /// read, and none with a sign-in the server does not take.
    /// </summary>
    private async Task CheckAsync(string refused, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Address.Api("repositories"));
            await AuthorizeAsync(request, ct).ConfigureAwait(false);
            response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SourceFailedException($"Artifactory ({Url.Host}) did not answer: {ex.Message}", inner: ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var said = ArtifactoryApi.Message(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                throw new SourceFailedException($"Artifactory ({Url.Host}) refused {refused}{(said is null ? "" : $" ({said})")}.", permanent: true);
            }

            using var doc = await JsonOfAsync(response, ct).ConfigureAwait(false);
            if (doc?.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new SourceFailedException($"{Address.Base} did not answer as Artifactory does ({(int)response.StatusCode}): give the server's address as the browser shows it, like https://artifactory.example.com/artifactory.", permanent: (int)response.StatusCode is 404 or 200);
            }

            // what a start asks for next, and the Test: kept for the first to ask (TakeRepositories)
            Volatile.Write(ref _checked, new Checked(ArtRepository.ListOf(doc.RootElement), Environment.TickCount64));
        }
    }

    // the repositories the sign-in's check read, and when, until someone takes them
    private sealed record Checked(List<ArtRepository> Repositories, long At);

    private Checked? _checked;

    /// <summary>
    /// The repositories the sign-in's check read (<c>api/repositories</c>, every one the account may see), for the first to
    /// ask for them within <paramref name="fresh"/> of then: a start, and the Test, ask nothing more for their first list.
    /// Null once taken, past that time, or where the sign-in read none (a web session's).
    /// </summary>
    public List<ArtRepository>? TakeRepositories(TimeSpan fresh)
        => Interlocked.Exchange(ref _checked, null) is { } taken && Environment.TickCount64 - taken.At <= fresh.TotalMilliseconds ? taken.Repositories : null;

    /// <summary>The account a signed token names (its subject, <c>…/users/name</c>); null for a token that says nothing (a reference token, an API key).</summary>
    internal static string? NameIn(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var subject = doc.RootElement.Str("sub") ?? "";
            var users = subject.LastIndexOf("/users/", StringComparison.Ordinal);
            return users >= 0 && subject.Length > users + 7 ? subject[(users + 7)..] : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>When a signed token runs out (its <c>exp</c>); null for one that does not say, or does not run out.</summary>
    internal static DateTimeOffset? ExpiryOf(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var exp = doc.RootElement.Long("exp");
            return exp > 0 ? DateTimeOffset.FromUnixTimeSeconds(exp) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>An API key, which Artifactory reads from a header of its own, not as a bearer token: they begin <c>AKC</c>.</summary>
    internal static bool IsApiKey(string token) => token.StartsWith("AKC", StringComparison.Ordinal) && !token.Contains('.');

    /// <summary>
    /// The signed-in account as the platform's own pages learn it (<c>ui/api/v1/ui/auth/current</c>), for a sign-in that does
    /// not name it; null where the platform does not say (an Artifactory before the platform, a server that keeps the call
    /// to its pages), or says nobody.
    /// </summary>
    private async Task<string?> NameFromPlatformAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Platform, "ui/api/v1/ui/auth/current"));
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            await AuthorizeAsync(request, ct).ConfigureAwait(false);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            {
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.Str("name") is { Length: > 0 } name && !name.Equals("anonymous", StringComparison.OrdinalIgnoreCase) ? name : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // --- the web session -----------------------------------------------------------------------------------------------

    /// <summary>The cookies of a sign-in just made, or kept from the last run, while the platform takes them.</summary>
    private async Task<bool> KeptAsync(CancellationToken ct)
    {
        if (_standIn is { UseKept: false })
        {
            return false;
        }

        // a sign-in just made (the Test's) first, then the one kept from the last run
        var recent = Recent.TryGetValue(Address.Base.AbsoluteUri, out var r) && DateTime.UtcNow - r.At < RecentFor ? r.Cookies : null;
        foreach (var kept in new[] { recent, _secrets?.Get(CookiesSecret) }.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            CookieJar.Clear(Cookies);
            if (CookieJar.Import(Cookies, kept, Address.Base.Host) <= 0)
            {
                continue;
            }

            if (await SessionAsync(ct).ConfigureAwait(false) is { } user)
            {
                User = user;
                return true;
            }
        }

        _logger.LogInformation("artifactory: no session kept from before for {Host} that the platform still takes", Url.Host);
        CookieJar.Clear(Cookies);
        return false;
    }

    /// <summary>The web session's sign-in in a browser of AnyFS's own: its cookies, then the platform asked who they are.</summary>
    private async Task BrowserSignInAsync(bool interactive, Action<string> progress, CancellationToken ct)
    {
        var cookies = await BrowserCookiesAsync(interactive, progress, ct).ConfigureAwait(false);
        CookieJar.Clear(Cookies);
        CookieJar.Add(Cookies, cookies);
        User = await SessionAsync(ct).ConfigureAwait(false)
            ?? throw new SignInRequiredException($"{BrowserName} signed in to {Url.Host}, but the platform does not take its session from AnyFS");
        How = $"with {BrowserName}";
        _logger.LogInformation("artifactory: signed in to {Host} with {Browser} ({Count} cookies)", Url.Host, BrowserName, cookies.Count);
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
        => _browser ??= new BrowserSession(BrowserLocator.Find(Settings.Browser, "ANYFS_ARTIFACTORY_BROWSER"), new ArtifactorySignInTarget(Platform, Settings.TrustAnyCertificate), _logger, Settings.Browser);

    /// <summary>
    /// The session the jar's cookies are, asked of the platform as its own pages ask (<c>ui/api/v1/ui/auth/current</c>): who
    /// is signed in, with the access token the answer leaves in the jar (the platform renews one that has run out while what
    /// renews it holds). Null where they are nobody's.
    /// </summary>
    private async Task<string?> SessionAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Platform, "ui/api/v1/ui/auth/current"));
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        AddCookies(request);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        TakeCookies(request.RequestUri!, response);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var name = doc.RootElement.Str("name");
        if (string.IsNullOrEmpty(name) || name.Equals("anonymous", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = Cookies.GetCookies(new Uri(Platform, "ui/"))[AccessCookie]?.Value;
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        _bearer = token;
        _bearerUntil = ExpiryOf(token) ?? DateTimeOffset.MaxValue;
        return name;
    }

    private void AddCookies(HttpRequestMessage request)
    {
        var header = Cookies.GetCookieHeader(request.RequestUri!);
        if (header.Length > 0)
        {
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", header);
        }
    }

    private void TakeCookies(Uri url, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var set))
        {
            return;
        }

        foreach (var cookie in set)
        {
            try
            {
                Cookies.SetCookies(url, cookie);
            }
            catch (CookieException)
            {
                // a cookie .NET will not hold: the session goes without it
            }
        }
    }

    /// <summary>Adds what the sign-in needs to a request to the server itself; nothing to one sent on elsewhere (a file's address in a storage service).</summary>
    public async ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = null;
        request.Headers.Remove("X-JFrog-Art-Api");
        if (request.RequestUri is not { } url || !OnServer(url))
        {
            return;
        }

        switch (Method)
        {
            case ArtifactorySignIn.Token when Settings.Token is { } token:
                if (IsApiKey(token))
                {
                    request.Headers.TryAddWithoutValidation("X-JFrog-Art-Api", token);
                }
                else
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                break;
            case ArtifactorySignIn.Password when Settings.User is { } user:
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{Settings.Password}")));
                break;
            case ArtifactorySignIn.WebSession:
                // an access token about to run out is renewed first, as the platform's pages have theirs renewed
                if (_signedIn && _bearer is not null && ServerNow > _bearerUntil - TimeSpan.FromMinutes(1))
                {
                    try
                    {
                        await RenewAsync(Generation, ct).ConfigureAwait(false);
                    }
                    catch (VfsException)
                    {
                        // the request goes as it is, and its refusal says why
                    }
                }

                if (_bearer is { } bearer)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }

                break;
        }
    }

    /// <summary>Whether an address is the server's own (its scheme and host, with the port), not another's it sent a request on to.</summary>
    public bool OnServer(Uri url)
        => url.Scheme.Equals(Address.Base.Scheme, StringComparison.OrdinalIgnoreCase) && url.Authority.Equals(Address.Base.Authority, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// After a request seen at <paramref name="seen"/> (its <see cref="Generation"/>) was refused, or as the access token
    /// runs out: the platform asked again with the session's cookies, which renews the token while it lets it; else the
    /// browser, headless, on the profile that keeps the person's sign-in. Once for every request refused with it. Any other
    /// sign-in cannot be renewed; a renewal that needs a person throws a <see cref="VfsException"/> saying so (the source
    /// shows Degraded; Retry signs in with a window).
    /// </summary>
    public async Task RenewAsync(int seen, CancellationToken ct)
    {
        if (!CanRenew)
        {
            throw new VfsException(VfsError.Unavailable, $"Artifactory ({Url.Host}) no longer takes {Settings.Who}: give a new one and Retry the source.");
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
                throw new VfsException(VfsError.Unavailable, $"Artifactory ({Url.Host}) refused the session again just after it was renewed; Retry the source to sign in again.");
            }

            _renewedAt = DateTime.UtcNow;
            string? user = null;
            try
            {
                user = await SessionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new VfsException(VfsError.Unavailable, $"Artifactory ({Url.Host}) did not answer: {ex.Message}", ex);
            }

            if (user is null)
            {
                _logger.LogInformation("artifactory: the session for {Host} has run out; signing in again without a window", Url.Host);
                try
                {
                    await BrowserSignInAsync(interactive: false, _ => { }, ct).ConfigureAwait(false);
                }
                catch (SignInRequiredException ex)
                {
                    throw new VfsException(VfsError.Unavailable, $"The Artifactory sign-in has lapsed and AnyFS could not sign in again without you ({ex.Message}); Retry the source to sign in.", ex);
                }
            }
            else
            {
                User = user;
            }

            Interlocked.Increment(ref _generation);
            Renewed?.Invoke();
            Save();
        }
        finally
        {
            _renewGate.Release();
        }
    }

    /// <summary>The sign-in was renewed: what failed for want of one may be asked again.</summary>
    public event Action? Renewed;

    /// <summary>
    /// Keeps a signed-in web session's cookies: in the secret store for the next start, and for a source started from the
    /// dialog whose Test made them. A session that never signed in keeps nothing: a start that failed or was stopped before
    /// its sign-in holds no cookies, and those would take the place of a session the platform still takes.
    /// </summary>
    public void Save()
    {
        if (Method != ArtifactorySignIn.WebSession || !_signedIn)
        {
            return;
        }

        var json = CookieJar.Export(Cookies);
        Recent[Address.Base.AbsoluteUri] = (json, DateTime.UtcNow);
        try
        {
            _secrets?.Set(CookiesSecret, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning("artifactory: the session could not be kept for the next start: {Error}", ex.Message);
        }
    }

    public void Dispose()
    {
        Http.Dispose();
        _handler.Dispose();
        _renewGate.Dispose();
    }

    /// <summary>Notes how far the server's clock is from this computer's, from each answer's Date (a second's grain, which is finer than what it is used for), and the server's version, which every answer of Artifactory's names.</summary>
    private sealed class ClockHandler(ArtifactorySession session, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (response.Headers.Date is { } date && request.RequestUri is { } url && session.OnServer(url))
            {
                Volatile.Write(ref session._clockAheadTicks, (date - DateTimeOffset.UtcNow).Ticks);
            }

            // "Artifactory/7.161.26 77161260"
            if (session.Version is null && response.Headers.TryGetValues("X-JFrog-Version", out var named) && named.FirstOrDefault() is { } product
                && product.StartsWith("Artifactory/", StringComparison.OrdinalIgnoreCase))
            {
                session.Version = product["Artifactory/".Length..].Split(' ')[0];
            }

            return response;
        }
    }
}
