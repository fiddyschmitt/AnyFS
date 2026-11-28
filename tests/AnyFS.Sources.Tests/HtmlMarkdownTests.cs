using System.Text;
using AnyFS.Sources.Atlassian;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The one HTML converter (the review of 21-Nov-2026, C7). The converter goes down nested HTML by calling itself: on an
/// ordinary thread 400 levels of <c>div</c> were converted and 800 ended the program with a stack overflow, which no
/// test can show and live, so what is shown here is that such HTML is converted now.
/// </summary>
[TestClass]
public class HtmlMarkdownTests
{
    private static string Nested(string tag, int levels, string inside)
    {
        var html = new StringBuilder();
        for (var n = 0; n < levels; n++)
        {
            html.Append('<').Append(tag).Append('>');
        }

        html.Append(inside);
        for (var n = 0; n < levels; n++)
        {
            html.Append("</").Append(tag).Append('>');
        }

        return html.ToString();
    }

    [TestMethod]
    public void HtmlIsMarkdownAsEachSourceMadeIt()
    {
        var made = HtmlMarkdown.Convert("<h2>Title</h2><p>Some <b>bold</b> text and a <a href=\"https://example.test/a\">link</a>.</p><ul><li>one</li><li>two</li></ul><!-- gone -->");
        Assert.AreEqual("## Title\n\nSome **bold** text and a [link](https://example.test/a).\n\n- one\n- two", made.Trim());
        StringAssert.Contains(HtmlMarkdown.Convert("<custom-tag>kept</custom-tag>"), "kept", "a tag it does not know is passed over, its text kept");
    }

    [TestMethod]
    [DataRow("div", 2_000)]
    [DataRow("blockquote", 3_000)]
    [DataRow("div", 15_000)]
    public void HtmlNestedFarDeeperThanAnOrdinaryThreadTakesIsConverted(string tag, int levels)
    {
        var made = HtmlMarkdown.Convert(Nested(tag, levels, "<p>at the bottom</p>"));
        StringAssert.Contains(made, "at the bottom");
        Assert.IsFalse(made.Contains(HtmlMarkdown.Unconverted, StringComparison.Ordinal), "converted, not shown as text");
    }

    [TestMethod]
    public void HtmlNestedDeeperThanIsTakenIsShownAsItsTextAndSaysSo()
    {
        var made = HtmlMarkdown.Convert(Nested("div", HtmlMarkdown.DeepestTaken + 1, "Fish &amp; chips"));
        Assert.AreEqual(HtmlMarkdown.Unconverted + "\n\nFish & chips", made);
    }

    [TestMethod]
    public void ASourcesOwnWorkAroundTheConversionRunsOnTheSameThreadAndThrowsWhatItThrows()
    {
        var names = new List<string?>();
        var made = HtmlMarkdown.Deep(() =>
        {
            names.Add(Thread.CurrentThread.Name);
            return HtmlMarkdown.Deep(() =>
            {
                names.Add(Thread.CurrentThread.Name);
                return HtmlMarkdown.Convert("<b>inside</b>");
            });
        });
        Assert.AreEqual("**inside**", made);
        CollectionAssert.AreEqual(new[] { "AnyFS HTML to Markdown", "AnyFS HTML to Markdown" }, names);
        Assert.AreNotEqual("AnyFS HTML to Markdown", Thread.CurrentThread.Name);
        var thrown = Assert.ThrowsExactly<InvalidDataException>(() => HtmlMarkdown.Deep<string>(() => throw new InvalidDataException("the source's own")));
        Assert.AreEqual("the source's own", thrown.Message);
        Assert.AreEqual("**after**", HtmlMarkdown.Convert("<b>after</b>"), "and the thread goes on");
    }

    [TestMethod]
    public async Task ManyAskAtOnceAndEachGetsItsOwn()
    {
        var made = await Task.WhenAll(Enumerable.Range(0, 200).Select(n => Task.Run(() => HtmlMarkdown.Convert($"<p>text <i>number {n}</i></p>"))));
        for (var n = 0; n < made.Length; n++)
        {
            Assert.AreEqual($"text *number {n}*", made[n].Trim());
        }
    }

    [TestMethod]
    [DataRow("<p>a<p>b<br><br><img src=x><li>c<li>d", 0)]
    [DataRow("<div><span>a</span><div><b>b</b></div></div>", 3)]
    [DataRow("<div><div><div>unclosed", 3)]
    [DataRow("</div></div><div>a</div>", 1)]
    [DataRow("a < b and <!-- a comment --> <!DOCTYPE html> text", 0)]
    [DataRow("<table><tr><td><table><tr><td>x</td></tr></table></td></tr></table>", 2)]
    public void HowDeepHtmlNestsIsToldFromItsTags(string html, int expected) => Assert.AreEqual(expected, HtmlMarkdown.Nesting(html));

    [TestMethod]
    public void ASourcesTextNestedDeepIsMadeIntoItsFile()
    {
        // Jira's and Confluence's text, with its links looked at on the way
        var made = AtlassianMarkdown.FromHtml(Nested("div", 3_000, "<a href=\"/browse/ABC-1\">ABC-1</a>"), new Uri("https://jira.fake.test/"));
        Assert.AreEqual("[ABC-1](https://jira.fake.test/browse/ABC-1)", made.Trim());
    }
}
