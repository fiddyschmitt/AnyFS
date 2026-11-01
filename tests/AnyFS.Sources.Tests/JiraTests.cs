using System.Text;
using System.Text.Json.Nodes;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Atlassian;
using AnyFS.Sources.Atlassian.Jira;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>The lab's Jira as the fake holds it (spec 7.10): the accounts, projects, issues and attachments the tests read.</summary>
internal static class JiraSeed
{
    public const string Token = "jira-tester-token";
    public const string OtherToken = "jira-other-token";
    public const string Password = "tester-password";

    public static readonly DateTimeOffset Day = new(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);

    /// <summary>A megabyte and a bit, the same every time: the large attachment.</summary>
    public static readonly byte[] Large = Enumerable.Range(0, (1 << 20) + 77).Select(i => (byte)(i * 13 % 251)).ToArray();

    public static FakeJira Lab(string root = "/jira")
    {
        var fake = new FakeJira(root: root);
        fake.AddAccount("tester", "Test Er", Token, Password);
        fake.AddAccount("other", "Someone Else", OtherToken);

        var anyfs = fake.AddProject("ANYFS", "AnyFS Lab");
        anyfs.Lead = "tester";
        anyfs.Description = "The lab's own project.";
        anyfs.Components.Add(("Core", "tester", "The core."));
        anyfs.Versions.Add(("1.0", true, "2026-09-01"));
        anyfs.Versions.Add(("1.1", false, null));

        var story = fake.AddIssue(anyfs, "List a project's issues", i =>
        {
            i.Type = "Story";
            i.Status = "In Progress";
            i.StatusCategory = "indeterminate";
            i.Priority = "High";
            i.Assignee = "tester";
            i.Created = Day;
            i.Updated = Day.AddHours(5);
            i.Labels.AddRange(["listing", "jira"]);
            i.Components.Add("Core");
            i.FixVersions.Add("1.1");
            i.Description = "h2. What happens\n\nThe mount *stops*.";
        });
        var diagram = fake.AddAttachment(story, "diagram.png", [137, 80, 78, 71, 1, 2, 3], Day.AddHours(1));
        story.DescriptionHtml = $"<h2>What happens</h2><p>The mount <b>stops</b> answering.</p><ol><li>Mount</li><li>Wait</li></ol>"
            + $"<table><tr><th>Setting</th><th>Value</th></tr><tr><td>Requests</td><td>8</td></tr></table>"
            + $"<pre>var x = 1;</pre><p>See <span class=\"image-wrap\"><img src=\"/jira/secure/attachment/{diagram.Id}/{diagram.Id}_diagram.png\" /></span> and <a href=\"https://example.com/docs\">the docs</a>.</p>";
        story.Custom["customfield_10100"] = ("Story Points", JsonValue.Create(5), null);
        story.Custom["customfield_10101"] = ("Team", new JsonObject { ["value"] = "Platform", ["child"] = new JsonObject { ["value"] = "Storage" } }, null);
        story.Custom["customfield_10102"] = ("Sprint", new JsonArray((JsonNode)"com.atlassian.greenhopper.service.sprint.Sprint@1a2b[id=1,rapidViewId=1,state=ACTIVE,name=Sprint 4,startDate=2026-10-19]"), null);
        story.Custom["customfield_10103"] = ("Acceptance criteria", JsonValue.Create("* lists\n* fast"), "<ul><li>lists</li><li>fast</li></ul>");
        story.Custom["customfield_10104"] = ("Empty field", null, null);
        fake.AddComment(story, "tester", "The first comment.", "<p>The first <b>comment</b>.</p>", Day.AddHours(2));
        fake.AddComment(story, "other", "A second comment.", "<p>A second comment.</p>", Day.AddHours(3));

        var bug = fake.AddIssue(anyfs, "Fix: path \"C:\\temp\\a*b?\" <fails> | sometimes", i => { i.Type = "Bug"; i.Created = Day; i.Updated = Day.AddHours(1); });
        bug.Links.Add(("Blocks", "blocks", "is blocked by", story, true));
        story.Links.Add(("Blocks", "blocks", "is blocked by", bug, false));

        var task = fake.AddIssue(anyfs, "Attachments with the same name", i => { i.Created = Day; i.Updated = Day.AddHours(2); });
        fake.AddAttachment(task, "notes.txt", Encoding.UTF8.GetBytes("first notes\n"), Day.AddHours(1));
        fake.AddAttachment(task, "notes.txt", Encoding.UTF8.GetBytes("second notes, newer\n"), Day.AddHours(2));
        fake.AddAttachment(task, "large.bin", Large, Day.AddHours(3));

        fake.AddIssue(anyfs, "A sub-task of the story", i => { i.Type = "Sub-task"; i.Parent = story; i.Created = Day; i.Updated = Day; });
        fake.AddIssue(anyfs, "An issue already done", i => { i.StatusCategory = "done"; i.Status = "Done"; i.Resolution = "Fixed"; i.Created = Day; i.Updated = Day; });

        var ops = fake.AddProject("OPS", "Operations");
        var many = fake.AddIssue(ops, "An issue with a hundred comments", i => { i.Created = Day; i.Updated = Day; });
        for (var n = 1; n <= 100; n++)
        {
            fake.AddComment(many, "tester", $"Comment number {n}.", $"<p>Comment number {n}.</p>", Day.AddMinutes(n));
        }

        fake.AddIssue(fake.AddProject("HIDDEN", "Hidden from the tester", "other"), "A secret issue");
        var bulk = fake.AddProject("BULK", "Bulk issues");
        for (var n = 1; n <= 250; n++)
        {
            fake.AddIssue(bulk, $"Bulk issue {n}", i => { i.Created = Day; i.Updated = Day; });
        }

        fake.Filters[10200] = ("Operations", "project = OPS ORDER BY key ASC");
        fake.Boards[7] = 10200;
        return fake;
    }

