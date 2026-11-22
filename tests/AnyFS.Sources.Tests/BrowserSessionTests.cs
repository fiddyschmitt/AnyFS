using System.Diagnostics;
using AnyFS.Sources.Common;
using AnyFS.Sources.Outlook;
using AnyFS.Sources.SharePoint;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Tests;

/// <summary>Which browser the web session signs in through, and the policies that keep AnyFS from its DevTools port (spec 7.3.2).</summary>
[TestClass]
public class BrowserChoiceTests
{
    [TestMethod]
    public void APolicyFileThatBlocksAnyFSIsFound()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anyfs-tests", "policies-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.IsNull(BrowserLocator.PolicyFilesBlock(dir), "no policy folder: nothing set");

            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a-broken.json"), "{ not json");
            File.WriteAllText(Path.Combine(dir, "b-allowed.json"), """{ "RemoteDebuggingAllowed": true, "UserDataDir": "", "HomepageLocation": "https://intranet" }""");
            File.WriteAllText(Path.Combine(dir, "c-list.json"), "[]");
            Assert.IsNull(BrowserLocator.PolicyFilesBlock(dir), "allowed, unreadable or not a policy object");

            var profiles = Path.Combine(dir, "d-roaming.json");
            File.WriteAllText(profiles, """{ "UserDataDir": "/home/${user_name}/roaming/edge" }""");
            StringAssert.Contains(BrowserLocator.PolicyFilesBlock(dir), "profile folder is fixed");

            File.Delete(profiles);
            var off = Path.Combine(dir, "e-locked-down.json");
            File.WriteAllText(off, """{ "RemoteDebuggingAllowed": false }""");
            var reason = BrowserLocator.PolicyFilesBlock(dir)!;
            StringAssert.Contains(reason, "remote debugging is turned off");
            StringAssert.Contains(reason, off);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    [DataRow("ChromeHTML", "Chrome")]
    [DataRow("MSEdgeHTM", "Edge")]
    [DataRow("FirefoxURL-308046B0AF4A39CB", "Firefox")]
    [DataRow("IE.HTTP", null)]
    [DataRow(null, null)]
    public void TheDefaultBrowserComesFromItsProgId(string? progId, string? kind) => Assert.AreEqual(kind, BrowserLocator.KindOfProgId(progId)?.ToString());

    [TestMethod]
    public void WebDriverTriesTheDefaultBrowserFirstAndSkipsOnesThePolicyBlocks()
    {
        BrowserKind[] all = [BrowserKind.Edge, BrowserKind.Chrome, BrowserKind.Firefox];
        CollectionAssert.AreEqual(new[] { BrowserKind.Chrome, BrowserKind.Firefox, BrowserKind.Edge }, WebDriverSession.Order(BrowserKind.Chrome, null, all, _ => false).ToList(), "the default browser, as Mount3DX picks it, then the rest");
        CollectionAssert.AreEqual(new[] { BrowserKind.Firefox, BrowserKind.Chrome }, WebDriverSession.Order(BrowserKind.Edge, null, all, k => k == BrowserKind.Edge).ToList(), "Edge's WebDriver needs the remote debugging its policy turns off");
        CollectionAssert.AreEqual(new[] { BrowserKind.Firefox, BrowserKind.Chromium }, WebDriverSession.Order(null, null, [BrowserKind.Chromium, BrowserKind.Firefox], _ => false).ToList(), "no default browser off Windows");
        CollectionAssert.AreEqual(new[] { BrowserKind.Firefox }, WebDriverSession.Order(BrowserKind.Chrome, BrowserKind.Firefox, all, _ => false).ToList(), "ANYFS_SHAREPOINT_WEBDRIVER names one");
        Assert.AreEqual(0, WebDriverSession.Order(null, BrowserKind.Chromium, all, _ => false).Count, "one that is not installed: none");
    }

    [TestMethod]
    [DataRow("edge", "Edge")]
    [DataRow(" FIREFOX ", "Firefox")]
    [DataRow("1", null)]
    [DataRow("true", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    public void TheForcedBrowserIsReadFromItsName(string? value, string? kind) => Assert.AreEqual(kind, WebDriverSession.ForcedKind(value)?.ToString());

    private sealed class NoTarget(string profile, string? variable = null, bool needsAPerson = false, Func<CancellationToken, ValueTask>? opening = null, string? unreachable = null) : WebSignInTarget(new Uri("https://example.test/"), profile)
    {
        public override string LogPrefix => "test";

        public override string? WebDriverVariable => variable;

        public override bool NeedsAPerson => needsAPerson;

        // example.test answers nobody: the tests say whether it is taken to
        public override Task<string?> UnreachableAsync(CancellationToken ct) => Task.FromResult(unreachable);

        public override ValueTask OpeningWindowAsync(CancellationToken ct) => opening?.Invoke(ct) ?? ValueTask.CompletedTask;

        public override ValueTask<SignInCheck> CheckAsync(IReadOnlyList<Uri> pages, IReadOnlyList<SessionCookie> cookies, CancellationToken ct) => ValueTask.FromResult(SignInCheck.NotYet);
    }

    [TestMethod]
    public async Task ASiteThatDoesNotAnswerHasNoBrowserLaunchedForItAndTheSignInMayBeTriedAgain()
    {
        // until 22-Nov-2026 the window opened on an error page (the computer awake before its network), and its closing, or
        // its ten minutes, counted as the person's refusal: the source then waited for a Retry by hand
        var browser = Path.Combine(Path.GetTempPath(), "anyfs-tests", "no such folder", "a-browser.exe");
        foreach (var needsAPerson in new[] { true, false })
        {
            var opened = 0;
            var told = new List<string>();
            var target = new NoTarget("profile", needsAPerson: needsAPerson, opening: _ =>
            {
                opened++;
                return ValueTask.CompletedTask;
            }, unreachable: "No such host is known.");
            var ex = await Assert.ThrowsExactlyAsync<SignInRequiredException>(() => new BrowserSession(browser, target, NullLogger.Instance).SignInAsync(interactive: true, told.Add, CancellationToken.None));
            Assert.AreEqual("example.test did not answer (No such host is known.), so its sign-in was not tried", ex.Message);
            Assert.IsFalse(ex.Permanent, "a failure that may pass: the host starts the source again by itself");
            Assert.AreEqual((0, 0), (opened, told.Count), "no window was about to open, and the person was told of none");
        }
    }

    private sealed class Answering(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add($"{request.Method} {request.RequestUri}");
            return Task.FromResult(answer());
        }
    }

    [TestMethod]
    public async Task ASiteAnswersWhateverItSaysAndDoesNotWhereNothingReachesIt()
    {
        var site = new Uri("https://server.example.test/some/page?with=a-query");
        var refusing = new Answering(() => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
        Assert.IsNull(await SiteProbe.UnreachableAsync(site, CancellationToken.None, refusing), "a refusal is an answer");
        CollectionAssert.AreEqual(new[] { "HEAD https://server.example.test/" }, refusing.Asked, "one plain question of the site's root, nothing of the page's address sent");
        Assert.IsNull(await SiteProbe.UnreachableAsync(site, CancellationToken.None, new Answering(() => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway))), "so is an error page");

        static Answering Failing(HttpRequestError error, string message) => new(() => throw new HttpRequestException(error, message));
        Assert.AreEqual("No such host is known.", await SiteProbe.UnreachableAsync(site, CancellationToken.None, Failing(HttpRequestError.NameResolutionError, "No such host is known.")));
        Assert.AreEqual("No connection could be made.", await SiteProbe.UnreachableAsync(site, CancellationToken.None, Failing(HttpRequestError.ConnectionError, "No connection could be made.")));
        const string noRoute = "The proxy tunnel request to proxy 'http://proxy.example.test:3128/' failed with status code '502'.";
        Assert.AreEqual(noRoute, await SiteProbe.UnreachableAsync(site, CancellationToken.None, Failing(HttpRequestError.ProxyTunnelError, noRoute)), "the proxy could not reach it");

        // the site is there, though a plain request gets nowhere with it: the browser may
        Assert.IsNull(await SiteProbe.UnreachableAsync(site, CancellationToken.None, Failing(HttpRequestError.ProxyTunnelError, "The proxy tunnel request to proxy 'http://proxy.example.test:3128/' failed with status code '407'.")), "a proxy that wants a sign-in the browser has");
        Assert.IsNull(await SiteProbe.UnreachableAsync(site, CancellationToken.None, Failing(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.")), "a site that wants a certificate the browser has");
        StringAssert.StartsWith(await SiteProbe.UnreachableAsync(site, CancellationToken.None, new Answering(() => throw new TaskCanceledException())), "no answer within");
    }

    [TestMethod]
    public void ASignInFoundToNeedThePersonIsNotTriedAgainByEachRequestThatFollows()
    {
        var time = new AnyFS.Sdk.Testing.FakeTime();
        var lapse = new SignInLapse(time: time);
        lapse.ThrowIfRemembered();
        var said = lapse.Remember(new SignInRequiredException("the sign-in to teams.example.test has lapsed; sign in again", permanent: false));
        for (var i = 0; i < 3; i++)
        {
            var again = Assert.ThrowsExactly<SignInRequiredException>(lapse.ThrowIfRemembered);
            Assert.AreEqual((said.Message, said.Permanent), (again.Message, again.Permanent), "told as it was found, with no browser launched to find it again");
            Assert.AreNotSame(said, again);
        }

        time.Advance(TimeSpan.FromMinutes(2));
        lapse.ThrowIfRemembered();
        lapse.Remember(new SignInRequiredException("no page", permanent: true));
        Assert.IsTrue(Assert.ThrowsExactly<SignInRequiredException>(lapse.ThrowIfRemembered).Permanent);
        lapse.Forget();
        lapse.ThrowIfRemembered();
    }

    [TestMethod]
    public async Task ASignInThatStartsWithARequestOfItsOwnMakesItWithTheProfilesTurnAsItsWindowOpens()
    {
        // a browser that is not there, and a request the server refuses, so that no window opens either way
        var browser = Path.Combine(Path.GetTempPath(), "anyfs-tests", "no such folder", "a-browser.exe");
        var profile = Path.Combine(Path.GetTempPath(), "anyfs-tests", "opening-" + Guid.NewGuid().ToString("N"));
        var told = new List<string>();
        var turnTaken = new List<bool>();
        ValueTask Refused(string turnOf)
        {
            turnTaken.Add(ProfileLocks.For(turnOf).CurrentCount == 0);
            return ValueTask.FromException(new SignInRequiredException("the server would not start a sign-in", permanent: true));
        }

        var devTools = new BrowserSession(browser, new NoTarget(profile, needsAPerson: true, opening: _ => Refused(profile)), NullLogger.Instance);
        var ex = await Assert.ThrowsExactlyAsync<SignInRequiredException>(() => devTools.SignInAsync(interactive: true, told.Add, CancellationToken.None));
        Assert.AreEqual("the server would not start a sign-in", ex.Message);

        // through WebDriver, whose profile is its own beside it
        var webDriver = new WebDriverSession([new WebDriverBrowser(BrowserKind.Firefox, browser)], new NoTarget(profile, needsAPerson: true, opening: _ => Refused(profile + "-firefox")), NullLogger.Instance);
        ex = await Assert.ThrowsExactlyAsync<SignInRequiredException>(() => webDriver.SignInAsync(interactive: true, told.Add, CancellationToken.None));
        Assert.AreEqual("the server would not start a sign-in", ex.Message);

        CollectionAssert.AreEqual(new[] { true, true }, turnTaken, "made with the profile's turn taken: no other window of the profile is open while the server's time for it runs");
        Assert.AreEqual(0, told.Count, "made before the person is told what to do, which may depend on it");
        Assert.AreEqual(1, ProfileLocks.For(profile).CurrentCount, "the turn given back");
        Assert.AreEqual(1, ProfileLocks.For(profile + "-firefox").CurrentCount);
    }

    [TestMethod]
    public async Task ASignInThatAsksThePersonEachTimeLaunchesNothingWhereNoWindowMayBeShown()
    {
        // a browser that is not there: a launch would fail otherwise than this does
        var chrome = Path.Combine(Path.GetTempPath(), "anyfs-tests", "no such folder", "chrome.exe");
        var session = new BrowserSession(chrome, new NoTarget("profile", needsAPerson: true), NullLogger.Instance);
        var ex = await Assert.ThrowsExactlyAsync<SignInRequiredException>(() => session.SignInAsync(interactive: false, _ => { }, CancellationToken.None));
        Assert.IsFalse(ex.Permanent, "the person can sign in again with the source's Retry");
        StringAssert.Contains(ex.Message, "the browser sign-in for example.test has lapsed");
        Assert.AreEqual("Sign in to example.test in the Edge window that opened", new NoTarget("profile").Prompt("Edge"));
    }

    [TestMethod]
    public void ABrowserSettingNamingFirefoxSignsInThroughWebDriverOnThatFirefox()
    {
        var firefox = Path.Combine(Path.GetTempPath(), "somewhere of its own", "firefox.exe");
        var session = new BrowserSession(firefox, new NoTarget("profile"), NullLogger.Instance);
        Assert.AreEqual("the Browser setting names Firefox", session.WebDriverFromTheStart());
        var only = session.WebDriverBrowsers.Single();
        Assert.AreEqual(BrowserKind.Firefox, only.Kind);
        Assert.AreEqual(firefox, only.Path);
        Assert.IsTrue(session.CanSignIn);
        Assert.AreEqual("Firefox", session.BrowserName);
    }

    [TestMethod]
    public async Task ABrowserTheSettingNamesThatIsNotThereIsSaidSoAndNoOtherOpens()
    {
        var missing = Path.Combine(Path.GetTempPath(), "anyfs-tests", "no such folder", "chrome.exe");
        var session = new BrowserSession(BrowserLocator.Find(missing), new NoTarget("profile"), NullLogger.Instance, named: " " + missing + " ");
        Assert.IsTrue(session.CanSignIn, "the sign-in is tried, so it can say why not");
        var ex = await Assert.ThrowsExactlyAsync<SignInRequiredException>(() => session.SignInAsync(interactive: true, _ => { }, CancellationToken.None));
        Assert.IsTrue(ex.Permanent, "a Retry would only fail again: the setting must change");
        StringAssert.Contains(ex.Message, $"There is no browser at {missing}, the path in the source's Browser setting");
    }

    [TestMethod]
    public async Task ABrowserTheSettingNamesThatIsNoProgramIsSaidSoAndNotTriedAgain()
    {
        // a file that is there, and that the system will not start: its refusal was no sign-in failure the host knew, so it
        // started the source again for ever, with the system's own words for why
        var dir = Path.Combine(Path.GetTempPath(), "anyfs-tests", "no-program-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var notes = Path.Combine(dir, "notes.txt");
        File.WriteAllText(notes, "not a program");
        try
        {
            foreach (var needsAPerson in new[] { false, true })
            {
                var session = new BrowserSession(BrowserLocator.Find(notes), new NoTarget(Path.Combine(dir, "profile"), needsAPerson: needsAPerson), NullLogger.Instance, named: notes);
                var ex = await Assert.ThrowsExactlyAsync<SignInRequiredException>(() => session.SignInAsync(interactive: true, _ => { }, CancellationToken.None));
                Assert.IsTrue(ex.Permanent, "a Retry would only fail again: the setting must change");
                StringAssert.Contains(ex.Message, $"{notes} is not a program that can be started");
                StringAssert.Contains(ex.Message, "the source's Browser setting");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void TheTroubleshootingVariableStillWinsOverTheBrowserSetting()
    {
        const string variable = "ANYFS_TEST_BROWSER_CHOICE_WEBDRIVER";
        Environment.SetEnvironmentVariable(variable, "edge");
        try
        {
            var session = new BrowserSession(Path.Combine(Path.GetTempPath(), "firefox.exe"), new NoTarget("profile", variable), NullLogger.Instance);
            Assert.AreEqual($"{variable} is set", session.WebDriverFromTheStart());
            Assert.IsFalse(session.WebDriverBrowsers.Any(b => b.Kind == BrowserKind.Firefox));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    public async Task TheDevToolsLauncherNeverStartsFirefox()
    {
        // started as Chromium is, it would run with no window on the person's own profile, and their next Firefox would open nothing;
        // this one is no program at all, so starting it would fail another way
        var dir = Path.Combine(Path.GetTempPath(), "anyfs-tests", "firefox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var firefox = Path.Combine(dir, "firefox.exe");
        File.WriteAllText(firefox, "not a program");
        try
        {
            var profile = Path.Combine(dir, "profile");
            var launch = await DevToolsBrowser.LaunchAsync(firefox, profile, headless: true, "about:blank", [], NullLogger.Instance, "test", CancellationToken.None);
            Assert.IsNull(launch.Browser);
            StringAssert.Contains(launch.Failure, "which Firefox does not have");
            Assert.IsFalse(Directory.Exists(profile), "nothing was done for it");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void OutlooksWebSessionSaysFirefoxWillNotDo()
    {
        var firefox = Path.Combine(Path.GetTempPath(), "firefox.exe");
        var named = new OutlookPlugin().Validate(new OutlookSettings { Path = "https://outlook.office.com", SignInMethod = OutlookSettings.WebSession, BrowserPath = firefox }).ToList();
        Assert.IsTrue(named.Any(m => !m.IsError && m.Property == nameof(OutlookSettings.BrowserPath) && m.Message.Contains("which Firefox does not have", StringComparison.Ordinal)), string.Join("; ", named.Select(m => m.Message)));
        var edge = new OutlookPlugin().Validate(new OutlookSettings { Path = "https://outlook.office.com", SignInMethod = OutlookSettings.WebSession, BrowserPath = Path.Combine(Path.GetTempPath(), "msedge.exe") }).ToList();
        Assert.IsFalse(edge.Any(m => m.Message.Contains("which Firefox does not have", StringComparison.Ordinal)));
    }
}

/// <summary>
/// The web session's browser against the real one, with the signed-in profile the REST live suite uses
/// (ANYFS_TEST_SPS_SIGNIN=web, DEVELOPING.md): a browser left open on the profile, as by an AnyFS stopped while its
/// sign-in window was up, is closed first when it can be reached, and the sign-in goes through WebDriver when it cannot.
/// </summary>
[TestClass]
public class BrowserSessionLiveTests
{
    private static (string Browser, Uri Site) Require()
    {
        var target = SpsTestServer.OnlineRest;
        target.Require();
        if (target.IsFake)
        {
            Assert.Inconclusive("the browser's live tests sign in to SharePoint Online itself, with the web session: ANYFS_TEST_SPS_SITE and ANYFS_TEST_SPS_SIGNIN=web");
        }

        var browser = BrowserLocator.Find(null, SharePointPlugin.BrowserVariable) ?? throw new AssertInconclusiveException("no Edge, Chrome or Chromium on this machine");
        SiteAddress.TryParse(target.Site, out var site, out _);
        return (browser, new Uri(site.Url));
    }

    /// <summary>A headless browser holding <paramref name="profile"/>; its output is drained so it can never hold the test run's.</summary>
    private static Process Leftover(string browser, string profile, bool debuggable)
    {
        var info = new ProcessStartInfo(browser) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { $"--user-data-dir={profile}", "--headless=new", "--no-first-run", "about:blank" })
        {
            info.ArgumentList.Add(arg);
        }

        if (debuggable)
        {
            info.ArgumentList.Add("--remote-debugging-port=0");
        }

        var process = Process.Start(info)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void Stop(Process leftover)
    {
        if (!leftover.HasExited)
        {
            leftover.Kill(entireProcessTree: true);
        }

        leftover.WaitForExit(10000);
    }

    [TestMethod]
    public async Task ABrowserLeftOpenOnTheProfileIsClosedAndTheSignInReadWithoutAWindow()
    {
        var (browser, site) = Require();
        var profile = SharePointPlugin.ProfileDir(site.Host);
        var portFile = Path.Combine(profile, "DevToolsActivePort");
        File.Delete(portFile);
        using var leftover = Leftover(browser, profile, debuggable: true);
        try
        {
            for (var i = 0; i < 80 && !File.Exists(portFile); i++)
            {
                await Task.Delay(250);
            }

            Assert.IsTrue(File.Exists(portFile), "the browser left open is listening");
            var cookies = await new BrowserSession(browser, new SharePointSignInTarget(site, profile), NullLogger.Instance).SignInAsync(interactive: false, _ => { }, CancellationToken.None);
            Assert.IsTrue(cookies.Any(c => c.Name == "FedAuth"), "the profile's sign-in was read");
            Assert.IsTrue(leftover.WaitForExit(15000), "the browser left open was closed first");
        }
        finally
        {
            Stop(leftover);
        }
    }

    [TestMethod]
    public async Task ABrowserThatKeepsAnyFSFromItsPortHandsTheSignInToWebDriver()
    {
        var (browser, site) = Require();
        var profile = Path.Combine(Path.GetTempPath(), "anyfs-tests", "browser-" + Guid.NewGuid().ToString("N"));
        using var leftover = Leftover(browser, profile, debuggable: false);
        var log = new ListLogger();
        try
        {
            await Task.Delay(4000);
            Assert.IsFalse(leftover.HasExited, "the browser holds the profile");
            var session = new BrowserSession(browser, new SharePointSignInTarget(site, profile), log);
            var ex = await Assert.ThrowsExactlyAsync<SignInRequiredException>(() => session.SignInAsync(interactive: false, _ => { }, CancellationToken.None));
            Assert.IsTrue(log.Lines.Any(l => l.Contains("through WebDriver", StringComparison.Ordinal)), "it fell back to WebDriver:\n" + string.Join("\n", log.Lines));
            Assert.IsTrue(log.Lines.Any(l => l.Contains("WebDriver opened", StringComparison.Ordinal)), "and WebDriver started a browser:\n" + string.Join("\n", log.Lines));
            StringAssert.Contains(ex.Message, "lapsed", "WebDriver's own profile has no sign-in yet, and no window may be shown");
            Assert.IsFalse(ex.Permanent);
        }
        finally
        {
            Stop(leftover);
            foreach (var dir in Directory.GetDirectories(Path.GetDirectoryName(profile)!, Path.GetFileName(profile) + "*"))
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private sealed class ListLogger : ILogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
            {
                _lines.Add(formatter(state, exception));
            }
        }
    }
}
