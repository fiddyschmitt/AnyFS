using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Chromium;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Firefox;
using SeleniumLogging = OpenQA.Selenium.Internal.Logging;

namespace AnyFS.Sources.Common;

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
/// fetches the driver that matches each browser. A profile of AnyFS's own per host and browser keeps the sign-in
/// session, so a headless renewal works as it does over DevTools. WebDriver reads only the cookies of the page the
/// browser shows, so once signed in it opens the target's other pages in turn for theirs (<see cref="WebSignInTarget.AlsoVisitAsync"/>).
/// </summary>
internal sealed class WebDriverSession
{
    private static readonly TimeSpan SilentDeadline = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan InteractiveDeadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan VisitDeadline = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<WebDriverBrowser> _browsers;
    private readonly WebSignInTarget _target;
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

    public WebDriverSession(IReadOnlyList<WebDriverBrowser> browsers, WebSignInTarget target, ILogger logger)
    {
        _browsers = browsers;
        _target = target;
        _profileBase = target.ProfileDir;
        _logger = logger;
    }

    /// <summary>The browser in use, or the first to be tried, for messages.</summary>
    public string BrowserName => (_working ?? _browsers.FirstOrDefault())?.Name ?? "a browser";

    public bool HasBrowsers => _browsers.Count > 0;

    private string Prefix => _target.LogPrefix;

    private Uri Site => _target.Page;

    /// <summary>
    /// The browsers to try, in order: the one the target's WebDriver variable names (edge, chrome, chromium, firefox), if it
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

    /// <summary>The browser a WebDriver variable names, or null for any (1, true, or nothing).</summary>
    internal static BrowserKind? ForcedKind(string? value) => Enum.TryParse<BrowserKind>(value?.Trim(), ignoreCase: true, out var kind) && !int.TryParse(value, out _) ? kind : null;

    /// <summary>
    /// The session's cookies, as <see cref="BrowserSession.SignInAsync"/> gets them over DevTools: headless first, then, only
    /// when <paramref name="interactive"/>, a window for the person; each browser in turn until one starts.
    /// </summary>
    public async Task<IReadOnlyList<SessionCookie>> SignInAsync(bool interactive, Action<string> progress, CancellationToken ct)
    {
        LogBridge.Instance.Use(_logger, Prefix);
        var failures = new List<string>();
        var browsers = _working is null ? _browsers : [_working, .. _browsers.Where(b => b != _working)];
        foreach (var browser in browsers)
        {
            var profile = $"{_profileBase}-{browser.Key}";
            var gate = ProfileLocks.For(profile);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var silent = await CaptureAsync(browser, profile, headless: true, SilentDeadline, ct).ConfigureAwait(false);
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
                    throw new SignInRequiredException($"the browser sign-in for {Site.Host} has lapsed; sign in again (open the source, or its Retry)");
                }

                _logger.LogInformation("{Prefix}: no sign-in to read for {Host} in {Browser} without a window ({Why}); opening it for the person to sign in", Prefix, Site.Host, browser.Name, silent.Failure);
                progress($"Sign in to {Site.Host} in the {browser.Name} window that opened");
                var shown = await CaptureAsync(browser, profile, headless: false, InteractiveDeadline, ct).ConfigureAwait(false);
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

    /// <summary>What one browser session came to: the session's cookies, or why not; whether trying again can help; whether the browser did not start at all.</summary>
    private sealed record Capture(IReadOnlyList<SessionCookie>? Cookies, string? Failure = null, bool Final = false, bool Unstartable = false);

    private async Task<Capture> CaptureAsync(WebDriverBrowser browser, string profile, bool headless, TimeSpan deadline, CancellationToken ct)
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
            _logger.LogInformation("{Prefix}: WebDriver could not start {Browser} {Mode}: {Error}", Prefix, browser.Name, mode, ex.Message);
            return new Capture(null, FirstLine(ex.Message), Unstartable: true);
        }

