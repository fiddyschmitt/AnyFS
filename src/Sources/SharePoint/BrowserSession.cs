using System.Collections.Concurrent;
using System.Diagnostics;
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

    /// <summary>
    /// The site's cookies: first a headless launch, which succeeds without a window while the profile's Microsoft
    /// session is still good (a restart, or a silent renewal); failing that, and only when <paramref name="interactive"/>,
    /// a window for the person to sign in. Throws <see cref="SignInRequiredException"/> when no sign-in can be had.
    /// </summary>
    public async Task<IReadOnlyList<SessionCookie>> SignInAsync(Uri site, bool interactive, Action<string> progress, CancellationToken ct)
    {
        if (await CaptureAsync(site, headless: true, SilentDeadline, progress, ct).ConfigureAwait(false) is { } silent)
        {
            _logger.LogDebug("sharepoint: browser session for {Host} renewed silently", site.Host);
            return silent;
        }

        if (!interactive)
        {
            throw new SignInRequiredException($"the browser sign-in for {site.Host} has lapsed; sign in again (open the source, or its Retry)", permanent: false);
        }

        progress($"Sign in to {site.Host} in the {BrowserName} window that opened");
        _logger.LogInformation("sharepoint: opened {Browser} for a sign-in to {Host}", BrowserName, site.Host);
        return await CaptureAsync(site, headless: false, InteractiveDeadline, progress, ct).ConfigureAwait(false)
            ?? throw new SignInRequiredException($"the sign-in to {site.Host} was not finished: the window was closed, or {InteractiveDeadline.TotalMinutes:N0} minutes passed");
    }

    /// <summary>
    /// Opens <paramref name="site"/> in the browser and waits for its FedAuth cookie, meaning the page is signed in;
    /// returns the site's cookies, or null if none appeared before <paramref name="deadline"/>. The browser is
    /// always closed before returning.
    /// </summary>
    private async Task<IReadOnlyList<SessionCookie>?> CaptureAsync(Uri site, bool headless, TimeSpan deadline, Action<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_profileDir);
        using var browser = LaunchBrowser(site, headless);
        try
        {
            await using var cdp = await Cdp.ConnectAsync(await DevToolsEndpointAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            var until = DateTime.UtcNow + deadline;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var cookies = await SiteCookiesAsync(cdp, site, ct).ConfigureAwait(false);
                if (cookies.Any(c => c.Name == "FedAuth") && await PageOnSiteAsync(cdp, site, ct).ConfigureAwait(false))
                {
                    return cookies;
                }

                if (DateTime.UtcNow > until)
                {
                    return null;
                }

                await Task.Delay(TimeSpan.FromSeconds(headless ? 1 : 2), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException)
        {
            // the browser went away before a sign-in completed: a closed window, or a crash
            _logger.LogDebug(ex, "sharepoint: the browser closed before a session for {Host} was captured", site.Host);
            return null;
        }
        finally
        {
            await CloseAsync(browser).ConfigureAwait(false);
        }
    }

    private Process LaunchBrowser(Uri site, bool headless)
    {
        var portFile = Path.Combine(_profileDir, "DevToolsActivePort");
        File.Delete(portFile);
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

    /// <summary>With <c>--remote-debugging-port=0</c> the browser writes the chosen port and the browser endpoint path to DevToolsActivePort in the profile.</summary>
    private async Task<Uri> DevToolsEndpointAsync(CancellationToken ct)
    {
        var portFile = Path.Combine(_profileDir, "DevToolsActivePort");
        for (var i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(portFile))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(portFile, ct).ConfigureAwait(false);
                    if (lines.Length >= 2 && int.TryParse(lines[0], out var port))
                    {
                        return new Uri($"ws://127.0.0.1:{port}{lines[1]}");
                    }
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        throw new TimeoutException($"{BrowserName} did not open its DevTools port within 30 s");
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

    private static async Task<bool> PageOnSiteAsync(Cdp cdp, Uri site, CancellationToken ct)
    {
        var targets = await cdp.SendAsync("Target.getTargets", ct: ct).ConfigureAwait(false);
        return targets.GetProperty("targetInfos").EnumerateArray()
            .Where(t => t.GetProperty("type").GetString() == "page")
            .Any(t => Uri.TryCreate(t.GetProperty("url").GetString(), UriKind.Absolute, out var u) && u.Host.Equals(site.Host, StringComparison.OrdinalIgnoreCase));
    }

    private async Task CloseAsync(Process browser)
    {
        try
        {
            await using var cdp = await Cdp.ConnectAsync(await DevToolsEndpointAsync(CancellationToken.None).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
            await cdp.SendAsync("Browser.close", ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
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
        await cdp._socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        cdp._receive = cdp.ReceiveAsync();
        return cdp;
    }

    public async Task<JsonElement> SendAsync(string method, JsonObject? parameters = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
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
