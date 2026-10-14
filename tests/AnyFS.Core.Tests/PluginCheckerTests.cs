using System.Xml.Linq;
using AnyFS.Core.Plugins;

namespace AnyFS.Core.Tests;

/// <summary><c>anyfs plugin check</c> (spec 14.1) and the guaranteed framework set it checks against.</summary>
[TestClass]
public class PluginCheckerTests
{
    private static string SamplePluginDll => Path.Combine(AppContext.BaseDirectory, "AnyFS.SamplePlugin.dll");

    [TestMethod]
    public void TheSamplePluginPasses()
    {
        Assert.IsTrue(File.Exists(SamplePluginDll), $"expected the sample plugin beside the tests at {SamplePluginDll}");
        var report = PluginChecker.Check(SamplePluginDll);
        Console.WriteLine(string.Join(Environment.NewLine, report.Lines));
        Assert.IsTrue(report.Passed, string.Join(" | ", report.Lines.Where(l => l.Level == "FAIL")));
        Assert.IsTrue(report.Lines.Any(l => l.Level == "PASS" && l.Text.StartsWith("AnyFS.Sdk ", StringComparison.Ordinal)), "the SDK reference and its version");
        Assert.IsTrue(report.Lines.Any(l => l.Text.Contains("source plugin 'sample'")), "the plugin type");
        Assert.IsTrue(report.Lines.Any(l => l.Text.Contains("round-trip")), "the settings round trip");
        Assert.IsTrue(report.Lines.Any(l => l.Text.Contains("built for .NETCoreApp")), "the target framework");
    }

    [TestMethod]
    public void AMissingFileAndAForeignFileFail()
    {
        var missing = PluginChecker.Check(Path.Combine(Path.GetTempPath(), "anyfs-tests", "nope-" + Guid.NewGuid().ToString("N") + ".dll"));
        Assert.IsFalse(missing.Passed);
        StringAssert.Contains(missing.Lines[0].Text, "does not exist");

        var text = Path.GetTempFileName();
        try
        {
            File.WriteAllText(text, "not a dll");
            var foreign = PluginChecker.Check(text);
            Assert.IsFalse(foreign.Passed);
            StringAssert.Contains(foreign.Lines[0].Text, "not a .NET assembly");
        }
        finally
        {
            File.Delete(text);
        }
    }

    [TestMethod]
    public void ReferencesAreClassifiedAgainstTheGuaranteedSet()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anyfs-tests", "refs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Some.Package.dll"), "");
            Assert.AreEqual(PluginChecker.ReferenceVerdict.Sdk, PluginChecker.Classify("AnyFS.Sdk", dir));
            Assert.AreEqual(PluginChecker.ReferenceVerdict.Kept, PluginChecker.Classify("System.Text.Json", dir));
            Assert.AreEqual(PluginChecker.ReferenceVerdict.Kept, PluginChecker.Classify("System.Runtime", dir));
            Assert.AreEqual(PluginChecker.ReferenceVerdict.Kept, PluginChecker.Classify("Microsoft.Extensions.Logging.Abstractions", dir));
            Assert.AreEqual(PluginChecker.ReferenceVerdict.MissingFramework, PluginChecker.Classify("System.Xml.Linq", dir), "spike 7: a call into System.Xml.Linq fails in the trimmed host");
            Assert.AreEqual(PluginChecker.ReferenceVerdict.MissingFramework, PluginChecker.Classify("System.Net.Sockets", dir));
            Assert.AreEqual(PluginChecker.ReferenceVerdict.MissingFramework, PluginChecker.Classify("Microsoft.Win32.Registry", dir));
            Assert.AreEqual(PluginChecker.ReferenceVerdict.Facade, PluginChecker.Classify("netstandard", dir));
            Assert.AreEqual(PluginChecker.ReferenceVerdict.Facade, PluginChecker.Classify("mscorlib", dir));
            Assert.AreEqual(PluginChecker.ReferenceVerdict.Beside, PluginChecker.Classify("Some.Package", dir));
            Assert.AreEqual(PluginChecker.ReferenceVerdict.MissingBeside, PluginChecker.Classify("Other.Package", dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The set lives in three places: the App project roots it, the checker reads it, the docs list it. They must agree.</summary>
    [TestMethod]
    public void TheGuaranteedSetMatchesTheAppProjectAndTheDocs()
    {
        var repo = RepoRoot();
        if (repo is null)
        {
            Assert.Inconclusive("not running from a checkout (no AnyFS.slnx above the test binaries)");
        }

        var csproj = XDocument.Load(Path.Combine(repo, "src", "AnyFS.App", "AnyFS.App.csproj"));
        var roots = csproj.Descendants("TrimmerRootAssembly").Select(e => (string)e.Attribute("Include")!).ToList();
        foreach (var name in GuaranteedFramework.Assemblies)
        {
            CollectionAssert.Contains(roots, name, $"{name} is in GuaranteedFramework.Assemblies but not rooted in AnyFS.App.csproj");
        }

        foreach (var root in roots.Where(r => GuaranteedFramework.IsFrameworkAssembly(r) || r.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)))
        {
            Assert.IsTrue(GuaranteedFramework.Contains(root), $"{root} is rooted in AnyFS.App.csproj but missing from GuaranteedFramework.Assemblies");
        }

        var docs = File.ReadAllText(Path.Combine(repo, "docs", "plugin-authoring.md"));
        foreach (var name in GuaranteedFramework.Assemblies)
        {
            StringAssert.Contains(docs, $"`{name}`", $"docs/plugin-authoring.md should list {name}");
        }
    }

    private static string? RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AnyFS.slnx")))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}