        try
        {
            _logger.LogInformation("{Prefix}: WebDriver opened {Browser} {Mode} for {Host}", Prefix, browser.Name, mode, Site.Host);
            await GoAsync(driver, Site, ct).ConfigureAwait(false);
            var until = DateTime.UtcNow + deadline;
            (Uri? Page, IReadOnlyList<SessionCookie> Cookies) seen = (null, []);
            string? hiccup = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = await TryReadAsync(driver, ct).ConfigureAwait(false);
                if (read.Error is { } error && error != hiccup)
                {
                    _logger.LogInformation("{Prefix}: {Browser} did not answer for a moment, its window still open ({Error}); asking again", Prefix, browser.Name, error);
                }

                hiccup = read.Error;
                if (read.Seen is { } now)
                {
                    seen = now;
                    var check = await _target.CheckAsync(now.Page is null ? [] : [now.Page], now.Cookies, ct).ConfigureAwait(false);
                    if (check.Done)
                    {
                        var all = await VisitTheRestAsync(driver, now.Cookies, ct).ConfigureAwait(false);
                        _logger.LogInformation("{Prefix}: read the sign-in to {Host} from {Browser} through WebDriver ({Count} cookies)", Prefix, Site.Host, browser.Name, all.Count);
                        return new Capture(all);
                    }

                    if (check.Failure is { } failure)
                    {
                        return new Capture(null, failure, Final: true);
                    }
                }

                if (DateTime.UtcNow > until)
                {
                    _logger.LogInformation("{Prefix}: no sign-in to {Host} after {Seconds} s in {Browser} {Mode}; page: {Page}; cookies for the session: {Cookies}",
                        Prefix, Site.Host, deadline.TotalSeconds, browser.Name, mode, seen.Page is null ? "none" : seen.Page.Host + seen.Page.AbsolutePath, string.Join(", ", seen.Cookies.Select(c => c.Name)));
                    return headless
                        ? new Capture(null, "no saved sign-in")
                        : new Capture(null, $"nobody finished the sign-in to {Site.Host} within {InteractiveDeadline.TotalMinutes:N0} minutes; Retry to sign in", Final: true);
                }

                await Task.Delay(TimeSpan.FromSeconds(headless ? 1 : 2), ct).ConfigureAwait(false);
            }
        }
        catch (WebDriverException ex)
        {
            // the browser has no window left: the person closed it, or it went away
            _logger.LogInformation("{Prefix}: {Browser} ({Mode}) closed before the sign-in to {Host} was read: {Error}", Prefix, browser.Name, mode, Site.Host, FirstLine(ex.Message));
            return headless
                ? new Capture(null, $"{browser.Name} quit before the sign-in was read")
                : new Capture(null, $"the {browser.Name} window was closed before the sign-in to {Site.Host} finished; Retry to sign in", Final: true);
        }
        finally
        {
            await Task.Run(() => Quit(driver)).ConfigureAwait(false);
        }
    }

    /// <summary>Opens <paramref name="page"/>; the browser's answer to the command does not matter (a slow page, a dialog the page shows, a page on its way), only what it then shows.</summary>
    private async Task GoAsync(IWebDriver driver, Uri page, CancellationToken ct)
    {
        try
        {
            await Task.Run(() => driver.Navigate().GoToUrl(page), ct).ConfigureAwait(false);
        }
        catch (WebDriverException ex)
        {
            _logger.LogDebug("{Prefix}: opening {Page}: {Error}", Prefix, page.GetLeftPart(UriPartial.Path), FirstLine(ex.Message));
        }
    }

    /// <summary>
    /// What <see cref="Read"/> reads, or, while the browser's window is open but does not answer (a dialog of the page's is
    /// up, which AnyFS leaves to the person; a page on its way between two sites), the error it gave. Throws only when the
    /// browser has no window left: closed by the person, or gone.
    /// </summary>
    private async Task<((Uri? Page, IReadOnlyList<SessionCookie> Cookies)? Seen, string? Error)> TryReadAsync(IWebDriver driver, CancellationToken ct)
    {
        try
        {
            return (await Task.Run(() => Read(driver), ct).ConfigureAwait(false), null);
        }
        catch (WebDriverException ex)
        {
            if (!await Task.Run(() => StillOpen(driver), ct).ConfigureAwait(false))
            {
                throw;
            }

            return (null, FirstLine(ex.Message));
        }
    }

    /// <summary>
    /// Whether the browser still has a window (asking for its windows is answered with a page's dialog up, too). A browser
    /// that does not answer in time is taken as open: a dialog of the system's can hold it, as the smartcard's PIN prompt
    /// does; one that is gone fails at once.
    /// </summary>
    private static bool StillOpen(IWebDriver driver)
    {
        try
        {
            return driver.WindowHandles.Count > 0;
        }
        catch (WebDriverException ex) when (ex is WebDriverTimeoutException || ex.InnerException is TaskCanceledException or TimeoutException)
        {
            return true;
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    /// <summary>
    /// Once signed in: the target's other pages (<see cref="WebSignInTarget.AlsoVisitAsync"/>), each opened until the browser
    /// shows it (its sign-in done) and its cookies read; all the cookies so far, each once. A page that does not show in
    /// time is passed over, with what it had set by then; a browser that goes away meanwhile ends the visits, the sign-in
    /// already read.
    /// </summary>
    private async Task<IReadOnlyList<SessionCookie>> VisitTheRestAsync(IWebDriver driver, IReadOnlyList<SessionCookie> cookies, CancellationToken ct)
    {
        var all = new Dictionary<(string, string, string), SessionCookie>();
        void Add(IEnumerable<SessionCookie> more)
        {
            foreach (var c in more)
            {
                all[(c.Name, c.Domain.TrimStart('.').ToLowerInvariant(), c.Path)] = c;
            }
        }

        Add(cookies);
        try
        {
            foreach (var page in await _target.AlsoVisitAsync(cookies, ct).ConfigureAwait(false))
            {
                await GoAsync(driver, page, ct).ConfigureAwait(false);
                var until = DateTime.UtcNow + VisitDeadline;
                Uri? shown = null;
                while (true)
                {
                    if ((await TryReadAsync(driver, ct).ConfigureAwait(false)).Seen is { } now)
                    {
                        shown = now.Page;
                        Add(now.Cookies);
                        if (shown is not null && shown.Host.Equals(page.Host, StringComparison.OrdinalIgnoreCase) && shown.AbsolutePath.StartsWith(page.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }

                    if (DateTime.UtcNow > until)
                    {
                        _logger.LogInformation("{Prefix}: {Page} did not show within {Seconds} s after the sign-in (the browser shows {Shown}); going on without it", Prefix, page.GetLeftPart(UriPartial.Path), VisitDeadline.TotalSeconds, shown is null ? "nothing" : shown.Host + shown.AbsolutePath);
                        break;
                    }

                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
        }
        catch (WebDriverException ex)
        {
            _logger.LogInformation("{Prefix}: the browser closed after the sign-in was read, before its other pages showed ({Error}); going on with the sign-in", Prefix, FirstLine(ex.Message));
        }

        return all.Values.ToList();
    }

    /// <summary>The page the browser shows and the cookies it holds for it that the target keeps; follows the person to another window when they close the first.</summary>
    private (Uri? Page, IReadOnlyList<SessionCookie> Cookies) Read(IWebDriver driver)
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

        var page = Uri.TryCreate(url, UriKind.Absolute, out var shown) ? shown : null;
        var cookies = driver.Manage().Cookies.AllCookies
            .Select(c => new SessionCookie(c.Name, c.Value, string.IsNullOrEmpty(c.Domain) ? (page ?? Site).Host : c.Domain, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Secure, c.IsHttpOnly))
            .Where(_target.Keeps)
            .ToList();
        return (page, cookies);
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
                var options = new FirefoxOptions { BinaryLocation = browser.Path, PageLoadStrategy = PageLoadStrategy.Eager, UnhandledPromptBehavior = PromptBehavior(headless) };
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

    /// <summary>
    /// What WebDriver does with a dialog a page shows (an alert, a confirmation) when AnyFS next asks the browser something:
    /// with a window, nothing, so the person answers it (AnyFS's questions fail meanwhile, and are asked again); headless,
    /// where nobody can, it is accepted, as a person going on with the sign-in would.
    /// </summary>
    private static UnhandledPromptBehavior PromptBehavior(bool headless) => headless ? UnhandledPromptBehavior.AcceptAndNotify : UnhandledPromptBehavior.Ignore;

    private static T Chromium<T>(T options, WebDriverBrowser browser, string profile, bool headless)
        where T : ChromiumOptions
    {
        options.BinaryLocation = browser.Path;
        options.PageLoadStrategy = PageLoadStrategy.Eager;
        options.UnhandledPromptBehavior = PromptBehavior(headless);
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

        private volatile Sink _sink = new(NullLogger.Instance, "web session");

        /// <summary>Where Selenium's lines go from now on: the log of the source signing in, with its prefix.</summary>
        public void Use(ILogger logger, string prefix) => _sink = new Sink(logger, prefix);

        public void Handle(SeleniumLogging.LogEvent logEvent)
        {
            var sink = _sink;
            sink.Logger.Log(logEvent.Level >= SeleniumLogging.LogEventLevel.Warn ? Microsoft.Extensions.Logging.LogLevel.Information : Microsoft.Extensions.Logging.LogLevel.Debug, "{Prefix}: selenium: {Message}", sink.Prefix, logEvent.Message);
        }

        private sealed record Sink(ILogger Logger, string Prefix);
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
