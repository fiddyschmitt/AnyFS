using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Outlook.Ews;

/// <summary>
/// The web session's token for Outlook on the web (spec 7.7.2, Spikes 11 and 14): a browser of AnyFS's own, on a
/// profile per mailbox, opens Outlook on the web; the page's own calls to service.svc carry the bearer Microsoft's
/// One Outlook Web client minted for the signed-in account (about a day's life, renewed silently by the page), which
/// AnyFS reads off those requests through DevTools and uses itself. A headless launch gets it without a window while
/// the profile's sign-in holds; a window is opened for the person only when it has lapsed and the caller allows.
/// </summary>
internal sealed class OwaTokenSession
{
    private static readonly TimeSpan SilentDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InteractiveDeadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(10);

    private readonly Uri _origin;
    private readonly string _browserPath;
    private readonly string _profileDir;
    private readonly ILogger _logger;
    private (string Value, DateTimeOffset ExpiresAt)? _token;

    /// <param name="origin">Outlook on the web's origin for the cloud (https://outlook.office.com).</param>
    /// <param name="browserPath">The Chromium browser to drive.</param>
    /// <param name="profileDir">The profile that holds the mailbox's sign-in.</param>
    public OwaTokenSession(Uri origin, string browserPath, string profileDir, ILogger logger)
    {
        _origin = origin;
        _browserPath = browserPath;
        _profileDir = profileDir;
        _logger = logger;
    }

    public string BrowserName => BrowserLocator.DisplayName(_browserPath);

    /// <summary>The account the token is for, from its claims; null before a token has been read.</summary>
    public string? Account { get; private set; }

    /// <summary>
    /// A bearer good for at least ten more minutes: the one in hand, else one read from the browser (headless first; a
    /// window only when <paramref name="interactive"/>). Throws <see cref="SignInRequiredException"/> when none can be
    /// had: permanent when waiting cannot help.
    /// </summary>
    public async Task<string> TokenAsync(bool interactive, Action<string> progress, CancellationToken ct)
    {
        if (_token is { } kept && kept.ExpiresAt - DateTimeOffset.UtcNow > Margin)
        {
            return kept.Value;
        }

        if (!interactive)
        {
            _lapse.ThrowIfRemembered();
        }

        var gate = ProfileLocks.For(_profileDir);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is { } again && again.ExpiresAt - DateTimeOffset.UtcNow > Margin)
            {
                return again.Value;
            }

            // the one that held the profile before this may have found it out
            if (!interactive)
            {
                _lapse.ThrowIfRemembered();
            }

            if (BrowserLocator.DevToolsBlocked(_browserPath) is { } blocked)
            {
                throw new SignInRequiredException($"The web session cannot read the sign-in from {BrowserName}: {blocked}.", permanent: true);
            }

            // a site that does not answer has no sign-in to read and no page to sign in on (SiteProbe): no browser for it
            if (await SiteProbe.UnreachableAsync(_origin, ct).ConfigureAwait(false) is { } unreachable)
            {
                throw new SignInRequiredException($"{_origin.Host} did not answer ({unreachable}), so its sign-in was not tried", permanent: false);
            }

            var silent = await CaptureAsync(headless: true, SilentDeadline, ct).ConfigureAwait(false);
            if (silent.Token is { } token)
            {
                _lapse.Forget();
                return Keep(token);
            }

            if (silent.Final)
            {
                throw _lapse.Remember(new SignInRequiredException(silent.Failure!, permanent: true));
            }

            if (!interactive)
            {
                throw _lapse.Remember(new SignInRequiredException($"the sign-in to {_origin.Host} has lapsed; sign in again (open the source, or its Retry)", permanent: false));
            }

            _logger.LogInformation("outlook: no sign-in to read for {Host} without a window ({Why}); opening {Browser} for the person to sign in", _origin.Host, silent.Failure, BrowserName);
            progress($"Sign in to {_origin.Host} in the {BrowserName} window that opened");
            var shown = await CaptureAsync(headless: false, InteractiveDeadline, ct).ConfigureAwait(false);
            if (shown.Token is { } fresh)
            {
                _lapse.Forget();
                return Keep(fresh);
            }

