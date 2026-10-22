using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.ThreeDX;

/// <summary>What a test (or the Test button) changes about a 3DX session: what sends its requests, whether it may open a browser and with which profile, how soon a service's session may be renewed again, whether a session kept from before is used.</summary>
internal sealed record SessionOptions(HttpMessageHandler? Handler = null, bool Browser = true, TimeSpan? MinRenewalInterval = null, string? Profile = null, bool UseKept = true);

/// <summary>
/// A 3DX source's sign-in and the cookies that carry it (spec 7.6.2). 3DSpace and 3DSearch each keep a session of their own
/// (a JSESSIONID), both opened through 3DPassport, whose own cookie (CASTGC) opens either again without asking anyone while
/// it lasts; the organisation's identity provider may sit behind 3DPassport. Every request goes through one handler with
/// one cookie jar, following no redirects itself: a request 3DX sends to its sign-in instead of answering shows the session
/// has lapsed (<see cref="IsLapsed"/>), and <see cref="RenewAsync"/> goes through the sign-in for that service again, once
/// for everything that was waiting on it.
///
/// How it signs in is the settings' to say: Windows account (as yourself, or a user name and password) through
/// <see cref="SignInFlow"/>, answering Windows sign-ins and 3DPassport's own; the web session tries single sign-on the same
/// way first (as the person is signed in to Windows, for the organisation's own servers; their smartcard where the badge
/// sign-in asks for it), as Mount3DX does, and failing that opens a browser of AnyFS's own (<see cref="BrowserSession"/>,
/// over DevTools or Selenium WebDriver) for the person to sign in, and takes its cookies. The cookies are kept in the
/// source's secret store, so a restart starts with them while they last, and are handed from the settings dialog's Test to
/// the source it starts.
/// </summary>
internal sealed class ThreeDXSession : IDisposable
{
    public const string CookiesSecret = "3dx-cookies";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan FlowTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultMinRenewalInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecentFor = TimeSpan.FromMinutes(15);

    // the cookies of a sign-in just made, by 3DSpace address: the Test's, for the source started from the same dialog
    private static readonly ConcurrentDictionary<string, (string Cookies, DateTime At)> Recent = new(StringComparer.OrdinalIgnoreCase);

    private readonly ThreeDXSettings _settings;
    private readonly ILogger _logger;
    private readonly ISecretStore? _secrets;
    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _flowHttp;
    private readonly HttpMessageHandler? _badgeHandler;
    private readonly HttpClient? _badgeHttp;
    private readonly SemaphoreSlim _renewGate = new(1, 1);
    private readonly ConcurrentDictionary<string, int> _generations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _renewedAt = new(StringComparer.OrdinalIgnoreCase);
    private BrowserSession? _browser;

    // why a server refused the Windows account, once one has: the account is not offered again (a lockout)
    private string? _refused;

    // a badge sign-in in the background did not get through: no more until a sign-in succeeds (no PIN prompts piling up)
    private volatile bool _badgeHeld;
    private readonly bool _allowBrowser;
    private readonly TimeSpan _minRenewalInterval;
    private readonly string? _profile;
    private readonly bool _useKept;