    public static JiraSettings Settings(FakeJira fake, string path = "", string token = Token) => new()
    {
        Address = fake.Server + path,
        AccessToken = token,
        RefreshSeconds = 60,
        Concurrency = 4,
    };
}

/// <summary>The Jira source against <see cref="FakeJira"/> (spec 7.10): the address, the sign-ins, the tree, the files and the walk.</summary>
[TestClass]
public class JiraTests
{
    private static async Task<JiraSource> StartAsync(FakeJira fake, JiraSettings settings, TestSourceContext? context = null, AtlassianStandIn? standIn = null)
    {
        var source = new JiraSource(settings, standIn ?? fake.StandIn());
        await source.InitialiseAsync(context ?? new TestSourceContext(), CancellationToken.None);
        return source;
    }

    private static async Task<List<string>> NamesAsync(ISource source, string path)
    {
        var names = new List<string>();
        await foreach (var e in source.ListAsync(path, CancellationToken.None))
        {
            names.Add(e.Name);
        }

        return names;
    }

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
    [DataRow("", "Server", "")]
    [DataRow("projects/ABC", "Project", "ABC")]
    [DataRow("projects/abc/issues/ABC-12", "Project", "ABC")]
    [DataRow("browse/ABC", "Project", "ABC")]
    [DataRow("browse/ABC-123", "Project", "ABC")]
    [DataRow("plugins/servlet/project-config/OPS/summary", "Project", "OPS")]
    [DataRow("projects/ABC/issues/?filter=allopenissues", "Project", "ABC")]
    [DataRow("issues/?filter=10200", "Filter", "10200")]
    [DataRow("secure/RapidBoard.jspa?rapidView=12&projectKey=ABC", "Board", "12")]
    [DataRow("issues/?jql=project%20%3D%20ABC%20AND%20status%20%3D%20Open", "Jql", "project = ABC AND status = Open")]
    [DataRow("secure/Dashboard.jspa", "Server", "")]
    public void TheAddressNamesTheScope(string rest, string kind, string value)
    {
        var address = AtlassianAddress.Candidates(new Uri("https://jira.example.com/" + rest)).First();
        var scope = JiraScope.From(address, out var problem);
        Assert.IsNotNull(scope, problem);
        Assert.AreEqual(kind, scope.Kind.ToString());
        Assert.AreEqual(value, scope.Value);
    }

