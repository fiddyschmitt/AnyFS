using System.Text;
using System.Text.RegularExpressions;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The geckodriver AnyFS carries (spec 7.3.2): written out once per version for Selenium, older copies gone, written again
/// when cleaned away; a build that carries none, or one that cannot write it, leaves Firefox's driver to Selenium Manager.
/// The real driver against a real Firefox: <see cref="ThreeDXBrowserTests"/> and <see cref="ThreeDXPublishedTests"/>.
/// </summary>
[TestClass]
[DoNotParallelize]
public class GeckoDriverTests
{
    private static readonly DateTime LongAgo = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private string _folder = "";
    private string? _keptFolder;
    private Func<Stream?> _keptOpen = null!;

    private static string Exe => OperatingSystem.IsWindows() ? ".exe" : "";

    [TestInitialize]
    public void Before()
    {
        _folder = Path.Combine(Path.GetTempPath(), "anyfs-tests", "drivers-" + Guid.NewGuid().ToString("N"));
        _keptFolder = Environment.GetEnvironmentVariable("ANYFS_DRIVERS");
        _keptOpen = GeckoDriver.Open;
        Environment.SetEnvironmentVariable("ANYFS_DRIVERS", _folder);
    }

    [TestCleanup]
    public void After()
    {
        GeckoDriver.Open = _keptOpen;
        Environment.SetEnvironmentVariable("ANYFS_DRIVERS", _keptFolder);
        if (File.Exists(_folder))
        {
            File.Delete(_folder);
        }
        else if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static Func<Stream?> Carrying(string driver) => () => new MemoryStream(Encoding.ASCII.GetBytes(driver));

    [TestMethod]
    public void TheCarriedDriverIsWrittenOncePerVersionAndOlderCopiesGo()
    {
        Directory.CreateDirectory(_folder);
        var older = Path.Combine(_folder, "geckodriver-000000000000" + Exe);
        var unfinished = Path.Combine(_folder, "geckodriver-111111111111" + Exe + ".0123.tmp");
        var other = Path.Combine(_folder, "chromedriver" + Exe);
        File.WriteAllText(older, "an older build's driver");
        File.WriteAllText(unfinished, "half a driver");
        File.WriteAllText(other, "not geckodriver's");
        GeckoDriver.Open = Carrying("driver one");

        var path = GeckoDriver.Write();
        Assert.IsNotNull(path);
        Assert.AreEqual(_folder, Path.GetDirectoryName(path));
        StringAssert.Matches(Path.GetFileName(path), new Regex("^geckodriver-[0-9a-f]{12}" + Regex.Escape(Exe) + "$"));
        Assert.AreEqual("driver one", File.ReadAllText(path));
        Assert.IsFalse(File.Exists(older), "an older version's copy goes");
        Assert.IsFalse(File.Exists(unfinished), "and a write that never finished");
        Assert.IsTrue(File.Exists(other), "only geckodriver's copies");
        if (!OperatingSystem.IsWindows())
        {
            Assert.IsTrue(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute), "it can be run");
        }

        File.SetLastWriteTimeUtc(path, LongAgo);
        Assert.AreEqual(path, GeckoDriver.Write(), "the same version, the same file");
        Assert.AreEqual(LongAgo, File.GetLastWriteTimeUtc(path), "not written again");

        File.Delete(path);
        Assert.AreEqual(path, GeckoDriver.Write(), "cleaned away meanwhile: written again");
        Assert.AreEqual("driver one", File.ReadAllText(path));

        GeckoDriver.Open = Carrying("driver two");
        var next = GeckoDriver.Write();
        Assert.AreNotEqual(path, next, "another version, another file");
        Assert.AreEqual("driver two", File.ReadAllText(next!));
        Assert.IsFalse(File.Exists(path), "and the one before goes");
    }

    [TestMethod]
    public void FirefoxIsDrivenThroughTheCarriedDriver()
    {
        GeckoDriver.Open = Carrying("driver");
        var service = GeckoDriver.Service(NullLogger.Instance, "test");
        var path = GeckoDriver.Write()!;
        Assert.AreEqual(Path.GetDirectoryName(path), service.DriverServicePath);
        Assert.AreEqual(Path.GetFileName(path), service.DriverServiceExecutableName);
        Assert.AreEqual(path, GeckoDriver.FileOf(service));
    }

    [TestMethod]
    public void ABuildThatCarriesNoneLeavesFirefoxsDriverToSeleniumManager()
    {
        GeckoDriver.Open = () => null;
        Assert.IsNull(GeckoDriver.Write());
        var service = GeckoDriver.Service(NullLogger.Instance, "test");
        Assert.IsNull(service.DriverServicePath, "Selenium Manager finds one when the browser starts");
        Assert.IsFalse(Directory.Exists(_folder), "nothing written");
    }

    [TestMethod]
    public void ADriverThatCannotBeWrittenIsSaidAndSeleniumManagerFindsOne()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_folder)!);
        File.WriteAllText(_folder, "a file where the folder goes");
        GeckoDriver.Open = Carrying("driver");
        var log = new LineLogger();
        var service = GeckoDriver.Service(log, "test");
        Assert.IsNull(service.DriverServicePath);
        Assert.IsTrue(log.Lines.Any(l => l.Contains("the geckodriver AnyFS carries could not be written to", StringComparison.Ordinal) && l.Contains("Selenium Manager finds one instead", StringComparison.Ordinal)), string.Join("\n", log.Lines));
    }
}
