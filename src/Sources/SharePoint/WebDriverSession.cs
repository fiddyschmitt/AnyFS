using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Firefox;
using SeleniumLogging = OpenQA.Selenium.Internal.Logging;

namespace AnyFS.Sources.SharePoint;

/// <summary>A browser the WebDriver fallback can use: its kind and where it is installed.</summary>
internal sealed record WebDriverBrowser(BrowserKind Kind, string Path)
{
    public string Name => BrowserLocator.DisplayName(Kind);

    /// <summary>The suffix of its profile folder beside the DevTools one: contoso.sharepoint.com-firefox.</summary>
    public string Key => Kind.ToString().ToLowerInvariant();
}

/// <summary>
/// The web session's sign-in through Selenium WebDriver, as Mount3DX signs in (spec 7.3.2), for a machine where AnyFS
/// cannot drive the browser's DevTools port itself: remote debugging turned off by policy, a profile folder fixed by
/// policy, or a browser that never opens the port. The person's default browser comes first, then the others installed,
/// Firefox among them, whose WebDriver no Edge or Chrome policy touches; a Chromium browser whose policy blocks remote
/// debugging is left out, since its WebDriver needs it too. Selenium Manager, carried in the single-file build, finds or
/// fetches the driver that matches each browser. A profile of AnyFS's own per site host and browser keeps the Microsoft
/// session, so a headless renewal works as it does over DevTools.
/// </summary>
internal sealed class WebDriverSession
{
    private static readonly TimeSpan SilentDeadline = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan InteractiveDeadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<WebDriverBrowser> _browsers;
    private readonly string _profileBase;
    private readonly ILogger _logger;
    private WebDriverBrowser? _working;

    static WebDriverSession()
    {
        // Selenium's own messages go to the source's log, not to the terminal
        var context = SeleniumLogging.Log.SetLevel(SeleniumLogging.LogEventLevel.Info);
        context.Handlers.Clear();
        context.Handlers.Add(LogBridge.Instance);
    }

    public WebDriverSession(IReadOnlyList<WebDriverBrowser> browsers, string profileBase, ILogger logger)
    {
        _browsers = browsers;
        _profileBase = profileBase;
        _logger = logger;
    }

    /// <summary>The browser in use, or the first to be tried, for messages.</summary>
    public string BrowserName => (_working ?? _browsers.FirstOrDefault())?.Name ?? "a browser";

    public bool HasBrowsers => _browsers.Count > 0;

    /// <summary>
    /// The browsers to try, in order: the one <c>ANYFS_SHAREPOINT_WEBDRIVER</c> names (edge, chrome, chromium, firefox), if it
    /// does; else the default browser, then Firefox, Chrome, Chromium and Edge, as installed and not blocked by policy.
    /// </summary>
    public static IReadOnlyList<WebDriverBrowser> Browsers(string? forced)
    {
        var installed = new Dictionary<BrowserKind, string>();
        foreach (var kind in Enum.GetValues<BrowserKind>())
        {
            if (BrowserLocator.FindKind(kind) is { } path)
            {
                installed[kind] = path;
            }
        }

        return Order(BrowserLocator.DefaultBrowser(), ForcedKind(forced), installed.Keys.ToList(), kind => BrowserLocator.DevToolsBlocked(kind) is not null)
            .Select(kind => new WebDriverBrowser(kind, installed[kind]))
            .ToList();
    }

    /// <summary>The order the browsers are tried in (see <see cref="Browsers"/>).</summary>
    internal static IReadOnlyList<BrowserKind> Order(BrowserKind? defaultBrowser, BrowserKind? forced, IReadOnlyCollection<BrowserKind> installed, Func<BrowserKind, bool> blocked)
    {
        if (forced is { } only)
        {
            return installed.Contains(only) ? [only] : [];
        }

        var order = new List<BrowserKind>();
        foreach (var kind in new BrowserKind?[] { defaultBrowser, BrowserKind.Firefox, BrowserKind.Chrome, BrowserKind.Chromium, BrowserKind.Edge })
        {
            if (kind is { } k && installed.Contains(k) && !blocked(k) && !order.Contains(k))
            {
                order.Add(k);
            }
        }

        return order;
    }

