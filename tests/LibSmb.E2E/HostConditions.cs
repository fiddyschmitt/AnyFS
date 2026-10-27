using System.Diagnostics;

namespace LibSmb.E2E;

/// <summary>One host machine's anyfs with the SMB conditions' shares, deployed once per test class, and the machines that reach it.</summary>
public sealed class Deployment : IDisposable
{
    public Machine? Host { get; set; }

    public Machine? Upstream { get; set; }

    /// <summary>The other OSes' host machines, connected as clients of this one.</summary>
    public List<Machine> Others { get; } = [];

    public AnyFsHost? AnyFs { get; set; }

    /// <summary>The clients by name: <c>local</c> is the host itself, <c>upstream</c> the Linux upstream, <c>windows</c> and <c>macos</c> the hosts of the other OSes.</summary>
    public Dictionary<string, Machine> Clients { get; } = [];

    /// <summary>Why a client's rows are Inconclusive: its machine can't be reached.</summary>
    public Dictionary<string, string> ClientUnavailable { get; } = [];

    /// <summary>Why the rows are Inconclusive: a machine that can't be reached.</summary>
    public string? Unavailable { get; set; }

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
/// portable-smb-server's rig, ported, against one lab host's anyfs with shares of its own over an empty folder
/// (<see cref="AnyFsHost.DeploySmbShares"/>): each condition (<see cref="SmbConditions"/>, the rig's TestMatrix) from every
/// client it applies to, the clients being every machine in the lab (the host itself, the Linux upstream and the hosts of
/// the other two OSes); then every client at once on one share (the rig's TestMultiClient), every client at once under
/// load (<see cref="SmbStressRun"/>, its TestStress), and anyfs's log.
/// </summary>
public abstract class HostConditions
{
    private static readonly Dictionary<string, Deployment> deployments = [];

    protected abstract string Os { get; }

    /// <summary>
    /// The clients of a host: itself, the Linux upstream, and the hosts of the other OSes; every machine in the lab but
    /// the one under test, so Windows and macOS are clients of the other hosts too, not only Linux.
    /// </summary>
    public static IReadOnlyList<string> ClientsOf(string os)
        => new[] { "local", "upstream" }.Concat(new[] { "windows", "macos" }.Where(o => o != os)).ToList();

    /// <summary>
    /// Comma-separated, case-insensitive lists that narrow the run to these conditions (<c>timestamps,guest_roundtrip</c>;
    /// <see cref="SeveralClients"/> and <see cref="Stress"/> name those two) or clients (<c>local,macos</c>), so a retest
    /// need not take the whole run's time (a test filter cannot pick data rows apart). Unset means all.
    /// </summary>
    public const string ConditionsVariable = "ANYFS_E2E_SMB_CONDITIONS";

    public const string ClientsVariable = "ANYFS_E2E_CLIENTS";

    /// <summary>Every client at once on one share, by its name for <see cref="ConditionsVariable"/>.</summary>
    public const string SeveralClients = "several_clients_at_once";

    /// <summary>The stress run, by its name for <see cref="ConditionsVariable"/>.</summary>
    public const string Stress = "stress";

    /// <summary>The rows: each condition × each client it applies to; with none selected, one row that says so (a data source may not be empty).</summary>
    public static IEnumerable<object[]> RowsFor(string os)
    {
        var rows = new List<object[]>();
        var conditions = Selection(ConditionsVariable);
        var clients = Selection(ClientsVariable);
        foreach (var condition in SmbConditions.All.Where(c => conditions is null || conditions.Contains(c.Name)))
        {
            foreach (var client in ClientsOf(os).Where(c => clients is null || clients.Contains(c)))
            {
                var clientOs = client switch { "local" => os, "upstream" => "linux", _ => client };
                if (condition.Only is null || condition.Only.Contains(clientOs))
                {
                    rows.Add([condition.Name, client]);
                }
            }
        }

        return rows.Count > 0 ? rows : [["-", "-"]];
    }

