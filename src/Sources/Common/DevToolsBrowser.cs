using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Common;

/// <summary>
/// A Chromium browser (Edge, Chrome, Chromium) of AnyFS's own on a profile, driven over DevTools: launched headless or
/// with a window, its DevTools port read from the profile's <c>DevToolsActivePort</c> once it listens, and a
/// <see cref="DevToolsClient"/> connected to it. A browser still open on the profile from an earlier sign-in is closed
/// first (a new launch would be handed to it and exit at once). Disposing closes the browser through DevTools, or kills
/// the process the launch started when it cannot be reached. The web sessions share it (<see cref="BrowserSession"/>,
/// Outlook's and Teams'); what each reads from the browser is its own. Hold <see cref="ProfileLocks"/> for the profile
/// around a launch and its use.
/// </summary>
public sealed class DevToolsBrowser : IAsyncDisposable
{
    private static readonly TimeSpan PortDeadline = TimeSpan.FromSeconds(30);

    private readonly Process _process;

    private DevToolsBrowser(Process process, DevToolsClient client, int port)
    {
        _process = process;
        Client = client;
        Port = port;
    }

    /// <summary>The DevTools connection to the browser.</summary>
    public DevToolsClient Client { get; }

    /// <summary>The port its DevTools listens on, for the log.</summary>
    public int Port { get; }

    /// <summary>A launch: the browser, or why there is none (no DevTools port appeared).</summary>
    public sealed record Launch(DevToolsBrowser? Browser, string? Failure);

    /// <summary>
    /// Launches the browser on the profile and connects to its DevTools. No browser, with the reason, when no DevTools port
    /// appears in time: the launch was handed to another browser holding the profile, or the port is off by policy.
    /// </summary>
    /// <param name="startPage">The page the browser opens with (<c>about:blank</c> for a session that navigates itself).</param>
    /// <param name="extraArguments">More command-line switches (<c>--auth-server-allowlist=…</c>).</param>
    /// <param name="logPrefix">The source's log prefix (<c>sharepoint</c>, <c>outlook</c>).</param>
    public static async Task<Launch> LaunchAsync(string browserPath, string profileDir, bool headless, string startPage, IReadOnlyList<string> extraArguments, ILogger logger, string logPrefix, CancellationToken ct)
    {
        Directory.CreateDirectory(profileDir);
        await CloseLeftoverAsync(browserPath, profileDir, logger, logPrefix, ct).ConfigureAwait(false);
        var process = Start(browserPath, profileDir, headless, startPage, extraArguments);
        try
        {
            var endpoint = await EndpointAsync(process, profileDir, ct).ConfigureAwait(false);
            if (endpoint is null)
            {
                var handedOver = process.HasExited;
                Stop(process);
                return new Launch(null, NoDevTools(browserPath, handedOver));
            }

            var client = await DevToolsClient.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            return new Launch(new DevToolsBrowser(process, client, endpoint.Port), null);
        }
        catch
        {
            Stop(process);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Client.SendAsync("Browser.close", ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException or InvalidOperationException or ObjectDisposedException)
        {
            // it may close the connection before it answers
        }

        await Client.DisposeAsync().ConfigureAwait(false);
        Stop(_process);
    }

    private static string NoDevTools(string browserPath, bool handedOver)
    {
        var name = BrowserLocator.DisplayName(browserPath);
        return handedOver
            ? $"{name} handed the launch to another {name} that holds the profile and cannot be reached (a window left open, or a profile folder fixed by policy)"
            : $"{name} started without opening its DevTools port (your organisation's RemoteDebuggingAllowed policy does this; edge://policy lists the policies in force)";
    }

    private static Process Start(string browserPath, string profileDir, bool headless, string startPage, IReadOnlyList<string> extraArguments)
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

        foreach (var arg in extraArguments)
        {
            info.ArgumentList.Add(arg);
        }

        info.ArgumentList.Add(startPage);

        // the browser's own log lines would otherwise land in AnyFS's terminal (anyfs serve); drained, not kept
        var browser = Process.Start(info) ?? throw new SignInRequiredException($"{BrowserLocator.DisplayName(browserPath)} did not start", permanent: true);
        browser.OutputDataReceived += (_, _) => { };
        browser.ErrorDataReceived += (_, _) => { };
        browser.BeginOutputReadLine();
        browser.BeginErrorReadLine();
        return browser;
    }

    /// <summary>The process ended, waited for or killed, and released.</summary>
    private static void Stop(Process process)
    {
        try
        {
            if (!process.WaitForExit(8000))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        process.Dispose();
    }

    /// <summary>
    /// The DevTools address the browser writes to the profile once it listens: the port, then the browser endpoint's path.
    /// Null when none appears in time: the browser runs without one (remote debugging turned off), or it exited at once,
    /// having handed the launch to a browser already holding the profile.
    /// </summary>
    private static async Task<Uri?> EndpointAsync(Process browser, string profileDir, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < PortDeadline)
        {
            if (ReadPortFile(profileDir) is { } endpoint)
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

    private static string PortFile(string profileDir) => Path.Combine(profileDir, "DevToolsActivePort");

    private static Uri? ReadPortFile(string profileDir)
    {
        try
        {
            var lines = File.ReadAllLines(PortFile(profileDir));
            return lines.Length >= 2 && int.TryParse(lines[0], out var port) && port > 0 ? new Uri($"ws://127.0.0.1:{port}{lines[1]}") : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Closes a browser still open on the profile from an earlier sign-in (AnyFS stopped while its window was up, or an
    /// older AnyFS left it): a new launch would be handed to it and exit at once. A port file nothing answers on is stale.
    /// </summary>
    private static async Task CloseLeftoverAsync(string browserPath, string profileDir, ILogger logger, string logPrefix, CancellationToken ct)
    {
        if (ReadPortFile(profileDir) is { } endpoint && await AnswersAsync(endpoint, ct).ConfigureAwait(false))
        {
            logger.LogInformation("{Prefix}: closing the {Browser} left open on the sign-in profile {Profile}", logPrefix, BrowserLocator.DisplayName(browserPath), profileDir);
            try
            {
                using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                quick.CancelAfter(TimeSpan.FromSeconds(5));
                await using var client = await DevToolsClient.ConnectAsync(endpoint, quick.Token).ConfigureAwait(false);
                await client.SendAsync("Browser.close", ct: quick.Token).ConfigureAwait(false);
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
            File.Delete(PortFile(profileDir));
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
}

/// <summary>
/// One browser at a time on a profile, whichever web session asks. A second launch on a profile that is already open is
/// handed to the browser holding it and exits at once, so two sources on one profile starting together (an Outlook and a
/// Teams source of one account, say), or a renewal during a sign-in, would lose track of each other's browser.
/// </summary>
public static class ProfileLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim For(string profileDir) => Gates.GetOrAdd(Path.GetFullPath(profileDir), _ => new SemaphoreSlim(1, 1));
}
