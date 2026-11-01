using System.Security.Cryptography;
using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Atlassian;
using AnyFS.Sources.Atlassian.Confluence;
using AnyFS.Sources.Atlassian.Jira;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The Jira and Confluence sources against the lab's servers (spec 7.10; provisioning/atlassian/README.md), whose seed
/// (ansible/files/atlassian-seed.sh) made what these read. Skipped unless the variables are set:
/// <c>ANYFS_TEST_JIRA_URL</c> (<c>http://192.168.0.78:8080/jira</c>) and <c>ANYFS_TEST_JIRA_TOKEN</c> (the tester's token,
/// provisioning/atlassian/secrets/jira-tester.token), <c>ANYFS_TEST_JIRA_USER</c> and <c>_PASSWORD</c> for the password
/// sign-in; the same for <c>CONFLUENCE</c>. "AnyFS local\spikes\atlassian\live-env.ps1" sets them without printing them.
/// </summary>
[TestClass]
public class AtlassianLiveTests
{
    /// <summary>The seed's large attachment: its line again and again, cut at 50 MiB (yes | head -c).</summary>
    private static readonly Lazy<string> LargeHash = new(() =>
    {
        var line = Encoding.ASCII.GetBytes("AnyFS Atlassian lab large attachment 0123456789abcdef\n");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long left = 52_428_800;
        while (left > 0)
        {
            var n = (int)Math.Min(line.Length, left);
            sha.AppendData(line, 0, n);
            left -= n;
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    });

    private static (string Url, string Token) Lab(string product)
    {
        var url = Environment.GetEnvironmentVariable($"ANYFS_TEST_{product}_URL");
        var token = Environment.GetEnvironmentVariable($"ANYFS_TEST_{product}_TOKEN");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
        {
            Assert.Inconclusive($"set ANYFS_TEST_{product}_URL and ANYFS_TEST_{product}_TOKEN to run against the lab's {product.ToLowerInvariant()}");
        }

        return (url.TrimEnd('/'), token.Trim());
    }

    private static (string User, string Password) Account(string product)
    {
        var user = Environment.GetEnvironmentVariable($"ANYFS_TEST_{product}_USER");
        var password = Environment.GetEnvironmentVariable($"ANYFS_TEST_{product}_PASSWORD");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            Assert.Inconclusive($"set ANYFS_TEST_{product}_USER and ANYFS_TEST_{product}_PASSWORD for the password sign-in");
        }

        return (user, password);
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

    private static async Task<List<string>> NamesAsync(ISource source, string path) => (await ListAsync(source, path)).Select(e => e.Name).ToList();

    private static async Task<string> TextAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length];
        var read = await file.ReadFullyAsync(0, buffer, CancellationToken.None);
        Assert.AreEqual(file.Length, read, path);
        return Encoding.UTF8.GetString(buffer);
    }

    private static async Task<string> HashAsync(ISource source, string path, long from = 0)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        for (var offset = from; offset < file.Length;)
        {
            var n = await file.ReadAsync(offset, buffer, CancellationToken.None);
            Assert.IsTrue(n > 0, $"{path} ended at {offset:N0} of {file.Length:N0}");
            sha.AppendData(buffer, 0, n);
            offset += n;
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    // --- Jira ----------------------------------------------------------------------------------------------------------

    private static async Task<JiraSource> JiraAsync(TestSourceContext context, string path = "", Action<JiraSettings>? change = null)
    {
        var (url, token) = Lab("JIRA");
        var settings = new JiraSettings { Address = url + path, AccessToken = token };
        change?.Invoke(settings);
        var source = new JiraSource(settings);
        await source.InitialiseAsync(context, CancellationToken.None);
        return source;
    }

    private static async Task<string> FolderStartingAsync(ISource source, string path, string summary)
        => (await NamesAsync(source, path)).Single(n => n.EndsWith(" " + summary, StringComparison.Ordinal));

    [TestMethod]
    [Timeout(300_000)]
    public async Task JiraShowsTheTestersProjectsAndNotTheHiddenOne()
    {
        using var context = new TestSourceContext();
        await using var source = await JiraAsync(context);
        var names = await NamesAsync(source, "");
        CollectionAssert.IsSubsetOf(new[] { "AnyFS Lab (ANYFS)", "Bulk issues (BULK)", "Operations (OPS)" }, names);
        Assert.IsFalse(names.Any(n => n.Contains("(HIDDEN)", StringComparison.Ordinal)), string.Join(", ", names));
        StringAssert.Contains(await TextAsync(source, "AnyFS Lab (ANYFS)/project.md"), "## Components");
    }

    [TestMethod]
    [Timeout(300_000)]
    public async Task JirasStoryReadsAsMarkdownWithItsAttachmentAndComments()
    {
        using var context = new TestSourceContext();
        await using var source = await JiraAsync(context, "/projects/ANYFS");
        var story = await FolderStartingAsync(source, "", "List a project's issues");
        CollectionAssert.AreEqual(new[] { "issue.md", "attachments" }, await NamesAsync(source, story));
        var md = await TextAsync(source, story + "/issue.md");
        StringAssert.Contains(md, "**Type:** Story");
        StringAssert.Contains(md, "**Labels:** listing, jira");
        StringAssert.Contains(md, "## What happens");
        StringAssert.Contains(md, "**stops**");
        StringAssert.Contains(md, "Requests at a time");
        StringAssert.Contains(md, "var source = new JiraSource(settings);");
        StringAssert.Contains(md, "attachments/diagram.png");
        StringAssert.Contains(md, "## Comments");
        StringAssert.Contains(md, "The first comment");
        CollectionAssert.AreEqual(new[] { "diagram.png" }, await NamesAsync(source, story + "/attachments"));
    }

    [TestMethod]
    [Timeout(600_000)]
    public async Task JirasAttachmentsKeepTheirNamesAndTheLargeOneReadsWhole()
    {
        using var context = new TestSourceContext();
        await using var source = await JiraAsync(context, "/projects/ANYFS");
        var task = await FolderStartingAsync(source, "", "Attachments with the same name");
        var folder = task + "/attachments";
        CollectionAssert.AreEquivalent(new[] { "notes.txt", "notes (2).txt", "spec.pdf", "large.bin" }, await NamesAsync(source, folder));
        Assert.AreEqual(LargeHash.Value, await HashAsync(source, folder + "/large.bin"));
        Assert.AreEqual("Notes kept beside the issue.\nSecond line.\n", await TextAsync(source, folder + "/notes.txt"));
    }

    [TestMethod]
    [Timeout(300_000)]
    public async Task JirasIssueWithAHundredCommentsHasThemAll()
    {
        using var context = new TestSourceContext();
        await using var source = await JiraAsync(context, "/projects/OPS");
        var many = await FolderStartingAsync(source, "", "An issue with a hundred comments");
        var md = await TextAsync(source, many + "/issue.md");
        StringAssert.Contains(md, "Comment number 1.");
        StringAssert.Contains(md, "Comment number 100.");
    }

    [TestMethod]
    [Timeout(600_000)]
    public async Task JirasWalkCountsEveryIssueAndAQuietProjectKeepsItsStamp()
    {
        using var context = new TestSourceContext();
        await using var source = await JiraAsync(context);
        await source.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
        Assert.IsTrue(source.Counters.FilesKnown >= 250 + 3, $"{source.Counters.FilesKnown:N0} files");
        Assert.AreEqual(251, (await NamesAsync(source, "Bulk issues (BULK)")).Count, "project.md and the 250 issues, paged by id");
        var first = await source.StampAsync("Bulk issues (BULK)", null, CancellationToken.None);
        Assert.AreEqual(first, await source.StampAsync("Bulk issues (BULK)", first, CancellationToken.None));
    }

    [TestMethod]
    [Timeout(300_000)]
    public async Task JiraTakesThePassword()
    {
        var (user, password) = Account("JIRA");
        using var context = new TestSourceContext();
        await using var source = await JiraAsync(context, "", s =>
        {
            s.SignInMethod = AtlassianSignInChoices.Password;
            s.Username = user;
            s.Password = password;
        });
        Assert.AreEqual(user, source.Session.User?.Name);
        CollectionAssert.Contains(await NamesAsync(source, ""), "AnyFS Lab (ANYFS)");
    }

    // --- Confluence ----------------------------------------------------------------------------------------------------

    private static async Task<ConfluenceSource> ConfluenceAsync(TestSourceContext context, string path = "", Action<ConfluenceSettings>? change = null)
    {
        var (url, token) = Lab("CONFLUENCE");
        var settings = new ConfluenceSettings { Address = url + path, AccessToken = token };
        change?.Invoke(settings);
        var source = new ConfluenceSource(settings);
        await source.InitialiseAsync(context, CancellationToken.None);
        return source;
    }

    [TestMethod]
    [Timeout(300_000)]
    public async Task ConfluenceShowsTheSpacesAndThePageTree()
    {
        using var context = new TestSourceContext();
        await using var source = await ConfluenceAsync(context);
        var spaces = await NamesAsync(source, "");
        CollectionAssert.IsSubsetOf(new[] { "AnyFS Lab (ANYFS)", "Bulk pages (BULK)" }, spaces);
        Assert.IsFalse(spaces.Any(s => s.Contains("(OLD)", StringComparison.Ordinal)), "the archived space shows only with its setting");
        var top = await NamesAsync(source, "AnyFS Lab (ANYFS)");
        CollectionAssert.Contains(top, "Blog");
        var home = top.Single(n => n != "Blog");
        var children = await NamesAsync(source, $"AnyFS Lab (ANYFS)/{home}");
        CollectionAssert.IsSubsetOf(new[] { home + ".md", "notes.txt", "Architecture", "Onboarding" }, children);
        Assert.IsFalse(children.Contains("Restricted page"), "a page the tester may not read");
        CollectionAssert.AreEquivalent(new[] { "Architecture.md", "diagram.png", "spec.pdf", "Architecture (2).md", "Services" },
            await NamesAsync(source, $"AnyFS Lab (ANYFS)/{home}/Architecture"));
    }

    [TestMethod]
    [Timeout(300_000)]
    public async Task ConfluencesPageReadsAsMarkdown()
    {
        using var context = new TestSourceContext();
        await using var source = await ConfluenceAsync(context, "/display/ANYFS/Architecture");
        var md = await TextAsync(source, "Architecture.md");
        StringAssert.Contains(md, "# Architecture");
        StringAssert.Contains(md, "## Layers");
        StringAssert.Contains(md, "**Info: Read-only**");
        StringAssert.Contains(md, "```csharp");
        StringAssert.Contains(md, "if (a < b && c > d) { }");
        StringAssert.Contains(md, "![diagram.png](diagram.png)");
        StringAssert.Contains(md, "[spec.pdf](spec.pdf)");
        StringAssert.Contains(md, "@AnyFS Tester");
        StringAssert.Contains(md, "- [x] Draw the diagram");
        StringAssert.Contains(md, "## Comments");
        Assert.IsFalse(md.Contains("ac:", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(600_000)]
    public async Task ConfluencesLargeAttachmentReadsWholeAndFromAnOffset()
    {
        using var context = new TestSourceContext();
        await using var source = await ConfluenceAsync(context, "/display/ANYFS/Services");
        Assert.AreEqual(LargeHash.Value, await HashAsync(source, "large.bin"));
    }

    [TestMethod]
    [Timeout(900_000)]
    public async Task ConfluencesWalkListsTheSpacesAsBrowsingWould()
    {
        using var walkedContext = new TestSourceContext();
        await using var walked = await ConfluenceAsync(walkedContext, "/display/ANYFS");
        await walked.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Complete, walked.Counters.State);

        using var browsedContext = new TestSourceContext();
        await using var browsed = await ConfluenceAsync(browsedContext, "/display/ANYFS");
        var pending = new Queue<string>([""]);
        while (pending.TryDequeue(out var path))
        {
            var fromWalk = (await walked.Listings.GetAsync(path, CancellationToken.None)).Select(e => $"{e.Name}|{e.IsDirectory}|{(e.IsDirectory ? 0 : e.Length)}").OrderBy(x => x, StringComparer.Ordinal).ToList();
            var entries = await ListAsync(browsed, path);
            var fromBrowse = entries.Select(e => $"{e.Name}|{e.IsDirectory}|{(e.IsDirectory ? 0 : e.Length)}").OrderBy(x => x, StringComparer.Ordinal).ToList();
            CollectionAssert.AreEqual(fromBrowse, fromWalk, $"'{path}' as browsed and as walked");
            foreach (var folder in entries.Where(e => e.IsDirectory))
            {
                pending.Enqueue(VfsPath.Join(path, folder.Name));
            }
        }
    }

    [TestMethod]
    [Timeout(300_000)]
    public async Task ConfluenceTakesThePassword()
    {
        var (user, password) = Account("CONFLUENCE");
        using var context = new TestSourceContext();
        await using var source = await ConfluenceAsync(context, "", s =>
        {
            s.SignInMethod = AtlassianSignInChoices.Password;
            s.Username = user;
            s.Password = password;
        });
        Assert.AreEqual(user, source.Session.User?.Name);
    }
}
