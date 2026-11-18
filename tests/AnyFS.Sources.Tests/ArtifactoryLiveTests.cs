using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Artifactory;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The Artifactory source against a real Artifactory (spec 7.13): the lab's (provisioning/artifactory/README.md), or any
/// server with the same seed. Skipped unless <c>ANYFS_TEST_ARTIFACTORY_URL</c> (<c>http://192.168.0.77:8082/artifactory</c>)
/// and <c>ANYFS_TEST_ARTIFACTORY_TOKEN</c> (the tester's access token, which the setup writes to
/// <c>provisioning/artifactory/secrets/tester.token</c>) are set. <c>ANYFS_TEST_ARTIFACTORY_REFERENCE_TOKEN</c> adds the
/// reference token's sign-in, and <c>ANYFS_TEST_ARTIFACTORY_USER</c> with <c>_PASSWORD</c> (the tester, <c>art_user</c>, and
/// its password in settings.yml) the password's. The web session's test needs the address with the user name and password
/// alone (any account, and no seed): it signs in through JFrog's client sign-in, approving the session at the page as the
/// page's own script does.
/// </summary>
[TestClass]
public class ArtifactoryLiveTests
{
    private const string Generic = "anyfs-generic-local";

    private static (string Url, string Token) Server()
    {
        var url = Environment.GetEnvironmentVariable("ANYFS_TEST_ARTIFACTORY_URL");
        var token = Environment.GetEnvironmentVariable("ANYFS_TEST_ARTIFACTORY_TOKEN");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
        {
            Assert.Inconclusive("set ANYFS_TEST_ARTIFACTORY_URL and ANYFS_TEST_ARTIFACTORY_TOKEN to run against an Artifactory with the lab's seed");
        }

        return (url.TrimEnd('/'), token.Trim());
    }

    private static async Task<ArtifactorySource> StartAsync(ArtifactorySettings settings, TestSourceContext context)
    {
        var source = new ArtifactorySource(settings);
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

    private static async Task<byte[]> ReadAsync(ISource source, string path, long offset = 0, int? count = null)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[count ?? (int)(file.Length - offset)];
        var read = await file.ReadFullyAsync(offset, buffer, CancellationToken.None);
        return buffer[..read];
    }

