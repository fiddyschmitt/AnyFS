using System.Security.Cryptography;
using System.Text;
using AnyFS.Fixtures;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.GitLab;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The GitLab source against a real GitLab (spec 7.8): the lab's (provisioning/gitlab/README.md), whose seed commits the
/// fixture tree to <c>anyfs/tools/app</c>, or any server with the same seed. Skipped unless <c>ANYFS_TEST_GITLAB_URL</c> (the
/// server, <c>http://anyfs-gitlab-1.anyfs.lab</c> or <c>http://192.168.0.85</c>) and <c>ANYFS_TEST_GITLAB_TOKEN</c> (the
/// tester's token, gitlab.tester_token in settings.yml) are set.
/// </summary>
[TestClass]
public class GitLabLiveTests
{
    private static (string Url, string Token) Server()
    {
        var url = Environment.GetEnvironmentVariable("ANYFS_TEST_GITLAB_URL");
        var token = Environment.GetEnvironmentVariable("ANYFS_TEST_GITLAB_TOKEN");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
        {
            Assert.Inconclusive("set ANYFS_TEST_GITLAB_URL and ANYFS_TEST_GITLAB_TOKEN to run against a GitLab with the lab's seed");
        }

        return (url.TrimEnd('/'), token);
    }

    private static async Task<GitLabSource> StartAsync(string address, string token, TestSourceContext context)
    {
        var source = new GitLabSource(new GitLabSettings { Address = address, AccessToken = token });
        await source.InitialiseAsync(context, CancellationToken.None);
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

    private static async Task<string> HashAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        for (long offset = 0; offset < file.Length;)
        {
            var n = await file.ReadAsync(offset, buffer, CancellationToken.None);
            Assert.IsTrue(n > 0, $"{path} ended at {offset:N0} of {file.Length:N0}");
            sha.AppendData(buffer, 0, n);
            offset += n;
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    [TestMethod]
    [Timeout(600_000)]
    public async Task TheFixtureTreeIsServedWholeFromTheLabsRepository()
    {
        var (url, token) = Server();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(url + "/anyfs/tools/app", token, context);

        // the fixture's own manifest (sha256  path), committed with it; empty folders are not kept by git
        var manifest = Encoding.UTF8.GetString(await ReadAsync(source, "files/e2e/fixture.sha256"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (Hash: l[..64], Path: l[66..].Replace('\\', '/'))).ToList();
        Assert.AreEqual(5_000 + 9, manifest.Count, "the fixture's files");
        foreach (var (hash, path) in manifest.Where(m => !m.Path.StartsWith("many/", StringComparison.Ordinal)))
        {
            Assert.AreEqual(hash, await HashAsync(source, "files/e2e/" + path), path);
        }

        var many = await ListAsync(source, "files/e2e/many");
        Assert.AreEqual(5_000, many.Count, "a folder of 5,000 files lists whole");
        Assert.AreEqual("file 4999\n", Encoding.UTF8.GetString(await ReadAsync(source, "files/e2e/many/file4999.txt")));
        var large = (await ListAsync(source, "files/e2e")).Single(e => e.Name == "large.bin");
        Assert.AreEqual(TreeGenerator.DefaultLargeSize, large.Length, "the LFS object's size, not its pointer's");
    }

    [TestMethod]
    public async Task BranchesAndTagsAreThere()
    {
        var (url, token) = Server();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(url + "/anyfs/tools/app", token, context);
        CollectionAssert.IsSubsetOf(new[] { "feature", "main" }, (await ListAsync(source, "branches")).Select(e => e.Name).ToArray());
        Assert.AreEqual("A change on a branch.\n", Encoding.UTF8.GetString(await ReadAsync(source, "branches/feature/x/feature.txt")));
        CollectionAssert.AreEqual(new[] { "v1.0", "v1.1" }, (await ListAsync(source, "tags")).Select(e => e.Name).ToArray());
        Assert.IsFalse((await ListAsync(source, "tags/v1.0")).Any(e => e.Name == "VERSION"), "v1.0 is the first commit");
        Assert.AreEqual("1.1\n", Encoding.UTF8.GetString(await ReadAsync(source, "tags/v1.1/VERSION")));
    }

    [TestMethod]
    public async Task TheServerShowsTheTestersGroups()
    {
        var (url, token) = Server();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(url, token, context);
        Assert.AreEqual(ScopeKind.Server, source.Scope);
        CollectionAssert.Contains((await ListAsync(source, "")).Select(e => e.Name).ToList(), "anyfs");
        CollectionAssert.AreEquivalent(new[] { "tools", "docs" }, (await ListAsync(source, "anyfs")).Select(e => e.Name).ToArray());
        Assert.AreEqual(ScopeKind.Server, source.Scope);
    }

    [TestMethod]
    public async Task TheDocsWikiIsThereAsItsPagesMarkup()
    {
        var (url, token) = Server();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(url + "/anyfs/docs", token, context);
        CollectionAssert.AreEqual(new[] { "guides", "home.md", "notes.asciidoc", "Ünïcödé-résumé.md" }, (await ListAsync(source, "wiki")).Select(e => e.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "install.md", "install" }, (await ListAsync(source, "wiki/guides")).Select(e => e.Name).ToArray());
        var home = Encoding.UTF8.GetString(await ReadAsync(source, "wiki/home.md"));
        StringAssert.StartsWith(home, $"{url}/anyfs/docs/-/wikis/home\n\n# Welcome\n");
        StringAssert.Contains(home, $"{url}/anyfs/docs/-/wikis/uploads/", "with a token the attachment's link points at GitLab");
        StringAssert.StartsWith(Encoding.UTF8.GetString(await ReadAsync(source, "wiki/notes.asciidoc")), $"// {url}/anyfs/docs/-/wikis/notes\n= Notes");
    }

    [TestMethod]
    public async Task TheDocsIssuesAndMergeRequestAreThereWithTheirFiles()
    {
        var (url, token) = Server();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(url + "/anyfs/docs", token, context);
        CollectionAssert.AreEqual(new[] { "#1 Install fails on Debian", "#2 An old question", "#3 Ünïcödé — résumé ✓ 日本語", "#4 Paths_ a_b _ c _ _ _q_ _x_ _p_" },
            (await ListAsync(source, "issues")).Select(e => e.Name).ToArray());
        const string first = "issues/#1 Install fails on Debian";
        var issue = Encoding.UTF8.GetString(await ReadAsync(source, first + "/issue.md"));
        StringAssert.Contains(issue, "**Labels:** bug, install");
        StringAssert.Contains(issue, "(attachments/steps.txt)");
        var comments = Encoding.UTF8.GetString(await ReadAsync(source, first + "/comments.md"));
        StringAssert.Contains(comments, "Fixed in the next build.");
        Assert.IsFalse(comments.Contains("assigned to", StringComparison.Ordinal));
        var attachments = await ListAsync(source, first + "/attachments");
        CollectionAssert.AreEquivalent(new[] { "steps.txt", "diagram.png", "data.bin" }, attachments.Select(e => e.Name).ToArray());
        Assert.AreEqual(102_400, attachments.Single(e => e.Name == "data.bin").Length);
        Assert.AreEqual("Steps to reproduce, as text.\n", Encoding.UTF8.GetString(await ReadAsync(source, first + "/attachments/steps.txt")));
        var patch = Encoding.UTF8.GetString(await ReadAsync(source, "merge requests/!1 Docs_ say more/changes.diff"));
        StringAssert.StartsWith(patch, "diff --git a/docs/more.md b/docs/more.md\nnew file mode 100644\n");
    }

    private static async Task<byte[]> ReadAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length];
        var read = await file.ReadFullyAsync(0, buffer, CancellationToken.None);
        Assert.AreEqual(buffer.Length, read);
        return buffer;
    }
}
