using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Atlassian;
using AnyFS.Sources.Atlassian.Confluence;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>The lab's Confluence as the fake holds it (spec 7.10): the accounts, spaces, pages, attachments and comments the tests read.</summary>
internal static class ConfluenceSeed
{
    public const string Token = "confluence-tester-token";
    public const string Password = "tester-password";

    public static readonly byte[] Large = Enumerable.Range(0, (1 << 20) + 31).Select(i => (byte)(i * 11 % 251)).ToArray();

    public const string ArchitectureStorage = """
        <ac:structured-macro ac:name="toc" ac:schema-version="1" />
        <h2>Layers</h2>
        <p>How it is put together.&nbsp;See <ac:link><ri:page ri:content-title="Onboarding" /></ac:link> first, and ask <ac:link><ri:user ri:userkey="key-tester" /></ac:link>.</p>
        <ul><li>Sources</li><li>Mounts<ul><li>WinFsp</li></ul></li></ul>
        <ac:structured-macro ac:name="info" ac:schema-version="1"><ac:parameter ac:name="title">Read-only</ac:parameter><ac:rich-text-body><p>The sources only read.</p></ac:rich-text-body></ac:structured-macro>
        <ac:structured-macro ac:name="code" ac:schema-version="1"><ac:parameter ac:name="language">csharp</ac:parameter><ac:plain-text-body><![CDATA[if (a < b && c > d) { }]]></ac:plain-text-body></ac:structured-macro>
        <table><tbody><tr><th>Layer</th><th>Project</th></tr><tr><td>SDK</td><td>AnyFS.Sdk</td></tr></tbody></table>
        <p><ac:image ac:width="200"><ri:attachment ri:filename="diagram.png" /></ac:image></p>
        <ac:structured-macro ac:name="expand" ac:schema-version="1"><ac:parameter ac:name="title">More detail</ac:parameter><ac:rich-text-body><p>Hidden until expanded.</p></ac:rich-text-body></ac:structured-macro>
        <p>Status: <ac:structured-macro ac:name="status" ac:schema-version="1"><ac:parameter ac:name="title">In progress</ac:parameter></ac:structured-macro></p>
        <ac:task-list><ac:task><ac:task-id>1</ac:task-id><ac:task-status>complete</ac:task-status><ac:task-body>Draw the diagram</ac:task-body></ac:task><ac:task><ac:task-id>2</ac:task-id><ac:task-status>incomplete</ac:task-status><ac:task-body>Review it</ac:task-body></ac:task></ac:task-list>
        <p>The spec: <ac:link><ri:attachment ri:filename="spec.pdf" /><ac:plain-text-link-body><![CDATA[the spec]]></ac:plain-text-link-body></ac:link>. <ac:emoticon ac:name="smile" /> Issue <ac:structured-macro ac:name="jira"><ac:parameter ac:name="key">ANYFS-1</ac:parameter></ac:structured-macro>.</p>
        <ac:structured-macro ac:name="some-vendor-macro"><ac:rich-text-body><p>A vendor's macro's own text.</p></ac:rich-text-body></ac:structured-macro>
        <ac:structured-macro ac:name="children" ac:schema-version="2" />
        """;

    public static FakeConfluence Lab(string root = "/confluence")
    {
        var fake = new FakeConfluence(root: root);
        fake.AddAccount("tester", "Test Er", Token, Password);
        fake.AddAccount("other", "Someone Else", "confluence-other-token");

        var anyfs = fake.AddSpace("ANYFS", "AnyFS Lab");
        var home = anyfs.Home!;
        fake.AddAttachment(home, "notes.txt", Encoding.UTF8.GetBytes("home notes\n"));
        var arch = fake.AddPage(anyfs, home, "Architecture", ArchitectureStorage);
        arch.Version = 7;
        arch.ChangedBy = "other";
        arch.Labels.AddRange(["design", "core"]);
        fake.AddAttachment(arch, "diagram.png", [137, 80, 78, 71, 9]);
        fake.AddAttachment(arch, "spec.pdf", Encoding.UTF8.GetBytes("%PDF-1.4\n"));
        fake.AddAttachment(arch, "Architecture.md", Encoding.UTF8.GetBytes("# Not the page\n"));
        var footer = fake.AddComment(arch, "tester", "<p>A footer comment, with <strong>bold</strong>.</p>", new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero));
        fake.AddComment(arch, "other", "<p>A reply.</p>", new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero), footer);
        fake.AddComment(arch, "other", "<p>Which layers?</p>", new DateTimeOffset(2026, 10, 4, 11, 0, 0, TimeSpan.Zero)).Inline = true;
        fake.Comments[^1].Selection = "Layers";
        var services = fake.AddPage(anyfs, arch, "Services", "<p>The services.</p>");
        fake.AddAttachment(services, "large.bin", Large);
        fake.AddPage(anyfs, services, "Storage", "<p>Three levels down.</p>");
        fake.AddPage(anyfs, home, "Onboarding", "<h2>Day one</h2><ol><li>Install</li></ol>");
        fake.AddAttachment(fake.PageTitled("Onboarding"), "notes.txt", Encoding.UTF8.GetBytes("onboarding notes\n"));
        fake.AddPage(anyfs, home, "What? Why: \"quotes\" <angles> / slash", "<p>A title Windows refuses.</p>");
        var restricted = fake.AddPage(anyfs, home, "Restricted page", "<p>Not for the tester.</p>");
        restricted.RestrictedTo = ["other"];
        fake.AddPage(anyfs, restricted, "Under the restricted page", "<p>Not for the tester either.</p>");
        fake.AddPage(anyfs, null, "Release notes 1", "<p>Release one.</p>", "blogpost").Created = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.FromHours(10));
        fake.AddPage(anyfs, null, "Release notes 2", "<p>Release two.</p>", "blogpost").Created = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.FromHours(10));

        fake.AddSpace("TEAM", "Team");
        fake.AddSpace("~tester", "Test Er", type: "personal");
        fake.AddSpace("OLD", "An archived space", status: "archived");
        fake.AddSpace("SECRET", "Secret").VisibleTo = ["other"];
        var bulk = fake.AddSpace("BULK", "Bulk pages");
        for (var n = 1; n <= 120; n++)
        {
            fake.AddPage(bulk, bulk.Home, $"Bulk page {n:D3}", $"<p>One of many: {n}.</p>");
        }

        return fake;
    }

    public static ConfluenceSettings Settings(FakeConfluence fake, string path = "", string token = Token) => new()
    {
        Address = fake.Server + path,
        AccessToken = token,
        RefreshSeconds = 60,
        Concurrency = 4,
    };
}