            throw new SignInRequiredException(shown.Failure!, permanent: true);
        }
        finally
        {
            gate.Release();
        }
    }

    // a renewal found to need the person: the requests that follow are told so at once, each launching no browser of its own
    private readonly SignInLapse _lapse = new();

    /// <summary>Forgets the token in hand (the server refused it): the next ask reads a fresh one.</summary>
    public void Invalidate() => _token = null;

    private string Keep(string token)
    {
        var (account, expires) = Claims(token);
        Account = account ?? Account;
        _token = (token, expires ?? DateTimeOffset.UtcNow + TimeSpan.FromMinutes(20));
        _logger.LogInformation("outlook: read the web session's token for {Host}{Account}, good until {Until:HH:mm}", _origin.Host, account is null ? "" : $" as {account}", _token.Value.ExpiresAt.ToLocalTime());
        return token;
    }

    /// <summary>What one launch of the browser came to: a token, or why not and whether trying again can help.</summary>
    internal sealed record Capture(string? Token, string? Failure = null, bool Final = false);

    private async Task<Capture> CaptureAsync(bool headless, TimeSpan deadline, CancellationToken ct)
    {
        var mode = Mode(headless);
        DevToolsBrowser? browser = null;
        try
        {
            var launch = await DevToolsBrowser.LaunchAsync(_browserPath, _profileDir, headless, "about:blank", [], _logger, "outlook", ct).ConfigureAwait(false);
            if (launch.Browser is null)
            {
                return new Capture(null, launch.Failure, Final: true);
            }

            browser = launch.Browser;
            var devTools = browser.Client;
            _logger.LogInformation("outlook: opened {Browser} {Mode} for {Host} (DevTools on port {Port})", BrowserName, mode, _origin.Host, browser.Port);

            var requests = new ConcurrentDictionary<string, (string? Url, string? Bearer)>();
            var pages = new DevToolsPages();
            string? found = null;
            devTools.Event += (method, p, sessionId) =>
            {
                pages.On(method, p);
                switch (method)
                {
                    case "Target.attachedToTarget":
                        {
                            _ = devTools.SendAsync("Network.enable", new JsonObject(), p["sessionId"]!.GetValue<string>(), CancellationToken.None);
                            break;
                        }

                    case "Network.requestWillBeSent":
                    case "Network.requestWillBeSentExtraInfo":
                        {
                            var id = sessionId + ":" + p["requestId"]?.GetValue<string>();
                            var url = p["request"]?["url"]?.GetValue<string>();
                            var headers = (p["request"]?["headers"] ?? p["headers"]) as JsonObject;
                            var auth = headers?.FirstOrDefault(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();
                            var bearer = auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..] : null;
                            // the page itself may be served from another host (outlook.cloud.microsoft) than the service it calls;
                            // any service.svc call's bearer is the one, its audience being the cloud's Outlook on the web
                            var seen = requests.AddOrUpdate(id, _ => (url, bearer), (_, old) => (url ?? old.Url, bearer ?? old.Bearer));
                            if (seen.Bearer is not null && seen.Url is not null && seen.Url.Contains("/service.svc", StringComparison.OrdinalIgnoreCase))
                            {
                                found = seen.Bearer;
                            }

                            break;
                        }
                }
            };
            await devTools.SendAsync("Target.setDiscoverTargets", new JsonObject { ["discover"] = true }, ct: ct).ConfigureAwait(false);
            await devTools.SendAsync("Target.setAutoAttach", new JsonObject { ["autoAttach"] = true, ["waitForDebuggerOnStart"] = false, ["flatten"] = true }, ct: ct).ConfigureAwait(false);

            // the first page is blank: DevTools listens before Outlook on the web's start-up calls, which carry the token
            string? page = null;
            for (var i = 0; i < 100 && page is null; i++)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                page = pages.FirstSession;
            }

            if (page is null)
            {
                return new Capture(null, $"{BrowserName} opened no page", Final: headless);
            }

            await Task.Delay(300, ct).ConfigureAwait(false);
            await devTools.SendAsync("Page.navigate", new JsonObject { ["url"] = _origin.AbsoluteUri.TrimEnd('/') + "/mail/" }, page, ct).ConfigureAwait(false);
            return await WaitAsync(headless, deadline, () => found, () => pages.Where, () => !pages.Any, devTools.Closed, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException)
        {
            return BrowserGone(headless, ex.Message);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return new Capture(null, $"{BrowserName} answered AnyFS in a form it does not know ({ex.Message})", Final: true);
        }
        finally
        {
            if (browser is not null)
            {
                await browser.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Waits while the page signs in and calls the service: the token, once a call carried it; otherwise what the wait
    /// came to — a headless launch held on the sign-in page (no saved sign-in), the window closed (<paramref name="noPage"/>:
    /// its last page gone, though the browser may stay up in the background), the browser gone, or the deadline.
    /// <paramref name="gone"/> ends when the browser's connection has (<see cref="DevToolsClient.Closed"/>): the wait
    /// sends the browser nothing, so nothing else would tell it.
    /// </summary>
    internal async Task<Capture> WaitAsync(bool headless, TimeSpan deadline, Func<string?> found, Func<string> where, Func<bool> noPage, Task gone, CancellationToken ct)
    {
        var until = DateTime.UtcNow + deadline;
        var onSignInSince = (DateTime?)null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (found() is { } token)
            {
                return new Capture(token);
            }

            if (gone.IsCompleted)
            {
                return BrowserGone(headless, "its DevTools connection closed");
            }

            var page = where();
            var atSignIn = page.Contains("login.microsoftonline.", StringComparison.OrdinalIgnoreCase) || page.Contains("login.live.com", StringComparison.OrdinalIgnoreCase) || page.Contains("login.windows.net", StringComparison.OrdinalIgnoreCase);
            if (headless && atSignIn)
            {
                // the profile's session has lapsed: a person is needed, and a headless browser can show them nothing
                onSignInSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - onSignInSince > TimeSpan.FromSeconds(8))
                {
                    return new Capture(null, "no saved sign-in");
                }
            }

            if (!headless && noPage())
            {
                return WindowClosed();
            }

            if (DateTime.UtcNow > until)
            {
                _logger.LogInformation("outlook: no token for {Host} after {Seconds} s {Mode}; the page is at {Where}", _origin.Host, deadline.TotalSeconds, Mode(headless), WebSignInTarget.Bare(page));
                return headless
                    ? new Capture(null, "no saved sign-in")
                    : new Capture(null, $"nobody finished the sign-in to {_origin.Host} within {InteractiveDeadline.TotalMinutes:N0} minutes; Retry to sign in", Final: true);
            }

            // the next look in a second, or at once when the browser goes: until 22-Nov-2026 only the second, and a browser
            // that had gone was waited on to the deadline
            await Task.WhenAny(gone, Task.Delay(TimeSpan.FromSeconds(1), ct)).ConfigureAwait(false);
        }
    }

    private Capture WindowClosed() => new(null, $"the {BrowserName} window was closed before the sign-in to {_origin.Host} finished; Retry to sign in", Final: true);

    /// <summary>The browser went before the sign-in was read: a window is the person's to close, and their closing it is final; one without a window quit, which a window may yet get past.</summary>
    private Capture BrowserGone(bool headless, string how)
    {
        _logger.LogInformation("outlook: {Browser} ({Mode}) closed before the sign-in to {Host} was read: {Error}", BrowserName, Mode(headless), _origin.Host, how);
        return headless ? new Capture(null, $"{BrowserName} quit before the sign-in was read") : WindowClosed();
    }

    private static string Mode(bool headless) => headless ? "headless" : "with a window";

    /// <summary>The account (upn or preferred_username) and expiry a JWT carries; nulls for anything else.</summary>
    internal static (string? Account, DateTimeOffset? ExpiresAt) Claims(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return (null, null);
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = json.RootElement;
            var account = root.TryGetProperty("upn", out var upn) ? upn.GetString() : root.TryGetProperty("preferred_username", out var pu) ? pu.GetString() : null;
            var expires = root.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null;
            return (account, expires);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return (null, null);
        }
    }
}
