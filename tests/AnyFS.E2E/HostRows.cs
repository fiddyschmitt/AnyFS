using System.Diagnostics;

namespace AnyFS.E2E;

/// <summary>One host machine's anyfs, deployed once per test class, and the clients that reach it.</summary>
public sealed class Deployment : IDisposable
{
    public Machine? Host { get; set; }

    public Machine? Upstream { get; set; }

    /// <summary>The other OSes' host machines, connected as clients of this one.</summary>
    public List<Machine> Others { get; } = [];

    public AnyFsHost? AnyFs { get; set; }

    /// <summary>The clients by name: <c>local</c> is the host itself, <c>upstream</c> the Linux upstream, <c>windows</c> and <c>macos</c> the hosts of the other OSes.</summary>
    public Dictionary<string, Client> Clients { get; } = [];

    /// <summary>Why a client's rows are Inconclusive: its machine can't be reached.</summary>
    public Dictionary<string, string> ClientUnavailable { get; } = [];

    public Client? Local => Clients.GetValueOrDefault("local");

    /// <summary>Why the rows are Inconclusive: a machine that can't be reached.</summary>
    public string? Unavailable { get; set; }

    /// <summary>Why a server's rows are Inconclusive, by its collection name (sharepoint, sharepoint2019, gitlab): the server is not up.</summary>
    public Dictionary<string, string> ServerUnavailable { get; } = [];

    /// <summary>Why the rows fail: the deployment itself went wrong.</summary>
    public string? Failure { get; set; }

    public void Dispose()
    {
        AnyFs?.Dispose();
        Host?.Dispose();
        Upstream?.Dispose();
        foreach (var other in Others)
        {
            other.Dispose();
        }
    }
}

/// <summary>
/// The E2E matrix of spec 16.2 for one anyfs host: (collection × mount × client) rows against one running
/// anyfs, where the collections are each source and its read-only twin, the mounts every provider the host's
/// OS has, and the clients every machine in the lab: the host itself, the Linux upstream, and the hosts of the
/// other two OSes (so each OS is also exercised as a client of each other OS). A row lists the tree and compares it with
/// the fixture (names, unicode, spaces, a 204-character name, nesting, an empty folder, the 5,000-file folder),
/// hashes the 1 KiB, 1 MiB and 200 MiB files (the last one timed, for the throughput floor), reads three ranges,
/// does the write round trip (or has it refused on the read-only twin, and skips it over HTTP), reads with eight
/// readers at once, and over HTTP resumes an interrupted download. The class also checks the counts, the
/// prompt-needing source, the plugin in the trimmed build, an upstream outage and the log.
/// </summary>
public abstract class HostRows
{
    private static readonly Dictionary<string, Deployment> deployments = [];

    protected abstract string Os { get; }

    public static IReadOnlyList<MountKind> MountsOf(string os) => os switch
    {
        "windows" => [MountKind.WinFsp, MountKind.Smb, MountKind.WebDav, MountKind.Ftp, MountKind.Http],
        "linux" => [MountKind.Fuse, MountKind.Smb, MountKind.WebDav, MountKind.Ftp, MountKind.Http],
        _ => [MountKind.Smb, MountKind.WebDav, MountKind.Ftp, MountKind.Http],
    };

    public static IReadOnlyList<string> CollectionsOf(string os)
    {
        var list = new List<string> { "mirror", "mirror-ro", "sshfs", "sshfs-ro" };
        if (os == "windows")
        {
            list.AddRange(["unc", "unc-ro"]);
        }

        foreach (var farm in Lab.SharePoints)
        {
            // the lab's SharePoint Server farms, as settings.yml describes them (a farm's rows are Inconclusive while it is down)
            list.AddRange([farm.Collection, farm.Collection + "-ro"]);
        }

        if (Lab.GitLab is not null)
        {
            // the lab's GitLab server; its source is read-only itself, so its -ro collection would only repeat these rows
            list.Add(GitLabFixture.Collection);
        }

        return list;
    }

