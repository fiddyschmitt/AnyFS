using System.Text.Json;
using AnyFS.Fixtures;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Microsoft365;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

[TestClass]
public class SharePointEmbeddedPluginTests
{
    private const string ContainerType = "91710488-5756-407f-9046-fbe5f0b4de73";

    [TestMethod]
    public void AContainerTypeIdIsTheAddressOfEmbedded()
    {
        var plugin = new SharePointPlugin();
        var settings = (SharePointSettings)plugin.CreateDefaultSettings();
        Assert.AreEqual("sharepoint", plugin.TypeId);
        Assert.AreEqual("sharepoint", plugin.TypeFolder);
        Assert.AreEqual(EntraSignIn.DefaultClientId, settings.ClientId);
        CollectionAssert.AreEqual(new[] { nameof(SharePointSettings.SiteUrl) }, plugin.Validate(settings).Select(m => m.Property).ToArray());

        settings.SiteUrl = $" {ContainerType} ";
        Assert.AreEqual(SharePointKind.Embedded, settings.Kind);
        Assert.AreEqual(SharePointSignIn.AppBrowser, settings.EffectiveSignIn, "Automatic signs in to Embedded as an app");
        Assert.AreEqual(0, plugin.Validate(settings).Count());
        Assert.IsTrue(settings.TryGetContainerType(out var id));
        Assert.AreEqual(Guid.Parse(ContainerType), id);
    }

    [TestMethod]
    public void BadValuesAreNamed()
    {
        var plugin = new SharePointPlugin();
        var settings = new SharePointSettings
        {
            SiteUrl = ContainerType,
            Library = "a/b",
            Account = "no-at-sign",
            Tenant = "has space",
            ClientId = "nope",
            Connections = 0,
            SpoolBelowMiB = -1,
        };
        var fields = plugin.Validate(settings).Select(m => m.Property).ToList();
        CollectionAssert.AreEquivalent(new[]
        {
            nameof(SharePointSettings.Library), nameof(SharePointSettings.Account), nameof(SharePointSettings.Tenant),
            nameof(SharePointSettings.ClientId), nameof(SharePointSettings.Connections), nameof(SharePointSettings.SpoolBelowMiB),
        }, fields);
    }

    [TestMethod]
    public void SignInMethodAndTenantFallBack()
    {
        var settings = new SharePointSettings { SiteUrl = ContainerType, SignInMethod = "device CODE", Tenant = " ", ClientId = "" };
        Assert.AreEqual(SignInMethod.DeviceCode, settings.Method, "the earlier \"Device code\" value still works");
        Assert.AreEqual("organizations", settings.EffectiveTenant);
        Assert.AreEqual(EntraSignIn.DefaultClientId, settings.EffectiveClientId);
        Assert.IsNull(settings.PinnedLibrary);
        Assert.IsNull(settings.LoginHint);
    }
}

[TestClass]
public class ContainerListingTests
{
    [TestMethod]
    [DataRow("Project files", "b!abc", "Project files")]
    [DataRow("  padded  ", "b!abc", "padded")]
    [DataRow("a/b:c*d?e\"f<g>h|i\\j", "b!abc", "a_b_c_d_e_f_g_h_i_j")]
    [DataRow("trailing dots...", "b!abc", "trailing dots")]
    [DataRow("", "b!0123456789", "Container 01234567")]
    [DataRow(null, "b!0123456789", "Container 01234567")]
    [DataRow("CON", "b!0123456789", "Container 01234567")]
    [DataRow("~$temp", "b!0123456789", "Container 01234567")]
    [DataRow("...", "b!xyz", "Container xyz")]
    public void ContainerNamesBecomeFolderNames(string? displayName, string id, string expected)
    {
        Assert.AreEqual(expected, GraphDocumentStore.FolderName(displayName, id));
    }

    [TestMethod]
    public void DuplicateNamesAreNumberedInIdOrder()
    {
        var containers = new List<LibraryInfo>
        {
            new("b!2", "Shared", null, null),
            new("b!1", "Shared", null, null),
            new("b!3", "shared", null, null),
            new("b!4", "Other", null, null),
        };
        var unique = GraphDocumentStore.UniqueNames(containers);
        CollectionAssert.AreEqual(new[] { "Other", "Shared", "Shared (2)", "shared (3)" }, unique.Select(c => c.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "b!4", "b!1", "b!2", "b!3" }, unique.Select(c => c.Key).ToArray());
    }

