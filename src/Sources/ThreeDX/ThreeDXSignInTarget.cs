using System.Diagnostics;
using System.Net;

namespace AnyFS.Sources.ThreeDX;

/// <summary>
/// The web session's sign-in to 3DX in a browser (spec 7.6.2): 3DSpace opened in a browser of AnyFS's own, one profile per
/// server (<see cref="DefaultProfile"/>), whatever the organisation's sign-in in between. The person counts as signed in once a
/// page of 3DSpace is open and 3DSpace answers AnyFS with the browser's cookies (its CSRF token, which only a signed-in
/// session gets). Every cookie of the profile is kept, 3DPassport's and the identity provider's too: with them AnyFS renews a
/// lapsed 3DSpace or 3DSearch session itself, without a browser. WebDriver sees one page's cookies at a time, so it opens
/// 3DSearch too once signed in, as Mount3DX does.
/// </summary>
internal sealed class ThreeDXSignInTarget(Uri space, bool trustAnyCertificate, string? profileDir = null)
    : WebSignInTarget(space, profileDir ?? DefaultProfile(space.Host))
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);

    private long _lastCheck;

    public override string LogPrefix => "3dx";

    public override string WebDriverVariable => "ANYFS_3DX_WEBDRIVER";

    public override string OtherWayIn => ", or choose Windows account under Sign in with";

    /// <summary>The browser profile that keeps the web session's sign-in to a 3DX server; <c>ANYFS_3DX_PROFILES</c> moves the root (tests).</summary>
    public static string DefaultProfile(string host) => ProfileFor("3dx-browser", host, "ANYFS_3DX_PROFILES");

    /// <summary>The profile is AnyFS's own, for this server only: every cookie in it belongs to the sign-in.</summary>
    public override bool Keeps(SessionCookie cookie) => true;

    public override async ValueTask<SignInCheck> CheckAsync(IReadOnlyList<Uri> pages, IReadOnlyList<SessionCookie> cookies, CancellationToken ct)
    {
        if (!pages.Any(IsSpacePage) || cookies.Count == 0)
        {
            return SignInCheck.NotYet;
        }

        // 3DSpace's own page can show for a moment before the sign-in (a page that sends the browser on): ask 3DSpace, now and then
        if (_lastCheck != 0 && Stopwatch.GetElapsedTime(_lastCheck) < CheckEvery)
        {
            return SignInCheck.NotYet;
        }

        _lastCheck = Stopwatch.GetTimestamp();
        return await SignedInAsync(cookies, ct).ConfigureAwait(false) ? SignInCheck.SignedIn : SignInCheck.NotYet;
    }

    /// <summary>
    /// Once signed in: 3DSearch, whose session WebDriver has to see the browser open for its cookies. Its search address (what
    /// Mount3DX's keep-alive asks), which answers with a body: a browser shows an empty error page of its own, without the
    /// site's cookies.
    /// </summary>
    public override async ValueTask<IReadOnlyList<Uri>> AlsoVisitAsync(IReadOnlyList<SessionCookie> cookies, CancellationToken ct)
    {
        using var http = Client(cookies);
        try
        {
            return await ThreeDXClient.SearchServiceAsync(http, Page, ct).ConfigureAwait(false) is { } search ? [ThreeDXSession.SearchProbe(search)] : [];
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            return [];
        }
    }

    private bool IsSpacePage(Uri page)
        => page.Host.Equals(Page.Host, StringComparison.OrdinalIgnoreCase) && page.Port == Page.Port
            && (page.AbsolutePath + "/").StartsWith(Page.AbsolutePath, StringComparison.OrdinalIgnoreCase);

    private async Task<bool> SignedInAsync(IReadOnlyList<SessionCookie> cookies, CancellationToken ct)
    {
        using var http = Client(cookies);
        try
        {
            using var response = await http.GetAsync(ThreeDXSession.SpaceProbe(Page), ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode && ThreeDXSession.IsJson(response);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>A client that sends the browser's cookies and follows no redirects (a redirect means not signed in).</summary>
    private HttpClient Client(IReadOnlyList<SessionCookie> cookies)
    {
        var jar = CookieJar.New();
        CookieJar.Add(jar, cookies);
        var handler = new SocketsHttpHandler { CookieContainer = jar, UseCookies = true, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
        if (trustAnyCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
    }
}