    /// <summary>
    /// The clients of a host: itself, the Linux upstream, and the hosts of the other OSes; every machine in the lab but
    /// the one under test, so Windows and macOS are clients of the other hosts too, not only Linux.
    /// </summary>
    public static IReadOnlyList<string> ClientsOf(string os)
        => new[] { "local", "upstream" }.Concat(new[] { "windows", "macos" }.Where(o => o != os)).ToList();

    /// <summary>
    /// Comma-separated, case-insensitive lists that narrow the rows to these collections (<c>sharepoint,sharepoint-ro</c>),
    /// mounts (<c>Smb,Ftp</c>) or clients (<c>local,macos</c>), so a retest of one source or provider need not take the
    /// whole matrix's time (a test filter cannot pick data rows apart). Unset means all.
    /// </summary>
    public const string CollectionsVariable = "ANYFS_E2E_COLLECTIONS";

    public const string MountsVariable = "ANYFS_E2E_MOUNTS";

    public const string ClientsVariable = "ANYFS_E2E_CLIENTS";

    /// <summary>The rows: every collection × mount × client, minus the native mounts from the other machines.</summary>
    public static IEnumerable<object[]> RowsFor(string os)
    {
        var collections = Selection(CollectionsVariable);
        var mounts = Selection(MountsVariable);
        var clients = Selection(ClientsVariable);
        foreach (var collection in CollectionsOf(os))
        {
            if (collections is not null && !collections.Contains(collection))
            {
                continue;
            }

            foreach (var mount in MountsOf(os))
            {
                if (mounts is not null && !mounts.Contains(mount.ToString()))
                {
                    continue;
                }

                foreach (var client in ClientsOf(os))
                {
                    if (client != "local" && mount is MountKind.Fuse or MountKind.WinFsp)
                    {
                        continue;
                    }

                    if (clients is not null && !clients.Contains(client))
                    {
                        continue;
                    }

                    yield return [collection, mount, client];
                }
            }
        }
    }

    private static HashSet<string>? Selection(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Connects to the host and the upstream, deploys anyfs with the full settings and waits for the collections to be green. Once per class.</summary>
    protected static Deployment Prepare(string os)
    {
        lock (deployments)
        {
            if (deployments.TryGetValue(os, out var existing))
            {
                return existing;
            }
        }

        var d = new Deployment();
        var hostInfo = Lab.Host(os);
        if (hostInfo is null)
        {
            d.Unavailable = $"settings.yml has no {os} machine with role host";
        }
        else if ((d.Host = Machine.TryConnect(hostInfo, Lab.SshKey)) is null)
        {
            d.Unavailable = $"{hostInfo.Name} ({hostInfo.Ip}) is not reachable over SSH with the lab key: is it up? (vagrant status / .\\mac.ps1 status)";
        }
        else if ((d.Upstream = Machine.TryConnect(Lab.Upstream, Lab.SshKey)) is null)
        {
            d.Unavailable = $"{Lab.Upstream.Name} ({Lab.Upstream.Ip}) is not reachable over SSH with the lab key";
        }
        else
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var sharePoint = new List<SourceSpec>();
                foreach (var farm in Lab.SharePoints)
                {
                    if (SharePointFixture.TryPrepare(farm, out var why) is { } spec)
                    {
                        sharePoint.Add(spec);
                    }
                    else
                    {
                        d.ServerUnavailable[farm.Collection] = why!;
                    }
                }

                if (Lab.GitLab is { } gitLab)
                {
                    if (GitLabFixture.TryPrepare(gitLab, os, out var why) is { } spec)
                    {
                        sharePoint.Add(spec);
                    }
                    else
                    {
                        d.ServerUnavailable[GitLabFixture.Collection] = why!;
                    }
                }

                d.AnyFs = new AnyFsHost(d.Host, d.Upstream, sharePoint);
                d.AnyFs.Deploy();
                d.AnyFs.Start();
                var status = d.AnyFs.WaitReady(d.AnyFs.FullCollections.Select(c => c.Collection));
                d.Clients["local"] = Client.For(d.Host);
                d.Clients["upstream"] = Client.For(d.Upstream);
                Trace.WriteLine($"{hostInfo.Name}: anyfs {status.Version} up with {status.Collections.Count} collections in {sw.Elapsed.TotalSeconds:N0} s");

                // the other OSes' hosts as clients of this one; a machine that is down makes its rows Inconclusive
                foreach (var other in ClientsOf(os).Where(c => c is not ("local" or "upstream")))
                {
                    var info = Lab.Host(other);
                    if (info is null)
                    {
                        d.ClientUnavailable[other] = $"settings.yml has no {other} machine with role host";
                    }
                    else if (Machine.TryConnect(info, Lab.SshKey) is { } machine)
                    {
                        d.Others.Add(machine);
                        d.Clients[other] = Client.For(machine);
                    }
                    else
                    {
                        d.ClientUnavailable[other] = $"{info.Name} ({info.Ip}) is not reachable over SSH with the lab key, so its rows as a client of {hostInfo.Name} did not run";
                    }
                }
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException or Renci.SshNet.Common.SshException)
            {
                d.Failure = ex.Message;
            }
        }

