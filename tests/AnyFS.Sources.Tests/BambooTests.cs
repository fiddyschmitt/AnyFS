using System.Text;
using System.Text.RegularExpressions;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Atlassian;
using AnyFS.Sources.Atlassian.Bamboo;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>A Bamboo server as the fake holds it (spec 7.12): the accounts, projects, plans, builds, logs and artifacts the tests read.</summary>
internal static class BambooSeed
{
    public const string Token = "bamboo-tester-token";
    public const string Password = "tester-password";

    public static readonly DateTimeOffset Day = new(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);

    /// <summary>A megabyte and a bit, the same every time: the large artifact file.</summary>
    public static readonly byte[] Large = Enumerable.Range(0, (1 << 20) + 77).Select(i => (byte)(i * 13 % 251)).ToArray();

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// Two projects the tester sees (ANYFS with the plans Build the app and Docs, OPS with Deploy) and one hidden from them.
    /// Build the app: two jobs; build #1 successful with changes, a Jira issue, a folder artifact (one large file in it) and a
    /// one-file artifact; #2 failed; #3 still building; a branch feature-x with a build of its own. Docs is disabled and
    /// has never built.
    /// </summary>
    public static FakeBamboo Lab(string root = "/bamboo")
    {
        var fake = new FakeBamboo(root: root);
        fake.AddAccount("tester", "Test Er", Token, Password);
        fake.AddAccount("other", "Someone Else", "bamboo-other-token");

        var anyfs = fake.AddProject("ANYFS", "AnyFS Lab");
        anyfs.Description = "The lab's own project.";
        var build = fake.AddPlan(anyfs, "BUILD", "Build the app");
        build.Description = "Builds the app.";
        fake.AddJob(build, "JOB1", "Default Job", "Build");
        fake.AddJob(build, "TEST", "Test", "Test");

        var first = fake.AddBuild(build, "Successful", Day, Day.AddSeconds(42), b =>
        {
            b.Passed = 10;
            b.Skipped = 1;
            b.Changes.Add(new FakeBamboo.Change("Test Er", "a1b2c3d4e5f6a7b8c9d0", Day.AddHours(-1), "Add the guide\n\nWith *detail*.", ["docs/guide.md", "README.md"]));
            b.Issues.Add(("ANYFS-12", "https://jira.fake.test/jira/browse/ANYFS-12"));
        });
        var reports = fake.AddArtifact(first.JobRuns[0], "Reports");
        reports.Files["index.html"] = Bytes("<html><body><h1>Report</h1><a href=\"css/style.css\">style</a></body></html>\n");
        reports.Files["css/style.css"] = Bytes("body { color: black }\n");
        reports.Files["data/large.bin"] = Large;
        var binary = fake.AddArtifact(first.JobRuns[0], "Binary", singleFile: true);
        binary.Files["app.exe"] = [77, 90, 1, 2, 3];

        fake.AddBuild(build, "Failed", Day.AddDays(1), Day.AddDays(1).AddMinutes(3).AddSeconds(5), b =>
        {
            b.Passed = 8;
            b.Failed = 2;
            b.Reason = "Changes by <a href=\"" + fake.Server + "/browse/user/other\">Someone Else</a>";
        });
        fake.AddBuild(build, "Unknown", Day.AddDays(2), null, b => b.LifeCycle = "InProgress", "JOB1");

        var feature = fake.AddBranch(build, "feature-x", 12);
        fake.AddBuild(feature, "Successful", Day.AddDays(1), Day.AddDays(1).AddSeconds(30), jobs: "JOB1");

        var docs = fake.AddPlan(anyfs, "DOCS", "Docs");
        docs.Enabled = false;

        var ops = fake.AddProject("OPS", "Operations");
        var deploy = fake.AddPlan(ops, "DEPLOY", "Deploy");
        fake.AddJob(deploy, "JOB1", "Default Job");
        fake.AddBuild(deploy, "Successful", Day, Day.AddSeconds(10));

        var hidden = fake.AddProject("HIDDEN", "Hidden from the tester", "other");
        var secret = fake.AddPlan(hidden, "SECRET", "Secret");
        fake.AddJob(secret, "JOB1", "Default Job");
        fake.AddBuild(secret, "Successful", Day, Day.AddSeconds(10));
        return fake;
    }