    [TestMethod]
    [DataRow("b!nw77cUDU_kaK32kwNjJjUr7Q4rON_QFLj5YpwhZMa74WTkVfoJqBQJNZTBT_55b3", true)]
    [DataRow("anyfs-test", false)]
    [DataRow("b!short", false)]
    [DataRow("b! has a space in it and is long enough", false)]
    public void AContainerIdIsToldFromAName(string value, bool isId)
    {
        Assert.AreEqual(isId, GraphDocumentStore.LooksLikeContainerId(value));
        var settings = new SharePointSettings { SiteUrl = "91710488-5756-407f-9046-fbe5f0b4de73", Library = value };
        Assert.AreEqual(isId ? value : null, settings.PinnedContainerId);
        Assert.AreEqual(value, settings.PinnedLibrary);
    }

    [TestMethod]
    public void InactiveContainersAreSkippedAndStatusIsOptional()
    {
        using var active = JsonDocument.Parse("{\"id\":\"b!one\",\"displayName\":\"One\",\"status\":\"active\"}");
        using var inactive = JsonDocument.Parse("{\"id\":\"b!two\",\"displayName\":\"Two\",\"status\":\"inactive\"}");
        using var terse = JsonDocument.Parse("{\"id\":\"b!three\",\"displayName\":\"Three\",\"containerTypeId\":\"91710488-5756-407f-9046-fbe5f0b4de73\"}");
        var one = GraphDocumentStore.ParseContainer(active.RootElement);
        Assert.IsNotNull(one);
        Assert.AreEqual("b!one", one.Key);
        Assert.AreEqual("One", one.Name);
        Assert.IsNull(one.QuotaTotal);
        Assert.IsNull(GraphDocumentStore.ParseContainer(inactive.RootElement));
        Assert.AreEqual("Three", GraphDocumentStore.ParseContainer(terse.RootElement)?.Name);
    }
}

/// <summary>
/// Where the Embedded live tests run: ANYFS_TEST_SPE_CONTAINER_TYPE (a container type the signed-in account has
/// containers of, made by provisioning/sharepoint/embedded-rig.ps1), _CONTAINER (the container the pinned tests
/// use, anyfs-test), _ACCOUNT and _CACHE (shared with the Online tests' ANYFS_TEST_SPO_ACCOUNT / _CACHE when
/// unset: one sign-in from tests/fixtures/SharePointSignIn covers both). The fixture tree is made by the tests
/// themselves, as for SharePoint Online.
/// </summary>
internal static class SpeTestServer
{
    private static readonly Lazy<SharePointTarget> LazyTarget = new(MakeTarget);

    /// <summary>The sandbox's container type, or the fake tenant's.</summary>
    public static SharePointTarget Target => LazyTarget.Value;

    /// <summary>The container the fixture is in (and the pinned tests use).</summary>
    public static string Container => Target.Library;

    public static string CachePath => Env("ANYFS_TEST_SPE_CACHE") ?? SpoTestServer.CachePath;

    /// <summary>Device code as the method, as for Online: a lapsed sign-in fails fast instead of opening a browser on the test machine.</summary>
    private static SharePointTarget MakeTarget()
    {
        var account = Env("ANYFS_TEST_SPE_ACCOUNT") ?? SpoTestServer.Account;
        if (Env("ANYFS_TEST_SPE_CONTAINER_TYPE") is { } containerType)
        {
            return new SharePointTarget
            {
                Description = $"SharePoint Embedded's containers of type {containerType}",
                Site = containerType,
                Library = Env("ANYFS_TEST_SPE_CONTAINER") ?? "anyfs-test",
                Account = account,
                SignInMethod = SharePointSettings.AppDeviceCode,
                ReadmeBytes = SpoTestServer.ReadmeBytes,
                CachePath = CachePath,
                LogVariable = "ANYFS_TEST_SPE_LOG",
                Missing = File.Exists(CachePath) ? null : $"no sign-in at {CachePath}: run tests/fixtures/SharePointSignIn once (dotnet run --project tests/fixtures/SharePointSignIn -- --account {account})",
            };
        }

        if (!SharePointFakes.Enabled)
        {
            return new SharePointTarget { Description = "no SharePoint Embedded", Site = "", SignInMethod = SharePointSettings.AppDeviceCode, ReadmeBytes = SpoTestServer.ReadmeBytes, Missing = "no SharePoint Embedded container type: set ANYFS_TEST_SPE_CONTAINER_TYPE (and _CONTAINER, _ACCOUNT, _CACHE; provisioning/sharepoint/embedded-rig.ps1 makes one), or let the fakes stand in" };
        }

        return new SharePointTarget
        {
            Description = "the fake tenant's containers",
            Site = FakeSharePoint.ContainerType.ToString(),
            Library = "anyfs-test",
            Account = FakeSharePoint.TenantUser,
            SignInMethod = SharePointSettings.AppDeviceCode,
            ReadmeBytes = SpoTestServer.ReadmeBytes,
            Fake = SharePointFakes.Tenant,
            LogVariable = "ANYFS_TEST_SPE_LOG",
        };
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

/// <summary>The SDK's contract on containers (the sandbox's, or the fake tenant's), containers at the top level.</summary>
[TestClass]
public class SharePointEmbeddedContractTests : SourceContractTests
{
    protected override Task<SourceContractSetup> SetUpAsync() => SpeTestServer.Target.ContractSetupAsync(pinned: false);
}

/// <summary>The same contract with one container pinned, nothing spooled.</summary>
[TestClass]
public class SharePointEmbeddedPinnedContainerContractTests : SourceContractTests
{
    protected override Task<SourceContractSetup> SetUpAsync() => SpeTestServer.Target.ContractSetupAsync(pinned: true);
}

[TestClass]
public class SharePointEmbeddedLiveTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static SharePointTarget Target => SpeTestServer.Target;

    [TestMethod]
    public async Task ContainersAreTheTopLevelFolders()
    {
        Target.Require();
        await using var source = await Target.StartWithFixtureAsync(Target.Settings());
        var names = new List<string>();
        await foreach (var entry in source.ListAsync("", None))
        {
            Assert.IsTrue(entry.IsDirectory, $"{entry.Name} should be a folder");
            names.Add(entry.Name);
        }

        CollectionAssert.Contains(names, SpeTestServer.Container);
        Assert.IsNotNull(await source.StatAsync(SpeTestServer.Container.ToUpperInvariant(), None), "container names are case-insensitive");

        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.OpenAsync("new-container-as-file.txt", VfsOpenMode.Create, None));
        Assert.AreEqual(VfsError.InvalidPath, ex.Error, "a file can't be created beside the containers");
        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.CreateDirectoryAsync("new-container", None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error, "a container can't be created from the file system");
        Assert.IsTrue(ex.Message.Contains("container", StringComparison.Ordinal), ex.Message);
    }

