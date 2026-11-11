using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.BiDi;
using OpenQA.Selenium.BiDi.Network;
using OpenQA.Selenium.Firefox;

namespace AnyFS.Sources.Common;

/// <summary>
/// Firefox of AnyFS's own on a profile, driven through Selenium WebDriver with WebDriver BiDi on, for a web session that
/// reads what the page sends (Teams'): where a Chromium browser's DevTools cannot be reached — an Edge whose policy fixes
/// its profile folder or turns remote debugging off, as on a work machine — Firefox's WebDriver answers to none of those
/// policies. Each request the page sends is handed to a callback with its address and Authorization header. Firefox's
/// driver is the geckodriver AnyFS carries (<see cref="GeckoDriver"/>), as for <see cref="WebDriverSession"/>. Windows sign-in for
/// Microsoft accounts, the operating system's client certificates (smart cards) and enterprise root certificates are
/// allowed, as a work sign-in may need them. Hold <see cref="ProfileLocks"/> for the profile around a launch and its use.
/// </summary>
public sealed class FirefoxBiDiBrowser : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private readonly IWebDriver _driver;
    private readonly BiDi _bidi;
    private readonly DriverProcesses _processes;

    private FirefoxBiDiBrowser(IWebDriver driver, BiDi bidi, DriverProcesses processes, string driverFile)
    {
        _driver = driver;
        _bidi = bidi;
        _processes = processes;
        Driver = driverFile;
    }

    /// <summary>A launch: the browser, or why there is none (Firefox or its driver would not start).</summary>
    public sealed record Launch(FirefoxBiDiBrowser? Browser, string? Failure);

    /// <summary>The driver it runs through, for the log: the geckodriver AnyFS carries, or Selenium Manager's.</summary>
    public string Driver { get; }

    /// <summary>The page's address now; null when it cannot be read (the browser closed).</summary>
    public string? Page
    {
        get
        {
            try
            {
                return _driver.Url;
            }
            catch (Exception ex) when (ex is WebDriverException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>Whether a window is still open.</summary>
    public bool Open
    {
        get
        {
            try
            {
                return _driver.WindowHandles.Count > 0;
            }
            catch (Exception ex) when (ex is WebDriverException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Launches Firefox on the profile, headless or with a window, each request the page sends handed to
    /// <paramref name="onRequest"/> with its address and Authorization header (null when it has none).
    /// </summary>
    /// <param name="logPrefix">The source's log prefix (<c>teams</c>).</param>
    public static async Task<Launch> LaunchAsync(string firefoxPath, string profileDir, bool headless, Action<string, string?> onRequest, ILogger logger, string logPrefix, CancellationToken ct)
    {
        Directory.CreateDirectory(profileDir);
        var processes = new DriverProcesses();
        IWebDriver driver;
        string driverFile;
        try
        {
            (driver, driverFile) = await Task.Run(() => Start(firefoxPath, profileDir, headless, processes, logger, logPrefix), ct).ConfigureAwait(false);
        }
        catch (WebDriverException ex)
        {
            processes.Dispose();
            return new Launch(null, $"Firefox could not be started through WebDriver: {FirstLine(ex.Message)}");
        }
        catch
        {
            processes.Dispose();
            throw;
        }

        try
        {
            var bidi = await driver.AsBiDiAsync().ConfigureAwait(false);
            await bidi.Network.OnBeforeRequestSentAsync(e =>
            {
                var authorization = e.Request.Headers.FirstOrDefault(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))?.Value is StringBytesValue v ? v.Value : null;
                onRequest(e.Request.Url, authorization);
            }).ConfigureAwait(false);
            return new Launch(new FirefoxBiDiBrowser(driver, bidi, processes, driverFile), null);
        }
        catch (Exception ex)
        {
            await processes.QuitAsync(driver).ConfigureAwait(false);
            processes.Dispose();
            if (ex is WebDriverException or BiDiException or IOException or System.Net.WebSockets.WebSocketException or InvalidOperationException)
            {
                return new Launch(null, $"Firefox's WebDriver BiDi could not be reached: {FirstLine(ex.Message)}");
            }

            throw;
        }
    }

    /// <summary>Starts loading a page and returns at once (a sign-in page waits on the person).</summary>
    /// <exception cref="IOException">Firefox could not be asked to (it closed).</exception>
    public void Navigate(string url)
    {
        try
        {
            _driver.Navigate().GoToUrl(url);
        }
        catch (WebDriverException ex)
        {
            throw new IOException($"Firefox could not open the page: {FirstLine(ex.Message)}", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _bidi.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebDriverException or BiDiException or IOException or InvalidOperationException or OperationCanceledException or System.Net.WebSockets.WebSocketException)
        {
            // the browser may be gone already
        }
        finally
        {
            await _processes.QuitAsync(_driver).ConfigureAwait(false);
            _processes.Dispose();
        }
    }

    private static (IWebDriver Driver, string DriverFile) Start(string firefoxPath, string profileDir, bool headless, DriverProcesses processes, ILogger logger, string logPrefix)
    {
        SeleniumManagerLocator.Prepare();
        var service = GeckoDriver.Service(logger, logPrefix);
        processes.Watch(service);
        service.HideCommandPromptWindow = true;
        service.SuppressInitialDiagnosticInformation = true;
        service.LogLevel = FirefoxDriverLogLevel.Fatal;
        var options = new FirefoxOptions
        {
            BinaryLocation = firefoxPath,
            PageLoadStrategy = PageLoadStrategy.None,
            UnhandledPromptBehavior = headless ? UnhandledPromptBehavior.AcceptAndNotify : UnhandledPromptBehavior.Ignore,
            UseWebSocketUrl = true,
        };
        options.AddArguments("-profile", profileDir);
        if (headless)
        {
            options.AddArgument("-headless");
        }

        options.SetPreference("network.http.windows-sso.enabled", true);
        options.SetPreference("security.osclientcerts.autoload", true);
        options.SetPreference("security.enterprise_roots.enabled", true);
        return (new FirefoxDriver(service, options, CommandTimeout), GeckoDriver.FileOf(service));
    }

    private static string FirstLine(string message) => message.Split('\n', 2)[0].Trim();
}
