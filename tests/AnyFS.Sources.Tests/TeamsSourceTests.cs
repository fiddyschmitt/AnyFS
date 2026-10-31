using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Teams;

namespace AnyFS.Sources.Tests;

/// <summary>The Teams source against <see cref="FakeTeams"/> (spec 7.9): the tree of teams, channels, posts and chats, and the Markdown.</summary>
[TestClass]
public class TeamsSourceTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 28, 14, 3, 0, TimeSpan.Zero);
    private const string Jane = "8:orgid:00000000-0000-0000-0000-0000000jane";
    private const string John = "8:orgid:00000000-0000-0000-0000-0000000john";

    /// <summary>A fake with one team (two channels, posts in General) and two chats, as the sandbox seed will have.</summary>
    private static FakeTeams Seed()
    {
        var fake = new FakeTeams();
        fake.AddPerson(Jane, "Jane Doe");
        fake.AddPerson(John, "John Roe");
        var general = FakeTeams.Channel("General", general: true);
        var random = FakeTeams.Channel("Random");
        var team = fake.AddTeam("AnyFS Lab", general, random);

        // a thread with a reply, and a second thread
        fake.Post(general.Id, Jane, "Jane Doe", "<p>Build is green</p>", When);
        fake.Post(general.Id, John, "John Roe", "<p>Nice work</p>", When.AddMinutes(5), rootId: When.ToUnixTimeMilliseconds().ToString());
        fake.Post(general.Id, Jane, "Jane Doe", "<p>Lunch?</p>", When.AddHours(1));
        fake.Post(general.Id, John, null, "<addmember/>", When.AddHours(-1), messageType: "ThreadActivity/AddMember");

        var oneOnOne = fake.AddChat(ChatKind.OneOnOne, null, [Jane], When);
        fake.Post(oneOnOne.Id, Jane, "Jane Doe", "<p>Hello there</p>", When);
        var group = fake.AddChat(ChatKind.Group, "Project Axle", [Jane, John], When);
        fake.Post(group.Id, John, "John Roe", "<p>Kickoff Monday</p>", When);
        return fake;
    }

    private static async Task<TeamsSource> StartAsync(FakeTeams fake, Action<TeamsSettings>? change = null, int newest = 2_000, int pageSize = 200, TimeSpan? historyWait = null)
    {
        var settings = new TeamsSettings { Account = fake.Account };
        change?.Invoke(settings);
        var source = new TeamsSource(settings, fake.StandIn(newest, pageSize, historyWait));
        await source.InitialiseAsync(new TestSourceContext(), CancellationToken.None);
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

    private static async Task<string> TextAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length];
        await file.ReadFullyAsync(0, buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer);
    }

    [TestMethod]
    public async Task TheRootShowsTheTeamsAndTheChatsFolder()
    {
        await using var source = await StartAsync(Seed());
        CollectionAssert.AreEqual(new[] { "AnyFS Lab", "Chats", "Other groups", "all-groups.md", "groups.md" }, await NamesAsync(source, ""));
        CollectionAssert.AreEqual(new[] { "General", "Random", "team.md" }, await NamesAsync(source, "AnyFS Lab"));
        CollectionAssert.AreEqual(new[] { "posts", "files", "channel.md" }, await NamesAsync(source, "AnyFS Lab/General"));
    }

    private const string Site = "https://contoso.sharepoint.com/sites/lab";

    [TestMethod]
    public async Task AChannelsFilesAreItsFolderOfTheTeamsLibrary()
    {
        var fake = new FakeTeams();
        var general = FakeTeams.Channel("General", general: true);
        var team = fake.AddTeam("AnyFS Lab", general);
        fake.AddSite(team, Site);
        var plan = Encoding.UTF8.GetBytes("The plan, in full: " + new string('x', 5000));
        fake.AddLibraryFile(Site, "General/plan.txt", plan, When);
        fake.AddLibraryFile(Site, "General/Specs/design.md", Encoding.UTF8.GetBytes("# Design"), When);
        fake.AddLibraryFile(Site, "Random/elsewhere.txt", [1, 2, 3], When);
        await using var source = await StartAsync(fake);

        CollectionAssert.AreEqual(new[] { "plan.txt", "Specs" }, await NamesAsync(source, "AnyFS Lab/General/files"));
        CollectionAssert.AreEqual(new[] { "design.md" }, await NamesAsync(source, "AnyFS Lab/General/files/Specs"));
        Assert.AreEqual(plan.Length, (await source.StatAsync("AnyFS Lab/General/files/plan.txt", CancellationToken.None))!.Length);

        // read whole, and from an offset (a range)
        Assert.AreEqual(Encoding.UTF8.GetString(plan), await TextAsync(source, "AnyFS Lab/General/files/plan.txt"));
        await using var file = await source.OpenAsync("AnyFS Lab/General/files/plan.txt", VfsOpenMode.Read, CancellationToken.None);
        var tail = new byte[10];
        await file.ReadFullyAsync(plan.Length - 10, tail, CancellationToken.None);
        CollectionAssert.AreEqual(plan[^10..], tail);
    }

    [TestMethod]
    public async Task ASharedChannelsFilesAreTheFolderItsThreadNames()
    {
        // a shared or private channel keeps its files in a site of its own, which its thread names
        const string DesignSite = "https://contoso.sharepoint.com/sites/lab-Design";
        var fake = new FakeTeams();
        var design = FakeTeams.Channel("Design", priv: true) with { Shared = true };
        var team = fake.AddTeam("AnyFS Lab", FakeTeams.Channel("General", general: true), design);
        fake.AddSite(team, Site);
        fake.AddLibraryFile(Site, "Design/not-this.txt", [1], When);
        fake.AddLibraryFile(DesignSite, "Design/spec.md", Encoding.UTF8.GetBytes("# Spec"), When);
        fake.Threads[design.Id] = new ThreadDetails(DesignSite, "/sites/lab-Design/Shared Documents/Design", 3, 1, 1, null);
        await using var source = await StartAsync(fake);

        CollectionAssert.AreEqual(new[] { "spec.md" }, await NamesAsync(source, "AnyFS Lab/Design/files"), "not the team site's folder of the same name");
        Assert.AreEqual("# Spec", await TextAsync(source, "AnyFS Lab/Design/files/spec.md"));
    }

    [TestMethod]
    public async Task AThreadsSharedFilesAndImagesAreItsAttachmentsAndTheMarkdownLinksThem()
    {
        const string ImageSrc = "https://as-prod.asyncgw.teams.microsoft.com/v1/objects/0-sa-d1-abc/views/imgo";
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0 };
        var fake = new FakeTeams();
        var general = FakeTeams.Channel("General", general: true);
        var team = fake.AddTeam("AnyFS Lab", general);
        fake.AddSite(team, Site);
        fake.AddLibraryFile(Site, "General/report.docx", Encoding.UTF8.GetBytes("a report"), When);
        fake.AddImage(ImageSrc, png);
        var root = fake.Post(general.Id, Jane, "Jane Doe", "<p>The report</p>", When,
            files: [new SharedFile("report.docx", $"{Site}/Shared Documents/General/report.docx", $"{Site}/")]);
        fake.Post(general.Id, John, "John Roe", $"<p>A picture:</p><p><img itemscope=\"\" itemtype=\"http://schema.skype.com/AMSImage\" src=\"{ImageSrc}\" alt=\"image\"></p>", When.AddMinutes(1), rootId: root.Id);
        await using var source = await StartAsync(fake);

        var thread = (await NamesAsync(source, "AnyFS Lab/General/posts")).Single();
        CollectionAssert.AreEqual(new[] { "post.md", "attachments" }, await NamesAsync(source, $"AnyFS Lab/General/posts/{thread}"));
        var image = $"{When.AddMinutes(1).ToLocalTime():yyyy-MM-dd_HHmm}_image-1.png";
        CollectionAssert.AreEqual(new[] { "report.docx", image }, await NamesAsync(source, $"AnyFS Lab/General/posts/{thread}/attachments"));
        Assert.AreEqual("a report", await TextAsync(source, $"AnyFS Lab/General/posts/{thread}/attachments/report.docx"));

        var md = await TextAsync(source, $"AnyFS Lab/General/posts/{thread}/post.md");
        StringAssert.Contains(md, "- 📎 [report.docx](<attachments/report.docx>)");
        StringAssert.Contains(md, $"(attachments/{image})");
        Assert.IsFalse(md.Contains("asyncgw", StringComparison.Ordinal), "the image points at its copy, not the media service");
        Assert.AreEqual(1, fake.ImageCalls, "an image is read once, however often its thread is listed or opened");
    }

    [TestMethod]
    public async Task AnImageLetGoFromMemoryIsReadAgainWhenOpenedNotWhenListed()
    {
        // past the 64 MiB of images kept: let go as soon as it is read, its length known all the same
        const string ImageSrc = "https://as-prod.asyncgw.teams.microsoft.com/v1/objects/0-sa-d1-big/views/imgo";
        var big = new byte[(64 << 20) + 16];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 }.CopyTo(big, 0);
        var fake = new FakeTeams();
        var chat = fake.AddChat(ChatKind.Group, "Pictures", [Jane], When);
        fake.AddImage(ImageSrc, big);
        fake.Post(chat.Id, Jane, "Jane Doe", $"<p><img itemscope=\"\" itemtype=\"http://schema.skype.com/AMSImage\" src=\"{ImageSrc}\" alt=\"image\"></p>", When);
        await using var source = await StartAsync(fake, s => s.RefreshSeconds = 0); // every listing builds the chat again

        var image = $"{When.ToLocalTime():yyyy-MM-dd_HHmm}_image-1.png";
        CollectionAssert.AreEqual(new[] { image }, await NamesAsync(source, "Chats/Pictures/attachments"));
        CollectionAssert.AreEqual(new[] { image }, await NamesAsync(source, "Chats/Pictures/attachments"));
        Assert.AreEqual(1, fake.ImageCalls, "measured once, though no longer kept");
        await using var file = await source.OpenAsync($"Chats/Pictures/attachments/{image}", VfsOpenMode.Read, CancellationToken.None);
        Assert.AreEqual(big.Length, file.Length);
        Assert.AreEqual(2, fake.ImageCalls, "read again when opened");
    }

    [TestMethod]
    public async Task OtherGroupsAreTheGroupSitesBeyondTheTeamsEachWithItsLibrary()
    {
        var fake = new FakeTeams();
        var team = fake.AddTeam("AnyFS Lab", FakeTeams.Channel("General", general: true));
        fake.AddGroupSite(new GroupSite(team.GroupId!, "AnyFS Lab", Site, null, When));
        const string Mark8 = "https://contoso.sharepoint.com/sites/Mark8ProjectTeam";
        fake.AddGroupSite(new GroupSite("g-mark8", "Mark 8 Project Team", Mark8, "The Mark 8", When));
        fake.AddGroupSite(new GroupSite("g-retail", "Retail", "https://contoso.sharepoint.com/sites/Retail", null, When));
        fake.AddLibraryFile(Mark8, "Plan.docx", Encoding.UTF8.GetBytes("the plan"), When);
        fake.AddLibraryFile(Mark8, "Design/spec.md", Encoding.UTF8.GetBytes("# Spec"), When);
        await using var source = await StartAsync(fake);

        CollectionAssert.AreEqual(new[] { "Mark 8 Project Team", "Retail" }, await NamesAsync(source, "Other groups"), "not the team's own, which is above");
        CollectionAssert.AreEqual(new[] { "Design", "Plan.docx" }, await NamesAsync(source, "Other groups/Mark 8 Project Team"));
        Assert.AreEqual("the plan", await TextAsync(source, "Other groups/Mark 8 Project Team/Plan.docx"));
        Assert.AreEqual("# Spec", await TextAsync(source, "Other groups/Mark 8 Project Team/Design/spec.md"));
    }

    [TestMethod]
    public async Task AGroupWhoseLibraryRefusesTheAccountAnswersAtOnceEveryPassAfterTheFirst()
    {
        var fake = new FakeTeams();
        fake.AddTeam("AnyFS Lab", FakeTeams.Channel("General", general: true));
        const string Mark8 = "https://contoso.sharepoint.com/sites/Mark8ProjectTeam";
        const string Board = "https://contoso.sharepoint.com/sites/Board";
        fake.AddGroupSite(new GroupSite("g-mark8", "Mark 8 Project Team", Mark8, null, When));
        fake.AddGroupSite(new GroupSite("g-board", "Board", Board, null, When));
        fake.AddLibraryFile(Mark8, "Plan.docx", Encoding.UTF8.GetBytes("the plan"), When);
        fake.RefusedSites.Add(Board);
        await using var source = await StartAsync(fake);
        await source.CountFilesAsync(CancellationToken.None);

        // a search over the tree, pass after pass (FileLocator Pro's): the refusal the walk met answers each time
        var asked = fake.SharePointCalls;
        for (var pass = 0; pass < 3; pass++)
        {
            CollectionAssert.AreEqual(new[] { "Plan.docx" }, await NamesAsync(source, "Other groups/Mark 8 Project Team"));
            var refused = await Assert.ThrowsExactlyAsync<VfsException>(async () => await NamesAsync(source, "Other groups/Board"));
            Assert.AreEqual(VfsError.AccessDenied, refused.Error);
        }

        Assert.AreEqual(asked, fake.SharePointCalls, "SharePoint not asked again for the refused library");

        // the next walk asks again, and finds it opened since
        fake.RefusedSites.Clear();
        fake.AddLibraryFile(Board, "Minutes.docx", Encoding.UTF8.GetBytes("minutes"), When);
        await source.Listings.RefreshAsync("Other groups/Board", CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "Minutes.docx" }, await NamesAsync(source, "Other groups/Board"));
    }

    [TestMethod]
    public async Task ASharedFileThatRefusesTheAccountIsAskedForOnceAWalkInterval()
    {
        // a file shared from a OneDrive the account may not open (a 403; five of them a pass at work): left out, and not
        // asked for again while a search lists the conversation pass after pass
        const string Mine = "https://contoso-my.sharepoint.com/personal/jane_contoso_com";
        var fake = new FakeTeams();
        fake.AddPerson(Jane, "Jane Doe");
        var chat = fake.AddChat(ChatKind.OneOnOne, null, [Jane], When);
        fake.AddLibraryFile(Mine, "open.docx", Encoding.UTF8.GetBytes("open"), When);
        fake.AddLibraryFile(Mine, "closed.docx", Encoding.UTF8.GetBytes("closed"), When);
        fake.RefusedFiles.Add($"{Mine}|closed.docx");
        fake.Post(chat.Id, Jane, "Jane Doe", "<p>Two files</p>", When, files:
        [
            new SharedFile("open.docx", $"{Mine}/Documents/open.docx", null),
            new SharedFile("closed.docx", $"{Mine}/Documents/closed.docx", null),
        ]);
        await using var source = await StartAsync(fake);

        CollectionAssert.AreEqual(new[] { "open.docx" }, await NamesAsync(source, "Chats/Jane Doe/attachments"));
        var asked = fake.SharePointCalls;
        for (var pass = 0; pass < 3; pass++)
        {
            await source.Listings.RefreshAsync("Chats/Jane Doe", CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "open.docx" }, await NamesAsync(source, "Chats/Jane Doe/attachments"));
        }

        Assert.AreEqual(asked, fake.SharePointCalls, "the refused file is not asked for again (the other is kept for the refresh interval)");
    }

    [TestMethod]
    public async Task AnOpenServesTheFileItsListingNamedWithoutListingAgain()
    {
        // with nothing kept (refresh 0), finding a file from the root would list the structure and the chat again: an open
        // of a shared file or an image a listing named needs none of that
        var fake = new FakeTeams();
        fake.AddPerson(Jane, "Jane Doe");
        var chat = fake.AddChat(ChatKind.OneOnOne, null, [Jane], When);
        fake.AddLibraryFile(Site, "General/report.docx", Encoding.UTF8.GetBytes("a report"), When);
        const string ImageSrc = "https://as-prod.asyncgw.teams.microsoft.com/v1/objects/0-sa-d1-abc/views/imgo";
        fake.AddImage(ImageSrc, [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0]);
        fake.Post(chat.Id, Jane, "Jane Doe", $"<p>The report <img itemscope=\"\" itemtype=\"http://schema.skype.com/AMSImage\" src=\"{ImageSrc}\" alt=\"image\"></p>", When,
            files: [new SharedFile("report.docx", $"{Site}/Shared Documents/General/report.docx", $"{Site}/")]);
        await using var source = await StartAsync(fake, s => s.RefreshSeconds = 0);

        var image = $"{When.ToLocalTime():yyyy-MM-dd_HHmm}_image-1.png";
        CollectionAssert.AreEqual(new[] { "report.docx", image }, await NamesAsync(source, "Chats/Jane Doe/attachments"));
        var structures = fake.StructureCalls;
        var messages = fake.MessageCalls;
        Assert.AreEqual("a report", await TextAsync(source, "Chats/Jane Doe/attachments/report.docx"));
        await using var opened = await source.OpenAsync($"Chats/Jane Doe/attachments/{image}", VfsOpenMode.Read, CancellationToken.None);
        Assert.AreEqual(10, opened.Length);
        Assert.AreEqual((structures, messages), (fake.StructureCalls, fake.MessageCalls), "found among the listing's files, not from the root again");
    }

    [TestMethod]
    public async Task AllGroupsMdHasEveryGroupAndGroupsMdTheAccountsWithTheirDetails()
    {
        var fake = new FakeTeams();
        var team = fake.AddTeam("AnyFS Lab", FakeTeams.Channel("General", general: true));
        fake.Directory = new GroupsDirectory(
        [
            new GroupInfo(team.GroupId!, "AnyFS Lab") { Kind = "Microsoft 365 group", Visibility = "Private", Team = true, Mail = "anyfslab@contoso.com", Alias = "anyfslab", SiteUrl = Site, Members = 4, Created = When },
            new GroupInfo("g-board", "Board | Exec") { Kind = "Microsoft 365 group", Visibility = "Hidden membership", Team = false, Description = "The board,\nprivately" },
            new GroupInfo("g-sales", "Sales staff") { Kind = "Security group", Dynamic = true, MembershipRule = "(user.department -eq \"Sales\")", OnPremisesSynced = true },
            new GroupInfo("g-news", "All news") { Kind = "Distribution list", Mail = "news@contoso.com" },
        ],
            new HashSet<string>([team.GroupId!, "g-sales"]),
            new HashSet<string>([team.GroupId!]),
            GroupsScope.Organisation,
            "Microsoft Graph");
        fake.GraphReady = true;
        await using var source = await StartAsync(fake);

        var md = await UntilAsync(() => TextAsync(source, "all-groups.md"), m => !m.Contains("being read", StringComparison.Ordinal));
        StringAssert.StartsWith(md, "# All groups\n");
        StringAssert.Contains(md, "Every group in the organisation, as Microsoft Graph lists them for tester@contoso.com: 4 (");
        StringAssert.Contains(md, "| AnyFS Lab | Microsoft 365 group | Private | yes | owner | anyfslab@contoso.com |");
        StringAssert.Contains(md, "| Board \\| Exec | Microsoft 365 group | Hidden membership | no |  |  |", "a pipe in a name escaped; not joined");
        StringAssert.Contains(md, "| Sales staff | Security group |  |  | member |  |");
        StringAssert.Contains(md, "### Board | Exec\n\n- Kind: Microsoft 365 group\n- Visibility: Hidden membership\n- Description: The board, privately\n- Id: g-board");
        StringAssert.Contains(md, "- Membership: dynamic, by the rule (user.department -eq \"Sales\")");
        StringAssert.Contains(md, "- Synced from on-premises: yes");
        StringAssert.Contains(md, "- Kind: Microsoft 365 group, with a team\n- Visibility: Private\n- You: owner\n- Members: 4");

        // groups.md: the account's own, of every kind
        var mine = await UntilAsync(() => TextAsync(source, "groups.md"), m => !m.Contains("being read", StringComparison.Ordinal));
        StringAssert.StartsWith(mine, "# Your groups\n");
        StringAssert.Contains(mine, "The groups tester@contoso.com is in, as Microsoft Graph lists them: 2 (1 Microsoft 365 group, 1 Security group). It owns 1 of them.");
        Assert.IsFalse(mine.Contains("All news", StringComparison.Ordinal), "a group the account is not in");
    }

    [TestMethod]
    public async Task WhereGraphListsNoGroupsTheTeamsOwnSiteIsStillLeftOutOfOtherGroups()
    {
        // a Graph token without Group.Read.All: Microsoft Search's sites, named without their groups, told from the team's by its site
        const string Mark8 = "https://contoso.sharepoint.com/sites/Mark8ProjectTeam";
        var fake = new FakeTeams { SharePointReady = false, GraphReady = true, GraphListsNoGroups = true };
        var team = fake.AddTeam("AnyFS Lab", FakeTeams.Channel("General", general: true));
        fake.AddSite(team, Site);
        fake.AddGroupSite(new GroupSite(team.GroupId!, "AnyFS Lab", Site, null, When));
        fake.AddGroupSite(new GroupSite("g-mark8", "Mark 8 Project Team", Mark8, null, When));
        fake.AddLibraryFile(Mark8, "Plan.docx", Encoding.UTF8.GetBytes("the plan"), When);
        await using var source = await StartAsync(fake);

        CollectionAssert.AreEqual(new[] { "Mark 8 Project Team" }, await NamesAsync(source, "Other groups"));
        Assert.AreEqual("the plan", await TextAsync(source, "Other groups/Mark 8 Project Team/Plan.docx"));
        CollectionAssert.DoesNotContain(await UntilAsync(() => NamesAsync(source, ""), n => !n.Contains("all-groups.md")), "all-groups.md", "no list of every group to show: Graph refused it and SharePoint's token is not in hand");
    }

    /// <summary>What <paramref name="read"/> gives once <paramref name="done"/> holds (work behind the listings lands), or after five seconds.</summary>
    private static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var value = await read();
            if (done(value) || DateTime.UtcNow > deadline)
            {
                return value;
            }

            await Task.Delay(25);
        }
    }

    [TestMethod]
    public async Task WithoutSharePointsTokenNothingWaitsAndOnlyWhatOpensIsServed()
    {
        // the page has not handed out SharePoint's token (as at work, often): Other groups and the files folders are not
        // served, a shared file is left out of the attachments and groups.md holds the teams' own groups; nothing waits for
        // the token, nothing asks SharePoint without it, and the source stays healthy while the token is asked for behind
        var fake = new FakeTeams { SharePointReady = false };
        var general = FakeTeams.Channel("General", general: true);
        var team = fake.AddTeam("AnyFS Lab", general);
        fake.AddSite(team, Site);
        fake.AddLibraryFile(Site, "General/report.docx", Encoding.UTF8.GetBytes("a report"), When);
        fake.AddGroupSite(new GroupSite("g-retail", "Retail", "https://contoso.sharepoint.com/sites/Retail", null, When));
        fake.Post(general.Id, Jane, "Jane Doe", "<p>The report</p>", When, files: [new SharedFile("report.docx", $"{Site}/Shared Documents/General/report.docx", $"{Site}/")]);
        using var context = new TestSourceContext();
        await using var source = new TeamsSource(new TeamsSettings { Account = fake.Account }, fake.StandIn());
        await source.InitialiseAsync(context, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "AnyFS Lab", "Chats", "groups.md" }, await NamesAsync(source, ""));
        Assert.AreEqual(VfsError.NotFound, (await Assert.ThrowsExactlyAsync<VfsException>(() => NamesAsync(source, "Other groups"))).Error);
        CollectionAssert.AreEqual(new[] { "posts", "channel.md" }, await NamesAsync(source, "AnyFS Lab/General"));
        var thread = (await NamesAsync(source, "AnyFS Lab/General/posts")).Single();
        CollectionAssert.AreEqual(new[] { "post.md" }, await NamesAsync(source, $"AnyFS Lab/General/posts/{thread}"), "the shared file is left out, not waited for");
        StringAssert.Contains(await TextAsync(source, $"AnyFS Lab/General/posts/{thread}/post.md"), "- 📎 report.docx");
        var md = await UntilAsync(() => TextAsync(source, "groups.md"), m => !m.Contains("being read", StringComparison.Ordinal));
        StringAssert.Contains(md, "The groups of tester@contoso.com's own teams, from Teams: 1.");

        Assert.AreEqual(0, fake.SharePointCalls, "nothing asks for a file without a token to read it");
        Assert.IsTrue(await UntilAsync(() => Task.FromResult(fake.SeekCalls), n => n > 0) > 0, "the token is asked for behind the listings");
        Assert.IsFalse(context.HealthReports.Any(h => h.Health == ComponentHealth.Degraded), "a token not handed out is not the source's health");
    }

    [TestMethod]
    public async Task WhenSharePointsTokenComesOtherGroupsTheFilesAndGroupsMdFollow()
    {
        var hold = new TaskCompletionSource();
        var fake = new FakeTeams { SharePointReady = false, SharePointComesWhenAsked = true, SeekHold = hold.Task };
        var general = FakeTeams.Channel("General", general: true);
        var team = fake.AddTeam("AnyFS Lab", general);
        fake.AddSite(team, Site);
        fake.AddLibraryFile(Site, "General/plan.txt", Encoding.UTF8.GetBytes("the plan"), When);
        fake.AddGroupSite(new GroupSite("g-retail", "Retail", "https://contoso.sharepoint.com/sites/Retail", null, When));
        await using var source = await StartAsync(fake);

        // answered at once without the token, which is asked for behind the listings
        CollectionAssert.AreEqual(new[] { "AnyFS Lab", "Chats", "groups.md" }, await NamesAsync(source, ""));
        CollectionAssert.AreEqual(new[] { "posts", "channel.md" }, await NamesAsync(source, "AnyFS Lab/General"));

        // it comes: the kept listings of the root and the channel are listed afresh, all-groups.md read from SharePoint
        hold.SetResult();
        CollectionAssert.AreEqual(new[] { "posts", "files", "channel.md" }, await UntilAsync(() => NamesAsync(source, "AnyFS Lab/General"), n => n.Contains("files")));
        CollectionAssert.AreEqual(new[] { "AnyFS Lab", "Chats", "Other groups", "all-groups.md", "groups.md" }, await NamesAsync(source, ""));
        CollectionAssert.AreEqual(new[] { "plan.txt" }, await NamesAsync(source, "AnyFS Lab/General/files"));
        CollectionAssert.AreEqual(new[] { "Retail" }, await NamesAsync(source, "Other groups"));
        StringAssert.Contains(await UntilAsync(() => TextAsync(source, "all-groups.md"), m => m.Contains("from SharePoint", StringComparison.Ordinal)), "The groups tester@contoso.com can see, from SharePoint: 1,");
    }

    [TestMethod]
    public async Task WithGraphsTokenAloneTheFilesAndOtherGroupsAreReadThroughIt()
    {
        // GCC High at work: no SharePoint token, but Graph's (Files.ReadWrite.All, Sites.ReadWrite.All). The group sites come
        // from Microsoft Search without their groups, so the team's own is told by its site; a shared file is opened by its
        // own address
        const string Mark8 = "https://contoso.sharepoint.com/sites/Mark8ProjectTeam";
        var fake = new FakeTeams { SharePointReady = false, GraphReady = true };
        var general = FakeTeams.Channel("General", general: true);
        var team = fake.AddTeam("AnyFS Lab", general);
        fake.AddSite(team, Site);
        fake.AddGroupSite(new GroupSite(team.GroupId!, "AnyFS Lab", Site, null, When));
        fake.AddGroupSite(new GroupSite("g-mark8", "Mark 8 Project Team", Mark8, null, When));
        fake.AddLibraryFile(Mark8, "Plan.docx", Encoding.UTF8.GetBytes("the plan"), When);
        fake.AddLibraryFile(Site, "General/report.docx", Encoding.UTF8.GetBytes("a report"), When);
        var shared = $"{Site}/Shared Documents/General/report.docx";
        fake.Post(general.Id, Jane, "Jane Doe", "<p>The report</p>", When, files: [new SharedFile("report.docx", shared, $"{Site}/")]);
        fake.Directory = new GroupsDirectory([new GroupInfo(team.GroupId!, "AnyFS Lab")], new HashSet<string>([team.GroupId!]), new HashSet<string>(), GroupsScope.Organisation, "Microsoft Graph");
        using var context = new TestSourceContext();
        await using var source = new TeamsSource(new TeamsSettings { Account = fake.Account }, fake.StandIn());
        await source.InitialiseAsync(context, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "AnyFS Lab", "Chats", "Other groups", "all-groups.md", "groups.md" }, await NamesAsync(source, ""));
        CollectionAssert.AreEqual(new[] { "Mark 8 Project Team" }, await NamesAsync(source, "Other groups"), "the team's own site, found without its group, is still left out");
        Assert.AreEqual("the plan", await TextAsync(source, "Other groups/Mark 8 Project Team/Plan.docx"));
        CollectionAssert.AreEqual(new[] { "posts", "files", "channel.md" }, await NamesAsync(source, "AnyFS Lab/General"));
        CollectionAssert.AreEqual(new[] { "report.docx" }, await NamesAsync(source, "AnyFS Lab/General/files"));

        var thread = (await NamesAsync(source, "AnyFS Lab/General/posts")).Single();
        CollectionAssert.AreEqual(new[] { "report.docx" }, await NamesAsync(source, $"AnyFS Lab/General/posts/{thread}/attachments"));
        Assert.AreEqual("a report", await TextAsync(source, $"AnyFS Lab/General/posts/{thread}/attachments/report.docx"));
        CollectionAssert.Contains(fake.SharedUrlsAsked, shared, "the shared file is opened by its own address");
        Assert.AreEqual(0, fake.SeekCalls, "with Graph's token the files need nothing asked for");
        Assert.IsFalse(context.HealthReports.Any(h => h.Health == ComponentHealth.Degraded));
    }

    [TestMethod]
    public async Task WithGroupsOffThereIsNoOtherGroupsFolderNorGroupsMd()
    {
        var fake = Seed();
        fake.AddGroupSite(new GroupSite("g-retail", "Retail", "https://contoso.sharepoint.com/sites/Retail", null, When));
        await using var source = await StartAsync(fake, s => s.Groups = false);
        CollectionAssert.AreEqual(new[] { "AnyFS Lab", "Chats" }, await NamesAsync(source, ""));
    }

    [TestMethod]
    public async Task WithFilesOffThereAreNoFilesFoldersOrAttachments()
    {
        var fake = new FakeTeams();
        var general = FakeTeams.Channel("General", general: true);
        var team = fake.AddTeam("AnyFS Lab", general);
        fake.AddSite(team, Site);
        fake.AddLibraryFile(Site, "General/report.docx", [1], When);
        fake.Post(general.Id, Jane, "Jane Doe", "<p>The report</p>", When, files: [new SharedFile("report.docx", $"{Site}/Shared Documents/General/report.docx", null)]);
        await using var source = await StartAsync(fake, s => s.Files = false);

        CollectionAssert.AreEqual(new[] { "posts", "channel.md" }, await NamesAsync(source, "AnyFS Lab/General"));
        var thread = (await NamesAsync(source, "AnyFS Lab/General/posts")).Single();
        CollectionAssert.AreEqual(new[] { "post.md" }, await NamesAsync(source, $"AnyFS Lab/General/posts/{thread}"));
        StringAssert.Contains(await TextAsync(source, $"AnyFS Lab/General/posts/{thread}/post.md"), "- 📎 report.docx");
    }

    [TestMethod]
    public async Task AChannelPostIsAFolderOfTheOpeningMessageAndItsReplies()
    {
        await using var source = await StartAsync(Seed());
        var posts = await NamesAsync(source, "AnyFS Lab/General/posts");
        Assert.AreEqual(2, posts.Count, "two threads; the system AddMember message is not one");
        var build = posts.Single(p => p.Contains("Build is green", StringComparison.Ordinal));
        var stamp = When.ToLocalTime().ToString("yyyy-MM-dd HHmm", System.Globalization.CultureInfo.InvariantCulture);
        StringAssert.StartsWith(build, $"{stamp} Jane Doe", "named by time (local), author and subject");

        var md = await TextAsync(source, $"AnyFS Lab/General/posts/{build}/post.md");
        StringAssert.Contains(md, "**Jane Doe**");
        StringAssert.Contains(md, "Build is green");
        StringAssert.Contains(md, "**John Roe**");
        StringAssert.Contains(md, "Nice work");
        Assert.IsFalse(md.Contains("addmember", StringComparison.OrdinalIgnoreCase), "system activities are left out");
    }

    [TestMethod]
    public async Task ChatsAreNamedForTheirTopicOrTheOtherPersonAndReadAsOneFile()
    {
        await using var source = await StartAsync(Seed());
        var chats = await NamesAsync(source, "Chats");
        CollectionAssert.AreEquivalent(new[] { "Jane Doe", "Project Axle" }, chats);
        CollectionAssert.AreEqual(new[] { "chat.md" }, await NamesAsync(source, "Chats/Jane Doe"));

        var oneOnOne = await TextAsync(source, "Chats/Jane Doe/chat.md");
        StringAssert.Contains(oneOnOne, "# Jane Doe");
        StringAssert.Contains(oneOnOne, "Hello there");
        var group = await TextAsync(source, "Chats/Project Axle/chat.md");
        StringAssert.Contains(group, "Kickoff Monday");
    }

    [TestMethod]
    public async Task CountFilesWalksTheConversationsTooAndTakesHalfTheRequests()
    {
        var fake = Seed();
        await using var source = await StartAsync(fake, s => s.Concurrency = 6);
        await source.CountFilesAsync(CancellationToken.None);

        // the walk lists the team, its channels and the chats, and reads each conversation, so the count is exact
        Assert.IsTrue(fake.MessageCalls > 0, "the conversations read");
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
        Assert.IsTrue(fake.MostMessageCallsAtOnce <= 3, $"half of six requests at most to the walk: {fake.MostMessageCallsAtOnce}");

        // a conversation walked is not read again when opened
        var calls = fake.MessageCalls;
        await NamesAsync(source, "AnyFS Lab/General/posts");
        await NamesAsync(source, "Chats/Project Axle");
        Assert.AreEqual(calls, fake.MessageCalls);
    }

    [TestMethod]
    public async Task AWriteIsRefused()
    {
        await using var source = await StartAsync(Seed());
        Assert.IsFalse(source.Capabilities.CanWrite);
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync("Chats/Jane Doe/chat.md", VfsOpenMode.ReadWrite, CancellationToken.None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error);
    }

    [TestMethod]
    public async Task AnOpenServesTheBytesTheListingMeasured()
    {
        var fake = Seed();
        await using var source = await StartAsync(fake, s => s.RefreshSeconds = 1);
        var listed = (await ListEntriesAsync(source, "Chats/Jane Doe")).Single();

        // the conversation moves on and the kept answers age past the interval: an open must still give the listed length
        var chat = (await fake.StructureAsync(CancellationToken.None)).Chats.Single(c => c.Kind == ChatKind.OneOnOne);
        fake.Post(chat.Id, Jane, "Jane Doe", "<p>A much longer reply that changes the file's length by a good many bytes</p>", When.AddMinutes(9));
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        Assert.AreEqual(listed.Length, (await TextBytesAsync(source, "Chats/Jane Doe/chat.md")).Length, "the bytes of the length the listing gave");

        // once the listing has caught up, the open gives the new bytes, their length the new stat's
        var deadline = DateTime.UtcNow.AddSeconds(10);
        VfsEntry? now;
        while ((now = await source.StatAsync("Chats/Jane Doe/chat.md", CancellationToken.None))!.Length == listed.Length)
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "the listing behind the answer lands");
            await Task.Delay(50);
        }

        var bytes = await TextBytesAsync(source, "Chats/Jane Doe/chat.md");
        Assert.AreEqual(now.Length, bytes.Length);
        StringAssert.Contains(Encoding.UTF8.GetString(bytes), "A much longer reply");
    }

    [TestMethod]
    public async Task APeopleLookupThatFailsLeavesTheChatsListed()
    {
        var fake = Seed();
        fake.PeopleFails = true;
        await using var source = await StartAsync(fake);
        var chats = await NamesAsync(source, "Chats");
        Assert.AreEqual(2, chats.Count, "the chats list though their people's names could not be read");
        CollectionAssert.Contains(chats, "Project Axle");
    }

    [TestMethod]
    public async Task TheWindowLeavesOlderChatsAndPostsOut()
    {
        var recent = DateTimeOffset.UtcNow.AddDays(-1);
        var old = DateTimeOffset.UtcNow.AddDays(-30);
        var fake = new FakeTeams();
        fake.AddPerson(Jane, "Jane Doe");
        var general = FakeTeams.Channel("General", general: true);
        fake.AddTeam("AnyFS Lab", general);
        fake.Post(general.Id, Jane, "Jane Doe", "<p>Old news</p>", old);
        fake.Post(general.Id, Jane, "Jane Doe", "<p>Fresh news</p>", recent);
        fake.AddChat(ChatKind.Group, "Old chat", [Jane], old);
        fake.AddChat(ChatKind.Group, "New chat", [Jane], recent);

        await using var source = await StartAsync(fake, s => s.WithinDays = 7);
        CollectionAssert.AreEqual(new[] { "New chat" }, await NamesAsync(source, "Chats"));
        var posts = await NamesAsync(source, "AnyFS Lab/General/posts");
        Assert.AreEqual(1, posts.Count, string.Join(" | ", posts));
        StringAssert.Contains(posts[0], "Fresh news");
    }

    [TestMethod]
    public async Task AnEmptyNewestPageWithAWindowReadsOnWithoutFailing()
    {
        var fake = Seed();
        fake.EmptyNewestPage = true;
        await using var source = await StartAsync(fake, s => s.WithinDays = 36_500);
        var posts = await NamesAsync(source, "AnyFS Lab/General/posts");
        Assert.AreEqual(2, posts.Count, "the threads came from the older page");
    }

    [TestMethod]
    public async Task AChatKeepsTheNameItWasFirstGiven()
    {
        const string Stranger = "8:orgid:00000000-0000-0000-0000-00000stranger";
        var fake = new FakeTeams();
        var chat = fake.AddChat(ChatKind.OneOnOne, null, [Stranger], When);
        fake.Post(chat.Id, Stranger, null, "<p>Hi</p>", When);
        await using var source = await StartAsync(fake);
        var first = (await NamesAsync(source, "Chats")).Single();

        // the name becomes known later (a message carries it): the folder keeps the name it was listed under
        fake.Post(chat.Id, Stranger, "Zed Stranger", "<p>It is Zed</p>", When.AddMinutes(1));
        fake.Rename(Stranger, "Zed Stranger");
        await TextBytesAsync(source, $"Chats/{first}/chat.md");
        CollectionAssert.AreEqual(new[] { first }, await NamesAsync(source, "Chats"));
    }

    [TestMethod]
    public async Task ABodilessPostIsNamedByItsTimeAndAuthorAlone()
    {
        var fake = new FakeTeams();
        var general = FakeTeams.Channel("General", general: true);
        fake.AddTeam("AnyFS Lab", general);
        fake.Post(general.Id, Jane, "Jane Doe", "<p> </p>", When);
        await using var source = await StartAsync(fake);
        var stamp = When.ToLocalTime().ToString("yyyy-MM-dd HHmm", System.Globalization.CultureInfo.InvariantCulture);
        CollectionAssert.AreEqual(new[] { $"{stamp} Jane Doe" }, await NamesAsync(source, "AnyFS Lab/General/posts"), "no subject, and the author once");
    }

    [TestMethod]
    public async Task AskersAtOnceFetchAThingOnce()
    {
        var cache = new TeamsCache(CancellationToken.None);
        var calls = 0;
        async Task<string> FetchAsync(CancellationToken c)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(100, c);
            return "x";
        }

        using var go = new ManualResetEventSlim();
        var askers = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            go.Wait();
            return await cache.GetAsync("k", TimeSpan.FromMinutes(1), FetchAsync, CancellationToken.None);
        })).ToList();
        go.Set();
        await Task.WhenAll(askers);
        Assert.AreEqual(1, calls, "one fetch however many asked at once");
    }

    private static async Task<List<VfsEntry>> ListEntriesAsync(ISource source, string path)
    {
        var entries = new List<VfsEntry>();
        await foreach (var e in source.ListAsync(path, CancellationToken.None))
        {
            entries.Add(e);
        }

        return entries;
    }

    private static async Task<byte[]> TextBytesAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length];
        await file.ReadFullyAsync(0, buffer, CancellationToken.None);
        return buffer;
    }

    [TestMethod]
    public void ThePluginNamesTheSourceFromTheAccount()
    {
        var plugin = new TeamsPlugin();
        Assert.AreEqual("jane (contoso.com)", plugin.SuggestName(new TeamsSettings { Account = "jane@contoso.com" }));
        Assert.AreEqual("teams", plugin.TypeId);
    }

    [TestMethod]
    public void TheIconIsA64PixelPng()
    {
        using var icon = new TeamsPlugin().OpenIcon();
        Assert.IsNotNull(icon, "embedded in the plugin");
        var header = new byte[24];
        icon.ReadExactly(header);
        CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header[..8], "a PNG: the window draws no SVG yet");
        Assert.AreEqual(64, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), "width");
        Assert.AreEqual(64, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)), "height");
    }

    [TestMethod]
    public async Task ReactionsEditsAndDeletionsShowInTheMarkdown()
    {
        const string Ann = "8:orgid:00000000-0000-0000-0000-00000000ann";
        var fake = new FakeTeams();
        fake.AddPerson(Ann, "Ann Lee"); // reacts, never posts: her name comes from the people lookup
        var general = FakeTeams.Channel("General", general: true);
        fake.AddTeam("AnyFS Lab", general);
        var root = fake.Post(general.Id, Jane, "Jane Doe", "<p>Release is out</p>", When,
            reactions: [new Reaction("like", [John, Ann]), new Reaction("heart", [Jane]), new Reaction("1f680_rocket", [John])]);
        fake.Post(general.Id, John, "John Roe", "<p>Notes are updated</p>", When.AddMinutes(1), rootId: root.Id, edited: When.AddMinutes(2));
        fake.Post(general.Id, Jane, "Jane Doe", "<p>gone</p>", When.AddMinutes(3), rootId: root.Id, deleted: When.AddMinutes(4));
        await using var source = await StartAsync(fake);

        var thread = (await NamesAsync(source, "AnyFS Lab/General/posts")).Single();
        var md = await TextAsync(source, $"AnyFS Lab/General/posts/{thread}/post.md");
        StringAssert.Contains(md, "👍 John Roe, Ann Lee  ·  ❤️ Jane Doe  ·  🚀 John Roe");
        StringAssert.Contains(md, "edited\n\nNotes are updated");
        StringAssert.Contains(md, "*This message was deleted.*");
        Assert.IsFalse(md.Contains("gone", StringComparison.Ordinal), "a deleted message's content is not shown");
    }

    [TestMethod]
    public async Task AnUnchangedConversationIsNotReadAgainAndAChangedOneOnlyItsNewestPage()
    {
        var fake = new FakeTeams();
        var chat = fake.AddChat(ChatKind.Group, "Project Axle", [Jane, John], When);
        fake.Post(chat.Id, John, "John Roe", "<p>Kickoff Monday</p>", When);
        await using var source = await StartAsync(fake, s => s.RefreshSeconds = 0); // every listing asks

        CollectionAssert.Contains(await NamesAsync(source, "Chats/Project Axle"), "chat.md");
        var calls = fake.MessageCalls;
        await NamesAsync(source, "Chats/Project Axle");
        Assert.AreEqual(calls, fake.MessageCalls, "its version has not moved on: not read again");

        fake.Post(chat.Id, Jane, "Jane Doe", "<p>Agenda attached</p>", When.AddHours(1));
        await NamesAsync(source, "Chats/Project Axle");
        Assert.AreEqual(calls + 1, fake.MessageCalls, "changed: its newest page alone");
        var md = await TextAsync(source, "Chats/Project Axle/chat.md");
        StringAssert.Contains(md, "Kickoff Monday");
        StringAssert.Contains(md, "Agenda attached");
    }

    [TestMethod]
    public async Task TheWalkReadsEveryConversationAndKeepsThoseWhoseVersionsHoldAcrossARestart()
    {
        var fake = Seed();
        var structure = await fake.StructureAsync(CancellationToken.None);
        var random = structure.Teams.Single().Channels.Single(c => c.DisplayName == "Random");
        fake.Post(random.Id, John, "John Roe", "<p>Anyone for lunch?</p>", When); // every conversation has a version
        var axle = structure.Chats.Single(c => c.Title == "Project Axle");
        var context = new TestSourceContext();
        int calls;
        await using (var first = new TeamsSource(new TeamsSettings { Account = fake.Account }, fake.StandIn()))
        {
            await first.InitialiseAsync(context, CancellationToken.None);
            await first.CountFilesAsync(CancellationToken.None);
            Assert.AreEqual(EnumerationState.Complete, first.Counters.State, "every folder walked, the conversations too");
            Assert.IsTrue(first.Counters.FilesKnown >= 10, $"team.md, two channel.md, three post.md, two chat.md, groups.md and all-groups.md at the least: {first.Counters.FilesKnown}");
            calls = fake.MessageCalls;
            Assert.IsTrue(calls > 0, "the conversations read");
            Assert.AreEqual(axle.Version, first.Listings.StampHeld("Chats/Project Axle"), "kept by its version");
        }

        // the next run, nothing changed: every conversation kept by its version, none read
        await using (var next = new TeamsSource(new TeamsSettings { Account = fake.Account }, fake.StandIn()))
        {
            await next.InitialiseAsync(context, CancellationToken.None);
            await next.CountFilesAsync(CancellationToken.None);
            Assert.AreEqual(EnumerationState.Complete, next.Counters.State);
            Assert.AreEqual(calls, fake.MessageCalls, "no conversation read again");
        }

        // a message in one chat: the next run reads that chat, its newest page, and no other
        fake.Post(axle.Id, Jane, "Jane Doe", "<p>Agenda attached</p>", When.AddHours(1));
        await using var third = new TeamsSource(new TeamsSettings { Account = fake.Account }, fake.StandIn());
        await third.InitialiseAsync(context, CancellationToken.None);
        await third.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(calls + 1, fake.MessageCalls, "the changed chat alone");
        StringAssert.Contains(await TextAsync(third, "Chats/Project Axle/chat.md"), "Agenda attached");
    }

    [TestMethod]
    public async Task AConversationListedBeforeItsHistoryIsWholeCarriesNoStampUntilItIs()
    {
        var fake = new FakeTeams { OlderDelay = TimeSpan.FromMilliseconds(200) };
        fake.AddPerson(Jane, "Jane Doe");
        var newest = Mid(10, 20);
        var chat = fake.AddChat(ChatKind.OneOnOne, null, [Jane], newest);
        for (var i = 0; i < 130; i++)
        {
            fake.Post(chat.Id, Jane, "Jane Doe", $"<p>Day {i}</p>", newest.AddDays(-i));
        }

        await using var source = await StartAsync(fake, newest: 5, pageSize: 5, historyWait: TimeSpan.Zero);
        await NamesAsync(source, "Chats/Jane Doe");
        Assert.IsNull(source.Listings.StampHeld("Chats/Jane Doe"), "listed while its history was still being read: not kept by its version");

        // read whole, the folder is listed afresh, and that listing carries the version
        var version = (await fake.StructureAsync(CancellationToken.None)).Chats.Single().Version;
        var stamp = await UntilAsync(() => Task.FromResult(source.Listings.StampHeld("Chats/Jane Doe")), s => s is not null);
        Assert.AreEqual(version, stamp);
        StringAssert.Contains(await TextAsync(source, "Chats/Jane Doe/chat.md"), "Day 129\n");
    }

    [TestMethod]
    public void AThreadsStampChangesWithAnEditAReactionADeletionOrAReply()
    {
        var root = new TeamsMessage("1", null, null, Jane, When, "RichText/Html", "<p>Hi</p>", "Jane Doe");
        var reply = new TeamsMessage("2", "1", "1", John, When.AddMinutes(1), "RichText/Html", "<p>Hello</p>", "John Roe");
        var stamp = TeamsStamps.Of([root, reply]);
        Assert.AreEqual(stamp, TeamsStamps.Of([reply, root]), "the same thread in any order");
        Assert.AreEqual(32, stamp.Length);
        var changed = new[]
        {
            TeamsStamps.Of([root, reply with { Edited = When.AddMinutes(2) }]),
            TeamsStamps.Of([root with { Reactions = [new Reaction("like", [John])] }, reply]),
            TeamsStamps.Of([root, reply with { Deleted = When.AddMinutes(3) }]),
            TeamsStamps.Of([root, reply, new TeamsMessage("3", "1", "1", Jane, When.AddMinutes(4), "RichText/Html", "<p>Bye</p>", "Jane Doe")]),
            TeamsStamps.Of([root with { Pinned = true }, reply]),
        };
        CollectionAssert.AllItemsAreUnique(changed.Append(stamp).ToArray());
    }

    [TestMethod]
    public async Task TheListingsTheLastRunSawOpenAtOnceAtTheNextStart()
    {
        var fake = Seed();
        var context = new TestSourceContext();
        await using (var first = new TeamsSource(new TeamsSettings { Account = fake.Account }, fake.StandIn()))
        {
            await first.InitialiseAsync(context, CancellationToken.None);
            CollectionAssert.Contains(await NamesAsync(first, "Chats"), "Project Axle");
        }

        // the next run, the same state folder: answered from what the last saw while Teams is slow, then listed afresh
        fake.AddChat(ChatKind.Group, "Later chat", [Jane], When);
        fake.StructureDelay = TimeSpan.FromSeconds(2);
        await using var next = new TeamsSource(new TeamsSettings { Account = fake.Account }, fake.StandIn());
        await next.InitialiseAsync(context, CancellationToken.None);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var names = await NamesAsync(next, "Chats");
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(1), $"answered from the saved listing, not the aggregator ({clock.Elapsed.TotalSeconds:F1} s)");
        CollectionAssert.DoesNotContain(names, "Later chat", "what the last run saw");
        for (var i = 0; i < 100 && !(await NamesAsync(next, "Chats")).Contains("Later chat"); i++)
        {
            await Task.Delay(100);
        }

        CollectionAssert.Contains(await NamesAsync(next, "Chats"), "Later chat", "listed afresh behind that answer");
    }

    private static string Local(DateTimeOffset when) => when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    [TestMethod]
    public async Task CallsMdIsTheCallHistoryAndActivityMdTheFeed()
    {
        var fake = Seed();
        var structure = await fake.StructureAsync(CancellationToken.None);
        var general = structure.Teams.Single().Channels.Single(c => c.IsGeneral);
        var axle = structure.Chats.Single(c => c.Title == "Project Axle");
        fake.Calls =
        [
            new CallRecord(When.AddHours(2), When.AddHours(2).AddSeconds(5), When.AddHours(2).AddMinutes(12).AddSeconds(5), "outgoing", "twoParty", "accepted", "Tester", "Jane Doe", 2),
            new CallRecord(When, null, When.AddSeconds(30), "incoming", "twoParty", "missed", "John Roe", "Tester", 2),
            new CallRecord(When.AddDays(-1), When.AddDays(-1), When.AddDays(-1).AddHours(1).AddMinutes(2).AddSeconds(3), "outgoing", "group", "accepted", "Tester", "Project | Axle", 4),
        ];
        fake.Activity =
        [
            new ActivityItem(When.AddHours(1), "mentions", "mention", "Jane Doe", null, general.Id, "@Tester can you look?", true),
            new ActivityItem(When.AddMinutes(30), "reply", null, "John Roe", "A topic Teams named", axle.Id, "Sure", false),
            new ActivityItem(When.AddMinutes(10), "reactionInChat", "like", "John Roe", "Somewhere else", "19:unknown", null, false),
            new ActivityItem(When.AddMinutes(5), "msGraph", "privateMeetingCreated", null, null, null, null, false),
        ];
        await using var source = await StartAsync(fake);

        CollectionAssert.IsSubsetOf(new[] { "calls.md", "activity.md" }, await UntilAsync(() => NamesAsync(source, ""), n => n.Contains("calls.md") && n.Contains("activity.md")));
        var calls = await TextAsync(source, "calls.md");
        StringAssert.StartsWith(calls, "# Calls\n\nThe account's call history as Teams keeps it: 3 calls, newest first.\n");
        StringAssert.Contains(calls, $"| {Local(When.AddHours(2))} | Out | Jane Doe | 1:1 | Answered | 12:00 |");
        StringAssert.Contains(calls, $"| {Local(When)} | In | John Roe | 1:1 | Missed |  |");
        StringAssert.Contains(calls, "| Out | Project \\| Axle (+2) | Group | Answered | 1:02:03 |");

        var activity = await TextAsync(source, "activity.md");
        StringAssert.Contains(activity, "1 mention and 3 other entries, newest first.");
        StringAssert.Contains(activity, $"## Mentions\n\n| When | What | Who | Where | Message |\n|---|---|---|---|---|\n| {Local(When.AddHours(1))} | Mentioned you | Jane Doe | AnyFS Lab / General | @Tester can you look? |\n");
        StringAssert.Contains(activity, "| Replied | John Roe | Chat: Project Axle | Sure |", "a chat by its folder's name");
        StringAssert.Contains(activity, "| Reacted | John Roe | Somewhere else |  |", "else where Teams says");
        StringAssert.Contains(activity, "| Private meeting created |  |  |  |");
    }

    [TestMethod]
    public async Task WithoutACallHistoryThereIsNoCallsMdAndOneRefusedSaysWhy()
    {
        var fake = Seed();
        fake.Activity = [];
        await using (var source = await StartAsync(fake))
        {
            // no call history (the chat service's 404, as the sandbox's): no calls.md; an empty feed is still activity.md
            var root = await UntilAsync(() => NamesAsync(source, ""), n => n.Contains("activity.md"));
            CollectionAssert.DoesNotContain(root, "calls.md");
            StringAssert.Contains(await TextAsync(source, "activity.md"), "## Mentions\n\nNone.\n");
        }

        fake.CallsFail = true;
        await using (var source = await StartAsync(fake))
        {
            await UntilAsync(() => NamesAsync(source, ""), n => n.Contains("calls.md"));
            StringAssert.Contains(await TextAsync(source, "calls.md"), "The call history could not be read: Teams (fake) answered 403");
        }

        // off: neither is shown, nor asked for
        fake.CallsFail = false;
        fake.Calls = [new CallRecord(When, When, When.AddMinutes(1), "outgoing", "twoParty", "accepted", "Tester", "Jane Doe", 2)];
        var asked = fake.CallsCalls;
        await using (var source = await StartAsync(fake, s => s.CallsAndActivity = false))
        {
            await NamesAsync(source, "");
            await Task.Delay(200);
            var root = await NamesAsync(source, "");
            CollectionAssert.DoesNotContain(root, "calls.md");
            CollectionAssert.DoesNotContain(root, "activity.md");
            Assert.AreEqual(asked, fake.CallsCalls);
        }
    }

    [TestMethod]
    public async Task TeamMdAndChannelMdTellTheirDetails()
    {
        var fake = new FakeTeams();
        fake.AddPerson(Jane, "Jane Doe");
        var general = FakeTeams.Channel("General", general: true) with { Description = "Everything", Created = When, CreatorMri = Jane };
        var design = FakeTeams.Channel("Design", priv: true) with
        {
            Description = "Drawings and specs",
            Created = When.AddDays(1),
            CreatorMri = Jane,
            Favourite = true,
            Tabs = [new TabInfo("Wiki | old", "Website", "https://wiki.contoso.com/design"), new TabInfo("Plan", "Pinned file", null)],
        };
        var named = FakeTeams.Channel("team.md");
        var team = fake.AddTeam("AnyFS Lab", general, design, named);
        fake.SetTeam(team with { Description = "The lab's team", Mail = "anyfslab@contoso.com", AccessType = 1, Owner = true, Members = 12, Owners = 2, Bots = 1, Classification = "Internal", Favourite = true });
        fake.Threads[general.Id] = new ThreadDetails("https://contoso.sharepoint.com/sites/anyfslab", "/sites/anyfslab/Shared Documents/General", 12, 2, 0, 1);
        fake.Threads[design.Id] = new ThreadDetails("https://contoso.sharepoint.com/sites/anyfslab-Design", "/sites/anyfslab-Design/Shared Documents/Design", 3, 1, 1, null);
        await using var source = await StartAsync(fake);

        CollectionAssert.AreEqual(new[] { "General", "Design", "team.md (2)", "team.md" }, await NamesAsync(source, "AnyFS Lab"), "a channel named as the team's file is numbered");
        var teamMd = await TextAsync(source, "AnyFS Lab/team.md");
        StringAssert.StartsWith(teamMd, $"https://teams.microsoft.com/l/team/{Uri.EscapeDataString(team.Id)}/conversations?groupId={team.GroupId}\n\n"
            + "# AnyFS Lab\n\nThe lab's team\n\n- Privacy: Private\n- Email: anyfslab@contoso.com\n- You: owner\n- Members: 12 (2 owners, 1 bot)\n- Classification: Internal\n- Site: https://contoso.sharepoint.com/sites/anyfslab\n- Favourite: yes\n");
        StringAssert.Contains(teamMd, "## Channels\n\n| Channel | Kind | Description |\n|---|---|---|\n| General | Standard (General) | Everything |\n| Design | Private | Drawings and specs |\n| team.md | Standard |  |\n");

        var channelMd = await TextAsync(source, "AnyFS Lab/Design/channel.md");
        StringAssert.StartsWith(channelMd, $"https://teams.microsoft.com/l/channel/{Uri.EscapeDataString(design.Id)}/Design?groupId={team.GroupId}\n\n# Design\n\nA channel of AnyFS Lab.\n\nDrawings and specs\n\n");
        StringAssert.Contains(channelMd, $"- Kind: Private\n- Made: {When.AddDays(1).ToLocalTime():yyyy-MM-dd} by Jane Doe\n- Members: 3 (1 owner, 1 from other organisations)\n- Files: <https://contoso.sharepoint.com/sites/anyfslab-Design/Shared%20Documents/Design>\n- Favourite: yes\n");
        StringAssert.Contains(channelMd, "## Tabs\n\n- Wiki | old: Website, <https://wiki.contoso.com/design>\n- Plan: Pinned file\n");

        // one the chat service does not answer for: what the aggregator gave alone
        var namedMd = await TextAsync(source, "AnyFS Lab/team.md (2)/channel.md");
        StringAssert.Contains(namedMd, "- Kind: Standard\n");
        Assert.IsFalse(namedMd.Contains("Members", StringComparison.Ordinal));

        // a walk interval's details: listed again, not asked again
        var asked = fake.ThreadCalls;
        await NamesAsync(source, "AnyFS Lab/Design");
        await TextAsync(source, "AnyFS Lab/team.md");
        Assert.AreEqual(asked, fake.ThreadCalls);
    }

    [TestMethod]
    public async Task APostAndAChatStartWithTheAddressThatOpensThemInTeams()
    {
        var fake = Seed();
        var structure = await fake.StructureAsync(CancellationToken.None);
        var team = structure.Teams.Single();
        var general = team.Channels.Single(c => c.IsGeneral);
        var chat = structure.Chats.Single(c => c.Kind == ChatKind.OneOnOne);
        await using var source = await StartAsync(fake);

        var build = (await NamesAsync(source, "AnyFS Lab/General/posts")).Single(p => p.Contains("Build is green", StringComparison.Ordinal));
        var root = When.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        StringAssert.StartsWith(await TextAsync(source, $"AnyFS Lab/General/posts/{build}/post.md"),
            $"https://teams.microsoft.com/l/message/{Uri.EscapeDataString(general.Id)}/{root}?groupId={team.GroupId}&parentMessageId={root}&createdTime={root}\n\n# Build is green\n\n");
        StringAssert.StartsWith(await TextAsync(source, "Chats/Jane Doe/chat.md"), $"https://teams.microsoft.com/l/chat/{Uri.EscapeDataString(chat.Id)}/conversations\n\n# Jane Doe\n\n");

        // in GCC High, on its own Teams host
        await using var gov = await StartAsync(fake, s => s.Cloud = "US Gov (GCC High)");
        StringAssert.StartsWith(await TextAsync(gov, "Chats/Jane Doe/chat.md"), $"https://gov.teams.microsoft.us/l/chat/{Uri.EscapeDataString(chat.Id)}/conversations\n\n");
    }

    [TestMethod]
    public async Task AChatMdSaysWhoIsInItWhoStartedItAndWhatIsPinned()
    {
        const string Ann = "8:orgid:00000000-0000-0000-0000-00000000ann";
        const string Polly = "28:00000000-0000-0000-0000-00000000poly";
        const string Kim = "8:orgid:00000000-0000-0000-0000-00000000kim";
        var fake = new FakeTeams();
        fake.AddPerson(fake.Mri!, "AnyFS tester", organisation: "Contoso");
        fake.AddPerson(Jane, "Jane Doe", organisation: "Contoso");
        fake.AddPerson(Ann, "Ann Lee", guest: true, organisation: "Contoso");
        fake.AddPerson(Polly, "Polly", bot: true);
        fake.AddPerson(Kim, "Kim Park", organisation: "Fabrikam");
        var chat = fake.AddChat(ChatKind.Group, "Project Axle", [fake.Mri!, Jane, Ann, Polly, Kim], When);
        fake.Post(chat.Id, Jane, "Jane Doe", "<p>Agenda for Monday</p><p>and more</p>", When, pinned: true);
        var plan = fake.Post(chat.Id, Kim, "Kim Park", "<p>The plan</p>", When.AddMinutes(5));
        fake.Post(chat.Id, Ann, "Ann Lee", "<p>Thanks</p>", When.AddMinutes(9));
        fake.SetChat((await fake.StructureAsync(CancellationToken.None)).Chats.Single(c => c.Id == chat.Id) with
        {
            Created = When.AddDays(-2), CreatorMri = Jane, External = true, Muted = true, PinnedIds = [plan.Id],
        });
        var crowd = fake.AddChat(ChatKind.Meeting, "All hands", Enumerable.Range(0, 60).Select(i => $"8:orgid:00000000-0000-0000-0000-{i:D12}").ToList(), When);
        fake.Post(crowd.Id, Jane, "Jane Doe", "<p>Welcome</p>", When);
        await using var source = await StartAsync(fake);

        StringAssert.Contains(await TextAsync(source, "Chats/Project Axle/chat.md"),
            $"/conversations\n\n# Project Axle\n\n- Kind: Group chat\n- Members (5): you, Ann Lee (guest), Jane Doe, Kim Park (Fabrikam), Polly (bot)\n"
            + $"- Started: {Local(When.AddDays(-2))} by Jane Doe\n- With people from other organisations\n- Muted\n"
            + $"- Pinned:\n  - Jane Doe, {Local(When)}: Agenda for Monday\n  - Kim Park, {Local(When.AddMinutes(5))}: The plan\n\n**Jane Doe**");

        // a meeting's crowd: named up to fifty
        var hands = await TextAsync(source, "Chats/All hands/chat.md");
        StringAssert.Contains(hands, "- Kind: Meeting chat\n- Members (60): ");
        StringAssert.Contains(hands, " and 10 more\n");
    }

    [TestMethod]
    public async Task ThePeopleLookupAsksAboutEachPersonOnce()
    {
        const string Stranger = "8:orgid:00000000-0000-0000-0000-00000stranger";
        var fake = Seed();
        fake.AddChat(ChatKind.OneOnOne, null, [Stranger], When);
        await using var source = await StartAsync(fake, s => s.RefreshSeconds = 0); // every listing reads the structure again

        await NamesAsync(source, "Chats");
        await NamesAsync(source, "Chats");
        CollectionAssert.AllItemsAreUnique(fake.PeopleAsked);
        CollectionAssert.Contains(fake.PeopleAsked, Stranger, "asked once, though the lookup names no one for it");
    }

    /// <summary>A day in the middle of a month, at noon: its messages fall in that month in any time zone.</summary>
    private static DateTimeOffset Mid(int month, int day = 15) => new(2026, month, day, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ALongChatsChatMdHasItsWholeHistory()
    {
        var fake = new FakeTeams();
        fake.AddPerson(Jane, "Jane Doe");
        var chat = fake.AddChat(ChatKind.Group, "Long chat", [Jane], Mid(10));
        var all = new List<string>();
        foreach (var (month, count) in new[] { (7, 4), (8, 4), (10, 6) })
        {
            for (var i = 0; i < count; i++)
            {
                fake.Post(chat.Id, Jane, "Jane Doe", $"<p>Message {month}-{i}</p>", Mid(month, 10 + i));
                all.Add($"Message {month}-{i}\n");
            }
        }

        // a newest read of five, the rest the chat's history: read behind the listing that begins it, which waits for it a moment
        await using var source = await StartAsync(fake, newest: 5, pageSize: 5);
        CollectionAssert.AreEqual(new[] { "chat.md" }, await NamesAsync(source, "Chats/Long chat"), "no folder of earlier messages");
        var md = await TextAsync(source, "Chats/Long chat/chat.md");
        Assert.IsFalse(md.Contains("Earlier messages", StringComparison.Ordinal), "nothing more to come");
        var at = all.Select(m => md.IndexOf(m, StringComparison.Ordinal)).ToList();
        Assert.IsTrue(at.All(p => p >= 0), md);
        CollectionAssert.AreEqual(at.Order().ToList(), at, "oldest first");
    }

    [TestMethod]
    public async Task AChatMdWhoseHistoryIsStillBeingReadSaysSoAndFillsIn()
    {
        var fake = new FakeTeams { OlderDelay = TimeSpan.FromMilliseconds(300) };
        fake.AddPerson(Jane, "Jane Doe");
        var newest = Mid(10, 20);
        var chat = fake.AddChat(ChatKind.OneOnOne, null, [Jane], newest);
        for (var i = 0; i < 130; i++)
        {
            fake.Post(chat.Id, Jane, "Jane Doe", $"<p>Day {i}</p>", newest.AddDays(-i));
        }

        // the listing that begins the history not waiting for it: chat.md has the newest read and says the rest is coming
        await using var source = await StartAsync(fake, newest: 5, pageSize: 5, historyWait: TimeSpan.Zero);
        CollectionAssert.AreEqual(new[] { "chat.md" }, await NamesAsync(source, "Chats/Jane Doe"));
        var first = await TextAsync(source, "Chats/Jane Doe/chat.md");
        StringAssert.Contains(first, $"- Earlier messages: still being read: this file goes back to {Local(newest.AddDays(-4))} so far, and fills in as they arrive\n");
        Assert.IsFalse(first.Contains("Day 5\n", StringComparison.Ordinal));

        // listed afresh once read
        var whole = await UntilAsync(() => TextAsync(source, "Chats/Jane Doe/chat.md"), md => md.Contains("Day 129\n", StringComparison.Ordinal));
        StringAssert.Contains(whole, "Day 129\n");
        Assert.IsFalse(whole.Contains("Earlier messages", StringComparison.Ordinal), "nothing more to come");
    }

    [TestMethod]
    public async Task TheHistoryReadFirstIsThatOfTheConversationOpenedLast()
    {
        var fake = new FakeTeams { OlderDelay = TimeSpan.FromMilliseconds(20) };
        fake.AddPerson(Jane, "Jane Doe");
        fake.AddPerson(John, "John Roe");
        var newest = Mid(10, 20);
        var first = fake.AddChat(ChatKind.Group, "First", [Jane], newest);
        var second = fake.AddChat(ChatKind.Group, "Second", [John], newest);
        for (var i = 0; i < 10_000; i++)
        {
            fake.Post(first.Id, Jane, "Jane Doe", $"<p>First {i}</p>", newest.AddMinutes(-i));
        }

        for (var i = 0; i < 200; i++)
        {
            fake.Post(second.Id, John, "John Roe", $"<p>Second {i}</p>", newest.AddMinutes(-i));
        }

        // the first chat's history under way, a second chat opened: its pages are read one after another, the first's after
        // (no walk, which would begin both on its own)
        await using var source = await StartAsync(fake, s => s.KeepListings = false, newest: 5, pageSize: 5, historyWait: TimeSpan.Zero);
        await NamesAsync(source, "Chats/First");
        await UntilAsync(() => Task.FromResult(fake.OlderAsked.Count), n => n > 0);
        await NamesAsync(source, "Chats/Second");
        StringAssert.Contains(await UntilAsync(() => TextAsync(source, "Chats/Second/chat.md"), md => md.Contains("Second 199\n", StringComparison.Ordinal)), "Second 199\n");
        string Order()
        {
            lock (fake.OlderAsked)
            {
                return string.Concat(fake.OlderAsked.Select(t => t == first.Id ? 'F' : 'S'));
            }
        }

        var order = await UntilAsync(() => Task.FromResult(Order()), o => o.EndsWith('F'));
        StringAssert.Matches(order, new System.Text.RegularExpressions.Regex("^F+S{4}F+$"), $"the second chat's four pages together, between the first's: {order}");
    }

    [TestMethod]
    public async Task AChannelsPostsGoBackToItsStart()
    {
        var fake = new FakeTeams();
        var general = FakeTeams.Channel("General", general: true);
        fake.AddTeam("AnyFS Lab", general);
        var july = fake.Post(general.Id, Jane, "Jane Doe", "<p>July plan</p>", Mid(7));
        fake.Post(general.Id, John, "John Roe", "<p>Agreed</p>", Mid(7, 16), rootId: july.Id);
        var august = fake.Post(general.Id, Jane, "Jane Doe", "<p>August review</p>", Mid(8));
        for (var i = 0; i < 4; i++)
        {
            fake.Post(general.Id, Jane, "Jane Doe", $"<p>October {i}</p>", Mid(10, 10 + i));
        }

        // a late reply brings August's thread among the newest, though its opening message is not
        fake.Post(general.Id, John, "John Roe", "<p>Late reply</p>", Mid(10, 20), rootId: august.Id);
        await using var source = await StartAsync(fake, newest: 5, pageSize: 5);

        // the history read behind the listing that begins it: every thread, August's whole and named by its opening message
        var posts = await NamesAsync(source, "AnyFS Lab/General/posts");
        Assert.AreEqual(6, posts.Count, string.Join(" | ", posts));
        var plan = posts.Single(p => p.Contains("July plan", StringComparison.Ordinal));
        var post = await TextAsync(source, $"AnyFS Lab/General/posts/{plan}/post.md");
        StringAssert.Contains(post, "July plan");
        StringAssert.Contains(post, "Agreed");
        var review = posts.Single(p => p.Contains("August review", StringComparison.Ordinal));
        StringAssert.Contains(await TextAsync(source, $"AnyFS Lab/General/posts/{review}/post.md"), "Late reply");
    }

    [TestMethod]
    public async Task TheWindowEndsTheHistory()
    {
        var now = DateTimeOffset.UtcNow;
        var fake = new FakeTeams();
        var chat = fake.AddChat(ChatKind.Group, "Windowed", [Jane], now);
        foreach (var days in new[] { 120, 50, 40, 10, 9, 8, 7, 6 })
        {
            fake.Post(chat.Id, Jane, "Jane Doe", $"<p>{days} days ago</p>", now.AddDays(-days));
        }

        await using var source = await StartAsync(fake, s => s.WithinDays = 60, newest: 5, pageSize: 5);
        var md = await TextAsync(source, "Chats/Windowed/chat.md");
        StringAssert.Contains(md, "50 days ago");
        StringAssert.Contains(md, "40 days ago");
        Assert.IsFalse(md.Contains("120 days ago", StringComparison.Ordinal), "past the window, though read with the page before it");
        Assert.IsFalse(md.Contains("Earlier messages", StringComparison.Ordinal), "read back to the window: nothing more to come");
    }

    [TestMethod]
    public async Task APostOrImageAListingFromBeforeNamedIsWaitedForAsTheHistoryIsRead()
    {
        // as after a restart, when a kept listing names an old post or image: opened at once, before the history has reached
        // it, it is waited for as the history is read
        const string ImageSrc = "https://as-prod.asyncgw.teams.microsoft.com/v1/objects/0-sa-d1-old/views/imgo";
        var fake = new FakeTeams { OlderDelay = TimeSpan.FromMilliseconds(200) };
        fake.AddPerson(Jane, "Jane Doe");
        var general = FakeTeams.Channel("General", general: true);
        fake.AddTeam("AnyFS Lab", general);
        var newest = Mid(10, 20);
        var chat = fake.AddChat(ChatKind.OneOnOne, null, [Jane], newest);
        fake.AddImage(ImageSrc, [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0]);
        for (var i = 0; i < 130; i++)
        {
            fake.Post(general.Id, Jane, "Jane Doe", $"<p>Day {i}</p>", newest.AddDays(-i));
            fake.Post(chat.Id, Jane, "Jane Doe", i < 129 ? $"<p>Day {i}</p>" : $"<p><img itemscope=\"\" itemtype=\"http://schema.skype.com/AMSImage\" src=\"{ImageSrc}\" alt=\"image\"></p>", newest.AddDays(-i));
        }

        await using var source = await StartAsync(fake, newest: 5, pageSize: 5, historyWait: TimeSpan.Zero);
        var oldest = newest.AddDays(-129).ToLocalTime();
        StringAssert.Contains(await TextAsync(source, $"AnyFS Lab/General/posts/{oldest:yyyy-MM-dd HHmm} Jane Doe Day 129/post.md"), "Day 129");
        await using var image = await source.OpenAsync($"Chats/Jane Doe/attachments/{oldest:yyyy-MM-dd_HHmm}_image-1.png", VfsOpenMode.Read, CancellationToken.None);
        Assert.AreEqual(10, image.Length);
    }

    /// <summary>
    /// A "call ended" message as a GCC High tenant's are shaped (the work probe, 30-Oct-2026): who came for how long, and
    /// the meeting's details. <paramref name="asAtWork"/>: those details empty and the start in a form not read, as at work.
    /// </summary>
    private static string CallEnded(DateTimeOffset started, string callId, bool asAtWork, params (string Mri, string Name, int Seconds)[] parts)
        => $"<ended/><partlist type=\"ended\" alt=\"\" count=\"{parts.Length}\">"
            + string.Concat(parts.Select(p => $"<part identity=\"{p.Mri}\"><name>{p.Mri}</name><displayName>{p.Name}</displayName><duration>{p.Seconds}</duration></part>"))
            + (asAtWork
                ? "</partlist><meetingDetails><organizerUpn></organizerUpn><meetingType></meetingType><startTime></startTime><endTime></endTime><iCalUid>ical-1</iCalUid>"
                    + "<hasEphemeralTranscript>False</hasEphemeralTranscript><isRecapEnabled>False</isRecapEnabled><subject></subject></meetingDetails>"
                    + $"<callEventType>callEnded</callEventType><conversationStartTime>soon after noon</conversationStartTime><conversationUrl>https://x/c</conversationUrl><callId>{callId}</callId>"
                : "</partlist><meetingDetails><organizerUpn>jane@contoso.com</organizerUpn><meetingType>Scheduled</meetingType>"
                    + $"<startTime>{started:O}</startTime><endTime>{started.AddHours(1):O}</endTime><iCalUid>ical-1</iCalUid><hasEphemeralTranscript>False</hasEphemeralTranscript>"
                    + "<isRecapEnabled>False</isRecapEnabled><subject>Weekly sync</subject><type>Recurring</type></meetingDetails>"
                    + $"<callEventType>callEnded</callEventType><conversationStartTime>{started:O}</conversationStartTime><conversationUrl>https://x/c</conversationUrl><callId>{callId}</callId>");

    /// <summary>A recording's message as such a tenant's are shaped: its title, file name, length and its video in the media service.</summary>
    private static string Recording(string video, DateTimeOffset at, double seconds, string callId)
        => "<URIObject type=\"Video.2/CallRecording.1\" uri=\"https://x/objects/0-rec\" url_thumbnail=\"https://x/t\" version=\"1\"><RecordingStatus status=\"Success\" code=\"200\"/>"
            + "<Title>Weekly sync</Title><a href=\"https://x/play\">Play</a><OriginalName v=\"Weekly sync-20261030_100231-Meeting Recording.mp4\"/>"
            + $"<RecordingStorage type=\"MeetingOrganizerOneDrive\" userId=\"u\"/><Identifiers><Id type=\"callId\" value=\"{callId}\"/><Id type=\"AMSDocumentID\" value=\"0-rec\"/></Identifiers>"
            + $"<RecordingContent contentTypes=\"video,transcript\" timestamp=\"{at:O}\" duration=\"{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}\">"
            + $"<item type=\"video\" uri=\"x\"/><item type=\"amsVideo\" uri=\"{video}\"/><item type=\"amsTranscript\" uri=\"{video}t\"/></RecordingContent></URIObject>";

    /// <summary>A span's end as the Markdown writes it: the time alone on its start's day.</summary>
    private static string End(DateTimeOffset from, DateTimeOffset to)
        => to.ToLocalTime().Date == from.ToLocalTime().Date ? to.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : Local(to);

    private const string Video = "https://as-prod.asyncgw.teams.microsoft.com/v1/objects/0-rec/views/video";
    private const string RecordingFile = "Weekly sync-20261030_100231-Meeting Recording.mp4";

    /// <summary>A meeting's chat that held a meeting, recorded: a message, its call's end, and its recording.</summary>
    private static (FakeTeams Fake, ChatInfo Meeting, DateTimeOffset Started) Meeting()
    {
        var fake = new FakeTeams();
        fake.AddPerson(Jane, "Jane Doe");
        var started = Mid(10, 30);
        var meeting = fake.AddChat(ChatKind.Meeting, "Weekly sync", [Jane, John], started);

        // as at work: the chat names its meeting (meetingInformation); its call's own details are empty
        meeting = meeting with
        {
            Meeting = new MeetingInfo("Weekly sync", "Room 1", started, started.AddHours(1), false, "https://teams.microsoft.com/l/meetup-join/x",
                Jane["8:orgid:".Length..], "weekly, on Monday", "Recurring", "accepted"),
        };
        fake.SetChat(meeting);
        fake.Post(meeting.Id, Jane, "Jane Doe", "<p>Agenda in the deck</p>", started.AddMinutes(-5));
        fake.Post(meeting.Id, Jane, null, CallEnded(started, "call-1", asAtWork: true, (Jane, "Jane Doe", 3730), (John, "John Roe", 3480)), started.AddSeconds(3735), messageType: "Event/Call");
        fake.Post(meeting.Id, Jane, null, Recording(Video, started.AddSeconds(31), 3555.2, "call-1"), started.AddSeconds(3800), messageType: "RichText/Media_CallRecording");
        return (fake, meeting, started);
    }

    [TestMethod]
    public async Task AMeetingsChatHasItsMeetingMdAndItsRecordings()
    {
        var (fake, meeting, started) = Meeting();
        var video = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        fake.AddMedia(Video, video);
        await using var source = await StartAsync(fake);

        CollectionAssert.AreEqual(new[] { "chat.md", "meeting.md", "recordings" }, await NamesAsync(source, "Chats/Weekly sync"));
        var md = await TextAsync(source, "Chats/Weekly sync/meeting.md");
        StringAssert.StartsWith(md, $"https://teams.microsoft.com/l/chat/{Uri.EscapeDataString(meeting.Id)}/conversations\n\n# Weekly sync\n\n"
            + $"- Subject: Weekly sync\n- Scheduled: {Local(started)} – {End(started, started.AddHours(1))} (1:00:00)\n- Repeats: weekly, on Monday\n- Location: Room 1\n"
            + "- Organiser: Jane Doe\n- Kind: Recurring\n- Your answer: Accepted\n- Join: <https://teams.microsoft.com/l/meetup-join/x>\n\n"
            + "Held 1 time as Teams recorded it, newest first; 1 recording.\n");

        // its start in a form not read: its end less the longest anyone stayed
        var ended = started.AddSeconds(3735);
        var began = ended.AddSeconds(-3730);
        StringAssert.Contains(md, $"\n## {Local(began)} – {End(began, ended)} (1:02:10)\n\n- Attended (2): Jane Doe (1:02:10), John Roe (58:00)\n");
        StringAssert.Contains(md, $"## Recordings\n\n- {Local(started.AddSeconds(31))}: Weekly sync (59:15), [{RecordingFile}](<recordings/{RecordingFile}>)\n");

        // the recording: its length as the media service gives it, read in ranges as it is read
        var entry = (await ListEntriesAsync(source, "Chats/Weekly sync/recordings")).Single();
        Assert.AreEqual((RecordingFile, (long)video.Length), (entry.Name, entry.Length));
        await using (var file = await source.OpenAsync($"Chats/Weekly sync/recordings/{RecordingFile}", VfsOpenMode.Read, CancellationToken.None))
        {
            var tail = new byte[1000];
            await file.ReadFullyAsync(video.Length - 1000, tail, CancellationToken.None);
            CollectionAssert.AreEqual(video[^1000..], tail);
        }

        // chat.md shows the recording as what it is, linked to its file, and no call markup
        var chat = await TextAsync(source, "Chats/Weekly sync/chat.md");
        StringAssert.Contains(chat, $"*🎥 Meeting recording: Weekly sync (59:15)* — [{RecordingFile}](<recordings/{RecordingFile}>)");
        Assert.IsFalse(chat.Contains("partlist", StringComparison.OrdinalIgnoreCase) || chat.Contains("URIObject", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ARecordingTheMediaServiceDoesNotServeIsLeftOutAndMeetingsOffShowsNeither()
    {
        var (fake, _, _) = Meeting();
        await using (var source = await StartAsync(fake))
        {
            CollectionAssert.AreEqual(new[] { "chat.md", "meeting.md" }, await NamesAsync(source, "Chats/Weekly sync"));
            StringAssert.Contains(await TextAsync(source, "Chats/Weekly sync/meeting.md"), ": Weekly sync (59:15), not served by Teams' media service now\n");
        }

        fake.AddMedia(Video, [1, 2, 3]);
        var asked = fake.MediaCalls;
        await using (var off = await StartAsync(fake, s => s.Meetings = false))
        {
            CollectionAssert.AreEqual(new[] { "chat.md" }, await NamesAsync(off, "Chats/Weekly sync"));
            StringAssert.Contains(await TextAsync(off, "Chats/Weekly sync/chat.md"), "*🎥 Meeting recording: Weekly sync (59:15)*");
            Assert.AreEqual(asked, fake.MediaCalls, "nothing asked of the media service");
        }
    }

    [TestMethod]
    public async Task AChannelMeetingsRecordingIsInItsPost()
    {
        var fake = new FakeTeams();
        var general = FakeTeams.Channel("General", general: true);
        fake.AddTeam("AnyFS Lab", general);
        var root = fake.Post(general.Id, Jane, "Jane Doe", "<p>Design review</p>", Mid(10, 29));
        fake.Post(general.Id, Jane, null, Recording(Video, Mid(10, 29).AddMinutes(1), 1800, "call-2"), Mid(10, 29).AddHours(1), rootId: root.Id, messageType: "RichText/Media_CallRecording");
        fake.AddMedia(Video, new byte[4096]);
        await using var source = await StartAsync(fake);

        var thread = (await NamesAsync(source, "AnyFS Lab/General/posts")).Single();
        CollectionAssert.AreEqual(new[] { "post.md", "recordings" }, await NamesAsync(source, $"AnyFS Lab/General/posts/{thread}"));
        CollectionAssert.AreEqual(new[] { RecordingFile }, await NamesAsync(source, $"AnyFS Lab/General/posts/{thread}/recordings"));
        StringAssert.Contains(await TextAsync(source, $"AnyFS Lab/General/posts/{thread}/post.md"), $"*🎥 Meeting recording: Weekly sync (30:00)* — [{RecordingFile}](<recordings/{RecordingFile}>)");
    }

    [TestMethod]
    public void ACallsOwnDetailsAreReadWhereItHasThem()
    {
        var started = Mid(10, 30);
        var message = new TeamsMessage("1", null, null, Jane, started.AddSeconds(3735), "Event/Call", CallEnded(started, "call-1", asAtWork: false, (Jane, "Jane Doe", 3730)), null);
        var call = TeamsMeetings.Calls([message]).Single();
        Assert.AreEqual((started, started.AddSeconds(3735), "Weekly sync", "jane@contoso.com", "Scheduled"), (call.Started, call.Ended, call.Subject, call.Organiser, call.Kind));
        Assert.AreEqual((started, started.AddHours(1)), (call.ScheduledStart, call.ScheduledEnd));
    }

    [TestMethod]
    public async Task AMeetingsChatHasItsMeetingMdBeforeItIsHeld()
    {
        var fake = new FakeTeams();
        fake.AddPerson(Jane, "Jane Doe");
        var at = Mid(11, 5);
        var chat = fake.AddChat(ChatKind.Meeting, "Planning", [Jane], Mid(10, 29));
        fake.SetChat(chat with { Meeting = new MeetingInfo("Planning", null, at, at.AddMinutes(30), true, null, null, null, null, null) });
        fake.Post(chat.Id, Jane, "Jane Doe", "<p>See you there</p>", Mid(10, 29));
        await using var source = await StartAsync(fake);

        CollectionAssert.AreEqual(new[] { "chat.md", "meeting.md" }, await NamesAsync(source, "Chats/Planning"));
        StringAssert.Contains(await TextAsync(source, "Chats/Planning/meeting.md"),
            $"# Planning\n\n- Subject: Planning\n- Scheduled: {Local(at)} – {End(at, at.AddMinutes(30))} (30:00)\n- Cancelled: yes\n\nHeld 0 times as Teams recorded it, newest first; 0 recordings.\n");
    }

    [TestMethod]
    public void ALengthIsReadAsTeamsWritesIt()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(3730), TeamsMeetings.Length("3730"));
        Assert.AreEqual(TimeSpan.FromSeconds(3555.2), TeamsMeetings.Length("3555.2"));
        Assert.AreEqual(new TimeSpan(1, 2, 10), TeamsMeetings.Length("01:02:10"));
        Assert.AreEqual(new TimeSpan(1, 2, 10), TeamsMeetings.Length("PT1H2M10S"));
        Assert.IsNull(TeamsMeetings.Length("soon"));
        Assert.IsNull(TeamsMeetings.Length(null));

        // markup that does not parse is no call
        Assert.AreEqual(0, TeamsMeetings.Calls([new TeamsMessage("1", null, null, Jane, When, "Event/Call", "<ended/><partlist><part>", null)]).Count);
    }

    [TestMethod]
    public async Task ATeamNamedAsOneOfTheRootsOwnIsNumbered()
    {
        var fake = new FakeTeams();
        fake.AddTeam("Chats", FakeTeams.Channel("General", general: true));
        await using var source = await StartAsync(fake);
        var root = await NamesAsync(source, "");
        Assert.AreEqual("Chats (2)", root[0]);
        CollectionAssert.Contains(root, "Chats");
        CollectionAssert.AreEqual(new[] { "General", "team.md" }, await NamesAsync(source, "Chats (2)"));
    }

    [TestMethod]
    public void AReactionsEmojiIsNamedOrByItsCodePoint()
    {
        Assert.AreEqual("👍", TeamsText.EmojiOf("like"));
        Assert.AreEqual("😆", TeamsText.EmojiOf("LAUGH"));
        Assert.AreEqual("😀", TeamsText.EmojiOf("1f600_grinningface"));
        Assert.AreEqual(":yes:", TeamsText.EmojiOf("yes"));
    }

    [TestMethod]
    public void TokensAreMatchedToTheirServiceInEveryCloud()
    {
        // Microsoft 365 (spike 18) and GCC High (the sign-in audit at a GCC High tenant)
        Assert.AreEqual(TeamsService.ChatService, TeamsServices.Of("https://ic3.teams.office.com"));
        Assert.AreEqual(TeamsService.ChatService, TeamsServices.Of("https://ic3.gov.teams.microsoft.us"));
        Assert.AreEqual(TeamsService.Aggregator, TeamsServices.Of("https://chatsvcagg.teams.microsoft.com"));
        Assert.AreEqual(TeamsService.Aggregator, TeamsServices.Of("https://chatsvcagg.gov.teams.microsoft.us"));
        Assert.AreEqual(TeamsService.MiddleTier, TeamsServices.Of("https://api.spaces.skype.com"));
        Assert.AreEqual(TeamsService.MiddleTier, TeamsServices.Of("https://api.spaces.skype.com/"));

        // DoD: Microsoft's endpoint list names *.dod.teams.microsoft.us as GCC High's names *.gov.teams.microsoft.us; its
        // services' audiences are taken to follow GCC High's with dod for gov (no DoD tenant to see them in)
        Assert.AreEqual(TeamsService.ChatService, TeamsServices.Of("https://ic3.dod.teams.microsoft.us"));
        Assert.AreEqual(TeamsService.Aggregator, TeamsServices.Of("https://chatsvcagg.dod.teams.microsoft.us"));
        Assert.AreEqual(TeamsService.SharePoint, TeamsServices.Of("https://agency.sharepoint-mil.us"));

        // SharePoint's, for the files: its app id in both clouds, or a tenant's host
        Assert.AreEqual(TeamsService.SharePoint, TeamsServices.Of("00000003-0000-0ff1-ce00-000000000000"));
        Assert.AreEqual(TeamsService.SharePoint, TeamsServices.Of("https://contoso.sharepoint.us"));

        // Graph's, for groups.md: each cloud's host, or its app id
        Assert.AreEqual(TeamsService.Graph, TeamsServices.Of("https://graph.microsoft.us"));
        Assert.AreEqual(TeamsService.Graph, TeamsServices.Of("https://graph.microsoft.com/"));
        Assert.AreEqual(TeamsService.Graph, TeamsServices.Of("https://dod-graph.microsoft.us"));
        Assert.AreEqual(TeamsService.Graph, TeamsServices.Of("00000003-0000-0000-c000-000000000000"));

        // the client's other tokens are not the source's
        Assert.IsNull(TeamsServices.Of("https://presence.gov.teams.microsoft.us/"));
        Assert.IsNull(TeamsServices.Of("https://outlook.office365.us"));
        Assert.IsNull(TeamsServices.Of("https://graph.windows.net"), "Azure AD Graph, retired, is not Microsoft Graph");
        Assert.IsNull(TeamsServices.Of(null));
    }
}
