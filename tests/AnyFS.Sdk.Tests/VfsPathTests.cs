namespace AnyFS.Sdk.Tests;

[TestClass]
public class VfsPathTests
{
    [TestMethod]
    [DataRow("", "")]
    [DataRow("/", "")]
    [DataRow("a/b", "a/b")]
    [DataRow("/a/b/", "a/b")]
    [DataRow("a\\b\\c", "a/b/c")]
    [DataRow("a//b", "a/b")]
    [DataRow("\\\\a\\\\b", "a/b")]
    [DataRow("spaces in names/a b c.txt", "spaces in names/a b c.txt")]
    [DataRow("ünïcödé ✓ 日本語.txt", "ünïcödé ✓ 日本語.txt")]
    public void Normalize_AcceptsBothSeparatorsAndTrimsSlashes(string input, string expected)
    {
        Assert.AreEqual(expected, VfsPath.Normalize(input));
    }

    [TestMethod]
    [DataRow("..")]
    [DataRow("../x")]
    [DataRow("a/../b")]
    [DataRow("a/./b")]
    [DataRow(".")]
    [DataRow("a\0b")]
    public void Normalize_RejectsDotSegmentsAndNul(string input)
    {
        var ex = Assert.ThrowsExactly<VfsException>(() => VfsPath.Normalize(input));
        Assert.AreEqual(VfsError.InvalidPath, ex.Error);
        Assert.IsFalse(VfsPath.TryNormalize(input, out _, out var reason));
        Assert.IsFalse(string.IsNullOrEmpty(reason));
    }

    [TestMethod]
    public void SplitJoinParentName()
    {
        CollectionAssert.AreEqual(Array.Empty<string>(), VfsPath.Split(""));
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, VfsPath.Split("a/b/c"));
        Assert.AreEqual("a/b", VfsPath.Join("a", "b"));
        Assert.AreEqual("b", VfsPath.Join("", "b"));
        Assert.AreEqual("a", VfsPath.Join("a", ""));
        Assert.AreEqual("a/b/c", VfsPath.Join(["a", "b", "c"]));
        Assert.AreEqual("a/b", VfsPath.Parent("a/b/c"));
        Assert.AreEqual("", VfsPath.Parent("a"));
        Assert.AreEqual("", VfsPath.Parent(""));
        Assert.AreEqual("c", VfsPath.Name("a/b/c"));
        Assert.AreEqual("a", VfsPath.Name("a"));
        Assert.AreEqual("", VfsPath.Name(""));
        Assert.AreEqual(("a/b", "c"), VfsPath.SplitLast("a/b/c"));
    }

    [TestMethod]
    public void IsUnder()
    {
        Assert.IsTrue(VfsPath.IsUnder("a/b/c", ""));
        Assert.IsTrue(VfsPath.IsUnder("a/b/c", "a/b"));
        Assert.IsTrue(VfsPath.IsUnder("a/b", "a/b"));
        Assert.IsFalse(VfsPath.IsUnder("a/bc", "a/b"));
        Assert.IsFalse(VfsPath.IsUnder("a", "a/b"));
    }

    [TestMethod]
    [DataRow("files", true, "")]
    [DataRow("aus-files-01", true, "")]
    [DataRow("a b", true, "")]
    [DataRow("日本語", true, "")]
    [DataRow("", false, "empty")]
    [DataRow(".", false, "not allowed")]
    [DataRow("..", false, "not allowed")]
    [DataRow("a:b", false, "':' is not allowed")]
    [DataRow("a*b", false, "'*' is not allowed")]
    [DataRow("a?b", false, "'?' is not allowed")]
    [DataRow("a\"b", false, "'\"' is not allowed")]
    [DataRow("a<b", false, "'<' is not allowed")]
    [DataRow("a|b", false, "'|' is not allowed")]
    [DataRow("a/b", false, "'/' is not allowed")]
    [DataRow("a\\b", false, "'\\' is not allowed")]
    [DataRow("trailing.", false, "space or a dot")]
    [DataRow(" leading", false, "space or a dot")]
    [DataRow("trailing ", false, "space or a dot")]
    [DataRow(".hidden", false, "space or a dot")]
    [DataRow("a\tb", false, "control")]
    public void IsValidSegment_AppliesTheCrossPlatformRules(string segment, bool valid, string reasonContains)
    {
        Assert.AreEqual(valid, VfsPath.IsValidSegment(segment, out var reason), reason);
        if (!valid)
        {
            StringAssert.Contains(reason, reasonContains);
        }
    }

    [TestMethod]
    public void IsValidSegment_LimitsTo255Bytes()
    {
        Assert.IsTrue(VfsPath.IsValidSegment(new string('n', 255), out _));
        Assert.IsFalse(VfsPath.IsValidSegment(new string('n', 256), out var reason));
        StringAssert.Contains(reason, "255");
        // 86 three-byte characters are 258 bytes
        Assert.IsFalse(VfsPath.IsValidSegment(new string('日', 86), out _));
        Assert.IsTrue(VfsPath.IsValidSegment(new string('日', 85), out _));
    }

    [TestMethod]
    [DataRow(" files/aus-files-01 ", "files/aus-files-01")]
    [DataRow("files\\x", "files/x")]
    [DataRow("/files/", "files")]
    [DataRow("", "")]
    [DataRow("   ", "")]
    public void TryNormalizeSourcePath_NormalisesAndAllowsEmpty(string configured, string expected)
    {
        Assert.IsTrue(VfsPath.TryNormalizeSourcePath(configured, out var normalized, out var reason), reason);
        Assert.AreEqual(expected, normalized);
    }

    [TestMethod]
    [DataRow("files/../x")]
    [DataRow("files/a:b")]
    [DataRow("files/trailing.")]
    public void TryNormalizeSourcePath_RejectsBadSegments(string configured)
    {
        Assert.IsFalse(VfsPath.TryNormalizeSourcePath(configured, out _, out var reason));
        Assert.IsFalse(string.IsNullOrEmpty(reason));
    }
}
