using System.Net;
using System.Text;
using AnyFS.Sdk;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

[TestClass]
public class SharePointPagesTests
{
    private static readonly Uri Page = new("https://contoso.sharepoint.com/sites/Team/SitePages/News.aspx");

    /// <summary>CanvasContent1 as SharePoint Online keeps it (20-Oct-2026, a page made through Graph): the controls' data in escaped JSON, colons in addresses as &amp;#58;.</summary>
    private const string ModernCanvas = """<div><div data-sp-canvascontrol="" data-sp-canvasdataversion="1.0" data-sp-controldata="&#123;&quot;emphasis&quot;&#58;&#123;&quot;zoneEmphasis&quot;&#58;0&#125;,&quot;position&quot;&#58;&#123;&quot;layoutIndex&quot;&#58;1,&quot;sectionFactor&quot;&#58;12,&quot;zoneIndex&quot;&#58;1,&quot;sectionIndex&quot;&#58;1,&quot;controlIndex&quot;&#58;1&#125;,&quot;id&quot;&#58;&quot;34e59ff4-e8ca-4ee0-a4ba-f1a89da7ae7d&quot;,&quot;controlType&quot;&#58;4,&quot;editorType&quot;&#58;&quot;CKEditor&quot;&#125;"><div data-sp-rte=""><h2>Heading two</h2><p>Hello <strong>world</strong> from <a href="https&#58;//example.com/x">AnyFS</a>.</p><ul><li>one</li><li>two</li></ul><p>A <em>second</em> paragraph.</p></div></div></div>""";

    /// <summary>An Image web part as a modern page keeps it: its type id, its data, and the server's searchable rendering (caption, image address).</summary>
    private const string ImageWebPart = """<div><div data-sp-canvascontrol="" data-sp-canvasdataversion="1.0" data-sp-controldata="&#123;&quot;controlType&quot;&#58;3&#125;"><div data-sp-webpart="" data-sp-webpartdataversion="1.9" data-sp-webpartdata="&#123;&quot;id&quot;&#58;&quot;d1d91016-032f-456d-98a4-721247c305e8&quot;&#125;"><div data-sp-componentid="">d1d91016-032f-456d-98a4-721247c305e8</div><div data-sp-htmlproperties=""><div data-sp-prop-name="captionText" data-sp-searchableplaintext="true">The new office</div><img data-sp-prop-name="imageSource" src="/sites/Team/SiteAssets/SitePages/News/office%20front.png" /></div></div></div></div>""";

    /// <summary>A wiki page's WikiField: two zones side by side in a layout table, and a web part's hidden marker.</summary>
    private const string WikiField = """<div class="ExternalClassA1"><table id="layoutsTable" style="width&#58;100%;"><tbody><tr style="vertical-align&#58;top;"><td style="width&#58;66.6%;"><div class="ms-rte-layoutszone-outer"><div class="ms-rte-layoutszone-inner"><h1>Welcome</h1><p>Read the <a href="/sites/Team/Shared%20Documents/Handbook.docx">handbook</a> first.</p></div></div></td><td style="width&#58;33.3%;"><div class="ms-rte-layoutszone-outer"><div class="ms-rte-layoutszone-inner"><div class="ms-rtestate-read ms-rte-wpbox"><div class="ms-rtestate-notify ms-rtestate-read 0c6c8b2b" id="div_0c6c8b2b"></div><div id="vid_0c6c8b2b" class="ms-hide">0c6c8b2b</div></div><p>Contacts on the right.</p></div></div></td></tr></tbody></table></div>""";

    private static string Text(byte[] markdown) => Encoding.UTF8.GetString(markdown);

    [TestMethod]
    public void AModernPageIsItsTitleThenItsTextAsMarkdown()
    {
        Assert.AreEqual("""
            # AnyFS test page

            ## Heading two

            Hello **world** from [AnyFS](https://example.com/x).

            - one
            - two

            A *second* paragraph.

            """.ReplaceLineEndings("\n"), Text(SharePointPages.Markdown("AnyFS test page", ModernCanvas, null, Page)));
    }

    [TestMethod]
    public void AWebPartKeepsItsSearchableRenderingWithoutItsTypeId()
    {
        var markdown = Text(SharePointPages.Markdown("News", ImageWebPart, null, Page));
        StringAssert.Contains(markdown, "The new office");
        StringAssert.Contains(markdown, "(https://contoso.sharepoint.com/sites/Team/SiteAssets/SitePages/News/office%20front.png)", "the image, at an address that works away from SharePoint");
        Assert.IsFalse(markdown.Contains("d1d91016", StringComparison.Ordinal), markdown);
    }

    [TestMethod]
    public void AWikiPagesZonesComeOneAfterAnother()
    {
        var markdown = Text(SharePointPages.Markdown("Home", null, WikiField, Page));
        Assert.IsFalse(markdown.Contains('|'), "the layout table is no table: " + markdown);
        Assert.IsFalse(markdown.Contains("0c6c8b2b", StringComparison.Ordinal), "the hidden web part marker goes: " + markdown);
        StringAssert.Contains(markdown, "# Welcome");
        StringAssert.Contains(markdown, "[handbook](https://contoso.sharepoint.com/sites/Team/Shared%20Documents/Handbook.docx)");
        Assert.IsTrue(markdown.IndexOf("first.", StringComparison.Ordinal) < markdown.IndexOf("Contacts on the right.", StringComparison.Ordinal), markdown);
    }

