using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Artifactory;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>An Artifactory server as the fake holds it (spec 7.13): the accounts, repositories and files the tests read.</summary>
internal static class ArtifactorySeed
{
    public const string Password = "tester-password";

    public static readonly DateTimeOffset Day = new(2026, 10, 20, 9, 30, 0, TimeSpan.Zero);

    /// <summary>A megabyte and a bit, the same every time: a file fetched whole.</summary>
    public static readonly byte[] Large = Enumerable.Range(0, (1 << 20) + 77).Select(i => (byte)(i * 13 % 251)).ToArray();

    /// <summary>Nine megabytes and a bit: a file read in ranges.</summary>
    public static readonly byte[] Huge = Enumerable.Range(0, (9 << 20) + 5).Select(i => (byte)(i * 7 % 253)).ToArray();

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The files the tester sees with the settings as they come: 13 in generic-local, 4 in libs-release-local, 2 cached in npm-remote.</summary>
    public const int Files = 13 + 4 + 2;

    /// <summary>
    /// Three repositories the tester reads: generic-local (documents, a megabyte's file, an empty one, names an address must
    /// escape and names Windows refuses, twins in case, a folder with nothing in it), libs-release-local (a Maven layout) and
    /// npm-remote (a remote repository with two files cached); the virtual repository libs-release over the two local ones;
    /// and hidden-local, which the tester may not see. Everything is dated <see cref="Day"/>; the server's clock stands twelve days on.
    /// </summary>
    public static FakeArtifactory Lab(string root = "/artifactory")
    {
        var fake = new FakeArtifactory(root: root);
        var tester = fake.AddAccount("tester", password: Password);
        tester.Tokens.Add(fake.TokenFor("tester"));
        fake.AddAccount("admin", "admin-token", admin: true);
        fake.AddAccount("other", "other-token").Reads["hidden-local"] = "";

        fake.AddRepository("generic-local").Description = "What the tests read";
        void Put(string repository, string path, byte[] content) => fake.Put(repository, path, content, Day);
        Put("generic-local", "README.md", Bytes("# Docs\n\nRead me.\n"));
        Put("generic-local", "docs/guide.md", Bytes("# Guide\n\nStep one.\n"));
        Put("generic-local", "docs/images/logo.png", [137, 80, 78, 71, 1, 2, 3]);
        Put("generic-local", "docs/large.bin", Large);
        Put("generic-local", "docs/empty.txt", []);
        foreach (var name in new[] { "with space.txt", "plus+sign.txt", "hash#tag.txt", "semi;colon.txt", "colon:name.txt", "trailing." })
        {
            Put("generic-local", "names/" + name, Bytes(name + "\n"));
        }

        Put("generic-local", "names/Twin.txt", Bytes("big T\n"));
        Put("generic-local", "names/twin.txt", Bytes("small t\n"));
        fake.AddFolder("generic-local", "empty-folder", Day);

        fake.AddRepository("libs-release-local", packageType: "Maven");
        foreach (var version in new[] { "1.0", "1.1" })
        {
            Put("libs-release-local", $"org/acme/app/{version}/app-{version}.jar", Bytes("jar " + version));
            Put("libs-release-local", $"org/acme/app/{version}/app-{version}.pom", Bytes("<project>" + version + "</project>"));
        }

        fake.AddRepository("npm-remote", "REMOTE", "Npm");
        Put("npm-remote", "lodash/-/lodash-4.17.21.tgz", Bytes("tgz"));
        Put("npm-remote", ".npm/lodash/package.json", Bytes("{}"));

        fake.AddRepository("libs-release", "VIRTUAL", "Maven", "libs-release-local", "generic-local");
        fake.AddRepository("hidden-local");
        Put("hidden-local", "secret.txt", Bytes("s"));
        foreach (var key in new[] { "generic-local", "libs-release-local", "npm-remote" })
        {
            tester.Reads[key] = "";
        }

        return fake;
    }

    public static string Token(FakeArtifactory fake) => fake.Accounts.First(a => a.Name == "tester").Tokens[0];

    public static ArtifactorySettings Settings(FakeArtifactory fake, string path = "", string? token = null) => new()
    {
        Address = fake.Server + path,
        AccessToken = token ?? Token(fake),
        RefreshSeconds = 60,
        RepositoriesAtATime = 2,
        RequestsPerRepository = 2,
    };
}

