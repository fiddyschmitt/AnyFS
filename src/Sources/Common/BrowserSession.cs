using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Common;

/// <summary>
/// A sign-in taken from a browser (spec 7.3.2), for people whose organisation will not register an app or whose sign-in
/// only a browser can do. AnyFS launches a Chromium browser (<see cref="BrowserLocator"/>) with a profile of its own, one per
/// host (<see cref="WebSignInTarget.ProfileFor"/>), so the person's own browser and its profile are never read; the person
/// signs in on the real sign-in page, where multi-factor and Conditional Access happen as always; the cookies are read over
/// the browser's DevTools protocol and replayed on plain requests, the way the site's own pages make them. What the session
/// is and when the person has signed in is the <see cref="WebSignInTarget"/>'s to say. The browser is open only long enough
/// to read the cookies, never while files are served. The profile keeps the sign-in session, so a later headless launch
/// renews the cookies with nobody at the keyboard until that session itself lapses; only then is a window shown again.
/// Where AnyFS cannot reach the DevTools port itself (a policy, or a browser that never opens it), the sign-in goes through
/// Selenium WebDriver instead (<see cref="WebDriverSession"/>), for good. The browser itself is the shared
/// <see cref="DevToolsBrowser"/>.
/// </summary>
public sealed class BrowserSession
{
    private static readonly TimeSpan SilentDeadline = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan InteractiveDeadline = TimeSpan.FromMinutes(10);

    private readonly string? _browserPath;
    private readonly WebSignInTarget _target;
    private readonly string _profileDir;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<WebDriverBrowser> _webDriverBrowsers;
    private WebDriverSession? _webDriver;

    /// <param name="browserPath">The Chromium browser to drive over DevTools, or null when there is none (WebDriver only, through Firefox).</param>
    /// <param name="target">What to sign in to, and how to tell the person has.</param>
    public BrowserSession(string? browserPath, WebSignInTarget target, ILogger logger)
    {
        _browserPath = browserPath;
        _target = target;
        _profileDir = target.ProfileDir;
        _logger = logger;
        _webDriverBrowsers = WebDriverSession.Browsers(ForcedWebDriver);
    }

    /// <summary>The browser AnyFS will drive, for messages ("Microsoft Edge").</summary>
    public string BrowserName => _webDriver?.BrowserName ?? (_browserPath is null ? "a browser" : BrowserLocator.DisplayName(_browserPath));

    /// <summary>Whether there is any browser to sign in with, over DevTools or WebDriver.</summary>
    public bool CanSignIn => _browserPath is not null || _webDriverBrowsers.Count > 0;

    private string Prefix => _target.LogPrefix;

    private Uri Site => _target.Page;

    private string? ForcedWebDriver => _target.WebDriverVariable is { } name ? Environment.GetEnvironmentVariable(name) : null;

    /// <summary>
    /// Why AnyFS should sign in through WebDriver from the start, or null: the target's variable says so (tests,
    /// troubleshooting), there is no Chromium browser, or its policy blocks AnyFS's own DevTools launch.
    /// </summary>
    private string? WebDriverFromTheStart()
    {
        if (!string.IsNullOrWhiteSpace(ForcedWebDriver))
        {
            return $"{_target.WebDriverVariable} is set";
        }

        return _browserPath is null ? "no Edge, Chrome or Chromium is installed" : BrowserLocator.DevToolsBlocked(_browserPath);
    }

    /// <summary>Signs in through WebDriver from now on, renewals included, since AnyFS cannot drive the browser's DevTools port itself.</summary>
    private Task<IReadOnlyList<SessionCookie>> FallBackAsync(string why, bool interactive, Action<string> progress, CancellationToken ct)
    {
        if (_webDriverBrowsers.Count == 0)
        {
            throw new SignInRequiredException($"The web session cannot read the sign-in from {BrowserName} ({why}), and there is no other browser for its WebDriver fallback; install Firefox{_target.OtherWayIn}.", permanent: true);
        }

        _logger.LogInformation("{Prefix}: signing in to {Host} through WebDriver ({Browsers}), since {Why}", Prefix, Site.Host, string.Join(", ", _webDriverBrowsers.Select(b => b.Name)), why);
        _webDriver ??= new WebDriverSession(_webDriverBrowsers, _target, _logger);
        return _webDriver.SignInAsync(interactive, progress, ct);
    }

