using System.Diagnostics;
using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Common;
using AnyFS.Sources.GitLab;

namespace AnyFS.Sources.Tests;

/// <summary>The lab's GitLab as the fake holds it (spec 7.8): the accounts, groups, projects and the app's repository the tests read.</summary>
internal static class GitLabSeed
{
    public const string Token = "glpat-tester-token";
    public const string OtherToken = "glpat-other-token";

    /// <summary>1 MiB and a bit of bytes, the same every time: the file in Git LFS.</summary>
    public static readonly byte[] Large = Enumerable.Range(0, (1 << 20) + 123).Select(i => (byte)(i * 7 % 251)).ToArray();

    public static FakeGitLab Lab(string origin = "https://gitlab.fake.test", string root = "") => Fill(new FakeGitLab(origin, root));

    /// <summary>The lab's accounts, groups and projects, into a fake made elsewhere (one listening on a port).</summary>
    public static FakeGitLab Fill(FakeGitLab fake)
    {
        var tester = fake.AddUser("tester", "Test Er", Token);
        fake.AddUser("other", "Someone Else", OtherToken);
        var anyfs = fake.AddGroup("anyfs", "AnyFS", null, tester);
        var tools = fake.AddGroup("tools", "Tools", anyfs);
        var docs = fake.AddProject(anyfs, "docs", "Docs");
        docs.Commit("main", new Dictionary<string, byte[]> { ["README.md"] = Bytes("# Docs\n") });
        Wiki(docs);
        Tickets(docs, tester);
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

    /// <summary>
    /// What a server holds beyond the tester's memberships, as the Explore page lists it (the shapes the lab's probes
    /// measured, GITLAB-NOTES): public and internal projects, archived ones and one marked for deletion, a group's private
    /// subgroup the tester was invited into, projects that keep their features for their members, another's own projects.
    /// </summary>
    public static FakeGitLab WithOpenProjects(FakeGitLab fake)
    {
        var tester = fake.UserNamed("tester");
        var other = fake.UserNamed("other");
        FakeGitLab.Group Open(string path, FakeGitLab.Group? parent = null, string visibility = "public")
        {
            var group = fake.AddGroup(path, path, parent);
            group.Visibility = visibility;
            return group;
        }

        FakeGitLab.Project Add(FakeGitLab.Project project, string visibility = "public")
        {
            project.Visibility = visibility;
            project.Commit("main", new Dictionary<string, byte[]> { ["README.md"] = Bytes($"# {project.Path}\n") });
            return project;
        }

        var open = Open("open");
        Add(fake.AddProject(open, "live", "Live"));
        Add(fake.AddProject(open, "old", "Old")).Archived = true;
        Add(fake.AddProject(open, "going-deletion_scheduled-5", "Going")).MarkedForDeletion = true;
        Add(fake.AddProject(open, "inner", "Inner"), "internal");
        Add(fake.AddProject(open, "closed", "Closed"), "private");
        Add(fake.AddProject(open, "kept", "Kept")).MembersOnly = true;
        Add(fake.AddProject(open, "mine", "Mine", tester)).MembersOnly = true;
        var jobs = Add(fake.AddProject(open, "jobs", "Jobs"));
        jobs.PublicJobs = false;
        jobs.PipelineList.Add(new FakeGitLab.Pipeline(9, "main", "success", FirstCommit, FirstCommit));
        var bare = fake.AddProject(open, "bare", "Bare");
        bare.Visibility = "public";

        Add(fake.AddProject(Open("team", open, "internal"), "deep", "Deep"), "internal");
        var quiet = Open("quiet", open, "private");
        Add(fake.AddProject(quiet, "invited", "Invited", tester), "private");
        Add(fake.AddProject(quiet, "uninvited", "Uninvited"), "private");
        var guest = Add(fake.AddProject(quiet, "guest", "Guest"), "private");
        guest.Guests.Add(tester.Id);
        guest.PipelineList.Add(new FakeGitLab.Pipeline(8, "main", "success", FirstCommit, FirstCommit));
        guest.JobsOf[8] = [];

        Add(fake.AddProject(Open("dusty"), "relic", "Relic")).Archived = true;
        Open("barren");
        Add(fake.AddPersonalProject(other, "theirs", "Theirs"));
        Add(fake.AddPersonalProject(other, "hidden", "Hidden"), "private");
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

        // a finished pipeline on main (build keeps artifacts, test none) and a running one on feature/x
        app.PipelineList.Add(new FakeGitLab.Pipeline(2, "main", "success", FirstCommit.AddHours(1), FirstCommit.AddHours(2)));
        app.PipelineList.Add(new FakeGitLab.Pipeline(3, "feature/x", "running", FirstCommit.AddDays(2), FirstCommit.AddDays(2)));
        var build = new FakeGitLab.Job(31, "build", "success") { Log = "$ mkdir -p out/sub\n$ seq 1 400000 > out/sub/numbers.txt\nJob succeeded\n" };
        build.Artifacts["out/hello.txt"] = Bytes("built from abc123\n");
        build.Artifacts["out/sub/numbers.txt"] = Numbers;
        app.JobsOf[2] = [build, new FakeGitLab.Job(32, "test", "success") { Log = "the tests passed\n" }];
        app.JobsOf[3] = [new FakeGitLab.Job(41, "build", "running") { Log = "$ mkdir -p out/sub\n" }];
        app.Uploads[$"{NotesSecret}/release-notes.txt"] = Bytes("What is new in v1.0.\n");
        app.ReleaseList.Add(new FakeGitLab.Release("v1.0", "Version 1.0", "The first release: the fixture tree.", FirstCommit.AddDays(1),
            [("release-notes.txt", $"https://gitlab.fake.test/-/project/{app.Id}/uploads/{NotesSecret}/release-notes.txt"), ("AnyFS on GitHub", "https://github.com/fiddyschmitt/AnyFS")]));
    }

    public const string NotesSecret = "12d86697a1756f24b87861d45eee5cf6";

    /// <summary>The build job's numbers.txt: seq 1 400000, as the lab's pipeline writes it.</summary>
    public static readonly byte[] Numbers = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(1, 400_000).Select(i => i + "\n")));

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

    public const string StepsSecret = "817b19e73071a0163e81cf960e6165fd";
    public const string DataSecret = "b24c363dbae7306cb60579b3bd7f7bd5";
    public static readonly byte[] Data = Enumerable.Range(0, 102_400).Select(i => (byte)(i * 13 % 256)).ToArray();
    public static readonly DateTimeOffset Opened = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);

    /// <summary>The docs project's issues and merge request, as the lab's seed makes them.</summary>
    private static void Tickets(FakeGitLab.Project docs, FakeGitLab.User tester)
    {
        var first = new FakeGitLab.Ticket(1, "Install fails on Debian",
            $"The installer stops half way.\n\n[steps.txt](/uploads/{StepsSecret}/steps.txt)\n\n![diagram](/uploads/{DiagramHash}/diagram.png)\n\nThe log: [data.bin](/uploads/{DataSecret}/data.bin)", tester, Opened)
        {
            Milestone = "v1",
            Updated = Opened.AddHours(2),
        };
        first.Labels.AddRange(["bug", "install"]);
        first.Assignees.Add(tester);
        first.Notes.Add(new FakeGitLab.Note("assigned to @tester", tester, Opened.AddMinutes(1), System: true));
        first.Notes.Add(new FakeGitLab.Note("It works on Debian 12.", tester, Opened.AddHours(1)));
        first.Notes.Add(new FakeGitLab.Note("Fixed in the next build.", tester, Opened.AddHours(2)));
        docs.IssueList.Add(first);
        docs.IssueList.Add(new FakeGitLab.Ticket(2, "An old question", "Answered long ago.", tester, Opened.AddHours(3)) { State = "closed", Closed = Opened.AddHours(4), Updated = Opened.AddHours(4) });
        docs.IssueList.Add(new FakeGitLab.Ticket(3, "Ünïcödé — résumé ✓ 日本語", "A title that is not ASCII.", tester, Opened.AddHours(5)));
        docs.IssueList.Add(new FakeGitLab.Ticket(4, "Paths: a/b \\ c * ? \"q\" <x> |p|", "A title with every character Windows refuses in a name.", tester, Opened.AddHours(6)));
        docs.Uploads[$"{StepsSecret}/steps.txt"] = Bytes("Steps to reproduce, as text.\n");
        docs.Uploads[$"{DiagramHash}/diagram.png"] = Diagram;
        docs.Uploads[$"{DataSecret}/data.bin"] = Data;

        var mr = new FakeGitLab.Ticket(1, "Docs: say more", "Adds a page and changes the README.", tester, Opened.AddDays(1)) { SourceBranch = "feature/docs", TargetBranch = "main" };
        mr.Notes.Add(new FakeGitLab.Note("Looks good to me.", tester, Opened.AddDays(1).AddHours(1)));
        mr.Diffs.Add(new FakeGitLab.Diff("docs/more.md", "docs/more.md", "@@ -0,0 +1,3 @@\n+# More\n+\n+A page the merge request adds.\n", NewFile: true));
        mr.Diffs.Add(new FakeGitLab.Diff("README.md", "README.md", "@@ -1 +1,3 @@\n-# Docs\n+# Docs\n+\n+Changed on a branch.\n"));
        mr.Diffs.Add(new FakeGitLab.Diff("logo.png", "logo.png", "", TooLarge: true));
        docs.MergeRequestList.Add(mr);
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
        CollectionAssert.AreEqual(new[] { "files", "branches", "tags", "issues", "merge requests", "pipelines", "releases" }, (await ListAsync(source, "")).Select(e => e.Name).ToArray());
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
    public async Task TheServerShowsWhatTheAccountCanSeeAndNoMore()
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

    /// <summary>How many listings of every project were being answered at once, each taking a moment; what it is told when one is asked.</summary>
    private sealed class ProjectListings(HttpMessageHandler inner, Action<string>? asked = null, Func<string, bool>? refuse = null) : DelegatingHandler(inner)
    {
        private int _now;
        private int _most;

        public int Most => Volatile.Read(ref _most);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/api/v4/projects", StringComparison.Ordinal))
            {
                return await base.SendAsync(request, cancellationToken);
            }

            if (refuse?.Invoke(request.RequestUri.Query) == true)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"403 Forbidden"}""") };
            }

            var now = Interlocked.Increment(ref _now);
            int most;
            while (now > (most = Volatile.Read(ref _most)) && Interlocked.CompareExchange(ref _most, now, most) != most)
            {
            }

            try
            {
                await Task.Delay(10, cancellationToken);
                var response = await base.SendAsync(request, cancellationToken);
                asked?.Invoke(request.RequestUri.Query);
                return response;
            }
            finally
            {
                Interlocked.Decrement(ref _now);
            }
        }
    }

    private static async Task<(GitLabSource Source, ProjectListings Listings)> StartCountingListingsAsync(FakeGitLab fake, GitLabSettings settings, Action<string>? asked = null, Func<string, bool>? refuse = null)
    {
        var listings = new ProjectListings(fake.Handler(), asked, refuse);
        var source = new GitLabSource(settings, new GitLabStandIn(() => listings));
        await source.InitialiseAsync(new TestSourceContext(), CancellationToken.None);
        return (source, listings);
    }

    private static List<long> ProjectsAdded(FakeGitLab fake, int count, int gapEvery = 0, int gap = 0)
    {
        var tester = fake.UserNamed("tester");
        var many = fake.AddGroup("many", "Many", null, tester);
        var ids = new List<long>();
        for (var i = 0; i < count; i++)
        {
            if (gapEvery > 0 && i % gapEvery == 0)
            {
                fake.SkipIds(gap);
            }

            ids.Add(fake.AddProject(many, $"p{i:0000}", $"Project {i}").Id);
        }

        return ids;
    }

    [TestMethod]
    public async Task ASmallServersProjectsAreOnePageAsTheyAlwaysWere()
    {
        var fake = GitLabSeed.Lab();
        var (source, listings) = await StartCountingListingsAsync(fake, GitLabSeed.Settings(fake.Server));
        await using var _s = source;
        Assert.IsTrue((await source.ServerListingAsync(CancellationToken.None)).Projects.Count > 0);
        Assert.AreEqual(1, fake.Count("GET /api/v4/projects?"), "the one page it takes: " + string.Join("; ", fake.Requests.Where(r => r.Contains("/projects?", StringComparison.Ordinal))));
        Assert.AreEqual(1, listings.Most);
    }

    [TestMethod]
    public async Task ManyProjectsAreListedInRangesOfIdsSideBySide()
    {
        var fake = GitLabSeed.Lab();
        var added = ProjectsAdded(fake, 2_000);
        var settings = GitLabSeed.Settings(fake.Server);
        var (source, listings) = await StartCountingListingsAsync(fake, settings);
        await using var _s = source;
        var listed = (await source.ServerListingAsync(CancellationToken.None)).Projects.Select(p => p.Id).ToList();

        CollectionAssert.AreEqual(listed.OrderBy(id => id).ToList(), listed, "in the order of their ids, as a single listing gives them");
        Assert.AreEqual(listed.Count, listed.Distinct().Count(), "none twice");
        CollectionAssert.IsSubsetOf(added, listed, "none left out");
        Assert.IsTrue(listings.Most is > 1 and <= 4, $"side by side, and no more at once than a walk may ask, half of Concurrency 8, the other half for whoever browses: {listings.Most}");

        var asked = fake.Requests.Where(r => r.StartsWith("GET /api/v4/projects?", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(1, asked.Count(r => r.Contains("sort=desc", StringComparison.Ordinal)), "the highest id, asked for once");
        var ranges = asked.Where(r => !r.Contains("sort=desc", StringComparison.Ordinal) && r.Contains("id_after=", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(15, ranges.Select(r => r.Split('?', '&').FirstOrDefault(p => p.StartsWith("id_before=", StringComparison.Ordinal))).Where(b => b is not null).Distinct().Count(), "sixteen ranges, the last with no end");
        Assert.IsTrue(ranges.Any(r => !r.Contains("id_before=", StringComparison.Ordinal)), "the last is open at its end");
        Assert.IsTrue(asked.Count <= 2 + (listed.Count / 100) + 16, $"the first page, the highest id, then a page of a hundred and a part page for each range at most: {asked.Count}");
    }

    [TestMethod]
    public async Task ProjectsFarApartInTheirIdsAreAllListedOnce()
    {
        var fake = GitLabSeed.Lab();
        var added = ProjectsAdded(fake, 450, gapEvery: 50, gap: 100_000);
        var (source, _) = await StartCountingListingsAsync(fake, GitLabSeed.Settings(fake.Server));
        await using var _s = source;
        var listed = (await source.ServerListingAsync(CancellationToken.None)).Projects.Select(p => p.Id).ToList();
        Assert.AreEqual(listed.Count, listed.Distinct().Count(), "none twice");
        CollectionAssert.IsSubsetOf(added, listed, "none left out");
        CollectionAssert.AreEqual(listed.OrderBy(id => id).ToList(), listed);
    }

    [TestMethod]
    public async Task ProjectsMostlyInOneCornerOfTheirIdsAreAllListedOnce()
    {
        var fake = GitLabSeed.Lab();
        var tester = fake.UserNamed("tester");
        var many = fake.AddGroup("many", "Many", null, tester);
        var added = new List<long>();
        for (var i = 0; i < 700; i++)
        {
            added.Add(fake.AddProject(many, $"p{i:0000}", $"Project {i}").Id);
        }

        fake.SkipIds(5_000_000);
        added.Add(fake.AddProject(many, "far-away", "Far away").Id);
        var (source, _) = await StartCountingListingsAsync(fake, GitLabSeed.Settings(fake.Server));
        await using var _s = source;
        var listed = (await source.ServerListingAsync(CancellationToken.None)).Projects.Select(p => p.Id).ToList();
        Assert.AreEqual(listed.Count, listed.Distinct().Count(), "none twice");
        CollectionAssert.IsSubsetOf(added, listed, "none left out");
    }

    [TestMethod]
    public async Task ExactlyOnePageOfProjectsAsksForNoRanges()
    {
        var fake = GitLabSeed.Lab();
        var (first, _) = await StartCountingListingsAsync(fake, GitLabSeed.Settings(fake.Server));
        var already = (await first.ServerListingAsync(CancellationToken.None)).Projects.Count;
        await first.DisposeAsync();

        ProjectsAdded(fake, 100 - already);
        var (source, _) = await StartCountingListingsAsync(fake, GitLabSeed.Settings(fake.Server));
        await using var _s = source;
        var before = fake.Requests.Count;
        Assert.AreEqual(100, (await source.ServerListingAsync(CancellationToken.None)).Projects.Count);
        var asked = fake.Requests.Skip(before).Where(r => r.StartsWith("GET /api/v4/projects?", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(0, asked.Count(r => r.Contains("id_before=", StringComparison.Ordinal) || r.Contains("id_after=", StringComparison.Ordinal)), string.Join("; ", asked));
    }

    [TestMethod]
    public async Task WithOnlyOneWalkerTheProjectsArePagedOneAfterAnother()
    {
        var fake = GitLabSeed.Lab();
        var added = ProjectsAdded(fake, 450);
        var settings = GitLabSeed.Settings(fake.Server);
        settings.Concurrency = 3;
        var (source, listings) = await StartCountingListingsAsync(fake, settings);
        await using var _s = source;
        var listed = (await source.ServerListingAsync(CancellationToken.None)).Projects.Select(p => p.Id).ToList();
        CollectionAssert.IsSubsetOf(added, listed);
        Assert.AreEqual(1, listings.Most);
        Assert.AreEqual(0, fake.Count("sort=desc"), "nothing to ask for the highest id for");
        Assert.AreEqual(0, fake.Count("id_before="));
    }

    [TestMethod]
    public async Task AServerThatDoesNotKeepToARangeIsListedAPageAtATimeWithNoneTwice()
    {
        var fake = GitLabSeed.Lab();
        fake.KeepsToIdBefore = false;
        var added = ProjectsAdded(fake, 2_000);
        var (source, _) = await StartCountingListingsAsync(fake, GitLabSeed.Settings(fake.Server));
        await using var _s = source;
        var listed = (await source.ServerListingAsync(CancellationToken.None)).Projects.Select(p => p.Id).ToList();
        Assert.AreEqual(listed.Count, listed.Distinct().Count(), "none twice");
        CollectionAssert.IsSubsetOf(added, listed, "none left out");
        CollectionAssert.AreEqual(listed.OrderBy(id => id).ToList(), listed);
    }

    [TestMethod]
    public async Task AProjectMadeAsTheyAreListedPastTheHighestIsListedToo()
    {
        var fake = GitLabSeed.Lab();
        ProjectsAdded(fake, 450);
        var tester = fake.UserNamed("tester");
        var late = fake.AddGroup("late", "Late", null, tester);
        var made = 0L;
        var (source, _) = await StartCountingListingsAsync(fake, GitLabSeed.Settings(fake.Server), query =>
        {
            if (query.Contains("sort=desc", StringComparison.Ordinal))
            {
                made = fake.AddProject(late, "late", "Late").Id;
            }
        });
        await using var _s = source;
        var listed = (await source.ServerListingAsync(CancellationToken.None)).Projects.Select(p => p.Id).ToList();
        Assert.AreNotEqual(0, made);
        CollectionAssert.Contains(listed, made, "the last range is open at its end");
    }

    [TestMethod]
    public async Task ARefusedRangeFailsTheListingAndTheNextAskStartsAgain()
    {
        var fake = GitLabSeed.Lab();
        var added = ProjectsAdded(fake, 450);
        var refuse = true;
        var (source, _) = await StartCountingListingsAsync(fake, GitLabSeed.Settings(fake.Server), refuse: query => refuse && query.Contains("id_before=", StringComparison.Ordinal));
        await using var _s = source;
        await Assert.ThrowsExactlyAsync<VfsException>(() => source.ServerListingAsync(CancellationToken.None));
        refuse = false;
        CollectionAssert.IsSubsetOf(added, (await source.ServerListingAsync(CancellationToken.None)).Projects.Select(p => p.Id).ToList());
    }

    /// <summary>Lists every folder from <paramref name="path"/> down as someone opening each would: a folder shown that GitLab refuses throws.</summary>
    private static async Task OpenEveryFolderAsync(ISource source, string path)
    {
        foreach (var entry in await ListAsync(source, path))
        {
            if (entry.IsDirectory)
            {
                await OpenEveryFolderAsync(source, VfsPath.Join(path, entry.Name));
            }
        }
    }

    [TestMethod]
    public async Task TheServerShowsEveryProjectTheAccountCanSeeActiveOrNot()
    {
        var fake = GitLabSeed.WithOpenProjects(GitLabSeed.Lab());
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(fake.Server));
        await using var _s = source;
        async Task<string[]> Names(string path) => (await ListAsync(source, path)).Select(e => e.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "anyfs", "barren", "dusty", "elsewhere", "open", "other", "tester" }, await Names(""), "no hidden group; a group with nothing in it shows as GitLab lists it");
        CollectionAssert.AreEqual(new[] { "quiet", "team", "bare", "going-deletion_scheduled-5", "inner", "jobs", "kept", "live", "mine", "old" }, await Names("open"));
        CollectionAssert.AreEqual(new[] { "deep" }, await Names("open/team"));
        CollectionAssert.AreEqual(new[] { "guest", "invited" }, await Names("open/quiet"), "a private subgroup shows what the account was invited to");
        CollectionAssert.AreEqual(new[] { "relic" }, await Names("dusty"));
        CollectionAssert.AreEqual(new[] { "theirs" }, await Names("other"), "another's own projects, those it can see");
        CollectionAssert.AreEqual(new[] { "shared" }, await Names("elsewhere"));
        CollectionAssert.AreEqual(new[] { "tools", "docs" }, await Names("anyfs"));

        foreach (var (path, content) in new[] { ("open/live", "live"), ("open/old", "old"), ("open/going-deletion_scheduled-5", "going-deletion_scheduled-5"), ("dusty/relic", "relic"), ("open/inner", "inner"), ("open/team/deep", "deep"), ("other/theirs", "theirs"), ("open/quiet/invited", "invited") })
        {
            Assert.AreEqual($"# {content}\n", Encoding.UTF8.GetString(await ReadAsync(source, path + "/files/README.md")), path);
        }

        Assert.IsNull(await source.StatAsync("hidden", CancellationToken.None));
        Assert.IsNull(await source.StatAsync("open/closed", CancellationToken.None));
        Assert.IsNull(await source.StatAsync("open/quiet/uninvited", CancellationToken.None));
        Assert.IsNull(await source.StatAsync("other/hidden", CancellationToken.None));

        // nothing shown is refused
        await OpenEveryFolderAsync(source, "");
    }

    [TestMethod]
    public async Task AProjectShowsOnlyTheFoldersTheAccountMayOpen()
    {
        var fake = GitLabSeed.WithOpenProjects(GitLabSeed.Lab());
        async Task<string[]> FoldersOf(string project)
        {
            var (source, _) = await StartAsync(fake, GitLabSeed.Settings($"{fake.Server}/{project}"));
            await using var _s = source;

            // nothing shown is refused
            await OpenEveryFolderAsync(source, "");
            return (await ListAsync(source, "")).Select(e => e.Name).ToArray();
        }

        var all = new[] { "files", "branches", "tags", "issues", "merge requests" };
        CollectionAssert.AreEqual(all, await FoldersOf("open/live"));
        CollectionAssert.AreEqual(all, await FoldersOf("open/bare"), "an empty repository's folders, which are empty");
        CollectionAssert.AreEqual(all, await FoldersOf("open/mine"), "a member of a project that keeps its features for its members has them");
        CollectionAssert.AreEqual(Array.Empty<string>(), await FoldersOf("open/kept"), "every feature kept for the project's members");
        CollectionAssert.AreEqual(all, await FoldersOf("open/jobs"), "no pipelines: they are kept from whoever is no member");
        CollectionAssert.AreEqual(new[] { "issues", "pipelines" }, await FoldersOf("open/quiet/guest"), "a guest of a private project is kept from its code");

        // and nothing is asked for where GitLab has said it is kept
        Assert.AreEqual(0, fake.Count($"/projects/{fake.ProjectAt("open/kept").Id}/repository"));
        Assert.AreEqual(0, fake.Count($"/projects/{fake.ProjectAt("open/quiet/guest").Id}/repository"));

        var kept = await GitLabPlugin.TestAsync(GitLabSeed.Settings(fake.Server + "/open/kept"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        StringAssert.Contains(kept.Message, "its repository is kept from this account");
    }

    [TestMethod]
    public async Task WithEveryProjectOffTheServerShowsOnlyWhatTheAccountIsAMemberOf()
    {
        var fake = GitLabSeed.WithOpenProjects(GitLabSeed.Lab());
        var settings = GitLabSeed.Settings(fake.Server);
        settings.EveryProject = false;
        var (source, _) = await StartAsync(fake, settings);
        await using var _s = source;
        async Task<string[]> Names(string path) => (await ListAsync(source, path)).Select(e => e.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "anyfs", "elsewhere", "open", "tester" }, await Names(""));
        CollectionAssert.AreEqual(new[] { "mine", "quiet" }, await Names("open"), "just the way to the projects it was invited to");
        CollectionAssert.AreEqual(new[] { "guest", "invited" }, await Names("open/quiet"));
        CollectionAssert.AreEqual(new[] { "tools", "docs" }, await Names("anyfs"), "a member group lists as GitLab lists it");
        Assert.AreEqual("# invited\n", Encoding.UTF8.GetString(await ReadAsync(source, "open/quiet/invited/files/README.md")));
        Assert.IsNull(await source.StatAsync("open/live", CancellationToken.None));
    }

    [TestMethod]
    [DataRow("https://gitlab.com")]
    [DataRow("https://staging.gitlab.com")]
    public async Task GitLabDotComShowsOnlyWhatTheAccountIsAMemberOf(string origin)
    {
        var fake = GitLabSeed.WithOpenProjects(GitLabSeed.Lab(origin));
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(fake.Server));
        await using var _s = source;
        CollectionAssert.AreEqual(new[] { "anyfs", "elsewhere", "open", "tester" }, (await ListAsync(source, "")).Select(e => e.Name).ToArray(), "its public projects are millions");
        Assert.AreEqual(0, fake.Count("projects?simple=true"), "they are never asked for");
        Assert.IsFalse(new GitLabPlugin().IsVisible(new GitLabSettings { Address = origin }, nameof(GitLabSettings.EveryProject)));
        Assert.IsTrue(new GitLabPlugin().IsVisible(new GitLabSettings { Address = "https://notgitlab.com" }, nameof(GitLabSettings.EveryProject)));
    }

    [TestMethod]
    public async Task ListingsSavedWithTheOtherSettingAreNotReadBack()
    {
        var fake = GitLabSeed.WithOpenProjects(GitLabSeed.Lab());
        using var context = new TestSourceContext();
        var members = GitLabSeed.Settings(fake.Server);
        members.EveryProject = false;
        var (first, _) = await StartAsync(fake, members, context);
        CollectionAssert.DoesNotContain((await ListAsync(first, "")).Select(e => e.Name).ToList(), "dusty");
        await first.DisposeAsync();

        var (second, _) = await StartAsync(fake, GitLabSeed.Settings(fake.Server), context);
        await using var _s = second;
        CollectionAssert.Contains((await ListAsync(second, "")).Select(e => e.Name).ToList(), "dusty", "the root of the last run would hide it until the next walk");
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

    /// <summary>Holds a project's whole repository until told, as a big one takes its time.</summary>
    private sealed class HoldingTrees(HttpMessageHandler inner, TaskCompletionSource hold) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/repository/tree", StringComparison.Ordinal))
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    [TestMethod]
    public async Task TheCountSaysWhichFolderOfWhichProjectItIsIn()
    {
        var fake = GitLabSeed.Lab();
        var hold = new TaskCompletionSource();
        var source = new GitLabSource(GitLabSeed.Settings(fake.Server), new GitLabStandIn(() => new HoldingTrees(fake.Handler(), hold)));
        await source.InitialiseAsync(new TestSourceContext(), CancellationToken.None);
        await using var _s = source;
        Assert.IsNull(source.Counters.CurrentFolder, "no count yet");

        var counting = source.CountFilesAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (source.Counters.CurrentFolder is not { } at || !at.EndsWith("/files", StringComparison.Ordinal))
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, $"the count is in '{source.Counters.CurrentFolder}'");
            await Task.Delay(5);
        }

        // a project's files, while its repository is read
        var folder = source.Counters.CurrentFolder!;
        Assert.IsNotNull(await source.StatAsync(VfsPath.Parent(folder), CancellationToken.None), $"{folder} is a folder of the source");
        hold.SetResult();
        await counting;
        Assert.IsNull(source.Counters.CurrentFolder, "the count is over");
        Assert.IsTrue(source.Counters.FilesKnown > 0);
    }

    /// <summary>Every answer a little late, so a walk outlasts a short refresh interval, as a big server's outlasts the usual one.</summary>
    private sealed class LateAnswers(HttpMessageHandler inner, int ms) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(ms, cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [TestMethod]
    public async Task AWalkAsksForAGroupOnceHoweverLongItTakes()
    {
        // each folder used to be found by asking for what is above it again once the refresh interval had passed: a group of
        // thousands of projects, its 35 pages, every minute, and a walk that hardly moved once those took longer than the minute
        var fake = GitLabSeed.Lab();
        var settings = GitLabSeed.Settings(fake.Server + "/anyfs");
        settings.RefreshSeconds = 1;
        settings.Concurrency = 2;
        var source = new GitLabSource(settings, new GitLabStandIn(() => new LateAnswers(fake.Handler(), 60)));
        await source.InitialiseAsync(new TestSourceContext(), CancellationToken.None);
        await using var _s = source;
        var clock = Stopwatch.StartNew();
        await source.CountFilesAsync(CancellationToken.None);
        Assert.IsTrue(clock.Elapsed > TimeSpan.FromSeconds(3), $"the walk outlasted the interval: {clock.Elapsed}");
        var groups = fake.Requests.Where(r => r.Contains("/groups/", StringComparison.Ordinal) && r.Contains("/projects?", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(2, groups.Count, "anyfs and anyfs/tools, once each: " + string.Join("; ", groups.Take(8)));
    }

    [TestMethod]
    public async Task AWalkDoesNotAskForEachProjectAGroupListedWhole()
    {
        var fake = GitLabSeed.Lab();
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(fake.Server + "/anyfs"));
        await using var _s = source;
        await source.CountFilesAsync(CancellationToken.None);
        var asked = fake.Requests.Where(r => System.Text.RegularExpressions.Regex.IsMatch(r, @"^GET /api/v4/projects/\d+(\?|$)")).ToList();
        Assert.AreEqual(0, asked.Count, "a group's listing tells all of them: " + string.Join("; ", asked.Take(5)));
        Assert.IsNotNull(await source.StatAsync("tools/app/files/README.md", CancellationToken.None));
    }

    [TestMethod]
    public async Task AWalkOfASourceOfOneProjectSeesItChange()
    {
        // nothing above the root lists it again: what its start was told of it would stand for good
        var fake = GitLabSeed.Lab();
        var team = fake.AddGroup("team", "Team", null, fake.UserNamed("tester"));
        var project = fake.AddProject(team, "later", "Later");
        var settings = GitLabSeed.Settings(fake.Server + "/team/later");
        settings.RefreshSeconds = 1;
        var (source, _) = await StartAsync(fake, settings);
        await using var _s = source;
        Assert.AreEqual(0, (await ListAsync(source, "files")).Count, "no commits yet");

        project.Commit("main", new Dictionary<string, byte[]> { ["a.txt"] = Encoding.UTF8.GetBytes("a") });
        await Task.Delay(1100);

        // as a walk goes: the root, then what is in it
        await source.Listings.RefreshAsync("", null, CancellationToken.None);
        Assert.AreEqual("a.txt", (await source.Listings.RefreshAsync("files", null, CancellationToken.None)).Single().Name);
    }

    [TestMethod]
    public async Task AFolderNamedSinceItsParentWasLastListedIsStillFound()
    {
        var fake = GitLabSeed.Lab();
        var team = fake.AddGroup("team", "Team", null, fake.UserNamed("tester"));
        fake.AddProject(team, "first", "First").Commit("main", new Dictionary<string, byte[]> { ["a.txt"] = Encoding.UTF8.GetBytes("a") });
        var settings = GitLabSeed.Settings(fake.Server + "/team");
        settings.RefreshSeconds = 1;
        var (source, _) = await StartAsync(fake, settings);
        await using var _s = source;
        Assert.AreEqual("first", (await ListAsync(source, "")).Single().Name);

        fake.AddProject(team, "newer", "Newer").Commit("main", new Dictionary<string, byte[]> { ["b.txt"] = Encoding.UTF8.GetBytes("bb") });
        await Task.Delay(1100);
        await using var file = await source.OpenAsync("newer/files/b.txt", VfsOpenMode.Read, CancellationToken.None);
        Assert.AreEqual(2, file.Length, "not in what the group last listed: it is asked for again");
    }

    [TestMethod]
    public async Task EveryRequestOfTheServerIsARowThatEnds()
    {
        var fake = GitLabSeed.Lab();
        using var context = new TestSourceContext();
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(fake.Server + "/anyfs"), context);
        await using var _s = source;
        await source.CountFilesAsync(CancellationToken.None);
        const string readme = "tools/app/files/README.md";
        byte[] bytes;
        await using (var file = await source.OpenAsync(readme, VfsOpenMode.Read, CancellationToken.None))
        {
            bytes = new byte[file.Length];
            Assert.AreEqual(bytes.Length, await file.ReadFullyAsync(0, bytes, CancellationToken.None));
        }

        await UpstreamRequestRows.AllEndAsync(context);
        Assert.AreEqual(fake.Requests.Count, context.UpstreamRequests.Requests.Count, "every one the server saw, and no more");
        CollectionAssert.AreEquivalent(
            fake.Requests.Select(r => r.Split(' ')[0]).ToList(),
            context.UpstreamRequests.Requests.Select(r => r.What).ToList(),
            "with what it was asked");
        Assert.AreEqual(fake.Count("repository/tree"), UpstreamRequestRows.To(context, "repository/tree").Count);
        Assert.IsTrue(context.UpstreamRequests.Requests.All(r => r.Target.StartsWith(fake.Origin, StringComparison.Ordinal)), "each with its whole address");
        Assert.IsTrue(context.UpstreamRequests.Requests.All(r => r.Answer is not null), "and what the server answered");
        Assert.IsTrue(context.UpstreamRequests.Requests.Any(r => r.What == "POST" && r.Target.EndsWith("/api/graphql", StringComparison.Ordinal) && r.Bytes > 0), "GraphQL's too");

        var raw = UpstreamRequestRows.To(context, "/raw").Single();
        Assert.AreEqual("200 OK", raw.Answer);
        Assert.IsTrue(raw.Bytes >= bytes.Length, $"the file went by on it: {raw.Bytes:N0} of {bytes.Length:N0}");
    }

    [TestMethod]
    public async Task ARefusalAndARetryAreRowsOfTheirOwn()
    {
        var fake = GitLabSeed.Lab();
        using var context = new TestSourceContext();
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(App(fake)), context);
        await using var _s = source;
        var before = context.UpstreamRequests.Requests.Count;
        fake.ThrottleNext = 1;
        await foreach (var _ in source.ListAsync("files", CancellationToken.None))
        {
        }

        await UpstreamRequestRows.AllEndAsync(context);
        var asked = context.UpstreamRequests.Requests.Skip(before).ToList();
        var throttled = asked.Single(r => r.Refused);
        StringAssert.StartsWith(throttled.Answer, "429");
        Assert.IsTrue(asked.Count(r => r.Target == throttled.Target) >= 2, "the same request again, each go a row: " + string.Join("; ", asked));
        Assert.IsFalse(asked.Last(r => r.Target == throttled.Target).Refused);
    }

    [TestMethod]
    public async Task TheCountShowsEachFolderItGoesThrough()
    {
        var fake = GitLabSeed.Lab();
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(fake.Server + "/anyfs"));
        await using var _s = source;
        var watcher = new RecordingFolderWatcher();
        source.Counters.FolderWatcher = watcher;
        await source.CountFilesAsync(CancellationToken.None);
        var lines = watcher.Lines.ToList();
        var shown = string.Join("; ", lines.Take(40));
        foreach (var folder in new[] { "(root)", "docs", "docs/issues", "docs/wiki", "tools", "tools/app", "tools/app/files" })
        {
            CollectionAssert.Contains(lines, $"in {folder}", shown);
            CollectionAssert.Contains(lines, $"done {folder}", shown);
        }

        Assert.AreEqual(lines.Count(l => l.StartsWith("in ", StringComparison.Ordinal)), lines.Count(l => l.StartsWith("done ", StringComparison.Ordinal)), "every folder gone into is done");
        var whole = lines.Single(l => l.StartsWith("listed tools/app/files ", StringComparison.Ordinal));
        Assert.IsTrue(long.Parse(whole.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture) > 258, $"the repository read at once, every folder of it: {whole}");
        Assert.AreEqual(0, lines.Count(l => l.StartsWith("in tools/app/files/", StringComparison.Ordinal)), "no row for each of its folders: they cost no request");
        Assert.AreEqual(0, lines.Count(l => l.StartsWith("failed ", StringComparison.Ordinal)), string.Join("; ", lines.Where(l => l.StartsWith("failed ", StringComparison.Ordinal))));
    }

    [TestMethod]
    public async Task TheCountWalksTheTreeAndKeepsWhatItFoundSoFoldersOpenAtOnce()
    {
        var fake = GitLabSeed.Lab();
        var (source, _) = await StartAsync(fake, GitLabSeed.Settings(fake.Server + "/anyfs"));
        await using var _s = source;
        await source.CountFilesAsync(CancellationToken.None);

        // each project's default branch whole, a hundred entries a request: the app's 264 in three, the docs' one in one;
        // the sizes a hundred at a time: the app's 258 files in three, the docs' README in one
        Assert.AreEqual(3 + 1, fake.Count("repository/tree"), "the default branches' trees, whole, and no branch's or tag's");
        Assert.AreEqual(0, fake.Count("path=many"), "no repository folder listed alone");
        Assert.AreEqual(3 + 1, fake.Count("POST /api/graphql"));
        Assert.IsTrue(fake.Count("/notes") > 0, "the issues' and merge requests' notes: their folders are walked");
        Assert.IsTrue(fake.Count("/jobs") > 0, "the pipelines' jobs too");

        // docs: README.md and the wiki's five pages; app: 258 files (and its release's)
        Assert.IsTrue(source.Counters.FilesKnown >= 1 + 5 + 258, $"{source.Counters.FilesKnown:N0} files");
        Assert.AreEqual(EnumerationState.Unknown, source.Counters.State, "the branches' and tags' repositories left out make it a lower bound");

        // every folder the walk went into answers from what it kept
        var asked = fake.Requests.Count;
        Assert.AreEqual(250, (await ListAsync(source, "tools/app/files/many")).Count);
        Assert.AreEqual("strings.c", (await ListAsync(source, "tools/app/files/src/util")).Single().Name);
        Assert.AreEqual(GitLabSeed.Large.Length, (await source.StatAsync("tools/app/files/e2e/large.bin", CancellationToken.None))!.Length, "the LFS object's size");
        CollectionAssert.AreEquivalent(new[] { "home.md", "guides", "notes.asciidoc", "Ünïcödé-résumé.md" }, (await ListAsync(source, "docs/wiki")).Select(e => e.Name).ToArray());
        Assert.AreEqual(4, (await ListAsync(source, "docs/issues")).Count);
        CollectionAssert.Contains((await ListAsync(source, "docs/issues/#1 Install fails on Debian")).Select(e => e.Name).ToList(), "comments.md");
        Assert.AreEqual(asked, fake.Requests.Count, "nothing asked");

        // a folder the walk left out (a tag's repository, at a commit that is no branch's head) is listed when opened
        var trees = fake.Count("repository/tree");
        Assert.IsTrue((await ListAsync(source, "tools/app/tags/release/2.0")).Count > 0);
        Assert.IsTrue(fake.Count("repository/tree") > trees, "the tag's repository, listed now");
    }

    [TestMethod]
    public async Task AProjectWhoseActivityHoldsIsKeptAcrossARestartAndAChangedOneListedAgain()
    {
        var fake = GitLabSeed.Lab();
        using var context = new TestSourceContext();
        var (first, _) = await StartAsync(fake, GitLabSeed.Settings(fake.Server + "/anyfs"), context);
        await first.CountFilesAsync(CancellationToken.None);
        var notes = fake.Count("/notes");
        Assert.IsTrue(notes > 0);
        var docs = fake.ProjectAt("anyfs/docs");
        StringAssert.StartsWith(first.Listings.StampHeld("docs"), $"{docs.LastActivity.UtcTicks}|", "kept by its activity and head");
        await first.DisposeAsync();

        // the next run: every project's activity and head hold, so nothing in them is asked again
        fake.Requests.Clear();
        var (second, _) = await StartAsync(fake, GitLabSeed.Settings(fake.Server + "/anyfs"), context);
        await second.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(0, fake.Count("/notes"), "no issue or merge request read again");
        foreach (var feature in new[] { "/wikis?", "/pipelines", "/releases", "/repository/branches?", "/repository/tags" })
        {
            Assert.AreEqual(0, fake.Count($"/projects/{docs.Id}{feature}"), $"nor {feature}: what was left out below a folder kept is remembered");
        }

        Assert.AreEqual(4, (await ListAsync(second, "docs/issues")).Count, "answered from what was kept");
        await second.DisposeAsync();
        fake.Requests.Clear();

        // an issue raised in docs, and its activity moved: its issues are listed again, the new one's notes read, the rest kept
        docs.IssueList.Add(new FakeGitLab.Ticket(5, "A new question", "Asked today.", fake.AddUser("asker", "Ask Er"), docs.LastActivity.AddDays(1)));
        docs.LastActivity = docs.LastActivity.AddDays(1);
        var (third, _) = await StartAsync(fake, GitLabSeed.Settings(fake.Server + "/anyfs"), context);
        await using var _t = third;
        await third.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(1, fake.Count("/notes"), "the new issue's notes alone");
        Assert.AreEqual(5, (await ListAsync(third, "docs/issues")).Count);
    }

    [TestMethod]
    public async Task AKeptListingAnswersAtOnceAndIsListedAfreshByAWalkAndNotBefore()
    {
        var fake = GitLabSeed.Lab();
        var settings = GitLabSeed.Settings(fake.Server + "/anyfs/docs");
        settings.RefreshSeconds = 1;
        var (source, _) = await StartAsync(fake, settings);
        await using var _s = source;
        async Task<string[]> FilesAsync() => (await ListAsync(source, "files")).Select(e => e.Name).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[] { "README.md" }, await FilesAsync());
        fake.ProjectAt("anyfs/docs").Commit("main", new Dictionary<string, byte[]> { ["README.md"] = GitLabSeed.Bytes("# Docs\n"), ["new.md"] = GitLabSeed.Bytes("new\n") });

        // older than any interval: answered at once from what was kept, with nothing asked
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        var asked = fake.Requests.Count;
        CollectionAssert.AreEqual(new[] { "README.md" }, await FilesAsync(), "what was kept");
        await Task.Delay(100);
        Assert.AreEqual(asked, fake.Requests.Count, "nothing asked");

        // what a walk does for each folder
        await source.Listings.RefreshAsync("files", CancellationToken.None);
        CollectionAssert.Contains(await FilesAsync(), "new.md");
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
        StringAssert.Contains(server.Message, "every group and project you can see (5 projects)");
        var refused = await GitLabPlugin.TestAsync(GitLabSeed.Settings(App(fake), "glpat-wrong"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsFalse(refused.Success);
    }

    [TestMethod]
    public async Task TheTestButtonCountsWhatTheServerShows()
    {
        var fake = GitLabSeed.WithOpenProjects(GitLabSeed.Lab());
        async Task<string> Says(bool every)
        {
            var settings = GitLabSeed.Settings(fake.Server);
            settings.EveryProject = every;
            return (await GitLabPlugin.TestAsync(settings, new Progress<string>(), fake.StandIn(), CancellationToken.None)).Message;
        }

        StringAssert.Contains(await Says(true), "every group and project you can see (18 projects)");
        StringAssert.Contains(await Says(false), "the groups and projects you are a member of (8 projects)");

        // past 10,000 GitLab no longer says how many
        fake.TotalsUpTo = 16;
        StringAssert.Contains(await Says(true), "(over 10,000 projects)");
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
        CollectionAssert.AreEqual(new[] { "files", "branches", "tags", "wiki", "issues", "merge requests" }, await NamesAsync(source, ""));
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
            CollectionAssert.AreEqual(new[] { "files", "branches", "tags", "issues", "merge requests", "pipelines", "releases" }, await NamesAsync(app, ""), "a wiki with no pages");
        }

        await using var off = await StartAsync(fake, "/anyfs/docs", s => s.Wiki = false);
        CollectionAssert.AreEqual(new[] { "files", "branches", "tags", "issues", "merge requests" }, await NamesAsync(off, ""));
    }
}

/// <summary>A project's issues and merge requests (spec 7.8): a folder each, Markdown, comments, a patch, the attached files.</summary>
[TestClass]
public class GitLabTicketTests
{
    private static async Task<GitLabSource> StartAsync(FakeGitLab fake, Action<GitLabSettings>? change = null)
    {
        var settings = GitLabSeed.Settings(fake.Server + "/anyfs/docs");
        change?.Invoke(settings);
        var source = new GitLabSource(settings, fake.StandIn());
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

    private static async Task<byte[]> ReadAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length];
        await file.ReadFullyAsync(0, buffer, CancellationToken.None);
        return buffer;
    }

    [TestMethod]
    public async Task AnIssueRetitledSinceItsFolderWasListedStillOpensByItsNumber()
    {
        var fake = GitLabSeed.Lab();
        await using var source = await StartAsync(fake, s => s.RefreshSeconds = 1);
        CollectionAssert.Contains((await ListAsync(source, "issues")).Select(e => e.Name).ToList(), "#1 Install fails on Debian");

        // a listing kept until the next walk gives the old title; what the client was shown opens
        var issue = fake.ProjectAt("anyfs/docs").IssueList[0];
        issue.Title = "A new title";
        issue.Updated = DateTimeOffset.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        CollectionAssert.Contains((await ListAsync(source, "issues")).Select(e => e.Name).ToList(), "#1 Install fails on Debian");
        StringAssert.StartsWith(System.Text.Encoding.UTF8.GetString(await ReadAsync(source, First + "/issue.md")), "# #1 A new title");
        await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync("issues/#99 Nothing/issue.md", VfsOpenMode.Read, CancellationToken.None));
    }

    private const string First = "issues/#1 Install fails on Debian";

    [TestMethod]
    public async Task IssuesAndMergeRequestsAreFoldersByNumberAndTitle()
    {
        var fake = GitLabSeed.Lab();
        await using var source = await StartAsync(fake);
        CollectionAssert.AreEqual(new[] { "files", "branches", "tags", "wiki", "issues", "merge requests" }, (await ListAsync(source, "")).Select(e => e.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "#1 Install fails on Debian", "#2 An old question", "#3 Ünïcödé — résumé ✓ 日本語", "#4 Paths_ a_b _ c _ _ _q_ _x_ _p_" },
            (await ListAsync(source, "issues")).Select(e => e.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "!1 Docs_ say more" }, (await ListAsync(source, "merge requests")).Select(e => e.Name).ToArray());
        var issue = (await ListAsync(source, "issues")).First();
        Assert.AreEqual(GitLabSeed.Opened.AddHours(2), issue.Modified);
        Assert.AreEqual(GitLabSeed.Opened, issue.Created);
    }

    [TestMethod]
    public async Task AnIssueIsItsMarkdownItsCommentsAndItsAttachments()
    {
        var fake = GitLabSeed.Lab();
        await using var source = await StartAsync(fake);
        CollectionAssert.AreEqual(new[] { "issue.md", "comments.md", "attachments" }, (await ListAsync(source, First)).Select(e => e.Name).ToArray());
        var issue = Encoding.UTF8.GetString(await ReadAsync(source, First + "/issue.md"));
        StringAssert.StartsWith(issue, "# #1 Install fails on Debian\n\n**State:** open  \n**Author:** Test Er (@tester)  \n**Assignees:** Test Er (@tester)  \n**Labels:** bug, install  \n**Milestone:** v1  \n");
        StringAssert.Contains(issue, $"**Web:** {fake.Server}/anyfs/docs/-/issues/1");
        StringAssert.Contains(issue, "[steps.txt](attachments/steps.txt)");
        StringAssert.Contains(issue, "![diagram](attachments/diagram.png)");
        var comments = Encoding.UTF8.GetString(await ReadAsync(source, First + "/comments.md"));
        StringAssert.Contains(comments, "It works on Debian 12.");
        StringAssert.Contains(comments, "Fixed in the next build.");
        Assert.IsFalse(comments.Contains("assigned to", StringComparison.Ordinal), "GitLab's own notes are left out");
        var attachments = await ListAsync(source, First + "/attachments");
        CollectionAssert.AreEqual(new[] { "steps.txt", "diagram.png", "data.bin" }, attachments.Select(e => e.Name).ToArray());
        Assert.AreEqual(GitLabSeed.Data.Length, attachments.Single(e => e.Name == "data.bin").Length);
        CollectionAssert.AreEqual(GitLabSeed.Data, await ReadAsync(source, First + "/attachments/data.bin"));
        Assert.AreEqual(0, fake.Count("GET /api/v4/projects/" + fake.ProjectAt("anyfs/docs").Id + "/uploads/" + GitLabSeed.StepsSecret), "sizes from HEAD requests");
    }

    [TestMethod]
    public async Task AClosedIssueSaysSoAndAnIssueWithoutCommentsSaysThat()
    {
        var fake = GitLabSeed.Lab();
        await using var source = await StartAsync(fake);
        StringAssert.Contains(Encoding.UTF8.GetString(await ReadAsync(source, "issues/#2 An old question/issue.md")), "**State:** closed ");
        StringAssert.Contains(Encoding.UTF8.GetString(await ReadAsync(source, "issues/#2 An old question/comments.md")), "No comments.");
        CollectionAssert.AreEqual(new[] { "issue.md", "comments.md" }, (await ListAsync(source, "issues/#2 An old question")).Select(e => e.Name).ToArray(), "no attachments, no folder");
    }

    [TestMethod]
    public async Task AMergeRequestHasItsChangesAsAPatch()
    {
        var fake = GitLabSeed.Lab();
        await using var source = await StartAsync(fake);
        const string mr = "merge requests/!1 Docs_ say more";
        CollectionAssert.AreEqual(new[] { "merge_request.md", "comments.md", "changes.diff" }, (await ListAsync(source, mr)).Select(e => e.Name).ToArray());
        StringAssert.Contains(Encoding.UTF8.GetString(await ReadAsync(source, mr + "/merge_request.md")), "**Branches:** feature/docs into main");
        var patch = Encoding.UTF8.GetString(await ReadAsync(source, mr + "/changes.diff"));
        StringAssert.StartsWith(patch, "diff --git a/docs/more.md b/docs/more.md\nnew file mode 100644\n--- /dev/null\n+++ b/docs/more.md\n@@ -0,0 +1,3 @@\n+# More\n");
        StringAssert.Contains(patch, "diff --git a/README.md b/README.md\n--- a/README.md\n+++ b/README.md\n@@ -1 +1,3 @@\n");
        StringAssert.Contains(patch, "diff --git a/logo.png b/logo.png\n# GitLab does not show this file's changes");
    }

    [TestMethod]
    public async Task OpenOnlyLeavesTheClosedOut()
    {
        var fake = GitLabSeed.Lab();
        await using var source = await StartAsync(fake, s => s.OpenOnly = true);
        CollectionAssert.AreEqual(new[] { "#1 Install fails on Debian", "#3 Ünïcödé — résumé ✓ 日本語", "#4 Paths_ a_b _ c _ _ _q_ _x_ _p_" },
            (await ListAsync(source, "issues")).Select(e => e.Name).ToArray());
    }

    [TestMethod]
    public async Task OnlyWhatChangedIsAskedForAgain()
    {
        var fake = GitLabSeed.Lab();
        await using var source = await StartAsync(fake, s => s.RefreshSeconds = 0);
        Assert.AreEqual(4, (await ListAsync(source, "issues")).Count);
        var docs = fake.ProjectAt("anyfs/docs");
        docs.IssueList.Add(new FakeGitLab.Ticket(5, "A new one", "Just made.", docs.IssueList[0].Author, DateTimeOffset.UtcNow));
        docs.IssueList[2].Title = "Renamed";
        docs.IssueList[2].Updated = DateTimeOffset.UtcNow;
        var names = (await ListAsync(source, "issues")).Select(e => e.Name).ToArray();
        CollectionAssert.AreEqual(new[] { "#1 Install fails on Debian", "#2 An old question", "#3 Renamed", "#4 Paths_ a_b _ c _ _ _q_ _x_ _p_", "#5 A new one" }, names);
        Assert.IsTrue(fake.Requests.Count(r => r.Contains("/issues?", StringComparison.Ordinal) && r.Contains("updated_after=", StringComparison.Ordinal)) >= 1, "the second listing asked only for the changed");
    }

    [TestMethod]
    public async Task TheSettingOrAFeatureTurnedOffHidesTheFolders()
    {
        var fake = GitLabSeed.Lab();
        await using (var off = await StartAsync(fake, s => s.IssuesAndMergeRequests = false))
        {
            CollectionAssert.AreEqual(new[] { "files", "branches", "tags", "wiki" }, (await ListAsync(off, "")).Select(e => e.Name).ToArray());
        }

        fake.ProjectAt("anyfs/docs").Issues = false;
        await using var noIssues = await StartAsync(fake);
        CollectionAssert.AreEqual(new[] { "files", "branches", "tags", "wiki", "merge requests" }, (await ListAsync(noIssues, "")).Select(e => e.Name).ToArray());
    }
}

/// <summary>A project's pipelines (jobs, logs, artifacts) and releases (spec 7.8).</summary>
[TestClass]
public class GitLabPipelineTests
{
    private static async Task<GitLabSource> StartAsync(FakeGitLab fake, string path = "/anyfs/tools/app", Action<GitLabSettings>? change = null)
    {
        var settings = GitLabSeed.Settings(fake.Server + path);
        change?.Invoke(settings);
        var source = new GitLabSource(settings, fake.StandIn());
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

    private static async Task<byte[]> ReadAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length];
        await file.ReadFullyAsync(0, buffer, CancellationToken.None);
        return buffer;
    }

    [TestMethod]
    public async Task PipelinesAndReleasesShowWhereTheProjectHasThem()
    {
        var fake = GitLabSeed.Lab();
        await using (var app = await StartAsync(fake))
        {
            CollectionAssert.AreEqual(new[] { "files", "branches", "tags", "issues", "merge requests", "pipelines", "releases" }, (await ListAsync(app, "")).Select(e => e.Name).ToArray());
        }

        await using var docs = await StartAsync(fake, "/anyfs/docs");
        CollectionAssert.AreEqual(new[] { "files", "branches", "tags", "wiki", "issues", "merge requests" }, (await ListAsync(docs, "")).Select(e => e.Name).ToArray());
    }

    [TestMethod]
    public async Task APipelineHasItsDocumentAndAFolderPerJob()
    {
        var fake = GitLabSeed.Lab();
        await using var source = await StartAsync(fake);
        CollectionAssert.AreEqual(new[] { "3 feature_x", "2 main" }, (await ListAsync(source, "pipelines")).Select(e => e.Name).ToArray(), "newest first");
        CollectionAssert.AreEqual(new[] { "pipeline.md", "build", "test" }, (await ListAsync(source, "pipelines/2 main")).Select(e => e.Name).ToArray());
        var doc = Encoding.UTF8.GetString(await ReadAsync(source, "pipelines/2 main/pipeline.md"));
        StringAssert.StartsWith(doc, "# Pipeline 2 on main\n\n**Status:** success  \n");
        StringAssert.Contains(doc, "| build | test | success |");
    }

    [TestMethod]
    public async Task AJobHasItsLogAndItsArtifactsListedFromTheArchivesEnd()
    {
        var fake = GitLabSeed.Lab();
        await using var source = await StartAsync(fake);
        const string build = "pipelines/2 main/build";
        CollectionAssert.AreEqual(new[] { "job.log", "artifacts" }, (await ListAsync(source, build)).Select(e => e.Name).ToArray());
        StringAssert.Contains(Encoding.UTF8.GetString(await ReadAsync(source, build + "/job.log")), "Job succeeded");
        CollectionAssert.AreEqual(new[] { "out" }, (await ListAsync(source, build + "/artifacts")).Select(e => e.Name).ToArray());
        var files = await ListAsync(source, build + "/artifacts/out");
        CollectionAssert.AreEqual(new[] { "hello.txt", "sub" }, files.Select(e => e.Name).ToArray());
        Assert.AreEqual(GitLabSeed.Numbers.Length, (await ListAsync(source, build + "/artifacts/out/sub")).Single().Length);
        CollectionAssert.AreEqual(GitLabSeed.Numbers, await ReadAsync(source, build + "/artifacts/out/sub/numbers.txt"));
        Assert.IsTrue(fake.RangeRequests >= 1, "the archive's end, by range");
        Assert.AreEqual(0, fake.Count("/jobs/31/artifacts?"), "never the archive whole");
        CollectionAssert.AreEqual(new[] { "job.log" }, (await ListAsync(source, "pipelines/2 main/test")).Select(e => e.Name).ToArray(), "no artifacts, no folder");
    }

    [TestMethod]
    public async Task AServerThatIgnoresRangesSendsTheArchiveOnce()
    {
        var fake = GitLabSeed.Lab();
        fake.HonoursRanges = false;
        await using var source = await StartAsync(fake);
        var files = await ListAsync(source, "pipelines/2 main/build/artifacts/out");
        CollectionAssert.AreEqual(new[] { "hello.txt", "sub" }, files.Select(e => e.Name).ToArray());
        Assert.AreEqual(1, fake.RangeRequests, "the whole archive came with the first answer");
    }

    [TestMethod]
    public async Task AReleaseHasItsNotesItsFilesAndItsLinks()
    {
        var fake = GitLabSeed.Lab();
        await using var source = await StartAsync(fake);
        CollectionAssert.AreEqual(new[] { "v1.0" }, (await ListAsync(source, "releases")).Select(e => e.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "release.md", "assets" }, (await ListAsync(source, "releases/v1.0")).Select(e => e.Name).ToArray());
        var notes = Encoding.UTF8.GetString(await ReadAsync(source, "releases/v1.0/release.md"));
        StringAssert.StartsWith(notes, "# Version 1.0\n\n**Tag:** v1.0  \n");
        StringAssert.Contains(notes, "The first release: the fixture tree.");
        CollectionAssert.AreEqual(new[] { "release-notes.txt", "AnyFS on GitHub.url" }, (await ListAsync(source, "releases/v1.0/assets")).Select(e => e.Name).ToArray());
        Assert.AreEqual("What is new in v1.0.\n", Encoding.UTF8.GetString(await ReadAsync(source, "releases/v1.0/assets/release-notes.txt")));
        Assert.AreEqual("[InternetShortcut]\r\nURL=https://github.com/fiddyschmitt/AnyFS\r\n", Encoding.UTF8.GetString(await ReadAsync(source, "releases/v1.0/assets/AnyFS on GitHub.url")));
    }

    [TestMethod]
    public async Task OnlyTheNewestPipelinesAreKeptAndTheSettingsHideTheFolders()
    {
        var fake = GitLabSeed.Lab();
        await using (var one = await StartAsync(fake, change: s => s.PipelinesKept = 1))
        {
            CollectionAssert.AreEqual(new[] { "3 feature_x" }, (await ListAsync(one, "pipelines")).Select(e => e.Name).ToArray());
        }

        await using var off = await StartAsync(fake, change: s => (s.Pipelines, s.Releases) = (false, false));
        CollectionAssert.AreEqual(new[] { "files", "branches", "tags", "issues", "merge requests" }, (await ListAsync(off, "")).Select(e => e.Name).ToArray());
    }
}

/// <summary>A zip archive's list read from its end (spec 7.8): the entries, their sizes and folders, Zip64 too.</summary>
[TestClass]
public class ZipDirectoryTests
{
    [TestMethod]
    public async Task TheEntriesComeFromTheCentralDirectoryAlone()
    {
        var job = new FakeGitLab.Job(1, "build", "success");
        job.Artifacts["a.txt"] = GitLabSeed.Bytes("aaa");
        job.Artifacts["dir/ünï cödé.txt"] = GitLabSeed.Bytes("unicode");
        job.Artifacts["dir/deep/big.bin"] = GitLabSeed.Numbers;
        var archive = job.Archive;
        var reads = new List<(long, int)>();
        var entries = await ZipDirectory.ReadAsync(archive.Length, (offset, length, _) =>
        {
            reads.Add((offset, length));
            return Task.FromResult(archive.AsSpan((int)offset, length).ToArray());
        }, CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { "dir", "dir/deep", "a.txt", "dir/ünï cödé.txt", "dir/deep/big.bin" }, entries.Select(e => e.Path).ToArray());
        Assert.AreEqual(GitLabSeed.Numbers.Length, entries.Single(e => e.Path == "dir/deep/big.bin").Size);
        Assert.IsTrue(entries.Single(e => e.Path == "dir").IsDirectory);
        Assert.IsTrue(reads.Sum(r => r.Item2) < archive.Length, "less than the archive was read");
    }

    [TestMethod]
    public async Task SomethingThatIsNoZipSaysSo()
    {
        var bytes = new byte[1000];
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ZipDirectory.ReadAsync(bytes.Length, (o, l, _) => Task.FromResult(bytes.AsSpan((int)o, l).ToArray()), CancellationToken.None));
    }
}

/// <summary>
/// The web session (spec 7.8): a browser's cookies, which GitLab's API takes as it takes a token (spike A), kept between
/// starts and renewed without a window. The browser is a stand-in that signs in to the fake; each test has a host of its own
/// (a sign-in just made is shared by every source of its server in the process).
/// </summary>
[TestClass]
public class GitLabWebSessionTests
{
    private static int _hosts;

    private sealed class Browser(FakeGitLab fake)
    {
        public List<bool> Calls { get; } = [];

        public bool NeedsAPerson { get; set; }

        public Task<IReadOnlyList<SessionCookie>> SignInAsync(bool interactive, CancellationToken ct)
        {
            Calls.Add(interactive);
            if (NeedsAPerson && !interactive)
            {
                throw new SignInRequiredException("the browser sign-in for the server has lapsed; sign in again", permanent: false);
            }

            return Task.FromResult(fake.SignInBrowser(fake.UserNamed("tester")));
        }
    }

    private static FakeGitLab Lab() => GitLabSeed.Lab(origin: $"https://gitlab-ws{Interlocked.Increment(ref _hosts)}.fake.test");

    private static async Task<GitLabSource> StartAsync(FakeGitLab fake, Browser browser, TestSourceContext context, string path = "/anyfs/docs")
    {
        var settings = new GitLabSettings { Address = fake.Server + path, SignInMethod = GitLabSettings.WebSession };
        var source = new GitLabSource(settings, new GitLabStandIn(fake.Handler, browser.SignInAsync));
        await source.InitialiseAsync(context, CancellationToken.None);
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

    private static async Task<byte[]> ReadAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length];
        await file.ReadFullyAsync(0, buffer, CancellationToken.None);
        return buffer;
    }

    [TestMethod]
    public async Task TheBrowsersSessionSignsInAndIsKept()
    {
        var fake = Lab();
        var browser = new Browser(fake);
        using var context = new TestSourceContext();
        await using (var source = await StartAsync(fake, browser, context))
        {
            Assert.AreEqual("tester", source.Session.User?.Username);
            StringAssert.Contains(source.Session.How, "with the browser");
            CollectionAssert.Contains(await NamesAsync(source, ""), "issues");
            CollectionAssert.AreEqual(new[] { true }, browser.Calls, "the start may show a window");
        }

        Assert.IsNotNull(context.Secrets.Get(GitLabSession.CookiesSecret), "the session is kept for the next start");
    }

    [TestMethod]
    public async Task TheNextStartUsesTheKeptSessionWithoutTheBrowser()
    {
        var fake = Lab();
        var browser = new Browser(fake);
        using var context = new TestSourceContext();
        await (await StartAsync(fake, browser, context)).DisposeAsync();
        await using var again = await StartAsync(fake, browser, context);
        Assert.AreEqual(1, browser.Calls.Count, "no second browser sign-in");
        Assert.AreEqual("with the session kept from before", again.Session.How);
    }

    [TestMethod]
    public async Task ALapsedSessionIsRenewedWithoutAWindow()
    {
        var fake = Lab();
        var browser = new Browser(fake);
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, browser, context);
        fake.EndSessions();
        CollectionAssert.Contains(await NamesAsync(source, "issues"), "#1 Install fails on Debian");
        CollectionAssert.AreEqual(new[] { true, false }, browser.Calls, "renewed headless");
        Assert.AreEqual(1, source.Session.Generation);
    }

    [TestMethod]
    public async Task ARenewalThatNeedsAPersonLeavesTheSourceDegraded()
    {
        var fake = Lab();
        var browser = new Browser(fake);
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, browser, context);
        fake.EndSessions();
        browser.NeedsAPerson = true;
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await NamesAsync(source, "issues"));
        Assert.AreEqual(VfsError.Unavailable, ex.Error);
        StringAssert.Contains(ex.Message, "Retry the source");
        Assert.AreEqual(ComponentHealth.Degraded, context.LastHealth);
    }

    [TestMethod]
    public async Task WithASessionAWikisEmbeddedFilesAreFilesTheLinksPointAt()
    {
        var fake = Lab();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, new Browser(fake), context);
        CollectionAssert.AreEqual(new[] { "guides", "home.md", "notes.asciidoc", "Ünïcödé-résumé.md", "attachments" }, await NamesAsync(source, "wiki"));
        CollectionAssert.AreEqual(new[] { GitLabSeed.DiagramHash, GitLabSeed.ShotHash }, await NamesAsync(source, "wiki/attachments"));
        CollectionAssert.AreEqual(GitLabSeed.Diagram, await ReadAsync(source, $"wiki/attachments/{GitLabSeed.DiagramHash}/diagram.png"));
        Assert.AreEqual("a screenshot", Encoding.UTF8.GetString(await ReadAsync(source, $"wiki/attachments/{GitLabSeed.ShotHash}/shot 1.png")));
        StringAssert.Contains(Encoding.UTF8.GetString(await ReadAsync(source, "wiki/home.md")), $"(attachments/{GitLabSeed.DiagramHash}/diagram.png)");
        StringAssert.Contains(Encoding.UTF8.GetString(await ReadAsync(source, "wiki/guides/install/linux.md")), $"(../../attachments/{GitLabSeed.ShotHash}/shot%201.png)");
    }

    [TestMethod]
    public async Task TheTestButtonsSignInIsHandedToTheSource()
    {
        var fake = Lab();
        var browser = new Browser(fake);
        var settings = new GitLabSettings { Address = fake.Server + "/anyfs/docs", SignInMethod = GitLabSettings.WebSession };
        var result = await GitLabPlugin.TestAsync(settings, new Progress<string>(), new GitLabStandIn(fake.Handler, browser.SignInAsync), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "with the browser");
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, browser, context);
        Assert.AreEqual(1, browser.Calls.Count, "the source took the Test's sign-in");
    }
}

/// <summary>
/// The Windows sign-ins (spec 7.8, spike B): GitLab's Kerberos sign-in, then a user name and password's LDAP form and GitLab's
/// own. In-process the fake's Negotiate page has no ticket to take; listening on 127.0.0.1 it takes Windows' own sign-in
/// (Windows only), which stands in for a Kerberos ticket at the HTTP level.
/// </summary>
[TestClass]
public class GitLabWindowsTests
{
    private static async Task<GitLabSource> StartAsync(FakeGitLab fake, GitLabSettings settings, bool inProcess = true)
    {
        var source = new GitLabSource(settings, inProcess ? fake.StandIn() : null);
        await source.InitialiseAsync(new TestSourceContext(), CancellationToken.None);
        return source;
    }

    private static GitLabSettings Named(FakeGitLab fake, string password) => new()
    {
        Address = fake.Server + "/anyfs/docs",
        SignInMethod = GitLabSettings.Windows,
        Username = @"ANYFS\tester",
        Password = password,
    };

    private static async Task<string[]> NamesAsync(ISource source, string path)
    {
        var names = new List<string>();
        await foreach (var e in source.ListAsync(path, CancellationToken.None))
        {
            names.Add(e.Name);
        }

        return names.ToArray();
    }

    [TestMethod]
    public async Task WithoutATicketANameAndPasswordGoesThroughLdap()
    {
        var fake = GitLabSeed.Lab(origin: "https://gitlab-win1.fake.test");
        fake.SetPassword("tester", "right");
        await using var source = await StartAsync(fake, Named(fake, "right"));
        CollectionAssert.AreEqual(new[] { "kerberos", "ldap tester" }, fake.SignIns, "Kerberos first, then the LDAP form with the name alone");
        StringAssert.Contains(source.Session.How, "through LDAP");
        CollectionAssert.Contains(await NamesAsync(source, ""), "issues");
    }

    [TestMethod]
    public async Task WithoutLdapGitLabsOwnPasswordFormIsTried()
    {
        var fake = GitLabSeed.Lab(origin: "https://gitlab-win2.fake.test");
        fake.SetPassword("tester", "right");
        fake.KerberosSignIn = false;
        fake.LdapSignIn = false;
        await using var source = await StartAsync(fake, Named(fake, "right"));
        CollectionAssert.AreEqual(new[] { "password tester" }, fake.SignIns);
        StringAssert.Contains(source.Session.How, "through GitLab's own sign-in");
    }

    [TestMethod]
    public async Task ARefusedPasswordFailsTheStartForGood()
    {
        var fake = GitLabSeed.Lab(origin: "https://gitlab-win3.fake.test");
        fake.SetPassword("tester", "right");
        fake.KerberosSignIn = false;
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => StartAsync(fake, Named(fake, "wrong")));
        Assert.IsTrue(ex.Permanent, "a wrong password is not tried again by itself");
        StringAssert.Contains(ex.Message, "refused the user name and password");
        CollectionAssert.AreEqual(new[] { "ldap tester", "password tester" }, fake.SignIns);
    }

    [TestMethod]
    public async Task AsYourselfNeedsAServerThatOffersKerberos()
    {
        var fake = GitLabSeed.Lab(origin: "https://gitlab-win4.fake.test");
        fake.KerberosSignIn = false;
        var settings = new GitLabSettings { Address = fake.Server + "/anyfs/docs", SignInMethod = GitLabSettings.WindowsCurrent };
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => StartAsync(fake, settings));
        StringAssert.Contains(ex.Message, OperatingSystem.IsWindows() ? "the server offers no Kerberos sign-in" : "works on Windows only");
    }

    [TestMethod]
    public void TheSettingsSayWhatAWindowsSignInNeeds()
    {
        var plugin = new GitLabPlugin();
        var named = new GitLabSettings { Address = "http://192.168.0.85", SignInMethod = GitLabSettings.Windows, Username = "a b", Password = "x" };
        Assert.IsTrue(plugin.Validate(named).Any(m => m.Property == nameof(GitLabSettings.Username)), "a user name with a space");
        Assert.IsTrue(plugin.Validate(named).Any(m => m.Property == nameof(GitLabSettings.Address) && !m.IsError), "Kerberos needs the name");
        Assert.IsTrue(plugin.IsVisible(named, nameof(GitLabSettings.Password)));
        Assert.IsFalse(plugin.IsVisible(new GitLabSettings { SignInMethod = GitLabSettings.WindowsCurrent }, nameof(GitLabSettings.Username)));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task AsYourselfSignsInThroughTheNegotiatePageAndRenews()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows' own sign-in over loopback is Windows-only");
        }

        var fake = GitLabSeed.Fill(FakeGitLab.Listen());
        try
        {
            fake.WindowsUserAs = "tester";
            var settings = new GitLabSettings { Address = fake.Server + "/anyfs/docs", SignInMethod = GitLabSettings.WindowsCurrent };
            await using var source = await StartAsync(fake, settings, inProcess: false);
            StringAssert.Contains(source.Session.How, "through Kerberos");
            Assert.AreEqual("tester", source.Session.User?.Username);
            CollectionAssert.Contains(await NamesAsync(source, ""), "issues");
            fake.EndSessions();
            CollectionAssert.Contains(await NamesAsync(source, "issues"), "#1 Install fails on Debian");
            Assert.AreEqual(1, source.Session.Generation, "signed in again by itself");
            CollectionAssert.AreEqual(new[] { "kerberos", "kerberos" }, fake.SignIns);
        }
        finally
        {
            fake.Stop();
        }
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
