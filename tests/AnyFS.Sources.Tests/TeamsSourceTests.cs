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
    public void ThePluginNamesTheSourceFromTheAccount()
    {
        var plugin = new TeamsPlugin();
        Assert.AreEqual("jane (contoso.com)", plugin.SuggestName(new TeamsSettings { Account = "jane@contoso.com" }));
        Assert.AreEqual("teams", plugin.TypeId);
    }
}
