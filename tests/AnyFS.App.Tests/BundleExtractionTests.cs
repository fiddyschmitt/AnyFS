using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.App.Tests;

/// <summary>The single-file host's unpacked-library folders: other builds' folders go at start-up, one in use or still being written stays.</summary>
[TestClass]
public class BundleExtractionTests
{
    private string _root = "";

    [TestInitialize]
    public void Init() => _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public void OtherBuildsFoldersGoOnceSettledAndFree()
    {
        var app = Path.Combine(_root, ".net", BundleExtraction.AppName);
        var own = Build(app, "ownbuild0001", ageMinutes: 0);
        var old1 = Build(app, "oldbuild0001", ageMinutes: 60);
        var old2 = Build(app, "oldbuild0002", ageMinutes: 60);
        var young = Build(app, "youngbuild01", ageMinutes: 2);
        var unrelatedFile = Path.Combine(app, "notes.txt");
        File.WriteAllText(unrelatedFile, "not a folder");

        var removed = BundleExtraction.RemoveOtherBuilds(own, TimeSpan.FromMinutes(10));

        CollectionAssert.AreEquivalent(new[] { "oldbuild0001", "oldbuild0002" }, removed.ToArray());
        Assert.IsFalse(Directory.Exists(old1));
        Assert.IsFalse(Directory.Exists(old2));
        Assert.IsTrue(Directory.Exists(own), "our own folder stays");
        Assert.IsTrue(Directory.Exists(young), "a folder written minutes ago may belong to an anyfs still starting");
        Assert.IsTrue(File.Exists(unrelatedFile), "only folders are considered");
    }

    [TestMethod]
    public void AFolderARunningAnyfsHasLoadedStays()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("only Windows keeps a loaded library from being opened exclusively");
        }

        var app = Path.Combine(_root, ".net", BundleExtraction.AppName);
        var own = Build(app, "ownbuild0001", ageMinutes: 0);
        var busy = Build(app, "busybuild001", ageMinutes: 60);
        using (new FileStream(Path.Combine(busy, "libSkiaSharp.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.AreEqual(0, BundleExtraction.RemoveOtherBuilds(own, TimeSpan.FromMinutes(10)).Count);
            Assert.IsTrue(Directory.Exists(busy), "a folder with a file in use is left whole");
            Assert.IsTrue(File.Exists(Path.Combine(busy, "libHarfBuzzSharp.dll")), "not even its free files go");
        }

        CollectionAssert.AreEqual(new[] { "busybuild001" }, BundleExtraction.RemoveOtherBuilds(own, TimeSpan.FromMinutes(10)).ToArray());
        Assert.IsFalse(Directory.Exists(busy));
    }

    [TestMethod]
    public void OutsideABundleThereIsNothingToDo()
    {
        Assert.IsNull(BundleExtraction.OwnDirectory(), "the test host is not a single-file bundle");
        BundleExtraction.CleanUpInBackground(NullLogger.Instance);
    }

    /// <summary>A pretend extraction folder with the two libraries the window loads, last written <paramref name="ageMinutes"/> ago.</summary>
    private static string Build(string app, string name, int ageMinutes)
    {
        var dir = Directory.CreateDirectory(Path.Combine(app, name)).FullName;
        File.WriteAllText(Path.Combine(dir, "libSkiaSharp.dll"), name);
        File.WriteAllText(Path.Combine(dir, "libHarfBuzzSharp.dll"), name);
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddMinutes(-ageMinutes));
        return dir;
    }
}
