using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Teams;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The Teams source against a real Teams account (spec 7.9): the web session signs in through a browser profile and reads
/// Microsoft's Teams services. Skipped unless <c>ANYFS_TEST_TEAMS_ACCOUNT</c> (the account, which the profile is signed in
/// as) and <c>ANYFS_TEAMS_PROFILES</c> (the root holding <c>teams-browser\&lt;account&gt;</c>, a signed-in profile) are set.
/// </summary>
[TestClass]
public class TeamsLiveTests
{
    private static (string Account, string Profiles) Env()
    {
        var account = Environment.GetEnvironmentVariable("ANYFS_TEST_TEAMS_ACCOUNT");
        var profiles = Environment.GetEnvironmentVariable("ANYFS_TEAMS_PROFILES");
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(profiles))
        {
            Assert.Inconclusive("set ANYFS_TEST_TEAMS_ACCOUNT and ANYFS_TEAMS_PROFILES (a signed-in teams-browser profile) to run against real Teams");
        }

        return (account, profiles);
    }

    private static async Task<TeamsSource> StartAsync(string account)
    {
        var source = new TeamsSource(new TeamsSettings { Account = account });
        await source.InitialiseAsync(new TestSourceContext(), CancellationToken.None);
        return source;
    }

    private static async Task<List<VfsEntry>> ListAsync(ISource source, string path)
    {
        var entries = new List<VfsEntry>();
        await foreach (var e in source.ListAsync(path, CancellationToken.None))
        {
            entries.Add(e);
        }

        return entries;
    }

    [TestMethod]
    [Timeout(300_000)]
    public async Task TheAccountsTeamsChannelsAndChatsAreServed()
    {
        var (account, _) = Env();
        await using var source = await StartAsync(account);

        var root = await ListAsync(source, "");
        Assert.IsTrue(root.Any(e => e.Name == "Chats"), "the root has a Chats folder");
        var teams = root.Where(e => e.Name != "Chats").ToList();
        Assert.IsTrue(teams.Count > 0, "the account is in at least one team (the sandbox seed)");

        // a team lists its channels, a channel its posts folder
        var channels = await ListAsync(source, teams[0].Name);
        Assert.IsTrue(channels.Count > 0, $"{teams[0].Name} has channels");
        var posts = await ListAsync(source, $"{teams[0].Name}/{channels[0].Name}");
        CollectionAssert.AreEqual(new[] { "posts" }, posts.Select(p => p.Name).ToArray());

        // the chats, each a chat.md
        var chats = await ListAsync(source, "Chats");
        Assert.IsTrue(chats.Count > 0, "the account has chats");
        var files = await ListAsync(source, $"Chats/{chats[0].Name}");
        CollectionAssert.AreEqual(new[] { "chat.md" }, files.Select(f => f.Name).ToArray());

        await using var chat = await source.OpenAsync($"Chats/{chats[0].Name}/chat.md", VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[chat.Length];
        await chat.ReadFullyAsync(0, buffer, CancellationToken.None);
        StringAssert.StartsWith(Encoding.UTF8.GetString(buffer), "# ", "chat.md opens with a heading");
    }

    private static async Task<string> TextAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length];
        await file.ReadFullyAsync(0, buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer);
    }

    /// <summary>The seeded sandbox (TeamsSandboxSeed): its thread's and chat's reactions, edit and deletion, as Markdown.</summary>
    [TestMethod]
    [Timeout(300_000)]
    public async Task TheSeededReactionsEditsAndDeletionsShow()
    {
        var (account, _) = Env();
        await using var source = await StartAsync(account);
        var posts = await ListAsync(source, "AnyFS Lab/General/posts");
        var thread = posts.FirstOrDefault(p => p.Name.Contains("A thread with replies", StringComparison.Ordinal))
            ?? throw new AssertInconclusiveException("the sandbox is not seeded (run TeamsSandboxSeed with ANYFS_SEED_TEAMS=1)");
        var md = await TextAsync(source, $"AnyFS Lab/General/posts/{thread.Name}/post.md");
        StringAssert.Contains(md, "👍 ");
        StringAssert.Contains(md, "❤️ ");
        StringAssert.Contains(md, "edited\n\nA reply, edited");
        StringAssert.Contains(md, "*This message was deleted.*");
        Assert.IsFalse(md.Contains("will be deleted", StringComparison.Ordinal));

        string? chatMd = null;
        foreach (var chat in await ListAsync(source, "Chats"))
        {
            var text = await TextAsync(source, $"Chats/{chat.Name}/chat.md");
            if (text.Contains("A chat message with a reaction", StringComparison.Ordinal))
            {
                chatMd = text;
                break;
            }
        }

        Assert.IsNotNull(chatMd, "the seeded chat");
        StringAssert.Contains(chatMd, "😆 ");
        StringAssert.Contains(chatMd, "edited\n\nA chat message, edited");
        StringAssert.Contains(chatMd, "*This message was deleted.*");
    }
}