    [TestMethod]
    public void AnAddressThatNamesNothingSaysWhat()
    {
        var address = AtlassianAddress.Candidates(new Uri("https://jira.example.com/secure/admin/ViewApplicationProperties.jspa")).First();
        Assert.IsNull(JiraScope.From(address, out var problem));
        StringAssert.Contains(problem, "a project's");
        Assert.IsNull(JiraScope.From(AtlassianAddress.Candidates(new Uri("https://jira.example.com/issues/?filter=-1")).First(), out problem));
        StringAssert.Contains(problem, "save it as a filter");
        Assert.IsNull(JiraScope.From(AtlassianAddress.Candidates(new Uri("https://jira.example.com/issues/?filter=myopenissues")).First(), out problem));
        StringAssert.Contains(problem, "save it as a filter");
    }

    [TestMethod]
    public void FolderNamesGiveBackTheirKeys()
    {
        Assert.AreEqual("ABC-12", JiraScope.KeyOfFolder("ABC-12 Install fails"));
        Assert.AreEqual("ABC-12", JiraScope.KeyOfFolder("abc-12"));
        Assert.IsNull(JiraScope.KeyOfFolder("project.md"));
        Assert.AreEqual("ABC", JiraScope.KeyOfProjectFolder("Alpha (project) (ABC)"));
        Assert.IsNull(JiraScope.KeyOfProjectFolder("Alpha"));
        Assert.AreEqual("(project = OPS)", JiraScope.Condition("project = OPS ORDER BY key ASC"));
        Assert.AreEqual("", JiraScope.Condition("ORDER BY created DESC"));
    }

    [TestMethod]
    public async Task AnInstallationAtAContextPathIsFoundBelowTheHost()
    {
        var fake = JiraSeed.Lab();
        await using var source = await StartAsync(fake, JiraSeed.Settings(fake, "/projects/ANYFS"));
        Assert.AreEqual("https://jira.fake.test/jira/", source.Session.Address.Base.AbsoluteUri);
        Assert.AreEqual("11.3.12", source.Session.Version);
        Assert.AreEqual(JiraScopeKind.Project, source.Scope.Kind);
    }

