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
    : AskedSignInTarget(installation, profileDir ?? DefaultProfile(product, installation.Host), trustAnyCertificate)
{
    public override string LogPrefix => Name(product);

    public override string WebDriverVariable => product.Variable("WEBDRIVER");

    public override string OtherWayIn => ", or choose Personal access token under Sign in with";

    /// <summary>The installation, whose sign-in may be Kerberos (an SSO app): the browser answers its Negotiate with the person's Windows sign-in.</summary>
    public override IReadOnlyList<string> NegotiateHosts => [Page.Host];

    /// <summary>The source type's name, as log lines start.</summary>
    public static string Name(AtlassianProduct product) => product.TypeId();

    /// <summary>
    /// The browser profile that keeps the web session's sign-in to an installation; <c>ANYFS_JIRA_PROFILES</c>,
    /// <c>ANYFS_CONFLUENCE_PROFILES</c>, <c>ANYFS_BITBUCKET_PROFILES</c>, <c>ANYFS_BAMBOO_PROFILES</c> move the root (tests).
    /// </summary>
    public static string DefaultProfile(AtlassianProduct product, string host) => ProfileFor(product.TypeId() + "-browser", host, product.Variable("PROFILES"));

    /// <summary>The profile is AnyFS's own, for this installation only: every cookie in it belongs to the sign-in (an identity provider's too).</summary>
    public override bool Keeps(SessionCookie cookie) => true;

    /// <summary>Whether the product's REST API takes the browser's cookies for a person (not the anonymous user).</summary>
    protected override async Task<bool> AnswersAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(Page, AtlassianSession.WhoPath(product)), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        // Bitbucket's who-am-I call names the account in a header, not its data
        if (AtlassianSession.NamedUser(product, response) is { } named)
        {
            return named.Length > 0;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        return AtlassianUser.Parse(product, doc.RootElement) is not null;
    }
}
