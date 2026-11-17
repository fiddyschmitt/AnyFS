using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace AnyFS.Sources.Artifactory;

/// <summary>
/// The web session's sign-in to Artifactory in a browser (spec 7.13): the platform's own page opened in a browser of
/// AnyFS's own, one profile per server (<see cref="DefaultProfile"/>), whatever its sign-in (SAML or OpenID Connect to the
/// organisation's identity provider, a password and a second factor). The person counts as signed in once a page of the
/// platform is open, the browser holds the platform's access token, and the platform tells AnyFS, with the browser's
/// cookies, who that is. Every cookie of the profile is kept: the identity provider's among them let a later headless
/// launch sign in again without anyone.
/// </summary>
internal sealed class ArtifactorySignInTarget(Uri platform, bool trustAnyCertificate, string? profileDir = null)
    : WebSignInTarget(new Uri(platform.AbsoluteUri.TrimEnd('/') + "/ui/"), profileDir ?? DefaultProfile(platform.Host))
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);

    private long _lastCheck;

    public override string LogPrefix => "artifactory";

    public override string WebDriverVariable => "ANYFS_ARTIFACTORY_WEBDRIVER";

    public override string OtherWayIn => ", or choose Access token under Sign in with";

    /// <summary>The browser profile that keeps the web session's sign-in to an Artifactory server; <c>ANYFS_ARTIFACTORY_PROFILES</c> moves the root (tests).</summary>
    public static string DefaultProfile(string host) => ProfileFor("artifactory-browser", host, "ANYFS_ARTIFACTORY_PROFILES");

    /// <summary>The profile is AnyFS's own, for this server only: every cookie in it belongs to the sign-in (an identity provider's too).</summary>
    public override bool Keeps(SessionCookie cookie) => true;

    public override async ValueTask<SignInCheck> CheckAsync(IReadOnlyList<Uri> pages, IReadOnlyList<SessionCookie> cookies, CancellationToken ct)
    {
        if (!pages.Any(IsPlatformPage) || !cookies.Any(c => c.Name == ArtifactorySession.AccessCookie))
        {
            return SignInCheck.NotYet;
        }

        // the platform gives the anonymous user a token too, where it lets anyone read: ask it who this one is, now and then
        if (_lastCheck != 0 && Stopwatch.GetElapsedTime(_lastCheck) < CheckEvery)
        {
            return SignInCheck.NotYet;
        }

        _lastCheck = Stopwatch.GetTimestamp();
        return await SignedInAsync(cookies, ct).ConfigureAwait(false) ? SignInCheck.SignedIn : SignInCheck.NotYet;
    }

    private bool IsPlatformPage(Uri page) => page.Host.Equals(Page.Host, StringComparison.OrdinalIgnoreCase) && page.Port == Page.Port;

    /// <summary>Whether the platform takes the browser's cookies as a signed-in session of someone's.</summary>
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
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Page, "api/v1/ui/auth/current"));
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            {
                return false;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.Str("name") is { Length: > 0 } name && !name.Equals("anonymous", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
