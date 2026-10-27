namespace AnyFS.Sources.Common;

/// <summary>One cookie a signed-in page holds: enough to replay it on a plain HTTP request.</summary>
public sealed record SessionCookie(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly)
{
    /// <summary>Whether a cookie set for <paramref name="cookieDomain"/> goes to <paramref name="host"/>: the domain itself or a host below it.</summary>
    public static bool DomainMatches(string cookieDomain, string host)
    {
        var domain = cookieDomain.TrimStart('.');
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Thrown when a session or token can't be had without the person signing in again (it lapsed, or was revoked), or the sign-in was refused.</summary>
/// <param name="permanent">The refusal is about the settings or the organisation's policy, so trying again unchanged would only fail again (and open the browser again).</param>
public sealed class SignInRequiredException(string message, Exception? inner = null, bool permanent = false) : Exception(message, inner)
{
    public bool Permanent { get; } = permanent;
}

/// <summary>What <see cref="WebSignInTarget.CheckAsync"/> makes of the browser: signed in, not yet, or a sign-in that cannot work.</summary>
public readonly record struct SignInCheck
{
    public static SignInCheck NotYet => default;

    public static SignInCheck SignedIn => new() { Done = true };

    /// <summary>The sign-in cannot work this way, and waiting will not change that (a session proxy the web session cannot go through).</summary>
    public static SignInCheck Failed(string why) => new() { Failure = why };

    public bool Done { get; init; }

    public string? Failure { get; init; }
}

/// <summary>
/// What a web session signs in to (spec 7.3.2, 7.6): the page AnyFS opens in a browser of its own for the person to sign in
/// on, the profile that keeps that sign-in for later renewals without a window, which of the browser's cookies the session
/// is, and how AnyFS tells that the person has signed in (<see cref="CheckAsync"/>). <see cref="BrowserSession"/> and its
/// WebDriver fallback do the rest.
/// </summary>
/// <param name="page">The page to open: the site, or the service whose session the source works with.</param>
/// <param name="profileDir">AnyFS's own browser profile for it (<see cref="ProfileFor"/>); the WebDriver fallback keeps its own beside it.</param>
public abstract class WebSignInTarget(Uri page, string profileDir)
{
    public Uri Page { get; } = page;

    public string ProfileDir { get; } = profileDir;

    /// <summary>What the log lines start with: the source type ("sharepoint", "3dx").</summary>
    public abstract string LogPrefix { get; }

    /// <summary>
    /// The environment variable that sends the sign-in through WebDriver from the start (tests, troubleshooting): <c>1</c> for
    /// any browser, or one browser's name (<c>edge</c>, <c>chrome</c>, <c>chromium</c>, <c>firefox</c>). Null for none.
    /// </summary>
    public virtual string? WebDriverVariable => null;

    /// <summary>What the person can do instead when no browser can sign in, after "install Firefox": ", or use an app sign-in (Sign in with)".</summary>
    public virtual string OtherWayIn => "";

    /// <summary>
    /// Hosts the browser may answer a Windows sign-in challenge (Negotiate: Kerberos) for with the person's own Windows
    /// sign-in, as a browser does for its intranet zone: a server whose sign-in page offers Kerberos (GitLab's) needs it,
    /// since a Chromium browser answers no Negotiate for a name with dots unless told to. Empty by default.
    /// </summary>
    public virtual IReadOnlyList<string> NegotiateHosts => [];

    /// <summary>Whether a cookie the browser holds is part of the session: by default those that go to the page's host.</summary>
    public virtual bool Keeps(SessionCookie cookie) => SessionCookie.DomainMatches(cookie.Domain, Page.Host);

    /// <summary>
    /// Whether the person has signed in, from the pages the browser has open and the cookies it holds that <see cref="Keeps"/>
    /// takes (over DevTools all of the profile's; through WebDriver those of the page it shows). Called every second or two
    /// until it says <see cref="SignInCheck.SignedIn"/> or <see cref="SignInCheck.Failed"/>, or the time is up.
    /// </summary>
    public abstract ValueTask<SignInCheck> CheckAsync(IReadOnlyList<Uri> pages, IReadOnlyList<SessionCookie> cookies, CancellationToken ct);

    /// <summary>
    /// Once signed in: other pages whose cookies the session needs too (another service of the same sign-in). WebDriver reads
    /// only the cookies of the page it shows, so it opens each in turn; DevTools reads every cookie the profile holds and
    /// needs none of this. Empty by default.
    /// </summary>
    public virtual ValueTask<IReadOnlyList<Uri>> AlsoVisitAsync(IReadOnlyList<SessionCookie> cookies, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<Uri>>([]);

    /// <summary>
    /// The browser profile that keeps a web session's sign-in for <paramref name="host"/>, shared by every source for it and by
    /// the Test button: <c>%LOCALAPPDATA%\AnyFS\&lt;folder&gt;\&lt;host&gt;</c>. It lives in the user's app data rather than the
    /// portable settings because a browser profile is bound to the machine; <paramref name="rootVariable"/> moves the root (tests).
    /// </summary>
    public static string ProfileFor(string folder, string host, string? rootVariable = null)
    {
        var root = rootVariable is null ? null : Environment.GetEnvironmentVariable(rootVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnyFS", folder);
        }

        return Path.Combine(root, host);
    }
}
