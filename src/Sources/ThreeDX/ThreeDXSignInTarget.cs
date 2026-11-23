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
    : AskedSignInTarget(space, profileDir ?? DefaultProfile(space.Host), trustAnyCertificate)
{
    public override string LogPrefix => "3dx";

    public override string WebDriverVariable => "ANYFS_3DX_WEBDRIVER";

    public override string OtherWayIn => ", or choose Windows account under Sign in with";

    /// <summary>The browser profile that keeps the web session's sign-in to a 3DX server; <c>ANYFS_3DX_PROFILES</c> moves the root (tests).</summary>
    public static string DefaultProfile(string host) => ProfileFor("3dx-browser", host, "ANYFS_3DX_PROFILES");

    /// <summary>The profile is AnyFS's own, for this server only: every cookie in it belongs to the sign-in.</summary>
    public override bool Keeps(SessionCookie cookie) => true;

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

    /// <summary>Whether 3DSpace answers the browser's cookies with its CSRF token, which only a signed-in session gets.</summary>
    protected override async Task<bool> AnswersAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync(ThreeDXSession.SpaceProbe(Page), ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode && ThreeDXSession.IsJson(response);
    }
}