    [TestMethod]
    public void TheCanvasWinsOverAWikiFieldAndAnEmptyPageIsItsTitle()
    {
        StringAssert.Contains(Text(SharePointPages.Markdown("News", ModernCanvas, WikiField, Page)), "Heading two");
        Assert.AreEqual("# Home\n", Text(SharePointPages.Markdown("Home", null, null, Page)));
        Assert.AreEqual("# Home\n", Text(SharePointPages.Markdown("  Home  ", "", "  ", Page)));
        Assert.AreEqual("", Text(SharePointPages.Markdown(null, null, null, Page)));
    }

    [TestMethod]
    public void PagesShowAsMarkdownFiles()
    {
        Assert.IsTrue(SharePointPages.IsPage("News.aspx"));
        Assert.IsTrue(SharePointPages.IsPage("Old.ASPX"));
        Assert.IsFalse(SharePointPages.IsPage(".aspx"));
        Assert.IsFalse(SharePointPages.IsPage("picture.png"));
        Assert.AreEqual("News.md", SharePointPages.Shown("News.aspx"));
        Assert.AreEqual("Templates/Plan.aspx", SharePointPages.Upstream("Templates/Plan.md"));
        Assert.AreEqual("News.aspx", SharePointPages.Upstream("News.MD"));
        Assert.IsNull(SharePointPages.Upstream("Templates"), "a folder");
        Assert.IsNull(SharePointPages.Upstream("picture.png"), "nothing but a page shows as a file there");
    }

    [TestMethod]
    public void APageReadsFromAnyOffset()
    {
        var markdown = "# Home\n"u8.ToArray();
        using (var whole = SharePointPages.Response(markdown, 0))
        {
            Assert.AreEqual(HttpStatusCode.OK, whole.StatusCode);
            CollectionAssert.AreEqual(markdown, whole.Content.ReadAsByteArrayAsync().Result);
        }

        using (var tail = SharePointPages.Response(markdown, 2))
        {
            Assert.AreEqual(HttpStatusCode.PartialContent, tail.StatusCode);
            CollectionAssert.AreEqual(markdown[2..], tail.Content.ReadAsByteArrayAsync().Result);
        }

        using (var past = SharePointPages.Response(markdown, markdown.Length))
        {
            Assert.AreEqual(HttpStatusCode.RequestedRangeNotSatisfiable, past.StatusCode);
        }

        using var empty = SharePointPages.Response([], 0);
        Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode);
    }

    [TestMethod]
    public void APagesVersionFollowsItsMarkdown()
    {
        var one = SharePointPages.Markdown("Home", ModernCanvas, null, Page);
        Assert.AreEqual(SharePointPages.Version(one), SharePointPages.Version(SharePointPages.Markdown("Home", ModernCanvas, null, Page)));
        Assert.AreNotEqual(SharePointPages.Version(one), SharePointPages.Version(SharePointPages.Markdown("Home!", ModernCanvas, null, Page)));
    }
}

/// <summary>The site's pages in the source: read, never written.</summary>
[TestClass]
public class SharePointPageLibraryTests
{
    private static readonly byte[] Home = "# Home\n"u8.ToArray();

    private static MemoryDocumentStore Store()
    {
        var store = new MemoryDocumentStore();
        store.AddLibrary("", "/Shared Documents", "Documents");
        store.AddLibrary("", "/SitePages", "Site Pages", pages: true);
        store.Seed("/SitePages", "Home.md", Home);
        store.Seed("/SitePages", "Templates/Plan.md", "# Plan\n"u8.ToArray());
        return store;
    }

    [TestMethod]
    public async Task ThePagesReadButNothingThereIsWritten()
    {
        await using var source = await MemorySharePointSource.StartAsync(Store());
        var none = CancellationToken.None;
        CollectionAssert.AreEquivalent(new[] { "Documents", "Site Pages" }, await SpsTestServer.NamesAsync(source, ""));
        await using (var file = await source.OpenAsync("Site Pages/Home.md", VfsOpenMode.Read, none))
        {
            var back = new byte[Home.Length];
            Assert.AreEqual(Home.Length, await file.ReadFullyAsync(0, back, none));
            CollectionAssert.AreEqual(Home, back);
        }

        async Task ReadOnly(Func<Task> write, string what)
        {
            var ex = await Assert.ThrowsExactlyAsync<VfsException>(write, what);
            Assert.AreEqual(VfsError.ReadOnly, ex.Error, $"{what}: {ex.Message}");
            StringAssert.Contains(ex.Message, "Markdown");
        }

        await ReadOnly(() => source.OpenAsync("Site Pages/Home.md", VfsOpenMode.ReadWrite, none), "opening a page to write");
        await ReadOnly(() => source.OpenAsync("Site Pages/New.md", VfsOpenMode.CreateNew, none), "making a page");
        await ReadOnly(() => source.CreateDirectoryAsync("Site Pages/Folder", none), "making a folder");
        await ReadOnly(() => source.DeleteAsync("Site Pages/Home.md", none), "deleting a page");
        await ReadOnly(() => source.RenameAsync("Site Pages/Home.md", "Site Pages/Start.md", false, none), "renaming a page");
        await ReadOnly(() => source.RenameAsync("Site Pages/Templates/Plan.md", "Documents/Plan.md", false, none), "moving a page out");
        await ReadOnly(() => source.SetTimesAsync("Site Pages/Home.md", DateTimeOffset.UtcNow, null, none), "setting a page's times");
        Assert.IsNotNull(await source.StatAsync("Site Pages/Home.md", none), "and the page is still there");
    }
}
