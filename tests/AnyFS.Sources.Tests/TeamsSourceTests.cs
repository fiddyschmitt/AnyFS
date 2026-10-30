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

    private static async Task<TeamsSource> StartAsync(FakeTeams fake, Action<TeamsSettings>? change = null)
    {
        var settings = new TeamsSettings { Account = fake.Account };
        change?.Invoke(settings);
        var source = new TeamsSource(settings, fake.StandIn());
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
        CollectionAssert.AreEqual(new[] { "General", "Random" }, await NamesAsync(source, "AnyFS Lab"));
        CollectionAssert.AreEqual(new[] { "posts", "files" }, await NamesAsync(source, "AnyFS Lab/General"));
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
        CollectionAssert.AreEqual(new[] { "posts" }, await NamesAsync(source, "AnyFS Lab/General"));
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
        CollectionAssert.AreEqual(new[] { "posts" }, await NamesAsync(source, "AnyFS Lab/General"));

        // it comes: the kept listings of the root and the channel are listed afresh, all-groups.md read from SharePoint
        hold.SetResult();
        CollectionAssert.AreEqual(new[] { "posts", "files" }, await UntilAsync(() => NamesAsync(source, "AnyFS Lab/General"), n => n.Contains("files")));
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
        CollectionAssert.AreEqual(new[] { "posts", "files" }, await NamesAsync(source, "AnyFS Lab/General"));
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

        CollectionAssert.AreEqual(new[] { "posts" }, await NamesAsync(source, "AnyFS Lab/General"));
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
    public async Task CountFilesWalksTheStructureButNotTheConversations()
    {
        var fake = Seed();
        await using var source = await StartAsync(fake);
        await source.CountFilesAsync(CancellationToken.None);

        // the walk lists the team, its channels and the chats; it does not open posts or a chat (those are messages calls)
        Assert.AreEqual(0, fake.MessageCalls, "a walk reads no conversation");
        Assert.AreEqual(EnumerationState.Unknown, source.Counters.State, "the conversations left out make it a lower bound");

        // a post is listed when opened, then the messages are fetched
        await NamesAsync(source, "AnyFS Lab/General/posts");
        Assert.IsTrue(fake.MessageCalls > 0);
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

    [TestMethod]
    public async Task HiddenChatsAreInAHiddenChatsFolder()
    {
        var fake = Seed();
        var hidden = fake.AddChat(ChatKind.Group, "Old project", [Jane], When, hidden: true);
        fake.Post(hidden.Id, Jane, "Jane Doe", "<p>Archived talk</p>", When);
        fake.AddChat(ChatKind.Group, "Hidden chats", [John], When);
        await using var source = await StartAsync(fake);

        CollectionAssert.AreEquivalent(new[] { "Jane Doe", "Project Axle", "Hidden chats (2)", "Hidden chats" }, await NamesAsync(source, "Chats"), "a chat named as the folder is numbered");
        CollectionAssert.AreEqual(new[] { "Old project" }, await NamesAsync(source, "Chats/Hidden chats"));
        StringAssert.Contains(await TextAsync(source, "Chats/Hidden chats/Old project/chat.md"), "Archived talk");
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
    public async Task ATeamNamedAsOneOfTheRootsOwnIsNumbered()
    {
        var fake = new FakeTeams();
        fake.AddTeam("Chats", FakeTeams.Channel("General", general: true));
        await using var source = await StartAsync(fake);
        var root = await NamesAsync(source, "");
        Assert.AreEqual("Chats (2)", root[0]);
        CollectionAssert.Contains(root, "Chats");
        CollectionAssert.AreEqual(new[] { "General" }, await NamesAsync(source, "Chats (2)"));
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