        lock (deployments)
        {
            deployments[os] = d;
        }

        return d;
    }

    protected static void Shutdown(string os)
    {
        Deployment? d;
        lock (deployments)
        {
            deployments.Remove(os, out d);
        }

        d?.Dispose();
    }

    protected Deployment Current()
    {
        var d = Prepare(Os);
        if (d.Unavailable is not null)
        {
            Assert.Inconclusive(d.Unavailable);
        }

        if (d.Failure is not null)
        {
            Assert.Fail("the deployment failed: " + d.Failure);
        }

        return d;
    }

    protected void RunRow(string collection, MountKind kind, string clientName)
    {
        var d = Current();
        var anyfs = d.AnyFs!;
        if (!d.Clients.TryGetValue(clientName, out var client))
        {
            Assert.Inconclusive(d.ClientUnavailable.GetValueOrDefault(clientName) ?? $"no client named {clientName}");
            return;
        }

        var farmCollection = collection.EndsWith("-ro", StringComparison.Ordinal) ? collection[..^3] : collection;
        if (d.ServerUnavailable.TryGetValue(farmCollection, out var farmDown))
        {
            Assert.Inconclusive(farmDown);
            return;
        }

        var spec = anyfs.Sources.Single(s => s.Collection == collection || s.ReadOnlyCollection == collection);
        // a GitLab source is read-only itself; git keeps no empty folder
        var readOnly = collection.EndsWith("-ro", StringComparison.Ordinal) || spec.Type == "gitlab";
        var noEmptyFolders = spec.Type == "gitlab";
        var prefix = spec.TreePrefix;
        string Rel(string p) => prefix.Length == 0 ? p : prefix + "/" + p;

        using var access = client.Attach(kind, anyfs, collection, clientName == "local");
        Console.WriteLine($"{collection} over {kind} from {client.M.Name}: {access.Path ?? access.Url}");

        // the tree. Names are compared in NFC: macOS's SMB client hands them back decomposed. An FTP NLST may
        // list "." and ".." (some servers do, like ProFTPD with -a), and davfs2 shows a lost+found of its own at
        // the mount root; neither is an entry of the tree. Nor is an e2e-* folder another row's failed write
        // round trip left behind (that row reported the failure; the sources share the upstream's tree).
        List<string> Names(string rel) => client.ListNames(access, rel)
            .Where(n => n is not "." and not ".." and not "lost+found" && !n.StartsWith("e2e-", StringComparison.Ordinal))
            .Select(n => n.Normalize(System.Text.NormalizationForm.FormC))
            .ToList();
        var root = Names(prefix);
        CollectionAssert.AreEquivalent(Fixture.RootNames.Where(n => !noEmptyFolders || n != "emptydir").ToList(), root, $"root listing: {string.Join(", ", root)}");
        Assert.AreEqual(Fixture.ManyCount, Names(Rel("many")).Count, "many/ lists completely");
        var unicode = Names(Rel("unicode"));
        CollectionAssert.AreEquivalent(new[] { "ünïcödé ✓ 日本語.txt" }, unicode, $"unicode name; the client saw: {string.Join(", ", unicode.Select(n => n + " [" + string.Join(" ", n.Select(c => ((int)c).ToString("x4"))) + "]"))}");
        CollectionAssert.AreEquivalent(new[] { "a b c.txt" }, Names(Rel("spaces")), "a name with spaces");
        var longNames = Names(Rel("long"));
        Assert.AreEqual(1, longNames.Count, "the long/ folder holds one name; the client saw: " + string.Join(" | ", longNames));
        Assert.AreEqual(new string('n', 200) + ".txt", longNames[0], "a 204-character name");
        Assert.AreEqual("deep.txt", Names(Rel("nested/a/b/c")).Single(), "nesting");
        if (!noEmptyFolders)
        {
            Assert.AreEqual(0, Names(Rel("emptydir")).Count, "an empty folder");
        }

        // whole files
        Assert.AreEqual(Fixture.Sha256Of("small.bin"), client.Sha256(access, Rel("small.bin")), "small.bin");
        Assert.AreEqual(Fixture.Sha256Of("medium.bin"), client.Sha256(access, Rel("medium.bin")), "medium.bin");
        var (large, seconds) = client.TimedSha256(access, Rel("large.bin"));
        Assert.AreEqual(Fixture.Sha256Of("large.bin"), large, "large.bin");
        var mbps = Fixture.SizeOf("large.bin") / 1048576.0 / Math.Max(seconds, 0.001);
        Console.WriteLine($"large.bin: {seconds:N1} s, {mbps:N0} MiB/s");
        Assert.IsTrue(mbps >= 8, $"throughput floor: {mbps:N1} MiB/s for the 200 MiB file");

        // ranges: the start, the middle, the tail
        Assert.AreEqual(Fixture.Sha256Hex(Fixture.Range("small.bin", 0, 100)), client.Sha256Range(access, Rel("small.bin"), 0, 100), "range at the start");
        Assert.AreEqual(Fixture.Sha256Hex(Fixture.Range("medium.bin", 512 * 1024, 4096)), client.Sha256Range(access, Rel("medium.bin"), 512 * 1024, 4096), "range in the middle");
        var tail = Fixture.SizeOf("large.bin") - 65536;
        Assert.AreEqual(Fixture.Sha256Hex(Fixture.Range("large.bin", tail, 65536)), client.Sha256Range(access, Rel("large.bin"), tail, 65536), "range at the tail");

        // no read-only mark on a file for being in a read-only collection or source: a copy of it would keep the mark (the
        // user, 23-Oct-2026); its writes are refused all the same (below)
        if (client.MarkedReadOnly(access, Rel("small.bin")) is { } marked)
        {
            Assert.IsFalse(marked, $"small.bin shows as read-only to {client.M.Name} over {kind}");
        }

        // writes
        var wrote = client.TryWriteRoundTrip(access, prefix, out var detail);
        if (kind == MountKind.Http)
        {
            Assert.IsFalse(wrote, "HTTP has no writes");
        }
        else if (readOnly)
        {
            Assert.IsFalse(wrote, $"a read-only membership refuses writes, but: {detail}");
        }
        else
        {
            Assert.IsTrue(wrote, $"write round trip (create, write, rename, read back, delete): {detail}");
        }

        // eight readers at once
        var hashes = client.ConcurrentSha256(access, Rel("medium.bin"), 8);
        Assert.AreEqual(8, hashes.Count, "eight concurrent readers answered: " + string.Join(", ", hashes));
        Assert.IsTrue(hashes.All(h => h == Fixture.Sha256Of("medium.bin")), "every concurrent reader got the file: " + string.Join(", ", hashes));

        // the resumable download of spec 8.6
        if (kind == MountKind.Http)
        {
            Assert.AreEqual(Fixture.Sha256Of("large.bin"), client.ResumedDownloadSha256(access, Rel("large.bin")), "curl -C - resumes");
            if (client is LinuxClient linux)
            {
                Assert.AreEqual(Fixture.Sha256Of("large.bin"), linux.WgetResumedSha256(access, Rel("large.bin")), "wget -c resumes");
            }
        }
    }

    protected void CheckCountsReachTheFixture()
    {
        var d = Current();
        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (true)
        {
            var snapshot = d.AnyFs!.Status()!;
            var sources = d.AnyFs.Sources.Select(s => snapshot.Collections.Single(c => c.Name == s.Collection).Sources.Single()).ToList();

            // a GitLab source counts each project's default branch alone, a lower bound ("+"), never complete
            var lowerBound = d.AnyFs.Sources.Where(s => s.Type == "gitlab").Select(s => s.Collection).ToHashSet(StringComparer.Ordinal);
            if (sources.Select((s, i) => (s, Collection: d.AnyFs.Sources[i].Collection)).All(x => (x.s.FilesComplete || lowerBound.Contains(x.Collection)) && x.s.Files >= Fixture.ManifestLines))
            {
                Console.WriteLine(string.Join("\n", sources.Select(s => $"{s.Name}: {s.Files:N0} files, complete")));
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("counts: " + string.Join("; ", sources.Select(s => $"{s.Name}: {s.Files} files, complete={s.FilesComplete}, {s.State} {s.Text}")));
            }

            Thread.Sleep(2000);
        }
    }

    protected void CheckPromptNeedingSourceFailsCleanly()
    {
        var d = Current();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        Core.Status.SourceStatus? source = null;
        while (DateTime.UtcNow < deadline)
        {
            source = d.AnyFs!.Status()?.Collections.SingleOrDefault(c => c.Name == "prompt")?.Sources.SingleOrDefault();
            if (source is { State: "Failed" })
            {
                break;
            }

            Thread.Sleep(2000);
        }

        Assert.IsNotNull(source, "the prompt collection is in the status");
        Assert.AreEqual("Failed", source.State, $"{source.State}: {source.Text}");
        StringAssert.Contains(source.Text, "Needs input", "headless anyfs can't answer the login prompt and says so");
    }

    protected void CheckSamplePluginLoaded()
    {
        var d = Current();
        var plugins = d.AnyFs!.Status()!.Plugins;
        var sample = plugins.SingleOrDefault(p => p.TypeId == "sample");
        Assert.IsNotNull(sample, "the sample plugin is listed: " + string.Join(", ", plugins.Select(p => $"{p.TypeId} ({p.Origin}{(p.Error is null ? "" : ": " + p.Error)})")));
        Assert.IsNull(sample.Error, sample.Error);
        StringAssert.Contains(sample.Origin, "AnyFS.SamplePlugin.dll");
    }

    protected void CheckUpstreamOutageDegradesThenRecovers()
    {
        var d = Current();
        var anyfs = d.AnyFs!;
        var host = d.Host!;
        string State() => anyfs.Status()?.Collections.Single(c => c.Name == "outage").Sources.Single() is { } s ? $"{s.State}|{s.Text}" : "?";
        try
        {
            if (host.IsWindows)
            {
                host.Sh($"Move-Item '{anyfs.OutageDir}' '{anyfs.OutageDir}.off'");
            }
            else
            {
                host.Sh($"mv '{anyfs.OutageDir}' '{anyfs.OutageDir}.off'");
            }

            Assert.IsTrue(WaitFor(() => State() is var s && (s.StartsWith("Degraded", StringComparison.Ordinal) || s.StartsWith("Failed", StringComparison.Ordinal)), 60), "the source notices the folder going away: " + State());
        }
        finally
        {
            if (host.IsWindows)
            {
                host.Run($"if (Test-Path '{anyfs.OutageDir}.off') {{ Move-Item '{anyfs.OutageDir}.off' '{anyfs.OutageDir}' }}");
            }
            else
            {
                host.Run($"[ ! -d '{anyfs.OutageDir}.off' ] || mv '{anyfs.OutageDir}.off' '{anyfs.OutageDir}'");
            }
        }

        Assert.IsTrue(WaitFor(() => State().StartsWith("Ready", StringComparison.Ordinal), 60), "the source recovers when the folder is back: " + State());
        using var access = d.Local!.Attach(MountKind.Http, anyfs, "outage", local: true);
        var prefix = AnyFsHost.TreePrefixOfLocalPath(anyfs.OutageDir);
        Assert.AreEqual(Fixture.Sha256Of("small.bin"), d.Local.Sha256(access, prefix + "/small.bin"), "and serves again");
    }

    protected void CheckLogHasNoErrors()
    {
        var d = Current();
        // the prompt-needing source fails on purpose (its row above checks that), so its error is expected
        var errors = d.AnyFs!.Log().Split('\n').Where(l => l.Contains(" ERROR ", StringComparison.Ordinal) && !l.Contains("AnyFS.Source.prompt ", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(0, errors.Count, "ERROR lines in anyfs.log:\n" + string.Join("\n", errors.Take(10)));
    }

    private static bool WaitFor(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(1500);
        }

        return condition();
    }
}

