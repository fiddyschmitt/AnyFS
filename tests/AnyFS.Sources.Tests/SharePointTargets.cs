using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

/// <summary>
/// Where a SharePoint suite runs: the lab's farm or the Microsoft 365 sandbox when the environment names them
/// (ANYFS_TEST_SPS_*, _SPO_*, _SPE_*), else the fakes (<see cref="SharePointFakes"/>), which stand in for them so the
/// suites run anywhere, CI included. What a test needs of its target is here: the settings, a source started (with the
/// fixture tree), the Test button, a context; a fake's sources sign in through its stand-in.
/// </summary>
internal sealed class SharePointTarget
{
    /// <summary>What it is, for messages: "the lab's farm at http://192.168.0.92/sites/anyfs", "the fake farm".</summary>
    public required string Description { get; init; }

    /// <summary>The test site's address (for Embedded, the container type).</summary>
    public required string Site { get; init; }

    /// <summary>The library the fixture is in (for Embedded, the container).</summary>
    public string Library { get; init; } = "Documents";

    /// <summary>A Windows account (farms).</summary>
    public string? User { get; init; }

    public string? Password { get; init; }

    /// <summary>A Microsoft 365 account (an app sign-in's).</summary>
    public string? Account { get; init; }

    public required string SignInMethod { get; init; }

    public string FixtureFolder { get; init; } = "anyfs-fixture";

    public required byte[] ReadmeBytes { get; init; }

    /// <summary>The fake standing in, or null for the lab or the sandbox.</summary>
    public FakeSharePoint? Fake { get; init; }

    /// <summary>A second server's test site, for the test of sites on two servers.</summary>
    public string? OtherServerSite { get; init; }

    /// <summary>The lab's Microsoft sign-in (tests/fixtures/SharePointSignIn's file), for an app sign-in.</summary>
    public string? CachePath { get; init; }

    /// <summary>The environment variable naming a file for the source's log.</summary>
    public string? LogVariable { get; init; }

    /// <summary>Why the suite cannot run here (no lab, and the fakes turned off); null when it can.</summary>
    public string? Missing { get; init; }

    public bool WebSession => SignInMethod == SharePointSettings.WebSession;

    public bool IsFake => Fake is not null;

    /// <summary>The stand-in for Microsoft 365 and the browser, for a fake tenant; null for the real thing and for a farm, which needs none.</summary>
    public SharePointStandIn? StandIn => Fake is { Flavour: FakeSharePoint.Kind.Online } fake ? fake.StandIn(Account ?? FakeSharePoint.TenantUser) : null;

    /// <summary>Inconclusive where the suite cannot run.</summary>
    public void Require()
    {
        if (Missing is not null)
        {
            Assert.Inconclusive(Missing);
        }
    }

    public SharePointSettings Settings(string? library = null, int spoolBelowMiB = 8) => new()
    {
        SiteUrl = Site,
        Library = library,
        SignInMethod = SignInMethod,
        Username = User,
        Password = Password,
        Account = Account,
        SpoolBelowMiB = spoolBelowMiB,
        TrustAnyCertificate = true,
    };

    /// <summary>A context for a source: its log where the environment names a file, and for the lab's app sign-in its saved sign-in.</summary>
    public TestSourceContext Context()
    {
        var logPath = LogVariable is null ? null : Environment.GetEnvironmentVariable(LogVariable);
        var context = new TestSourceContext("sharepoint", string.IsNullOrWhiteSpace(logPath) ? null : new SpoTestServer.FileLogger(logPath));
        if (!IsFake && CachePath is not null && SignInCacheFile.Read(CachePath) is { } cache)
        {
            context.Secrets.Set("msal-cache", Convert.ToBase64String(cache));
        }

        return context;
    }

    public SharePointSiteSource CreateSource(SharePointSettings settings) => new(settings, StandIn);

    /// <summary>The settings dialog's Test button.</summary>
    public Task<Sdk.TestResult> TestAsync(SharePointSettings settings, CancellationToken ct = default) => new SharePointPlugin().TestAsync(settings, new Progress<string>(), StandIn, ct);

