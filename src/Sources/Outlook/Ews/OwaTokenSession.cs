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

        var gate = ProfileLocks.For(_profileDir);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is { } again && again.ExpiresAt - DateTimeOffset.UtcNow > Margin)
            {
                return again.Value;
            }

            if (BrowserLocator.DevToolsBlocked(_browserPath) is { } blocked)
            {
                throw new SignInRequiredException($"The web session cannot read the sign-in from {BrowserName}: {blocked}.", permanent: true);
            }

            var silent = await CaptureAsync(headless: true, SilentDeadline, ct).ConfigureAwait(false);
            if (silent.Token is { } token)
            {
                return Keep(token);
            }

            if (silent.Final)
            {
                throw new SignInRequiredException(silent.Failure!, permanent: true);
            }

            if (!interactive)
            {
                throw new SignInRequiredException($"the sign-in to {_origin.Host} has lapsed; sign in again (open the source, or its Retry)", permanent: false);
            }

            _logger.LogInformation("outlook: no sign-in to read for {Host} without a window ({Why}); opening {Browser} for the person to sign in", _origin.Host, silent.Failure, BrowserName);
            progress($"Sign in to {_origin.Host} in the {BrowserName} window that opened");
            var shown = await CaptureAsync(headless: false, InteractiveDeadline, ct).ConfigureAwait(false);
            return shown.Token is { } fresh ? Keep(fresh) : throw new SignInRequiredException(shown.Failure!, permanent: true);
        }
        finally
        {
            gate.Release();
        }
    }

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
    private sealed record Capture(string? Token, string? Failure = null, bool Final = false);

    private async Task<Capture> CaptureAsync(bool headless, TimeSpan deadline, CancellationToken ct)
    {
        var mode = headless ? "headless" : "with a window";
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
            var pages = new ConcurrentDictionary<string, string>();
            string? found = null;
            devTools.Event += (method, p, sessionId) =>
            {
                switch (method)
                {
                    case "Target.attachedToTarget":
                        {
                            var sid = p["sessionId"]!.GetValue<string>();
                            var info = p["targetInfo"]!;
                            if (info["type"]?.GetValue<string>() == "page")
                            {
                                pages[sid] = info["url"]?.GetValue<string>() ?? "";
                            }

                            _ = devTools.SendAsync("Network.enable", new JsonObject(), sid, CancellationToken.None);
                            break;
                        }

                    case "Target.targetInfoChanged":
                        {
                            var info = p["targetInfo"]!;
                            if (info["type"]?.GetValue<string>() == "page")
                            {
                                foreach (var key in pages.Keys)
                                {
                                    pages[key] = info["url"]?.GetValue<string>() ?? "";
                                }
                            }

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
                page = pages.Keys.FirstOrDefault();
            }

            if (page is null)
            {
                return new Capture(null, $"{BrowserName} opened no page", Final: headless);
            }

            await Task.Delay(300, ct).ConfigureAwait(false);
            await devTools.SendAsync("Page.navigate", new JsonObject { ["url"] = _origin.AbsoluteUri.TrimEnd('/') + "/mail/" }, page, ct).ConfigureAwait(false);

            var until = DateTime.UtcNow + deadline;
            var onSignInSince = (DateTime?)null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (found is { } token)
                {
                    return new Capture(token);
                }

                var where = pages.Values.FirstOrDefault(u => u.Length > 0) ?? "";
                var atSignIn = where.Contains("login.microsoftonline.", StringComparison.OrdinalIgnoreCase) || where.Contains("login.live.com", StringComparison.OrdinalIgnoreCase) || where.Contains("login.windows.net", StringComparison.OrdinalIgnoreCase);
                if (headless && atSignIn)
                {
                    // the profile's session has lapsed: a person is needed, and a headless browser can show them nothing
                    onSignInSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - onSignInSince > TimeSpan.FromSeconds(8))
                    {
                        return new Capture(null, "no saved sign-in");
                    }
                }

                if (!headless && pages.IsEmpty)
                {
                    return new Capture(null, $"the {BrowserName} window was closed before the sign-in to {_origin.Host} finished; Retry to sign in", Final: true);
                }

                if (DateTime.UtcNow > until)
                {
                    _logger.LogInformation("outlook: no token for {Host} after {Seconds} s {Mode}; the page is at {Where}", _origin.Host, deadline.TotalSeconds, mode, where);
                    return headless
                        ? new Capture(null, "no saved sign-in")
                        : new Capture(null, $"nobody finished the sign-in to {_origin.Host} within {InteractiveDeadline.TotalMinutes:N0} minutes; Retry to sign in", Final: true);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException)
        {
            _logger.LogInformation("outlook: {Browser} ({Mode}) closed before the sign-in to {Host} was read: {Error}", BrowserName, mode, _origin.Host, ex.Message);
            return headless ? new Capture(null, $"{BrowserName} quit before the sign-in was read") : new Capture(null, $"the {BrowserName} window was closed before the sign-in to {_origin.Host} finished; Retry to sign in", Final: true);
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