[TestClass]
public class LinuxHostRows : HostRows
{
    protected override string Os => "linux";

    [ClassInitialize]
    public static void Init(TestContext _) => Prepare("linux");

    [ClassCleanup]
    public static void Done() => Shutdown("linux");

    public static IEnumerable<object[]> Rows() => RowsFor("linux");

    [TestMethod]
    [DynamicData(nameof(Rows))]
    [Timeout(900_000)]
    public void Row(string collection, MountKind mount, string client) => RunRow(collection, mount, client);

    [TestMethod]
    public void CountsReachTheFixture() => CheckCountsReachTheFixture();

    [TestMethod]
    public void PromptNeedingSourceFailsCleanlyHeadless() => CheckPromptNeedingSourceFailsCleanly();

    [TestMethod]
    public void SamplePluginLoadsInTheTrimmedBuild() => CheckSamplePluginLoaded();

    [TestMethod]
    public void UpstreamOutageDegradesThenRecovers() => CheckUpstreamOutageDegradesThenRecovers();

    [TestMethod]
    public void ZLogHasNoErrors() => CheckLogHasNoErrors();
}

[TestClass]
public class WindowsHostRows : HostRows
{
    protected override string Os => "windows";

    [ClassInitialize]
    public static void Init(TestContext _) => Prepare("windows");