    /// <summary>Starts a source of the settings; for the lab's app sign-in, a sign-in that no longer works makes the test inconclusive.</summary>
    public async Task<SharePointSiteSource> StartAsync(SharePointSettings settings, TestSourceContext? context = null)
    {
        var source = CreateSource(settings);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await source.InitialiseAsync(context ?? Context(), timeout.Token);
        }
        catch (SourceFailedException ex) when (!IsFake && CachePath is not null && (ex.Message.Contains("sign", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Conditional Access", StringComparison.Ordinal)))
        {
            Assert.Inconclusive($"the saved sign-in did not work ({ex.Message}); run tests/fixtures/SharePointSignIn again");
        }
        catch (OperationCanceledException) when (!IsFake && CachePath is not null)
        {
            Assert.Inconclusive("the saved sign-in has lapsed and the source waited for a new one; run tests/fixtures/SharePointSignIn again");
        }

        return source;
    }

    /// <summary>Starts a source and makes sure the fixture tree is in the library: readme.txt, medium.bin (50,000 bytes), docs/a.txt, scratch/.</summary>
    public async Task<SharePointSiteSource> StartWithFixtureAsync(SharePointSettings settings)
    {
        var source = await StartAsync(settings);
        var root = settings.Library is null ? Library : "";
        var fixture = VfsPath.Join(root, FixtureFolder);
        await SpoTestServer.EnsureDirectoryAsync(source, fixture);
        await SpoTestServer.EnsureDirectoryAsync(source, VfsPath.Join(fixture, "docs"));
        await SpoTestServer.EnsureDirectoryAsync(source, VfsPath.Join(fixture, "scratch"));
        await SpoTestServer.EnsureFileAsync(source, VfsPath.Join(fixture, "readme.txt"), ReadmeBytes);
        await SpoTestServer.EnsureFileAsync(source, VfsPath.Join(fixture, "docs/a.txt"), "aaa"u8.ToArray());
        await SpoTestServer.EnsureFileAsync(source, VfsPath.Join(fixture, "medium.bin"), SpoTestServer.MediumBytes());
        return source;
    }

    /// <summary>Starts a source of the settings with these sites, one per line.</summary>
    public Task<SharePointSiteSource> StartSitesAsync(SharePointSettings settings, TestSourceContext context, params string[] sites)
    {
        settings.SiteUrl = string.Join("\n", sites);
        return StartAsync(settings, context);
    }

    /// <summary>A file of each size written into the fixture's scratch folder, its length and its end read back from SharePoint, and deleted.</summary>
    public async Task UploadsRoundTripAsync(params long[] sizes)
    {
        Require();
        await using var source = await StartWithFixtureAsync(Settings());
        var scratch = VfsPath.Join(VfsPath.Join(Library, FixtureFolder), "scratch");
        foreach (var size in sizes)
        {
            var bytes = new byte[size];
            new Random((int)size).NextBytes(bytes);
            var path = VfsPath.Join(scratch, $"size-{size}-{Guid.NewGuid():N}.bin");
            await using (var file = await source.OpenAsync(path, VfsOpenMode.CreateNew, CancellationToken.None))
            {
                await file.WriteAsync(0, bytes, CancellationToken.None);
            }

            try
            {
                Assert.AreEqual(size, (await source.StatAsync(path, CancellationToken.None))?.Length, $"{size:N0} bytes");
                await using var back = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
                var tail = new byte[4096];
                Assert.AreEqual(tail.Length, await back.ReadFullyAsync(size - tail.Length, tail, CancellationToken.None), $"{size:N0} bytes");
                CollectionAssert.AreEqual(bytes[^tail.Length..], tail, $"the end of {size:N0} bytes");
            }
            finally
            {
                await source.DeleteAsync(path, CancellationToken.None);
            }
        }
    }

    /// <summary>The SDK contract's setup on this target: libraries at the top, or the library pinned and nothing spooled.</summary>
    public async Task<SourceContractSetup> ContractSetupAsync(bool pinned)
    {
        Require();
        var source = await StartWithFixtureAsync(pinned ? Settings(library: Library, spoolBelowMiB: 0) : Settings());
        var fixture = pinned ? FixtureFolder : VfsPath.Join(Library, FixtureFolder);
        return new SourceContractSetup(source, fixture, VfsPath.Join(fixture, "readme.txt"), ReadmeBytes, WritableDirectory: VfsPath.Join(fixture, "scratch"));
    }

    public override string ToString() => Description;
}

/// <summary>
/// The fakes the suites run against when the lab and the sandbox are not named: one farm as the lab's (and a second, on
/// localhost, for sites on two servers) and one tenant as the sandbox, each started once for the run and shared, as the
/// lab and the sandbox are. ANYFS_TEST_SHAREPOINT_FAKES=0 turns them off, and the suites are then inconclusive without
/// the lab, as before the fakes.
/// </summary>
internal static class SharePointFakes
{
    private static readonly Lazy<FakeSharePoint> LazyFarm = new(() => FakeSharePoint.LabFarm());
    private static readonly Lazy<FakeSharePoint> LazyOtherFarm = new(() => FakeSharePoint.LabFarm("localhost"));
    private static readonly Lazy<FakeSharePoint> LazyTenant = new(() => FakeSharePoint.Sandbox());

    public static bool Enabled => Environment.GetEnvironmentVariable("ANYFS_TEST_SHAREPOINT_FAKES") is not ("0" or "false" or "off");

    public static FakeSharePoint Farm => LazyFarm.Value;

    public static FakeSharePoint OtherFarm => LazyOtherFarm.Value;

    public static FakeSharePoint Tenant => LazyTenant.Value;
}