    /// <summary>What the seed's <c>yes 'line' | head -c n</c> writes: the line and a newline over and over, cut at so many bytes.</summary>
    private static byte[] Repeated(string line, long offset, int count)
    {
        var unit = Encoding.ASCII.GetBytes(line + "\n");
        var bytes = new byte[count];
        for (var i = 0; i < count; i++)
        {
            bytes[i] = unit[(offset + i) % unit.Length];
        }

        return bytes;
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task TheTesterSeesTheLabsRepositoriesAndTheirFilesAsTheSeedPutThem()
    {
        var (url, token) = Server();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(new ArtifactorySettings { Address = url, AccessToken = token }, context);
        Assert.AreEqual("art_user", source.Session.User, "the token names its account");
        Assert.IsTrue(source.Queries, "the lab's edition answers queries");
        var repositories = (await ListAsync(source, "")).Select(e => e.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "anyfs-big-local", "anyfs-empty-local", Generic, "anyfs-public-local", "anyfs-remote" }, repositories);
        CollectionAssert.DoesNotContain(repositories, "anyfs-hidden-local", "kept from the tester");
        CollectionAssert.DoesNotContain(repositories, "anyfs-virtual", "virtual repositories are off as the settings come");

        var top = await ListAsync(source, Generic);
        CollectionAssert.IsSubsetOf(new[] { "big", "deep", "docs", "empty-folder", "names", "org", "README.md" }, top.Select(e => e.Name).ToList());
        Assert.AreEqual(0, (await ListAsync(source, Generic + "/empty-folder")).Count);
        var docs = await ListAsync(source, Generic + "/docs");
        CollectionAssert.AreEqual(new[] { "images", "empty.txt", "guide.md", "large.bin" }, docs.Select(e => e.Name).ToArray());
        var guide = docs.Single(e => e.Name == "guide.md");
        Assert.AreEqual(19, guide.Length);
        Assert.AreEqual(Convert.ToHexStringLower(SHA1.HashData("# Guide\n\nStep one.\n"u8)), guide.ETag);
        Assert.IsTrue(guide.Modified > DateTimeOffset.UtcNow.AddYears(-1) && guide.Modified < DateTimeOffset.UtcNow.AddHours(1), $"{guide.Modified}");
        Assert.AreEqual("# Guide\n\nStep one.\n", Encoding.UTF8.GetString(await ReadAsync(source, Generic + "/docs/guide.md")));
        Assert.AreEqual(0, (await ReadAsync(source, Generic + "/docs/empty.txt")).Length);

        // a file fetched whole, by its checksum
        var large = docs.Single(e => e.Name == "large.bin");
        Assert.AreEqual(1_048_653, large.Length);
        var bytes = await ReadAsync(source, Generic + "/docs/large.bin");
        Assert.AreEqual(large.ETag, Convert.ToHexStringLower(SHA1.HashData(bytes)));
        CollectionAssert.AreEqual(Repeated("AnyFS Artifactory lab file 0123456789abcdef", 0, bytes.Length), bytes);
        Assert.AreEqual("a name\n", Encoding.UTF8.GetString(await ReadAsync(source, Generic + "/deep/a/b/c/d/e/f/g/file.txt")));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ALongFileIsReadInRangesFromWhereItIsAskedFor()
    {
        var (url, token) = Server();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(new ArtifactorySettings { Address = url + "/" + Generic + "/big", AccessToken = token }, context);
        const string line = "AnyFS Artifactory lab large file 0123456789abcdef";
        const long length = 52_428_800;
        Assert.AreEqual(length, (await source.StatAsync("large-50m.bin", CancellationToken.None))!.Length);
        CollectionAssert.AreEqual(Repeated(line, 40L << 20, 1 << 20), await ReadAsync(source, "large-50m.bin", 40L << 20, 1 << 20));
        CollectionAssert.AreEqual(Repeated(line, length - 4096, 4096), await ReadAsync(source, "large-50m.bin", length - 4096, 4096));
        CollectionAssert.AreEqual(Repeated(line, 0, 65536), await ReadAsync(source, "large-50m.bin", 0, 65536));
        var ranged = UpstreamRequestRows.To(context, "large-50m.bin");
        Assert.IsTrue(ranged.Count is >= 2 and <= 4, $"a request where each read began, not the whole file: {ranged.Count}");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task NamesAnAddressMustEscapeAndNamesInOtherScriptsOpenAndTwinsInCaseAreToldApart()
    {
        var (url, token) = Server();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(new ArtifactorySettings { Address = url + "/" + Generic, AccessToken = token }, context);
        var names = (await ListAsync(source, "names")).Select(e => e.Name).ToList();
        foreach (var name in new[] { "with space.txt", "plus+sign.txt", "hash#tag.txt", "semi;colon.txt", "percent%done.txt", "amp&ersand=.txt", "ünïcödé ☃.txt", "日本語.txt", "it's.txt", "[brackets].txt", "{braces}.txt", "comma,name.txt", "~tilde.txt" })
        {
            CollectionAssert.Contains(names, name);
            Assert.AreEqual("a name\n", Encoding.UTF8.GetString(await ReadAsync(source, "names/" + name)), name);
        }

        // Artifactory keeps Twin.txt and twin.txt apart, and the folders names and Names; AnyFS's paths take either case
        CollectionAssert.Contains(names, "Twin.txt");
        CollectionAssert.Contains(names, "twin (2).txt");
        var top = (await ListAsync(source, "")).Select(e => e.Name).ToList();
        CollectionAssert.Contains(top, "Names");
        CollectionAssert.Contains(top, "names (2)");
        Assert.AreEqual("in-the-other-case.txt", (await ListAsync(source, "Names")).Single().Name);
        Assert.IsTrue((await ListAsync(source, "names (2)")).Count > 10);
        Assert.AreEqual(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "no two alike in either case");
    }

    [TestMethod]
    [Timeout(900_000)]
    public async Task TheCountReadsEveryRepositoryAndTheWalkAfterAsksEachOnlyWhatChanged()
    {
        var (url, token) = Server();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(new ArtifactorySettings { Address = url, AccessToken = token }, context);
        await source.CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
        Assert.IsTrue(source.Counters.FilesKnown > 20_000, $"{source.Counters.FilesKnown:N0} files");
        await UpstreamRequestRows.AllEndAsync(context);
        var counted = context.UpstreamRequests.Requests.Count;
        var repositories = (await ListAsync(source, "")).Count;
        Assert.IsTrue(counted < repositories * 12, $"{counted} requests for {source.Counters.FilesKnown:N0} files in {repositories} repositories: each read in a few");

        // a pass over everything asks nothing
        var folders = await ListEverything.OfAsync(source);
        Assert.IsTrue(folders > 2_000, folders.ToString());
        Assert.AreEqual(counted, context.UpstreamRequests.Requests.Count, "asked for after the walk");

        // the walk after: what changed in each repository, and nothing has
        var counters = new SourceCounters();
        Assert.IsTrue(await source.WalkAsync(counters, full: false, CancellationToken.None));
        Assert.AreEqual(source.Counters.FilesKnown, counters.FilesKnown);
        var asked = context.UpstreamRequests.Requests.Count - counted;
        Assert.IsTrue(asked <= repositories + 1, $"{asked} requests for a walk over {repositories} repositories nothing happened in");
    }

    [TestMethod]
    [Timeout(300_000)]
    public async Task AFilePutShowsAfterTheNextWalkAndOneDeletedAfterAWalkOfEveryFolder()
    {
        var (url, token) = Server();
        const string scratch = "anyfs-scratch-local";
        var folder = "live-" + Guid.NewGuid().ToString("N")[..8];
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        async Task PutAsync(string path, string text)
        {
            using var answer = await http.PutAsync($"{url}/{scratch}/{folder}/{path}", new StringContent(text));
            Assert.IsTrue(answer.IsSuccessStatusCode, $"putting {path}: {(int)answer.StatusCode}");
        }

        await PutAsync("first/a.txt", "a\n");
        try
        {
            using var context = new TestSourceContext();
            await using var source = await StartAsync(new ArtifactorySettings { Address = $"{url}/{scratch}", AccessToken = token }, context);
            await source.CountFilesAsync(CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "a.txt" }, (await ListAsync(source, folder + "/first")).Select(e => e.Name).ToArray());

            // a file beside the first, another in a new folder, and the first written again
            await PutAsync("first/b.txt", "bb\n");
            await PutAsync("second/deeper/c.txt", "ccc\n");
            await PutAsync("first/a.txt", "a, again\n");
            Assert.AreEqual(1, (await ListAsync(source, folder + "/first")).Count, "kept as it was until the walk");
            var before = context.UpstreamRequests.Requests.Count;
            await source.WalkAsync(new SourceCounters(), full: false, CancellationToken.None);
            var first = await ListAsync(source, folder + "/first");
            CollectionAssert.AreEqual(new[] { "a.txt", "b.txt" }, first.Select(e => e.Name).ToArray());
            Assert.AreEqual(9, first[0].Length, "the file written again has its new size");
            Assert.AreEqual("ccc\n", Encoding.UTF8.GetString(await ReadAsync(source, folder + "/second/deeper/c.txt")));
            var asked = context.UpstreamRequests.Requests.Count - before - 1;
            Assert.IsTrue(asked <= 6, $"{asked} requests: what changed, the folders on the way to it, and the new folder");

            // a file deleted is nothing a query for what changed tells of: gone after a walk of every folder
            using (var deleted = await http.DeleteAsync($"{url}/{scratch}/{folder}/first/b.txt"))
            {
                Assert.IsTrue(deleted.IsSuccessStatusCode, $"deleting: {(int)deleted.StatusCode}");
            }

            await source.WalkAsync(new SourceCounters(), full: true, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "a.txt" }, (await ListAsync(source, folder + "/first")).Select(e => e.Name).ToArray());
        }
        finally
        {
            using var gone = await http.DeleteAsync($"{url}/{scratch}/{folder}");
        }
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ARemoteRepositoryShowsItsCacheAndAVirtualOneItsMembersAsOne()
    {
        var (url, token) = Server();
        using var context = new TestSourceContext();
        await using var source = await StartAsync(new ArtifactorySettings { Address = url, AccessToken = token, VirtualRepositories = true }, context);
        CollectionAssert.AreEqual(new[] { "docs", "org", "README.md" }, (await ListAsync(source, "anyfs-remote")).Select(e => e.Name).ToArray(), "what was fetched through it");
        Assert.AreEqual("# Docs\n\nRead me.\n", Encoding.UTF8.GetString(await ReadAsync(source, "anyfs-remote/README.md")));
        Assert.IsTrue(UpstreamRequestRows.To(context, "/anyfs-remote-cache/README.md").Count == 1, "read from the cache");

        var together = (await ListAsync(source, "anyfs-virtual")).Select(e => e.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "docs", "org", "README.md", "names" }, together);
        Assert.AreEqual(together.Count, together.Distinct(StringComparer.Ordinal).Count(), "a folder the members share shows once");
        Assert.AreEqual(19, (await source.StatAsync("anyfs-virtual/docs/guide.md", CancellationToken.None))!.Length);
        Assert.AreEqual("# Guide\n\nStep one.\n", Encoding.UTF8.GetString(await ReadAsync(source, "anyfs-virtual/docs/guide.md")));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task TheOtherSignInsReadTheSame()
    {
        var (url, _) = Server();
        var reference = Environment.GetEnvironmentVariable("ANYFS_TEST_ARTIFACTORY_REFERENCE_TOKEN");
        var user = Environment.GetEnvironmentVariable("ANYFS_TEST_ARTIFACTORY_USER");
        var password = Environment.GetEnvironmentVariable("ANYFS_TEST_ARTIFACTORY_PASSWORD");
        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            Assert.Inconclusive("set ANYFS_TEST_ARTIFACTORY_REFERENCE_TOKEN, ANYFS_TEST_ARTIFACTORY_USER and ANYFS_TEST_ARTIFACTORY_PASSWORD for the other sign-ins");
        }

        using var context = new TestSourceContext();
        await using (var byReference = await StartAsync(new ArtifactorySettings { Address = url + "/" + Generic, AccessToken = reference.Trim() }, context))
        {
            CollectionAssert.Contains((await ListAsync(byReference, "docs")).Select(e => e.Name).ToList(), "guide.md");
        }

        using var second = new TestSourceContext();
        await using (var byPassword = await StartAsync(new ArtifactorySettings { Address = url + "/" + Generic, SignInMethod = ArtifactorySettings.PasswordChoice, Username = user, Password = password }, second))
        {
            Assert.AreEqual(user, byPassword.Session.User);
            Assert.AreEqual("# Guide\n\nStep one.\n", Encoding.UTF8.GetString(await ReadAsync(byPassword, "docs/guide.md")));
        }

        // nobody: what anyone may read, through the storage calls
        using var third = new TestSourceContext();
        await using var anyone = await StartAsync(new ArtifactorySettings { Address = url, SignInMethod = ArtifactorySettings.AnonymousChoice }, third);
        Assert.IsFalse(anyone.Queries);
        CollectionAssert.AreEqual(new[] { "anyfs-public-local" }, (await ListAsync(anyone, "")).Select(e => e.Name).ToArray());
        var open = await ListAsync(anyone, "anyfs-public-local");
        CollectionAssert.AreEqual(new[] { "pub", "public.txt" }, open.Select(e => e.Name).ToArray());
        Assert.AreEqual(7, open.Single(e => e.Name == "public.txt").Length);
        Assert.AreEqual("# Docs\n\nRead me.\n", Encoding.UTF8.GetString(await ReadAsync(anyone, "anyfs-public-local/pub/README.md")));
    }

