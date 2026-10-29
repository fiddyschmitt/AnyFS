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

    /// <summary>
    /// Waits, up to two minutes, until what SharePoint's token opens is served: the page mints that token as it gets round to
    /// it and nothing waits on it (it is asked for behind the listings); Other groups at the root shows it is in hand.
    /// </summary>
    private static async Task SharePointServedAsync(ISource source)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!(await ListAsync(source, "")).Any(e => e.Name == "Other groups"))
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "Teams on the web handed out no SharePoint token in two minutes");
            await Task.Delay(1000);
        }
    }

    [TestMethod]
    [Timeout(300_000)]
    public async Task TheAccountsTeamsChannelsAndChatsAreServed()
    {
        var (account, _) = Env();
        await using var source = await StartAsync(account);
        await SharePointServedAsync(source);

        var root = await ListAsync(source, "");
        Assert.IsTrue(root.Any(e => e.Name == "Chats"), "the root has a Chats folder");
        var teams = root.Where(e => e.Name is not ("Chats" or "Other groups" or "groups.md")).ToList();
        Assert.IsTrue(teams.Count > 0, "the account is in at least one team (the sandbox seed)");

        // a team lists its channels, a channel its posts folder
        var channels = await ListAsync(source, teams[0].Name);
        Assert.IsTrue(channels.Count > 0, $"{teams[0].Name} has channels");
        var posts = await ListAsync(source, $"{teams[0].Name}/{channels[0].Name}");
        CollectionAssert.AreEqual(new[] { "posts", "files" }, posts.Select(p => p.Name).ToArray());

        // the chats, each a chat.md
        var chats = await ListAsync(source, "Chats");
        Assert.IsTrue(chats.Count > 0, "the account has chats");
        var files = await ListAsync(source, $"Chats/{chats[0].Name}");
        CollectionAssert.Contains(files.Select(f => f.Name).ToArray(), "chat.md", "and its attachments where it has any");

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

    /// <summary>The sandbox's groups: the sample groups the tester has not joined, with their files, and groups.md (from SharePoint: Microsoft 365's client gives no Graph token).</summary>
    [TestMethod]
    [Timeout(300_000)]
    public async Task TheOtherGroupsAndGroupsMdShowTheSandboxsGroups()
    {
        var (account, _) = Env();
        await using var source = await StartAsync(account);
        await SharePointServedAsync(source);

        var others = (await ListAsync(source, "Other groups")).Select(e => e.Name).ToList();
        CollectionAssert.Contains(others, "Mark 8 Project Team", "a public sample group the tester has not joined");
        CollectionAssert.DoesNotContain(others, "AnyFS Lab", "its team is at the root");
        Assert.IsTrue((await ListAsync(source, "Other groups/Mark 8 Project Team")).Count > 0, "its library, readable without joining");

        // groups.md is read behind the listings: the file says so until its reading with SharePoint's token lands
        var md = await TextAsync(source, "groups.md");
        for (var deadline = DateTime.UtcNow.AddMinutes(1); !md.Contains("from SharePoint", StringComparison.Ordinal) && DateTime.UtcNow < deadline; md = await TextAsync(source, "groups.md"))
        {
            await Task.Delay(500);
        }

        StringAssert.Contains(md, $"The groups {account} can see, from SharePoint");
        StringAssert.Contains(md, "| Mark 8 Project Team | Microsoft 365 group | Public |");
        StringAssert.Contains(md, "| AnyFS Lab | Microsoft 365 group | Private | yes | owner |");
        StringAssert.Contains(md, "| MSFT | Microsoft 365 group | Org-wide | yes | member |");
    }

    /// <summary>The seeded sandbox's files: the channel's folder of its team's library, a thread's and a chat's attachments.</summary>
    [TestMethod]
    [Timeout(300_000)]
    public async Task TheSeededFilesAndImagesAreServed()
    {
        var (account, _) = Env();
        await using var source = await StartAsync(account);
        await SharePointServedAsync(source);

        // the channel's files: its folder of the team's library, a subfolder too
        var files = (await ListAsync(source, "AnyFS Lab/General/files")).Select(e => e.Name).ToList();
        if (!files.Contains("anyfs-seed.txt"))
        {
            Assert.Inconclusive("the sandbox is not seeded (run TeamsSandboxSeed with ANYFS_SEED_TEAMS=1)");
        }

        CollectionAssert.Contains(files, "Specs");
        StringAssert.StartsWith(await TextAsync(source, "AnyFS Lab/General/files/anyfs-seed.txt"), "Seeded by AnyFS");
        StringAssert.StartsWith(await TextAsync(source, "AnyFS Lab/General/files/Specs/design.md"), "# Design");

        // the thread's attachments: the file a reply shares and the inline image, which its post.md links
        var thread = (await ListAsync(source, "AnyFS Lab/General/posts")).First(p => p.Name.Contains("A thread with replies", StringComparison.Ordinal)).Name;
        var attachments = (await ListAsync(source, $"AnyFS Lab/General/posts/{thread}/attachments")).Select(e => e.Name).ToList();
        CollectionAssert.Contains(attachments, "anyfs-seed.txt");

        // the media service serves its imgo view as JPEG, whatever was pasted
        var image = attachments.Single(a => a.Contains("_image-1.", StringComparison.Ordinal));
        StringAssert.EndsWith(image, ".jpg");
        await using (var jpeg = await source.OpenAsync($"AnyFS Lab/General/posts/{thread}/attachments/{image}", VfsOpenMode.Read, CancellationToken.None))
        {
            var head = new byte[3];
            await jpeg.ReadFullyAsync(0, head, CancellationToken.None);
            CollectionAssert.AreEqual(new byte[] { 0xFF, 0xD8, 0xFF }, head, "the image's bytes, from Teams' media service");
        }

        var md = await TextAsync(source, $"AnyFS Lab/General/posts/{thread}/post.md");
        StringAssert.Contains(md, "[anyfs-seed.txt](<attachments/anyfs-seed.txt>)");
        StringAssert.Contains(md, $"attachments/{image}");

        // the chat's: the file from the tester's OneDrive, and its image
        foreach (var chat in await ListAsync(source, "Chats"))
        {
            var names = (await ListAsync(source, $"Chats/{chat.Name}")).Select(e => e.Name).ToList();
            if (names.Contains("attachments"))
            {
                var chatFiles = (await ListAsync(source, $"Chats/{chat.Name}/attachments")).Select(e => e.Name).ToList();
                CollectionAssert.Contains(chatFiles, "anyfs-chat-seed.txt");
                Assert.IsTrue(chatFiles.Any(f => f.Contains("_image-1.", StringComparison.Ordinal)));
                StringAssert.StartsWith(await TextAsync(source, $"Chats/{chat.Name}/attachments/anyfs-chat-seed.txt"), "Shared in a chat");
                return;
            }
        }

        Assert.Fail("no chat has the seeded attachments");
    }
}
