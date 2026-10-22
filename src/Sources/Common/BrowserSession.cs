using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
/// Selenium WebDriver instead (<see cref="WebDriverSession"/>), for good.
/// </summary>
public sealed class BrowserSession
{
    private static readonly TimeSpan SilentDeadline = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan InteractiveDeadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PortDeadline = TimeSpan.FromSeconds(30);

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

    /// <summary>Where a browser on the profile writes its DevTools port once it listens (<c>--remote-debugging-port=0</c>).</summary>
    private string PortFile => Path.Combine(_profileDir, "DevToolsActivePort");

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
        Directory.CreateDirectory(_profileDir);
        await CloseLeftoverAsync(ct).ConfigureAwait(false);
        var mode = headless ? "headless" : "with a window";
        using var browser = LaunchBrowser(headless);
        Cdp? cdp = null;
        try
        {
            var endpoint = await DevToolsEndpointAsync(browser, ct).ConfigureAwait(false);
            if (endpoint is null)
            {
                return new Capture(null, NoDevTools(browser.HasExited), Final: true, DevToolsUnreachable: true);
            }

            cdp = await Cdp.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            _logger.LogInformation("{Prefix}: opened {Browser} {Mode} for {Host} (DevTools on port {Port})", Prefix, BrowserName, mode, Site.Host, endpoint.Port);
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
            await CloseAsync(browser, cdp).ConfigureAwait(false);
        }
    }

    private string WindowClosed() => $"the {BrowserName} window was closed before the sign-in to {Site.Host} finished; Retry to sign in";

    private string NoDevTools(bool handedOver) => handedOver
        ? $"{BrowserName} handed the launch to another {BrowserName} that holds the profile and cannot be reached (a window left open, or a profile folder fixed by policy)"
        : $"{BrowserName} started without opening its DevTools port (your organisation's RemoteDebuggingAllowed policy does this; edge://policy lists the policies in force)";

    /// <summary>
    /// Closes a browser still open on the profile from an earlier sign-in (AnyFS stopped while its window was up, or an
    /// older AnyFS left it): a new launch would be handed to it and exit at once. A port file nothing answers on is stale.
    /// </summary>
    private async Task CloseLeftoverAsync(CancellationToken ct)
    {
        if (ReadPortFile() is { } endpoint && await AnswersAsync(endpoint, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("{Prefix}: closing the {Browser} left open on the sign-in profile {Profile}", Prefix, BrowserName, _profileDir);
            try
            {
                using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                quick.CancelAfter(TimeSpan.FromSeconds(5));
                await using var cdp = await Cdp.ConnectAsync(endpoint, quick.Token).ConfigureAwait(false);
                await cdp.SendAsync("Browser.close", ct: quick.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                // it may close the connection before it answers
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

    private Process LaunchBrowser(bool headless)
    {
        var info = new ProcessStartInfo(_browserPath ?? throw new InvalidOperationException("no Chromium browser to launch")) { UseShellExecute = false };
        foreach (var arg in new[] { $"--user-data-dir={_profileDir}", "--remote-debugging-port=0", "--no-first-run", "--no-default-browser-check", "--new-window" })
        {
            info.ArgumentList.Add(arg);
        }

        if (headless)
        {
            info.ArgumentList.Add("--headless=new");
        }

        info.ArgumentList.Add(Site.AbsoluteUri);

        // the browser's own log lines would otherwise land in AnyFS's terminal (anyfs serve); drained, not kept
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        var browser = Process.Start(info) ?? throw new SignInRequiredException($"{BrowserName} did not start", permanent: true);
        browser.OutputDataReceived += (_, _) => { };
        browser.ErrorDataReceived += (_, _) => { };
        browser.BeginOutputReadLine();
        browser.BeginErrorReadLine();
        return browser;
    }

    /// <summary>
    /// The DevTools address the browser writes to the profile once it listens: the port, then the browser endpoint's
    /// path. Null when none appears in time: the browser runs without one (remote debugging turned off), or it exited
    /// at once, having handed the launch to a browser already holding the profile.
    /// </summary>
    private async Task<Uri?> DevToolsEndpointAsync(Process browser, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < PortDeadline)
        {
            if (ReadPortFile() is { } endpoint)
            {
                return endpoint;
            }

            // a handed-over launch exits at once; a browser that relaunches itself gets a few seconds to write the file
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

    /// <summary>The cookies the profile holds that the target keeps.</summary>
    private async Task<IReadOnlyList<SessionCookie>> CookiesAsync(Cdp cdp, CancellationToken ct)
    {
        var result = await cdp.SendAsync("Storage.getCookies", ct: ct).ConfigureAwait(false);
        var cookies = new List<SessionCookie>();
        foreach (var c in result.GetProperty("cookies").EnumerateArray())
        {
            var cookie = new SessionCookie(
                c.GetProperty("name").GetString()!,
                c.GetProperty("value").GetString()!,
                c.GetProperty("domain").GetString()!,
                c.GetProperty("path").GetString()!,
                c.GetProperty("secure").GetBoolean(),
                c.GetProperty("httpOnly").GetBoolean());
            if (_target.Keeps(cookie))
            {
                cookies.Add(cookie);
            }
        }

        return cookies;
    }

    /// <summary>The addresses of the browser's open pages (tabs).</summary>
    private static async Task<IReadOnlyList<Uri>> PagesAsync(Cdp cdp, CancellationToken ct)
    {
        var targets = await cdp.SendAsync("Target.getTargets", ct: ct).ConfigureAwait(false);
        return targets.GetProperty("targetInfos").EnumerateArray()
            .Where(t => t.GetProperty("type").GetString() == "page")
            .Select(t => Uri.TryCreate(t.GetProperty("url").GetString(), UriKind.Absolute, out var u) ? u : null)
            .OfType<Uri>()
            .ToList();
    }

    /// <summary>Closes the browser through DevTools, or kills the process the launch started when it cannot be reached.</summary>
    private static async Task CloseAsync(Process browser, Cdp? cdp)
    {
        if (cdp is not null)
        {
            try
            {
                await cdp.SendAsync("Browser.close", ct: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException or InvalidOperationException or ObjectDisposedException)
            {
                // it may close the connection before it answers
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

/// <summary>
/// One browser at a time on a profile. A second launch on a profile that is already open is handed to the browser holding
/// it and exits at once, so two sources for one site starting together, or a renewal during a sign-in, would lose track
/// of each other's browser.
/// </summary>
internal static class ProfileLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim For(string profileDir) => Gates.GetOrAdd(Path.GetFullPath(profileDir), _ => new SemaphoreSlim(1, 1));
}

/// <summary>Just enough of the Chrome DevTools protocol: commands over one WebSocket, answers matched by id; events ignored (the caller polls).</summary>
internal sealed class Cdp : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private Task _receive = Task.CompletedTask;
    private int _nextId;

    public static async Task<Cdp> ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        var cdp = new Cdp();
        try
        {
            await cdp._socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch
        {
            await cdp.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        cdp._receive = cdp.ReceiveAsync();
        return cdp;
    }

    public async Task<JsonElement> SendAsync(string method, JsonObject? parameters = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        if (_receive.IsCompleted)
        {
            // the browser is gone: nothing will answer
            _pending.TryRemove(id, out _);
            throw new IOException("the DevTools connection closed");
        }

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }

        return await answer.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[1 << 16];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(message.ToArray());
                message.SetLength(0);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var id) && _pending.TryRemove(id.GetInt32(), out var answer))
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        answer.TrySetException(new IOException($"DevTools error: {error.GetRawText()}"));
                    }
                    else
                    {
                        answer.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
        }
        finally
        {
            foreach (var answer in _pending.Values)
            {
                answer.TrySetException(new IOException("the DevTools connection closed"));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }

        try
        {
            await _receive.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
        }

        _socket.Dispose();
        _sendGate.Dispose();
        _stop.Dispose();
    }
}
