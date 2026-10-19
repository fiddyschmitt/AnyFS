using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.SharePoint;

/// <summary>One cookie a signed-in SharePoint page holds: enough to replay it on a plain HTTP request.</summary>
internal sealed record SessionCookie(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly);

/// <summary>
/// A SharePoint Online sign-in taken from a browser, for people whose organisation will not register an app
/// (spec 7.3.2). AnyFS launches a Chromium browser (<see cref="BrowserLocator"/>) with a profile of its own, one per
/// site host (<see cref="SharePointPlugin.ProfileDir"/>), so the person's own browser and its profile are never read; the person signs in on
/// Microsoft's real page, where multi-factor and Conditional Access happen as always; the site's cookies are read
/// over the browser's DevTools protocol and replayed on plain requests to SharePoint's REST API, the way the
/// site's own pages call it. The browser is open only long enough to read the cookies, never while files are
/// served. The profile keeps the Microsoft sign-in session, so a later headless launch renews the site cookies
/// with nobody at the keyboard until that session itself lapses; only then is a window shown again.
/// </summary>
internal sealed class BrowserSession
{
    private static readonly TimeSpan SilentDeadline = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan InteractiveDeadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PortDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One browser at a time on a profile. A second launch on a profile that is already open is handed to the browser
    /// holding it and exits at once, so two sources for one site starting together, or a renewal during a sign-in,
    /// would lose track of each other's browser.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProfileGates = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _browserPath;
    private readonly string _profileDir;
    private readonly ILogger _logger;

    public BrowserSession(string browserPath, string profileDir, ILogger logger)
    {
        _browserPath = browserPath;
        _profileDir = profileDir;
        _logger = logger;
    }

    /// <summary>The browser AnyFS will drive, for messages ("Microsoft Edge").</summary>
    public string BrowserName => BrowserLocator.DisplayName(_browserPath);

    /// <summary>Where a browser on the profile writes its DevTools port once it listens (<c>--remote-debugging-port=0</c>).</summary>
    private string PortFile => Path.Combine(_profileDir, "DevToolsActivePort");

