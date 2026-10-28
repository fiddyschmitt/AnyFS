using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Teams;

/// <summary>
/// The web session's tokens for the Teams services (spec 7.9, spike 18): a browser of AnyFS's own, on a profile per
/// account, opens Teams on the web; the page's own calls to the chat service, the aggregator and the middle tier carry a
/// bearer per service that Microsoft's first-party Teams web client minted for the signed-in account, which AnyFS reads
/// off those requests through DevTools and uses itself — the same way the Outlook web session (<c>OwaTokenSession</c>)
/// reads the one bearer OWA mints. A headless launch gets them without a window while the profile's sign-in holds; a
/// window is opened for the person only when it has lapsed and the caller allows. The account each token is for is
/// checked against the account the source was given, so one profile's session is never read as another's.
/// </summary>
internal sealed class TeamsWebSession(Uri teamsHost, string browserPath, string profileDir, string expectedAccount, ILogger logger)
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SilentDeadline = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan InteractiveDeadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PortDeadline = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    // the service audiences the source needs a bearer for (spike 18)
    private static readonly string[] Wanted = ["https://ic3.teams.office.com", "https://chatsvcagg.teams.microsoft.com", "https://api.spaces.skype.com"];

    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public string BrowserName => BrowserLocator.DisplayName(browserPath);

    /// <summary>The account the tokens are for, from their claims; null before any has been read.</summary>
    public string? Account { get; private set; }

    /// <summary>The account's own mri (<c>8:orgid:&lt;object id&gt;</c>), from a token's object-id claim.</summary>
    public string? Mri { get; private set; }

    /// <summary>A bearer for <paramref name="audience"/> good for at least ten more minutes; captures a fresh set from the browser when none is in hand.</summary>
    public async Task<string> TokenAsync(string audience, bool interactive, Action<string> progress, CancellationToken ct)
    {
        if (Fresh(audience) is { } kept)
        {
            return kept;
        }

        var gate = Gates.GetOrAdd(Path.GetFullPath(profileDir), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Fresh(audience) is { } again)
            {
                return again;
            }

            if (BrowserLocator.DevToolsBlocked(browserPath) is { } blocked)
            {
                throw new SignInRequiredException($"The web session cannot read the sign-in from {BrowserName}: {blocked}.", permanent: true);
            }

            var silent = await CaptureAsync(headless: true, SilentDeadline, ct).ConfigureAwait(false);
            if (silent.Final && silent.Failure is { } fatal)
            {
                throw new SignInRequiredException(fatal, permanent: true);
            }

            if (Fresh(audience) is { } fresh)
            {
                return fresh;
            }

            if (!interactive)
            {
                throw new SignInRequiredException($"the sign-in to {teamsHost.Host} has lapsed; sign in again (open the source, or its Retry)", permanent: false);
            }

            logger.LogInformation("teams: no sign-in to read for {Host} without a window ({Why}); opening {Browser} for the person to sign in", teamsHost.Host, silent.Failure, BrowserName);
            progress($"Sign in to Teams in the {BrowserName} window that opened");
            var shown = await CaptureAsync(headless: false, InteractiveDeadline, ct).ConfigureAwait(false);
            return Fresh(audience) ?? throw new SignInRequiredException(shown.Failure ?? $"no sign-in to {teamsHost.Host} was read", permanent: shown.Final);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Forgets a token the service refused: the next ask reads a fresh one.</summary>
    public void Invalidate(string audience) => _tokens.TryRemove(audience, out _);

    private string? Fresh(string audience) => _tokens.TryGetValue(audience, out var t) && t.Expires - DateTimeOffset.UtcNow > Margin ? t.Token : null;

    private void Keep(string token)
    {
        var (aud, account, mri, expires) = Claims(token);
        if (aud is null || expires is null)
        {
            return;
        }

        var match = Wanted.FirstOrDefault(w => aud.TrimEnd('/').Equals(w, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return;
        }

        Account ??= account;
        Mri ??= mri;
        _tokens[match] = (token, expires.Value);
    }

    /// <summary>What one browser launch came to: whether a fresh set was captured, else why not and whether trying again can help.</summary>
    private sealed record Capture(bool Got, string? Failure = null, bool Final = false);

    private async Task<Capture> CaptureAsync(bool headless, TimeSpan deadline, CancellationToken ct)
    {
        Directory.CreateDirectory(profileDir);
        await CloseLeftoverAsync(ct).ConfigureAwait(false);
        var mode = headless ? "headless" : "with a window";
        using var browser = Launch(headless);
        TeamsCdp? cdp = null;
        try
        {
            var endpoint = await DevToolsEndpointAsync(browser, ct).ConfigureAwait(false);
            if (endpoint is null)
            {
                return new Capture(false, browser.HasExited
                    ? $"{BrowserName} handed the launch to another {BrowserName} that holds the profile"
                    : $"{BrowserName} started without opening its DevTools port (your organisation's RemoteDebuggingAllowed policy does this)", Final: true);
            }

            cdp = await TeamsCdp.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            logger.LogInformation("teams: opened {Browser} {Mode} for {Host}", BrowserName, mode, teamsHost.Host);

            var pages = new ConcurrentDictionary<string, string>();
            string? wrongAccount = null;
            cdp.Event += (method, p, sessionId) =>
            {
                switch (method)
                {
                    case "Target.attachedToTarget":
                        {
                            var sid = p["sessionId"]!.GetValue<string>();
                            if (p["targetInfo"]?["type"]?.GetValue<string>() == "page")
                            {
                                pages[sid] = p["targetInfo"]?["url"]?.GetValue<string>() ?? "";
                            }

                            _ = cdp.SendAsync("Network.enable", new JsonObject(), sid, CancellationToken.None);
                            _ = cdp.SendAsync("Target.setAutoAttach", new JsonObject { ["autoAttach"] = true, ["waitForDebuggerOnStart"] = false, ["flatten"] = true }, sid, CancellationToken.None);
                            break;
                        }

                    case "Target.targetInfoChanged":
                        {
                            if (p["targetInfo"]?["type"]?.GetValue<string>() == "page")
                            {
                                foreach (var key in pages.Keys)
                                {
                                    pages[key] = p["targetInfo"]?["url"]?.GetValue<string>() ?? "";
                                }
                            }

                            break;
                        }

                    case "Network.requestWillBeSent":
                    case "Network.requestWillBeSentExtraInfo":
                        {
                            var headers = (p["request"]?["headers"] ?? p["headers"]) as JsonObject;
                            var auth = headers?.FirstOrDefault(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();
                            if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                            {
                                var token = auth[7..];
                                var (_, account, _, _) = Claims(token);
                                if (account is not null && !account.Equals(expectedAccount, StringComparison.OrdinalIgnoreCase))
                                {
                                    // another account is signed in on this profile: do not read it
                                    wrongAccount = account;
                                }
                                else
                                {
                                    Keep(token);
                                }
                            }

                            break;
                        }
                }
            };
            await cdp.SendAsync("Target.setDiscoverTargets", new JsonObject { ["discover"] = true }, ct: ct).ConfigureAwait(false);
            await cdp.SendAsync("Target.setAutoAttach", new JsonObject { ["autoAttach"] = true, ["waitForDebuggerOnStart"] = false, ["flatten"] = true }, ct: ct).ConfigureAwait(false);

            string? page = null;
            for (var i = 0; i < 100 && page is null; i++)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                page = pages.Keys.FirstOrDefault();
            }

            if (page is null)
            {
                return new Capture(false, $"{BrowserName} opened no page", Final: headless);
            }

            await Task.Delay(300, ct).ConfigureAwait(false);
            await cdp.SendAsync("Page.navigate", new JsonObject { ["url"] = $"{teamsHost.AbsoluteUri.TrimEnd('/')}/v2/?login_hint={Uri.EscapeDataString(expectedAccount)}" }, page, ct).ConfigureAwait(false);

            var until = DateTime.UtcNow + deadline;
            var onSignInSince = (DateTime?)null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (wrongAccount is { } other)
                {
                    return new Capture(false, $"the {BrowserName} profile is signed in as {other}, not {expectedAccount}; sign in as {expectedAccount}", Final: true);
                }

                if (Wanted.All(w => Fresh(w) is not null))
                {
                    return new Capture(true);
                }

                var where = pages.Values.FirstOrDefault(u => u.Length > 0) ?? "";
                if (headless && (where.Contains("login.microsoftonline.", StringComparison.OrdinalIgnoreCase) || where.Contains("login.live.com", StringComparison.OrdinalIgnoreCase)))
                {
                    onSignInSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - onSignInSince > TimeSpan.FromSeconds(8))
                    {
                        return new Capture(false, "no saved sign-in");
                    }
                }

                if (!headless && pages.IsEmpty)
                {
                    return new Capture(false, $"the {BrowserName} window was closed before the sign-in to {teamsHost.Host} finished; Retry to sign in", Final: true);
                }

                if (DateTime.UtcNow > until)
                {
                    logger.LogInformation("teams: {Count}/{Total} service tokens after {Seconds:F0}s {Mode}; the page is at {Where}", Wanted.Count(w => Fresh(w) is not null), Wanted.Length, deadline.TotalSeconds, mode, where);
                    return Wanted.Any(w => Fresh(w) is not null) && !headless
                        ? new Capture(true)
                        : new Capture(false, headless ? "no saved sign-in" : $"nobody finished the sign-in to {teamsHost.Host} in time; Retry to sign in", Final: !headless);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException)
        {
            return new Capture(false, $"{BrowserName} ({mode}) closed before the sign-in to {teamsHost.Host} was read: {ex.Message}", Final: !headless);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return new Capture(false, $"{BrowserName} answered AnyFS in a form it does not know ({ex.Message})", Final: true);
        }
        finally
        {
            await CloseAsync(browser, cdp).ConfigureAwait(false);
        }
    }

    /// <summary>A JWT's audience, the account (upn/preferred_username), the account's mri (<c>8:orgid:&lt;oid&gt;</c>) and expiry; nulls for anything else.</summary>
    internal static (string? Aud, string? Account, string? Mri, DateTimeOffset? Expires) Claims(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return (null, null, null, null);
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = json.RootElement;
            string? S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var aud = S("aud");
            var account = S("upn") ?? S("preferred_username") ?? S("unique_name");
            var oid = S("oid");
            var mri = oid is null ? null : $"8:orgid:{oid}";
            var expires = root.TryGetProperty("exp", out var e) && e.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null;
            return (aud, account, mri, expires);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return (null, null, null, null);
        }
    }

    private string PortFile => Path.Combine(profileDir, "DevToolsActivePort");

    private Process Launch(bool headless)
    {
        var info = new ProcessStartInfo(browserPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { $"--user-data-dir={profileDir}", "--remote-debugging-port=0", "--no-first-run", "--no-default-browser-check", "--new-window" })
        {
            info.ArgumentList.Add(arg);
        }

        if (headless)
        {
            info.ArgumentList.Add("--headless=new");
        }

        info.ArgumentList.Add("about:blank");
        var browser = Process.Start(info) ?? throw new SignInRequiredException($"{BrowserName} did not start", permanent: true);
        browser.OutputDataReceived += (_, _) => { };
        browser.ErrorDataReceived += (_, _) => { };
        browser.BeginOutputReadLine();
        browser.BeginErrorReadLine();
        return browser;
    }

    private async Task<Uri?> DevToolsEndpointAsync(Process browser, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < PortDeadline)
        {
            if (ReadPortFile() is { } endpoint)
            {
                return endpoint;
            }

            if (browser.HasExited && DateTime.UtcNow - started > TimeSpan.FromSeconds(5))
            {
                return null;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return null;
    }

    private Uri? ReadPortFile()
    {
        try
        {
            var lines = File.ReadAllLines(PortFile);
            return lines.Length >= 2 && int.TryParse(lines[0], out var port) && port > 0 ? new Uri($"ws://127.0.0.1:{port}{lines[1]}") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task CloseLeftoverAsync(CancellationToken ct)
    {
        if (ReadPortFile() is { } endpoint && await AnswersAsync(endpoint, ct).ConfigureAwait(false))
        {
            logger.LogInformation("teams: closing the {Browser} left open on the sign-in profile", BrowserName);
            try
            {
                using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                quick.CancelAfter(TimeSpan.FromSeconds(5));
                await using var cdp = await TeamsCdp.ConnectAsync(endpoint, quick.Token).ConfigureAwait(false);
                await cdp.SendAsync("Browser.close", ct: quick.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
            }

            for (var i = 0; i < 20 && await AnswersAsync(endpoint, ct).ConfigureAwait(false); i++)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }

        try
        {
            File.Delete(PortFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task<bool> AnswersAsync(Uri endpoint, CancellationToken ct)
    {
        using var probe = new TcpClient();
        using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quick.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await probe.ConnectAsync("127.0.0.1", endpoint.Port, quick.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task CloseAsync(Process browser, TeamsCdp? cdp)
    {
        if (cdp is not null)
        {
            try
            {
                await cdp.SendAsync("Browser.close", ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException or InvalidOperationException or ObjectDisposedException)
            {
            }

            await cdp.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            if (!browser.WaitForExit(8000))
            {
                browser.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
