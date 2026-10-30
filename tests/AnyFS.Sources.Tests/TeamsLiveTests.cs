using System.Text;
using AnyFS.Fixtures;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Common;
using AnyFS.Sources.Microsoft365;
using AnyFS.Sources.Teams;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>The account's cloud, from what the identity platform publishes for its domain: the sandbox is in the global cloud (region AS, Singapore).</summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task TheAccountsCloudIsFoundFromItsDomain()
    {
        var (account, _) = Env();
        var realm = await MicrosoftRealm.LookupAsync(account, null, CancellationToken.None);
        Assert.IsNotNull(realm, "the identity platform names the sandbox's organisation");
        Assert.AreEqual(("microsoftonline.com", "AS", "graph.microsoft.com"), (realm.CloudInstance, realm.Region, realm.GraphHost));
        Assert.AreSame(TeamsCloud.Global, TeamsCloud.Of(realm));
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

    /// <summary>
    /// The libraries through Microsoft Graph alone, as at work when the page hands out Graph's token and not SharePoint's.
    /// The sandbox's Teams client mints no Graph token, so the AnyFS app's stands in (Files.ReadWrite.All, Sites.ReadWrite.All,
    /// no Group.Read.All), from a tests/fixtures/SharePointSignIn sign-in as the tester (ANYFS_TEST_GRAPH_CACHE names its file,
    /// else the default); SharePoint's token is withheld.
    /// </summary>
    [TestMethod]
    [Timeout(300_000)]
    public async Task TheLibrariesAreReadThroughGraphWithoutSharePointsToken()
    {
        var (account, _) = Env();
        var cache = Environment.GetEnvironmentVariable("ANYFS_TEST_GRAPH_CACHE") ?? SignInCacheFile.DefaultPath;
        if (SignInCacheFile.Read(cache) is not { } saved)
        {
            Assert.Inconclusive($"no sign-in at {cache}: run tests/fixtures/SharePointSignIn --account {account}");
            return;
        }

        var entra = new EntraSignIn(new SignInOptions(new Microsoft365Service("Teams (test)", "teams", "the libraries"), EntraSignIn.DefaultClientId, account.Split('@')[1], account,
            SignInMethod.DeviceCode, ["Files.ReadWrite.All", "Sites.ReadWrite.All"], _ => { }, null, () => saved, _ => { }), NullLogger.Instance);
        using (var signIn = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
        {
            try
            {
                await entra.SignInAsync(signIn.Token);
            }
            catch (Exception ex) when (ex is SignInRequiredException or OperationCanceledException)
            {
                Assert.Inconclusive($"the saved sign-in for {account} has lapsed: run tests/fixtures/SharePointSignIn --account {account} ({ex.Message})");
            }
        }

        var graph = await entra.GetAccessTokenAsync(CancellationToken.None);
        var browser = BrowserLocator.Find(null, "ANYFS_TEAMS_BROWSER") ?? throw new AssertInconclusiveException("no browser for the web session");
        var session = new TeamsWebSession(new Uri(TeamsCloud.Global.TeamsHost), browser, WebSignInTarget.ProfileFor("teams-browser", account, "ANYFS_TEAMS_PROFILES"), account, NullLogger.Instance);
        await session.TokenAsync(TeamsService.Aggregator, interactive: false, _ => { }, CancellationToken.None);
        var rest = new TeamsRest(new Uri(TeamsCloud.Global.ApiHost), (s, _, c) => s switch
        {
            TeamsService.Graph => Task.FromResult(graph),
            TeamsService.SharePoint => throw TokenNotHandedOutException.Refusal(s),
            _ => session.TokenAsync(s, interactive: false, _ => { }, c),
        }, 4, NullLogger.Instance, inHand: s => s == TeamsService.Graph ? graph : null)
        {
            Account = session.Account ?? account,
            Mri = session.Mri,
        };
        await using var source = new TeamsSource(new TeamsSettings { Account = account }, new TeamsStandIn(rest));
        await source.InitialiseAsync(new TestSourceContext(), CancellationToken.None);

        // Other groups from Microsoft Search, the tester's own team told by its site; a group's library
        var others = (await ListAsync(source, "Other groups")).Select(e => e.Name).ToList();
        CollectionAssert.Contains(others, "Mark 8 Project Team");
        CollectionAssert.DoesNotContain(others, "AnyFS Lab", "its team is at the root, though Search names no site's group");
        Assert.IsTrue((await ListAsync(source, "Other groups/Mark 8 Project Team")).Count > 0, "its library through Graph");

        // the channel's folder from filesFolder, and a file read through Graph's download address
        var files = (await ListAsync(source, "AnyFS Lab/General/files")).Select(e => e.Name).ToList();
        if (!files.Contains("anyfs-seed.txt"))
        {
            Assert.Inconclusive("the sandbox is not seeded (run TeamsSandboxSeed with ANYFS_SEED_TEAMS=1)");
        }

        CollectionAssert.Contains(files, "Specs");
        StringAssert.StartsWith(await TextAsync(source, "AnyFS Lab/General/files/anyfs-seed.txt"), "Seeded by AnyFS");

        // a thread's and a chat's shared files, opened by their own addresses (the shares API)
        var thread = (await ListAsync(source, "AnyFS Lab/General/posts")).First(p => p.Name.Contains("A thread with replies", StringComparison.Ordinal)).Name;
        CollectionAssert.Contains((await ListAsync(source, $"AnyFS Lab/General/posts/{thread}/attachments")).Select(e => e.Name).ToArray(), "anyfs-seed.txt");
        StringAssert.StartsWith(await TextAsync(source, $"AnyFS Lab/General/posts/{thread}/attachments/anyfs-seed.txt"), "Seeded by AnyFS");
        var chatFile = false;
        foreach (var chat in await ListAsync(source, "Chats"))
        {
            if ((await ListAsync(source, $"Chats/{chat.Name}")).Any(e => e.Name == "attachments"))
            {
                CollectionAssert.Contains((await ListAsync(source, $"Chats/{chat.Name}/attachments")).Select(e => e.Name).ToArray(), "anyfs-chat-seed.txt");
                StringAssert.StartsWith(await TextAsync(source, $"Chats/{chat.Name}/attachments/anyfs-chat-seed.txt"), "Shared in a chat");
                chatFile = true;
                break;
            }
        }

        Assert.IsTrue(chatFile, "no chat has the seeded attachments");

        // groups.md: this Graph token cannot list every group (no Group.Read.All, unlike GCC High's), nor is SharePoint's in
        // hand, so it holds the teams' own
        var md = await TextAsync(source, "groups.md");
        for (var deadline = DateTime.UtcNow.AddSeconds(30); md.Contains("being read", StringComparison.Ordinal) && DateTime.UtcNow < deadline; md = await TextAsync(source, "groups.md"))
        {
            await Task.Delay(250);
        }

        StringAssert.Contains(md, $"The groups of {session.Account ?? account}'s own teams");
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