    /// <param name="options">What a test changes; null for the real thing.</param>
    public ThreeDXSession(ThreeDXSettings settings, ILogger logger, ISecretStore? secrets, SessionOptions? options = null)
    {
        options ??= new SessionOptions();
        var handler = options.Handler;
        _allowBrowser = options.Browser;
        _minRenewalInterval = options.MinRenewalInterval ?? DefaultMinRenewalInterval;
        _profile = options.Profile;
        _useKept = options.UseKept;
        _settings = settings;
        _logger = logger;
        _secrets = secrets;
        Space = settings.Space ?? throw new SourceFailedException("The 3DSpace URL is not an http or https address.", permanent: true);
        Method = settings.EffectiveSignIn;
        Cookies = NewCookieContainer();
        var credentials = settings.Credentials();
        _handler = new SocketsHttpHandler
        {
            CookieContainer = Cookies,
            UseCookies = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 32,

            // the web session's single sign-on goes as the person only to the organisation's own servers; a Windows account to any that asks
            Credentials = Method == ThreeDXSignIn.WebSession ? new OwnServersOnly(credentials, Space) : credentials,
        };
        if (settings.TrustAnyCertificate)
        {
            _handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        HttpMessageHandler send = handler ?? _handler;
        Http = new HttpClient(send, disposeHandler: false) { Timeout = RequestTimeout };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"AnyFS/{typeof(ThreeDXSession).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        _flowHttp = new HttpClient(send, disposeHandler: false) { Timeout = FlowTimeout };
        Browserlike(_flowHttp);

        // a smartcard is for the person's own sign-in: the web session and as yourself, never a user name and password; the
        // person's certificates are looked at only when a badge sign-in comes
        if (Method != ThreeDXSignIn.Windows)
        {
            _badgeHandler = handler is null ? new LazyHandler(() => BadgeHandler(settings.TrustAnyCertificate)) : null;
            _badgeHttp = new HttpClient(handler ?? _badgeHandler!, disposeHandler: false) { Timeout = FlowTimeout };
            Browserlike(_badgeHttp);
        }
    }

    /// <summary>The badge sign-in's connections: the session's cookies, and the person's smartcard for a server that asks for a certificate.</summary>
    private SocketsHttpHandler BadgeHandler(bool trustAnyCertificate)
    {
        var badge = new SocketsHttpHandler
        {
            CookieContainer = Cookies,
            UseCookies = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        badge.SslOptions.ClientCertificates = ClientCertificates.Load();
        _logger.LogInformation("3dx: {Count} certificate(s) of yours can sign in to a server (a smartcard's first)", badge.SslOptions.ClientCertificates.Count);
        if (trustAnyCertificate)
        {
            badge.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return badge;
    }

    /// <summary>A handler made when the first request comes: the badge sign-in's, which reads the person's certificates.</summary>
    private sealed class LazyHandler(Func<HttpMessageHandler> make) : HttpMessageHandler
    {
        private readonly Lazy<HttpMessageInvoker> _inner = new(() => new HttpMessageInvoker(make(), disposeHandler: true));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _inner.Value.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing && _inner.IsValueCreated)
            {
                _inner.Value.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>The 3DSpace service, with a slash at the end.</summary>
    public Uri Space { get; }

    public ThreeDXSignIn Method { get; }

    /// <summary>For API requests: no redirects followed (a redirect to the sign-in is a lapse), the session's cookies sent.</summary>
    public HttpClient Http { get; }

    public CookieContainer Cookies { get; }

    /// <summary>The 3DSearch service once known (<see cref="ThreeDXClient"/> finds it), with a slash at the end; its session is renewed like 3DSpace's.</summary>
    public Uri? Search { get; set; }

    /// <summary>How the source signed in, for messages ("with Microsoft Edge", "single sign-on", "as CONTOSO\name").</summary>
    public string How { get; private set; } = "";

    public static CookieContainer NewCookieContainer() => new(capacity: 3000, perDomainCapacity: 300, maxCookieSize: 64 * 1024);

    private static void Browserlike(HttpClient http)
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd(SignInFlow.BrowserUserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    // --- signing in -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Signs in for the source's start or the Test: with the cookies of a sign-in just made or kept from the last run while
    /// they work (or open 3DSpace again through 3DPassport), else the way the settings say; a browser window only when
    /// <paramref name="interactive"/>. Throws <see cref="SignInRequiredException"/> when no sign-in can be had.
    /// </summary>
    public async Task SignInAsync(bool interactive, Action<string> progress, CancellationToken ct)
    {
        progress($"Signing in to {Space.Host}");
        var kept = !_useKept ? null : Recent.TryGetValue(Space.AbsoluteUri, out var recent) && DateTime.UtcNow - recent.At < RecentFor ? recent.Cookies : _secrets?.Get(CookiesSecret);
        var hadKept = kept is not null && Import(kept) > 0;
        if (hadKept && await AnswersAsync(Space, ct).ConfigureAwait(false))
        {
            How = "with the session kept from before";
            _logger.LogInformation("3dx: signed in to {Host} with the session kept from before", Space.Host);
            Save();
            return;
        }

        // through the sign-in without a browser: 3DPassport with a sign-in kept from before, a Windows sign-in where a server
        // asks for one, 3DPassport's own sign-in with a user name and password, the web session's single sign-on
        var result = await FlowAsync(Space, ct).ConfigureAwait(false);
        if (result.SignedIn)
        {
            How = Method switch
            {
                ThreeDXSignIn.WebSession => "by single sign-on",
                _ => $"as {_settings.Who}",
            };
            _logger.LogInformation("3dx: signed in to {Host} {How}{Kept}: {Route}", Space.Host, How, hadKept ? " (the sign-in kept from before)" : "", result.Route);
            Save();
            return;
        }

        if (Method != ThreeDXSignIn.WebSession)
        {
            _logger.LogInformation("3dx: the sign-in to {Host} as {Who} did not get through: {Route}", Space.Host, _settings.Who, result.Route);
            throw new SignInRequiredException($"Signing in to {Space.Host} as {_settings.Who}: {result.Problem}.{Advice(result)}", permanent: result.Refused);
        }

        if (!_allowBrowser)
        {
            throw new SignInRequiredException($"Single sign-on to {Space.Host} did not get through: {result.Problem}");
        }

        _logger.LogInformation("3dx: single sign-on to {Host} did not get through ({Problem}); signing in with a browser. Route: {Route}", Space.Host, result.Problem, result.Route);
        await BrowserSignInAsync(interactive, progress, ct).ConfigureAwait(false);
    }

    private static string Advice(FlowResult result)
        => result.Problem?.Contains("user name and password", StringComparison.Ordinal) == true
            ? " Choose Windows account and give the user name and password, or Web session to sign in in a browser."
            : result.Refused ? " Check the user name and password, or choose another way under Sign in with." : "";

    /// <summary>The web session's browser sign-in: the cookies of the browser AnyFS opens (headless first), then 3DSpace asked with them.</summary>
    private async Task BrowserSignInAsync(bool interactive, Action<string> progress, CancellationToken ct)
    {
        var browser = Browser();
        if (!browser.CanSignIn)
        {
            throw new SignInRequiredException("No Edge, Chrome, Chromium or Firefox was found for the web session's sign-in. Install one, set the Browser path under Advanced, or choose Windows account under Sign in with.", permanent: true);
        }

        var cookies = await browser.SignInAsync(interactive, progress, ct).ConfigureAwait(false);
        Add(Cookies, cookies);
        if (!await AnswersAsync(Space, ct).ConfigureAwait(false))
        {
            throw new SignInRequiredException($"{browser.BrowserName} signed in to {Space.Host}, but 3DSpace does not take its session from AnyFS (it sends AnyFS to the sign-in again)");
        }

        How = $"with {browser.BrowserName}";
        _logger.LogInformation("3dx: signed in to {Host} with {Browser} ({Count} cookies)", Space.Host, browser.BrowserName, cookies.Count);
        Save();
    }

    private BrowserSession Browser() => _browser ??= new BrowserSession(BrowserLocator.Find(_settings.Browser, "ANYFS_3DX_BROWSER"), new ThreeDXSignInTarget(Space, _settings.TrustAnyCertificate, _profile), _logger);

    /// <summary>
    /// The sign-in without a browser, from <paramref name="service"/>'s probe until the service answers. Once a server has
    /// refused the Windows account, it is not tried again until the source starts afresh: renewals giving a wrong password
    /// every few seconds would lock the account. In the background (<paramref name="renewal"/>) the badge sign-in, and the
    /// PIN Windows asks for with it, comes at most once until a sign-in succeeds: a PIN prompt nobody answers (overnight) is
    /// not joined by another every few minutes; the source shows Degraded and waits for a Retry.
    /// </summary>
    private async Task<FlowResult> FlowAsync(Uri service, CancellationToken ct, bool renewal = false)
    {
        if (_refused is { } why)
        {
            return new FlowResult(false, "", $"{why} (not tried again until the source is restarted)", Refused: true);
        }

        var badge = renewal && _badgeHeld ? null : _badgeHttp;
        var flow = new SignInFlow(_flowHttp, _logger, _settings.FormCredentials(), badge);
        FlowResult result;
        try
        {
            result = await flow.RunAsync(ProbeOf(service), (response, body) => IsAnswer(response, body, service), ct).ConfigureAwait(false);
            if (result.RefusedAtStart)
            {
                // as Mount3DX signs in: from the service's own page, whose answer (once signed in) is a page of the service;
                // then the probe again, which only a session answers
                _logger.LogInformation("3dx: {Probe} refuses a request without a session ({Problem}); signing in from {Service} instead", ProbeOf(service).AbsolutePath, result.Problem, service.AbsolutePath);
                result = await flow.RunAsync(service, (response, _) => response.RequestMessage?.RequestUri is { } at && IsUnder(at, service) && response.IsSuccessStatusCode, ct).ConfigureAwait(false);
                if (result.SignedIn && !await AnswersAsync(service, ct).ConfigureAwait(false))
                {
                    result = result with { SignedIn = false, Problem = $"the sign-in reached {service.AbsolutePath}, but {ProbeOf(service).AbsolutePath} still refuses AnyFS" };
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SignInRequiredException or System.Text.Json.JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            // a server in the sign-in that did not answer, or a badge sign-in that went wrong (a PIN nobody gave)
            result = new FlowResult(false, "", $"the sign-in did not get through: {ex.Message}");
        }

        if (result.Refused && Method != ThreeDXSignIn.WebSession)
        {
            _refused = result.Problem;
        }

        if (result.SignedIn)
        {
            _badgeHeld = false;
        }
        else if (renewal && flow.BadgeTaken)
        {
            _badgeHeld = true;
            _logger.LogInformation("3dx: the badge sign-in did not get through in the background ({Problem}); it is not tried again before a sign-in succeeds (Retry the source)", result.Problem);
        }

        return result;
    }

    // --- lapses and renewals ----------------------------------------------------------------------------------------

    /// <summary>The service a request goes to: 3DSearch's when it is under it, else 3DSpace's.</summary>
    public Uri ServiceOf(Uri request) => Search is { } search && IsUnder(request, search) ? search : Space;

    /// <summary>How many times the service's session has been renewed: a request remembers it, so that one renewal serves every request that lapsed with it.</summary>
    public int GenerationOf(Uri service) => _generations.GetValueOrDefault(service.AbsoluteUri);

    /// <summary>
    /// Whether 3DX answered a request to <paramref name="service"/> by sending it to the sign-in: a 401, a redirect away
    /// from the service (to 3DPassport or the identity provider) or to a login page, or a web page where data was asked
    /// for (a sign-in page served in place).
    /// </summary>
    public static bool IsLapsed(HttpResponseMessage response, Uri service, bool expectsJson)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return true;
        }

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            if (response.Headers.Location is not { } location)
            {
                return false;
            }

            var target = new Uri(response.RequestMessage?.RequestUri ?? service, location);
            return !IsUnder(target, service) || target.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase);
        }

        return expectsJson && response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType is { } type && type.Contains("html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// After a request to <paramref name="service"/> lapsed (seen at <paramref name="seen"/>, its <see cref="GenerationOf"/>):
    /// the sign-in again for that service, once for every request that lapsed with it. Through 3DPassport with the cookies
    /// held, or the settings' own sign-in; for the web session, a headless browser on its profile last. False when the
    /// service was renewed just before (3DX keeps refusing the session: no use renewing again at once); throws
    /// <see cref="SignInRequiredException"/> when the sign-in needs a person.
    /// </summary>
    public async Task<bool> RenewAsync(Uri service, int seen, CancellationToken ct)
    {
        await _renewGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = service.AbsoluteUri;
            if (GenerationOf(service) != seen)
            {
                return true;
            }

            if (_renewedAt.TryGetValue(key, out var at) && Stopwatch.GetElapsedTime(at) < _minRenewalInterval)
            {
                _logger.LogInformation("3dx: {Service} sent AnyFS to its sign-in again {Seconds:F0} s after the session was renewed; not renewing it again yet", service.AbsolutePath, Stopwatch.GetElapsedTime(at).TotalSeconds);
                return false;
            }

            _renewedAt[key] = Stopwatch.GetTimestamp();
            _logger.LogInformation("3dx: the session for {Host}{Path} has lapsed; signing in to it again", service.Host, service.AbsolutePath);
            var result = await FlowAsync(service, ct, renewal: true).ConfigureAwait(false);
            if (!result.SignedIn && Method == ThreeDXSignIn.WebSession && _allowBrowser && Browser().CanSignIn)
            {
                _logger.LogInformation("3dx: {Path} could not be signed in to again without a browser ({Problem}); renewing the web session headless", service.AbsolutePath, result.Problem);
                Add(Cookies, await Browser().SignInAsync(interactive: false, _ => { }, ct).ConfigureAwait(false));
                result = await FlowAsync(service, ct, renewal: true).ConfigureAwait(false);
            }

            if (!result.SignedIn)
            {
                _logger.LogInformation("3dx: renewing the session for {Path} failed: {Route}", service.AbsolutePath, result.Route);
                throw new SignInRequiredException($"the 3DX sign-in has lapsed and AnyFS could not sign in again without you ({result.Problem}); Retry the source to sign in", permanent: false);
            }

            _logger.LogInformation("3dx: signed in to {Host}{Path} again: {Route}", service.Host, service.AbsolutePath, result.Route);
            _generations.AddOrUpdate(key, 1, (_, n) => n + 1);
            Save();
            return true;
        }
        finally
        {
            _renewGate.Release();
        }
    }

    // --- probes ---------------------------------------------------------------------------------------------------------

    /// <summary>What answers only a signed-in session, per service: 3DSearch's search (which answers a query-less GET with an error of its own), 3DSpace's CSRF token.</summary>
    public Uri ProbeOf(Uri service) => Search is { } search && service == search ? SearchProbe(search) : SpaceProbe(service);

    /// <summary>3DSpace's CSRF token: data for a signed-in session, the sign-in for any other.</summary>
    public static Uri SpaceProbe(Uri space) => new(space, "resources/v1/application/CSRF");

    /// <summary>3DSearch's search, asked nothing: its "no query" error for a signed-in session (Mount3DX's ping), the sign-in for any other.</summary>
    public static Uri SearchProbe(Uri search) => new(search, "search");

    /// <summary>Whether the service itself answered (not a sign-in page): from under it, and data rather than a page; 3DSearch's "no query" error counts.</summary>
    public static bool IsAnswer(HttpResponseMessage response, string body, Uri service)
    {
        var url = response.RequestMessage?.RequestUri;
        if (url is null || !IsUnder(url, service) || (int)response.StatusCode is >= 300 and < 400 || response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return false;
        }

        return body.Contains("FS_INVALID_QUERY", StringComparison.Ordinal) || IsJson(response) && (int)response.StatusCode < 500;
    }

    public static bool IsJson(HttpResponseMessage response)
        => response.Content.Headers.ContentType?.MediaType is { } type && type.Contains("json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the service answers now with the cookies held, without signing in.</summary>
    private async Task<bool> AnswersAsync(Uri service, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(ProbeOf(service), ct).ConfigureAwait(false);
            return IsAnswer(response, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), service);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("3dx: {Host} did not answer: {Error}", service.Host, ex.Message);
            return false;
        }
    }

    public static bool IsUnder(Uri url, Uri service)
        => url.Scheme.Equals(service.Scheme, StringComparison.OrdinalIgnoreCase) && url.Host.Equals(service.Host, StringComparison.OrdinalIgnoreCase) && url.Port == service.Port
            && (url.AbsolutePath.TrimEnd('/') + "/").StartsWith(service.AbsolutePath, StringComparison.OrdinalIgnoreCase);

    // --- cookies ----------------------------------------------------------------------------------------------------------

    /// <summary>Keeps the cookies: in the secret store for the next start, and for a source started from the dialog whose Test made them.</summary>
    public void Save()
    {
        var json = Export(Cookies);
        Recent[Space.AbsoluteUri] = (json, DateTime.UtcNow);
        try
        {
            _secrets?.Set(CookiesSecret, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning("3dx: the session could not be kept for the next start: {Error}", ex.Message);
        }
    }

    public static void Add(CookieContainer jar, IEnumerable<SessionCookie> cookies)
    {
        foreach (var c in cookies)
        {
            try
            {
                jar.Add(new Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Domain) { Secure = c.Secure, HttpOnly = c.HttpOnly });
            }
            catch (CookieException)
            {
                // a cookie .NET will not hold (a name or value it refuses): the session goes without it
            }
        }
    }

    internal static string Export(CookieContainer jar)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (Cookie c in jar.GetAllCookies())
            {
                if (c.Expired)
                {
                    continue;
                }

                json.WriteStartObject();
                json.WriteString("name", c.Name);
                json.WriteString("value", c.Value);
                json.WriteString("domain", c.Domain);
                json.WriteString("path", c.Path);
                json.WriteBoolean("secure", c.Secure);
                json.WriteBoolean("httpOnly", c.HttpOnly);
                if (c.Expires != DateTime.MinValue)
                {
                    json.WriteString("expires", c.Expires.ToUniversalTime());
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The cookies <see cref="Export"/> wrote, into the session's jar (those since expired left out); how many.</summary>
    private int Import(string text)
    {
        var count = 0;
        try
        {
            using var doc = JsonDocument.Parse(text);
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var expires = c.TryGetProperty("expires", out var e) && e.TryGetDateTime(out var when) ? when : DateTime.MinValue;
                if (expires != DateTime.MinValue && expires < DateTime.UtcNow)
                {
                    continue;
                }

                try
                {
                    var cookie = new Cookie(c.GetProperty("name").GetString()!, c.GetProperty("value").GetString() ?? "", c.GetProperty("path").GetString() ?? "/", c.GetProperty("domain").GetString() ?? Space.Host)
                    {
                        Secure = c.GetProperty("secure").GetBoolean(),
                        HttpOnly = c.GetProperty("httpOnly").GetBoolean(),
                    };
                    if (expires != DateTime.MinValue)
                    {
                        cookie.Expires = expires;
                    }

                    Cookies.Add(cookie);
                    count++;
                }
                catch (CookieException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            _logger.LogInformation("3dx: the session kept from before could not be read; signing in afresh");
        }

        return count;
    }

    public void Dispose()
    {
        Http.Dispose();
        _flowHttp.Dispose();
        _badgeHttp?.Dispose();
        _badgeHandler?.Dispose();
        _handler.Dispose();
        _renewGate.Dispose();
    }

    /// <summary>
    /// The web session's single sign-on credentials: the Windows sign-in only for the organisation's own servers (the
    /// 3DSpace server itself, its domain, or a name without dots on its network), never for a server elsewhere a redirect
    /// leads to.
    /// </summary>
    internal sealed class OwnServersOnly(ICredentials inner, Uri space) : ICredentials
    {
        private readonly string _domain = Parent(space.Host);

        public NetworkCredential? GetCredential(Uri uri, string authType) => IsOwn(uri) ? inner.GetCredential(uri, authType) : null;

        internal bool IsOwn(Uri uri)
            => uri.Host.Equals(space.Host, StringComparison.OrdinalIgnoreCase)
                || !uri.Host.Contains('.') && !uri.Host.Contains(':')
                || (_domain.Length > 0 && (uri.Host.Equals(_domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + _domain, StringComparison.OrdinalIgnoreCase)));

        /// <summary>contoso.com from 3dx.contoso.com (the host itself when it has no parent of two labels or more; nothing for an address).</summary>
        private static string Parent(string host)
        {
            if (IPAddress.TryParse(host, out _))
            {
                return "";
            }

            var dot = host.IndexOf('.');
            var parent = dot < 0 ? host : host[(dot + 1)..];
            return parent.Contains('.') ? parent : host;
        }
    }
}
