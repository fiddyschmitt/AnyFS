using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.GitLab;

namespace AnyFS.Sources.Tests;

/// <summary>The lab's GitLab as the fake holds it (spec 7.8): the accounts, groups, projects and the app's repository the tests read.</summary>
internal static class GitLabSeed
{
    public const string Token = "glpat-tester-token";
    public const string OtherToken = "glpat-other-token";

    /// <summary>1 MiB and a bit of bytes, the same every time: the file in Git LFS.</summary>
    public static readonly byte[] Large = Enumerable.Range(0, (1 << 20) + 123).Select(i => (byte)(i * 7 % 251)).ToArray();

    public static FakeGitLab Lab(string origin = "https://gitlab.fake.test", string root = "")
    {
        var fake = new FakeGitLab(origin, root);
        var tester = fake.AddUser("tester", "Test Er", Token);
        fake.AddUser("other", "Someone Else", OtherToken);
        var anyfs = fake.AddGroup("anyfs", "AnyFS", null, tester);
        var tools = fake.AddGroup("tools", "Tools", anyfs);
        var docs = fake.AddProject(anyfs, "docs", "Docs");
        docs.Commit("main", new Dictionary<string, byte[]> { ["README.md"] = Bytes("# Docs\n") });
        Wiki(docs);
        anyfs.WikiPages.Add(new FakeGitLab.WikiPage("handbook", "handbook", "markdown", "# Handbook\n\nThe group's own wiki.\n"));
        App(fake.AddProject(tools, "app", "App"));
        fake.AddProject(tools, "empty", "Empty");

        // a group the tester is not a member of, with one project the tester was invited to, and one it was not
        var elsewhere = fake.AddGroup("elsewhere", "Elsewhere");
        fake.AddProject(elsewhere, "shared", "Shared", tester).Commit("main", new Dictionary<string, byte[]> { ["shared.txt"] = Bytes("shared\n") });
        fake.AddProject(elsewhere, "private", "Private").Commit("main", new Dictionary<string, byte[]> { ["x.txt"] = Bytes("x\n") });
        var hidden = fake.AddGroup("hidden", "Hidden");
        fake.AddProject(hidden, "secret", "Secret").Commit("main", new Dictionary<string, byte[]> { ["secret.txt"] = Bytes("secret\n") });
        fake.AddPersonalProject(tester, "scratch", "Scratch").Commit("main", new Dictionary<string, byte[]> { ["notes.txt"] = Bytes("notes\n") });
        return fake;
    }

