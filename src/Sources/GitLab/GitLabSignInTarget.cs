using System.Diagnostics;
using System.Net;

namespace AnyFS.Sources.GitLab;

/// <summary>
/// The web session's sign-in to GitLab in a browser (spec 7.8): the server's own page opened in a browser of AnyFS's own,
/// one profile per server (<see cref="DefaultProfile"/>), whatever its sign-in (SAML or OpenID Connect to the
/// organisation's identity provider, Kerberos, LDAP, a password and a second factor). GitLab's API takes the browser's
/// session cookie as it takes a token, so the person counts as signed in once a page of the server is open and GitLab's
/// <c>/api/v4/user</c> answers AnyFS with the browser's cookies. Every cookie of the profile is kept: GitLab's
/// <c>remember_user_token</c> among them lets a later headless launch sign in again without anyone.
/// </summary>
internal sealed class GitLabSignInTarget(Uri server, bool trustAnyCertificate, string? profileDir = null)
    : WebSignInTarget(new Uri(server.AbsoluteUri.TrimEnd('/') + "/"), profileDir ?? DefaultProfile(server.Host))
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);

    private long _lastCheck;

    public override string LogPrefix => "gitlab";

    public override string WebDriverVariable => "ANYFS_GITLAB_WEBDRIVER";

    public override string OtherWayIn => ", or choose Access token under Sign in with";

    /// <summary>The server, whose sign-in page may offer Kerberos (spike B): the browser answers its Negotiate with the person's Windows sign-in.</summary>
    public override IReadOnlyList<string> NegotiateHosts => [Page.Host];

    /// <summary>The browser profile that keeps the web session's sign-in to a GitLab server; <c>ANYFS_GITLAB_PROFILES</c> moves the root (tests).</summary>
    public static string DefaultProfile(string host) => ProfileFor("gitlab-browser", host, "ANYFS_GITLAB_PROFILES");

    /// <summary>The profile is AnyFS's own, for this server only: every cookie in it belongs to the sign-in (an identity provider's too).</summary>
    public override bool Keeps(SessionCookie cookie) => true;

    public override async ValueTask<SignInCheck> CheckAsync(IReadOnlyList<Uri> pages, IReadOnlyList<SessionCookie> cookies, CancellationToken ct)
    {
        if (!pages.Any(IsServerPage) || !cookies.Any(c => c.Name == "_gitlab_session"))
        {
            return SignInCheck.NotYet;
        }

        // the sign-in page itself is a page of the server with a session cookie: ask GitLab, now and then
        if (_lastCheck != 0 && Stopwatch.GetElapsedTime(_lastCheck) < CheckEvery)
        {
            return SignInCheck.NotYet;
        }

        _lastCheck = Stopwatch.GetTimestamp();
        return await SignedInAsync(cookies, ct).ConfigureAwait(false) ? SignInCheck.SignedIn : SignInCheck.NotYet;
    }

    private bool IsServerPage(Uri page)
        => page.Host.Equals(Page.Host, StringComparison.OrdinalIgnoreCase) && page.Port == Page.Port
            && (page.AbsolutePath.TrimEnd('/') + "/").StartsWith(Page.AbsolutePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether GitLab's API takes the browser's cookies as a signed-in session.</summary>
    internal async Task<bool> SignedInAsync(IReadOnlyList<SessionCookie> cookies, CancellationToken ct)
    {
        var jar = new CookieContainer();
        foreach (var c in cookies)
        {
            try
            {
                jar.Add(new Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Domain) { Secure = c.Secure, HttpOnly = c.HttpOnly });
            }
            catch (CookieException)
            {
            }
        }

        using var handler = new SocketsHttpHandler { CookieContainer = jar, UseCookies = true, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
        if (trustAnyCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var response = await http.GetAsync(new Uri(Page, "api/v4/user"), ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