    /// <summary>
    /// What the person does at the platform's sign-in page for a client's session, over HTTP as the page's own script does
    /// it: signs in with a user name and password (the page's own sign-in), then, the code given, approves the session
    /// with the page's own call, which the pages' service hands on to the access service with the page's session.
    /// </summary>
    private static Func<bool, Uri, CancellationToken, Task> AtThePage(string user, string password) => async (interactive, page, ct) =>
    {
        Assert.IsTrue(interactive, "the page asks the person for a code: never without a window");
        var session = page.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Single(p => p[0] == "jfClientSession")[1];
        var platform = new Uri(page, "../");
        using var handler = new SocketsHttpHandler { CookieContainer = new System.Net.CookieContainer(), UseCookies = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(4) };
        http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        using (var signedIn = await http.PostAsync(new Uri(platform, "ui/api/v1/ui/auth/login"), JsonOf(new System.Text.Json.Nodes.JsonObject { ["user"] = user, ["password"] = password, ["type"] = "login" }), ct))
        {
            Assert.IsTrue(signedIn.IsSuccessStatusCode, $"the page's sign-in: {(int)signedIn.StatusCode}");
        }

        using var approved = await http.PostAsync(new Uri(platform, "ui/api/v1/access/api/v2/authentication/jfrog_client_login/response"), JsonOf(new System.Text.Json.Nodes.JsonObject { ["session"] = session }), ct);
        Assert.IsTrue(approved.IsSuccessStatusCode, $"the page's approval of the session: {(int)approved.StatusCode} {await approved.Content.ReadAsStringAsync(ct)}");
    };