    public static BambooSettings Settings(FakeBamboo fake, string path = "", string token = Token) => new()
    {
        Address = fake.Server + path,
        AccessToken = token,
        RefreshSeconds = 60,
        PlansAtATime = 2,
        RequestsPerPlan = 2,
    };
}

/// <summary>The Bamboo source against <see cref="FakeBamboo"/> (spec 7.12): the address, the sign-ins, the tree, the documents, the logs and artifacts, and the walk.</summary>
[TestClass]
public class BambooTests
{
    private const string Plan = "AnyFS Lab (ANYFS)/Build the app (ANYFS-BUILD)";

    private static async Task<BambooSource> StartAsync(FakeBamboo fake, BambooSettings settings, TestSourceContext? context = null, AtlassianStandIn? standIn = null)
    {
        var source = new BambooSource(settings, standIn ?? fake.StandIn());
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
    [DataRow("allPlans.action", "Server", "")]
    [DataRow("browse", "Server", "")]
    [DataRow("browse/ANYFS", "Project", "ANYFS")]
    [DataRow("browse/anyfs-build", "Plan", "ANYFS-BUILD")]
    [DataRow("browse/ANYFS-BUILD/latest", "Plan", "ANYFS-BUILD")]
    [DataRow("browse/ANYFS-BUILD-12", "Plan", "ANYFS-BUILD")]
    [DataRow("browse/ANYFS-BUILD-JOB1", "Plan", "ANYFS-BUILD")]
    [DataRow("browse/ANYFS-BUILD-JOB1-12/log", "Plan", "ANYFS-BUILD")]
    [DataRow("browse/ANYFS-BUILD12", "Plan", "ANYFS-BUILD12")]
    [DataRow("chain/viewChain.action?planKey=ANYFS-BUILD", "Plan", "ANYFS-BUILD")]
    [DataRow("build/viewBuildResults.action?buildKey=ANYFS-BUILD&buildNumber=5", "Plan", "ANYFS-BUILD")]
    [DataRow("browse/project/ANYFS", "Project", "PROJECT")]
    public void TheAddressNamesTheScope(string rest, string kind, string value)
    {
        var address = AtlassianAddress.Candidates(new Uri("https://bamboo.example.com/" + rest)).First();
        var scope = BambooScope.From(address, out var problem);
        Assert.IsNotNull(scope, problem);
        Assert.AreEqual(kind, scope.Kind.ToString());
        Assert.AreEqual(value, scope.Value);
    }

    [TestMethod]
    public void AnAddressThatNamesNothingSaysWhat()
    {
        var address = AtlassianAddress.Candidates(new Uri("https://bamboo.example.com/admin/users")).First();
        Assert.IsNull(BambooScope.From(address, out var problem));
        StringAssert.Contains(problem, "a plan's");
        Assert.IsNull(BambooScope.From(AtlassianAddress.Candidates(new Uri("https://bamboo.example.com/browse/user/tester")).First(), out _), "a person's page");
    }

    [TestMethod]
    public async Task AnInstallationAtAContextPathIsFoundBelowTheHost()
    {
        var fake = BambooSeed.Lab();
        await using var source = await StartAsync(fake, BambooSeed.Settings(fake, "/browse/ANYFS-BUILD-2"));
        Assert.AreEqual("https://bamboo.fake.test/bamboo/", source.Session.Address.Base.AbsoluteUri);
        Assert.AreEqual("9.6.5", source.Session.Version);
        Assert.AreEqual(BambooScopeKind.Plan, source.Scope.Kind);
        Assert.AreEqual("ANYFS-BUILD", source.Scope.Value);
        Assert.AreEqual("tester", source.Session.User?.Name);
        Assert.AreEqual("Test Er", source.Session.User?.DisplayName);
    }

    [TestMethod]
    public async Task AConfluenceServerIsNamedForWhatItIs()
    {
        var confluence = new FakeConfluence("https://bamboo.fake.test", "/bamboo");
        confluence.AddAccount("tester", "Test Er", BambooSeed.Token);
        var source = new BambooSource(new BambooSettings { Address = confluence.Server, AccessToken = BambooSeed.Token }, confluence.StandIn());
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        StringAssert.Contains(ex.Message, "is Confluence, not Bamboo");
        await source.DisposeAsync();
    }

    // --- signing in ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ARefusedTokenFailsForGood()
    {
        var fake = BambooSeed.Lab();
        var source = new BambooSource(BambooSeed.Settings(fake, token: "wrong"), fake.StandIn());
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        Assert.IsTrue(ex.Permanent);
        StringAssert.Contains(ex.Message, "refused the personal access token");
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task APasswordSignsInThroughBamboosOwnForm()
    {
        var fake = BambooSeed.Lab();
        fake.TwoStepLogin = false;
        var settings = new BambooSettings { Address = fake.Server, SignInMethod = AtlassianSignInChoices.Password, Username = "tester", Password = BambooSeed.Password };
        await using var source = await StartAsync(fake, settings);
        Assert.AreEqual("tester", source.Session.User?.Name);
        Assert.AreEqual(1, fake.Count("POST /bamboo/userlogin!doDefault.action"), string.Join("; ", fake.Requests));
        CollectionAssert.Contains(await NamesAsync(source, ""), "AnyFS Lab (ANYFS)");
    }

    [TestMethod]
    public async Task AWebSessionsCookiesSignIn()
    {
        var fake = BambooSeed.Lab();
        var settings = new BambooSettings { Address = fake.Server + "/browse/OPS", SignInMethod = AtlassianSignInChoices.WebSession };
        var standIn = fake.StandIn((_, _) => Task.FromResult<IReadOnlyList<SessionCookie>>([fake.SessionFor("tester")]));
        await using var source = await StartAsync(fake, settings, standIn: standIn);
        Assert.AreEqual("tester", source.Session.User?.Name);
        CollectionAssert.AreEqual(new[] { "Deploy (OPS-DEPLOY)" }, await NamesAsync(source, ""));
    }

    // --- the tree ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheServerShowsTheProjectsTheAccountCanSeeAndAPlanItsBuilds()
    {
        var fake = BambooSeed.Lab();
        await using var source = await StartAsync(fake, BambooSeed.Settings(fake));
        CollectionAssert.AreEqual(new[] { "AnyFS Lab (ANYFS)", "Operations (OPS)" }, await NamesAsync(source, ""), "the hidden project left out");
        CollectionAssert.AreEqual(new[] { "Build the app (ANYFS-BUILD)", "Docs (ANYFS-DOCS)" }, await NamesAsync(source, "AnyFS Lab (ANYFS)"));
        CollectionAssert.AreEqual(new[] { "plan.md", "#3 In progress", "#2 Failed", "#1 Successful", "branches" }, await NamesAsync(source, Plan), "newest build first");
        CollectionAssert.AreEqual(new[] { "plan.md", "branches" }, await NamesAsync(source, "AnyFS Lab (ANYFS)/Docs (ANYFS-DOCS)"), "a plan that never built");
        CollectionAssert.AreEqual(new[] { "feature-x (ANYFS-BUILD12)" }, await NamesAsync(source, Plan + "/branches"));
        CollectionAssert.AreEqual(new[] { "plan.md", "#1 Successful" }, await NamesAsync(source, Plan + "/branches/feature-x (ANYFS-BUILD12)"), "a branch has no branches of its own");
        var build = (await EntriesAsync(source, Plan)).Single(e => e.Name == "#1 Successful");
        Assert.IsTrue(build.IsDirectory);
        Assert.AreEqual(BambooSeed.Day.AddSeconds(42), build.Modified, "when it completed");
        Assert.AreEqual(BambooSeed.Day, build.Created, "when it started");
    }

    [TestMethod]
    public async Task APlanIsItsMarkdown()
    {
        var fake = BambooSeed.Lab();
        await using var source = await StartAsync(fake, BambooSeed.Settings(fake));
        Assert.AreEqual("# Build the app (ANYFS-BUILD)\n\n**Project:** AnyFS Lab (ANYFS)  \n**Kind:** Plan  \n**Enabled:** yes  \n**Web:** https://bamboo.fake.test/bamboo/browse/ANYFS-BUILD  \n\n---\n\nBuilds the app.\n",
            await TextAsync(source, Plan + "/plan.md"));
        StringAssert.Contains(await TextAsync(source, "AnyFS Lab (ANYFS)/Docs (ANYFS-DOCS)/plan.md"), "**Enabled:** no  \n");
        StringAssert.StartsWith(await TextAsync(source, Plan + "/branches/feature-x (ANYFS-BUILD12)/plan.md"), "# feature-x (ANYFS-BUILD12)\n\n**Project:** AnyFS Lab (ANYFS)  \n**Kind:** Plan branch  \n");
    }

    [TestMethod]
    public async Task ABuildIsItsMarkdownAndAFolderPerJob()
    {
        var fake = BambooSeed.Lab();
        await using var source = await StartAsync(fake, BambooSeed.Settings(fake, "/browse/ANYFS-BUILD"));
        CollectionAssert.AreEqual(new[] { "build.md", "Default Job", "Test" }, await NamesAsync(source, "#1 Successful"));

        var md = await TextAsync(source, "#1 Successful/build.md");
        StringAssert.StartsWith(md, "# #1 Build the app\n\n**Key:** ANYFS-BUILD-1  \n**State:** Successful  \n**Reason:** Manual run by [Test Er](https://bamboo.fake.test/bamboo/browse/user/tester)  \n**Started:** ");
        StringAssert.Contains(md, "**Took:** 42 s  \n**Tests:** 10 passed, 1 skipped  \n**Web:** https://bamboo.fake.test/bamboo/browse/ANYFS-BUILD-1  \n\n---\n\n## Jobs\n\n| Job | State | Started | Took | Artifacts |\n|---|---|---|---|---|\n| Default Job | Successful | ");
        StringAssert.Contains(md, " | 42 s | Reports, Binary |\n| Test | Successful | ");
        StringAssert.Contains(md, "\n## Changes\n\n### Test Er, ");
        StringAssert.Contains(md, " (a1b2c3d4e5f)\n\nAdd the guide\n\nWith *detail*.\n\n- docs/guide.md\n- README.md\n\n## Jira issues\n\n- [ANYFS-12](https://jira.fake.test/jira/browse/ANYFS-12)\n");

        var failed = await TextAsync(source, "#2 Failed/build.md");
        StringAssert.Contains(failed, "**State:** Failed  \n**Reason:** Changes by [Someone Else](https://bamboo.fake.test/bamboo/browse/user/other)  \n");
        StringAssert.Contains(failed, "**Took:** 3 min 5 s  \n**Tests:** 8 passed, 2 failed  \n");

        var building = await TextAsync(source, "#3 In progress/build.md");
        StringAssert.Contains(building, "**State:** In progress  \n");
        Assert.IsFalse(building.Contains("**Took:**", StringComparison.Ordinal), "not done yet");
        CollectionAssert.AreEqual(new[] { "build.md", "Default Job" }, await NamesAsync(source, "#3 In progress"), "the job that has run");
    }

    [TestMethod]
    public async Task AJobHoldsItsLogAndTheArtifactsItKept()
    {
        var fake = BambooSeed.Lab();
        await using var source = await StartAsync(fake, BambooSeed.Settings(fake, "/browse/ANYFS-BUILD"));
        CollectionAssert.AreEqual(new[] { "ANYFS-BUILD-JOB1-1.log", "artifacts" }, await NamesAsync(source, "#1 Successful/Default Job"));
        CollectionAssert.AreEqual(new[] { "ANYFS-BUILD-TEST-1.log" }, await NamesAsync(source, "#1 Successful/Test"), "no artifacts folder where the job kept none");
        var log = await TextAsync(source, "#1 Successful/Default Job/ANYFS-BUILD-JOB1-1.log");
        StringAssert.Contains(log, "Build AnyFS Lab - Build the app #1 (ANYFS-BUILD-JOB1-1) started building");
        StringAssert.Contains(log, "Finished building ANYFS-BUILD-JOB1-1. Successful.");
        Assert.AreEqual(Encoding.UTF8.GetByteCount(log), (await source.StatAsync("#1 Successful/Default Job/ANYFS-BUILD-JOB1-1.log", CancellationToken.None))!.Length, "sized by HEAD");

        CollectionAssert.AreEqual(new[] { "Reports", "app.exe" }, await NamesAsync(source, "#1 Successful/Default Job/artifacts"), "a folder of files, and a one-file artifact as its file");
        CollectionAssert.AreEqual(new[] { "css", "data", "index.html" }, await NamesAsync(source, "#1 Successful/Default Job/artifacts/Reports"), "folders first, from the page's links");
        CollectionAssert.AreEqual(new[] { "style.css" }, await NamesAsync(source, "#1 Successful/Default Job/artifacts/Reports/css"));
        Assert.AreEqual("body { color: black }\n", await TextAsync(source, "#1 Successful/Default Job/artifacts/Reports/css/style.css"));
        CollectionAssert.AreEqual(new byte[] { 77, 90, 1, 2, 3 }, await ReadAsync(source, "#1 Successful/Default Job/artifacts/app.exe"));
        Assert.AreEqual(BambooSeed.Large.Length, (await source.StatAsync("#1 Successful/Default Job/artifacts/Reports/data/large.bin", CancellationToken.None))!.Length);
        CollectionAssert.AreEqual(BambooSeed.Large, await ReadAsync(source, "#1 Successful/Default Job/artifacts/Reports/data/large.bin"));
        CollectionAssert.AreEqual(BambooSeed.Large[700_000..], await ReadAsync(source, "#1 Successful/Default Job/artifacts/Reports/data/large.bin", 700_000));
    }

    [TestMethod]
    public void APagesLinksBelowItAreItsFilesAndFolders()
    {
        var page = new Uri("https://bamboo.example.com/bamboo/browse/A-B-JOB1-1/artifact/JOB1/Reports/");
        var html = "<html><body><a href=\"../\">Parent</a> <a href=\"css/\">css/</a> <a href=\"index.html\">index.html</a> <a href=\"data/large.bin\">deep</a> <a href=\"https://elsewhere.test/x\">away</a>"
            + " <a href=\"#top\">top</a> <a href=\"?C=M\">sort</a> <a href=\"/bamboo/browse/A-B-JOB1-1/artifact/JOB1/Reports/notes%20v2.txt\">absolute</a> <a href=\"INDEX.HTML\">again</a></body></html>";
        var entries = BambooSource.Linked(html, page).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(new[] { "css", "data", "index.html", "notes v2.txt" }, entries.Select(e => e.Name).ToArray());
        Assert.IsTrue(entries[0].IsDirectory);
        Assert.IsTrue(entries[1].IsDirectory, "a link going deeper names a folder");
        Assert.IsFalse(entries[2].IsDirectory);
        Assert.AreEqual("https://bamboo.example.com/bamboo/browse/A-B-JOB1-1/artifact/JOB1/Reports/data/", entries[1].Url.AbsoluteUri);
        Assert.AreEqual("https://bamboo.example.com/bamboo/browse/A-B-JOB1-1/artifact/JOB1/Reports/notes%20v2.txt", entries[3].Url.AbsoluteUri);
    }

    [TestMethod]
    public async Task TheSettingsTurnOffLogsArtifactsAndBranches()
    {
        var fake = BambooSeed.Lab();
        var settings = BambooSeed.Settings(fake, "/browse/ANYFS-BUILD");
        settings.Logs = false;
        settings.PlanBranches = false;
        await using var source = await StartAsync(fake, settings);
        CollectionAssert.AreEqual(new[] { "plan.md", "#3 In progress", "#2 Failed", "#1 Successful" }, await NamesAsync(source, ""));
        CollectionAssert.AreEqual(new[] { "artifacts" }, await NamesAsync(source, "#1 Successful/Default Job"));
        Assert.IsNull(await source.StatAsync("branches", CancellationToken.None));

        var neither = BambooSeed.Settings(fake, "/browse/ANYFS-BUILD");
        neither.Logs = false;
        neither.Artifacts = false;
        await using var bare = await StartAsync(fake, neither);
        CollectionAssert.AreEqual(new[] { "build.md" }, await NamesAsync(bare, "#1 Successful"));
    }

    [TestMethod]
    public async Task BuildsKeptLimitsABusyPlanToItsNewest()
    {
        var fake = BambooSeed.Lab();
        var settings = BambooSeed.Settings(fake, "/browse/ANYFS-BUILD");
        settings.BuildsKept = 2;
        await using var source = await StartAsync(fake, settings);
        CollectionAssert.AreEqual(new[] { "plan.md", "#3 In progress", "#2 Failed", "branches" }, await NamesAsync(source, ""));
        Assert.IsTrue(fake.Requests.Any(r => r.Contains("/result/ANYFS-BUILD?", StringComparison.Ordinal) && r.Contains("max-results=2", StringComparison.Ordinal)), "asked for two");
        StringAssert.StartsWith(await TextAsync(source, "#1 Successful/build.md"), "# #1 Build the app\n", "an older build opens by its number all the same");
    }

    [TestMethod]
    public async Task ABuildIsFoundByItsNumberWhateverItsNameBecame()
    {
        var fake = BambooSeed.Lab();
        var settings = BambooSeed.Settings(fake, "/browse/ANYFS-BUILD");
        settings.RefreshSeconds = 1;
        await using var source = await StartAsync(fake, settings);
        CollectionAssert.Contains(await NamesAsync(source, ""), "#3 In progress");

        // the build finishes; the listing is kept as it was, and the name it gives opens the build as it is now
        var third = fake.BuildAt("ANYFS-BUILD", 3);
        third.State = "Successful";
        third.LifeCycle = "Finished";
        third.Completed = BambooSeed.Day.AddDays(2).AddSeconds(50);
        await Task.Delay(1200);
        CollectionAssert.Contains(await NamesAsync(source, ""), "#3 In progress");
        StringAssert.Contains(await TextAsync(source, "#3 In progress/build.md"), "**State:** Successful  \n");
        Assert.IsNull(await source.StatAsync("#7 Nothing", CancellationToken.None));

        // the walk renames it
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        var names = await NamesAsync(source, "");
        CollectionAssert.Contains(names, "#3 Successful");
        CollectionAssert.DoesNotContain(names, "#3 In progress");
    }

    // --- the walk ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheCountWalksEveryPlanWholeAndSaysHowFarItHasGot()
    {
        var fake = BambooSeed.Lab();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, BambooSeed.Settings(fake), context);
        await source.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State);

        // Build the app: plan.md; #1 build.md, two logs, three files of Reports, app.exe; #2 build.md and two logs; #3 build.md and
        // a log; the branch's plan.md, build.md and log. Docs: plan.md. Deploy: plan.md, build.md and a log.
        Assert.AreEqual(1 + 7 + 3 + 2 + 3 + 1 + 3, source.Counters.FilesKnown);
        var said = CountProgress(context);
        Assert.AreEqual("Counting files: listing the plans", said[0]);
        Assert.AreEqual("Counting files: 1 of 3 plans", said[1]);
        Assert.AreEqual("Counting files: 3 of 3 plans", said[^1]);

        // every folder the walk went into answers from what it kept: a file's bytes are the one thing asked for
        var asked = fake.Requests.Count;
        StringAssert.Contains(await TextAsync(source, Plan + "/#2 Failed/Test/ANYFS-BUILD-TEST-2.log"), "Finished building ANYFS-BUILD-TEST-2. Failed.");
        CollectionAssert.AreEqual(new[] { "css", "data", "index.html" }, await NamesAsync(source, Plan + "/#1 Successful/Default Job/artifacts/Reports"));
        Assert.AreEqual(asked + 1, fake.Requests.Count, ListEverything.Since(fake.Requests, asked));
    }