/// <summary>The Artifactory source against <see cref="FakeArtifactory"/> (spec 7.13): the address, the sign-ins, the tree, the files and the walks.</summary>
[TestClass]
public class ArtifactoryTests
{
    private static async Task<ArtifactorySource> StartAsync(FakeArtifactory fake, ArtifactorySettings settings, TestSourceContext? context = null, ArtifactoryStandIn? standIn = null)
    {
        var source = new ArtifactorySource(settings, standIn ?? fake.StandIn());
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

    /// <summary>The server's clock moves on, and the source hears of it as it would from any answer.</summary>
    private static async Task LaterAsync(FakeArtifactory fake, ArtifactorySource source, TimeSpan by)
    {
        fake.Now += by;
        using var _ = await source.Api.GetAsync("system/version", CancellationToken.None);
    }

    private static List<string> QueriesSince(FakeArtifactory fake, int before) => fake.Queries.Skip(before).ToList();

    private static bool IsChanges(string query) => query.Contains("\"$gt\"", StringComparison.Ordinal);

    /// <summary>The time a query asks for what changed since, in a repository.</summary>
    private static string Since(IEnumerable<string> queries, string repository)
        => Regex.Match(queries.Last(q => IsChanges(q) && q.Contains($"\"repo\":\"{repository}\"", StringComparison.Ordinal)), @"""created"":\{""\$gt"":""([^""]+)""").Groups[1].Value;

    // --- the address ---------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("https://a.example.com/artifactory", "https://a.example.com/artifactory/", "", "")]
    [DataRow("https://a.example.com/artifactory/", "https://a.example.com/artifactory/", "", "")]
    [DataRow("https://acme.jfrog.io", "https://acme.jfrog.io/artifactory/", "", "")]
    [DataRow("https://a.example.com/artifactory/libs-release-local/org/acme", "https://a.example.com/artifactory/", "libs-release-local", "org/acme")]
    [DataRow("https://a.example.com/artifactory/generic-local/names/with%20space.txt", "https://a.example.com/artifactory/", "generic-local", "names/with space.txt")]
    [DataRow("https://a.example.com/ui/repos/tree/General/libs-release-local/org/acme", "https://a.example.com/artifactory/", "libs-release-local", "org/acme")]
    [DataRow("https://a.example.com/ui/repos/tree/Properties/libs-release-local", "https://a.example.com/artifactory/", "libs-release-local", "")]
    [DataRow("https://a.example.com/ui/native/libs-release-local/org/", "https://a.example.com/artifactory/", "libs-release-local", "org")]
    [DataRow("https://a.example.com/ui/packages", "https://a.example.com/artifactory/", "", "")]
    [DataRow("https://a.example.com/artifactory/webapp/#/artifacts/browse/tree/General/libs-release-local/org/acme", "https://a.example.com/artifactory/", "libs-release-local", "org/acme")]
    [DataRow("https://a.example.com/artifactory/webapp/#/home", "https://a.example.com/artifactory/", "", "")]
    [DataRow("https://a.example.com/artifactory/api/storage/libs-release-local/org", "https://a.example.com/artifactory/", "libs-release-local", "org")]
    [DataRow("https://a.example.com/artifactory/api/npm/npm-remote/lodash", "https://a.example.com/artifactory/", "npm-remote", "")]
    [DataRow("https://a.example.com/artifactory/api/system/version", "https://a.example.com/artifactory/", "", "")]
    [DataRow("https://a.example.com/artifactory/list/generic-local/docs/", "https://a.example.com/artifactory/", "generic-local", "docs")]
    [DataRow("https://a.example.com/jfrog/artifactory/generic-local", "https://a.example.com/jfrog/artifactory/", "generic-local", "")]
    [DataRow("https://a.example.com/artifactory/simple/generic-local/docs", "https://a.example.com/artifactory/", "generic-local", "docs")]
    [DataRow("https://a.example.com/platform/ui/repos/tree/General/generic-local/docs", "https://a.example.com/platform/artifactory/", "generic-local", "docs")]
    public void TheAddressNamesTheServerARepositoryOrAFolder(string given, string server, string repository, string path)
    {
        var address = ArtifactoryAddress.Candidates(ArtifactoryAddress.Tidy(given)!)[0];
        Assert.AreEqual(server, address.Base.AbsoluteUri);
        Assert.AreEqual(repository, address.Repository);
        Assert.AreEqual(path, address.Path);
    }

    [TestMethod]
    public void AnAddressThatNamesNoPlaceForArtifactoryIsTriedWhereJFrogPutsItThenAtTheRoot()
    {
        var candidates = ArtifactoryAddress.Candidates(ArtifactoryAddress.Tidy("https://repo.example.com/generic-local/docs")!);
        Assert.AreEqual("https://repo.example.com/artifactory/", candidates[0].Base.AbsoluteUri);
        Assert.AreEqual("https://repo.example.com/", candidates[1].Base.AbsoluteUri);
        Assert.AreEqual("generic-local", candidates[1].Repository);
        Assert.AreEqual("docs", candidates[1].Path);

        // or at a path of its own, which the address then begins with
        Assert.AreEqual("https://repo.example.com/generic-local/", candidates[2].Base.AbsoluteUri);
        Assert.AreEqual("docs", candidates[2].Repository);
        Assert.AreEqual("https://repo.example.com/generic-local/docs/", candidates[3].Base.AbsoluteUri);
        Assert.AreEqual(4, candidates.Count);
        Assert.IsNull(ArtifactoryAddress.Tidy("artifactory.example.com"));
        Assert.IsNull(ArtifactoryAddress.Tidy("ftp://artifactory.example.com/x"));
        Assert.AreEqual("libs%2Brelease/a%20b/c%23d%3Be", ArtifactoryAddress.Escaped("libs+release", "a b/c#d;e"));
    }

    [TestMethod]
    public async Task ArtifactoryAtTheHostsRootIsFound()
    {
        var fake = ArtifactorySeed.Lab(root: "");
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/generic-local/docs"));
        Assert.AreEqual("https://artifactory.fake.test/", source.Session.Address.Base.AbsoluteUri);
        Assert.AreEqual(ArtifactoryScope.Folder, source.Scope);
        CollectionAssert.AreEqual(new[] { "images", "empty.txt", "guide.md", "large.bin" }, await NamesAsync(source, ""));
    }

    [TestMethod]
    public async Task AnAddressThatIsNotArtifactorysSaysSo()
    {
        var fake = ArtifactorySeed.Lab(root: "/elsewhere");
        var source = new ArtifactorySource(new ArtifactorySettings { Address = "https://artifactory.fake.test/", AccessToken = ArtifactorySeed.Token(fake) }, fake.StandIn());
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        StringAssert.Contains(ex.Message, "did not answer as Artifactory does");
        await source.DisposeAsync();
    }

    // --- signing in ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ASignedTokenSignsInAndNamesItsAccount()
    {
        var fake = ArtifactorySeed.Lab();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake));
        Assert.AreEqual("tester", source.Session.User, "the token's subject");
        Assert.AreEqual("with an access token", source.Session.How);
        Assert.AreEqual("7.161.26", source.Session.Version);
        Assert.AreEqual(ArtifactoryScope.Server, source.Scope);
        Assert.IsTrue(source.Queries);
    }

    [TestMethod]
    public async Task AStartAsksForTheRepositoriesOnceAndSoDoesTheTest()
    {
        // the sign-in's check reads them, and the source's first list is that one
        var fake = ArtifactorySeed.Lab();
        await using (var server = await StartAsync(fake, ArtifactorySeed.Settings(fake)))
        {
            CollectionAssert.Contains(await NamesAsync(server, ""), "generic-local");
        }

        Assert.AreEqual(1, fake.Count("/api/repositories"), string.Join("\n", fake.Requests));

        // a repository's source finds its repository in it
        fake = ArtifactorySeed.Lab();
        await using (var repository = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/generic-local")))
        {
            CollectionAssert.Contains(await NamesAsync(repository, ""), "docs");
        }

        Assert.AreEqual(1, fake.Count("/api/repositories"), string.Join("\n", fake.Requests));

        fake = ArtifactorySeed.Lab();
        var result = await ArtifactoryPlugin.TestAsync(ArtifactorySeed.Settings(fake), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual(1, fake.Count("/api/repositories"), string.Join("\n", fake.Requests));
    }

    [TestMethod]
    public async Task AReferenceTokenSignsInWithoutNamingItsAccount()
    {
        var fake = ArtifactorySeed.Lab();
        fake.Accounts.First(a => a.Name == "tester").Tokens.Add("cmVmdGtuOjAxOjAwMDAwMDAwMDA6ZmFrZQ");
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake, token: "cmVmdGtuOjAxOjAwMDAwMDAwMDA6ZmFrZQ"));
        Assert.IsNull(source.Session.User, "nothing in the token says, and the platform's own call is its pages'");
        Assert.AreEqual("the access token", source.Session.Who);
        CollectionAssert.Contains(await NamesAsync(source, ""), "generic-local");
    }

    [TestMethod]
    public async Task AnApiKeyGoesInTheHeaderArtifactoryReadsOneFrom()
    {
        var fake = ArtifactorySeed.Lab();
        fake.Accounts.First(a => a.Name == "tester").ApiKey = "AKCp8fakefakefake";
        var withBearer = 0;
        var withKey = 0;
        fake.Fault = r =>
        {
            withBearer += r.Headers.ContainsKey("Authorization") ? 1 : 0;
            withKey += r.Headers.ContainsKey("X-JFrog-Art-Api") ? 1 : 0;
            return null;
        };
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake, token: "AKCp8fakefakefake"));
        Assert.AreEqual("with an API key", source.Session.How);
        CollectionAssert.Contains(await NamesAsync(source, ""), "generic-local");
        Assert.AreEqual(0, withBearer);
        Assert.IsTrue(withKey > 0);
    }

    [TestMethod]
    public async Task ARefusedTokenFailsForGood()
    {
        var fake = ArtifactorySeed.Lab();
        var source = new ArtifactorySource(ArtifactorySeed.Settings(fake, token: "wrong"), fake.StandIn());
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        Assert.IsTrue(ex.Permanent);
        StringAssert.Contains(ex.Message, "refused the access token");
        StringAssert.Contains(ex.Message, "Token failed verification", "in the server's own words");
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task AUserNameAndPasswordSignIn()
    {
        var fake = ArtifactorySeed.Lab();
        var settings = new ArtifactorySettings { Address = fake.Server + "/libs-release-local", SignInMethod = ArtifactorySettings.PasswordChoice, Username = "tester", Password = ArtifactorySeed.Password };
        await using var source = await StartAsync(fake, settings);
        Assert.AreEqual("tester", source.Session.User);
        CollectionAssert.AreEqual(new[] { "org" }, await NamesAsync(source, ""));

        settings.Password = "wrong";
        var refused = new ArtifactorySource(settings, fake.StandIn());
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => refused.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        StringAssert.Contains(ex.Message, "the user name and password of tester");
        await refused.DisposeAsync();
    }

    [TestMethod]
    public async Task WithNoSignInTheSourceShowsWhatAnyoneMayReadThroughTheStorageCalls()
    {
        var fake = ArtifactorySeed.Lab();
        fake.Anonymous = true;
        fake.AddAccount("anonymous").Reads["generic-local"] = "";
        var settings = new ArtifactorySettings { Address = fake.Server, SignInMethod = ArtifactorySettings.AnonymousChoice };
        await using var source = await StartAsync(fake, settings);
        Assert.IsFalse(source.Queries, "Artifactory keeps its query language from anyone not signed in");
        CollectionAssert.AreEqual(new[] { "generic-local" }, await NamesAsync(source, ""));
        var docs = await EntriesAsync(source, "generic-local/docs");
        CollectionAssert.AreEqual(new[] { "images", "empty.txt", "guide.md", "large.bin" }, docs.Select(e => e.Name).ToArray());
        Assert.AreEqual(19, docs.Single(e => e.Name == "guide.md").Length, "each file's size from its own call: the file list is for signed-in accounts");
        Assert.AreEqual(ArtifactorySeed.Day, docs.Single(e => e.Name == "guide.md").Modified);
        Assert.AreEqual("# Guide\n\nStep one.\n", await TextAsync(source, "generic-local/docs/guide.md"));
        Assert.AreEqual(1, fake.Count("?list"), "asked once, refused, not asked again");
    }

    [TestMethod]
    public async Task WithNoSignInAServerThatLetsNoOneInSaysSo()
    {
        var fake = ArtifactorySeed.Lab();
        var source = new ArtifactorySource(new ArtifactorySettings { Address = fake.Server, SignInMethod = ArtifactorySettings.AnonymousChoice }, fake.StandIn());
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        Assert.IsTrue(ex.Permanent);
        StringAssert.Contains(ex.Message, "it lets no one read without one");
        await source.DisposeAsync();
    }

    private static ArtifactorySettings WebSession(FakeArtifactory fake, string path = "/generic-local")
        => new() { Address = fake.Server + path, SignInMethod = ArtifactorySettings.WebSessionChoice };

    [TestMethod]
    public async Task AWebSessionSignsInThroughJFrogsClientSignInWithTheCodeItShowsAndReadsWithTheTokenItGets()
    {
        var fake = ArtifactorySeed.Lab();
        var asked = new List<bool>();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, WebSession(fake), context, fake.StandIn(fake.Person("tester", asked), useKept: false));
        Assert.AreEqual("tester", source.Session.User, "the token names its account");
        Assert.AreEqual("with the browser", source.Session.How);
        CollectionAssert.AreEqual(new[] { true }, asked);
        Assert.AreEqual("# Guide\n\nStep one.\n", await TextAsync(source, "docs/guide.md"));

        // the platform's page for a session of AnyFS's own, asking for the code, as JFrog's own tool opens it
        var page = fake.SignInPages.Single();
        Assert.AreEqual("https://artifactory.fake.test/ui/login", page.GetLeftPart(UriPartial.Path));
        var query = page.Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
        Assert.IsTrue(Guid.TryParse(query["jfClientSession"], out _), query["jfClientSession"]);
        Assert.AreEqual("AnyFS", query["jfClientName"]);
        Assert.AreEqual("1", query["jfClientCode"]);

        // the person is told the code: the session's last four characters
        string[] said;
        lock (context.Progress)
        {
            said = context.Progress.Select(p => p.Message).ToArray();
        }

        var code = query["jfClientSession"][^4..];
        Assert.IsTrue(said.Any(m => m.Contains($"enter {code}", StringComparison.Ordinal) && m.Contains("the code that you received from AnyFS", StringComparison.Ordinal)), string.Join(" | ", said));

        // the token went on the requests, the page's own sign-in nowhere
        Assert.AreEqual(1, fake.ClientTokens.Count);
        Assert.AreEqual(0, fake.Count("/ui/api/v1/ui/auth/current"));
    }

    [TestMethod]
    public async Task AWebSessionsTokenIsRenewedBeforeItRunsOutWithNoWindow()
    {
        var fake = ArtifactorySeed.Lab();
        var asked = new List<bool>();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, WebSession(fake), context, fake.StandIn(fake.Person("tester", asked), useKept: false));
        CollectionAssert.Contains(await NamesAsync(source, ""), "README.md");

        // within a week of the year's end (a tenth of its life, at most a week, as JFrog's own tool has it): renewed
        // before a request, once, with the refresh token, and the renewal kept for the next start
        fake.Now += TimeSpan.FromDays(365 - 6);
        Assert.AreEqual("# Guide\n\nStep one.\n", await TextAsync(source, "docs/guide.md"));
        Assert.IsTrue((await NamesAsync(source, "docs/images")).Count > 0);
        Assert.AreEqual(1, fake.Count("POST /access/api/v1/tokens"), "renewed once");
        Assert.AreEqual(2, fake.ClientTokens.Count);
        StringAssert.Contains(context.Secrets.Get(ArtifactorySession.TokenSecret), fake.ClientTokens[^1]);
        CollectionAssert.AreEqual(new[] { true }, asked, "no window for it");
    }

    [TestMethod]
    public async Task AWebSessionThatHasEndedWaitsForTheSourcesRetryInsteadOfOpeningAWindow()
    {
        var fake = ArtifactorySeed.Lab();
        var asked = new List<bool>();
        await using var source = await StartAsync(fake, WebSession(fake), standIn: fake.StandIn(fake.Person("tester", asked), useKept: false));
        fake.EndSessions();
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(() => TextAsync(source, "docs/guide.md"));
        StringAssert.Contains(ex.Message, "only you can sign in again");
        StringAssert.Contains(ex.Message, "Retry the source to sign in");
        CollectionAssert.AreEqual(new[] { true }, asked, "the page asks the person for a code: no browser without them");

        // the renewal Artifactory refused is not asked for again
        var again = await Assert.ThrowsExactlyAsync<VfsException>(() => TextAsync(source, "docs/large.bin"));
        StringAssert.Contains(again.Message, "only you can sign in again");
        Assert.AreEqual(1, fake.Count("POST /access/api/v1/tokens"));
    }

    [TestMethod]
    public async Task TwoSourcesOfOneServerEachSignInAndRenewATokenOfTheirOwn()
    {
        var fake = ArtifactorySeed.Lab(root: "/two");
        var asked = new List<bool>();
        using var firstContext = new TestSourceContext();
        using var secondContext = new TestSourceContext();
        await using var first = await StartAsync(fake, WebSession(fake), firstContext, fake.StandIn(fake.Person("tester", asked)));
        await using var second = await StartAsync(fake, WebSession(fake, "/generic-local/docs"), secondContext, fake.StandIn(fake.Person("tester", asked)));
        CollectionAssert.AreEqual(new[] { true, true }, asked, "each its own sign-in: a refresh token renews once, so two sessions cannot share one");
        Assert.AreEqual(2, fake.ClientTokens.Count);

        // near the year's end both are renewed, each with its own refresh token
        fake.Now += TimeSpan.FromDays(360);
        foreach (var (source, path) in new[] { (first, "docs/"), (second, "") })
        {
            Assert.AreEqual("# Guide\n\nStep one.\n", await TextAsync(source, path + "guide.md"));
            Assert.IsTrue((await NamesAsync(source, path + "images")).Count > 0);
        }

        Assert.AreEqual(2, fake.Count("POST /access/api/v1/tokens"));
        Assert.AreEqual(4, fake.ClientTokens.Count);
        Assert.AreNotEqual(firstContext.Secrets.Get(ArtifactorySession.TokenSecret), secondContext.Secrets.Get(ArtifactorySession.TokenSecret));
    }

    [TestMethod]
    public async Task ASignInKeptFromBeforeSignsInAtTheNextStartAndIsRenewedNearItsEnd()
    {
        var fake = ArtifactorySeed.Lab(root: "/kept");
        using var context = new TestSourceContext();
        var settings = WebSession(fake, "");
        await using (var first = await StartAsync(fake, settings, context, fake.StandIn(fake.Person("tester"), useKept: false)))
        {
            Assert.AreEqual("tester", first.Session.User);
        }

        var kept = context.Secrets.Get(ArtifactorySession.TokenSecret);
        Assert.IsNotNull(kept);
        StringAssert.Contains(kept, fake.ClientTokens.Single());
        var nobody = fake.StandIn((_, _, _) => throw new InvalidOperationException("no browser should be needed"));
        await using (var second = await StartAsync(fake, settings, context, nobody))
        {
            Assert.AreEqual("with the sign-in kept from before", second.Session.How);
            Assert.AreEqual("tester", second.Session.User);
        }

        // started again near the year's end: the kept token signs in, and the first request after renews it
        fake.Now += TimeSpan.FromDays(360);
        await using (var third = await StartAsync(fake, settings, context, nobody))
        {
            Assert.AreEqual("with the sign-in kept from before", third.Session.How);
            CollectionAssert.Contains(await NamesAsync(third, "generic-local"), "README.md");
            Assert.AreEqual(2, fake.ClientTokens.Count);
        }

        StringAssert.Contains(context.Secrets.Get(ArtifactorySession.TokenSecret), fake.ClientTokens[^1]);
        Assert.AreEqual(1, fake.Count("POST /access/api/v1/tokens"));
    }

    [TestMethod]
    public async Task ATokenRefusedAsRunOutIsRenewedAndTheRequestAskedAgain()
    {
        var fake = ArtifactorySeed.Lab();
        var asked = new List<bool>();
        await using var source = await StartAsync(fake, WebSession(fake), standIn: fake.StandIn(fake.Person("tester", asked), useKept: false));
        CollectionAssert.Contains(await NamesAsync(source, ""), "README.md");

        // past the year with nothing asked in its last week (a computer asleep): refused as run out, renewed with the
        // token that ran out as the bearer, which the platform takes for that, and asked again
        fake.Now += TimeSpan.FromDays(400);
        Assert.AreEqual("# Guide\n\nStep one.\n", await TextAsync(source, "docs/guide.md"));
        Assert.AreEqual(1, fake.Count("POST /access/api/v1/tokens"));
        CollectionAssert.AreEqual(new[] { true }, asked, "no window for it");
    }

    [TestMethod]
    public async Task AKeptSignInThatRanOutWhileAnyFSWasOffIsRenewedAtTheStartWithNoWindow()
    {
        var fake = ArtifactorySeed.Lab(root: "/ran-out");
        using var context = new TestSourceContext();
        var settings = WebSession(fake, "");
        await using (await StartAsync(fake, settings, context, fake.StandIn(fake.Person("tester"), useKept: false)))
        {
        }

        fake.Now += TimeSpan.FromDays(400);
        await using var again = await StartAsync(fake, settings, context, fake.StandIn((_, _, _) => throw new InvalidOperationException("no browser should be needed")));
        Assert.AreEqual("with the sign-in kept from before", again.Session.How);
        Assert.AreEqual(1, fake.Count("POST /access/api/v1/tokens"));
        Assert.AreEqual(2, fake.Count($"GET {fake.Root}/api/repositories"), "once at each start: the token that ran out was renewed before anything was asked with it");
        StringAssert.Contains(context.Secrets.Get(ArtifactorySession.TokenSecret), fake.ClientTokens[^1]);
    }

    [TestMethod]
    public async Task AKeptSignInRenewedAsAStartBeginsIsKeptThoughTheStartIsCutShort()
    {
        // A refresh token is good for one use. A kept token that had run out was renewed as the start began, and the pair
        // the platform answered with was kept only once the start was through, two requests later. A connection dropped
        // or AnyFS closed in between left the spent token in the store, and the next start asked the person for a
        // browser and a code (the review of 21-Nov-2026, R9a)
        var fake = ArtifactorySeed.Lab(root: "/cut-short");
        using var context = new TestSourceContext();
        var settings = WebSession(fake, "");
        await using (await StartAsync(fake, settings, context, fake.StandIn(fake.Person("tester"), useKept: false)))
        {
        }

        fake.Now += TimeSpan.FromDays(400);
        fake.Fault = r => r.Method == "GET" && r.Url.AbsolutePath.EndsWith("/api/repositories", StringComparison.Ordinal) ? throw new HttpRequestException("the connection was closed") : null;
        var noBrowser = fake.StandIn((_, _, _) => throw new InvalidOperationException("no browser should be needed"));
        await Assert.ThrowsAsync<Exception>(() => StartAsync(fake, settings, context, noBrowser));
        Assert.AreEqual(1, fake.Count("POST /access/api/v1/tokens"), "the token was renewed before the connection dropped");
        StringAssert.Contains(context.Secrets.Get(ArtifactorySession.TokenSecret), fake.ClientTokens[^1], "the pair the renewal answered with is the one kept");

        fake.Fault = null;
        await using var again = await StartAsync(fake, settings, context, noBrowser);
        Assert.AreEqual("with the sign-in kept from before", again.Session.How);
    }
    [TestMethod]
    public async Task AKeptSignInNothingRenewsAnyMoreAsksThePersonAgain()
    {
        var fake = ArtifactorySeed.Lab();
        using var context = new TestSourceContext();
        await using (await StartAsync(fake, WebSession(fake), context, fake.StandIn(fake.Person("tester"), useKept: false)))
        {
        }

        fake.EndSessions();
        var asked = new List<bool>();
        await using var again = await StartAsync(fake, WebSession(fake), context, fake.StandIn(fake.Person("tester", asked)));
        Assert.AreEqual("with the browser", again.Session.How);
        CollectionAssert.AreEqual(new[] { true }, asked);
        StringAssert.Contains(context.Secrets.Get(ArtifactorySession.TokenSecret), fake.ClientTokens[^1], "the new sign-in kept in place of the old");
    }

    [TestMethod]
    public async Task AStartTheHostRetriesByItselfShowsNoSignInWindow()
    {
        var fake = ArtifactorySeed.Lab(root: "/unattended");
        var asked = new List<bool>();
        var standIn = fake.StandIn(fake.Person("tester", asked), useKept: false);
        using var context = new TestSourceContext { Attended = false };
        var source = new ArtifactorySource(WebSession(fake), standIn);
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(context, CancellationToken.None));
        await source.DisposeAsync();
        Assert.AreEqual(0, asked.Count, "no window for nobody: the page asks a person for a code");
        Assert.AreEqual(0, fake.SessionsAsked.Count, "and no sign-in asked of the platform for it");
        Assert.IsFalse(ex.Permanent);
        StringAssert.Contains(ex.Message, "only you can sign in again");

        context.Attended = true;
        await using var retried = await StartAsync(fake, WebSession(fake), context, standIn);
        CollectionAssert.AreEqual(new[] { true }, asked, "the person's Retry signs in with the window");
    }

    [TestMethod]
    public async Task AServerWithoutTheClientSignInSaysToUseAnAccessToken()
    {
        var fake = ArtifactorySeed.Lab();
        fake.ClientSignIn = false;
        var asked = new List<bool>();
        var source = new ArtifactorySource(WebSession(fake), fake.StandIn(fake.Person("tester", asked), useKept: false));
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        Assert.IsTrue(ex.Permanent, "trying again will not give the server one");
        StringAssert.Contains(ex.Message, "has no client sign-in");
        StringAssert.Contains(ex.Message, "choose Access token");
        Assert.AreEqual(0, asked.Count, "no browser for a sign-in that cannot end in a token");
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task APersonWhoClosesTheWindowLeavesTheSourceWaitingForARetry()
    {
        var fake = ArtifactorySeed.Lab();
        var source = new ArtifactorySource(WebSession(fake), fake.StandIn((_, _, _) => Task.CompletedTask, useKept: false));
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        StringAssert.Contains(ex.Message, "the browser window was closed before the sign-in to artifactory.fake.test finished; Retry to sign in");
        Assert.IsTrue(ex.Permanent, "the source waits for its Retry rather than opening the window again");
        await source.DisposeAsync();
    }

    [TestMethod]
    public async Task APersonWhoClosesTheWindowJustAfterGivingTheCodeIsSignedIn()
    {
        // the code given and the window closed before AnyFS asks the platform for the token again: it still asks once more
        var fake = ArtifactorySeed.Lab();
        await using var source = await StartAsync(fake, WebSession(fake), standIn: fake.StandIn(async (_, page, ct) =>
        {
            await Task.Delay(500, ct);
            fake.SignInAt(page, "tester");
        }, useKept: false));
        Assert.AreEqual("tester", source.Session.User);
        Assert.AreEqual("with the browser", source.Session.How);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ASignInNotFinishedWhileThePlatformHoldsItClosesItsWindowAndWaitsForARetry()
    {
        // the platform forgets a client sign-in's session a while after it was asked for (five minutes on a real one); a
        // person slower than that has nothing to give the code to, so the window closes then, as JFrog's own tool gives up
        var fake = ArtifactorySeed.Lab();
        fake.ClientSessionsLast = TimeSpan.FromSeconds(2);
        var said = new ConcurrentQueue<string>();
        var source = new ArtifactorySource(WebSession(fake), fake.StandIn(fake.PersonTaking(TimeSpan.FromSeconds(6), "tester", said), useKept: false));
        using var context = new TestSourceContext();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(context, stop.Token));
            StringAssert.Contains(ex.Message, "the sign-in to artifactory.fake.test was not finished within the 2 seconds Artifactory holds it for; Retry to sign in again");
            Assert.IsTrue(ex.Permanent, "the source waits for its Retry, which signs in with a new code");
            CollectionAssert.AreEqual(new[] { "closed before the code was given" }, said.ToArray(), "the window closed when the platform forgot the session, not left open for a code that could go nowhere");
            Assert.AreEqual(0, fake.ClientTokens.Count);
        }
        finally
        {
            await source.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task TwoSourcesOfOneServerSigningInAtOnceEachHaveThePlatformsWholeTimeOnceTheirWindowOpens()
    {
        // both start at once, as at AnyFS's start: one window at a time on the server's profile, so the second's waits for
        // the first's; each person gives the code a while after their window opens, within the time the platform holds a
        // session for, which runs for each from its own window
        var fake = ArtifactorySeed.Lab(root: "/both");
        fake.ClientSessionsLast = TimeSpan.FromSeconds(5);
        var said = new ConcurrentQueue<string>();
        var person = fake.PersonTaking(TimeSpan.FromSeconds(3), "tester", said);
        var first = new ArtifactorySource(WebSession(fake), fake.StandIn(person, useKept: false));
        var second = new ArtifactorySource(WebSession(fake, "/generic-local/docs"), fake.StandIn(person, useKept: false));
        using var firstContext = new TestSourceContext();
        using var secondContext = new TestSourceContext();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await Task.WhenAll(first.InitialiseAsync(firstContext, stop.Token), second.InitialiseAsync(secondContext, stop.Token));
            CollectionAssert.AreEqual(new[] { "code given", "code given" }, said.ToArray());
            Assert.AreEqual(2, fake.ClientTokens.Count, "each its own token");
            Assert.AreEqual(2, fake.SessionsAsked.Count, "a session for each window");
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task TheTestButtonSignsInTheWebSessionAndTheSourceStartedFromItAsksNoMore()
    {
        var fake = ArtifactorySeed.Lab(root: "/tested");
        var asked = new List<bool>();
        var settings = WebSession(fake, "");
        var result = await ArtifactoryPlugin.TestAsync(settings, new Progress<string>(), fake.StandIn(fake.Person("tester", asked), useKept: false), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "as tester with the browser");
        await using var started = await StartAsync(fake, settings, standIn: fake.StandIn((_, _, _) => throw new InvalidOperationException("signed in by the Test")));
        Assert.AreEqual("with the sign-in kept from before", started.Session.How);
        CollectionAssert.AreEqual(new[] { true }, asked);
    }

    // --- the tree ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheServerShowsTheRepositoriesTheAccountCanSeeOfTheKindsAskedFor()
    {
        var fake = ArtifactorySeed.Lab();
        await using (var source = await StartAsync(fake, ArtifactorySeed.Settings(fake)))
        {
            CollectionAssert.AreEqual(new[] { "generic-local", "libs-release-local", "npm-remote" }, await NamesAsync(source, ""), "the virtual one left out, the hidden one not there");
            Assert.IsTrue((await source.StatAsync("generic-local", CancellationToken.None))!.IsDirectory);
            Assert.IsNull(await source.StatAsync("hidden-local", CancellationToken.None));
            Assert.IsNull(await source.StatAsync("libs-release", CancellationToken.None));
        }

        var settings = ArtifactorySeed.Settings(fake);
        settings.VirtualRepositories = true;
        settings.RemoteRepositories = false;
        await using var other = await StartAsync(fake, settings);
        CollectionAssert.AreEqual(new[] { "generic-local", "libs-release", "libs-release-local" }, await NamesAsync(other, ""));
    }

    [TestMethod]
    public async Task AFolderListsItsFoldersThenItsFilesWithSizesTimesAndChecksums()
    {
        var fake = ArtifactorySeed.Lab();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake));
        var top = await EntriesAsync(source, "generic-local");
        CollectionAssert.AreEqual(new[] { "docs", "empty-folder", "names", "README.md" }, top.Select(e => e.Name).ToArray(), "folders first, then files, each by name");
        var readme = top.Single(e => e.Name == "README.md");
        Assert.AreEqual(17, readme.Length);
        Assert.AreEqual(ArtifactorySeed.Day, readme.Modified);
        Assert.AreEqual(ArtifactorySeed.Day, readme.Created);
        Assert.AreEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(ArtifactorySeed.Bytes("# Docs\n\nRead me.\n"))), readme.ETag, "the file's SHA-1");
        Assert.IsTrue(top[0].IsDirectory);
        Assert.AreEqual(ArtifactorySeed.Day, top[0].Modified);
        CollectionAssert.AreEqual(new[] { "images", "empty.txt", "guide.md", "large.bin" }, await NamesAsync(source, "generic-local/docs"));
        Assert.AreEqual(0, (await NamesAsync(source, "generic-local/empty-folder")).Count);
        Assert.AreEqual(ArtifactorySeed.Large.Length, (await source.StatAsync("generic-local/docs/large.bin", CancellationToken.None))!.Length);
        Assert.IsNull(await source.StatAsync("generic-local/docs/nothing.txt", CancellationToken.None));
        Assert.AreEqual("# Guide\n\nStep one.\n", await TextAsync(source, "GENERIC-LOCAL/Docs/GUIDE.md"), "found in either case");
        CollectionAssert.AreEqual(new[] { "app-1.0.jar", "app-1.0.pom" }, await NamesAsync(source, "libs-release-local/org/acme/app/1.0"));
    }

    [TestMethod]
    public async Task NamesWindowsRefusesAndTwinsInCaseShowUnderNamesOfTheirOwnAndOpen()
    {
        var fake = ArtifactorySeed.Lab();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/generic-local/names"));
        CollectionAssert.AreEqual(new[] { "Twin.txt", "colon_name.txt", "hash#tag.txt", "plus+sign.txt", "semi;colon.txt", "trailing._", "twin (2).txt", "with space.txt" }, await NamesAsync(source, ""));
        Assert.AreEqual("big T\n", await TextAsync(source, "Twin.txt"));
        Assert.AreEqual("small t\n", await TextAsync(source, "twin (2).txt"));
        Assert.AreEqual("colon:name.txt\n", await TextAsync(source, "colon_name.txt"));
        Assert.AreEqual("trailing.\n", await TextAsync(source, "trailing._"));

        // names an address has to escape: each asked for as its own, a ; not taken for the start of parameters
        foreach (var name in new[] { "with space.txt", "plus+sign.txt", "hash#tag.txt", "semi;colon.txt" })
        {
            Assert.AreEqual(name + "\n", await TextAsync(source, name), name);
        }

        Assert.AreEqual(1, fake.Count("GET /artifactory/generic-local/names/semi;colon.txt"), string.Join("; ", fake.Requests));
    }

    [TestMethod]
    public async Task ARemoteRepositoryShowsWhatItHasCachedAndAReadFetchesNothingFromTheOtherServer()
    {
        var fake = ArtifactorySeed.Lab();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake));
        CollectionAssert.AreEqual(new[] { ".npm", "lodash" }, await NamesAsync(source, "npm-remote"));
        Assert.AreEqual("tgz", await TextAsync(source, "npm-remote/lodash/-/lodash-4.17.21.tgz"));
        Assert.IsTrue(fake.Queries.Any(q => q.Contains("\"repo\":\"npm-remote-cache\"", StringComparison.Ordinal)), "listed from its cache");
        Assert.AreEqual(1, fake.Count("GET /artifactory/npm-remote-cache/lodash/-/lodash-4.17.21.tgz"), "read from its cache");
        Assert.AreEqual(0, fake.RemoteFetches);

        // the cache's own key, as Artifactory's pages show it in their tree, names the repository
        await using var cache = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/npm-remote-cache/lodash"));
        CollectionAssert.AreEqual(new[] { "-" }, await NamesAsync(cache, ""));
    }

    [TestMethod]
    public async Task AVirtualRepositoryShowsItsMembersAsOneTree()
    {
        var fake = ArtifactorySeed.Lab();
        var settings = ArtifactorySeed.Settings(fake);
        settings.VirtualRepositories = true;
        await using var source = await StartAsync(fake, settings);
        CollectionAssert.AreEqual(new[] { "docs", "empty-folder", "names", "org", "README.md" }, await NamesAsync(source, "libs-release"), "the members' folders once each");
        Assert.AreEqual(17, (await source.StatAsync("libs-release/README.md", CancellationToken.None))!.Length);
        Assert.AreEqual("jar 1.1", await TextAsync(source, "libs-release/org/acme/app/1.1/app-1.1.jar"));
        Assert.IsFalse(fake.Queries.Any(q => q.Contains("\"repo\":\"libs-release\"", StringComparison.Ordinal)), "through the storage calls: a query gives each member's items apart");
    }

    [TestMethod]
    public async Task AnAddressOfARepositoryAFolderOrAFileShowsThatMuch()
    {
        var fake = ArtifactorySeed.Lab();
        await using (var repository = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/libs-release-local")))
        {
            Assert.AreEqual(ArtifactoryScope.Repository, repository.Scope);
            CollectionAssert.AreEqual(new[] { "org" }, await NamesAsync(repository, ""));
            Assert.AreEqual("jar 1.0", await TextAsync(repository, "org/acme/app/1.0/app-1.0.jar"));
        }

        await using (var folder = await StartAsync(fake, new ArtifactorySettings { Address = "https://artifactory.fake.test/ui/repos/tree/General/libs-release-local/org/acme", AccessToken = ArtifactorySeed.Token(fake) }))
        {
            Assert.AreEqual(ArtifactoryScope.Folder, folder.Scope);
            CollectionAssert.AreEqual(new[] { "app" }, await NamesAsync(folder, ""));
            CollectionAssert.AreEqual(new[] { "1.0", "1.1" }, await NamesAsync(folder, "app"));
        }

        // a file's address: its folder
        await using var file = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/generic-local/docs/guide.md"));
        Assert.AreEqual(ArtifactoryScope.Folder, file.Scope);
        CollectionAssert.Contains(await NamesAsync(file, ""), "guide.md");
    }

    [TestMethod]
    public async Task ARepositoryOrFolderTheAccountCannotSeeIsNotThere()
    {
        var fake = ArtifactorySeed.Lab();
        var hidden = new ArtifactorySource(ArtifactorySeed.Settings(fake, "/hidden-local"), fake.StandIn());
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => hidden.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        Assert.IsTrue(ex.Permanent);
        StringAssert.Contains(ex.Message, "has no repository hidden-local that tester can see");
        await hidden.DisposeAsync();

        var missing = new ArtifactorySource(ArtifactorySeed.Settings(fake, "/generic-local/nowhere"), fake.StandIn());
        ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => missing.InitialiseAsync(new TestSourceContext(), CancellationToken.None));
        StringAssert.Contains(ex.Message, "the repository generic-local has no nowhere");
        await missing.DisposeAsync();
    }

    [TestMethod]
    public async Task AnAccountThatReadsPartOfARepositorySeesThatPartWhole()
    {
        var fake = ArtifactorySeed.Lab();
        var partial = fake.AddAccount("partial");
        partial.Tokens.Add(fake.TokenFor("partial"));
        partial.Reads["generic-local"] = "docs";
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake, token: partial.Tokens[0]), context);
        await source.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
        Assert.AreEqual(4, source.Counters.FilesKnown, "docs and what is below it");
        CollectionAssert.AreEqual(new[] { "docs" }, await NamesAsync(source, "generic-local"));
        CollectionAssert.AreEqual(new[] { "images", "empty.txt", "guide.md", "large.bin" }, await NamesAsync(source, "generic-local/docs"));
        Assert.IsFalse(fake.Queries.Any(q => q.Contains(".limit(", StringComparison.Ordinal)), "no limit on a query: Artifactory applies one before it leaves out what the account may not read");
    }

    // --- files ---------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AFileReadsWholeAndFromAnOffsetAndAnEmptyOneAsksNothing()
    {
        var fake = ArtifactorySeed.Lab();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/generic-local"));
        CollectionAssert.AreEqual(ArtifactorySeed.Large, await ReadAsync(source, "docs/large.bin"));
        CollectionAssert.AreEqual(ArtifactorySeed.Large[500_000..], await ReadAsync(source, "docs/large.bin", 500_000));
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 1, 2, 3 }, await ReadAsync(source, "docs/images/logo.png"));
        var before = fake.Requests.Count;
        Assert.AreEqual(0, (await ReadAsync(source, "docs/empty.txt")).Length);
        Assert.AreEqual(before, fake.Requests.Count);
        await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync("docs", VfsOpenMode.Read, CancellationToken.None));
        var refused = await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync("docs/guide.md", VfsOpenMode.ReadWrite, CancellationToken.None));
        Assert.AreEqual(VfsError.NotSupported, refused.Error);
    }

    [TestMethod]
    public async Task ALongFileIsReadInRangesFromWhereItIsAskedFor()
    {
        var fake = ArtifactorySeed.Lab();
        fake.Put("generic-local", "big/huge.bin", ArtifactorySeed.Huge, ArtifactorySeed.Day);
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/generic-local/big"));
        var from = ArtifactorySeed.Huge.Length - 4_000;
        CollectionAssert.AreEqual(ArtifactorySeed.Huge[from..], await ReadAsync(source, "huge.bin", from));
        Assert.AreEqual(1, fake.Count($"GET /artifactory/generic-local/big/huge.bin bytes={from}-"), string.Join("; ", fake.Requests));
        await using var file = await source.OpenAsync("huge.bin", VfsOpenMode.Read, CancellationToken.None);
        var start = new byte[100_000];
        Assert.AreEqual(start.Length, await file.ReadFullyAsync(0, start, CancellationToken.None));
        CollectionAssert.AreEqual(ArtifactorySeed.Huge[..start.Length], start);
    }

    [TestMethod]
    public async Task ADownloadSentOnToAStorageServiceGoesThereWithoutTheSignIn()
    {
        var fake = ArtifactorySeed.Lab();
        fake.Redirects = true;
        fake.Put("generic-local", "big/huge.bin", ArtifactorySeed.Huge, ArtifactorySeed.Day);
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/generic-local"));
        CollectionAssert.AreEqual(ArtifactorySeed.Large, await ReadAsync(source, "docs/large.bin"));
        Assert.AreEqual(1, fake.Count("GET /filestore/"), "the storage service refuses a request that carries Artifactory's sign-in");
        var from = ArtifactorySeed.Huge.Length - 1_000;
        CollectionAssert.AreEqual(ArtifactorySeed.Huge[from..], await ReadAsync(source, "big/huge.bin", from));
        Assert.IsTrue(fake.Requests.Any(r => r.StartsWith("GET /filestore/", StringComparison.Ordinal) && r.EndsWith($"bytes={from}-", StringComparison.Ordinal)), "the range goes with it");
    }

    // --- the walks -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheCountReadsEachRepositoryInOneQueryAndSaysHowFarItHasGot()
    {
        var fake = ArtifactorySeed.Lab();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake), context);
        await source.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
        Assert.AreEqual(ArtifactorySeed.Files, source.Counters.FilesKnown);
        var said = CountProgress(context);
        Assert.AreEqual("Counting files: listing the repositories", said[0]);
        Assert.AreEqual("Counting files: 1 of 3 repositories", said[1]);
        Assert.AreEqual("Counting files: 3 of 3 repositories", said[^1]);

        // the question that finds nothing at the start, then a repository a query, its answer asked for a batch at a time
        var queries = fake.Queries.ToList();
        Assert.AreEqual(1 + 3, queries.Count, string.Join("\n", queries));
        foreach (var key in new[] { "generic-local", "libs-release-local", "npm-remote-cache" })
        {
            Assert.AreEqual(1, queries.Count(q => q.StartsWith($"stream items.find({{\"repo\":\"{key}\",\"type\":\"any\"}})", StringComparison.Ordinal)), key);
        }

        // every folder answers from what the walk kept: a file's bytes are the one thing asked for
        var asked = fake.Requests.Count;
        Assert.AreEqual("jar 1.1", await TextAsync(source, "libs-release-local/org/acme/app/1.1/app-1.1.jar"));
        CollectionAssert.AreEqual(new[] { "Twin.txt", "colon_name.txt", "hash#tag.txt", "plus+sign.txt", "semi;colon.txt", "trailing._", "twin (2).txt", "with space.txt" }, await NamesAsync(source, "generic-local/names"));
        Assert.AreEqual("small t\n", await TextAsync(source, "generic-local/names/twin (2).txt"));
        Assert.AreEqual(asked + 2, fake.Requests.Count, ListEverything.Since(fake.Requests, asked));
    }

    [TestMethod]
    public async Task AfterTheCountAPassOverEverythingAsksTheServerNothing()
    {
        var fake = ArtifactorySeed.Lab();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake), context);
        await source.CountFilesAsync(CancellationToken.None);
        await UpstreamRequestRows.AllEndAsync(context);
        var before = fake.Requests.Count;
        var folders = await ListEverything.OfAsync(source);
        Assert.AreEqual(1 + 3 + 4 + 5 + 4, folders);
        Assert.AreEqual(before, fake.Requests.Count, "asked for after the walk: " + ListEverything.Since(fake.Requests, before));
    }

    [TestMethod]
    public async Task OnceTheRefreshIntervalHasPassedBrowsingStillAsksNothing()
    {
        // the walks are what checks kept folders: a path is found from the kept listings, the repositories' kinds among them
        var fake = ArtifactorySeed.Lab();
        var settings = ArtifactorySeed.Settings(fake);
        settings.RefreshSeconds = 1;
        settings.VirtualRepositories = true;
        await using var source = await StartAsync(fake, settings);
        await source.CountFilesAsync(CancellationToken.None);
        await Task.Delay(1200);
        var before = fake.Requests.Count;
        await ListEverything.OfAsync(source);
        Assert.IsTrue((await source.StatAsync("npm-remote/lodash/-/lodash-4.17.21.tgz", CancellationToken.None))!.Length > 0);
        Assert.AreEqual(before, fake.Requests.Count, "asked for after the interval: " + ListEverything.Since(fake.Requests, before));

        // a file read goes where the kept listing says: a remote repository's cache, a virtual repository's own key
        Assert.AreEqual("tgz", await TextAsync(source, "npm-remote/lodash/-/lodash-4.17.21.tgz"));
        Assert.AreEqual("jar 1.0", await TextAsync(source, "libs-release/org/acme/app/1.0/app-1.0.jar"));
        CollectionAssert.AreEqual(new[] { "GET /artifactory/npm-remote-cache/lodash/-/lodash-4.17.21.tgz", "GET /artifactory/libs-release/org/acme/app/1.0/app-1.0.jar" }, fake.Requests.Skip(before).ToArray());
    }

    [TestMethod]
    public async Task ARepositoryTooBigToReadAtOnceIsReadInPieces()
    {
        var fake = ArtifactorySeed.Lab();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/generic-local"));
        source.TreeMost = 5;
        await source.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
        Assert.AreEqual(13, source.Counters.FilesKnown);
        Assert.AreEqual(4, source.Counters.DirectoriesKnown);

        // the repository (17 items) and names (8) hold too much: each listed alone; docs (5) and the empty folder are read at once
        var queries = fake.Queries.Skip(1).ToList();
        Assert.AreEqual(1, queries.Count(q => q == "stream items.find({\"repo\":\"generic-local\",\"type\":\"any\"}).include(\"repo\",\"path\",\"name\",\"type\",\"size\",\"created\",\"modified\",\"actual_sha1\")"), string.Join("\n", queries));
        Assert.AreEqual(1, queries.Count(q => q.StartsWith("items.find({\"repo\":\"generic-local\",\"path\":\".\",", StringComparison.Ordinal)));
        Assert.AreEqual(1, queries.Count(q => q.StartsWith("items.find({\"repo\":\"generic-local\",\"path\":\"names\",", StringComparison.Ordinal)));
        Assert.AreEqual(1, queries.Count(q => q.StartsWith("stream ", StringComparison.Ordinal) && q.Contains("\"path\":{\"$match\":\"docs/*\"}", StringComparison.Ordinal)));
        Assert.AreEqual(0, queries.Count(q => q.Contains("\"path\":\"docs\",\"type\"", StringComparison.Ordinal)), "docs was read with what is below it");
        CollectionAssert.AreEqual(new[] { "logo.png" }, await NamesAsync(source, "docs/images"));
    }

    [TestMethod]
    public async Task AWalkOverAQuietServerAsksEachRepositoryWhatChangedAndNoMore()
    {
        var fake = ArtifactorySeed.Lab();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake));
        await source.CountFilesAsync(CancellationToken.None);
        await LaterAsync(fake, source, TimeSpan.FromMinutes(30));

        var before = fake.Requests.Count;
        var asked = fake.Queries.Count;
        var counters = new SourceCounters();
        Assert.IsTrue(await source.WalkAsync(counters, full: false, CancellationToken.None));
        Assert.AreEqual(ArtifactorySeed.Files, counters.FilesKnown, "what is kept is counted");
        var queries = QueriesSince(fake, asked);
        Assert.AreEqual(3, queries.Count, string.Join("\n", queries));
        Assert.IsTrue(queries.All(IsChanges));
        Assert.AreEqual(3, fake.Requests.Count - before, "a request a repository: " + ListEverything.Since(fake.Requests, before));

        // since the time the listings were as of: the count's, five minutes behind the server's clock
        Assert.AreEqual("2026-11-01T11:55:00.000Z", Since(queries, "generic-local")[..17] + "00.000Z");
    }

    [TestMethod]
    public async Task ANewFileShowsAfterTheNextWalkWhichListsOnlyTheFoldersThatHoldIt()
    {
        var fake = ArtifactorySeed.Lab();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake));
        await source.CountFilesAsync(CancellationToken.None);
        await LaterAsync(fake, source, TimeSpan.FromMinutes(30));
        fake.Put("libs-release-local", "org/acme/app/1.2/app-1.2.jar", ArtifactorySeed.Bytes("jar 1.2"));
        CollectionAssert.AreEqual(new[] { "1.0", "1.1" }, await NamesAsync(source, "libs-release-local/org/acme/app"), "kept as it was until the walk");

        // the walk comes ten minutes after: a file put within the five before a walk is found by the next one too
        await LaterAsync(fake, source, TimeSpan.FromMinutes(10));

        var asked = fake.Queries.Count;
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "1.0", "1.1", "1.2" }, await NamesAsync(source, "libs-release-local/org/acme/app"));
        Assert.AreEqual("jar 1.2", await TextAsync(source, "libs-release-local/org/acme/app/1.2/app-1.2.jar"));

        // what changed, a repository each; then the folders on the way to the new file, each alone, and the new folder
        var queries = QueriesSince(fake, asked).Where(q => !IsChanges(q)).ToList();
        CollectionAssert.AreEquivalent(new[] { ".", "org", "org/acme", "org/acme/app" },
            queries.Where(q => !q.StartsWith("stream ", StringComparison.Ordinal)).Select(q => Regex.Match(q, @"""path"":""([^""]+)""").Groups[1].Value).ToList(), string.Join("\n", queries));
        Assert.AreEqual(1, queries.Count(q => q.StartsWith("stream ", StringComparison.Ordinal) && q.Contains("org/acme/app/1.2/*", StringComparison.Ordinal)), "the new folder, with what is below it");
        Assert.IsTrue(queries.All(q => q.Contains("\"repo\":\"libs-release-local\"", StringComparison.Ordinal)), "nothing in the other repositories");

        // the walk after: from the time of the one that found it, and nothing to list; a repository nothing changed in is
        // asked from the same time as before (each repository's time is its own: the count came to each at its own moment)
        var since = Since(fake.Queries, "libs-release-local");
        var quiet = Since(fake.Queries, "generic-local");
        await LaterAsync(fake, source, TimeSpan.FromMinutes(30));
        asked = fake.Queries.Count;
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        queries = QueriesSince(fake, asked);
        Assert.IsTrue(queries.All(IsChanges), string.Join("\n", queries));
        Assert.IsTrue(string.CompareOrdinal(Since(queries, "libs-release-local"), since) > 0, "the time moved on");
        Assert.AreEqual(quiet, Since(queries, "generic-local"), "where nothing changed it is as it was");
    }

    [TestMethod]
    public async Task AFileDeletedShowsAsGoneAfterAWalkOfEveryFolder()
    {
        var fake = ArtifactorySeed.Lab();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/generic-local"));
        await source.CountFilesAsync(CancellationToken.None);
        await LaterAsync(fake, source, TimeSpan.FromMinutes(30));
        fake.Delete("generic-local", "docs/guide.md");
        fake.Delete("generic-local", "names");

        // nothing a query for what changed tells of
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        CollectionAssert.Contains(await NamesAsync(source, "docs"), "guide.md");

        var counters = new SourceCounters();
        await source.WalkAsync(counters, full: true, CancellationToken.None);
        CollectionAssert.DoesNotContain(await NamesAsync(source, "docs"), "guide.md");
        CollectionAssert.AreEqual(new[] { "docs", "empty-folder", "README.md" }, await NamesAsync(source, ""));
        Assert.AreEqual(13 - 1 - 8, counters.FilesKnown);
        Assert.IsNull(source.Listings.Held("names"), "let go with the folder");
    }

    [TestMethod]
    public async Task AWalkAFolderCouldNotBeListedInAsksAgainFromTheSameTime()
    {
        var fake = ArtifactorySeed.Lab();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/generic-local"));
        await source.CountFilesAsync(CancellationToken.None);
        await LaterAsync(fake, source, TimeSpan.FromMinutes(30));
        fake.Put("generic-local", "docs/new.txt", ArtifactorySeed.Bytes("new\n"));
        var failing = true;
        fake.Fault = r => failing && r.Method == "POST" && Encoding.UTF8.GetString(r.Body).Contains("\"path\":\"docs\",", StringComparison.Ordinal)
            ? FakeArtifactory.Response.Error(400, "Failed to parse query")
            : null;

        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        CollectionAssert.DoesNotContain(await NamesAsync(source, "docs"), "new.txt", "its folder could not be listed");
        var first = Since(fake.Queries, "generic-local");

        // the next walk asks from the same time, so the folder that failed is among what changed again
        failing = false;
        await LaterAsync(fake, source, TimeSpan.FromMinutes(30));
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        Assert.AreEqual(first, Since(fake.Queries, "generic-local"));
        CollectionAssert.Contains(await NamesAsync(source, "docs"), "new.txt");

        await LaterAsync(fake, source, TimeSpan.FromMinutes(30));
        await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
        Assert.IsTrue(string.CompareOrdinal(Since(fake.Queries, "generic-local"), first) > 0, "it went everywhere: the time moved on");
    }

    [TestMethod]
    public async Task ARestartKeepsTheListingsAndAsksOnlyWhatChanged()
    {
        var fake = ArtifactorySeed.Lab();
        using var context = new TestSourceContext();
        await using (var first = await StartAsync(fake, ArtifactorySeed.Settings(fake), context))
        {
            await first.CountFilesAsync(CancellationToken.None);
        }

        fake.Now += TimeSpan.FromHours(3);
        fake.Put("generic-local", "docs/later.txt", ArtifactorySeed.Bytes("later\n"));
        var asked = fake.Queries.Count;
        await using var second = await StartAsync(fake, ArtifactorySeed.Settings(fake), context);
        CollectionAssert.DoesNotContain(await NamesAsync(second, "generic-local/docs"), "later.txt", "from what the last run kept");
        await second.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Complete, second.Counters.State);
        Assert.AreEqual(ArtifactorySeed.Files + 1, second.Counters.FilesKnown);
        CollectionAssert.Contains(await NamesAsync(second, "generic-local/docs"), "later.txt");

        // the start's question, what changed in each repository, and the two folders on the way to the new file
        var queries = QueriesSince(fake, asked);
        Assert.AreEqual(1 + 3 + 2, queries.Count, string.Join("\n", queries));
        Assert.AreEqual(0, queries.Count(q => q.StartsWith("stream ", StringComparison.Ordinal) && !IsChanges(q)), "no repository read again");
    }

    [TestMethod]
    public async Task WithoutTheQueryLanguageFoldersAreListedThroughTheStorageCalls()
    {
        var fake = ArtifactorySeed.Lab();
        fake.Queryable = false;
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake), context);
        Assert.IsFalse(source.Queries);
        var docs = await EntriesAsync(source, "generic-local/docs");
        CollectionAssert.AreEqual(new[] { "images", "empty.txt", "guide.md", "large.bin" }, docs.Select(e => e.Name).ToArray());
        Assert.AreEqual(ArtifactorySeed.Large.Length, docs.Single(e => e.Name == "large.bin").Length);
        Assert.AreEqual(1, fake.Count("GET /artifactory/api/storage/generic-local/docs?list&deep=0&listFolders=1"), "the folder's files in one request");
        await source.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
        Assert.AreEqual(ArtifactorySeed.Files, source.Counters.FilesKnown);
        Assert.AreEqual("Counting files: 3 of 3 repositories", CountProgress(context)[^1]);
        Assert.AreEqual(1, fake.Count("POST /artifactory/api/search/aql"), "the one question at the start, which the server would not take");

        // an edition without the file list either: each file's size from its own call
        fake.FileList = false;
        await using var plain = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/libs-release-local/org/acme/app/1.0"));
        var files = await EntriesAsync(plain, "");
        Assert.AreEqual(7, files.Single(e => e.Name == "app-1.0.jar").Length);
        Assert.AreEqual(1, fake.Count("GET /artifactory/api/storage/libs-release-local/org/acme/app/1.0/app-1.0.jar"));
    }

    [TestMethod]
    public async Task AnAnswerTheServerCutShortIsListedThroughTheStorageCalls()
    {
        var fake = ArtifactorySeed.Lab();
        fake.HardLimit = 3;
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/generic-local/names"));
        Assert.AreEqual(8, (await NamesAsync(source, "")).Count);
        Assert.AreEqual(1, fake.Count("GET /artifactory/api/storage/generic-local/names?list"));
    }

    [TestMethod]
    public Task AServerThatRefusesQueriesBeyondThoseItRunsAtOnceIsSentFewerAtATime() => ARefusingServerIsSentFewerAtATimeAsync(againLate: false);

    /// <summary>
    /// The same where a query refused and sent again reaches the server late, after the one that was in its way has gone
    /// (once on macOS in CI, 22-Nov-2026): it is answered, the count ends with the source still sending two at a time, and
    /// the refusal that halves that comes in the walk that follows. The test above said no walk after the count is refused
    /// anything, which the source does not promise: it halves once for each round of refusals, and loses nothing.
    /// </summary>
    [TestMethod]
    public Task AQuerySentAgainThatReachesTheServerLateLeavesTheNextRefusalToAWalk() => ARefusingServerIsSentFewerAtATimeAsync(againLate: true);

    private static readonly Regex SendsFewer = new(@"sends it (\d+) at a time from now on", RegexOptions.CultureInvariant);

    /// <summary>
    /// What the source promises of a server that runs one query at once and refuses the rest (429), in whatever order the
    /// refusals and the queries sent again reach it: the count and the walks are whole and right; every query refused is
    /// sent again and answered; each time it sends fewer at once, that is fewer than before; and it comes down to what
    /// the server runs, from when a walk is refused nothing and never has more there at once. How many it sends at once
    /// is read where it says so, in its log.
    /// </summary>
    private static async Task ARefusingServerIsSentFewerAtATimeAsync(bool againLate)
    {
        var fake = ArtifactorySeed.Lab();
        fake.QueriesAtOnce = 1;
        fake.QueryTakes = TimeSpan.FromMilliseconds(150);
        fake.QueryPending = TimeSpan.FromMilliseconds(20);
        if (againLate)
        {
            OneAtATimeAfterARefusal(fake);
        }

        var log = new LineLogger();
        using var context = new TestSourceContext(logger: log);
        var settings = ArtifactorySeed.Settings(fake);
        await using var source = await StartAsync(fake, settings, context);

        // how many queries the source sends at once: what the settings come to, then what it said each time it sent fewer
        List<int> AtOnce() => [settings.Requests, .. log.Lines.Select(l => SendsFewer.Match(l)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];

        await source.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
        Assert.AreEqual(ArtifactorySeed.Files, source.Counters.FilesKnown);
        Assert.IsTrue(fake.QueriesRefused > 0, "one too many was refused, and asked again");
        Assert.IsTrue(AtOnce().Count > 1, "refused, the source sends fewer at once, and says how many in its log, where this reads it:\n" + log);
        if (againLate)
        {
            fake.Fault = null;
            CollectionAssert.AreEqual(new[] { settings.Requests, settings.Requests / 2 }, AtOnce(), "the query sent again met no other at the server, so the count ended a refusal short: two at a time, one more than the server runs");
        }

        // walks of every folder until the source is down to what the server runs: it is by the count's end as a rule, and
        // by a walk's where the count ended above it (a walk is refused as soon as two of its queries are there at once)
        for (var above = 0; ; above++)
        {
            var atOnce = AtOnce()[^1];
            var refused = fake.QueriesRefused;
            fake.MostQueriesAtOnce = 0;
            var walked = new SourceCounters();
            Assert.IsTrue(await source.WalkAsync(walked, full: true, CancellationToken.None), "the walk went into every folder");
            Assert.AreEqual(ArtifactorySeed.Files, walked.FilesKnown, "and counted every file");
            if (atOnce <= fake.QueriesAtOnce)
            {
                Assert.AreEqual(refused, fake.QueriesRefused, "from then on a walk of every folder is refused nothing");
                Assert.IsTrue(fake.MostQueriesAtOnce <= fake.QueriesAtOnce, $"nor has it more queries at the server at once than the server runs: {fake.MostQueriesAtOnce}");
                break;
            }

            Assert.IsTrue(above < 3, $"after {above + 1} walks the source still sends {AtOnce()[^1]} queries at once to a server that runs {fake.QueriesAtOnce}");
        }

        var answers = fake.QueryAnswers.ToList();
        for (var i = 0; i < answers.Count; i++)
        {
            if (answers[i].Refused)
            {
                Assert.IsTrue(answers.Skip(i + 1).Any(a => a.Query == answers[i].Query && !a.Refused), "a query that was refused was not sent again and answered: " + answers[i].Query);
            }
        }

        var said = AtOnce();
        for (var i = 1; i < said.Count; i++)
        {
            Assert.IsTrue(said[i] < said[i - 1], "each time fewer than before, never more again: " + string.Join(", ", said));
        }

        Assert.IsTrue(said.Count - 1 <= fake.QueriesRefused, $"and once for a round of refusals, not without one: {said.Count - 1} times for {fake.QueriesRefused} refusals");
    }

    /// <summary>
    /// From the first query refused on, each query waits at the server's door until the server is done with every one
    /// before it: none meets another there, so none is refused, whenever the source sends them. A query refused and sent
    /// again so reaches the server after the one that was in its way has gone, as one held up on its way would.
    /// </summary>
    private static void OneAtATimeAfterARefusal(FakeArtifactory fake)
    {
        var door = new object();
        var through = 0;
        fake.Fault = request =>
        {
            if (request.Method == "POST" && request.Url.AbsolutePath.EndsWith("/api/search/aql", StringComparison.Ordinal))
            {
                lock (door)
                {
                    if (fake.QueriesRefused > 0)
                    {
                        SpinWait.SpinUntil(() => fake.QueryAnswers.Count >= through, TimeSpan.FromSeconds(30));
                    }

                    through++;
                }
            }

            return null;
        };
    }

    [TestMethod]
    public async Task EveryRequestOfTheServerIsARowThatEnds()
    {
        var fake = ArtifactorySeed.Lab();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake), context);
        source.TreeMost = 5;
        await source.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual("# Guide\n\nStep one.\n", await TextAsync(source, "generic-local/docs/guide.md"));
        await UpstreamRequestRows.AllEndAsync(context);
        Assert.IsTrue(fake.Requests.Count > 0);
        Assert.AreEqual(fake.Requests.Count, context.UpstreamRequests.Requests.Count, "every one the server saw, and no more");
        Assert.IsTrue(context.UpstreamRequests.Requests.All(r => r.Answer is not null));
    }

    [TestMethod]
    public async Task AThrottledAnswerIsWaitedForAndTheRequestMadeAgain()
    {
        var fake = ArtifactorySeed.Lab();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/generic-local"));
        fake.ThrottleNext = 1;
        CollectionAssert.Contains(await NamesAsync(source, ""), "README.md");
        Assert.IsTrue(fake.Count("POST /artifactory/api/search/aql") >= 3, "the refused request made again: " + string.Join("; ", fake.Requests));
    }

    [TestMethod]
    public async Task AQueryWhoseAnswerBreaksOffPartWayIsAskedAgain()
    {
        // The answer to a query begins, and the connection is lost part way through its body. That was the query's
        // failure at once, though a dropped connection is tried again and the class said so: a walk then read the
        // repository a folder at a time, or went without what had changed in it (the review of 21-Nov-2026, N20).
        var fake = ArtifactorySeed.Lab();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake));
        const string query = "items.find({\"repo\":\"generic-local\",\"type\":\"any\"}).include(\"repo\",\"path\",\"name\",\"type\")";
        var whole = await source.Api.AqlAsync(query, int.MaxValue, "everything in generic-local", CancellationToken.None);
        Assert.IsTrue(whole.Items.Count > 10, whole.Items.Count.ToString(CultureInfo.InvariantCulture));

        var asked = fake.Queries.Count;
        fake.QueryAnswersBreak = 1;
        var again = await source.Api.AqlAsync(query, int.MaxValue, "everything in generic-local", CancellationToken.None);
        Assert.AreEqual(whole.Items.Count, again.Items.Count, "the answer whole, from the second asking");
        Assert.AreEqual(asked + 2, fake.Queries.Count, "asked again, once");

        // one that breaks off every time is the query's failure, after as many askings as a request that gets no answer has
        asked = fake.Queries.Count;
        fake.QueryAnswersBreak = int.MaxValue;
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(() => source.Api.AqlAsync(query, int.MaxValue, "everything in generic-local", CancellationToken.None));
        Assert.AreEqual(VfsError.Unavailable, ex.Error);
        StringAssert.Contains(ex.Message, "broke off its answer to everything in generic-local");
        Assert.AreEqual(asked + 3, fake.Queries.Count, "three times in all");
    }

    [TestMethod]
    public async Task AFolderWhoseListingFailedStaysSoUntilTheNextWalk()
    {
        var fake = ArtifactorySeed.Lab();
        await using var source = await StartAsync(fake, ArtifactorySeed.Settings(fake, "/generic-local"));
        var failing = true;
        var asked = 0;
        fake.Fault = r =>
        {
            if (failing && r.Method == "POST" && Encoding.UTF8.GetString(r.Body).Contains("\"path\":\"docs\",", StringComparison.Ordinal))
            {
                asked++;
                return FakeArtifactory.Response.Error(400, "Failed to parse query");
            }

            return null;
        };
        var first = await Assert.ThrowsExactlyAsync<VfsException>(async () => await NamesAsync(source, "docs"));
        Assert.AreEqual(VfsError.NotSupported, first.Error);
        StringAssert.Contains(first.Message, "Failed to parse query");
        Assert.AreEqual(1, asked);
        failing = false;
        var again = await Assert.ThrowsExactlyAsync<VfsException>(async () => await NamesAsync(source, "docs"));
        Assert.AreEqual(first.Message, again.Message, "answered at once as it failed");
        Assert.AreEqual(1, asked, "nothing asked");

        await source.WalkAsync(new SourceCounters(), full: true, CancellationToken.None);
        CollectionAssert.Contains(await NamesAsync(source, "docs"), "guide.md");
    }

    // --- the answer's reader --------------------------------------------------------------------------------------------

    [TestMethod]
    public void AQuerysAnswerIsReadTheSameHoweverItArrives()
    {
        const string answer = """
            {
            "results" : [ {
              "repo" : "generic-local", "path" : ".", "name" : ".", "type" : "folder", "size" : 0,
              "created" : "2026-10-20T09:30:00.000Z", "modified" : "2026-10-20T09:30:00.000Z"
            },{
              "repo" : "generic-local", "path" : ".", "name" : "docs", "type" : "folder", "size" : 0,
              "created" : "2026-10-20T11:30:00.123+02:00", "modified" : "2026-10-20T09:30:00.000Z", "extra" : { "deep" : [ 1, { "x" : "}" } ] }
            },{
              "repo" : "generic-local", "path" : "docs/a \"b\" \\ c/é", "name" : "ünï☃.txt", "type" : "file", "size" : 9007199254740993,
              "created" : "2026-10-20T09:30:00.000Z", "modified" : "2026-10-21T09:30:00.000Z", "actual_sha1" : "3F388E71281B7C9845260F16B2F079D6B75DC197"
            },{
              "repo" : "generic-local", "path" : "docs/a \"b\" \\ c/é", "name" : "text-size.bin", "type" : "file", "size" : "12"
            } ],
            "range" : { "start_pos" : 0, "end_pos" : 4, "total" : 4, "notification" : "AQL query reached the search hard limit, results are trimmed." }
            }
            """;
        var bytes = Encoding.UTF8.GetBytes(answer);
        foreach (var chunk in new[] { 1, 2, 3, 7, 31, 64, bytes.Length })
        {
            var reader = new AqlReader();
            var items = new List<ArtItem>();
            var buffer = new List<byte>();
            for (var at = 0; at < bytes.Length || !reader.Done; at += chunk)
            {
                var final = at >= bytes.Length;
                buffer.AddRange(bytes.AsSpan(Math.Min(at, bytes.Length), Math.Max(0, Math.Min(chunk, bytes.Length - at))));
                var taken = reader.Read(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer), final || at + chunk >= bytes.Length, items);
                buffer.RemoveRange(0, taken);
                Assert.IsFalse(final && !reader.Done, $"not read to its end in chunks of {chunk}");
            }

            Assert.AreEqual(3, items.Count, $"chunks of {chunk}: the root is nobody's child");
            Assert.AreEqual(new ArtItem("", "docs", true, 0, new DateTimeOffset(2026, 10, 20, 9, 30, 0, 123, TimeSpan.Zero), ArtifactorySeed.Day, null), items[0]);
            Assert.AreEqual("docs/a \"b\" \\ c/é", items[1].Folder);
            Assert.AreEqual("ünï☃.txt", items[1].Name);
            Assert.AreEqual(9007199254740993, items[1].Size);
            Assert.AreEqual("3f388e71281b7c9845260f16b2f079d6b75dc197", items[1].Sha1);
            Assert.AreEqual(ArtifactorySeed.Day.AddDays(1), items[1].Modified);
            Assert.AreSame(items[1].Folder, items[2].Folder, "a folder's path is one string for all in it");
            Assert.AreEqual(12, items[2].Size);
            Assert.IsNull(items[2].Sha1);
            Assert.IsTrue(reader.Trimmed);
        }
    }

    // --- the settings and the Test button -------------------------------------------------------------------------------

    [TestMethod]
    public void ThePluginNamesValidatesAndShowsTheSignInsFields()
    {
        var plugin = new ArtifactoryPlugin();
        var settings = new ArtifactorySettings { Address = "not an address", AccessToken = "t", RepositoriesAtATime = 0, RequestsPerRepository = 65, RefreshSeconds = -1 };
        CollectionAssert.AreEquivalent(new[] { "Address", "RepositoriesAtATime", "RequestsPerRepository", "RefreshSeconds" }, plugin.Validate(settings).Select(p => p.Property).ToList());
        Assert.IsFalse(plugin.Validate(new ArtifactorySettings { Address = "https://artifactory.example.com/artifactory/libs-release-local", AccessToken = "t" }).Any(m => m.IsError));
        CollectionAssert.Contains(plugin.Validate(new ArtifactorySettings { Address = "https://artifactory.example.com/artifactory" }).Select(p => p.Property).ToList(), "AccessToken");
        CollectionAssert.Contains(plugin.Validate(new ArtifactorySettings { Address = "https://a.example.com", SignInMethod = ArtifactorySettings.PasswordChoice }).Select(p => p.Property).ToList(), "Username");
        Assert.IsFalse(plugin.Validate(new ArtifactorySettings { Address = "https://a.example.com", SignInMethod = ArtifactorySettings.AnonymousChoice }).Any(m => m.IsError));
        CollectionAssert.Contains(plugin.Validate(new ArtifactorySettings { Address = "https://a.example.com", AccessToken = "t", LocalRepositories = false, RemoteRepositories = false }).Select(p => p.Property).ToList(), "LocalRepositories");

        Assert.AreEqual("libs-release-local (artifactory.example.com)", plugin.SuggestName(new ArtifactorySettings { Address = "https://artifactory.example.com/ui/repos/tree/General/libs-release-local" }));
        Assert.AreEqual("acme (artifactory.example.com)", plugin.SuggestName(new ArtifactorySettings { Address = "https://artifactory.example.com/artifactory/libs-release-local/org/acme/" }));
        Assert.AreEqual("index (artifactory.example.com)", plugin.SuggestName(new ArtifactorySettings { Address = "https://artifactory.example.com/artifactory/libs-release-local/.index/" }), "a source's name begins with no dot, as a folder's may");
        Assert.AreEqual("example.jfrog.io", plugin.SuggestName(new ArtifactorySettings { Address = "https://example.jfrog.io/" }));
        Assert.IsNull(plugin.SuggestName(new ArtifactorySettings { Address = "nothing" }));

        var s = new ArtifactorySettings { Address = " https://artifactory.example.com/artifactory/webapp/#/artifacts/browse/tree/General/libs-release-local/org ", SignInMethod = ArtifactorySettings.PasswordChoice };
        plugin.NormaliseSettings(s);
        Assert.AreEqual("https://artifactory.example.com/artifactory/libs-release-local/org", s.Address);
        Assert.IsTrue(plugin.IsVisible(s, nameof(ArtifactorySettings.Username)));
        Assert.IsFalse(plugin.IsVisible(s, nameof(ArtifactorySettings.AccessToken)));
        Assert.IsFalse(plugin.IsVisible(s, nameof(ArtifactorySettings.BrowserPath)));
        Assert.IsFalse(plugin.IsVisible(s, nameof(ArtifactorySettings.VirtualRepositories)), "one repository: no kinds to choose");
        Assert.IsTrue(plugin.IsVisible(new ArtifactorySettings { Address = "https://artifactory.example.com/artifactory" }, nameof(ArtifactorySettings.VirtualRepositories)));
        Assert.AreEqual("artifactory", plugin.TypeId);
        using var icon = plugin.OpenIcon();
        Assert.IsNotNull(icon);
    }

    [TestMethod]
    public async Task TheTestSaysWhoAndWhat()
    {
        var fake = ArtifactorySeed.Lab();
        var result = await ArtifactoryPlugin.TestAsync(ArtifactorySeed.Settings(fake), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "Signed in to artifactory.fake.test (Artifactory 7.161.26) as tester with an access token.");
        StringAssert.Contains(result.Message, "The source shows 3 of the 4 repositories you can see (local, remote).");

        result = await ArtifactoryPlugin.TestAsync(ArtifactorySeed.Settings(fake, "/npm-remote-cache"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "the repository npm-remote (remote, Npm)");

        result = await ArtifactoryPlugin.TestAsync(ArtifactorySeed.Settings(fake, "/libs-release-local/org/acme"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "the folder org/acme of the repository libs-release-local (local, Maven)");

        result = await ArtifactoryPlugin.TestAsync(ArtifactorySeed.Settings(fake, "/hidden-local"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "no repository hidden-local");

        result = await ArtifactoryPlugin.TestAsync(ArtifactorySeed.Settings(fake, token: "wrong"), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "refused the access token");
    }
}

/// <summary>The SDK's contract for a read-only source, against the fake server.</summary>
[TestClass]
public class ArtifactoryContractTests : SourceContractTests
{
    protected override async Task<SourceContractSetup> SetUpAsync()
    {
        var fake = ArtifactorySeed.Lab();
        var source = new ArtifactorySource(ArtifactorySeed.Settings(fake, "/generic-local"), fake.StandIn());
        await source.InitialiseAsync(new TestSourceContext(), CancellationToken.None);
        return new SourceContractSetup(source, "docs", "docs/large.bin", ArtifactorySeed.Large);
    }
}
