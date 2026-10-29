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
        CollectionAssert.AreEqual(new[] { "AnyFS Lab", "Chats" }, await NamesAsync(source, ""));
        CollectionAssert.AreEqual(new[] { "General", "Random" }, await NamesAsync(source, "AnyFS Lab"));
        CollectionAssert.AreEqual(new[] { "posts" }, await NamesAsync(source, "AnyFS Lab/General"));
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
    public void TokensAreMatchedToTheirServiceInEveryCloud()
    {
        // Microsoft 365 (spike 18) and GCC High (the sign-in audit at a GCC High tenant)
        Assert.AreEqual(TeamsService.ChatService, TeamsServices.Of("https://ic3.teams.office.com"));
        Assert.AreEqual(TeamsService.ChatService, TeamsServices.Of("https://ic3.gov.teams.microsoft.us"));
        Assert.AreEqual(TeamsService.Aggregator, TeamsServices.Of("https://chatsvcagg.teams.microsoft.com"));
        Assert.AreEqual(TeamsService.Aggregator, TeamsServices.Of("https://chatsvcagg.gov.teams.microsoft.us"));
        Assert.AreEqual(TeamsService.MiddleTier, TeamsServices.Of("https://api.spaces.skype.com"));
        Assert.AreEqual(TeamsService.MiddleTier, TeamsServices.Of("https://api.spaces.skype.com/"));

        // SharePoint's, for the files: its app id in both clouds, or a tenant's host
        Assert.AreEqual(TeamsService.SharePoint, TeamsServices.Of("00000003-0000-0ff1-ce00-000000000000"));
        Assert.AreEqual(TeamsService.SharePoint, TeamsServices.Of("https://contoso.sharepoint.us"));

        // the client's other tokens are not the source's
        Assert.IsNull(TeamsServices.Of("https://presence.gov.teams.microsoft.us/"));
        Assert.IsNull(TeamsServices.Of("https://outlook.office365.us"));
        Assert.IsNull(TeamsServices.Of("https://graph.microsoft.us"));
        Assert.IsNull(TeamsServices.Of(null));
    }
}