    public static readonly DateTimeOffset FirstCommit = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The app's repository: names Windows refuses, twins in case, a file in LFS, a submodule, many files, branches and tags.</summary>
    private static void App(FakeGitLab.Project app)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["README.md"] = Bytes("# App\n"),
            ["readme.md"] = Bytes("lower-case twin\n"),
            ["src/main.c"] = Bytes("int main(void) { return 0; }\n"),
            ["src/util/strings.c"] = Bytes("/* strings */\n"),
            ["bad:name?.txt"] = Bytes("a name Windows refuses\n"),
            ["trailing."] = Bytes("a name ending in a dot\n"),
            ["empty.txt"] = [],
            ["e2e/large.bin"] = Large,
        };
        for (var i = 0; i < 250; i++)
        {
            files[$"many/file{i:D3}.txt"] = Bytes($"file {i}\n");
        }

        var first = app.Commit("main", files, FirstCommit, lfs: ["e2e/large.bin"], submodules: ["vendor/lib"]);
        app.Tags["v1.0"] = first;
        var branch = new Dictionary<string, byte[]>(files) { ["feature.txt"] = Bytes("on feature/x\n") };
        app.Commit("feature/x", branch, FirstCommit.AddDays(2), lfs: ["e2e/large.bin"]);
        app.Commit("Feature", new Dictionary<string, byte[]> { ["capital.txt"] = Bytes("the branch named Feature\n") }, FirstCommit.AddDays(3));
        app.Tags["release/2.0"] = app.Branches["feature/x"];
        app.Branches["main"] = first;
    }

    public const string DiagramHash = "0123456789abcdef0123456789abcdef";
    public const string ShotHash = "fedcba9876543210fedcba9876543210";
    public static readonly byte[] Diagram = Enumerable.Range(0, 463).Select(i => (byte)i).ToArray();

    /// <summary>The docs project's wiki, as the lab's seed makes it: nested pages, another markup, a title in another script, two attachments.</summary>
    private static void Wiki(FakeGitLab.Project docs)
    {
        docs.WikiPages.Add(new FakeGitLab.WikiPage("home", "home", "markdown", $"# Welcome\n\nThe AnyFS lab's wiki. Start with [installing](guides/install).\n\n![A diagram](uploads/{DiagramHash}/diagram.png)\n"));
        docs.WikiPages.Add(new FakeGitLab.WikiPage("guides/install", "install", "markdown", "Download the build for your system.\n"));
        docs.WikiPages.Add(new FakeGitLab.WikiPage("guides/install/linux", "linux", "markdown", $"`chmod +x anyfs`, then run it.\n\n![The terminal](uploads/{ShotHash}/shot%201.png)\n"));
        docs.WikiPages.Add(new FakeGitLab.WikiPage("notes", "notes", "asciidoc", "= Notes\n\nSome *bold* text, in AsciiDoc.\n"));
        docs.WikiPages.Add(new FakeGitLab.WikiPage("Ünïcödé-résumé", "Ünïcödé résumé", "markdown", "A page whose title is not ASCII: 日本語.\n"));
        docs.WikiUploads[$"{DiagramHash}/diagram.png"] = Diagram;
        docs.WikiUploads[$"{ShotHash}/shot 1.png"] = Bytes("a screenshot");
    }

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    public static GitLabSettings Settings(string address, string token = Token) => new() { Address = address, AccessToken = token };
}

/// <summary>The names, addresses and settings of the GitLab source (spec 7.8).</summary>
[TestClass]
public class GitLabNamesTests
{
    [TestMethod]
    [DataRow("plain.txt", "plain.txt")]
    [DataRow("bad:name?.txt", "bad_name_.txt")]
    [DataRow("a<b>|c\"d", "a_b__c_d")]
    [DataRow("trailing.", "trailing._")]
    [DataRow("trailing ", "trailing _")]
    [DataRow("CON", "_CON")]
    [DataRow("nul.txt", "_nul.txt")]
    [DataRow("ünïcödé ✓ 日本語.txt", "ünïcödé ✓ 日本語.txt")]
    public void NamesWindowsRefusesAreMadeSafe(string raw, string safe) => Assert.AreEqual(safe, GitLabNames.Safe(raw));

    [TestMethod]
    public void TwinsInCaseAreNumberedAndCleanNamesKeepTheirs()
    {
        CollectionAssert.AreEqual(new[] { "README.md", "readme (2).md", "Makefile" }, GitLabNames.Unique(["README.md", "readme.md", "Makefile"]).ToArray());
        CollectionAssert.AreEqual(new[] { "a_b (2)", "a_b" }, GitLabNames.Unique(["a:b", "a_b"]).ToArray(), "the name that needs no change keeps it");
    }

    [TestMethod]
    public void IssuesAndMergeRequestsAreNamedByNumberAndTitle()
    {
        Assert.AreEqual("#12 Install fails on Debian", GitLabNames.Issue(12, "Install fails on Debian"));
        Assert.AreEqual("!7 Docs_ say more", GitLabNames.MergeRequest(7, "Docs: say more"));
        Assert.AreEqual("#4 Paths_ a_b _ c _ _ _q_ _x_ _p_", GitLabNames.Issue(4, "Paths: a/b \\ c * ? \"q\" <x> |p|"));
        var cut = GitLabNames.Issue(1, new string('a', 200));
        Assert.IsTrue(cut.Length <= GitLabNames.MaxTitle + 5 && cut.EndsWith('…'), cut);
    }

