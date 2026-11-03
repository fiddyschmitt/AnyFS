using System.Diagnostics;
using System.Reflection;
using AnyFS.Sources.Common;
using OpenQA.Selenium;

namespace AnyFS.Sources.Tests;

/// <summary>What a WebDriver session started ends with the session, whichever way the driver went (spec 7.3.2): a shell stands in for the driver, what it starts for the browser.</summary>
[TestClass]
public class DriverProcessesTests
{
    public class Stub : DispatchProxy
    {
        public Action Quit { get; set; } = () => { };

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IWebDriver.Quit))
            {
                Quit();
                return null;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private static IWebDriver Driver(Action quit)
    {
        var driver = DispatchProxy.Create<IWebDriver, Stub>();
        ((Stub)(object)driver).Quit = quit;
        return driver;
    }

    private sealed class Tree(Process driver, Process browser) : IDisposable
    {
        public Process Driver { get; } = driver;

        public Process Browser { get; } = browser;

        public void AssertEnded()
        {
            Assert.IsTrue(Browser.WaitForExit(20000), "the browser ended");
            Assert.IsTrue(Driver.WaitForExit(20000), "the driver ended");
        }

        public void Dispose()
        {
            foreach (var process in new[] { Browser, Driver })
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }

                process.Dispose();
            }
        }
    }

    /// <summary>A "driver" taken in as it starts, which only then starts its "browser", as a driver does.</summary>
    private static async Task<Tree> StartAsync(DriverProcesses processes)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "sh") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
        var driver = Process.Start(info)!;
        processes.Take(driver.Id);
        if (!OperatingSystem.IsWindows())
        {
            await driver.StandardInput.WriteLineAsync("sleep 300 & echo $!");
            await driver.StandardInput.FlushAsync();
            return new Tree(driver, Process.GetProcessById(int.Parse((await driver.StandardOutput.ReadLineAsync())!, System.Globalization.CultureInfo.InvariantCulture)));
        }

        // it says who it is itself: other test runs start programs of their own, so no name will do
        var said = Path.Combine(Path.GetTempPath(), "anyfs-driver-" + Guid.NewGuid().ToString("N"));
        await driver.StandardInput.WriteLineAsync($"powershell -NoProfile -NonInteractive -Command \"[IO.File]::WriteAllText('{said}', $PID); Start-Sleep 300\"");
        await driver.StandardInput.FlushAsync();
        try
        {
            for (var i = 0; i < 600; i++)
            {
                if (File.Exists(said) && int.TryParse(ReadShared(said), out var id))
                {
                    return new Tree(driver, Process.GetProcessById(id));
                }

                await Task.Delay(50);
            }
        }
        finally
        {
            try
            {
                File.Delete(said);
            }
            catch (IOException)
            {
                // it has written the file and not closed it yet
            }
        }

        driver.Kill(entireProcessTree: true);
        throw new AssertFailedException("the stand-in for the browser did not start");
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    [TestMethod]
    public async Task AllThatTheDriverStartedEndsWithTheSession()
    {
        var processes = new DriverProcesses();
        using var tree = await StartAsync(processes);
        Assert.IsFalse(tree.Browser.HasExited);
        processes.Dispose();
        tree.AssertEnded();
    }

    [TestMethod]
    public async Task AQuitThatComesBackSaysSoAndEndsWhatItLeft()
    {
        using var processes = new DriverProcesses();
        using var tree = await StartAsync(processes);
        Assert.IsTrue(await processes.QuitAsync(Driver(() => { })));
        tree.AssertEnded();
    }

    [TestMethod]
    public async Task AQuitThatFailsEndsThemAllTheSame()
    {
        using var processes = new DriverProcesses();
        using var tree = await StartAsync(processes);
        Assert.IsTrue(await processes.QuitAsync(Driver(() => throw new WebDriverException("session deleted"))));
        tree.AssertEnded();
    }

    [TestMethod]
    public async Task AQuitThatDoesNotComeBackEndsThemAtItsLimit()
    {
        using var processes = new DriverProcesses(TimeSpan.FromMilliseconds(300));
        using var tree = await StartAsync(processes);

        // as a driver held up by a page that stalled answers: not until it is ended
        var clock = Stopwatch.StartNew();
        Assert.IsFalse(await processes.QuitAsync(Driver(() => tree.Driver.WaitForExit(60000))));
        tree.AssertEnded();
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(30), $"{clock.Elapsed}: not Selenium's minute");
    }

    [TestMethod]
    public async Task ABrowserWhoseDriverWentFirstEndsToo()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("only Windows has jobs: elsewhere a browser whose driver went first stays");
        }

        var processes = new DriverProcesses();
        using var tree = await StartAsync(processes);

        // as Selenium ends the driver when a Quit fails or a session cannot be made
        tree.Driver.Kill();
        Assert.IsTrue(tree.Driver.WaitForExit(20000));
        await Task.Delay(300);
        Assert.IsFalse(tree.Browser.HasExited, "nothing else ends it");
        processes.Dispose();
        Assert.IsTrue(tree.Browser.WaitForExit(20000), "the session's end does");
    }

    [TestMethod]
    public void NoProcessIsNoMatter()
    {
        using var processes = new DriverProcesses();
        processes.Take(int.MaxValue);
    }
}