    [TestMethod]
    public async Task RangedReadsAgreeWithTheWholeFile()
    {
        Target.Require();
        var expected = SpoTestServer.MediumBytes();
        await using var source = await Target.StartWithFixtureAsync(Target.Settings(spoolBelowMiB: 0));
        var path = VfsPath.Join(VfsPath.Join(SpeTestServer.Container, SpoTestServer.FixtureFolder), "medium.bin");
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, None);
        Assert.AreEqual(expected.Length, file.Length);
        var whole = new byte[expected.Length];
        Assert.AreEqual(whole.Length, await file.ReadFullyAsync(0, whole, None));
        CollectionAssert.AreEqual(expected, whole);
        var tail = new byte[1000];
        Assert.AreEqual(tail.Length, await file.ReadFullyAsync(49_000, tail, None));
        CollectionAssert.AreEqual(expected[49_000..], tail);
    }

    [TestMethod]
    public async Task LargeUploadRoundTrips()
    {
        Target.Require();
        await using var source = await Target.StartWithFixtureAsync(Target.Settings());
        var big = new byte[5 * 1024 * 1024];
        new Random(7).NextBytes(big);
        var path = VfsPath.Join(VfsPath.Join(VfsPath.Join(SpeTestServer.Container, SpoTestServer.FixtureFolder), "scratch"), $"big-{Guid.NewGuid():N}.bin");
        await using (var file = await source.OpenAsync(path, VfsOpenMode.CreateNew, None))
        {
            await file.WriteAsync(0, big, None);
        }

        var stat = await source.StatAsync(path, None);
        Assert.IsNotNull(stat);
        Assert.AreEqual(big.Length, stat.Length);
        await using (var file = await source.OpenAsync(path, VfsOpenMode.Read, None))
        {
            var head = new byte[4096];
            Assert.AreEqual(head.Length, await file.ReadFullyAsync(0, head, None));
            CollectionAssert.AreEqual(big[..4096], head);
        }

        await source.DeleteAsync(path, None);
        Assert.IsNull(await source.StatAsync(path, None));
    }

    [TestMethod]
    public async Task TestButtonListsTheContainers()
    {
        Target.Require();
        var settings = Target.Settings();
        if (!Target.IsFake)
        {
            // the Test button has only the process-wide cache (a source that signed in would have left it there)
            EntraSignIn.Remember(settings.EffectiveClientId, settings.EffectiveTenant, settings.LoginHint, SignInCacheFile.Read(SpeTestServer.CachePath)!);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await Target.TestAsync(settings, timeout.Token);
        if (!result.Success && result.Message.Contains("sign-in", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive($"the saved sign-in did not work ({result.Message}); run tests/fixtures/SharePointSignIn again");
        }

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(result.Message.Contains(SpeTestServer.Container, StringComparison.OrdinalIgnoreCase), result.Message);
    }
}
