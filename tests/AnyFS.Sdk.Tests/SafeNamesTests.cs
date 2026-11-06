namespace AnyFS.Sdk.Tests;

/// <summary>The names a client takes for what an upstream calls something (spec 6.2), as every source makes them.</summary>
[TestClass]
public class SafeNamesTests
{
    [TestMethod]
    public void RefusedCharactersEndingsAndDeviceNamesAreMadeSafe()
    {
        Assert.AreEqual("a_b_c_d_e_f_g_h_i_j", SafeNames.Safe("a\\b/c:d*e?f\"g<h>i|j"));
        Assert.AreEqual("tab_here", SafeNames.Safe("tab\there"));
        Assert.AreEqual("trailing._", SafeNames.Safe("trailing."));
        Assert.AreEqual("trailing _", SafeNames.Safe("trailing "));
        Assert.AreEqual("_CON", SafeNames.Safe("CON"));
        Assert.AreEqual("_com1.txt", SafeNames.Safe("com1.txt"));
        Assert.AreEqual("_", SafeNames.Safe(""));
        Assert.AreEqual("_", SafeNames.Safe("."));
        Assert.AreEqual("__", SafeNames.Safe(".."));
        Assert.AreEqual("plain name.txt", SafeNames.Safe("plain name.txt"));
        Assert.AreEqual("  spaced", SafeNames.Safe("  spaced"), "kept as it is unless trimmed");
        Assert.AreEqual("spaced", SafeNames.Safe("  spaced  ", trim: true));
        Assert.AreEqual(new string('x', SafeNames.MaxName), SafeNames.Safe(new string('x', 300)));
        Assert.AreEqual("xxxxx", SafeNames.Safe(new string('x', 300), maxLength: 5));
    }

    [TestMethod]
    public void TwinsInCaseAreNumberedAfterTheFirstAndCleanNamesKeepTheirs()
    {
        CollectionAssert.AreEqual(
            new[] { "README", "readme (2)", "notes.txt", "Notes (2).txt", "con_", "_CON" },
            SafeNames.Unique(["README", "readme", "notes.txt", "Notes.txt", "con?", "CON"]).ToArray());
        CollectionAssert.AreEqual(new[] { "team.md (2)", "a" }, SafeNames.Unique(["team.md", "a"], reserved: ["team.md"], numbered: (name, n) => $"{name} ({n})").ToArray());
        CollectionAssert.AreEqual(new[] { "x (2)", "x" }, SafeNames.Unique(["x ", "x"], safe: name => name.Trim()).ToArray(), "a clean name keeps its name whatever its place; each stays in its own place");
    }

    [TestMethod]
    public void TitlesAreOneLineAndCutWithAnEllipsis()
    {
        Assert.AreEqual("one two three", SafeNames.OneLine("  one\n two\t\tthree "));
        Assert.AreEqual("one two…", SafeNames.Title("one two three", 8));
        Assert.AreEqual("short", SafeNames.Title("short", 8));
        Assert.AreEqual("", SafeNames.Title(null, 8));
        Assert.AreEqual("notes (2).pdf", SafeNames.Numbered("notes.pdf", 2));
        Assert.AreEqual("Makefile (2)", SafeNames.Numbered("Makefile", 2));
        Assert.AreEqual("a.longextensionname (2)", SafeNames.Numbered("a.longextensionname", 2));
        Assert.AreEqual("👍👍", SafeNames.Cut("👍👍👍", 5), "never half a surrogate pair");
    }
}