    [TestMethod]
    [DataRow("https://gitlab.example.com/", "https://gitlab.example.com/")]
    [DataRow(" https://gitlab.example.com/group/project.git ", "https://gitlab.example.com/group/project")]
    [DataRow("https://gitlab.example.com/group/project/-/tree/main/src?x=1#top", "https://gitlab.example.com/group/project")]
    [DataRow("http://host:8080/gitlab/a/b/", "http://host:8080/gitlab/a/b")]
    public void AnAddressIsTidied(string given, string tidy) => Assert.AreEqual(tidy, GitLabAddress.Tidy(given)!.AbsoluteUri);

    [TestMethod]
    [DataRow("ftp://host/x")]
    [DataRow("gitlab.example.com")]
    [DataRow("")]
    public void ANonWebAddressIsNone(string given) => Assert.IsNull(GitLabAddress.Tidy(given));

    [TestMethod]
    public void TheInstallationIsLookedForFromTheHostDown()
    {
        var candidates = GitLabAddress.Candidates(new Uri("https://example.com/gitlab/group/project")).ToList();
        CollectionAssert.AreEqual(new[] { "https://example.com/", "https://example.com/gitlab", "https://example.com/gitlab/group", "https://example.com/gitlab/group/project" }, candidates.Select(c => c.Server.AbsoluteUri).ToArray());
        CollectionAssert.AreEqual(new[] { "gitlab/group/project", "group/project", "project", "" }, candidates.Select(c => c.Path).ToArray());
        Assert.AreEqual("https://example.com/gitlab/api/v4/projects", new Uri(candidates[1].Api, "projects").AbsoluteUri);
    }

    [TestMethod]
    public void TheSuggestedNameIsTheHostOrTheLastNameAndTheHost()
    {
        var plugin = new GitLabPlugin();
        Assert.AreEqual("gitlab.example.com", plugin.SuggestName(new GitLabSettings { Address = "https://gitlab.example.com" }));
        Assert.AreEqual("app (gitlab.example.com)", plugin.SuggestName(new GitLabSettings { Address = "https://gitlab.example.com/group/tools/app/-/tree/main" }));
        Assert.IsNull(plugin.SuggestName(new GitLabSettings { Address = "not an address" }));
    }

    [TestMethod]
    public void TheAddressIsStoredTidy()
    {
        var s = new GitLabSettings { Address = "https://gitlab.example.com/group/app.git/" };
        new GitLabPlugin().NormaliseSettings(s);
        Assert.AreEqual("https://gitlab.example.com/group/app", s.Address);
    }

    [TestMethod]
    public void TheSettingsAreChecked()
    {
        var plugin = new GitLabPlugin();
        Assert.IsFalse(plugin.Validate(GitLabSeed.Settings("https://gitlab.example.com")).Any(m => m.IsError));
        Assert.IsTrue(plugin.Validate(new GitLabSettings { Address = "https://gitlab.example.com" }).Any(m => m.Property == nameof(GitLabSettings.AccessToken)), "a token is needed");
        Assert.IsTrue(plugin.Validate(GitLabSeed.Settings("gitlab")).Any(m => m.Property == nameof(GitLabSettings.Address)));
        Assert.IsTrue(plugin.Validate(new GitLabSettings { Address = "https://h", AccessToken = "t", Concurrency = 0 }).Any(m => m.Property == nameof(GitLabSettings.Concurrency)));
        Assert.IsTrue(plugin.IsVisible(new GitLabSettings(), nameof(GitLabSettings.AccessToken)));
        Assert.IsFalse(plugin.IsVisible(new GitLabSettings(), nameof(GitLabSettings.Username)));
    }
}

