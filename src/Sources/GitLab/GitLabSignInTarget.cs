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
    : AskedSignInTarget(new Uri(server.AbsoluteUri.TrimEnd('/') + "/"), profileDir ?? DefaultProfile(server.Host), trustAnyCertificate)
{
    public override string LogPrefix => "gitlab";

    public override string WebDriverVariable => "ANYFS_GITLAB_WEBDRIVER";

    public override string OtherWayIn => ", or choose Access token under Sign in with";

    /// <summary>The server, whose sign-in page may offer Kerberos (spike B): the browser answers its Negotiate with the person's Windows sign-in.</summary>
    public override IReadOnlyList<string> NegotiateHosts => [Page.Host];

    /// <summary>The browser profile that keeps the web session's sign-in to a GitLab server; <c>ANYFS_GITLAB_PROFILES</c> moves the root (tests).</summary>
    public static string DefaultProfile(string host) => ProfileFor("gitlab-browser", host, "ANYFS_GITLAB_PROFILES");

    /// <summary>The profile is AnyFS's own, for this server only: every cookie in it belongs to the sign-in (an identity provider's too).</summary>
    public override bool Keeps(SessionCookie cookie) => true;

    // the sign-in page itself is a page of the server: only with GitLab's session cookie is it worth asking
    protected override bool MayBeSignedIn(IReadOnlyList<Uri> pages, IReadOnlyList<SessionCookie> cookies)
        => base.MayBeSignedIn(pages, cookies) && cookies.Any(c => c.Name == "_gitlab_session");

    /// <summary>Whether GitLab's API takes the browser's cookies as a signed-in session.</summary>
    protected override async Task<bool> AnswersAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(Page, "api/v4/user"), ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
    }
}