    private static HashSet<string>? Selection(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Connects to the host, the upstream and the other hosts, deploys anyfs with the shares and waits for them to be green. Once per class.</summary>
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
                d.AnyFs = new AnyFsHost(d.Host, d.Upstream);
                d.AnyFs.DeploySmbShares();
                d.AnyFs.Start();
                var status = d.AnyFs.WaitReady(AnyFsHost.SmbCollections);
                d.Clients["local"] = d.Host;
                d.Clients["upstream"] = d.Upstream;
                Trace.WriteLine($"{hostInfo.Name}: anyfs {status.Version} up with the SMB conditions' shares in {sw.Elapsed.TotalSeconds:N0} s");

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
                        d.Clients[other] = machine;
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

    /// <summary>One condition, from one client, against this host's anyfs.</summary>
    protected void RunCondition(string condition, string clientName)
    {
        if (condition == "-")
        {
            Assert.Inconclusive($"no SMB condition is selected ({ConditionsVariable}, {ClientsVariable})");
        }

        var d = Current();
        if (!d.Clients.TryGetValue(clientName, out var client))
        {
            Assert.Inconclusive(d.ClientUnavailable.GetValueOrDefault(clientName) ?? $"no client named {clientName}");
            return;
        }

        Console.WriteLine($"{condition} from {client.Name} against {d.Host!.Name}");
        new SmbPair(d.AnyFs!, client, clientName == "local").Run(SmbConditions.Named(condition).Run);
    }

    /// <summary>Every client at once against this host's anyfs (<see cref="SmbConditions.SeveralClientsAtOnce"/>).</summary>
    protected void RunSeveralClients()
    {
        if (Selection(ConditionsVariable) is { } conditions && !conditions.Contains(SeveralClients))
        {
            Assert.Inconclusive($"left out by {ConditionsVariable}");
        }

        var d = Current();
        SmbConditions.SeveralClientsAtOnce(d.AnyFs!, SelectedClients(d));
    }

    /// <summary>Every client at once under load against this host's anyfs (<see cref="SmbStressRun"/>).</summary>
    protected void RunStress()
    {
        if (Selection(ConditionsVariable) is { } conditions && !conditions.Contains(Stress))
        {
            Assert.Inconclusive($"left out by {ConditionsVariable}");
        }

        var d = Current();
        SmbStressRun.Run(d.AnyFs!, SelectedClients(d));
    }

    protected void CheckLogHasNoErrors()
    {
        var d = Current();
        var errors = d.AnyFs!.Log().Split('\n').Where(l => l.Contains(" ERROR ", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(0, errors.Count, "ERROR lines in anyfs.log:\n" + string.Join("\n", errors.Take(10)));
    }

    private static Dictionary<string, Machine> SelectedClients(Deployment d)
    {
        var clients = Selection(ClientsVariable);
        return d.Clients.Where(c => clients is null || clients.Contains(c.Key)).ToDictionary(c => c.Key, c => c.Value);
    }
}

[TestClass]
public class LinuxHost : HostConditions
{
    protected override string Os => "linux";

    [ClassInitialize]
    public static void Init(TestContext _) => Prepare("linux");

    [ClassCleanup]
    public static void Done() => Shutdown("linux");

    public static IEnumerable<object[]> Rows() => RowsFor("linux");

    [TestMethod]
    [DynamicData(nameof(Rows))]
    [Timeout(1_800_000)]
    public void Condition(string condition, string client) => RunCondition(condition, client);

    [TestMethod]
    [Timeout(900_000)]
    public void SeveralClientsAtOnce() => RunSeveralClients();

    [TestMethod]
    [Timeout(3_600_000)]
    public void StressFromEveryClient() => RunStress();

    [TestMethod]
    public void ZLogHasNoErrors() => CheckLogHasNoErrors();
}

[TestClass]
public class WindowsHost : HostConditions
{
    protected override string Os => "windows";

    [ClassInitialize]
    public static void Init(TestContext _) => Prepare("windows");

    [ClassCleanup]
    public static void Done() => Shutdown("windows");

    public static IEnumerable<object[]> Rows() => RowsFor("windows");

    [TestMethod]
    [DynamicData(nameof(Rows))]
    [Timeout(1_800_000)]
    public void Condition(string condition, string client) => RunCondition(condition, client);

    [TestMethod]
    [Timeout(900_000)]
    public void SeveralClientsAtOnce() => RunSeveralClients();

    [TestMethod]
    [Timeout(3_600_000)]
    public void StressFromEveryClient() => RunStress();

    [TestMethod]
    public void ZLogHasNoErrors() => CheckLogHasNoErrors();
}

[TestClass]
public class MacHost : HostConditions
{
    protected override string Os => "macos";

    [ClassInitialize]
    public static void Init(TestContext _) => Prepare("macos");

    [ClassCleanup]
    public static void Done() => Shutdown("macos");

    public static IEnumerable<object[]> Rows() => RowsFor("macos");

    [TestMethod]
    [DynamicData(nameof(Rows))]
    [Timeout(1_800_000)]
    public void Condition(string condition, string client) => RunCondition(condition, client);

    [TestMethod]
    [Timeout(900_000)]
    public void SeveralClientsAtOnce() => RunSeveralClients();

    [TestMethod]
    [Timeout(3_600_000)]
    public void StressFromEveryClient() => RunStress();

    [TestMethod]
    public void ZLogHasNoErrors() => CheckLogHasNoErrors();
}
