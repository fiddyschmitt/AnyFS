using System.Diagnostics;
using AnyFS.Sources.SharePoint;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Tests;

/// <summary>Whether a browser's managed policy turns off the remote debugging the web session reads the sign-in through (spec 7.3.2).</summary>
[TestClass]
public class BrowserPolicyTests
{
    [TestMethod]
    public void APolicyFileThatTurnsRemoteDebuggingOffIsFound()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anyfs-tests", "policies-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.IsNull(BrowserLocator.PolicyFilesOff(dir), "no policy folder: nothing set");

            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a-broken.json"), "{ not json");
            File.WriteAllText(Path.Combine(dir, "b-allowed.json"), """{ "RemoteDebuggingAllowed": true, "HomepageLocation": "https://intranet" }""");
            File.WriteAllText(Path.Combine(dir, "c-list.json"), "[]");
            Assert.IsNull(BrowserLocator.PolicyFilesOff(dir), "allowed, unreadable or not a policy object");

            var off = Path.Combine(dir, "d-locked-down.json");
            File.WriteAllText(off, """{ "RemoteDebuggingAllowed": false }""");
            Assert.AreEqual(off, BrowserLocator.PolicyFilesOff(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

/// <summary>
/// The web session's browser against the real one, with the signed-in profile the REST live suite uses
/// (ANYFS_TEST_SPS_SIGNIN=web, DEVELOPING.md): a browser left open on the profile, as by an AnyFS stopped while its
/// sign-in window was up, is closed first when it can be reached and named when it cannot.
/// </summary>
[TestClass]
public class BrowserSessionLiveTests
{
    private static (string Browser, Uri Site) Require()
    {
        SpsTestServer.Require();
        if (!SpsTestServer.WebSession)
        {
            Assert.Inconclusive("the browser's live tests run with the web session: ANYFS_TEST_SPS_SIGNIN=web");
        }

        var browser = BrowserLocator.Find() ?? throw new AssertInconclusiveException("no Edge, Chrome or Chromium on this machine");
        SiteAddress.TryParse(SpsTestServer.Site, out var site, out _);
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
            var cookies = await new BrowserSession(browser, profile, NullLogger.Instance).SignInAsync(site, interactive: false, _ => { }, CancellationToken.None);
            Assert.IsTrue(cookies.Any(c => c.Name == "FedAuth"), "the profile's sign-in was read");
            Assert.IsTrue(leftover.WaitForExit(15000), "the browser left open was closed first");
        }
        finally
        {
            Stop(leftover);
        }
    }

    [TestMethod]
    public async Task ABrowserOnTheProfileThatCannotBeReachedIsNamed()
    {
        var (browser, site) = Require();
        var profile = Path.Combine(Path.GetTempPath(), "anyfs-tests", "browser-" + Guid.NewGuid().ToString("N"));
        using var leftover = Leftover(browser, profile, debuggable: false);
        try
        {
            await Task.Delay(4000);
            Assert.IsFalse(leftover.HasExited, "the browser holds the profile");
            var ex = await Assert.ThrowsExactlyAsync<SignInRequiredException>(() => new BrowserSession(browser, profile, NullLogger.Instance).SignInAsync(site, interactive: false, _ => { }, CancellationToken.None));
            Assert.IsTrue(ex.Permanent, "waiting will not help, so the source waits for a Retry");
            StringAssert.Contains(ex.Message, "close that window");
        }
        finally
        {
            Stop(leftover);
            try
            {
                Directory.Delete(profile, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
