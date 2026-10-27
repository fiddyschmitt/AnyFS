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

    // why every way in refused the account's password, once it has: it is not given again (a lockout)
    private string? _refused;
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

            // the Windows sign-ins answer GitLab's Kerberos challenge (Negotiate) with these; nothing else asks for them
            if (Method is GitLabSignIn.WindowsCurrent or GitLabSignIn.Windows)
            {
                sockets.Credentials = settings.Credentials();
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
                if (Method == GitLabSignIn.WindowsCurrent && !OperatingSystem.IsWindows())
                {
                    throw new SourceFailedException("Signing in as yourself works on Windows only: choose Windows account and give a user name and password, or another way.", permanent: true);
                }

                if (await KeptAsync(ct).ConfigureAwait(false))
                {
                    How = "with the session kept from before";
                    break;
                }

                if (await WindowsSignInAsync(ct).ConfigureAwait(false) is { } problem)
                {
                    throw new SourceFailedException(problem, permanent: _refused is not null);
                }

                break;
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

    // --- the Windows sign-ins ----------------------------------------------------------------------------------------

    /// <summary>
    /// The Windows sign-ins (spike B): GitLab's Kerberos sign-in, as its Kerberos button does it, the handler answering the
    /// Negotiate challenge with the person's Windows sign-in or the account given; then, for a user name and password, an
    /// LDAP form and GitLab's own password form, in that order, while the server offers them. Null when signed in (then
    /// <see cref="User"/> and <see cref="How"/> say who and how), else what was tried and how each went. A password every
    /// way refused is not given again until the source restarts: renewals every few seconds would lock the account.
    /// </summary>
    private async Task<string?> WindowsSignInAsync(CancellationToken ct)
    {
        if (_refused is { } why)
        {
            return $"{why} (not tried again until the source is restarted)";
        }

        CookieJar.Clear(Cookies);
        GitLabSignInPage page;
        try
        {
            page = await SignInPageAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return $"GitLab ({Url.Host}) did not answer: {ex.Message}";
        }

        var tried = new List<string>();
        var refusals = 0;
        if (page.Kerberos)
        {
            if (await KerberosAsync(page, ct).ConfigureAwait(false) is not { } kerberos)
            {
                How = $"as {Settings.Who} (Kerberos)";
                return null;
            }

            tried.Add("Kerberos: " + kerberos);
            CookieJar.Clear(Cookies);
            page = await SignInPageAsync(ct).ConfigureAwait(false);
        }
        else
        {
            tried.Add("the server offers no Kerberos sign-in");
        }

        if (Method == GitLabSignIn.Windows && Settings.FormCredentials() is { } account)
        {
            var forms = page.LdapForms.Select(f => (Name: "LDAP", Action: f, Fields: new[] { ("username", account.UserName), ("password", account.Password), ("remember_me", "1") })).ToList();
            if (page.PasswordForm)
            {
                forms.Add(("GitLab's own sign-in", "/users/sign_in", [("user[login]", account.UserName), ("user[password]", account.Password), ("user[remember_me]", "1")]));
            }

            foreach (var (name, action, fields) in forms)
            {
                var result = await FormAsync(action, page.Csrf, fields, ct).ConfigureAwait(false);
                if (result is null)
                {
                    How = $"as {Settings.Who} ({name})";
                    return null;
                }

                refusals += result.StartsWith("refused", StringComparison.Ordinal) ? 1 : 0;
                tried.Add($"{name}: {result}");
                CookieJar.Clear(Cookies);
                page = await SignInPageAsync(ct).ConfigureAwait(false);
            }

            if (forms.Count > 0 && refusals == forms.Count)
            {
                _refused = $"GitLab ({Url.Host}) refused the user name and password of {account.UserName}";
            }
        }

        _logger.LogInformation("gitlab: the Windows sign-in to {Host} as {Who} did not get through (the server offers {Offers}): {Tried}", Url.Host, Settings.Who, page.Offers(), string.Join("; ", tried));
        return $"Signing in to {Url.Host} as {Settings.Who} did not get through: {string.Join("; ", tried)}."
            + (Method == GitLabSignIn.WindowsCurrent && !page.Kerberos ? " Choose Web session to sign in the way the server's own page offers." : "");
    }

    /// <summary>GitLab's sign-in page, with a fresh session for its CSRF token.</summary>
    private async Task<GitLabSignInPage> SignInPageAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync(Address.Page("users/sign_in"), ct).ConfigureAwait(false);
        return GitLabSignInPage.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// GitLab's Kerberos sign-in: the button's form posted, then its Negotiate page, which the handler answers with the
    /// Windows credentials (a ticket for HTTP/&lt;the server's name&gt;), and its callback, which signs the session in. Null
    /// when signed in, else why not.
    /// </summary>
    private async Task<string?> KerberosAsync(GitLabSignInPage page, CancellationToken ct)
    {
        var next = await PostFormAsync("/users/auth/kerberos", page.Csrf, [], ct).ConfigureAwait(false);
        for (var hop = 0; next is not null && hop < 6; hop++)
        {
            using var response = await Http.GetAsync(next, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return IPAddress.TryParse(Url.Host, out _)
                    ? "GitLab asked for a Kerberos ticket, which needs the server's name, not its address"
                    : $"GitLab asked for a Kerberos ticket this computer could not give for {Url.Host} (is it in the domain, or can it reach the domain's KDC?)";
            }

            next = (int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location ? Rebase(next, location) : null;
            if (next is not null && next.AbsolutePath.Contains("/users/sign_in", StringComparison.Ordinal))
            {
                return "GitLab refused the Kerberos sign-in (is the account linked to a GitLab user?)";
            }

            if (next is not null && !next.AbsolutePath.Contains("/users/auth/", StringComparison.Ordinal))
            {
                break;
            }
        }

        if (await WhoAsync(ct).ConfigureAwait(false) is not { } user)
        {
            return "the Kerberos sign-in went through, but GitLab's API does not take its session";
        }

        User = user;
        return null;
    }

    /// <summary>A sign-in form posted (an LDAP server's, GitLab's own): null when signed in, else why not ("refused ...").</summary>
    private async Task<string?> FormAsync(string action, string? csrf, IEnumerable<(string Name, string Value)> fields, CancellationToken ct)
    {
        using var response = await PostAsync(action, csrf, fields, ct).ConfigureAwait(false);
        var location = (int)response.StatusCode is >= 300 and < 400 ? response.Headers.Location : null;
        if (location is null || location.OriginalString.Contains("/users/sign_in", StringComparison.Ordinal))
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Contains("otp_attempt", StringComparison.Ordinal)
                ? "the account has two-factor authentication, which only the web session can answer"
                : "refused the user name and password";
        }

        if (await WhoAsync(ct).ConfigureAwait(false) is not { } user)
        {
            return "the sign-in went through, but GitLab's API does not take its session";
        }

        User = user;
        return null;
    }

    /// <summary>A form posted with the page's CSRF token; the address it sends the browser on to (at the source's address), or null.</summary>
    private async Task<Uri?> PostFormAsync(string action, string? csrf, IEnumerable<(string Name, string Value)> fields, CancellationToken ct)
    {
        using var response = await PostAsync(action, csrf, fields, ct).ConfigureAwait(false);
        return (int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location ? Rebase(response.RequestMessage!.RequestUri!, location) : null;
    }

    private Task<HttpResponseMessage> PostAsync(string action, string? csrf, IEnumerable<(string Name, string Value)> fields, CancellationToken ct)
    {
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)).ToList();
        if (csrf is not null)
        {
            form.Insert(0, new KeyValuePair<string, string>("authenticity_token", csrf));
        }

        // a page's action carries the installation's own path (/gitlab/users/...); AnyFS's own do not
        var root = Address.Server.AbsolutePath.TrimEnd('/');
        var path = root.Length > 0 && action.StartsWith(root + "/", StringComparison.Ordinal) ? action[root.Length..] : action;
        return Http.PostAsync(Address.Page(path.TrimStart('/')), new FormUrlEncodedContent(form), ct);
    }

    /// <summary>
    /// A redirect's address at the source's own: GitLab writes its configured address into them (its external URL), which
    /// need not be the one the source reaches it by; the path and query are kept.
    /// </summary>
    private Uri Rebase(Uri from, Uri location)
    {
        var absolute = location.IsAbsoluteUri ? location : new Uri(from, location);
        return new Uri($"{Address.Server.Scheme}://{Address.Server.Authority}{absolute.PathAndQuery}");
    }

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
            if (Method == GitLabSignIn.WebSession)
            {
                try
                {
                    await BrowserSignInAsync(interactive: false, _ => { }, ct).ConfigureAwait(false);
                }
                catch (SignInRequiredException ex)
                {
                    throw new VfsException(VfsError.Unavailable, $"The GitLab sign-in has lapsed and AnyFS could not sign in again without you ({ex.Message}); Retry the source to sign in.", ex);
                }
            }
            else if (await WindowsSignInAsync(ct).ConfigureAwait(false) is { } problem)
            {
                throw new VfsException(VfsError.Unavailable, $"The GitLab sign-in has lapsed and signing in again did not get through: {problem}");
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
