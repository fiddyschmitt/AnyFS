using System.Diagnostics;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The fake SharePoint itself, and what only it can show: what the lab and the sandbox do not do on demand (throttling,
/// a lapsed session or token, a server that ignores ranges, an answer that is not SharePoint's), each as the real servers
/// were seen to answer, and the faults those showed when the fakes were made (24-Oct-2026), kept fixed.
/// </summary>
[TestClass]
public class FakeSharePointTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The farm's test site (or <paramref name="site"/>) as the lab's test account; listings not kept, so nothing walks behind a test's back.</summary>
    private static SharePointSettings FarmSettings(FakeSharePoint fake, string? site = null, int spoolBelowMiB = 8) => new()
    {
        SiteUrl = site ?? fake.Origin + FakeSharePoint.TestSitePath,
        Username = FakeSharePoint.FarmUser,
        Password = FakeSharePoint.FakePassword,
        SpoolBelowMiB = spoolBelowMiB,
        KeepListings = false,
    };

    /// <summary>The tenant's test site (or <paramref name="site"/>) as the sandbox's test account, signed in with <paramref name="signIn"/>.</summary>
    private static SharePointSettings TenantSettings(FakeSharePoint fake, string signIn, string? site = null, int spoolBelowMiB = 8) => new()
    {
        SiteUrl = site ?? fake.Origin + FakeSharePoint.TestSitePath,
        SignInMethod = signIn,
        Account = FakeSharePoint.TenantUser,
        SpoolBelowMiB = spoolBelowMiB,
        KeepListings = false,
    };

    /// <summary>A source of the settings, started; a tenant's signs in through the fake's stand-in.</summary>
    private static async Task<SharePointSiteSource> StartAsync(FakeSharePoint fake, SharePointSettings settings, TestSourceContext context)
    {
        var source = new SharePointSiteSource(settings, fake.Flavour == FakeSharePoint.Kind.Online ? fake.StandIn() : null);
        await source.InitialiseAsync(context, None);
        return source;
    }

    [TestMethod]
    public async Task TheFarmAnswersThePluginAsTheLabDoes()
    {
        using var fake = FakeSharePoint.LabFarm();
        var result = await new SharePointPlugin().TestAsync(FarmSettings(fake), new Progress<string>(), None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.StartsWith(result.Message, $"Signed in as {FakeSharePoint.FarmUser}. anyfs ({fake.Origin}{FakeSharePoint.TestSitePath})");

        await using var source = (SharePointSiteSource)new SharePointPlugin().CreateSource(FarmSettings(fake));
        using var context = new TestSourceContext("sharepoint");
        await source.InitialiseAsync(context, None);
        var names = await SpsTestServer.NamesAsync(source, "");
        CollectionAssert.IsSubsetOf(new[] { "Documents", "Site Assets", "Site Pages", "Style Library", "Form Templates" }, names, string.Join(", ", names));
        CollectionAssert.DoesNotContain(names, "Master Page Gallery", "only document libraries and pages");

        // a wrong password: the farm's 401 with no body
        var wrong = FarmSettings(fake);
        wrong.Password = "not it";
        result = await new SharePointPlugin().TestAsync(wrong, new Progress<string>(), None);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "refused the sign-in (401");
    }

    /// <summary>
    /// The farm's Windows sign-in as the farms have it (Negotiate or NTLM, here through HTTP.sys): "Windows account, as
    /// yourself" signs in as this PC's user, whom the fake takes for the lab's test account (Windows only).
    /// </summary>
    [TestMethod]
    public async Task AFarmSignedInToAsYourselfTakesThisPCsUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows' own sign-in, through HTTP.sys");
        }

        using var fake = FakeSharePoint.LabFarm(signIn: FakeSharePoint.FarmSignIn.Windows);
        var settings = new SharePointSettings { SiteUrl = fake.Origin + FakeSharePoint.TestSitePath, SignInMethod = SharePointSettings.WindowsCurrent, KeepListings = false };
        var result = await new SharePointPlugin().TestAsync(settings, new Progress<string>(), None);
        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(fake.Requests.Any(r => r.Account == FakeSharePoint.FarmUser), "signed in, and taken for the test account");
    }

    /// <summary>
    /// A server, or a proxy before it, that ignores a Range sends the whole file: what is read from an offset is still the
    /// file from there (it was the file from its start).
    /// </summary>
    [TestMethod]
    [DataRow(false, DisplayName = "farm, REST")]
    [DataRow(true, DisplayName = "tenant, Graph")]
    public async Task AServerThatIgnoresRangesIsReadPastTheStart(bool tenant)
    {
        using var fake = tenant ? FakeSharePoint.Sandbox() : FakeSharePoint.LabFarm();
        var bytes = new byte[300_000];
        new Random(3).NextBytes(bytes);
        fake.Seed(fake.TestSite.Library("Documents"), "ranges/whole.bin", bytes);
        fake.HonoursRanges = false;
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, tenant ? TenantSettings(fake, SharePointSettings.AppDeviceCode, spoolBelowMiB: 0) : FarmSettings(fake, spoolBelowMiB: 0), context);

        await using var file = await source.OpenAsync("Documents/ranges/whole.bin", VfsOpenMode.Read, None);
        var middle = new byte[10_000];
        Assert.AreEqual(middle.Length, await file.ReadFullyAsync(200_000, middle, None));
        CollectionAssert.AreEqual(bytes[200_000..210_000], middle, "the file from the offset, not from its start");
    }

    /// <summary>A web session that lapses is renewed without a window, and the write it refused goes through.</summary>
    [TestMethod]
    public async Task ALapsedWebSessionIsRenewedWithoutAWindow()
    {
        using var fake = FakeSharePoint.Sandbox();
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.WebSession), context);
        Assert.AreEqual(1, fake.SessionSignIns);

        fake.ExpireSessions();
        await source.CreateDirectoryAsync("Documents/after-lapse", None);
        Assert.AreEqual(2, fake.SessionSignIns, "renewed once");
        Assert.IsNotNull(fake.TestSite.Library("Documents").Root.Folder("after-lapse"));
    }

    /// <summary>
    /// SharePoint Online refuses a reader's write as it refuses a lapsed session (403), but sets the session cookie again on
    /// the answer: the refusal is the account's, said as such, and the session is not renewed for it (21-Oct-2026's storm).
    /// </summary>
    [TestMethod]
    public async Task AReadersRefusedWriteKeepsTheSession()
    {
        using var fake = FakeSharePoint.Sandbox();
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.WebSession, site: fake.Origin), context);

        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.CreateDirectoryAsync("Documents/not-mine", None));
        Assert.AreEqual(VfsError.AccessDenied, ex.Error, ex.Message);
        Assert.IsTrue(fake.Count(s => s.Status == 403) > 0, "SharePoint refused it");
        Assert.AreEqual(1, fake.SessionSignIns, "no renewal");
        Assert.AreNotEqual(ComponentHealth.Degraded, context.LastHealth);
    }

    /// <summary>Throttled (429 with Retry-After) at SharePoint Online's pace: the requests wait it out, and the write goes through.</summary>
    [TestMethod]
    public async Task AThrottledWriteWaitsAndGoesThrough()
    {
        using var fake = FakeSharePoint.Sandbox("anyfs-paced", paced: true);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.WebSession), context);

        fake.Throttle(2, retryAfterSeconds: 1, when: r => r.Method == "POST" && r.RawPath.Contains("/Folders/", StringComparison.OrdinalIgnoreCase));
        var watch = Stopwatch.StartNew();
        await source.CreateDirectoryAsync("Documents/after-throttle", None);
        Assert.IsTrue(watch.Elapsed >= TimeSpan.FromSeconds(1.8), $"held for each Retry-After ({watch.Elapsed.TotalSeconds:F1} s)");
        Assert.AreEqual(2, fake.Count(s => s.Status == 429));
        Assert.IsNotNull(fake.TestSite.Library("Documents").Root.Folder("after-throttle"));
    }

    /// <summary>Graph's download addresses expire: a read after that asks for the file's address again.</summary>
    [TestMethod]
    public async Task ADownloadAddressThatExpiredIsAskedForAgain()
    {
        using var fake = FakeSharePoint.Sandbox();
        var bytes = new byte[3 << 20];
        new Random(5).NextBytes(bytes);
        fake.Seed(fake.TestSite.Library("Documents"), "expiry/big.bin", bytes);
        using var context = new TestSourceContext("sharepoint");
        await using var source = await StartAsync(fake, TenantSettings(fake, SharePointSettings.AppDeviceCode, spoolBelowMiB: 0), context);

        await using var file = await source.OpenAsync("Documents/expiry/big.bin", VfsOpenMode.Read, None);
        var piece = new byte[4096];
        Assert.AreEqual(piece.Length, await file.ReadFullyAsync(0, piece, None));
        fake.ExpireDownloadUrls();
        Assert.AreEqual(piece.Length, await file.ReadFullyAsync(5 << 19, piece, None));
        CollectionAssert.AreEqual(bytes[(5 << 19)..((5 << 19) + 4096)], piece);
        Assert.AreNotEqual(ComponentHealth.Degraded, context.LastHealth);
    }
}