    /// <summary>
    /// The session's cookies: first a headless launch, which succeeds without a window while the profile's sign-in is still
    /// good (a restart, or a silent renewal); failing that, and only when <paramref name="interactive"/>, a window for the
    /// person to sign in. Throws <see cref="SignInRequiredException"/> when no sign-in can be had: permanent when waiting
    /// cannot help (remote debugging turned off, the window closed, nobody signing in), so the source waits for a Retry
    /// instead of opening windows every few minutes.
    /// </summary>
    public async Task<IReadOnlyList<SessionCookie>> SignInAsync(bool interactive, Action<string> progress, CancellationToken ct)
    {
        if (_webDriver is not null)
        {
            return await _webDriver.SignInAsync(interactive, progress, ct).ConfigureAwait(false);
        }

        if (WebDriverFromTheStart() is { } why)
        {
            return await FallBackAsync(why, interactive, progress, ct).ConfigureAwait(false);
        }

        Capture silent;
        var gate = ProfileLocks.For(_profileDir);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            silent = await CaptureAsync(headless: true, SilentDeadline, ct).ConfigureAwait(false);
            if (silent.Cookies is { } cookies)
            {
                return cookies;
            }

            if (silent.Final && !silent.DevToolsUnreachable)
            {
                throw new SignInRequiredException(silent.Failure!, permanent: true);
            }

            if (!silent.DevToolsUnreachable)
            {
                return await SignInWithAWindowAsync(interactive, progress, silent, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }

        // the browser would not let AnyFS at its DevTools port: WebDriver instead, outside the gate (it keeps its own profile)
        return await FallBackAsync(silent.Failure!, interactive, progress, ct).ConfigureAwait(false);
    }

    /// <summary>After a headless launch found no saved sign-in: a window for the person, where one may be shown.</summary>
    private async Task<IReadOnlyList<SessionCookie>> SignInWithAWindowAsync(bool interactive, Action<string> progress, Capture silent, CancellationToken ct)
    {
        if (!interactive)
        {
            throw new SignInRequiredException($"the browser sign-in for {Site.Host} has lapsed; sign in again (open the source, or its Retry)", permanent: false);
        }

        _logger.LogInformation("{Prefix}: no sign-in to read for {Host} without a window ({Why}); opening {Browser} for the person to sign in", Prefix, Site.Host, silent.Failure, BrowserName);
        progress($"Sign in to {Site.Host} in the {BrowserName} window that opened");
        var shown = await CaptureAsync(headless: false, InteractiveDeadline, ct).ConfigureAwait(false);
        return shown.Cookies ?? throw new SignInRequiredException(shown.Failure!, permanent: true);
    }

    /// <summary>
    /// What one launch of the browser came to: the session's cookies, or why not; whether trying again can help; whether the
    /// browser kept AnyFS from its DevTools port, which WebDriver may still get past.
    /// </summary>
    private sealed record Capture(IReadOnlyList<SessionCookie>? Cookies, string? Failure = null, bool Final = false, bool DevToolsUnreachable = false);

    /// <summary>
    /// Opens the target's page in the browser and waits for the target to find the person signed in, until
    /// <paramref name="deadline"/>. The browser is always closed before returning.
    /// </summary>
    private async Task<Capture> CaptureAsync(bool headless, TimeSpan deadline, CancellationToken ct)
    {
        var mode = headless ? "headless" : "with a window";
        var browserPath = _browserPath ?? throw new InvalidOperationException("no Chromium browser to launch");
        var negotiate = _target.NegotiateHosts.Count > 0 ? new[] { "--auth-server-allowlist=" + string.Join(',', _target.NegotiateHosts) } : [];
        DevToolsBrowser? browser = null;
        try
        {
            var launch = await DevToolsBrowser.LaunchAsync(browserPath, _profileDir, headless, Site.AbsoluteUri, negotiate, _logger, Prefix, ct).ConfigureAwait(false);
            if (launch.Browser is null)
            {
                return new Capture(null, launch.Failure, Final: true, DevToolsUnreachable: true);
            }

            browser = launch.Browser;
            var cdp = browser.Client;
            _logger.LogInformation("{Prefix}: opened {Browser} {Mode} for {Host} (DevTools on port {Port})", Prefix, BrowserName, mode, Site.Host, browser.Port);
            var until = DateTime.UtcNow + deadline;
            var sawPage = false;
            DateTime? noPagesSince = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var cookies = await CookiesAsync(cdp, ct).ConfigureAwait(false);
                var pages = await PagesAsync(cdp, ct).ConfigureAwait(false);
                var check = await _target.CheckAsync(pages, cookies, ct).ConfigureAwait(false);
                if (check.Done)
                {
                    _logger.LogInformation("{Prefix}: read the sign-in to {Host} from {Browser} ({Count} cookies)", Prefix, Site.Host, BrowserName, cookies.Count);
                    return new Capture(cookies);
                }

                if (check.Failure is { } failure)
                {
                    return new Capture(null, failure, Final: true);
                }

                // a browser that keeps running in the background after its last window closes stays connected, so a
                // closed window shows as no pages rather than as a dropped connection (counted once a page has been
                // seen: the DevTools port can open before the first window does)
                sawPage |= pages.Count > 0;
                noPagesSince = headless || !sawPage || pages.Count > 0 ? null : noPagesSince ?? DateTime.UtcNow;
                if (DateTime.UtcNow - noPagesSince > TimeSpan.FromSeconds(3))
                {
                    return new Capture(null, WindowClosed(), Final: true);
                }

                if (DateTime.UtcNow > until)
                {
                    _logger.LogInformation("{Prefix}: no sign-in to {Host} after {Seconds} s {Mode}; pages open: {Pages}; cookies for the session: {Cookies}",
                        Prefix, Site.Host, deadline.TotalSeconds, mode, string.Join(", ", pages.Select(p => p.Host + p.AbsolutePath)), string.Join(", ", cookies.Select(c => c.Name)));
                    return headless
                        ? new Capture(null, "no saved sign-in")
                        : new Capture(null, $"nobody finished the sign-in to {Site.Host} within {InteractiveDeadline.TotalMinutes:N0} minutes; Retry to sign in", Final: true);
                }

                await Task.Delay(TimeSpan.FromSeconds(headless ? 1 : 2), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException)
        {
            // the browser went away before a sign-in was read: a closed window, or a crash
            _logger.LogInformation("{Prefix}: {Browser} ({Mode}) closed before the sign-in to {Host} was read: {Error}", Prefix, BrowserName, mode, Site.Host, ex.Message);
            return headless ? new Capture(null, $"{BrowserName} quit before the sign-in was read") : new Capture(null, WindowClosed(), Final: true);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "{Prefix}: {Browser} answered a DevTools command in an unexpected form", Prefix, BrowserName);
            return new Capture(null, $"{BrowserName} answered AnyFS in a form it does not know ({ex.Message}); the web session cannot read the sign-in from this version", Final: true);
        }
        finally
        {
            if (browser is not null)
            {
                await browser.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private string WindowClosed() => $"the {BrowserName} window was closed before the sign-in to {Site.Host} finished; Retry to sign in";

    /// <summary>The cookies the profile holds that the target keeps.</summary>
    private async Task<IReadOnlyList<SessionCookie>> CookiesAsync(DevToolsClient cdp, CancellationToken ct)
    {
        var result = await cdp.SendAsync("Storage.getCookies", ct: ct).ConfigureAwait(false);
        var cookies = new List<SessionCookie>();
        foreach (var c in Items(result, "cookies"))
        {
            var cookie = new SessionCookie(Text(c, "name"), Text(c, "value"), Text(c, "domain"), Text(c, "path"), Flag(c, "secure"), Flag(c, "httpOnly"));
            if (_target.Keeps(cookie))
            {
                cookies.Add(cookie);
            }
        }

        return cookies;
    }

    /// <summary>The addresses of the browser's open pages (tabs).</summary>
    private static async Task<IReadOnlyList<Uri>> PagesAsync(DevToolsClient cdp, CancellationToken ct)
    {
        var targets = await cdp.SendAsync("Target.getTargets", ct: ct).ConfigureAwait(false);
        return Items(targets, "targetInfos")
            .Where(t => Text(t, "type") == "page")
            .Select(t => Uri.TryCreate(Text(t, "url"), UriKind.Absolute, out var u) ? u : null)
            .OfType<Uri>()
            .ToList();
    }

    // DevTools' answers read strictly: a missing member is an answer in a form this version does not know (KeyNotFound)
    private static IEnumerable<JsonNode> Items(JsonNode? node, string name) => (node?[name] as JsonArray ?? throw new KeyNotFoundException(name)).OfType<JsonNode>();

    private static string Text(JsonNode? node, string name) => node?[name]?.GetValue<string>() ?? throw new KeyNotFoundException(name);

    private static bool Flag(JsonNode? node, string name) => node?[name] is { } value ? value.GetValue<bool>() : throw new KeyNotFoundException(name);
}