    /// <summary>
    /// The site's cookies: first a headless launch, which succeeds without a window while the profile's Microsoft
    /// session is still good (a restart, or a silent renewal); failing that, and only when <paramref name="interactive"/>,
    /// a window for the person to sign in. Throws <see cref="SignInRequiredException"/> when no sign-in can be had:
    /// permanent when waiting cannot help (remote debugging turned off, the window closed, nobody signing in), so the
    /// source waits for a Retry instead of opening windows every few minutes.
    /// </summary>
    public async Task<IReadOnlyList<SessionCookie>> SignInAsync(Uri site, bool interactive, Action<string> progress, CancellationToken ct)
    {
        if (BrowserLocator.RemoteDebuggingOff(_browserPath) is { } policy)
        {
            throw new SignInRequiredException($"{BrowserName} has remote debugging turned off by your organisation's policy ({policy}), and the web session reads the sign-in through it. Use an app sign-in instead (Sign in with), with your organisation's own app registration where AnyFS's cannot be used.", permanent: true);
        }

        var gate = ProfileGates.GetOrAdd(Path.GetFullPath(_profileDir), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var silent = await CaptureAsync(site, headless: true, SilentDeadline, ct).ConfigureAwait(false);
            if (silent.Cookies is { } cookies)
            {
                return cookies;
            }

            if (silent.Final)
            {
                throw new SignInRequiredException(silent.Failure!, permanent: true);
            }

            if (!interactive)
            {
                throw new SignInRequiredException($"the browser sign-in for {site.Host} has lapsed; sign in again (open the source, or its Retry)", permanent: false);
            }

            _logger.LogInformation("sharepoint: no sign-in to read for {Host} without a window ({Why}); opening {Browser} for the person to sign in", site.Host, silent.Failure, BrowserName);
            progress($"Sign in to {site.Host} in the {BrowserName} window that opened");
            var shown = await CaptureAsync(site, headless: false, InteractiveDeadline, ct).ConfigureAwait(false);
            return shown.Cookies ?? throw new SignInRequiredException(shown.Failure!, permanent: true);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>What one launch of the browser came to: the site's cookies, or why not, and whether trying again can help.</summary>
    private sealed record Capture(IReadOnlyList<SessionCookie>? Cookies, string? Failure = null, bool Final = false);

    /// <summary>
    /// Opens <paramref name="site"/> in the browser and waits for its FedAuth cookie with a page on the site, meaning the
    /// page is signed in, until <paramref name="deadline"/>. The browser is always closed before returning.
    /// </summary>
    private async Task<Capture> CaptureAsync(Uri site, bool headless, TimeSpan deadline, CancellationToken ct)
    {
        Directory.CreateDirectory(_profileDir);
        await CloseLeftoverAsync(ct).ConfigureAwait(false);
        var mode = headless ? "headless" : "with a window";
        using var browser = LaunchBrowser(site, headless);
        Cdp? cdp = null;
        try
        {
            var endpoint = await DevToolsEndpointAsync(browser, ct).ConfigureAwait(false);
            if (endpoint is null)
            {
                return new Capture(null, NoDevTools(browser.HasExited), Final: true);
            }

            cdp = await Cdp.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            _logger.LogInformation("sharepoint: opened {Browser} {Mode} for {Host} (DevTools on port {Port})", BrowserName, mode, site.Host, endpoint.Port);
            var until = DateTime.UtcNow + deadline;
            var sawPage = false;
            DateTime? noPagesSince = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var cookies = await SiteCookiesAsync(cdp, site, ct).ConfigureAwait(false);
                var pages = await PagesAsync(cdp, ct).ConfigureAwait(false);
                if (cookies.Any(c => c.Name == "FedAuth") && pages.Any(p => p.Host.Equals(site.Host, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation("sharepoint: read the sign-in to {Host} from {Browser} ({Count} cookies)", site.Host, BrowserName, cookies.Count);
                    return new Capture(cookies);
                }

                if (pages.FirstOrDefault(p => IsSessionProxy(p, site)) is { } proxied)
                {
                    return new Capture(null, $"{site.Host} opens through a session proxy ({proxied.Host}, Microsoft Defender for Cloud Apps), which the web session cannot go through; an app sign-in can", Final: true);
                }

                // a browser that keeps running in the background after its last window closes stays connected, so a
                // closed window shows as no pages rather than as a dropped connection (counted once a page has been
                // seen: the DevTools port can open before the first window does)
                sawPage |= pages.Count > 0;
                noPagesSince = headless || !sawPage || pages.Count > 0 ? null : noPagesSince ?? DateTime.UtcNow;
                if (DateTime.UtcNow - noPagesSince > TimeSpan.FromSeconds(3))
                {
                    return new Capture(null, WindowClosed(site), Final: true);
                }

                if (DateTime.UtcNow > until)
                {
                    _logger.LogInformation("sharepoint: no sign-in to {Host} after {Seconds} s {Mode}; pages open: {Pages}; cookies for the site: {Cookies}",
                        site.Host, deadline.TotalSeconds, mode, string.Join(", ", pages.Select(p => p.Host + p.AbsolutePath)), string.Join(", ", cookies.Select(c => c.Name)));
                    return headless
                        ? new Capture(null, "no saved sign-in")
                        : new Capture(null, $"nobody finished the sign-in to {site.Host} within {InteractiveDeadline.TotalMinutes:N0} minutes; Retry to sign in", Final: true);
                }

                await Task.Delay(TimeSpan.FromSeconds(headless ? 1 : 2), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException)
        {
            // the browser went away before a sign-in was read: a closed window, or a crash
            _logger.LogInformation("sharepoint: {Browser} ({Mode}) closed before the sign-in to {Host} was read: {Error}", BrowserName, mode, site.Host, ex.Message);
            return headless ? new Capture(null, $"{BrowserName} quit before the sign-in was read") : new Capture(null, WindowClosed(site), Final: true);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "sharepoint: {Browser} answered a DevTools command in an unexpected form", BrowserName);
            return new Capture(null, $"{BrowserName} answered AnyFS in a form it does not know ({ex.Message}); the web session cannot read the sign-in from this version", Final: true);
        }
        finally
        {
            await CloseAsync(browser, cdp).ConfigureAwait(false);
        }
    }

    private string WindowClosed(Uri site) => $"the {BrowserName} window was closed before the sign-in to {site.Host} finished; Retry to sign in";

    private string NoDevTools(bool handedOver) => handedOver
        ? $"{BrowserName} handed the sign-in to another {BrowserName} that is open on AnyFS's profile for this site and cannot be reached; close that window and Retry"
        : $"{BrowserName} opened but never opened its DevTools port, which the web session reads the sign-in through. On a managed machine that is usually your organisation's RemoteDebuggingAllowed policy (edge://policy lists the policies in force); there the web session cannot work, and an app sign-in (Sign in with) can.";

    /// <summary>A Microsoft Defender for Cloud Apps session proxy serves the site under its own suffix (contoso.sharepoint.com.mcas.ms).</summary>
    private static bool IsSessionProxy(Uri page, Uri site)
        => page.Host.StartsWith(site.Host + ".", StringComparison.OrdinalIgnoreCase) && page.Host.Contains(".mcas", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Closes a browser still open on the profile from an earlier sign-in (AnyFS stopped while its window was up, or an
    /// older AnyFS left it): a new launch would be handed to it and exit at once. A port file nothing answers on is stale.
    /// </summary>
    private async Task CloseLeftoverAsync(CancellationToken ct)
    {
        if (ReadPortFile() is { } endpoint && await AnswersAsync(endpoint, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("sharepoint: closing the {Browser} left open on the sign-in profile {Profile}", BrowserName, _profileDir);
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

    private Process LaunchBrowser(Uri site, bool headless)
    {
        var info = new ProcessStartInfo(_browserPath) { UseShellExecute = false };
        foreach (var arg in new[] { $"--user-data-dir={_profileDir}", "--remote-debugging-port=0", "--no-first-run", "--no-default-browser-check", "--new-window" })
        {
            info.ArgumentList.Add(arg);
        }

        if (headless)
        {
            info.ArgumentList.Add("--headless=new");
        }

        info.ArgumentList.Add(site.AbsoluteUri);

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

    private static async Task<IReadOnlyList<SessionCookie>> SiteCookiesAsync(Cdp cdp, Uri site, CancellationToken ct)
    {
        var result = await cdp.SendAsync("Storage.getCookies", ct: ct).ConfigureAwait(false);
        var cookies = new List<SessionCookie>();
        foreach (var c in result.GetProperty("cookies").EnumerateArray())
        {
            var domain = c.GetProperty("domain").GetString()!;
            if (!DomainMatches(domain, site.Host))
            {
                continue;
            }

            cookies.Add(new SessionCookie(
                c.GetProperty("name").GetString()!,
                c.GetProperty("value").GetString()!,
                domain,
                c.GetProperty("path").GetString()!,
                c.GetProperty("secure").GetBoolean(),
                c.GetProperty("httpOnly").GetBoolean()));
        }

        return cookies;
    }

    private static bool DomainMatches(string cookieDomain, string host)
    {
        var domain = cookieDomain.TrimStart('.');
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
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