    private static StringContent JsonOf(System.Text.Json.Nodes.JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    [TestMethod]
    [Timeout(600_000)]
    public async Task AWebSessionSignsInThroughJFrogsClientSignInAndItsTokenRenewsAndSignsInTheNextStart()
    {
        var url = Environment.GetEnvironmentVariable("ANYFS_TEST_ARTIFACTORY_URL");
        var user = Environment.GetEnvironmentVariable("ANYFS_TEST_ARTIFACTORY_USER");
        var password = Environment.GetEnvironmentVariable("ANYFS_TEST_ARTIFACTORY_PASSWORD");
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            Assert.Inconclusive("set ANYFS_TEST_ARTIFACTORY_URL, ANYFS_TEST_ARTIFACTORY_USER and ANYFS_TEST_ARTIFACTORY_PASSWORD for the web session");
        }

        static HttpMessageHandler Network() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All };
        var settings = new ArtifactorySettings { Address = url.TrimEnd('/'), SignInMethod = ArtifactorySettings.WebSessionChoice };
        using var context = new TestSourceContext();
        await using (var source = new ArtifactorySource(settings, new ArtifactoryStandIn(Network, AtThePage(user, password), UseKept: false)))
        {
            await source.InitialiseAsync(context, CancellationToken.None);
            Assert.AreEqual(user, source.Session.User, "the token names its account");
            Assert.AreEqual("with the browser", source.Session.How);
            var repositories = await ListAsync(source, "");
            Assert.IsTrue(repositories.Count > 0, "the server's root, read with the token the sign-in gave");

            // a renewal, as one near the token's end makes it: the refresh token, once, and the next token read with
            var kept = context.Secrets.Get(ArtifactorySession.TokenSecret);
            await source.Session.RenewAsync(source.Session.Generation, CancellationToken.None);
            Assert.AreNotEqual(kept, context.Secrets.Get(ArtifactorySession.TokenSecret), "the renewal kept in place of the first");
            await ListAsync(source, repositories[0].Name);
        }

        // the next start: the kept sign-in, with nobody at the browser
        await using var again = new ArtifactorySource(settings, new ArtifactoryStandIn(Network, (_, _, _) => throw new InvalidOperationException("no browser should be needed")));
        await again.InitialiseAsync(context, CancellationToken.None);
        Assert.AreEqual("with the sign-in kept from before", again.Session.How);
        Assert.AreEqual(user, again.Session.User);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task TheTestButtonSaysWhoAndWhat()
    {
        var (url, token) = Server();
        var result = await new ArtifactoryPlugin().TestAsync(new ArtifactorySettings { Address = url.Replace("/artifactory", "/ui/repos/tree/General/" + Generic + "/docs", StringComparison.Ordinal), AccessToken = token }, new Progress<string>(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "as art_user with an access token");
        StringAssert.Contains(result.Message, "the folder docs of the repository " + Generic);
    }
}
