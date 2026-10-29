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
/// driver (geckodriver) is found or fetched by Selenium Manager, as for <see cref="WebDriverSession"/>. Windows sign-in for
/// Microsoft accounts, the operating system's client certificates (smart cards) and enterprise root certificates are
/// allowed, as a work sign-in may need them. Hold <see cref="ProfileLocks"/> for the profile around a launch and its use.
/// </summary>
public sealed class FirefoxBiDiBrowser : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private readonly IWebDriver _driver;
    private readonly BiDi _bidi;

    private FirefoxBiDiBrowser(IWebDriver driver, BiDi bidi)
    {
        _driver = driver;
        _bidi = bidi;
    }

    /// <summary>A launch: the browser, or why there is none (Firefox or its driver would not start).</summary>
    public sealed record Launch(FirefoxBiDiBrowser? Browser, string? Failure);

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
    public static async Task<Launch> LaunchAsync(string firefoxPath, string profileDir, bool headless, Action<string, string?> onRequest, CancellationToken ct)
    {
        Directory.CreateDirectory(profileDir);
        IWebDriver driver;
        try
        {
            driver = await Task.Run(() => Start(firefoxPath, profileDir, headless), ct).ConfigureAwait(false);
        }
        catch (WebDriverException ex)
        {
            return new Launch(null, $"Firefox could not be started through WebDriver: {FirstLine(ex.Message)}");
        }

        try
        {
            var bidi = await driver.AsBiDiAsync().ConfigureAwait(false);
            await bidi.Network.OnBeforeRequestSentAsync(e =>
            {
                var authorization = e.Request.Headers.FirstOrDefault(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))?.Value is StringBytesValue v ? v.Value : null;
                onRequest(e.Request.Url, authorization);
            }).ConfigureAwait(false);
            return new Launch(new FirefoxBiDiBrowser(driver, bidi), null);
        }
        catch (Exception ex) when (ex is WebDriverException or IOException or System.Net.WebSockets.WebSocketException or InvalidOperationException)
        {
            Quit(driver);
            return new Launch(null, $"Firefox's WebDriver BiDi could not be reached: {FirstLine(ex.Message)}");
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
        catch (Exception ex) when (ex is WebDriverException or IOException or InvalidOperationException or OperationCanceledException or System.Net.WebSockets.WebSocketException)
        {
            // the browser may be gone already
        }

        Quit(_driver);
    }

    private static IWebDriver Start(string firefoxPath, string profileDir, bool headless)
    {
        SeleniumManagerLocator.Prepare();
        var service = FirefoxDriverService.CreateDefaultService();
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
        return new FirefoxDriver(service, options, CommandTimeout);
    }

    private static void Quit(IWebDriver driver)
    {
        try
        {
            driver.Quit();
        }
        catch (Exception ex) when (ex is WebDriverException or InvalidOperationException)
        {
            // already gone
        }

        driver.Dispose();
    }

    private static string FirstLine(string message) => message.Split('\n', 2)[0].Trim();
}
