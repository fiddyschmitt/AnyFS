using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Atlassian;

/// <summary>
/// What a test (or the Test button) changes about a Jira or Confluence session: what sends its requests; for the web
/// session, what stands in for the browser (the cookies a sign-in in it would give; interactive or not); and whether a
/// session kept from before is used.
/// </summary>
internal sealed record AtlassianStandIn(Func<HttpMessageHandler> Handler, Func<bool, CancellationToken, Task<IReadOnlyList<SessionCookie>>>? Browser = null, bool UseKept = true);

/// <summary>
/// An Atlassian source's sign-in (Jira, Confluence, Bitbucket, Bamboo) and the client its requests go through (spec 7.10).
/// First it finds the installation behind the address (the host, or a context path below it); then it signs in the way the
/// settings say. A personal access token goes on every request as a bearer token; a refused token is final. The web session
/// takes the cookies of a browser of AnyFS's own signed in on the installation's own page (<see cref="AtlassianSignInTarget"/>);
/// a user name and password go through the product's own login form (Seraph's, or Bitbucket's), which gives the same session
/// cookie, or, where the form is not there, as Basic authentication on every request where the server allows it. The
/// products' REST APIs take the session cookie as they take a token. A refused request signs in again (the browser headless,
/// the form again), once for every request refused with it. A session's cookies are kept in the source's secret store for
/// the next start, and handed from the settings dialog's Test to the source it starts. The client follows no redirects itself,
/// so a login page in place of data is seen for what it is.
/// </summary>
internal sealed class AtlassianSession : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan RecentFor = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinRenewalInterval = TimeSpan.FromSeconds(30);

    // the cookies of a sign-in just made, by product and installation: the Test's, for the source started from the same dialog
    private static readonly ConcurrentDictionary<string, (string Cookies, DateTime At)> Recent = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger _logger;
    private readonly ISecretStore? _secrets;
    private readonly AtlassianStandIn? _standIn;
    private readonly HttpMessageHandler _handler;
    private readonly SemaphoreSlim _renewGate = new(1, 1);
    private BrowserSession? _browser;
    private DateTime _renewedAt;
    private string? _basic;

    // why the server refused the account's password, once it has: it is not given again (a CAPTCHA, a lockout)
    private string? _refused;
    private int _generation;

    /// <param name="watcher">Who hears of each request made (<see cref="ISourceContext.UpstreamWatcher"/>).</param>
    public AtlassianSession(AtlassianProduct product, IAtlassianSettings settings, ILogger logger, AtlassianStandIn? standIn = null, ISecretStore? secrets = null, IUpstreamWatcher? watcher = null)
    {
        Product = product;
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

        _handler = new CookieHandler(Cookies, UpstreamRequestsHandler.Around(inner, watcher));
        Http = new HttpClient(_handler, disposeHandler: false) { Timeout = RequestTimeout };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"AnyFS/{typeof(AtlassianSession).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        Address = AtlassianAddress.Candidates(Url).First();
    }

    public AtlassianProduct Product { get; }

    public IAtlassianSettings Settings { get; }

    /// <summary>The address as given, tidied.</summary>
    public Uri Url { get; }

    public AtlassianSignIn Method { get; }

    /// <summary>The installation and the rest of the address below it, once <see cref="FindServerAsync"/> has found them.</summary>
    public AtlassianAddress Address { get; private set; }

    /// <summary>The installation's version, where it said (its application links manifest).</summary>
    public string? Version { get; private set; }

    /// <summary>Who is signed in, once signed in.</summary>
    public AtlassianUser? User { get; private set; }

    /// <summary>For every request: no redirects followed, the session's cookies sent and kept.</summary>
    public HttpClient Http { get; }

    public CookieContainer Cookies { get; }

    /// <summary>How the source signed in, for messages ("with a personal access token", "with Microsoft Edge").</summary>
    public string How { get; private set; } = "";

    /// <summary>How many times the sign-in has been renewed: a request remembers it, so one renewal serves every request that lapsed with it.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Raised when the sign-in has been renewed.</summary>
    public event Action? Renewed;

    /// <summary>Whether a refused request may be signed in again: not with a token, which only a person can replace.</summary>
    public bool CanRenew => Method != AtlassianSignIn.Token;

    /// <summary>The product's name, as messages say it.</summary>
    public string ProductName => Product.Name();

    private string LogName => AtlassianSignInTarget.Name(Product);

    private string CookiesSecret => LogName + "-cookies";

    /// <summary>Who the sign-in goes as, for messages.</summary>
    public string Who => Method switch
    {
        AtlassianSignIn.WebSession => "the web session",
        AtlassianSignIn.Password => $"the account {Settings.User}",
        _ => "the personal access token",
    };

    /// <summary>
    /// The who-am-I call, relative to the installation: Jira's <c>rest/api/2/myself</c>, Confluence's <c>rest/api/user/current</c>,
    /// Bamboo's <c>rest/api/latest/currentUser</c>; Bitbucket has none, so its application properties (data to anyone) are asked
    /// for, and the account read from the <c>X-AUSERNAME</c> header every answer of Seraph's carries (<see cref="NamedUser"/>).
    /// </summary>
    public static string WhoPath(AtlassianProduct product) => product switch
    {
        AtlassianProduct.Jira => "rest/api/2/myself",
        AtlassianProduct.Confluence => "rest/api/user/current",
        AtlassianProduct.Bitbucket => "rest/api/1.0/application-properties",
        _ => "rest/api/latest/currentUser",
    };

    /// <summary>
    /// For a product whose who-am-I answer names the account in its <c>X-AUSERNAME</c> header rather than its data (Bitbucket):
    /// the name, empty for nobody (<c>anonymous</c>, or no header); null for a product whose answer's data says.
    /// </summary>
    public static string? NamedUser(AtlassianProduct product, HttpResponseMessage response)
    {
        if (product != AtlassianProduct.Bitbucket)
        {
            return null;
        }

        var name = response.Headers.TryGetValues("X-AUSERNAME", out var names) ? names.FirstOrDefault()?.Trim() ?? "" : "";
        return name.Equals("anonymous", StringComparison.OrdinalIgnoreCase) ? "" : name;
    }

    /// <summary>
    /// Finds the installation behind the address: the first of its candidates (the host, then each longer path) whose
    /// application links manifest names this product, or failing a manifest, whose <c>status</c> answers as an Atlassian
    /// server does. Where none answers so (a server behind a sign-in proxy redirects them all), the host itself. An
    /// installation of the other product says so.
    /// </summary>
    public async Task FindServerAsync(CancellationToken ct)
    {
        string? other = null;
        AtlassianAddress? running = null;
        foreach (var candidate in AtlassianAddress.Candidates(Url))
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, candidate.Page("rest/applinks/1.0/manifest")))
                {
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode && await ManifestAsync(response, ct).ConfigureAwait(false) is { } manifest)
                    {
                        if (AtlassianProducts.OfManifest(manifest.TypeId) == Product)
                        {
                            Address = candidate;
                            Version = manifest.Version;
                            _logger.LogInformation("{Product}: the installation at {Server} ({Version})", LogName, candidate.Server, manifest.Version);
                            return;
                        }

                        other ??= manifest.TypeId;
                        continue;
                    }
                }

                if (running is null)
                {
                    using var status = await Http.GetAsync(candidate.Page("status"), ct).ConfigureAwait(false);
                    if (status.IsSuccessStatusCode && status.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
                        && (await status.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Contains("\"state\"", StringComparison.Ordinal))
                    {
                        running = candidate;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new SourceFailedException($"{ProductName} ({Url.Host}) did not answer: {ex.Message}", inner: ex);
            }
        }

        if (running is not null)
        {
            Address = running;
            _logger.LogInformation("{Product}: an Atlassian server at {Server} (it gave no application links manifest)", LogName, running.Server);
            return;
        }

        if (other is not null)
        {
            var named = AtlassianProducts.OfManifest(other)?.Name() ?? other;
            throw new SourceFailedException($"{Url.Host} is {named}, not {ProductName}: add it as a {named} source.", permanent: true);
        }

        Address = AtlassianAddress.Candidates(Url).First();
        _logger.LogInformation("{Product}: no installation answered below {Url} without a sign-in; taking {Server} as the installation", LogName, Url, Address.Server);
    }

    private sealed record Manifest(string TypeId, string? Version);

    /// <summary>An application links manifest's product and version (JSON, or the XML a server sends whatever was asked), or null for anything else.</summary>
    private static async Task<Manifest?> ManifestAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            if (text.TrimStart().StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.Str("typeId") is { Length: > 0 } type ? new Manifest(type, doc.RootElement.Str("version")) : null;
            }

            if (text.TrimStart().StartsWith('<'))
            {
                var xml = System.Xml.Linq.XDocument.Parse(text);
                var type = xml.Root?.Element("typeId")?.Value;
                return string.IsNullOrEmpty(type) ? null : new Manifest(type, xml.Root?.Element("version")?.Value);
            }
        }
        catch (Exception ex) when (ex is JsonException or System.Xml.XmlException)
        {
        }

        return null;
    }

    /// <summary>
    /// Signs in for the source's start or the Test: a session's cookies kept from before (or just made by the Test) while the
    /// server takes them, else the way the settings say, with a browser window only when <paramref name="interactive"/>.
    /// Throws <see cref="SourceFailedException"/> (permanent for a refusal only new settings can change) when no sign-in
    /// can be had.
    /// </summary>
    public async Task SignInAsync(bool interactive, Action<string> progress, CancellationToken ct)
    {
        progress($"Signing in to {Url.Host}");
        await FindServerAsync(ct).ConfigureAwait(false);
        switch (Method)
        {
            case AtlassianSignIn.Token:
                if (Settings.Token is null)
                {
                    throw new SourceFailedException("Give a personal access token (Sign in with: Personal access token), or choose another way to sign in.", permanent: true);
                }

                User = await WhoAsync(ct).ConfigureAwait(false)
                    ?? throw new SourceFailedException($"{ProductName} ({Url.Host}) refused the personal access token: it may have expired or been revoked.", permanent: true);
                How = "with a personal access token";
                break;

            case AtlassianSignIn.WebSession:
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
                if (await KeptAsync(ct).ConfigureAwait(false))
                {
                    How = "with the session kept from before";
                    break;
                }

                if (await PasswordSignInAsync(ct).ConfigureAwait(false) is { } problem)
                {
                    throw new SourceFailedException(problem, permanent: _refused is not null);
                }

                break;
        }

        _logger.LogInformation("{Product}: signed in to {Host} as {User} {How}", LogName, Url.Host, User!.Name, How);
        Save();
    }

    /// <summary>The cookies of a sign-in just made, or kept from the last run, while the server takes them.</summary>
    private async Task<bool> KeptAsync(CancellationToken ct)
    {
        if (_standIn is { UseKept: false })
        {
            return false;
        }

        // a sign-in just made (the Test's) first, then the one kept from the last run
        var recent = Recent.TryGetValue(RecentKey, out var r) && DateTime.UtcNow - r.At < RecentFor ? r.Cookies : null;
        foreach (var kept in new[] { recent, _secrets?.Get(CookiesSecret) }.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            CookieJar.Clear(Cookies);
            if (CookieJar.Import(Cookies, kept, Address.Base.Host) <= 0)
            {
                continue;
            }

            if (await WhoAsync(ct).ConfigureAwait(false) is { } user && (Method != AtlassianSignIn.Password || SameAccount(user)))
            {
                User = user;
                return true;
            }
        }

        _logger.LogInformation("{Product}: no session kept from before for {Host} that the server still takes", LogName, Url.Host);
        CookieJar.Clear(Cookies);
        return false;
    }

    private string RecentKey => $"{LogName}|{Address.Base.AbsoluteUri}";

    /// <summary>Whether a kept session is the account the settings name (a changed user name signs in afresh).</summary>
    private bool SameAccount(AtlassianUser user) => Settings.User is not { } wanted || user.Name.Equals(wanted.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The web session's sign-in in a browser of AnyFS's own: its cookies, then the product asked who they are.</summary>
    private async Task BrowserSignInAsync(bool interactive, Action<string> progress, CancellationToken ct)
    {
        var cookies = await BrowserCookiesAsync(interactive, progress, ct).ConfigureAwait(false);
        CookieJar.Clear(Cookies);
        CookieJar.Add(Cookies, cookies);
        User = await WhoAsync(ct).ConfigureAwait(false)
            ?? throw new SignInRequiredException($"{BrowserName} signed in to {Url.Host}, but {ProductName}'s REST API does not take its session from AnyFS");
        How = $"with {BrowserName}";
        _logger.LogInformation("{Product}: signed in to {Host} with {Browser} ({Count} cookies)", LogName, Url.Host, BrowserName, cookies.Count);
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
            throw new SignInRequiredException("No Edge, Chrome, Chromium or Firefox was found for the web session's sign-in. Install one, set the Browser path under Advanced, or sign in with a personal access token.", permanent: true);
        }

        return session.SignInAsync(interactive, progress, ct);
    }

    private string BrowserName => _standIn?.Browser is not null ? "the browser" : Browser().BrowserName;

    private BrowserSession Browser()
        => _browser ??= new BrowserSession(
            BrowserLocator.Find(Settings.Browser, Product.Variable("BROWSER")),
            new AtlassianSignInTarget(Product, Address.Base, Settings.TrustAnyCertificate),
            _logger);

    // --- the user name and password -----------------------------------------------------------------------------------

    /// <summary>
    /// The user name and password: the product's own login form (Seraph's: Jira's <c>login.jsp</c>, Confluence's
    /// <c>dologin.action</c>), whose session cookie the REST API takes; where the server shows no form to post to (single
    /// sign-on only), Basic authentication on every request, where the server allows it. Null when signed in, else what
    /// was tried and how each went. A password the server refused is not given again until the source restarts: a renewal
    /// every few seconds would have the server ask for a CAPTCHA, or lock the account.
    /// </summary>
    private async Task<string?> PasswordSignInAsync(CancellationToken ct)
    {
        if (_refused is { } why)
        {
            return $"{why} (not tried again until the source is restarted)";
        }

        var user = (Settings.User ?? "").Trim();
        if (user.Length == 0)
        {
            _refused = "Give the user name to sign in as";
            return _refused;
        }

        CookieJar.Clear(Cookies);
        _basic = null;
        string form;
        try
        {
            form = await LoginFormAsync(user, Settings.Secret ?? "", ct).ConfigureAwait(false) is not { } problem ? "" : problem;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return $"{ProductName} ({Url.Host}) did not answer the sign-in: {ex.Message}";
        }

        if (form.Length == 0)
        {
            How = "with the account's password";
            return null;
        }

        if (_refused is not null)
        {
            return _refused;
        }

        // no form to post to: Basic authentication, where the server takes it
        _basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{Settings.Secret}"));
        CookieJar.Clear(Cookies);
        var (who, refusal) = await BasicAsync(ct).ConfigureAwait(false);
        if (who is not null)
        {
            User = who;
            How = "with the account's password (Basic authentication)";
            return null;
        }

        _basic = null;
        if (refusal is { } basicRefused && basicRefused.Contains("refused the user name", StringComparison.Ordinal))
        {
            _refused = $"{ProductName} ({Url.Host}) refused the user name and password";
            return _refused;
        }

        return $"{ProductName} ({Url.Host}) took no user name and password: its login form {form}; Basic authentication {refusal}. Sign in with a personal access token or a web session.";
    }

    /// <summary>
    /// The login, as the server's own page does it: the two-step verification login of Jira 10.2+ and Confluence 9.1+
    /// (<c>rest/tsv/latest/authenticate</c>, on by default there), else Seraph's form, as a browser posts it with
    /// "Remember me" ticked. Null when signed in (<see cref="User"/> set), else why not, the refusals that are final kept
    /// in <see cref="_refused"/>.
    /// </summary>
    private async Task<string?> LoginFormAsync(string user, string password, CancellationToken ct)
    {
        var tsv = await TwoStepLoginAsync(user, password, ct).ConfigureAwait(false);
        if (tsv is null || _refused is not null)
        {
            return tsv;
        }

        _logger.LogDebug("{Product}: the two-step verification login {Problem}; trying the login form", LogName, tsv);
        CookieJar.Clear(Cookies);
        return await SeraphFormAsync(user, password, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The two-step verification login (Jira 10.2+, Confluence 9.1+): the user name and password as JSON, a session cookie
    /// back. Null when signed in, else why not; an account that must also give a code from an authenticator app is refused
    /// for good here (only the web session can answer that).
    /// </summary>
    private async Task<string?> TwoStepLoginAsync(string user, string password, CancellationToken ct)
    {
        var body = new System.Text.Json.Nodes.JsonObject { ["username"] = user, ["password"] = password }.ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, Address.Page("rest/tsv/latest/authenticate")) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("X-Atlassian-Token", "no-check");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        _logger.LogDebug("{Product}: the two-step verification login answered {Status}", LogName, (int)response.StatusCode);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed || (int)response.StatusCode is >= 300 and < 400)
        {
            return "is not there";
        }

        // a sign-in that got through is one whatever its answer says (an answer may name the second step it did not need)
        if (response.IsSuccessStatusCode && await WhoAsync(ct).ConfigureAwait(false) is { } who)
        {
            if (!SameAccount(who))
            {
                return $"signed in as {who.Name}, not {user}";
            }

            User = who;
            return null;
        }

        if (text.Contains("totp", StringComparison.OrdinalIgnoreCase) || text.Contains("verification code", StringComparison.OrdinalIgnoreCase))
        {
            _refused = $"{ProductName} ({Url.Host}) asks this account for a code from an authenticator app as well as its password: sign in with a web session or a personal access token";
            return _refused;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            if (text.Contains("captcha", StringComparison.OrdinalIgnoreCase))
            {
                _refused = $"{ProductName} ({Url.Host}) asks for a CAPTCHA before it takes this account's password again (too many failed sign-ins): sign in once in a browser, or use a personal access token";
                return _refused;
            }

            // a disabled login form answers so too: the Seraph form or Basic may still get in, so not final here
            return $"refused ({(int)response.StatusCode}{(AtlassianApi.Message(text) is { } m ? ": " + m : "")})";
        }

        return response.IsSuccessStatusCode ? "gave no session the REST API takes" : $"answered {(int)response.StatusCode}";
    }

    /// <summary>
    /// The product's login form (Jira's <c>login.jsp</c>, Confluence's <c>dologin.action</c>, Bamboo's
    /// <c>userlogin!doDefault.action</c>, all Seraph's; Bitbucket's <c>j_atl_security_check</c>, Spring Security's with
    /// Seraph's headers): null when signed in, else why not.
    /// </summary>
    private async Task<string?> SeraphFormAsync(string user, string password, CancellationToken ct)
    {
        var (page, fields) = Product switch
        {
            AtlassianProduct.Jira => ("login.jsp", new List<KeyValuePair<string, string>> { new("os_username", user), new("os_password", password), new("os_cookie", "true"), new("os_destination", ""), new("login", "Log In") }),
            AtlassianProduct.Confluence => ("dologin.action", new List<KeyValuePair<string, string>> { new("os_username", user), new("os_password", password), new("os_cookie", "true"), new("os_destination", ""), new("login", "Log in") }),
            AtlassianProduct.Bitbucket => ("j_atl_security_check", new List<KeyValuePair<string, string>> { new("j_username", user), new("j_password", password), new("_atl_remember_me", "on"), new("submit", "Log in") }),
            _ => ("userlogin!doDefault.action", new List<KeyValuePair<string, string>> { new("os_username", user), new("os_password", password), new("os_cookie", "true"), new("checkBoxFields", "os_cookie"), new("os_destination", ""), new("save", "Log in") }),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Address.Page(page)) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.TryAddWithoutValidation("X-Atlassian-Token", "no-check");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var reason = response.Headers.TryGetValues("X-Seraph-LoginReason", out var reasons) ? string.Join(",", reasons) : null;
        _logger.LogDebug("{Product}: the login form answered {Status}, login reason {Reason}", LogName, (int)response.StatusCode, reason ?? "(none)");
        if (reason is not null && reason.Contains("AUTHENTICATION_DENIED", StringComparison.OrdinalIgnoreCase))
        {
            _refused = $"{ProductName} ({Url.Host}) asks for a CAPTCHA before it takes this account's password again (too many failed sign-ins): sign in once in a browser, or use a personal access token";
            return _refused;
        }

        if (reason is not null && reason.Contains("AUTHENTICATED_FAILED", StringComparison.OrdinalIgnoreCase))
        {
            _refused = $"{ProductName} ({Url.Host}) refused the user name and password";
            return _refused;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "is not there";
        }

        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location
            && (location.IsAbsoluteUri ? location : new Uri(Address.Base, location)) is var to && !to.Host.Equals(Address.Base.Host, StringComparison.OrdinalIgnoreCase))
        {
            return $"sends the browser to single sign-on ({to.Host})";
        }

        if (await WhoAsync(ct).ConfigureAwait(false) is not { } who)
        {
            return reason is null ? "did not sign in (the server may sign people in through single sign-on only)" : $"did not sign in ({reason})";
        }

        if (!SameAccount(who))
        {
            return $"signed in as {who.Name}, not {user}";
        }

        User = who;
        return null;
    }

    /// <summary>The who-am-I call with Basic authentication: who it signs in as, else why not.</summary>
    private async Task<(AtlassianUser? User, string? Refusal)> BasicAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Address.Page(WhoPath(Product)));
        await AuthorizeAsync(request, ct).ConfigureAwait(false);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (await UserOfAsync(response, body, ct).ConfigureAwait(false) is { } user)
            {
                return (user, null);
            }
        }

        if (body.Contains("Basic Authentication has been disabled", StringComparison.OrdinalIgnoreCase) || body.Contains("basic auth", StringComparison.OrdinalIgnoreCase) && body.Contains("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "is turned off on the server");
        }

        return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? (null, response.Headers.TryGetValues("X-Seraph-LoginReason", out var reasons) && reasons.Any(r => r.Contains("AUTHENTICATED_FAILED", StringComparison.OrdinalIgnoreCase))
                ? "refused the user name and password"
                : $"answered {(int)response.StatusCode}")
            : (null, $"answered {(int)response.StatusCode}");
    }

    /// <summary>
    /// The signed-in account, or null where the server refuses the sign-in (a 401, a redirect to its login page, the
    /// anonymous user) or serves a page in place of data (a sign-in proxy's login page, at 200). Throws for a server that
    /// cannot answer (a 500).
    /// </summary>
    private async Task<AtlassianUser?> WhoAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Address.Page(WhoPath(Product)));
        await AuthorizeAsync(request, ct).ConfigureAwait(false);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || (int)response.StatusCode is >= 300 and < 400)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SourceFailedException($"{ProductName} ({Url.Host}) answered {(int)response.StatusCode} when asked who is signed in.");
        }

        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
        {
            _logger.LogDebug("{Product}: {Host} answered a page ({Type}), not data, when asked who is signed in: nobody is", LogName, Url.Host, response.Content.Headers.ContentType?.MediaType ?? "no type");
            return null;
        }

        return await UserOfAsync(response, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The account a who-am-I answer names: in its data, or (Bitbucket) in its <c>X-AUSERNAME</c> header, the display name then
    /// asked of the users call, the name alone where that cannot be had.
    /// </summary>
    private async Task<AtlassianUser?> UserOfAsync(HttpResponseMessage response, string body, CancellationToken ct)
    {
        if (NamedUser(Product, response) is { } named)
        {
            if (named.Length == 0)
            {
                return null;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Address.Page("rest/api/1.0/users/" + Uri.EscapeDataString(named)));
                await AuthorizeAsync(request, ct).ConfigureAwait(false);
                using var who = await Http.SendAsync(request, ct).ConfigureAwait(false);
                if (who.IsSuccessStatusCode && who.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    using var doc = JsonDocument.Parse(await who.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (AtlassianUser.Parse(Product, doc.RootElement) is { } user)
                    {
                        return user with { Name = named };
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogDebug("{Product}: the account {Name} could not be read ({Error}); its name serves", LogName, named, ex.Message);
            }

            return new AtlassianUser(named, named);
        }

        using var data = JsonDocument.Parse(body);
        return AtlassianUser.Parse(Product, data.RootElement);
    }

    /// <summary>Adds what the sign-in needs to a request: the token's or Basic's header (a session's cookies go by themselves).</summary>
    public ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Method == AtlassianSignIn.Token && Settings.Token is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else if (_basic is { } basic)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Whether a response was answered for nobody: Seraph names the user it served in <c>X-AUSERNAME</c>, and a lapsed
    /// session is served as the anonymous user where the site lets anonymous people read (less data, not a 401).
    /// </summary>
    public bool ServedAnonymously(HttpResponseMessage response)
        => User is not null && response.Headers.TryGetValues("X-AUSERNAME", out var names) && names.Any(n => n.Equals("anonymous", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// After a request seen at <paramref name="seen"/> (its <see cref="Generation"/>) was refused: signs in again once for
    /// every request refused with it, without anyone (the browser headless, on the profile that keeps the person's
    /// sign-in; the login form again). A token cannot be renewed; a renewal that needs a person, or a server that refuses
    /// the session again at once, throws a <see cref="VfsException"/> saying so (the source shows Degraded; Retry signs in
    /// with a window).
    /// </summary>
    public async Task RenewAsync(int seen, CancellationToken ct)
    {
        if (!CanRenew)
        {
            throw new VfsException(VfsError.Unavailable, $"{ProductName} ({Url.Host}) no longer takes {Who}: give a new one and Retry the source.");
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
                throw new VfsException(VfsError.Unavailable, $"{ProductName} ({Url.Host}) refused the session again just after it was renewed; Retry the source to sign in again.");
            }

            _renewedAt = DateTime.UtcNow;
            _logger.LogInformation("{Product}: the session for {Host} was refused; signing in again without a window", LogName, Url.Host);
            try
            {
                if (Method == AtlassianSignIn.WebSession)
                {
                    await BrowserSignInAsync(interactive: false, _ => { }, ct).ConfigureAwait(false);
                }
                else if (await PasswordSignInAsync(ct).ConfigureAwait(false) is { } problem)
                {
                    throw new VfsException(VfsError.Unavailable, $"The {ProductName} sign-in has lapsed and signing in again did not get through: {problem}");
                }
            }
            catch (SignInRequiredException ex)
            {
                throw new VfsException(VfsError.Unavailable, $"The {ProductName} sign-in has lapsed and AnyFS could not sign in again without you ({ex.Message}); Retry the source to sign in.", ex);
            }
            catch (SourceFailedException ex)
            {
                // the who-am-I call could not be answered (a 500): the request's failure, as the API reports one
                throw new VfsException(VfsError.Unavailable, $"The {ProductName} sign-in has lapsed and signing in again did not get through: {ex.Message}", ex);
            }

            Interlocked.Increment(ref _generation);
            Save();
            Renewed?.Invoke();
        }
        finally
        {
            _renewGate.Release();
        }
    }

    /// <summary>Keeps a session's cookies: in the secret store for the next start, and for a source started from the dialog whose Test made them.</summary>
    public void Save()
    {
        if (Method == AtlassianSignIn.Token || _basic is not null)
        {
            return;
        }

        var json = CookieJar.Export(Cookies);
        Recent[RecentKey] = (json, DateTime.UtcNow);
        try
        {
            _secrets?.Set(CookiesSecret, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning("{Product}: the session could not be kept for the next start: {Error}", LogName, ex.Message);
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
    /// one jar holds the browser's or the login form's cookies and the server's renewals of them. A request that carries
    /// its own authorisation (a token, Basic) goes without them: the server ends the session such a request makes at once.
    /// </summary>
    private sealed class CookieHandler(CookieContainer jar, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!;
            var header = request.Headers.Authorization is null ? jar.GetCookieHeader(url) : "";
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