    /// <summary>The browser <c>ANYFS_SHAREPOINT_WEBDRIVER</c> names, or null for any (1, true, or nothing).</summary>
    internal static BrowserKind? ForcedKind(string? value) => Enum.TryParse<BrowserKind>(value?.Trim(), ignoreCase: true, out var kind) && !int.TryParse(value, out _) ? kind : null;

    /// <summary>
    /// The site's cookies, as <see cref="BrowserSession.SignInAsync"/> gets them over DevTools: headless first, then, only
    /// when <paramref name="interactive"/>, a window for the person; each browser in turn until one starts.
    /// </summary>
    public async Task<IReadOnlyList<SessionCookie>> SignInAsync(Uri site, bool interactive, Action<string> progress, CancellationToken ct)
    {
        LogBridge.Instance.Target = _logger;
        var failures = new List<string>();
        var browsers = _working is null ? _browsers : [_working, .. _browsers.Where(b => b != _working)];
        foreach (var browser in browsers)
        {
            var profile = $"{_profileBase}-{browser.Key}";
            var gate = ProfileLocks.For(profile);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var silent = await CaptureAsync(browser, profile, site, headless: true, SilentDeadline, ct).ConfigureAwait(false);
                if (silent.Unstartable)
                {
                    failures.Add($"{browser.Name}: {silent.Failure}");
                    continue;
                }

                if (silent.Cookies is { } cookies)
                {
                    _working = browser;
                    return cookies;
                }

                if (silent.Final)
                {
                    throw new SignInRequiredException(silent.Failure!, permanent: true);
                }

                if (!interactive)
                {
                    throw new SignInRequiredException($"the browser sign-in for {site.Host} has lapsed; sign in again (open the source, or its Retry)");
                }

                _logger.LogInformation("sharepoint: no sign-in to read for {Host} in {Browser} without a window ({Why}); opening it for the person to sign in", site.Host, browser.Name, silent.Failure);
                progress($"Sign in to {site.Host} in the {browser.Name} window that opened");
                var shown = await CaptureAsync(browser, profile, site, headless: false, InteractiveDeadline, ct).ConfigureAwait(false);
                if (shown.Unstartable)
                {
                    failures.Add($"{browser.Name}: {shown.Failure}");
                    continue;
                }

                _working = shown.Cookies is null ? _working : browser;
                return shown.Cookies ?? throw new SignInRequiredException(shown.Failure!, permanent: true);
            }
            finally
            {
                gate.Release();
            }
        }