    [ClassCleanup]
    public static void Done() => Shutdown("windows");

    public static IEnumerable<object[]> Rows() => RowsFor("windows");

    [TestMethod]
    [DynamicData(nameof(Rows))]
    [Timeout(900_000)]
    public void Row(string collection, MountKind mount, string client) => RunRow(collection, mount, client);

    [TestMethod]
    public void CountsReachTheFixture() => CheckCountsReachTheFixture();

    [TestMethod]
    public void PromptNeedingSourceFailsCleanlyHeadless() => CheckPromptNeedingSourceFailsCleanly();

    [TestMethod]
    public void SamplePluginLoadsInTheTrimmedBuild() => CheckSamplePluginLoaded();

    [TestMethod]
    public void UpstreamOutageDegradesThenRecovers() => CheckUpstreamOutageDegradesThenRecovers();

    [TestMethod]
    public void ZLogHasNoErrors() => CheckLogHasNoErrors();
}

[TestClass]
public class MacHostRows : HostRows
{
    protected override string Os => "macos";

    [ClassInitialize]
    public static void Init(TestContext _) => Prepare("macos");

    [ClassCleanup]
    public static void Done() => Shutdown("macos");

    public static IEnumerable<object[]> Rows() => RowsFor("macos");

    [TestMethod]
    [DynamicData(nameof(Rows))]
    [Timeout(900_000)]
    public void Row(string collection, MountKind mount, string client) => RunRow(collection, mount, client);

    [TestMethod]
    public void CountsReachTheFixture() => CheckCountsReachTheFixture();

    [TestMethod]
    public void PromptNeedingSourceFailsCleanlyHeadless() => CheckPromptNeedingSourceFailsCleanly();

    [TestMethod]
    public void SamplePluginLoadsInTheTrimmedBuild() => CheckSamplePluginLoaded();

    [TestMethod]
    public void UpstreamOutageDegradesThenRecovers() => CheckUpstreamOutageDegradesThenRecovers();

    [TestMethod]
    public void ZLogHasNoErrors() => CheckLogHasNoErrors();
}
