using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace AnyFS.Sources.Atlassian;

/// <summary>
/// The web session's sign-in to Jira or Confluence in a browser (spec 7.10): the installation's own page opened in a
/// browser of AnyFS's own, one profile per product and host (<see cref="DefaultProfile"/>), whatever its sign-in (the
/// bundled single sign-on to the organisation's identity provider, a Kerberos SSO app, the login form, a second step).
/// Both products' REST APIs take the browser's session cookie as they take a token, so the person counts as signed in once
/// a page of the installation is open and the who-am-I call (<see cref="AtlassianSession.WhoPath"/>) answers AnyFS with
/// the browser's cookies for a person. Every cookie of the profile is kept: the "remember me" cookie among them lets a later
/// headless launch sign in again without anyone.
/// </summary>
internal sealed class AtlassianSignInTarget(AtlassianProduct product, Uri installation, bool trustAnyCertificate, string? profileDir = null)
    : WebSignInTarget(installation, profileDir ?? DefaultProfile(product, installation.Host))
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);

    private long _lastCheck;

    public override string LogPrefix => Name(product);

    public override string WebDriverVariable => product == AtlassianProduct.Jira ? "ANYFS_JIRA_WEBDRIVER" : "ANYFS_CONFLUENCE_WEBDRIVER";

    public override string OtherWayIn => ", or choose Personal access token under Sign in with";

    /// <summary>The installation, whose sign-in may be Kerberos (an SSO app): the browser answers its Negotiate with the person's Windows sign-in.</summary>
    public override IReadOnlyList<string> NegotiateHosts => [Page.Host];

    /// <summary>The source type's name, as log lines start.</summary>
    public static string Name(AtlassianProduct product) => product == AtlassianProduct.Jira ? "jira" : "confluence";

    /// <summary>
    /// The browser profile that keeps the web session's sign-in to a Jira or Confluence installation;
    /// <c>ANYFS_JIRA_PROFILES</c> / <c>ANYFS_CONFLUENCE_PROFILES</c> move the root (tests).
    /// </summary>
    public static string DefaultProfile(AtlassianProduct product, string host)
        => product == AtlassianProduct.Jira
            ? ProfileFor("jira-browser", host, "ANYFS_JIRA_PROFILES")
            : ProfileFor("confluence-browser", host, "ANYFS_CONFLUENCE_PROFILES");

    /// <summary>The profile is AnyFS's own, for this installation only: every cookie in it belongs to the sign-in (an identity provider's too).</summary>
    public override bool Keeps(SessionCookie cookie) => true;

    public override async ValueTask<SignInCheck> CheckAsync(IReadOnlyList<Uri> pages, IReadOnlyList<SessionCookie> cookies, CancellationToken ct)
    {
        if (!pages.Any(IsInstallationPage) || cookies.Count == 0)
        {
            return SignInCheck.NotYet;
        }

        // the login page itself is a page of the installation with a session cookie: ask the product, now and then
        if (_lastCheck != 0 && Stopwatch.GetElapsedTime(_lastCheck) < CheckEvery)
        {
            return SignInCheck.NotYet;
        }

        _lastCheck = Stopwatch.GetTimestamp();
        return await SignedInAsync(cookies, ct).ConfigureAwait(false) ? SignInCheck.SignedIn : SignInCheck.NotYet;
    }

    private bool IsInstallationPage(Uri page)
        => page.Host.Equals(Page.Host, StringComparison.OrdinalIgnoreCase) && page.Port == Page.Port
            && (page.AbsolutePath.TrimEnd('/') + "/").StartsWith(Page.AbsolutePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the product's REST API takes the browser's cookies for a person (not the anonymous user).</summary>
    internal async Task<bool> SignedInAsync(IReadOnlyList<SessionCookie> cookies, CancellationToken ct)
    {
        // a jar with room for an identity provider's cookies too: the default holds twenty a domain and lets the oldest go
        var jar = CookieJar.New();
        CookieJar.Add(jar, cookies);
        using var handler = new SocketsHttpHandler { CookieContainer = jar, UseCookies = true, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
        if (trustAnyCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            using var response = await http.GetAsync(new Uri(Page, AtlassianSession.WhoPath(product)), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            {
                return false;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            return AtlassianUser.Parse(product, doc.RootElement) is not null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
