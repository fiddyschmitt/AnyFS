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
    public void ANameSuggestedForASourceIsAPathSegmentAndAnEntrysOwnNameStaysAsItIs()
    {
        // a source's name is its default path, a segment: no dot or space at either end (ISourcePlugin.SuggestName), which
        // a file's name may have at its start. A page titled ".NET guidelines" suggested a name the dialog then refused
        Assert.AreEqual("NET guidelines (example.com)", SafeNames.Suggested(".NET guidelines (example.com)"));
        Assert.AreEqual("notes", SafeNames.Suggested(" . .notes. . "), "the dots and spaces at either end, however they alternate");
        Assert.AreEqual("a_b (host)", SafeNames.Suggested("a/b (host)"));
        Assert.AreEqual("_CON", SafeNames.Suggested("CON"));
        Assert.AreEqual("v1.2 notes", SafeNames.Suggested("v1.2 notes"), "a dot inside is no matter");
        Assert.AreEqual("", SafeNames.Suggested(" .. "), "nothing left: nothing to suggest");
        Assert.AreEqual("", SafeNames.Suggested(""));
        foreach (var raw in new[] { ".NET guidelines", "trailing. ", "..", "a\tb", " .hidden", "com1.txt", new string('x', 300), new string('x', 239) + " .tail", new string('界', 200) })
        {
            var suggested = SafeNames.Suggested(raw);
            var valid = VfsPath.IsValidSegment(suggested, out var reason);
            Assert.IsTrue(valid || suggested.Length == 0, $"'{raw}' suggested '{suggested}': {reason}");
        }

        // what a source lists is named as before: a repository's .github folder is .github
        Assert.AreEqual(".github", SafeNames.Safe(".github"));
        Assert.AreEqual(".NET guidelines", SafeNames.Safe(" .NET guidelines ", trim: true));
        CollectionAssert.AreEqual(new[] { ".github", ".gitignore", "README.md" }, SafeNames.Unique([".github", ".gitignore", "README.md"]).ToArray());
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