    [TestMethod]
    public async Task AfterTheCountAPassOverEverythingAsksTheServerNothing()
    {
        var fake = BambooSeed.Lab();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, BambooSeed.Settings(fake), context);
        await source.CountFilesAsync(CancellationToken.None);
        await UpstreamRequestRows.AllEndAsync(context);
        var before = fake.Requests.Count;
        var folders = await ListEverything.OfAsync(source);
        Assert.IsTrue(folders > 15, folders.ToString());
        Assert.AreEqual(before, fake.Requests.Count, "asked for after the walk: " + ListEverything.Since(fake.Requests, before));
    }

    [TestMethod]
    public async Task AWalkOverAQuietServerAsksTheListingsAloneAndANewBuildShowsAfterTheNextWalk()
    {
        var fake = BambooSeed.Lab();
        var settings = BambooSeed.Settings(fake);
        settings.RefreshSeconds = 1;
        await using var source = await StartAsync(fake, settings);
        await source.CountFilesAsync(CancellationToken.None);
        await Task.Delay(1200);

        // the projects, their plans, each plan's and branch's builds, each plan's branches: no build read again, no file asked for
        var before = fake.Requests.Count;
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        var walk = fake.Requests.Skip(before).ToList();
        var listings = new Regex(@"/rest/api/latest/(project\?|project/[^/?]+\?expand=plans|result/[A-Z0-9-]+\?expand=results|plan/[^/?]+/branch\?)");
        Assert.IsTrue(walk.All(r => listings.IsMatch(r)), "not a listing: " + string.Join("; ", walk.Where(r => !listings.IsMatch(r))));
        Assert.AreEqual(1 + 2 + 4 + 3, walk.Distinct().Count(), string.Join("; ", walk));

        // a new build: the next walk reads it, and it alone
        var plan = fake.PlanAt("ANYFS-BUILD");
        fake.AddBuild(plan, "Successful", BambooSeed.Day.AddDays(3), BambooSeed.Day.AddDays(3).AddSeconds(20), jobs: "JOB1");
        await Task.Delay(1200);
        CollectionAssert.DoesNotContain(await NamesAsync(source, Plan), "#4 Successful", "kept as it was until the walk");
        before = fake.Requests.Count;
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        CollectionAssert.Contains(await NamesAsync(source, Plan), "#4 Successful");
        CollectionAssert.AreEqual(new[] { "build.md", "Default Job" }, await NamesAsync(source, Plan + "/#4 Successful"));
        var builds = fake.Requests.Skip(before).Where(r => Regex.IsMatch(r, @"/result/[A-Z0-9-]+-\d+\?")).ToList();
        Assert.AreEqual(1, builds.Count, string.Join("; ", builds));
        StringAssert.Contains(builds[0], "/result/ANYFS-BUILD-4?");
    }

    [TestMethod]
    public async Task AListingPastAPageFollowsTheStartIndex()
    {
        var fake = BambooSeed.Lab();
        fake.PageLimit = 2;
        for (var i = 0; i < 5; i++)
        {
            fake.AddProject($"P{i}", $"Project {i}");
        }

        await using var source = await StartAsync(fake, BambooSeed.Settings(fake));
        Assert.AreEqual(7, (await NamesAsync(source, "")).Count);
        Assert.AreEqual(4, fake.Count("/rest/api/latest/project?"), "four pages of two");
    }

    [TestMethod]
    public async Task EveryRequestOfTheServerIsARowThatEnds()
    {
        var fake = BambooSeed.Lab();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, BambooSeed.Settings(fake), context);
        await source.CountFilesAsync(CancellationToken.None);
        await UpstreamRequestRows.AllEndAsync(context);
        Assert.IsTrue(fake.Requests.Count > 0);
        Assert.AreEqual(fake.Requests.Count, context.UpstreamRequests.Requests.Count, "every one the server saw, and no more");
        Assert.IsTrue(context.UpstreamRequests.Requests.All(r => r.Answer is not null));
    }

    [TestMethod]
    public async Task AThrottledAnswerIsWaitedForAndTheRequestMadeAgain()
    {
        var fake = BambooSeed.Lab();
        await using var source = await StartAsync(fake, BambooSeed.Settings(fake, "/browse/ANYFS-BUILD"));
        fake.ThrottleNext = 1;
        CollectionAssert.Contains(await NamesAsync(source, ""), "#1 Successful");
        Assert.IsTrue(fake.Count("/result/ANYFS-BUILD?") >= 2, "the refused request made again: " + string.Join("; ", fake.Requests));
    }

    [TestMethod]
    public async Task AFolderWhoseListingFailedStaysSoUntilTheNextWalk()
    {
        var fake = BambooSeed.Lab();
        await using var source = await StartAsync(fake, BambooSeed.Settings(fake, "/browse/ANYFS-BUILD"));
        var failing = true;
        var asked = 0;
        fake.Fault = r =>
        {
            if (failing && r.Url.AbsolutePath.EndsWith("/result/ANYFS-BUILD-1", StringComparison.Ordinal))
            {
                asked++;
                return FakeAtlassian.Response.Html("<html>busy</html>");
            }

            return null;
        };
        var first = await Assert.ThrowsExactlyAsync<VfsException>(async () => await NamesAsync(source, "#1 Successful"));
        Assert.AreEqual(VfsError.Unavailable, first.Error);
        var times = asked;
        Assert.IsTrue(times > 0);
        failing = false;
        var again = await Assert.ThrowsExactlyAsync<VfsException>(async () => await NamesAsync(source, "#1 Successful"));
        Assert.AreEqual(first.Message, again.Message, "answered at once as it failed");
        Assert.AreEqual(times, asked, "nothing asked");

        await source.WalkAsync(new SourceCounters(), full: true, CancellationToken.None);
        CollectionAssert.Contains(await NamesAsync(source, "#1 Successful"), "build.md");
    }

    // --- the settings and the Test button -------------------------------------------------------------------------------

    [TestMethod]
    public void ThePluginNamesValidatesAndShowsTheSignInsFields()
    {
        var plugin = new BambooPlugin();
        var settings = new BambooSettings { Address = "not an address", AccessToken = "t", BuildsKept = 0, PlansAtATime = 65, RequestsPerPlan = 0, RefreshSeconds = -1 };
        CollectionAssert.AreEquivalent(new[] { "Address", "BuildsKept", "PlansAtATime", "RequestsPerPlan", "RefreshSeconds" }, plugin.Validate(settings).Select(p => p.Property).ToList());
        Assert.IsFalse(plugin.Validate(new BambooSettings { Address = "https://bamboo.example.com/browse/ABC-DEF", AccessToken = "t" }).Any(m => m.IsError));
        Assert.AreEqual("ABC-DEF (bamboo.example.com)", plugin.SuggestName(new BambooSettings { Address = "https://bamboo.example.com/browse/ABC-DEF-12" }));
        Assert.AreEqual("ABC (bamboo.example.com)", plugin.SuggestName(new BambooSettings { Address = "https://bamboo.example.com/browse/ABC" }));
        Assert.AreEqual("bamboo.example.com", plugin.SuggestName(new BambooSettings { Address = "https://bamboo.example.com/allPlans.action" }));

        var s = new BambooSettings { Address = "https://bamboo.example.com/browse/ABC/", SignInMethod = AtlassianSignInChoices.Password };
        plugin.NormaliseSettings(s);
        Assert.AreEqual("https://bamboo.example.com/browse/ABC", s.Address);
        Assert.IsTrue(plugin.IsVisible(s, nameof(BambooSettings.Username)));
        Assert.IsFalse(plugin.IsVisible(s, nameof(BambooSettings.AccessToken)));
        using var icon = plugin.OpenIcon();
        Assert.IsNotNull(icon);
        Assert.AreEqual("3 min 5 s", BambooDocuments.Took(185));
        Assert.AreEqual("2 h 10 min", BambooDocuments.Took(7_830));
    }

    [TestMethod]
    public async Task TheTestSaysWhoAndWhat()
    {
        var fake = BambooSeed.Lab();
        var result = await BambooPlugin.TestAsync(BambooSeed.Settings(fake, "/browse/ANYFS-BUILD"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "(Bamboo 9.6.5) as Test Er (tester) with a personal access token");
        StringAssert.Contains(result.Message, "the plan Build the app (ANYFS-BUILD) of AnyFS Lab");

        result = await BambooPlugin.TestAsync(BambooSeed.Settings(fake), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "the 2 projects you can see");

        result = await BambooPlugin.TestAsync(BambooSeed.Settings(fake, "/browse/NOPE"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "no project NOPE");
    }
}

/// <summary>The SDK's contract for a read-only source, against the fake server.</summary>
[TestClass]
public class BambooContractTests : SourceContractTests
{
    protected override async Task<SourceContractSetup> SetUpAsync()
    {
        var fake = BambooSeed.Lab();
        var source = new BambooSource(BambooSeed.Settings(fake, "/browse/ANYFS-BUILD"), fake.StandIn());
        await source.InitialiseAsync(new TestSourceContext(), CancellationToken.None);
        return new SourceContractSetup(source, "#1 Successful/Default Job/artifacts/Reports/data", "#1 Successful/Default Job/artifacts/Reports/data/large.bin", BambooSeed.Large);
    }
}