/// <summary>The Confluence source against <see cref="FakeConfluence"/> (spec 7.10): the address, the tree, the Markdown, the attachments and the walk.</summary>
[TestClass]
public class ConfluenceTests
{
    private const string Arch = "AnyFS Lab (ANYFS)/AnyFS Lab Home/Architecture";

    private static async Task<ConfluenceSource> StartAsync(FakeConfluence fake, ConfluenceSettings settings, TestSourceContext? context = null, AtlassianStandIn? standIn = null)
    {
        var source = new ConfluenceSource(settings, standIn ?? fake.StandIn());
        await source.InitialiseAsync(context ?? new TestSourceContext(), CancellationToken.None);
        return source;
    }

    private static async Task<List<VfsEntry>> EntriesAsync(ISource source, string path)
    {
        var entries = new List<VfsEntry>();
        await foreach (var e in source.ListAsync(path, CancellationToken.None))
        {
            entries.Add(e);
        }

        return entries;
    }

    private static async Task<List<string>> NamesAsync(ISource source, string path) => (await EntriesAsync(source, path)).Select(e => e.Name).ToList();

    private static async Task<byte[]> ReadAsync(ISource source, string path, long offset = 0)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length - offset];
        var read = await file.ReadFullyAsync(offset, buffer, CancellationToken.None);
        return buffer[..read];
    }

    private static async Task<string> TextAsync(ISource source, string path) => Encoding.UTF8.GetString(await ReadAsync(source, path));

    // --- the address ---------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("", "Server", "", null, null)]
    [DataRow("display/ENG", "Space", "ENG", null, null)]
    [DataRow("display/ENG/Page+Title", "Page", "ENG", null, "Page Title")]
    [DataRow("display/ENG/2026/10/30/Post+Title", "Page", "ENG", null, "Post Title")]
    [DataRow("display/~smith", "Space", "~smith", null, null)]
    [DataRow("spaces/ENG/overview", "Space", "ENG", null, null)]
    [DataRow("spaces/ENG/pages/123456/Some+Page", "Page", "ENG", "123456", null)]
    [DataRow("spaces/ENG/blog/2026/10/30/123457/Post+Title", "Page", "ENG", "123457", null)]
    [DataRow("spaces/ENG/blog/123458", "Page", "ENG", "123458", null)]
    [DataRow("spaces/viewspace.action?key=ENG", "Space", "ENG", null, null)]
    [DataRow("pages/viewpage.action?pageId=98765", "Page", "", "98765", null)]
    [DataRow("pages/viewpage.action?spaceKey=ENG&title=A+Title", "Page", "ENG", null, "A Title")]
    [DataRow("collector/pages.action?key=ENG", "Space", "ENG", null, null)]
    [DataRow("index.action", "Server", "", null, null)]
    public void TheAddressNamesTheScope(string rest, string kind, string space, string? pageId, string? title)
    {
        var address = AtlassianAddress.Candidates(new Uri("https://confluence.example.com/" + rest)).First();
        var scope = ConfluenceScope.From(address, out var problem);
        Assert.IsNotNull(scope, problem);
        Assert.AreEqual(kind, scope.Kind.ToString());
        Assert.AreEqual(space, scope.Space);
        Assert.AreEqual(pageId, scope.PageId);
        Assert.AreEqual(title, scope.Title);
    }

    [TestMethod]
    [DataRow(98765L)]
    [DataRow(1L)]
    [DataRow(123456789012L)]
    public void ATinyLinkNamesItsPage(long id)
    {
        // as Confluence's TinyUrl makes them: the id's bytes low first, trailing zeros dropped, base 64 with - and _
        var bytes = BitConverter.GetBytes(id);
        var length = bytes.Length;
        while (length > 1 && bytes[length - 1] == 0)
        {
            length--;
        }

        var tiny = Convert.ToBase64String(bytes, 0, length).TrimEnd('=').Replace('/', '-').Replace('+', '_').TrimEnd('A');
        Assert.AreEqual(id, ConfluenceScope.TinyId(tiny));
        var scope = ConfluenceScope.From(AtlassianAddress.Candidates(new Uri("https://confluence.example.com/x/" + tiny)).First(), out _);
        Assert.AreEqual(id.ToString(System.Globalization.CultureInfo.InvariantCulture), scope?.PageId);
    }

    [TestMethod]
    public async Task EveryRequestOfTheServerIsARowThatEnds()
    {
        var fake = ConfluenceSeed.Lab();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake), context);
        await source.CountFilesAsync(CancellationToken.None);
        await UpstreamRequestRows.AllEndAsync(context);
        Assert.IsTrue(fake.Requests.Count > 0);
        Assert.AreEqual(fake.Requests.Count, context.UpstreamRequests.Requests.Count, "every one the server saw, and no more");
        Assert.IsTrue(context.UpstreamRequests.Requests.All(r => r.Answer is not null));
    }

    [TestMethod]
    public async Task AnInstallationAtAContextPathIsFound()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake, "/display/ANYFS"));
        Assert.AreEqual("https://confluence.fake.test/confluence/", source.Session.Address.Base.AbsoluteUri);
        Assert.AreEqual(ConfluenceScopeKind.Space, source.Scope.Kind);
    }

    // --- the tree ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheServerShowsTheGlobalSpacesTheAccountCanRead()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake));
        CollectionAssert.AreEqual(new[] { "AnyFS Lab (ANYFS)", "Bulk pages (BULK)", "Team (TEAM)" }, await NamesAsync(source, ""));
    }

    [TestMethod]
    public async Task PersonalAndArchivedSpacesHaveFoldersOfTheirOwnWhenShown()
    {
        var fake = ConfluenceSeed.Lab();
        var settings = ConfluenceSeed.Settings(fake);
        settings.PersonalSpaces = true;
        settings.ArchivedSpaces = true;
        await using var source = await StartAsync(fake, settings);
        CollectionAssert.AreEqual(new[] { "AnyFS Lab (ANYFS)", "Bulk pages (BULK)", "Team (TEAM)", "Personal spaces", "Archived spaces" }, await NamesAsync(source, ""));
        CollectionAssert.AreEqual(new[] { "Test Er (~tester)" }, await NamesAsync(source, "Personal spaces"));
        CollectionAssert.AreEqual(new[] { "An archived space (OLD)" }, await NamesAsync(source, "Archived spaces"));
        CollectionAssert.AreEqual(new[] { "An archived space Home" }, await NamesAsync(source, "Archived spaces/An archived space (OLD)"));
    }

    [TestMethod]
    public async Task ASpaceShowsItsTopPagesAndItsBlog()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake));
        CollectionAssert.AreEqual(new[] { "AnyFS Lab Home", "Blog" }, await NamesAsync(source, "AnyFS Lab (ANYFS)"));
        CollectionAssert.AreEqual(new[] { "Team Home" }, await NamesAsync(source, "Team (TEAM)"), "no blog where there are no posts");
        CollectionAssert.AreEquivalent(new[] { "2026-10-05 Release notes 1", "2026-10-12 Release notes 2" }, await NamesAsync(source, "AnyFS Lab (ANYFS)/Blog"));
        CollectionAssert.AreEqual(new[] { "2026-10-05 Release notes 1.md" }, await NamesAsync(source, "AnyFS Lab (ANYFS)/Blog/2026-10-05 Release notes 1"));
        StringAssert.Contains(await TextAsync(source, "AnyFS Lab (ANYFS)/Blog/2026-10-05 Release notes 1/2026-10-05 Release notes 1.md"), "Release one.");
    }

    [TestMethod]
    public async Task EveryPageIsAFolderOfItsMarkdownItsAttachmentsAndItsChildren()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake));
        CollectionAssert.AreEqual(new[] { "AnyFS Lab Home.md", "notes.txt", "Architecture", "Onboarding", "What_ Why_ _quotes_ _angles_ _ slash" },
            await NamesAsync(source, "AnyFS Lab (ANYFS)/AnyFS Lab Home"), "the restricted page is not the tester's to see");
        CollectionAssert.AreEqual(new[] { "Architecture.md", "diagram.png", "spec.pdf", "Architecture (2).md", "Services" }, await NamesAsync(source, Arch),
            "an attachment named as the page's own Markdown is numbered");
        Assert.AreEqual("# Not the page\n", await TextAsync(source, Arch + "/Architecture (2).md"));
        CollectionAssert.AreEqual(new[] { "Storage.md" }, await NamesAsync(source, Arch + "/Services/Storage"));
        CollectionAssert.AreEqual(ConfluenceSeed.Large, await ReadAsync(source, Arch + "/Services/large.bin"));
        CollectionAssert.AreEqual(ConfluenceSeed.Large[4096..], await ReadAsync(source, Arch + "/Services/large.bin", 4096));
    }

    [TestMethod]
    public async Task APagesMarkdownConvertsItsStorageFormat()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake));
        var md = await TextAsync(source, Arch + "/Architecture.md");
        StringAssert.StartsWith(md, "https://confluence.fake.test/confluence/display/ANYFS/Architecture\n\n# Architecture\n\n**Created:** Test Er (tester), ");
        StringAssert.Contains(md, "**Last changed:** Someone Else (other), ");
        StringAssert.Contains(md, "(version 7)  \n**Labels:** design, core  \n\n---\n\n");
        StringAssert.Contains(md, "*(table of contents)*");
        StringAssert.Contains(md, "## Layers");
        StringAssert.Contains(md, "[Onboarding](https://confluence.fake.test/confluence/display/ANYFS/Onboarding)");
        StringAssert.Contains(md, "ask @Test Er.");
        StringAssert.Contains(md, "> **Info: Read-only**");
        StringAssert.Contains(md, "> The sources only read.");
        StringAssert.Contains(md, "```csharp\nif (a < b && c > d) { }\n```");
        StringAssert.Contains(md, "| Layer | Project |");
        StringAssert.Contains(md, "![diagram.png](diagram.png)", "the image is the attachment beside the Markdown");
        StringAssert.Contains(md, "**More detail**");
        StringAssert.Contains(md, "Status: [In progress]");
        StringAssert.Contains(md, "- [x] Draw the diagram");
        StringAssert.Contains(md, "- [ ] Review it");
        StringAssert.Contains(md, "[the spec](spec.pdf)");
        StringAssert.Contains(md, "🙂");
        StringAssert.Contains(md, "Issue ANYFS-1.");
        StringAssert.Contains(md, "A vendor's macro's own text.");
        StringAssert.Contains(md, "*(child pages)*");
        Assert.IsFalse(md.Contains("ac:", StringComparison.Ordinal), "no storage markup left");
        Assert.IsFalse(md.Contains("&nbsp;", StringComparison.Ordinal));
        Assert.AreEqual((await source.StatAsync(Arch + "/Architecture.md", CancellationToken.None))!.Length, Encoding.UTF8.GetByteCount(md));
    }

    [TestMethod]
    public async Task APagesCommentsEndItsMarkdownOldestFirst()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake));
        var md = await TextAsync(source, Arch + "/Architecture.md");
        var comments = md[md.IndexOf("## Comments", StringComparison.Ordinal)..];
        StringAssert.Contains(comments, "### Test Er (tester), ");
        StringAssert.Contains(comments, "A footer comment, with **bold**.");
        StringAssert.Contains(comments, "(reply to Test Er)\n\nA reply.");
        StringAssert.Contains(comments, "(on \"Layers\")\n\nWhich layers?");

        var settings = ConfluenceSeed.Settings(fake);
        settings.Comments = false;
        await using var without = await StartAsync(fake, settings);
        Assert.IsFalse((await TextAsync(without, Arch + "/Architecture.md")).Contains("## Comments", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AttachmentsOffShowNoneAndLinkThemInConfluence()
    {
        var fake = ConfluenceSeed.Lab();
        var settings = ConfluenceSeed.Settings(fake);
        settings.Attachments = false;
        await using var source = await StartAsync(fake, settings);
        CollectionAssert.AreEqual(new[] { "Architecture.md", "Services" }, await NamesAsync(source, Arch));
        StringAssert.Contains(await TextAsync(source, Arch + "/Architecture.md"), $"/confluence/download/attachments/{fake.PageTitled("Architecture").Id}/diagram.png");
    }

    [TestMethod]
    public async Task APageAsTheScopeShowsItselfAndThePagesBelowIt()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake, "/pages/viewpage.action?pageId=" + fake.PageTitled("Architecture").Id));
        CollectionAssert.AreEqual(new[] { "Architecture.md", "diagram.png", "spec.pdf", "Architecture (2).md", "Services" }, await NamesAsync(source, ""));

        await using var titled = await StartAsync(fake, ConfluenceSeed.Settings(fake, "/display/ANYFS/Services"));
        CollectionAssert.AreEqual(new[] { "Services.md", "large.bin", "Storage" }, await NamesAsync(titled, ""));
    }

    [TestMethod]
    public async Task APagesBodyIsReadWithItsSiblings()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake, "/display/BULK"));
        var pages = await NamesAsync(source, "Bulk pages Home");
        var before = BodyReads(fake);
        foreach (var page in pages.Where(p => p.StartsWith("Bulk page 0", StringComparison.Ordinal)).Take(40))
        {
            StringAssert.Contains(await TextAsync(source, $"Bulk pages Home/{page}/{page}.md"), "One of many");
        }

        // two alone, then one alone and ten behind it four times, or a little more where a slow machine left a gap between two opens
        var bodies = BodyReads(fake) - before;
        Assert.IsTrue(bodies <= 16, $"{bodies} reads with bodies for forty pages opened one straight after the other: they come ten a request");
    }

    private static int BodyReads(FakeConfluence fake)
        => fake.Requests.Count(r => r.Contains("body.storage", StringComparison.Ordinal) && !r.Contains("/child/comment", StringComparison.Ordinal));

    [TestMethod]
    public async Task GoingThroughFoldersOfPagesReadsTheBodiesBesideThem()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake, "/display/BULK"));
        var pages = (await NamesAsync(source, "Bulk pages Home")).Where(p => p.StartsWith("Bulk page 0", StringComparison.Ordinal)).Take(40).ToList();
        var before = BodyReads(fake);
        foreach (var page in pages)
        {
            await NamesAsync(source, $"Bulk pages Home/{page}");
        }

        var bodies = BodyReads(fake) - before;
        Assert.IsTrue(bodies <= 6, $"{bodies} reads with bodies for forty folders listed one after the other: they come ten a request");
    }

    [TestMethod]
    public async Task AnOpenBesideFoldersBeingGoneThroughIsReadAlone()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake, "/display/BULK"));
        var pages = (await NamesAsync(source, "Bulk pages Home")).Where(p => p.StartsWith("Bulk page ", StringComparison.Ordinal)).ToList();

        // a client going straight through the folders, reading those beside them
        await NamesAsync(source, $"Bulk pages Home/{pages[0]}");
        await NamesAsync(source, $"Bulk pages Home/{pages[1]}");
        Assert.AreEqual(1, fake.Count("/child/page?start="));

        // and someone waiting for one page, under the same parent, waits for no others
        var before = BodyReads(fake);
        StringAssert.Contains(await TextAsync(source, $"Bulk pages Home/{pages[50]}/{pages[50]}.md"), "One of many");
        Assert.AreEqual(1, fake.Count("/child/page?start="), "no read of several");
        Assert.AreEqual(1, BodyReads(fake) - before, "its own body");

        // a stat that has to list the folder of its file, as a program does that opens a file by its path
        Assert.IsNotNull(await source.StatAsync($"Bulk pages Home/{pages[60]}/{pages[60]}.md", CancellationToken.None));
        Assert.AreEqual(1, fake.Count("/child/page?start="), "no read of several");
        Assert.AreEqual(2, BodyReads(fake) - before, "its own body");
    }

    [TestMethod]
    public async Task AnOpenBesideAClientGoingThroughTheFilesWaitsForNoOthers()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake, "/display/BULK"));
        var pages = (await NamesAsync(source, "Bulk pages Home")).Where(p => p.StartsWith("Bulk page ", StringComparison.Ordinal)).ToList();

        // a read of several that does not answer
        using var held = new ManualResetEventSlim();
        fake.Fault = r =>
        {
            if (r.Url.AbsolutePath.EndsWith("/child/page", StringComparison.Ordinal) && r.Q("expand")?.Contains("body.storage", StringComparison.Ordinal) == true)
            {
                held.Wait(TimeSpan.FromSeconds(30));
            }

            return null;
        };

        try
        {
            // the third of a run brings those beside it, and neither it nor someone opening a page elsewhere waits for them
            for (var i = 0; i < 3; i++)
            {
                StringAssert.Contains(await TextAsync(source, $"Bulk pages Home/{pages[i]}/{pages[i]}.md").WaitAsync(TimeSpan.FromSeconds(20)), "One of many");
            }

            StringAssert.Contains(await TextAsync(source, $"Bulk pages Home/{pages[50]}/{pages[50]}.md").WaitAsync(TimeSpan.FromSeconds(20)), "One of many");
        }
        finally
        {
            held.Set();
            fake.Fault = null;
        }
    }

    [TestMethod]
    public async Task AReadOfTheSiblingsThatFailsIsNoFailureOfThePage()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake, "/display/BULK"));
        var pages = (await NamesAsync(source, "Bulk pages Home")).Where(n => n.StartsWith("Bulk page ", StringComparison.Ordinal)).ToList();

        // the server takes too long over several at once (a page in place of data stands for any such failure)
        fake.Fault = r => r.Url.AbsolutePath.EndsWith("/child/page", StringComparison.Ordinal) && r.Q("expand")?.Contains("body.storage", StringComparison.Ordinal) == true
            ? FakeAtlassian.Response.Html("<html>busy</html>")
            : null;
        StringAssert.Contains(await TextAsync(source, $"Bulk pages Home/{pages[0]}/{pages[0]}.md"), "One of many");
        Assert.AreEqual(0, fake.Count("/child/page?start="), "the first alone");

        // a client going straight through the files: from the third or so those beside it come behind it; which fails, and each is read alone
        for (var i = 1; i < 8; i++)
        {
            StringAssert.Contains(await TextAsync(source, $"Bulk pages Home/{pages[i]}/{pages[i]}.md"), "One of many");
        }

        var together = fake.Count("/child/page?start=");
        Assert.IsTrue(together is 1 or 2, $"{together} tries: not tried again for a while, each of them costs a timeout on a slow server");
        StringAssert.Contains(await TextAsync(source, $"Bulk pages Home/{pages[20]}/{pages[20]}.md"), "One of many");
        Assert.AreEqual(together, fake.Count("/child/page?start="), "nor for a page further along");
    }

    [TestMethod]
    public async Task APageAskedForAloneIsReadAlone()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake, "/display/BULK"));
        var pages = (await NamesAsync(source, "Bulk pages Home")).Where(n => n.StartsWith("Bulk page ", StringComparison.Ordinal)).ToList();
        fake.Requests.Clear();
        await TextAsync(source, $"Bulk pages Home/{pages[7]}/{pages[7]}.md");
        Assert.AreEqual(1, fake.Requests.Count(r => r.Contains("body.storage", StringComparison.Ordinal) && !r.Contains("/child/comment", StringComparison.Ordinal)), "one body, not the hundred and twenty beside it");
    }

    [TestMethod]
    public async Task ABlogPostsBodyIsNeverAskedForWithoutItsSpace()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake, "/display/ANYFS"));
        var posts = await NamesAsync(source, "Blog");
        Assert.AreEqual(2, posts.Count);
        StringAssert.Contains(await TextAsync(source, $"Blog/{posts[0]}/{posts[0]}.md"), "Release");
        Assert.AreEqual(0, fake.Count("spaceKey=&"), "an empty key may name every space there is");
    }

    [TestMethod]
    public async Task AnOpenAndAListingAskNothingAboutTheFoldersAboveThem()
    {
        var fake = ConfluenceSeed.Lab();
        var settings = ConfluenceSeed.Settings(fake);
        settings.RefreshSeconds = 1;
        await using var source = await StartAsync(fake, settings);

        // down to the page, as a client goes
        var path = "";
        foreach (var name in new[] { "AnyFS Lab (ANYFS)", "AnyFS Lab Home", "Architecture", "Services" })
        {
            await NamesAsync(source, path);
            path = VfsPath.Join(path, name);
        }

        await NamesAsync(source, path);
        await Task.Delay(1100);

        // past every request kept: an attachment comes straight from its listing
        fake.Requests.Clear();
        CollectionAssert.AreEqual(ConfluenceSeed.Large, await ReadAsync(source, path + "/large.bin"));
        Assert.AreEqual(1, fake.Requests.Count, string.Join("; ", fake.Requests));
        StringAssert.Contains(fake.Requests.Single(), "/download/attachments/");

        // a page's Markdown asks for that page alone: no page above it, no page beside it
        await Task.Delay(1100);
        fake.Requests.Clear();
        StringAssert.Contains(await TextAsync(source, path + "/Services.md"), "The services.");
        var services = fake.PageTitled("Services").Id;
        Assert.IsTrue(fake.Requests.All(r => r.Contains($"/content/{services}", StringComparison.Ordinal)), string.Join("; ", fake.Requests));
    }

    [TestMethod]
    public async Task WhatAKeptListingNamesIsFoundFromItAfterARestart()
    {
        var fake = ConfluenceSeed.Lab();
        using var context = new TestSourceContext();
        var path = "AnyFS Lab (ANYFS)/AnyFS Lab Home/Architecture/Services";
        await using (var first = await StartAsync(fake, ConfluenceSeed.Settings(fake), context))
        {
            await first.CountFilesAsync(CancellationToken.None);
        }

        await using var next = await StartAsync(fake, ConfluenceSeed.Settings(fake), context);
        fake.Requests.Clear();
        CollectionAssert.AreEqual(ConfluenceSeed.Large, await ReadAsync(next, path + "/large.bin"));
        Assert.AreEqual(1, fake.Requests.Count, string.Join("; ", fake.Requests));
    }

    [TestMethod]
    public async Task WithNoListingHeldAPathIsFoundByNamesAlone()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake));

        // straight to a deep file: the pages above it are asked for their children, not their bodies or comments
        fake.Requests.Clear();
        CollectionAssert.AreEqual(ConfluenceSeed.Large, await ReadAsync(source, "AnyFS Lab (ANYFS)/AnyFS Lab Home/Architecture/Services/large.bin"));
        var services = fake.PageTitled("Services").Id;
        Assert.IsTrue(fake.Requests.Count(r => r.Contains("body.storage", StringComparison.Ordinal)) <= 2, string.Join("; ", fake.Requests));
        Assert.AreEqual(0, fake.Count("/content/" + fake.PageTitled("Architecture").Id + "?"), "no page above it read");
        Assert.AreEqual(0, fake.Count("/content/" + fake.PageTitled("AnyFS Lab Home").Id + "?"), "no page above it read");
    }

    [TestMethod]
    public async Task AFolderWhoseListingFailedStaysSoUntilTheNextWalk()
    {
        var fake = ConfluenceSeed.Lab();
        var settings = ConfluenceSeed.Settings(fake);
        settings.WalkMinutes = 30;
        await using var source = await StartAsync(fake, settings);
        await NamesAsync(source, "AnyFS Lab (ANYFS)");
        var home = "AnyFS Lab (ANYFS)/AnyFS Lab Home";

        // a service that does not answer, not only a refusal: a whole interval, not the 60 s between refreshes
        var failing = true;
        var asked = 0;
        fake.Fault = r =>
        {
            if (failing && r.Url.AbsolutePath.EndsWith("/child/comment", StringComparison.Ordinal))
            {
                asked++;
                return FakeAtlassian.Response.Html("<html>busy</html>");
            }

            return null;
        };
        var first = await Assert.ThrowsExactlyAsync<VfsException>(async () => await NamesAsync(source, home));
        Assert.AreEqual(VfsError.Unavailable, first.Error);
        var times = asked;
        Assert.IsTrue(times > 0);
        failing = false;
        var again = await Assert.ThrowsExactlyAsync<VfsException>(async () => await NamesAsync(source, home));
        Assert.AreEqual(first.Message, again.Message, "answered at once as it failed");
        Assert.AreEqual(times, asked, "nothing asked");

        // the walk lists it again, and it comes back
        await source.WalkAsync(new SourceCounters(), full: true, CancellationToken.None);
        CollectionAssert.Contains(await NamesAsync(source, home), "AnyFS Lab Home.md");
    }

    [TestMethod]
    public async Task AKeptFolderIsNotAskedForAgainAndTheNextWalkKeepsAQuietSpaceAndListsAChangedOne()
    {
        var fake = ConfluenceSeed.Lab();
        var settings = ConfluenceSeed.Settings(fake, "/display/ANYFS");
        settings.RefreshSeconds = 1;
        await using var source = await StartAsync(fake, settings);
        var home = "AnyFS Lab Home";
        await source.CountFilesAsync(CancellationToken.None);

        // older than any interval: answered from what is kept, however often, with nothing asked
        await Task.Delay(1100);
        fake.Requests.Clear();
        await NamesAsync(source, home);
        await NamesAsync(source, home);
        await Task.Delay(100);
        Assert.AreEqual(0, fake.Requests.Count, string.Join("; ", fake.Requests));

        // a walk over the space, quiet: the search for its stamp and no listing
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        Assert.AreEqual(0, fake.Count("/child/"), string.Join("; ", fake.Requests));
        Assert.AreEqual(0, fake.Count("/content/" + fake.PageTitled("AnyFS Lab Home").Id), string.Join("; ", fake.Requests));

        // something changed in the space: the next walk shows it
        fake.AddPage(fake.Spaces[0], fake.Spaces[0].Home, "A page made since", "<p>New.</p>");
        CollectionAssert.DoesNotContain(await NamesAsync(source, home), "A page made since");
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        CollectionAssert.Contains(await NamesAsync(source, home), "A page made since");
    }

    [TestMethod]
    public async Task ThePasswordGoesThroughTheLoginFormOnAnOlderServer()
    {
        var fake = ConfluenceSeed.Lab();
        fake.TwoStepLogin = false;
        var settings = new ConfluenceSettings { Address = fake.Server, SignInMethod = AtlassianSignInChoices.Password, Username = "tester", Password = ConfluenceSeed.Password };
        await using var source = await StartAsync(fake, settings);
        Assert.AreEqual("tester", source.Session.User?.Name);
        Assert.AreEqual(1, fake.Count("POST /confluence/dologin.action"));
    }

    [TestMethod]
    public async Task TheAnonymousUserIsNoSignIn()
    {
        var fake = ConfluenceSeed.Lab();
        fake.Anonymous = true;
        var settings = new ConfluenceSettings { Address = fake.Server, SignInMethod = AtlassianSignInChoices.WebSession };
        var source = new ConfluenceSource(settings, fake.StandIn((_, _) => Task.FromResult<IReadOnlyList<SessionCookie>>([])));
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        StringAssert.Contains(ex.Message, "does not take its session");
        await source.DisposeAsync();
    }

    // --- the storage format --------------------------------------------------------------------------------------------

    [TestMethod]
    public void StorageThatIsNotWellFormedIsKeptAsItsText()
    {
        var context = new StorageContext(new Uri("https://c.example.com/"), "KEY", "1", _ => null, _ => null);
        var html = ConfluenceStorage.ToHtml("<p>Unclosed <b>bold &amp; more</p>", context);
        StringAssert.Contains(AtlassianMarkdown.FromHtml(html, null), "Unclosed bold & more");
        Assert.AreEqual("&#8212;&#160;&amp;bogus;&lt;", ConfluenceStorage.NumericEntities("&mdash;&nbsp;&bogus;&lt;"));

        // a code macro's body is literal text: an HTML sample in it keeps its entities as written
        Assert.AreEqual("<![CDATA[&nbsp;&copy;\n]]>&#160;", ConfluenceStorage.NumericEntities("<![CDATA[&nbsp;&copy;\n]]>&nbsp;"));
        var code = ConfluenceStorage.ToHtml("<ac:structured-macro ac:name=\"code\"><ac:plain-text-body><![CDATA[<p>&nbsp;</p>]]></ac:plain-text-body></ac:structured-macro>", context);
        StringAssert.Contains(AtlassianMarkdown.FromHtml(code, null), "<p>&nbsp;</p>");
    }

    [TestMethod]
    public void AnotherPagesAttachmentAndABlogPostLinkInConfluence()
    {
        var context = new StorageContext(new Uri("https://c.example.com/wiki/"), "KEY", "1", _ => null, key => key == "k1" ? "Ann" : null);
        var md = AtlassianMarkdown.FromHtml(ConfluenceStorage.ToHtml(
            "<p><ac:link><ri:attachment ri:filename=\"a.pdf\"><ri:page ri:content-title=\"Other Page\" /></ri:attachment></ac:link> "
            + "<ac:link><ri:blog-post ri:content-title=\"News\" ri:posting-day=\"2026/10/30\" /></ac:link> <ac:link><ri:user ri:userkey=\"k2\" /></ac:link></p>", context), null);
        StringAssert.Contains(md, "[a.pdf](https://c.example.com/wiki/display/KEY/Other+Page)");
        StringAssert.Contains(md, "[News](https://c.example.com/wiki/display/KEY/2026/10/30/News)");
        StringAssert.Contains(md, "@k2", "an unknown person shows their key");
    }

    // --- the walk ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheWalkListsEachSpaceWholeAsBrowsingWould()
    {
        var fake = ConfluenceSeed.Lab();
        await using var walked = await StartAsync(fake, ConfluenceSeed.Settings(fake));
        await walked.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Complete, walked.Counters.State);

        await using var browsed = await StartAsync(fake, ConfluenceSeed.Settings(fake));
        var pending = new Queue<string>([""]);
        var folders = 0;
        while (pending.TryDequeue(out var path))
        {
            var fromWalk = (await walked.Listings.GetAsync(path, CancellationToken.None)).Select(e => $"{e.Name}|{e.IsDirectory}|{(e.IsDirectory ? 0 : e.Length)}").ToList();
            var fromBrowse = (await EntriesAsync(browsed, path)).Select(e => $"{e.Name}|{e.IsDirectory}|{(e.IsDirectory ? 0 : e.Length)}").ToList();
            CollectionAssert.AreEqual(fromBrowse, fromWalk, $"'{path}' as browsed and as walked");
            foreach (var folder in (await EntriesAsync(browsed, path)).Where(e => e.IsDirectory))
            {
                pending.Enqueue(VfsPath.Join(path, folder.Name));
            }

            folders++;
        }

        Assert.IsTrue(folders > 130, $"{folders} folders compared");
    }

    [TestMethod]
    public async Task AChangedSpaceRelistsOnlyThePagesThatChanged()
    {
        var fake = ConfluenceSeed.Lab();
        var now = DateTimeOffset.UtcNow;
        foreach (var c in fake.Contents)
        {
            c.Created = c.Changed = now.AddDays(-1);
        }

        foreach (var a in fake.Attachments)
        {
            a.Changed = now.AddDays(-1);
        }

        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake, "/display/ANYFS"));
        await source.CountFilesAsync(CancellationToken.None);
        var scans = fake.Count("/rest/api/content/scan");
        Assert.IsTrue(scans > 0);

        // a comment on one page and an edit of another: those two listed again, the space not read whole
        fake.AddComment(fake.PageTitled("Onboarding"), "other", "<p>Fresh comment.</p>", now);
        var services = fake.PageTitled("Services");
        services.Storage = "<p>The services, edited.</p>";
        services.Version++;
        services.Changed = now;
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        Assert.AreEqual(scans, fake.Count("/rest/api/content/scan"), "not read whole again");
        var onboarding = "AnyFS Lab Home/Onboarding";
        var md = await TextAsync(source, onboarding + "/Onboarding.md");
        StringAssert.Contains(md, "Fresh comment.");
        Assert.AreEqual(Encoding.UTF8.GetByteCount(md), (await source.Listings.GetAsync(onboarding, CancellationToken.None)).Single(e => e.Name == "Onboarding.md").Length, "the kept size is the new file's");
        StringAssert.Contains(await TextAsync(source, "AnyFS Lab Home/Architecture/Services/Services.md"), "edited");

        // a new page moves the tree: the space is read whole
        fake.AddPage(fake.Spaces[0], fake.Spaces[0].Home, "A page made since", "<p>New.</p>").Created = now;
        fake.PageTitled("A page made since").Changed = now;
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        Assert.IsTrue(fake.Count("/rest/api/content/scan") > scans, "read whole");
        CollectionAssert.Contains((await source.Listings.GetAsync("AnyFS Lab Home", CancellationToken.None)).Select(e => e.Name).ToList(), "A page made since");
    }

    [TestMethod]
    public async Task ASpacesStampMovesWithAnyChangeInIt()
    {
        var fake = ConfluenceSeed.Lab();
        await using var source = await StartAsync(fake, ConfluenceSeed.Settings(fake));
        var stamp = await source.StampAsync("AnyFS Lab (ANYFS)", null, CancellationToken.None);
        Assert.IsNotNull(stamp);
        Assert.AreEqual(stamp, await source.StampAsync("AnyFS Lab (ANYFS)", stamp, CancellationToken.None));
        fake.AddComment(fake.PageTitled("Onboarding"), "other", "<p>New.</p>", new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
        Assert.AreNotEqual(stamp, await source.StampAsync("AnyFS Lab (ANYFS)", stamp, CancellationToken.None));
        fake.Requests.Clear();
        Assert.IsNull(await source.StampAsync("AnyFS Lab (ANYFS)/AnyFS Lab Home", null, CancellationToken.None), "a page's folder goes by its space, and asks nothing");
        Assert.AreEqual(0, fake.Count("/rest/api/search"));
        Assert.IsNull(await source.StampAsync("", null, CancellationToken.None), "the list of spaces has none");
    }

    // --- the plugin ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public void ThePluginNamesValidatesAndShowsTheSignInsFields()
    {
        var plugin = new ConfluencePlugin();
        Assert.AreEqual("confluence.example.com", plugin.SuggestName(new ConfluenceSettings { Address = "https://confluence.example.com" }));
        Assert.AreEqual("ENG (confluence.example.com)", plugin.SuggestName(new ConfluenceSettings { Address = "https://confluence.example.com/display/ENG" }));
        Assert.AreEqual("Some Page (example.com)", plugin.SuggestName(new ConfluenceSettings { Address = "https://example.com/wiki/display/ENG/Some+Page" }));
        var s = new ConfluenceSettings { Address = "https://confluence.example.com", SignInMethod = AtlassianSignInChoices.WebSession };
        Assert.IsTrue(plugin.IsVisible(s, nameof(ConfluenceSettings.BrowserPath)));
        Assert.IsFalse(plugin.IsVisible(s, nameof(ConfluenceSettings.Password)));
        Assert.IsFalse(plugin.Validate(s).Any(m => m.IsError));
    }

    [TestMethod]
    public async Task TheTestSaysWhoAndWhat()
    {
        var fake = ConfluenceSeed.Lab();
        var result = await ConfluencePlugin.TestAsync(ConfluenceSeed.Settings(fake, "/display/ANYFS"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "the space AnyFS Lab (ANYFS)");
        var secret = await ConfluencePlugin.TestAsync(ConfluenceSeed.Settings(fake, "/display/SECRET"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsFalse(secret.Success);
    }
}
