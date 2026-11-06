using System.Text;
using System.Text.RegularExpressions;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Atlassian;
using AnyFS.Sources.Atlassian.Bitbucket;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>A Bitbucket server as the fake holds it (spec 7.11): the accounts, projects, repositories, commits and pull requests the tests read.</summary>
internal static class BitbucketSeed
{
    public const string Token = "bitbucket-tester-token";
    public const string Password = "tester-password";

    public static readonly DateTimeOffset Day = new(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);

    /// <summary>A megabyte and a bit, the same every time: the large file.</summary>
    public static readonly byte[] Large = Enumerable.Range(0, (1 << 20) + 77).Select(i => (byte)(i * 13 % 251)).ToArray();

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// Two projects the tester sees (ANYFS with the repositories Docs and Empty, OPS with Scripts), one hidden from them, and
    /// their own. Docs: five files on main (one of them large, a submodule beside them), feature/x one file ahead, a branch
    /// named Feature (a twin of feature's folder in case), the tags v1.0 at the first commit and release/2.0 at feature/x;
    /// an open pull request with comments, a reply, an approval and a diff, and a merged one with a name Windows refuses.
    /// </summary>
    public static FakeBitbucket Lab(string root = "/bitbucket")
    {
        var fake = new FakeBitbucket(root: root);
        fake.AddAccount("tester", "Test Er", Token, Password);
        fake.AddAccount("other", "Someone Else", "bitbucket-other-token");

        var anyfs = fake.AddProject("ANYFS", "AnyFS Lab");
        anyfs.Description = "The lab's own project.";
        var docs = fake.AddRepository(anyfs, "docs", "Docs");
        docs.Description = "The documentation.";
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["README.md"] = Bytes("# Docs\n\nRead me.\n"),
            ["docs/guide.md"] = Bytes("# Guide\n\nStep one.\n"),
            ["docs/images/logo.png"] = [137, 80, 78, 71, 1, 2, 3],
            ["docs/large.bin"] = Large,
            ["src/main.py"] = Bytes("print('hello')\n"),
            ["vendor/lib/.gitmodule"] = Bytes("submodule"),
        };
        docs.Submodules.Add("vendor/lib");
        var first = docs.Commit("main", files, Day, message: "The first commit");
        docs.Tags["v1.0"] = first;
        var branch = new Dictionary<string, byte[]>(files, StringComparer.Ordinal) { ["feature.txt"] = Bytes("on feature/x\n") };
        docs.Commit("feature/x", branch, Day.AddDays(1), message: "A feature");
        docs.Commit("Feature", new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["capital.txt"] = Bytes("the branch named Feature\n") }, Day.AddDays(2));
        docs.Tags["release/2.0"] = docs.Branches["feature/x"];

        var guide = fake.AddPullRequest(docs, "Add the guide", "tester", "feature/x", "main", pr =>
        {
            pr.Description = "Adds the **guide**, and a path \"C:\\temp\" <here> | or there.\n\n- lists\n- too";
            pr.Created = Day.AddDays(1);
            pr.Updated = Day.AddDays(1);
            pr.Reviewers.Add(("other", "UNAPPROVED"));
            pr.Diff = "diff --git a/feature.txt b/feature.txt\nnew file mode 100644\n--- /dev/null\n+++ b/feature.txt\n@@ -0,0 +1 @@\n+on feature/x\n";
        });
        var question = fake.AddComment(guide, "other", "Is step one *really* first?", Day.AddDays(1).AddHours(1), "docs/guide.md", 3);
        fake.Reply(guide, question, "tester", "Yes, it is.", Day.AddDays(1).AddHours(2));
        fake.AddComment(guide, "tester", "A general remark.", Day.AddDays(1).AddHours(3));
        fake.Approve(guide, "other", Day.AddDays(1).AddHours(4));

        var old = fake.AddPullRequest(docs, "Fix: path \"C:\\temp\\a*b?\" <fails> | sometimes", "other", "fix/paths", "main", pr =>
        {
            pr.Created = Day.AddDays(-5);
            pr.Updated = Day.AddDays(-4);
        });
        fake.Merge(old, "tester", Day.AddDays(-4));

        fake.AddRepository(anyfs, "empty", "Empty");
        var ops = fake.AddProject("OPS", "Operations");
        var scripts = fake.AddRepository(ops, "scripts", "Scripts");
        scripts.Commit("master", new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["run.sh"] = Bytes("#!/bin/sh\n") }, Day);
        var hidden = fake.AddProject("HIDDEN", "Hidden from the tester", "other");
        fake.AddRepository(hidden, "secret", "Secret").Commit("main", new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["s.txt"] = Bytes("s") }, Day);
        var personal = fake.AddProject("~TESTER", "Test Er");
        fake.AddRepository(personal, "notes", "Notes").Commit("main", new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["notes.txt"] = Bytes("mine\n") }, Day);
        return fake;
    }

    public static BitbucketSettings Settings(FakeBitbucket fake, string path = "", string token = Token) => new()
    {
        Address = fake.Server + path,
        AccessToken = token,
        RefreshSeconds = 60,
        RepositoriesAtATime = 2,
        RequestsPerRepository = 2,
    };
}