        throw new SignInRequiredException(failures.Count == 0
            ? "WebDriver has no browser to sign in with: install Firefox, Chrome or Edge"
            : $"WebDriver could not start a browser to sign in with ({string.Join("; ", failures)})", permanent: true);
    }

    /// <summary>What one browser session came to: the site's cookies, or why not; whether trying again can help; whether the browser did not start at all.</summary>
    private sealed record Capture(IReadOnlyList<SessionCookie>? Cookies, string? Failure = null, bool Final = false, bool Unstartable = false);

    private async Task<Capture> CaptureAsync(WebDriverBrowser browser, string profile, Uri site, bool headless, TimeSpan deadline, CancellationToken ct)
    {
        var mode = headless ? "headless" : "with a window";
        Directory.CreateDirectory(profile);
        IWebDriver driver;
        try
        {
            driver = await Task.Run(() => Start(browser, profile, headless), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebDriverException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            _logger.LogInformation("sharepoint: WebDriver could not start {Browser} {Mode}: {Error}", browser.Name, mode, ex.Message);
            return new Capture(null, FirstLine(ex.Message), Unstartable: true);
        }

        try
        {
            _logger.LogInformation("sharepoint: WebDriver opened {Browser} {Mode} for {Host}", browser.Name, mode, site.Host);
            try
            {
                await Task.Run(() => driver.Navigate().GoToUrl(site), ct).ConfigureAwait(false);
            }
            catch (WebDriverTimeoutException)
            {
                // a slow page; the cookies are what matters
            }

            var until = DateTime.UtcNow + deadline;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var (page, cookies) = await Task.Run(() => Read(driver, site), ct).ConfigureAwait(false);
                if (page is not null && cookies.Any(c => c.Name == "FedAuth") && page.Host.Equals(site.Host, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("sharepoint: read the sign-in to {Host} from {Browser} through WebDriver ({Count} cookies)", site.Host, browser.Name, cookies.Count);
                    return new Capture(cookies);
                }

                if (page is not null && page.Host.StartsWith(site.Host + ".", StringComparison.OrdinalIgnoreCase) && page.Host.Contains(".mcas", StringComparison.OrdinalIgnoreCase))
                {
                    return new Capture(null, $"{site.Host} opens through a session proxy ({page.Host}, Microsoft Defender for Cloud Apps), which the web session cannot go through; an app sign-in can", Final: true);
                }

                if (DateTime.UtcNow > until)
                {
                    _logger.LogInformation("sharepoint: no sign-in to {Host} after {Seconds} s in {Browser} {Mode}; page: {Page}; cookies for the site: {Cookies}",
                        site.Host, deadline.TotalSeconds, browser.Name, mode, page is null ? "none" : page.Host + page.AbsolutePath, string.Join(", ", cookies.Select(c => c.Name)));
                    return headless
                        ? new Capture(null, "no saved sign-in")
                        : new Capture(null, $"nobody finished the sign-in to {site.Host} within {InteractiveDeadline.TotalMinutes:N0} minutes; Retry to sign in", Final: true);
                }

                await Task.Delay(TimeSpan.FromSeconds(headless ? 1 : 2), ct).ConfigureAwait(false);
            }
        }
        catch (WebDriverException ex)
        {
            // the window was closed, or the browser went away
            _logger.LogInformation("sharepoint: {Browser} ({Mode}) closed before the sign-in to {Host} was read: {Error}", browser.Name, mode, site.Host, FirstLine(ex.Message));
            return headless
                ? new Capture(null, $"{browser.Name} quit before the sign-in was read")
                : new Capture(null, $"the {browser.Name} window was closed before the sign-in to {site.Host} finished; Retry to sign in", Final: true);
        }
        finally
        {
            await Task.Run(() => Quit(driver)).ConfigureAwait(false);
        }
    }

    /// <summary>The page the browser shows and the cookies it holds for the site; follows the person to another window when they close the first.</summary>
    private static (Uri? Page, IReadOnlyList<SessionCookie> Cookies) Read(IWebDriver driver, Uri site)
    {
        string url;
        try
        {
            url = driver.Url;
        }
        catch (NoSuchWindowException)
        {
            var handles = driver.WindowHandles;
            if (handles.Count == 0)
            {
                throw;
            }

            driver.SwitchTo().Window(handles[^1]);
            url = driver.Url;
        }

        var cookies = driver.Manage().Cookies.AllCookies
            .Select(c => new SessionCookie(c.Name, c.Value, string.IsNullOrEmpty(c.Domain) ? site.Host : c.Domain, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Secure, c.IsHttpOnly))
            .Where(c => DomainMatches(c.Domain, site.Host))
            .ToList();
        return (Uri.TryCreate(url, UriKind.Absolute, out var page) ? page : null, cookies);
    }

    private static bool DomainMatches(string cookieDomain, string host)
    {
        var domain = cookieDomain.TrimStart('.');
        return host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    private static IWebDriver Start(WebDriverBrowser browser, string profile, bool headless)
    {
        SeleniumManagerLocator.Prepare();
        switch (browser.Kind)
        {
            case BrowserKind.Firefox:
            {
                var service = FirefoxDriverService.CreateDefaultService();
                service.HideCommandPromptWindow = true;
                service.SuppressInitialDiagnosticInformation = true;
                service.LogLevel = FirefoxDriverLogLevel.Fatal;
                var options = new FirefoxOptions { BinaryLocation = browser.Path, PageLoadStrategy = PageLoadStrategy.Eager };
                options.AddArguments("-profile", profile);
                if (headless)
                {
                    options.AddArgument("-headless");
                }

                return new FirefoxDriver(service, options, CommandTimeout);
            }

            case BrowserKind.Edge:
            {
                var service = EdgeDriverService.CreateDefaultService();
                Quiet(service);
                return new EdgeDriver(service, Chromium(new EdgeOptions(), browser, profile, headless), CommandTimeout);
            }

            default:
            {
                var service = ChromeDriverService.CreateDefaultService();
                Quiet(service);
                return new ChromeDriver(service, Chromium(new ChromeOptions(), browser, profile, headless), CommandTimeout);
            }
        }
    }

    private static void Quiet(ChromiumDriverService service)
    {
        // --silent alone: chromedriver and msedgedriver refuse it together with a --log-level
        service.HideCommandPromptWindow = true;
        service.SuppressInitialDiagnosticInformation = true;
    }

    private static T Chromium<T>(T options, WebDriverBrowser browser, string profile, bool headless)
        where T : ChromiumOptions
    {
        options.BinaryLocation = browser.Path;
        options.PageLoadStrategy = PageLoadStrategy.Eager;
        options.AddArguments($"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check");
        if (headless)
        {
            options.AddArgument("--headless=new");
        }

        return options;
    }

    private static void Quit(IWebDriver driver)
    {
        try
        {
            driver.Quit();
        }
        catch (Exception ex) when (ex is WebDriverException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private static string FirstLine(string message) => message.Split('\n', 2)[0].Trim();

    /// <summary>Selenium's log lines to the log of the source signing in: warnings and errors as information, the rest at debug.</summary>
    private sealed class LogBridge : SeleniumLogging.ILogHandler
    {
        public static readonly LogBridge Instance = new();

        public volatile ILogger Target = NullLogger.Instance;

        public void Handle(SeleniumLogging.LogEvent logEvent)
            => Target.Log(logEvent.Level >= SeleniumLogging.LogEventLevel.Warn ? Microsoft.Extensions.Logging.LogLevel.Information : Microsoft.Extensions.Logging.LogLevel.Debug, "sharepoint: selenium: {Message}", logEvent.Message);
    }
}

/// <summary>
/// Points Selenium at the Selenium Manager the build carries (spec 7.3.2). The single-file build extracts it with the
/// native libraries, where Selenium looks on Windows; on Linux and macOS the extracted copy has no execute bit and
/// Selenium may not look there, so it is made executable and named in <c>SE_MANAGER_PATH</c>.
/// </summary>
internal static class SeleniumManagerLocator
{
    private static readonly Lock Gate = new();
    private static bool _prepared;

    public static void Prepare()
    {
        lock (Gate)
        {
            if (_prepared || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SE_MANAGER_PATH")))
            {
                return;
            }

            _prepared = true;
            var name = OperatingSystem.IsWindows() ? "selenium-manager.exe" : "selenium-manager";
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            var places = new List<string> { Path.Combine(AppContext.BaseDirectory, name), Path.Combine(AppContext.BaseDirectory, "runtimes", os, "native", name) };
            if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string dirs)
            {
                places.AddRange(dirs.Split([';', Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries).Select(dir => Path.Combine(dir, name)));
            }

            if (places.FirstOrDefault(File.Exists) is not { } found)
            {
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    var mode = File.GetUnixFileMode(found);
                    File.SetUnixFileMode(found, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            Environment.SetEnvironmentVariable("SE_MANAGER_PATH", found);
        }
    }
}