/// <summary>A GitLab source against <see cref="FakeGitLab"/>: what it shows and reads (spec 7.8).</summary>
[TestClass]
public class GitLabSourceTests
{
    private static async Task<(GitLabSource Source, TestSourceContext Context)> StartAsync(FakeGitLab fake, GitLabSettings settings, TestSourceContext? context = null)
    {
        var source = new GitLabSource(settings, fake.StandIn());
        context ??= new TestSourceContext();
        await source.InitialiseAsync(context, CancellationToken.None);
        return (source, context);
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

    private static async Task<byte[]> ReadAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length];
        var read = await file.ReadFullyAsync(0, buffer, CancellationToken.None);
        Assert.AreEqual(buffer.Length, read);
        return buffer;
    }

    private static string App(FakeGitLab fake) => fake.Server + "/anyfs/tools/app";

    [TestMethod]
    public async Task AProjectShowsItsFilesBranchesAndTags()
    {
        var fake = GitLabSeed.Lab();
        var (source, context) = await StartAsync(fake, GitLabSeed.Settings(App(fake)));
        await using var _ = source;
        Assert.AreEqual(ScopeKind.Project, source.Scope);
        CollectionAssert.AreEqual(new[] { "files", "branches", "tags" }, (await ListAsync(source, "")).Select(e => e.Name).ToArray());
        Assert.IsTrue(context.Progress.Any(p => p.Message.Contains("anyfs/tools/app", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task FilesIsTheDefaultBranchWithEachFilesSizeAndTheCommitsTime()
    {
        var fake = GitLabSeed.Lab();
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(App(fake)));
        await using var _s = source;
        var files = await ListAsync(source, "files");
        var names = files.Select(e => e.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "src", "e2e", "many", "vendor", "README.md", "readme (2).md", "bad_name_.txt", "trailing._", "empty.txt" }, names);
        Assert.IsFalse(names.Contains("feature.txt"), "the default branch, not feature/x");
        Assert.AreEqual(6, files.Single(e => e.Name == "README.md").Length);
        Assert.IsTrue(files.Single(e => e.Name == "src").IsDirectory);
        Assert.AreEqual(GitLabSeed.FirstCommit, files.Single(e => e.Name == "README.md").Modified);
        var large = (await ListAsync(source, "files/e2e")).Single();
        Assert.AreEqual(GitLabSeed.Large.Length, large.Length, "a file in LFS shows its object's size, not its pointer's");
        CollectionAssert.AreEqual(Array.Empty<VfsEntry>(), await ListAsync(source, "files/vendor/lib"), "a submodule is an empty folder");
    }

    [TestMethod]
    public async Task FilesReadAsGitLabHoldsThemLfsIncluded()
    {
        var fake = GitLabSeed.Lab();
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(App(fake)));
        await using var _s = source;
        Assert.AreEqual("int main(void) { return 0; }\n", Encoding.UTF8.GetString(await ReadAsync(source, "files/src/main.c")));
        CollectionAssert.AreEqual(GitLabSeed.Large, await ReadAsync(source, "files/e2e/large.bin"));
        Assert.AreEqual(1, fake.Count("lfs=true"), "an LFS file is read as its object");
        Assert.AreEqual("a name Windows refuses\n", Encoding.UTF8.GetString(await ReadAsync(source, "files/bad_name_.txt")));
        Assert.AreEqual("lower-case twin\n", Encoding.UTF8.GetString(await ReadAsync(source, "files/readme (2).md")));
        Assert.AreEqual(0, (await ReadAsync(source, "files/empty.txt")).Length);
        Assert.AreEqual("/* strings */\n", Encoding.UTF8.GetString(await ReadAsync(source, "FILES/SRC/UTIL/strings.c")), "names are found in either case");
    }

    [TestMethod]
    public async Task BranchesAndTagsAreTheRepositoryAtEachAndASlashMakesAFolder()
    {
        var fake = GitLabSeed.Lab();
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(App(fake)));
        await using var _s = source;
        CollectionAssert.AreEqual(new[] { "Feature", "feature (2)", "main" }, (await ListAsync(source, "branches")).Select(e => e.Name).ToArray(),
            "the branch Feature and the folder of feature/x are told apart");
        Assert.AreEqual("on feature/x\n", Encoding.UTF8.GetString(await ReadAsync(source, "branches/feature (2)/x/feature.txt")));
        Assert.AreEqual("the branch named Feature\n", Encoding.UTF8.GetString(await ReadAsync(source, "branches/Feature/capital.txt")));
        CollectionAssert.AreEqual(new[] { "release", "v1.0" }, (await ListAsync(source, "tags")).Select(e => e.Name).ToArray());
        Assert.IsTrue((await ListAsync(source, "tags/release/2.0")).Any(e => e.Name == "feature.txt"));
        Assert.IsFalse((await ListAsync(source, "tags/v1.0")).Any(e => e.Name == "feature.txt"));
    }

    [TestMethod]
    public async Task AFolderOfManyFilesListsWholeWithTheirSizesInFewRequests()
    {
        var fake = GitLabSeed.Lab();
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(App(fake)));
        await using var _s = source;
        var many = await ListAsync(source, "files/many");
        Assert.AreEqual(250, many.Count);
        Assert.IsTrue(many.All(e => e.Length == $"file {e.Name[4..7].TrimStart('0').PadLeft(1, '0')}\n".Length));
        Assert.AreEqual(1 + 3, fake.Count("POST /api/graphql"), "sizes a hundred at a time (the root's five files, then the 250)");
        Assert.AreEqual(3, fake.Count("path=many"), "250 entries in pages of 100");
    }

    [TestMethod]
    public async Task PagesComeFromTheSourcesAddressWhateverHostGitLabsLinksName()
    {
        // GitLab writes its Link headers with its configured address; the lab's, reached by IP, named a host only its DNS knows
        var fake = GitLabSeed.Lab();
        fake.ExternalUrl = "http://gitlab.only-its-own-dns-knows.example";
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(App(fake)));
        await using var _s = source;
        Assert.AreEqual(250, (await ListAsync(source, "files/many")).Count);
        Assert.AreEqual(3, fake.Count("path=many"));
    }

    [TestMethod]
    public async Task WithoutGraphQlEachFilesSizeIsAskedFor()
    {
        var fake = GitLabSeed.Lab();
        fake.GraphQl = false;
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(App(fake)));
        await using var _s = source;
        var src = await ListAsync(source, "files/src");
        Assert.AreEqual(29, src.Single(e => e.Name == "main.c").Length);
        Assert.AreEqual(5 + 1, fake.Count("HEAD /api/v4/projects/"), "a HEAD per file: the root's five, then main.c");
    }

    [TestMethod]
    public async Task TheServerShowsWhatTheAccountIsAMemberOfAndNoMore()
    {
        var fake = GitLabSeed.Lab();
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(fake.Server));
        await using var _s = source;
        Assert.AreEqual(ScopeKind.Server, source.Scope);
        CollectionAssert.AreEqual(new[] { "anyfs", "elsewhere", "tester" }, (await ListAsync(source, "")).Select(e => e.Name).ToArray(), "no hidden group");
        CollectionAssert.AreEqual(new[] { "shared" }, (await ListAsync(source, "elsewhere")).Select(e => e.Name).ToArray(), "only the project the account was invited to");
        CollectionAssert.AreEqual(new[] { "tools", "docs" }, (await ListAsync(source, "anyfs")).Select(e => e.Name).ToArray(), "a member group lists as GitLab lists it");
        CollectionAssert.AreEqual(new[] { "app", "empty" }, (await ListAsync(source, "anyfs/tools")).Select(e => e.Name).ToArray());
        Assert.AreEqual("notes\n", Encoding.UTF8.GetString(await ReadAsync(source, "tester/scratch/files/notes.txt")));
        CollectionAssert.AreEqual(Array.Empty<string>(), (await ListAsync(source, "anyfs/tools/empty/files")).Select(e => e.Name).ToArray(), "an empty repository's files are empty");
        Assert.IsNull(await source.StatAsync("hidden", CancellationToken.None));
    }

    [TestMethod]
    public async Task AGroupShowsItsSubgroupsAndProjects()
    {
        var fake = GitLabSeed.Lab();
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(fake.Server + "/anyfs"));
        await using var _s = source;
        Assert.AreEqual(ScopeKind.Group, source.Scope);
        CollectionAssert.AreEqual(new[] { "tools", "docs" }, (await ListAsync(source, "")).Select(e => e.Name).ToArray());
        Assert.AreEqual("# Docs\n", Encoding.UTF8.GetString(await ReadAsync(source, "docs/files/README.md")));
    }

    [TestMethod]
    public async Task AGroupOrProjectTheAccountCannotSeeFailsTheStart()
    {
        var fake = GitLabSeed.Lab();
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => StartAsync(fake, GitLabSeed.Settings(fake.Server + "/hidden/secret")));
        Assert.IsTrue(ex.Permanent);
        StringAssert.Contains(ex.Message, "hidden/secret");
    }

    [TestMethod]
    public async Task ARefusedTokenFailsTheStartForGood()
    {
        var fake = GitLabSeed.Lab();
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => StartAsync(fake, GitLabSeed.Settings(App(fake), "glpat-wrong")));
        Assert.IsTrue(ex.Permanent);
        StringAssert.Contains(ex.Message, "refused the access token");
    }

    [TestMethod]
    public async Task AnInstallationAtARelativeAddressIsFound()
    {
        var fake = GitLabSeed.Lab(root: "/gitlab");
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(fake.Server + "/anyfs/tools/app/-/blob/main/README.md"));
        await using var _s = source;
        Assert.AreEqual(fake.Server, source.Session.Address.Server.AbsoluteUri.TrimEnd('/'));
        Assert.AreEqual("anyfs/tools/app", source.Session.Address.Path);
        Assert.AreEqual("# App\n", Encoding.UTF8.GetString(await ReadAsync(source, "files/README.md")));
    }

    [TestMethod]
    public async Task ARepositorysFoldersAreKeptBetweenStarts()
    {
        var fake = GitLabSeed.Lab();
        using var context = new TestSourceContext();
        var (first, _) = await StartAsync(fake, GitLabSeed.Settings(App(fake)), context);
        Assert.AreEqual(250, (await ListAsync(first, "files/many")).Count);
        await first.DisposeAsync();
        Assert.IsTrue(File.Exists(Path.Combine(context.StateDirectory, TreeStore.FileName)));

        var trees = fake.Count("repository/tree");
        var (second, _) = await StartAsync(fake, GitLabSeed.Settings(App(fake)), context);
        await using var _s = second;
        Assert.AreEqual(250, (await ListAsync(second, "files/many")).Count);
        Assert.AreEqual(trees, fake.Count("repository/tree"), "the commit's folders came from the state folder");
    }

    [TestMethod]
    public async Task AMovedDefaultBranchShowsOnceTheRefreshIntervalHasPassed()
    {
        var fake = GitLabSeed.Lab();
        var settings = GitLabSeed.Settings(fake.Server + "/anyfs/docs");
        settings.RefreshSeconds = 0;
        var (source, _) = await StartAsync(fake, settings);
        await using var _s = source;
        CollectionAssert.AreEqual(new[] { "README.md" }, (await ListAsync(source, "files")).Select(e => e.Name).ToArray());
        var docs = fake.ProjectAt("anyfs/docs");
        docs.Commit("main", new Dictionary<string, byte[]> { ["README.md"] = GitLabSeed.Bytes("# Docs\n"), ["new.md"] = GitLabSeed.Bytes("new\n") });
        CollectionAssert.AreEqual(new[] { "README.md", "new.md" }, (await ListAsync(source, "files")).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [TestMethod]
    public async Task AThrottledRequestWaitsAndIsTriedAgain()
    {
        var fake = GitLabSeed.Lab();
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(App(fake)));
        await using var _s = source;
        fake.ThrottleNext = 2;
        Assert.AreEqual("# App\n", Encoding.UTF8.GetString(await ReadAsync(source, "files/README.md")));
        Assert.AreEqual(0, fake.ThrottleNext);
    }

    [TestMethod]
    public async Task WritesAreRefused()
    {
        var fake = GitLabSeed.Lab();
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(App(fake)));
        await using var _s = source;
        Assert.IsFalse(source.Capabilities.CanWrite);
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync("files/README.md", VfsOpenMode.ReadWrite, CancellationToken.None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error);
    }

    [TestMethod]
    public async Task CountingCountsEveryProjectsDefaultBranch()
    {
        var fake = GitLabSeed.Lab();
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(fake.Server + "/anyfs"));
        await using var _s = source;
        await source.CountFilesAsync(CancellationToken.None);
        // docs: README.md; app: 8 files + 250 + the submodule entry, folders src, src/util, e2e, many, vendor
        Assert.AreEqual(1 + 8 + 250 + 1, source.Counters.FilesKnown);
        Assert.AreEqual(5, source.Counters.DirectoriesKnown);
        Assert.AreEqual(EnumerationState.Unknown, source.Counters.State, "branches, tags and the rest are not counted: a lower bound");
    }

    [TestMethod]
    public async Task TheTestButtonSaysWhoAndWhat()
    {
        var fake = GitLabSeed.Lab();
        var result = await GitLabPlugin.TestAsync(GitLabSeed.Settings(App(fake)), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "as Test Er (tester) with an access token");
        StringAssert.Contains(result.Message, "the default branch main");
        var server = await GitLabPlugin.TestAsync(GitLabSeed.Settings(fake.Server), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        StringAssert.Contains(server.Message, "(5 projects)");
        var refused = await GitLabPlugin.TestAsync(GitLabSeed.Settings(App(fake), "glpat-wrong"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsFalse(refused.Success);
    }
}

/// <summary>A project's and a group's wiki (spec 7.8), with an access token: GitLab serves the attachments to no token.</summary>
[TestClass]
public class GitLabWikiTests
{
    private static async Task<GitLabSource> StartAsync(FakeGitLab fake, string path, Action<GitLabSettings>? change = null)
    {
        var settings = GitLabSeed.Settings(fake.Server + path);
        change?.Invoke(settings);
        var source = new GitLabSource(settings, fake.StandIn());
        await source.InitialiseAsync(new TestSourceContext(), CancellationToken.None);
        return source;
    }

    private static async Task<string[]> NamesAsync(ISource source, string path)
    {
        var names = new List<string>();
        await foreach (var e in source.ListAsync(path, CancellationToken.None))
        {
            names.Add(e.Name);
        }

        return names.ToArray();
    }

    private static async Task<string> TextAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length];
        await file.ReadFullyAsync(0, buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer);
    }

    [TestMethod]
    public async Task PagesAreFilesOfTheirMarkupNestedByTheirSlugs()
    {
        var fake = GitLabSeed.Lab();
        await using var source = await StartAsync(fake, "/anyfs/docs");
        CollectionAssert.AreEqual(new[] { "files", "branches", "tags", "wiki" }, await NamesAsync(source, ""));
        CollectionAssert.AreEqual(new[] { "guides", "home.md", "notes.asciidoc", "Ünïcödé-résumé.md" }, await NamesAsync(source, "wiki"), "no attachments folder with a token");
        CollectionAssert.AreEqual(new[] { "install.md", "install" }, await NamesAsync(source, "wiki/guides"), "a page beside the folder of the pages under it");
        CollectionAssert.AreEqual(new[] { "linux.md" }, await NamesAsync(source, "wiki/guides/install"));
    }

    [TestMethod]
    public async Task APageStartsWithItsAddressAndItsTitleUnlessItHasOne()
    {
        var fake = GitLabSeed.Lab();
        await using var source = await StartAsync(fake, "/anyfs/docs");
        Assert.AreEqual($"{fake.Server}/anyfs/docs/-/wikis/guides/install\n\n# install\n\nDownload the build for your system.\n", await TextAsync(source, "wiki/guides/install.md"));
        var home = await TextAsync(source, "wiki/home.md");
        Assert.IsTrue(home.StartsWith($"{fake.Server}/anyfs/docs/-/wikis/home\n\n# Welcome\n", StringComparison.Ordinal), home);
        Assert.AreEqual($"// {fake.Server}/anyfs/docs/-/wikis/notes\n= Notes\n\nSome *bold* text, in AsciiDoc.\n", await TextAsync(source, "wiki/notes.asciidoc"));
        StringAssert.Contains(await TextAsync(source, "wiki/Ünïcödé-résumé.md"), "# Ünïcödé résumé");
    }

    [TestMethod]
    public async Task WithATokenTheEmbeddedFilesLinksPointAtGitLab()
    {
        var fake = GitLabSeed.Lab();
        await using var source = await StartAsync(fake, "/anyfs/docs");
        StringAssert.Contains(await TextAsync(source, "wiki/home.md"), $"![A diagram]({fake.Server}/anyfs/docs/-/wikis/uploads/{GitLabSeed.DiagramHash}/diagram.png)");
        StringAssert.Contains(await TextAsync(source, "wiki/guides/install/linux.md"), $"({fake.Server}/anyfs/docs/-/wikis/uploads/{GitLabSeed.ShotHash}/shot%201.png)");
    }

    [TestMethod]
    public async Task AGroupsWikiShowsWhereTheServerHasGroupWikis()
    {
        var fake = GitLabSeed.Lab();
        await using (var free = await StartAsync(fake, "/anyfs"))
        {
            CollectionAssert.AreEqual(new[] { "tools", "docs" }, await NamesAsync(free, ""), "a Free server has no group wikis");
        }

        fake.Premium = true;
        await using var premium = await StartAsync(fake, "/anyfs");
        CollectionAssert.AreEqual(new[] { "wiki", "tools", "docs" }, await NamesAsync(premium, ""));
        Assert.AreEqual($"{fake.Server}/groups/anyfs/-/wikis/handbook\n\n# Handbook\n\nThe group's own wiki.\n", await TextAsync(premium, "wiki/handbook.md"));
    }

    [TestMethod]
    public async Task AProjectWithoutPagesOrWithTheSettingOffShowsNoWiki()
    {
        var fake = GitLabSeed.Lab();
        await using (var app = await StartAsync(fake, "/anyfs/tools/app"))
        {
            CollectionAssert.AreEqual(new[] { "files", "branches", "tags" }, await NamesAsync(app, ""), "a wiki with no pages");
        }

        await using var off = await StartAsync(fake, "/anyfs/docs", s => s.Wiki = false);
        CollectionAssert.AreEqual(new[] { "files", "branches", "tags" }, await NamesAsync(off, ""));
    }
}

/// <summary>The SDK's read-only contract on a GitLab project.</summary>
[TestClass]
public class GitLabContractTests : SourceContractTests
{
    protected override async Task<SourceContractSetup> SetUpAsync()
    {
        var fake = GitLabSeed.Lab();
        var source = new GitLabSource(GitLabSeed.Settings(fake.Server + "/anyfs/tools/app"), fake.StandIn());
        await source.InitialiseAsync(new TestSourceContext(), CancellationToken.None);
        return new SourceContractSetup(source, "files/e2e", "files/e2e/large.bin", GitLabSeed.Large);
    }
}
