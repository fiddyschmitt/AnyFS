using System.Diagnostics;

namespace AnyFS.Sources.Artifactory;

/// <summary>
/// The web session's sign-in to Artifactory in a browser (spec 7.13): JFrog's client sign-in, the one its own command-line
/// tool's <c>jf login</c> makes. AnyFS asks the platform for a sign-in under a session of its own, then opens the platform's
/// sign-in page for that session in a browser of AnyFS's own, one profile per server (<see cref="DefaultProfile"/>), whatever
/// its sign-in (SAML or OpenID Connect to the organisation's identity provider, a password and a second factor). Once
/// signed in, the page asks the person for the code AnyFS shows them (<see cref="Code"/>, the session's last four
/// characters) and, given it, lets the session have a token: an access token the whole platform takes, and what renews
/// it. The person counts as signed in once the platform hands that token over (<see cref="Token"/>). The token the
/// page's own sign-in leaves in its cookies is no use: it is the platform's pages', and Artifactory's REST API refuses it
/// ("Token failed verification: audience").
/// </summary>
/// <param name="platform">The platform's address (its pages at <c>ui/</c>, its access service at <c>access/</c>).</param>
/// <param name="session">The sign-in's session, which AnyFS has asked the platform for.</param>
/// <param name="poll">Asks the platform for the session's token: null until the person has given the code.</param>
internal sealed class ArtifactorySignInTarget(Uri platform, string session, Func<CancellationToken, Task<ArtifactoryClientToken?>> poll, string? profileDir = null)
    : WebSignInTarget(PageFor(platform, session), profileDir ?? DefaultProfile(platform.Host))
{
    /// <summary>The name the platform's page gives AnyFS ("enter the code that you received from AnyFS").</summary>
    public const string ClientName = "AnyFS";

    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);

    private long _lastCheck;

    public override string LogPrefix => "artifactory";

    public override string WebDriverVariable => "ANYFS_ARTIFACTORY_WEBDRIVER";

    public override string OtherWayIn => ", or choose Access token under Sign in with";

    /// <summary>The page asks for a code every time: there is no sign-in to read without a window.</summary>
    public override bool NeedsAPerson => true;

    /// <summary>What the person types into the page when it asks: the session's last four characters, as JFrog's own tool shows them.</summary>
    public string Code => session[^4..];

    /// <summary>The token the platform gave for the session, once the person has signed in and given the code.</summary>
    public ArtifactoryClientToken? Token { get; private set; }

    /// <summary>The browser profile that keeps the sign-in to an Artifactory server's platform; <c>ANYFS_ARTIFACTORY_PROFILES</c> moves the root (tests).</summary>
    public static string DefaultProfile(string host) => ProfileFor("artifactory-browser", host, "ANYFS_ARTIFACTORY_PROFILES");

    /// <summary>The platform's sign-in page for a client's session, as JFrog's own tool opens it: with the code asked for.</summary>
    public static Uri PageFor(Uri platform, string session)
        => new(platform, $"ui/login?jfClientSession={Uri.EscapeDataString(session)}&jfClientName={ClientName}&jfClientCode=1");

    public override string Prompt(string browserName)
        => $"Sign in to {Page.Host} in the {browserName} window that opened; when the page asks for the code that you received from {ClientName}, enter {Code}";

    /// <summary>None of the browser's cookies: the session is the token the platform hands over, not the page's own.</summary>
    public override bool Keeps(SessionCookie cookie) => false;

    public override async ValueTask<SignInCheck> CheckAsync(IReadOnlyList<Uri> pages, IReadOnlyList<SessionCookie> cookies, CancellationToken ct)
    {
        if (_lastCheck != 0 && Stopwatch.GetElapsedTime(_lastCheck) < CheckEvery)
        {
            return SignInCheck.NotYet;
        }

        _lastCheck = Stopwatch.GetTimestamp();
        Token ??= await poll(ct).ConfigureAwait(false);
        return Token is null ? SignInCheck.NotYet : SignInCheck.SignedIn;
    }
}