/// <summary>The Bitbucket source against <see cref="FakeBitbucket"/> (spec 7.11): the address, the sign-ins, the tree, the files, the pull requests and the walk.</summary>
[TestClass]
public class BitbucketTests
{
    private const string Docs = "AnyFS Lab (ANYFS)/Docs";

    private static async Task<BitbucketSource> StartAsync(FakeBitbucket fake, BitbucketSettings settings, TestSourceContext? context = null, AtlassianStandIn? standIn = null)
    {
        var source = new BitbucketSource(settings, standIn ?? fake.StandIn());
        await source.InitialiseAsync(context ?? new TestSourceContext(), CancellationToken.None);
        return source;
    }

    private static async Task<List<string>> NamesAsync(ISource source, string path) => (await EntriesAsync(source, path)).Select(e => e.Name).ToList();

    private static async Task<List<VfsEntry>> EntriesAsync(ISource source, string path)
    {
        var entries = new List<VfsEntry>();
        await foreach (var e in source.ListAsync(path, CancellationToken.None))
        {
            entries.Add(e);
        }

        return entries;
    }

    private static async Task<byte[]> ReadAsync(ISource source, string path, long offset = 0)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length - offset];
        var read = await file.ReadFullyAsync(offset, buffer, CancellationToken.None);
        return buffer[..read];
    }

    private static async Task<string> TextAsync(ISource source, string path) => Encoding.UTF8.GetString(await ReadAsync(source, path));

    private static List<string> CountProgress(TestSourceContext context)
    {
        lock (context.Progress)
        {
            return context.Progress.Where(p => p.Message.StartsWith("Counting files", StringComparison.Ordinal)).Select(p => p.Message).ToList();
        }
    }

    // --- the address ---------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("", "Server", "")]
    [DataRow("dashboard", "Server", "")]
    [DataRow("projects", "Server", "")]
    [DataRow("projects/ABC", "Project", "ABC")]
    [DataRow("projects/abc/repos", "Project", "ABC")]
    [DataRow("projects/abc/repos/Docs/browse/src/main.py?at=refs%2Fheads%2Fmain", "Repository", "ABC/docs")]
    [DataRow("projects/ABC/repos/docs/pull-requests/12/overview", "Repository", "ABC/docs")]
    [DataRow("scm/abc/docs.git", "Repository", "ABC/docs")]
    [DataRow("users/jane", "Project", "~JANE")]
    [DataRow("users/jane/repos/notes/browse", "Repository", "~JANE/notes")]
    [DataRow("projects/~jane/repos/notes", "Repository", "~JANE/notes")]
    public void TheAddressNamesTheScope(string rest, string kind, string value)
    {
        var address = AtlassianAddress.Candidates(new Uri("https://bitbucket.example.com/" + rest)).First();
        var scope = BitbucketScope.From(address, out var problem);
        Assert.IsNotNull(scope, problem);
        Assert.AreEqual(kind, scope.Kind.ToString());
        Assert.AreEqual(value, scope.Value);
    }

    [TestMethod]
    public void AnAddressThatNamesNothingSaysWhat()
    {
        var address = AtlassianAddress.Candidates(new Uri("https://bitbucket.example.com/admin/users")).First();
        Assert.IsNull(BitbucketScope.From(address, out var problem));
        StringAssert.Contains(problem, "a repository's");
        Assert.AreEqual(12, BitbucketScope.NumberOfFolder("#12 Fix the build"));
        Assert.IsNull(BitbucketScope.NumberOfFolder("pull-request.md"));
        Assert.AreEqual("ABC", BitbucketScope.KeyOfProjectFolder("Alpha (project) (ABC)"));
        Assert.IsNull(BitbucketScope.KeyOfProjectFolder("Alpha"));
    }

    [TestMethod]
    public async Task AnInstallationAtAContextPathIsFoundBelowTheHostAndTheAccountIsNamedByTheHeader()
    {
        var fake = BitbucketSeed.Lab();
        await using var source = await StartAsync(fake, BitbucketSeed.Settings(fake, "/projects/ANYFS/repos/docs/browse"));
        Assert.AreEqual("https://bitbucket.fake.test/bitbucket/", source.Session.Address.Base.AbsoluteUri);
        Assert.AreEqual("9.4.2", source.Session.Version);
        Assert.AreEqual(BitbucketScopeKind.Repository, source.Scope.Kind);
        Assert.AreEqual("tester", source.Session.User?.Name);
        Assert.AreEqual("Test Er", source.Session.User?.DisplayName, "the account X-AUSERNAME names, its display name from the users call");
    }

    [TestMethod]
    public async Task AJiraServerIsNamedForWhatItIs()
    {
        var jira = new FakeJira("https://bitbucket.fake.test", "/bitbucket");
        jira.AddAccount("tester", "Test Er", BitbucketSeed.Token);
        var source = new BitbucketSource(new BitbucketSettings { Address = jira.Server, AccessToken = BitbucketSeed.Token }, jira.StandIn());
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        StringAssert.Contains(ex.Message, "is Jira, not Bitbucket");
        await source.DisposeAsync();
    }

    // --- signing in ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ARefusedTokenFailsForGood()
    {
        var fake = BitbucketSeed.Lab();
        var source = new BitbucketSource(BitbucketSeed.Settings(fake, token: "wrong"), fake.StandIn());
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        Assert.IsTrue(ex.Permanent);
        StringAssert.Contains(ex.Message, "refused the personal access token");
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task APasswordSignsInThroughBitbucketsOwnForm()
    {
        var fake = BitbucketSeed.Lab();
        fake.TwoStepLogin = false;
        var settings = new BitbucketSettings { Address = fake.Server, SignInMethod = AtlassianSignInChoices.Password, Username = "tester", Password = BitbucketSeed.Password };
        await using var source = await StartAsync(fake, settings);
        Assert.AreEqual("tester", source.Session.User?.Name);
        Assert.AreEqual(1, fake.Count("POST /bitbucket/j_atl_security_check"));
        CollectionAssert.Contains(await NamesAsync(source, ""), "AnyFS Lab (ANYFS)");
    }

    [TestMethod]
    public async Task AWebSessionsCookiesSignIn()
    {
        var fake = BitbucketSeed.Lab();
        var settings = new BitbucketSettings { Address = fake.Server + "/projects/OPS", SignInMethod = AtlassianSignInChoices.WebSession };
        var standIn = fake.StandIn((_, _) => Task.FromResult<IReadOnlyList<SessionCookie>>([fake.SessionFor("tester")]));
        await using var source = await StartAsync(fake, settings, standIn: standIn);
        Assert.AreEqual("tester", source.Session.User?.Name);
        CollectionAssert.AreEqual(new[] { "Scripts" }, await NamesAsync(source, ""));
    }

    // --- the tree ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheServerShowsTheProjectsTheAccountCanSeeAndARepositoryItsFolders()
    {
        var fake = BitbucketSeed.Lab();
        await using var source = await StartAsync(fake, BitbucketSeed.Settings(fake));
        CollectionAssert.AreEqual(new[] { "AnyFS Lab (ANYFS)", "Operations (OPS)" }, await NamesAsync(source, ""), "the hidden project and the person's own left out");
        CollectionAssert.AreEqual(new[] { "Docs", "Empty" }, await NamesAsync(source, "AnyFS Lab (ANYFS)"));
        CollectionAssert.AreEqual(new[] { "files", "branches", "tags", "pull requests" }, await NamesAsync(source, Docs));
        CollectionAssert.AreEqual(new[] { "branches", "tags", "pull requests" }, await NamesAsync(source, "AnyFS Lab (ANYFS)/Empty"), "no files in a repository with no commits");
        Assert.AreEqual(0, (await NamesAsync(source, "AnyFS Lab (ANYFS)/Empty/branches")).Count);
    }

    [TestMethod]
    public async Task APersonsOwnProjectIsAScopeOfItsOwn()
    {
        var fake = BitbucketSeed.Lab();
        await using var source = await StartAsync(fake, BitbucketSeed.Settings(fake, "/users/tester/repos/notes"));
        Assert.AreEqual("~TESTER/notes", source.Scope.Value);
        Assert.AreEqual("mine\n", await TextAsync(source, "files/notes.txt"));
    }

    [TestMethod]
    public async Task FilesAreTheDefaultBranchWholeWithTheirSizesAndTimes()
    {
        var fake = BitbucketSeed.Lab();
        await using var source = await StartAsync(fake, BitbucketSeed.Settings(fake, "/projects/ANYFS/repos/docs"));
        var top = await EntriesAsync(source, "files");
        CollectionAssert.AreEqual(new[] { "docs", "src", "vendor", "README.md" }, top.Select(e => e.Name).ToArray(), "folders first, then files, each by name");
        Assert.AreEqual(17, top.Single(e => e.Name == "README.md").Length);
        Assert.AreEqual(BitbucketSeed.Day, top.Single(e => e.Name == "README.md").Modified, "the commit's time");
        CollectionAssert.AreEqual(new[] { "images", "guide.md", "large.bin" }, await NamesAsync(source, "files/docs"));
        Assert.AreEqual("# Guide\n\nStep one.\n", await TextAsync(source, "files/docs/guide.md"));
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 1, 2, 3 }, await ReadAsync(source, "files/docs/images/logo.png"));
        Assert.AreEqual(BitbucketSeed.Large.Length, (await source.StatAsync("files/docs/large.bin", CancellationToken.None))!.Length);
        Assert.AreEqual(0, (await NamesAsync(source, "files/vendor/lib")).Count, "a submodule is a folder with nothing in it");
        Assert.IsNull(await source.StatAsync("files/nothing.txt", CancellationToken.None));
    }

    [TestMethod]
    public async Task ALargeFileReadsWholeAndFromAnOffset()
    {
        var fake = BitbucketSeed.Lab();
        await using var source = await StartAsync(fake, BitbucketSeed.Settings(fake, "/projects/ANYFS/repos/docs"));
        CollectionAssert.AreEqual(BitbucketSeed.Large, await ReadAsync(source, "files/docs/large.bin"));
        CollectionAssert.AreEqual(BitbucketSeed.Large[500_000..], await ReadAsync(source, "files/docs/large.bin", 500_000));
    }

    [TestMethod]
    public async Task BranchesAndTagsAreTheRepositoryAtTheirCommits()
    {
        var fake = BitbucketSeed.Lab();
        await using var source = await StartAsync(fake, BitbucketSeed.Settings(fake, "/projects/ANYFS/repos/docs"));
        CollectionAssert.AreEqual(new[] { "Feature", "feature (2)", "main" }, await NamesAsync(source, "branches"), "the branch Feature and the folder of feature/x are told apart");
        CollectionAssert.AreEqual(new[] { "x" }, await NamesAsync(source, "branches/feature (2)"), "a slash in a name makes a folder");
        Assert.AreEqual("on feature/x\n", await TextAsync(source, "branches/feature (2)/x/feature.txt"));
        CollectionAssert.AreEqual(new[] { "capital.txt" }, await NamesAsync(source, "branches/Feature"));
        CollectionAssert.DoesNotContain(await NamesAsync(source, "branches/main"), "feature.txt");
        CollectionAssert.AreEqual(new[] { "release", "v1.0" }, await NamesAsync(source, "tags"));
        Assert.AreEqual("on feature/x\n", await TextAsync(source, "tags/release/2.0/feature.txt"), "a tag at the branch's commit");
        Assert.AreEqual(BitbucketSeed.Day.AddDays(1), (await source.StatAsync("tags/release/2.0", CancellationToken.None))!.Modified, "dated by its commit");
    }

    [TestMethod]
    public async Task WithoutBranchesAndTagsOrPullRequestsARepositoryHasItsFilesAlone()
    {
        var fake = BitbucketSeed.Lab();
        var settings = BitbucketSeed.Settings(fake, "/projects/ANYFS/repos/docs");
        settings.BranchesAndTags = false;
        settings.PullRequests = false;
        await using var source = await StartAsync(fake, settings);
        CollectionAssert.AreEqual(new[] { "files" }, await NamesAsync(source, ""));
    }

    // --- pull requests -------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task APullRequestIsItsMarkdownAndItsDiff()
    {
        var fake = BitbucketSeed.Lab();
        await using var source = await StartAsync(fake, BitbucketSeed.Settings(fake, "/projects/ANYFS/repos/docs"));
        var names = await NamesAsync(source, "pull requests");
        CollectionAssert.AreEqual(new[] { "#2 Fix_ path _C__temp_a_b__ _fails_ _ sometimes", "#1 Add the guide" }, names, "newest first, names Windows takes");
        CollectionAssert.AreEqual(new[] { "pull-request.md", "changes.diff" }, await NamesAsync(source, "pull requests/#1 Add the guide"));

        var md = await TextAsync(source, "pull requests/#1 Add the guide/pull-request.md");
        StringAssert.StartsWith(md, "# #1 Add the guide\n\n**State:** Open  \n**Author:** Test Er (tester)  \n**From:** feature/x (");
        StringAssert.Contains(md, "**To:** main (");
        StringAssert.Contains(md, "**Reviewers:** Someone Else (other) (approved)  \n");
        StringAssert.Contains(md, "**Web:** https://bitbucket.fake.test/bitbucket/projects/ANYFS/repos/docs/pull-requests/1  \n\n---\n\n");
        StringAssert.Contains(md, "Adds the **guide**, and a path \"C:\\temp\" <here> | or there.\n\n- lists\n- too\n\n## Activity\n");
        StringAssert.Contains(md, "\n### Someone Else (other), ");
        StringAssert.Contains(md, " (on docs/guide.md:3)\n\nIs step one *really* first?\n");
        StringAssert.Contains(md, "\n#### Test Er (tester), ");
        StringAssert.Contains(md, "\n\nYes, it is.\n");
        StringAssert.Contains(md, "\n\nA general remark.\n");
        StringAssert.Contains(md, ": approved\n");
        Assert.IsTrue(md.IndexOf("Is step one", StringComparison.Ordinal) < md.IndexOf("A general remark", StringComparison.Ordinal), "oldest first");
        Assert.IsTrue(md.IndexOf("A general remark", StringComparison.Ordinal) < md.IndexOf(": approved", StringComparison.Ordinal), "oldest first");

        var diff = await TextAsync(source, "pull requests/#1 Add the guide/changes.diff");
        StringAssert.StartsWith(diff, "diff --git a/feature.txt b/feature.txt\n");

        var merged = await TextAsync(source, "pull requests/" + names[0] + "/pull-request.md");
        StringAssert.StartsWith(merged, "# #2 Fix: path \"C:\\temp\\a*b?\" <fails> | sometimes\n\n**State:** Merged  \n");
        StringAssert.Contains(merged, "**Merged:** ");
        StringAssert.Contains(merged, ": merged\n");
        CollectionAssert.AreEqual(new[] { "pull-request.md" }, await NamesAsync(source, "pull requests/" + names[0]), "no diff where it has none");
    }

    [TestMethod]
    public async Task OpenPullRequestsOnlyLeavesTheMergedOut()
    {
        var fake = BitbucketSeed.Lab();
        var settings = BitbucketSeed.Settings(fake, "/projects/ANYFS/repos/docs");
        settings.OpenOnly = true;
        await using var source = await StartAsync(fake, settings);
        CollectionAssert.AreEqual(new[] { "#1 Add the guide" }, await NamesAsync(source, "pull requests"));
        Assert.IsNull(await source.StatAsync("pull requests/#2 Fix_ path _C__temp_a_b__ _fails_ _ sometimes", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync("pull requests/#2 Fix_ path _C__temp_a_b__ _fails_ _ sometimes/pull-request.md", VfsOpenMode.Read, CancellationToken.None));
    }

    [TestMethod]
    public async Task APullRequestIsFoundByItsNumberWhateverItsTitleBecame()
    {
        var fake = BitbucketSeed.Lab();
        var settings = BitbucketSeed.Settings(fake, "/projects/ANYFS/repos/docs");
        settings.RefreshSeconds = 1;
        await using var source = await StartAsync(fake, settings);
        CollectionAssert.Contains(await NamesAsync(source, "pull requests"), "#1 Add the guide");

        // the title changes; the listing is kept as it was, and the name it gives opens the request as it is now
        fake.RepositoryAt("ANYFS", "docs").PullRequests[0].Title = "Add the guide, revised";
        await Task.Delay(1200);
        CollectionAssert.Contains(await NamesAsync(source, "pull requests"), "#1 Add the guide");
        StringAssert.StartsWith(await TextAsync(source, "pull requests/#1 Add the guide/pull-request.md"), "# #1 Add the guide, revised\n");
        Assert.AreEqual(1, fake.Requests.Count(r => r.EndsWith("/pull-requests/1", StringComparison.Ordinal)), "read alone, by its number");
        Assert.IsNull(await source.StatAsync("pull requests/#7 Nothing", CancellationToken.None));
    }

    // --- the walk ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheCountWalksEveryRepositoryWholeAndSaysHowFarItHasGot()
    {
        var fake = BitbucketSeed.Lab();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, BitbucketSeed.Settings(fake), context);
        await source.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State);

        // Docs: 5 files on main, the branches main 5, feature/x 6 and Feature 1, the tags v1.0 5 and release/2.0 6, two pull
        // requests with three files between them; Scripts: 1 on master and 1 on the branch; Empty nothing
        Assert.AreEqual(5 + (5 + 6 + 1) + (5 + 6) + 3 + 2, source.Counters.FilesKnown);
        var said = CountProgress(context);
        Assert.AreEqual("Counting files: listing the repositories", said[0]);
        Assert.AreEqual("Counting files: 1 of 3 repositories", said[1]);
        Assert.AreEqual("Counting files: 3 of 3 repositories", said[^1]);

        // every folder the walk went into answers from what it kept: a file's bytes are the one thing asked for
        var asked = fake.Requests.Count;
        Assert.AreEqual("on feature/x\n", await TextAsync(source, Docs + "/tags/release/2.0/feature.txt"));
        CollectionAssert.AreEqual(new[] { "pull-request.md", "changes.diff" }, await NamesAsync(source, Docs + "/pull requests/#1 Add the guide"));
        Assert.AreEqual(asked + 1, fake.Requests.Count, ListEverything.Since(fake.Requests, asked));
    }

    [TestMethod]
    public async Task AfterTheCountAPassOverEverythingAsksTheServerNothing()
    {
        var fake = BitbucketSeed.Lab();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, BitbucketSeed.Settings(fake), context);
        await source.CountFilesAsync(CancellationToken.None);
        await UpstreamRequestRows.AllEndAsync(context);
        var before = fake.Requests.Count;
        var folders = await ListEverything.OfAsync(source);
        Assert.IsTrue(folders > 30, folders.ToString());
        Assert.AreEqual(before, fake.Requests.Count, "asked for after the walk: " + ListEverything.Since(fake.Requests, before));
    }

    [TestMethod]
    public async Task AWalkOverAQuietServerAsksTheListingsAloneAndAChangeShowsAfterTheNextWalk()
    {
        var fake = BitbucketSeed.Lab();
        var settings = BitbucketSeed.Settings(fake);
        settings.RefreshSeconds = 1;
        await using var source = await StartAsync(fake, settings);
        await source.CountFilesAsync(CancellationToken.None);
        await Task.Delay(1200);

        // the projects, their repositories, and each repository's branches, tags and pull requests: nothing below a commit that has not moved
        var before = fake.Requests.Count;
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        var walk = fake.Requests.Skip(before).ToList();
        var listings = new Regex(@"/rest/api/1\.0/(projects\?|projects/[^/]+/repos\?|projects/[^/]+/repos/[^/]+/(branches|tags|pull-requests)\?|projects/[^/]+/repos/[^/]+/pull-requests/\d+$)");
        Assert.IsTrue(walk.All(r => listings.IsMatch(r)), "not a listing: " + string.Join("; ", walk.Where(r => !listings.IsMatch(r))));
        Assert.AreEqual(1 + 2 + 3 * 3, walk.Distinct().Count(), string.Join("; ", walk));

        // a commit on main: the next walk lists the files afresh, the other branches and the tags kept
        var docs = fake.RepositoryAt("ANYFS", "docs");
        var files = new Dictionary<string, byte[]>(docs.Commits[docs.Branches["main"]].Files, StringComparer.Ordinal) { ["NEW.md"] = BitbucketSeed.Bytes("new\n") };
        var moved = docs.Commit("main", files, BitbucketSeed.Day.AddDays(3), message: "New");
        await Task.Delay(1200);
        CollectionAssert.DoesNotContain(await NamesAsync(source, Docs + "/files"), "NEW.md", "kept as it was until the walk");
        before = fake.Requests.Count;
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        CollectionAssert.Contains(await NamesAsync(source, Docs + "/files"), "NEW.md");
        CollectionAssert.Contains(await NamesAsync(source, Docs + "/branches/main"), "NEW.md");
        var listed = fake.Requests.Skip(before).Where(r => r.Contains("/browse", StringComparison.Ordinal)).ToList();
        Assert.IsTrue(listed.Count > 0);
        Assert.IsTrue(listed.All(r => r.Contains("at=" + moved, StringComparison.Ordinal)), "the new commit's folders alone: " + string.Join("; ", listed));
    }

    [TestMethod]
    public async Task AListingPastAPageFollowsNextPageStart()
    {
        var fake = BitbucketSeed.Lab();
        fake.PageLimit = 2;
        for (var i = 0; i < 5; i++)
        {
            fake.AddProject($"P{i}", $"Project {i}");
        }

        await using var source = await StartAsync(fake, BitbucketSeed.Settings(fake));
        Assert.AreEqual(7, (await NamesAsync(source, "")).Count);
        Assert.AreEqual(4, fake.Count("/rest/api/1.0/projects?"), "four pages of two");
    }

    [TestMethod]
    public async Task EveryRequestOfTheServerIsARowThatEnds()
    {
        var fake = BitbucketSeed.Lab();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, BitbucketSeed.Settings(fake), context);
        await source.CountFilesAsync(CancellationToken.None);
        await UpstreamRequestRows.AllEndAsync(context);
        Assert.IsTrue(fake.Requests.Count > 0);
        Assert.AreEqual(fake.Requests.Count, context.UpstreamRequests.Requests.Count, "every one the server saw, and no more");
        Assert.IsTrue(context.UpstreamRequests.Requests.All(r => r.Answer is not null));
    }

    [TestMethod]
    public async Task AThrottledAnswerIsWaitedForAndTheRequestMadeAgain()
    {
        var fake = BitbucketSeed.Lab();
        await using var source = await StartAsync(fake, BitbucketSeed.Settings(fake, "/projects/ANYFS/repos/docs"));
        fake.ThrottleNext = 1;
        CollectionAssert.Contains(await NamesAsync(source, "files"), "README.md");
        Assert.IsTrue(fake.Count("/branches?") >= 2, "the refused request made again: " + string.Join("; ", fake.Requests));
    }

    [TestMethod]
    public async Task AFolderWhoseListingFailedStaysSoUntilTheNextWalk()
    {
        var fake = BitbucketSeed.Lab();
        await using var source = await StartAsync(fake, BitbucketSeed.Settings(fake, "/projects/ANYFS/repos/docs"));
        var failing = true;
        var asked = 0;
        fake.Fault = r =>
        {
            if (failing && r.Url.AbsolutePath.Contains("/browse/docs", StringComparison.Ordinal))
            {
                asked++;
                return FakeAtlassian.Response.Html("<html>busy</html>");
            }

            return null;
        };
        var first = await Assert.ThrowsExactlyAsync<VfsException>(async () => await NamesAsync(source, "files/docs"));
        Assert.AreEqual(VfsError.Unavailable, first.Error);
        var times = asked;
        Assert.IsTrue(times > 0);
        failing = false;
        var again = await Assert.ThrowsExactlyAsync<VfsException>(async () => await NamesAsync(source, "files/docs"));
        Assert.AreEqual(first.Message, again.Message, "answered at once as it failed");
        Assert.AreEqual(times, asked, "nothing asked");

        await source.WalkAsync(new SourceCounters(), full: true, CancellationToken.None);
        CollectionAssert.Contains(await NamesAsync(source, "files/docs"), "guide.md");
    }

    // --- the settings and the Test button -------------------------------------------------------------------------------

    [TestMethod]
    public void ThePluginNamesValidatesAndShowsTheSignInsFields()
    {
        var plugin = new BitbucketPlugin();
        var settings = new BitbucketSettings { Address = "not an address", AccessToken = "t", RepositoriesAtATime = 0, RequestsPerRepository = 65, RefreshSeconds = -1 };
        CollectionAssert.AreEquivalent(new[] { "Address", "RepositoriesAtATime", "RequestsPerRepository", "RefreshSeconds" }, plugin.Validate(settings).Select(p => p.Property).ToList());
        Assert.IsFalse(plugin.Validate(new BitbucketSettings { Address = "https://bitbucket.example.com/projects/ABC", AccessToken = "t" }).Any(m => m.IsError));
        Assert.AreEqual("docs (bitbucket.example.com)", plugin.SuggestName(new BitbucketSettings { Address = "https://bitbucket.example.com/projects/ABC/repos/docs/browse" }));
        Assert.AreEqual("ABC (bitbucket.example.com)", plugin.SuggestName(new BitbucketSettings { Address = "https://bitbucket.example.com/projects/ABC" }));
        Assert.AreEqual("bitbucket.example.com", plugin.SuggestName(new BitbucketSettings { Address = "https://bitbucket.example.com/" }));

        var s = new BitbucketSettings { Address = "https://bitbucket.example.com/projects/ABC/", SignInMethod = AtlassianSignInChoices.Password };
        plugin.NormaliseSettings(s);
        Assert.AreEqual("https://bitbucket.example.com/projects/ABC", s.Address);
        Assert.IsTrue(plugin.IsVisible(s, nameof(BitbucketSettings.Username)));
        Assert.IsFalse(plugin.IsVisible(s, nameof(BitbucketSettings.AccessToken)));
        Assert.IsFalse(plugin.IsVisible(new BitbucketSettings { PullRequests = false }, nameof(BitbucketSettings.OpenOnly)));
        using var icon = plugin.OpenIcon();
        Assert.IsNotNull(icon);
    }

    [TestMethod]
    public async Task TheTestSaysWhoAndWhat()
    {
        var fake = BitbucketSeed.Lab();
        var result = await BitbucketPlugin.TestAsync(BitbucketSeed.Settings(fake, "/projects/ANYFS/repos/docs"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "(Bitbucket 9.4.2) as Test Er (tester) with a personal access token");
        StringAssert.Contains(result.Message, "the repository Docs of AnyFS Lab");

        result = await BitbucketPlugin.TestAsync(BitbucketSeed.Settings(fake), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "the 2 projects you can see");

        result = await BitbucketPlugin.TestAsync(BitbucketSeed.Settings(fake, "/projects/NOPE"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "no project NOPE");
    }
}

/// <summary>The SDK's contract for a read-only source, against the fake server.</summary>
[TestClass]
public class BitbucketContractTests : SourceContractTests
{
    protected override async Task<SourceContractSetup> SetUpAsync()
    {
        var fake = BitbucketSeed.Lab();
        var source = new BitbucketSource(BitbucketSeed.Settings(fake, "/projects/ANYFS/repos/docs"), fake.StandIn());
        await source.InitialiseAsync(new TestSourceContext(), CancellationToken.None);
        return new SourceContractSetup(source, "files/docs", "files/docs/large.bin", BitbucketSeed.Large);
    }
}
