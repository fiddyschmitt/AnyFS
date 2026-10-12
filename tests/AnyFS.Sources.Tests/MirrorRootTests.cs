using AnyFS.Sources.Mirror;

namespace AnyFS.Sources.Tests;

/// <summary>Spec 7.1: how configured paths become folder chains inside the source.</summary>
[TestClass]
public class MirrorRootTests
{
    [TestMethod]
    [DataRow(@"\\aus-files-01\shared", "aus-files-01/shared", @"\\aus-files-01\shared")]
    [DataRow(@"\\h\s\x\y", "h/s/x/y", @"\\h\s\x\y")]
    [DataRow(@"//h/s", "h/s", @"\\h\s")]
    [DataRow(@"\\?\UNC\h\s", "h/s", @"\\h\s")]
    [DataRow(@"C:\", "c", @"C:\")]
    [DataRow("c:", "c", @"C:\")]
    [DataRow(@"D:\Photos", "d/Photos", @"D:\Photos")]
    [DataRow(@"D:\Photos\2024\", "d/Photos/2024", @"D:\Photos\2024")]
    [DataRow("C:/Users/me", "c/Users/me", @"C:\Users\me")]
    [DataRow(@"\\?\C:\x", "c/x", @"C:\x")]
    [DataRow("/mnt/nas", "mnt/nas", "/mnt/nas")]
    [DataRow("/", "", "/")]
    public void WindowsForms(string line, string chain, string fullPath)
    {
        Assert.IsTrue(MirrorRoot.TryParse(line, windows: true, out var root, out var error), error);
        Assert.AreEqual(chain, root.ChainPath);
        Assert.AreEqual(fullPath, root.FullPath);
        Assert.AreEqual(line, root.Original);
    }

    [TestMethod]
    [DataRow("/mnt/nas", "mnt/nas", "/mnt/nas")]
    [DataRow("/Volumes/x/", "Volumes/x", "/Volumes/x")]
    [DataRow("/", "", "/")]
    public void PosixForms(string line, string chain, string fullPath)
    {
        Assert.IsTrue(MirrorRoot.TryParse(line, windows: false, out var root, out var error), error);
        Assert.AreEqual(chain, root.ChainPath);
        Assert.AreEqual(fullPath, root.FullPath);
    }

    [TestMethod]
    [DataRow(@"\\aus-files-01\shared", false, "UNC paths need Windows")]
    [DataRow(@"C:\", false, "drive letters")]
    [DataRow("relative/path", false, "absolute path")]
    [DataRow("relative\\path", true, "absolute path")]
    [DataRow(@"\\server-only", true, "server and a share")]
    [DataRow("/a/../b", true, "not allowed")]
    [DataRow("", true, "absolute path")]
    public void Errors(string line, bool windows, string reasonContains)
    {
        Assert.IsFalse(MirrorRoot.TryParse(line, windows, out _, out var error));
        StringAssert.Contains(error, reasonContains);
    }

    [TestMethod]
    public void LinesSkipBlanksAndComments()
    {
        var lines = MirrorRoot.Lines("  /a  \r\n\r\n# comment\n/b\n").ToList();
        CollectionAssert.AreEqual(new[] { "/a", "/b" }, lines);
    }

    [TestMethod]
    public void PluginValidatesTheList()
    {
        var plugin = new MirrorPlugin();
        Assert.AreEqual("mirror", plugin.TypeId);
        Assert.AreEqual("files", plugin.TypeFolder);
        var messages = plugin.Validate(new MirrorSettings { Paths = "" }).ToList();
        Assert.AreEqual(1, messages.Count);
        StringAssert.Contains(messages[0].Message, "at least one path");

        var sep = OperatingSystem.IsWindows() ? @"C:\x" : "/x";
        var dup = OperatingSystem.IsWindows() ? @"c:\X\" : "/x/";
        messages = plugin.Validate(new MirrorSettings { Paths = sep + "\n" + dup + "\nnot-absolute", CheckIntervalSeconds = 1 }).ToList();
        Assert.IsTrue(messages.Any(m => m.Message.Contains("would both appear", StringComparison.Ordinal)), string.Join(" | ", messages.Select(m => m.Message)));
        Assert.IsTrue(messages.Any(m => m.Message.Contains("not-absolute", StringComparison.Ordinal)));
        Assert.IsTrue(messages.Any(m => m.Property == nameof(MirrorSettings.CheckIntervalSeconds)));
    }
}