    // --- signing in ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ARefusedTokenFailsForGood()
    {
        var fake = JiraSeed.Lab();
        var source = new JiraSource(JiraSeed.Settings(fake, token: "wrong"), fake.StandIn());
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        Assert.IsTrue(ex.Permanent);
        StringAssert.Contains(ex.Message, "refused the personal access token");
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task APasswordSignsInThroughTheTwoStepLoginAndItsSessionIsUsed()
    {
        var fake = JiraSeed.Lab();
        var settings = new JiraSettings { Address = fake.Server, SignInMethod = AtlassianSignInChoices.Password, Username = "tester", Password = JiraSeed.Password };
        await using var source = await StartAsync(fake, settings);
        Assert.AreEqual("tester", source.Session.User?.Name);
        Assert.AreEqual(1, fake.Count("POST /jira/rest/tsv/latest/authenticate"));
        Assert.AreEqual(0, fake.Count("login.jsp"));
        CollectionAssert.Contains(await NamesAsync(source, ""), "AnyFS Lab (ANYFS)");
    }

    [TestMethod]
    public async Task AnOlderServerTakesThePasswordThroughSeraphsForm()
    {
        var fake = JiraSeed.Lab();
        fake.TwoStepLogin = false;
        var settings = new JiraSettings { Address = fake.Server, SignInMethod = AtlassianSignInChoices.Password, Username = "tester", Password = JiraSeed.Password };
        await using var source = await StartAsync(fake, settings);
        Assert.AreEqual("tester", source.Session.User?.Name);
        Assert.AreEqual(1, fake.Count("POST /jira/login.jsp"));
    }

    [TestMethod]
    public async Task WithoutALoginFormBasicAuthenticationIsTriedWhereAllowed()
    {
        var fake = JiraSeed.Lab();
        fake.TwoStepLogin = false;
        fake.LoginForm = false;
        fake.BasicAllowed = true;
        var settings = new JiraSettings { Address = fake.Server, SignInMethod = AtlassianSignInChoices.Password, Username = "tester", Password = JiraSeed.Password };
        await using var source = await StartAsync(fake, settings);
        StringAssert.Contains(source.Session.How, "Basic");
        CollectionAssert.Contains(await NamesAsync(source, ""), "AnyFS Lab (ANYFS)");

        fake.BasicAllowed = false;
        var refused = new JiraSource(settings, fake.StandIn());
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => refused.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        StringAssert.Contains(ex.Message, "single sign-on");
        StringAssert.Contains(ex.Message, "turned off");
        await refused.DisposeAsync();
    }

    [TestMethod]
    public async Task AWrongPasswordIsRefusedOnceAndNotGivenAgain()
    {
        var fake = JiraSeed.Lab();
        var settings = new JiraSettings { Address = fake.Server, SignInMethod = AtlassianSignInChoices.Password, Username = "tester", Password = "wrong" };
        var source = new JiraSource(settings, fake.StandIn());
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        Assert.IsTrue(ex.Permanent);
        StringAssert.Contains(ex.Message, "refused the user name and password");
        Assert.IsTrue(fake.FailedLogins <= 2, $"{fake.FailedLogins} failed sign-ins: a server asks for a CAPTCHA after three");
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task AWebSessionTakesTheBrowsersCookiesAndRenewsThemWhenTheSessionEnds()
    {
        var fake = JiraSeed.Lab();
        var signIns = 0;
        var standIn = fake.StandIn((_, _) => { signIns++; return Task.FromResult<IReadOnlyList<SessionCookie>>([fake.SessionFor("tester")]); });
        var settings = new JiraSettings { Address = fake.Server, SignInMethod = AtlassianSignInChoices.WebSession, RefreshSeconds = 0 };
        await using var source = await StartAsync(fake, settings, standIn: standIn);
        Assert.AreEqual(1, signIns);
        Assert.AreEqual("Test Er", source.Session.User?.DisplayName);

        fake.EndSessions();
        CollectionAssert.Contains(await NamesAsync(source, ""), "AnyFS Lab (ANYFS)");
        Assert.AreEqual(2, signIns, "the lapsed session was renewed through the browser once");
    }

    [TestMethod]
    public async Task ASessionServedAsTheAnonymousUserIsRenewedNotBelieved()
    {
        var fake = JiraSeed.Lab();
        fake.Anonymous = true;
        var signIns = 0;
        var standIn = fake.StandIn((_, _) => { signIns++; return Task.FromResult<IReadOnlyList<SessionCookie>>([fake.SessionFor("tester")]); });
        var settings = new JiraSettings { Address = fake.Server, SignInMethod = AtlassianSignInChoices.WebSession, RefreshSeconds = 0 };
        await using var source = await StartAsync(fake, settings, standIn: standIn);
        fake.EndSessions();
        CollectionAssert.Contains(await NamesAsync(source, ""), "AnyFS Lab (ANYFS)");
        Assert.AreEqual(2, signIns);
    }

    // --- the tree ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheServerShowsTheProjectsTheAccountCanBrowse()
    {
        var fake = JiraSeed.Lab();
        await using var source = await StartAsync(fake, JiraSeed.Settings(fake));
        CollectionAssert.AreEqual(new[] { "AnyFS Lab (ANYFS)", "Bulk issues (BULK)", "Operations (OPS)" }, await NamesAsync(source, ""));
    }

    [TestMethod]
    public async Task AProjectShowsItsProjectFileAndAFolderPerIssue()
    {
        var fake = JiraSeed.Lab();
        await using var source = await StartAsync(fake, JiraSeed.Settings(fake));
        var names = await NamesAsync(source, "AnyFS Lab (ANYFS)");
        CollectionAssert.AreEqual(new[]
        {
            "project.md", "ANYFS-1 List a project's issues", "ANYFS-2 Fix_ path _C__temp_a_b__ _fails_ _ sometimes", "ANYFS-3 Attachments with the same name",
            "ANYFS-4 A sub-task of the story", "ANYFS-5 An issue already done",
        }, names);

        var project = await TextAsync(source, "AnyFS Lab (ANYFS)/project.md");
        StringAssert.StartsWith(project, "# AnyFS Lab (ANYFS)\n");
        StringAssert.Contains(project, "**Lead:** Test Er (tester)");
        StringAssert.Contains(project, "- Core (lead Test Er (tester)): The core.");
        StringAssert.Contains(project, "- 1.0 (released 2026-09-01)");
        StringAssert.Contains(project, "- 1.1 (unreleased)");
    }

    [TestMethod]
    public async Task AnIssueIsOneMarkdownFileWithItsFieldsDescriptionAndComments()
    {
        var fake = JiraSeed.Lab();
        await using var source = await StartAsync(fake, JiraSeed.Settings(fake, "/projects/ANYFS"));
        CollectionAssert.AreEqual(new[] { "issue.md", "attachments" }, await NamesAsync(source, "ANYFS-1 List a project's issues"));
        var md = await TextAsync(source, "ANYFS-1 List a project's issues/issue.md");
        StringAssert.StartsWith(md, "# ANYFS-1 List a project's issues\n\n**Type:** Story  \n**Status:** In Progress  \n**Resolution:** Unresolved  \n**Priority:** High  \n");
        StringAssert.Contains(md, "**Assignee:** Test Er (tester)  \n");
        StringAssert.Contains(md, "**Labels:** listing, jira  \n");
        StringAssert.Contains(md, "**Components:** Core  \n");
        StringAssert.Contains(md, "**Fix versions:** 1.1  \n");
        StringAssert.Contains(md, "**Sub-tasks:** ANYFS-4 A sub-task of the story  \n");
        StringAssert.Contains(md, "**Links:** is blocked by ANYFS-2 Fix: path \"C:\\temp\\a*b?\" <fails> | sometimes  \n");
        StringAssert.Contains(md, "**Story Points:** 5  \n");
        StringAssert.Contains(md, "**Team:** Platform - Storage  \n");
        StringAssert.Contains(md, "**Sprint:** Sprint 4  \n");
        Assert.IsFalse(md.Contains("Rank", StringComparison.Ordinal), "Jira's own rank is left out");
        Assert.IsFalse(md.Contains("Empty field", StringComparison.Ordinal), "a field without a value is left out");
        StringAssert.Contains(md, "**Web:** https://jira.fake.test/jira/browse/ANYFS-1  \n\n---\n\n");
        StringAssert.Contains(md, "## What happens");
        StringAssert.Contains(md, "The mount **stops** answering.");
        StringAssert.Contains(md, "| Setting | Value |");
        StringAssert.Contains(md, "![](attachments/diagram.png)", "the image points at the attachment beside the file");
        StringAssert.Contains(md, "[the docs](https://example.com/docs)");
        StringAssert.Contains(md, "## Acceptance criteria\n\n- lists\n- fast");
        StringAssert.Contains(md, "## Comments\n\n### Test Er (tester), ");
        StringAssert.Contains(md, "The first **comment**.");
        StringAssert.Contains(md, "### Someone Else (other), ");
        Assert.IsTrue(md.IndexOf("The first", StringComparison.Ordinal) < md.IndexOf("A second", StringComparison.Ordinal), "oldest first");
        Assert.IsFalse(md.Contains('\r'), "\\n line ends");
        Assert.AreEqual((await source.StatAsync("ANYFS-1 List a project's issues/issue.md", CancellationToken.None))!.Length, Encoding.UTF8.GetByteCount(md));
    }

    [TestMethod]
    public async Task WithoutCommentsTheIssueEndsAtItsDescription()
    {
        var fake = JiraSeed.Lab();
        var settings = JiraSeed.Settings(fake, "/projects/ANYFS");
        settings.Comments = false;
        settings.Attachments = false;
        await using var source = await StartAsync(fake, settings);
        CollectionAssert.AreEqual(new[] { "issue.md" }, await NamesAsync(source, "ANYFS-1 List a project's issues"));
        var md = await TextAsync(source, "ANYFS-1 List a project's issues/issue.md");
        Assert.IsFalse(md.Contains("## Comments", StringComparison.Ordinal));
        StringAssert.Contains(md, "/jira/secure/attachment/", "without the attachments folder the image points at Jira");
    }

    [TestMethod]
    public async Task AttachmentsKeepTheirNamesATwinNumberedByAge()
    {
        var fake = JiraSeed.Lab();
        await using var source = await StartAsync(fake, JiraSeed.Settings(fake, "/projects/ANYFS"));
        var folder = "ANYFS-3 Attachments with the same name/attachments";
        CollectionAssert.AreEqual(new[] { "notes.txt", "notes (2).txt", "large.bin" }, await NamesAsync(source, folder));
        Assert.AreEqual("first notes\n", await TextAsync(source, folder + "/notes.txt"));
        Assert.AreEqual("second notes, newer\n", await TextAsync(source, folder + "/notes (2).txt"));
        CollectionAssert.AreEqual(JiraSeed.Large, await ReadAsync(source, folder + "/large.bin"));
        CollectionAssert.AreEqual(JiraSeed.Large[1000..], await ReadAsync(source, folder + "/large.bin", 1000));
    }

    [TestMethod]
    public async Task AnOpenReadsTheIssueAndItsNeighboursWithoutListingTheProject()
    {
        var fake = JiraSeed.Lab();
        await using var source = await StartAsync(fake, JiraSeed.Settings(fake, "/projects/BULK"));
        var searches = fake.Count("/rest/api/2/search");
        for (var n = 101; n <= 150; n++)
        {
            StringAssert.StartsWith(await TextAsync(source, $"BULK-{n} Bulk issue {n}/issue.md"), $"# BULK-{n} Bulk issue {n}\n");
        }

        Assert.AreEqual(1, fake.Count("/rest/api/2/search") - searches, "fifty issues, one search: the hundred around the first");
        Assert.AreEqual(0, fake.Count("GET /jira/rest/api/2/issue/"), "none read alone");
    }

    [TestMethod]
    public async Task AnIssueTheRangeCannotFindIsReadAlone()
    {
        var fake = JiraSeed.Lab();
        fake.Issues.RemoveAll(i => i.Key == "BULK-101");
        await using var source = await StartAsync(fake, JiraSeed.Settings(fake, "/projects/BULK"));
        StringAssert.StartsWith(await TextAsync(source, "BULK-102 Bulk issue 102/issue.md"), "# BULK-102");
    }

    [TestMethod]
    public async Task AStaleNameIsNotFound()
    {
        var fake = JiraSeed.Lab();
        await using var source = await StartAsync(fake, JiraSeed.Settings(fake, "/projects/ANYFS"));
        await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync("ANYFS-1 An old summary/issue.md", VfsOpenMode.Read, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync("ANYFS-99 Nothing/issue.md", VfsOpenMode.Read, CancellationToken.None));
    }

    [TestMethod]
    public async Task IssuesArePagedByIdAHundredAndAThousandAtATime()
    {
        var fake = JiraSeed.Lab();
        fake.MaxResults = 100;
        await using var source = await StartAsync(fake, JiraSeed.Settings(fake, "/projects/BULK"));
        var names = await NamesAsync(source, "");
        Assert.AreEqual(251, names.Count, "project.md and 250 issues");
        var pages = fake.Searches.Where(q => q.EndsWith("ORDER BY id ASC", StringComparison.Ordinal)).ToList();
        CollectionAssert.AreEqual(
            new[] { "project = \"BULK\" ORDER BY id ASC", $"project = \"BULK\" AND id > {fake.IssueAt("BULK-100").Id} ORDER BY id ASC", $"project = \"BULK\" AND id > {fake.IssueAt("BULK-200").Id} ORDER BY id ASC" },
            pages, "three pages of a hundred by id, though a thousand were asked");
    }

    [TestMethod]
    public async Task CommentsBeyondWhatSearchCarriesAreAskedFor()
    {
        var fake = JiraSeed.Lab();
        fake.CommentsCarried = 20;
        await using var source = await StartAsync(fake, JiraSeed.Settings(fake, "/projects/OPS"));
        var md = await TextAsync(source, "OPS-1 An issue with a hundred comments/issue.md");
        StringAssert.Contains(md, "Comment number 1.");
        StringAssert.Contains(md, "Comment number 100.");
        Assert.AreEqual(100, md.Split("\n### ").Length - 1);
        Assert.IsTrue(fake.Count("/rest/api/2/issue/OPS-1/comment") >= 1);
    }

    [TestMethod]
    public async Task OpenOnlyLeavesDoneIssuesOut()
    {
        var fake = JiraSeed.Lab();
        var settings = JiraSeed.Settings(fake, "/projects/ANYFS");
        settings.OpenOnly = true;
        await using var source = await StartAsync(fake, settings);
        var names = await NamesAsync(source, "");
        Assert.IsFalse(names.Any(n => n.StartsWith("ANYFS-5", StringComparison.Ordinal)));
        Assert.AreEqual(5, names.Count);
    }

    [TestMethod]
    public async Task AFilterOrABoardShowsTheIssuesItFindsAtTheRoot()
    {
        var fake = JiraSeed.Lab();
        await using (var filter = await StartAsync(fake, JiraSeed.Settings(fake, "/issues/?filter=10200")))
        {
            CollectionAssert.AreEqual(new[] { "OPS-1 An issue with a hundred comments" }, await NamesAsync(filter, ""));
        }

        await using var board = await StartAsync(fake, JiraSeed.Settings(fake, "/secure/RapidBoard.jspa?rapidView=7"));
        CollectionAssert.AreEqual(new[] { "OPS-1 An issue with a hundred comments" }, await NamesAsync(board, ""));
    }

    [TestMethod]
    public async Task AQueryJiraCannotRunFailsTheStartInJirasWords()
    {
        var fake = JiraSeed.Lab();
        var source = new JiraSource(JiraSeed.Settings(fake, "/issues/?jql=" + Uri.EscapeDataString("nonsense ~ 1")), fake.StandIn());
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        StringAssert.Contains(ex.Message, "not one the fake knows");
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task AThrottledRequestWaitsAndIsAskedAgain()
    {
        var fake = JiraSeed.Lab();
        await using var source = await StartAsync(fake, JiraSeed.Settings(fake));
        fake.ThrottleNext = 1;
        CollectionAssert.Contains(await NamesAsync(source, ""), "Operations (OPS)");
    }

    [TestMethod]
    public async Task TheSourceIsReadOnly()
    {
        var fake = JiraSeed.Lab();
        await using var source = await StartAsync(fake, JiraSeed.Settings(fake, "/projects/ANYFS"));
        await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync("ANYFS-1 List a project's issues/issue.md", VfsOpenMode.ReadWrite, CancellationToken.None));
        Assert.IsFalse(source.Capabilities.CanWrite);
    }

    // --- the walk ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheCountWalksEveryProjectWholeAndKeepsWhatItListed()
    {
        var fake = JiraSeed.Lab();
        var context = new TestSourceContext();
        await using var source = await StartAsync(fake, JiraSeed.Settings(fake), context);
        await source.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State);

        // 3 project.md, 5 + 1 + 250 issue.md, 1 + 3 attachments
        Assert.AreEqual(3 + 256 + 4, source.Counters.FilesKnown);
        var requests = fake.Requests.Count;
        var md = await NamesAsync(source, "AnyFS Lab (ANYFS)/ANYFS-3 Attachments with the same name/attachments");
        Assert.AreEqual(3, md.Count);
        Assert.AreEqual(requests, fake.Requests.Count, "listed from what the walk kept");
    }

    [TestMethod]
    public async Task AWalkOverAQuietServerAsksTheStampsAlone()
    {
        var fake = JiraSeed.Lab();
        var settings = JiraSeed.Settings(fake);
        await using var source = await StartAsync(fake, settings);
        await source.CountFilesAsync(CancellationToken.None);
        var stamp = await source.StampAsync("AnyFS Lab (ANYFS)", null, CancellationToken.None);
        Assert.AreEqual($"{fake.IssueAt("ANYFS-1").Updated.UtcTicks}|5", stamp);

        // an issue changes: the project's stamp moves with it
        fake.IssueAt("ANYFS-2").Updated = JiraSeed.Day.AddDays(3);
        Assert.AreNotEqual(stamp, await source.StampAsync("AnyFS Lab (ANYFS)", stamp, CancellationToken.None));
        Assert.AreEqual(JiraSeed.Day.AddDays(3).UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            await source.StampAsync("AnyFS Lab (ANYFS)/ANYFS-2 Fix_ path _C__temp_a_b__ _fails_ _ sometimes", null, CancellationToken.None));
    }

    [TestMethod]
    public async Task AChangedProjectReadsOnlyWhatChangedSinceTheLastWalk()
    {
        var fake = JiraSeed.Lab();
        var now = DateTimeOffset.UtcNow;
        foreach (var issue in fake.Issues.Where(i => i.Project.Key == "BULK"))
        {
            issue.Updated = now.AddDays(-1);
        }

        fake.IssueAt("BULK-250").Updated = now.AddHours(-1);
        await using var source = await StartAsync(fake, JiraSeed.Settings(fake, "/projects/BULK"));
        await source.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(2, fake.Searches.Count(q => q.Contains(" AND id > ", StringComparison.Ordinal)), "read whole the first time: three pages of a hundred");

        var seven = fake.IssueAt("BULK-7");
        seven.Summary = "Bulk issue seven, renamed";
        seven.Updated = now;
        var before = fake.Searches.Count;
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        var asked = fake.Searches.Skip(before).ToList();
        Assert.IsTrue(asked.Any(q => q.Contains("updated >= -", StringComparison.Ordinal)), string.Join("\n", asked));
        Assert.IsFalse(asked.Any(q => q.Contains(" AND id > ", StringComparison.Ordinal)), "not read whole again: " + string.Join("\n", asked));
        var names = (await source.Listings.GetAsync("", CancellationToken.None)).Select(e => e.Name).ToList();
        CollectionAssert.Contains(names, "BULK-7 Bulk issue seven, renamed");
        CollectionAssert.DoesNotContain(names, "BULK-7 Bulk issue 7");
        Assert.AreEqual(251, names.Count);
        StringAssert.StartsWith(await TextAsync(source, "BULK-7 Bulk issue seven, renamed/issue.md"), "# BULK-7 Bulk issue seven, renamed\n");

        // a deleted issue leaves nothing to find by its update: the count says so, and the project is read whole
        fake.Issues.Remove(fake.IssueAt("BULK-9"));
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        names = (await source.Listings.GetAsync("", CancellationToken.None)).Select(e => e.Name).ToList();
        Assert.AreEqual(250, names.Count);
        CollectionAssert.DoesNotContain(names, "BULK-9 Bulk issue 9");
    }

    // --- the plugin ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public void ThePluginNamesValidatesAndShowsTheSignInsFields()
    {
        var plugin = new JiraPlugin();
        Assert.AreEqual("jira.example.com", plugin.SuggestName(new JiraSettings { Address = "https://jira.example.com" }));
        Assert.AreEqual("ABC (jira.example.com)", plugin.SuggestName(new JiraSettings { Address = "https://jira.example.com/browse/ABC-1" }));
        Assert.AreEqual("filter 10200 (example.com)", plugin.SuggestName(new JiraSettings { Address = "https://example.com/jira/issues/?filter=10200" }));

        var s = new JiraSettings { Address = "https://jira.example.com/projects/ABC/", SignInMethod = AtlassianSignInChoices.Password };
        plugin.NormaliseSettings(s);
        Assert.AreEqual("https://jira.example.com/projects/ABC", s.Address);
        Assert.IsTrue(plugin.IsVisible(s, nameof(JiraSettings.Username)));
        Assert.IsFalse(plugin.IsVisible(s, nameof(JiraSettings.AccessToken)));
        Assert.IsTrue(plugin.Validate(s).Any(m => m.Property == nameof(JiraSettings.Username)));
        Assert.IsTrue(plugin.Validate(new JiraSettings { Address = "not an address" }).Any(m => m.Property == nameof(JiraSettings.Address)));
        Assert.IsFalse(plugin.Validate(new JiraSettings { Address = "https://jira.example.com", AccessToken = "t" }).Any(m => m.IsError));
    }

    [TestMethod]
    public async Task TheTestSaysWhoAndWhat()
    {
        var fake = JiraSeed.Lab();
        var result = await JiraPlugin.TestAsync(JiraSeed.Settings(fake, "/projects/ANYFS"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "as Test Er (tester)");
        StringAssert.Contains(result.Message, "the project AnyFS Lab (ANYFS)");

        var hidden = await JiraPlugin.TestAsync(JiraSeed.Settings(fake, "/projects/HIDDEN"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsFalse(hidden.Success);
    }
}
