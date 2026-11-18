using System.Diagnostics;

namespace AnyFS.Sources.Artifactory;

/// <summary>
/// The web session's sign-in to Artifactory in a browser (spec 7.13): JFrog's client sign-in, the one its own command-line
/// tool's <c>jf login</c> makes. As the window opens (<see cref="OpeningWindowAsync"/>), AnyFS asks the platform for a
/// sign-in under a session of its own; the window shows the platform's sign-in page for that session, in a browser profile
/// of AnyFS's own, one per server (<see cref="DefaultProfile"/>), whatever its sign-in (SAML or OpenID Connect to the
/// organisation's identity provider, a password and a second factor). Once signed in, the page asks the person for the
/// code AnyFS shows them (<see cref="Code"/>, the session's last four characters) and, given it, lets the session have a
/// token: an access token the whole platform takes, and what renews it. The person counts as signed in once the platform
/// hands that token over (<see cref="Token"/>). The platform holds the session for five minutes from when it was asked
/// for (<see cref="Held"/>), and a code given after that goes nowhere, so the window closes then, as JFrog's own tool gives
/// up then, and the source waits for its Retry, which signs in again with a new code. The token the page's own sign-in
/// leaves in its cookies is no use: it is the platform's pages', and Artifactory's REST API refuses it ("Token failed
/// verification: audience").
/// </summary>
/// <param name="platform">The platform's address (its pages at <c>ui/</c>, its access service at <c>access/</c>).</param>
/// <param name="ask">Asks the platform for a client sign-in under a session.</param>
/// <param name="poll">Asks the platform for a session's token: null until the person has given the code.</param>
/// <param name="held">How long the platform holds a session from when it was asked for (<see cref="Held"/>, a test's shorter).</param>
internal sealed class ArtifactorySignInTarget(
    Uri platform,
    Func<string, CancellationToken, Task> ask,
    Func<string, CancellationToken, Task<ArtifactoryClientToken?>> poll,
    TimeSpan? held = null,
    string? profileDir = null)
    : WebSignInTarget(new Uri(platform, "ui/login"), profileDir ?? DefaultProfile(platform.Host))
{
    /// <summary>The name the platform's page gives AnyFS ("enter the code that you received from AnyFS").</summary>
    public const string ClientName = "AnyFS";

    /// <summary>
    /// How long the platform holds a client sign-in's session from when it was asked for: its access service forgets one
    /// after five minutes (its <c>jfrog-client-login</c> cache's <c>expire-after-write-seconds</c>, 300 unless an
    /// administrator changed it), and JFrog's own tool waits as long.
    /// </summary>
    public static readonly TimeSpan Held = TimeSpan.FromMinutes(5);

    // how often the platform is asked for the token at most: every two seconds, more often for a session held a shorter time
    private static readonly TimeSpan MostEvery = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _held = held ?? Held;
    private readonly Uri _platform = platform;
    private string? _session;
    private long _asked;
    private long _lastCheck;

    public override string LogPrefix => "artifactory";

    public override string WebDriverVariable => "ANYFS_ARTIFACTORY_WEBDRIVER";

    public override string OtherWayIn => ", or choose Access token under Sign in with";

    /// <summary>The page asks for a code every time: there is no sign-in to read without a window.</summary>
    public override bool NeedsAPerson => true;

    /// <summary>The platform's sign-in page for the session once it is asked for (<see cref="OpeningWindowAsync"/>); before, its sign-in page alone.</summary>
    public override Uri Page => _session is null ? base.Page : PageFor(_platform, _session);

    /// <summary>What the person types into the page when it asks: the session's last four characters, as JFrog's own tool shows them.</summary>
    public string Code => _session is null ? "" : _session[^4..];

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

    /// <summary>A session of AnyFS's own, asked of the platform as the window opens: its time (<see cref="Held"/>) runs from now.</summary>
    public override async ValueTask OpeningWindowAsync(CancellationToken ct)
    {
        var session = Guid.NewGuid().ToString("D");
        _asked = Stopwatch.GetTimestamp();
        await ask(session, ct).ConfigureAwait(false);
        _session = session;
        _lastCheck = 0;
        Token = null;
    }

    public override async ValueTask<SignInCheck> CheckAsync(IReadOnlyList<Uri> pages, IReadOnlyList<SessionCookie> cookies, CancellationToken ct)
    {
        if (_session is not { } session)
        {
            return SignInCheck.NotYet;
        }

        // once the platform's time is up a code given goes nowhere: asked once more, for a code given just before, then the
        // window closes
        var late = Stopwatch.GetElapsedTime(_asked) >= _held;
        if (!late && _lastCheck != 0 && Stopwatch.GetElapsedTime(_lastCheck) < CheckEvery)
        {
            return SignInCheck.NotYet;
        }

        _lastCheck = Stopwatch.GetTimestamp();
        Token ??= await poll(session, ct).ConfigureAwait(false);
        return Token is not null ? SignInCheck.SignedIn
            : late ? SignInCheck.Failed($"the sign-in to {Page.Host} was not finished within the {Said(_held)} Artifactory holds it for; Retry to sign in again, with a new code")
            : SignInCheck.NotYet;
    }

    private TimeSpan CheckEvery => TimeSpan.FromTicks(Math.Min(MostEvery.Ticks, _held.Ticks / 20));

    private static string Said(TimeSpan time) => time >= TimeSpan.FromMinutes(1) ? $"{time.TotalMinutes:0.#} minutes" : $"{time.TotalSeconds:0.#} seconds";
}
