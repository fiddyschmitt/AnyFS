using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnyFS.Fixtures.SmbStress;

namespace LibSmb.E2E;

/// <summary>
/// The SMB stress run, portable-smb-server's TestStress ported: every client machine at once hammers one host's smbdata
/// share for a set time with SmbStress (tests/fixtures/SmbStress): small-file churn, medium and large files with reads at
/// random offsets, publish-by-rename, overwrites, appends and truncates in place, trees with renames, listing checks, a
/// shared corpus, and reads of what the other clients published. Every byte read is checked against deterministic content.
/// Byte-perfect results alone are not enough, since SMB clients reconnect and retry without a word, which can hide a server
/// that drops connections: the run also fails on reconnect or signing complaints in the clients' own logs, a stalled
/// operation, anyfs stopping or not giving its memory back, and the audit of the host's folder (every published file there
/// and byte-exact, nothing left over). The Linux clients mount with 2.1 and 3.02 in turn, so both signing algorithms
/// (HMAC-SHA256, AES-CMAC) are under load at once. anyfs's own log is the class's ZLogHasNoErrors.
/// </summary>
public static partial class SmbStressRun
{
    /// <summary>How long the load runs (default 120 s).</summary>
    public const string SecondsVariable = "ANYFS_E2E_SMB_STRESS_SECONDS";

    /// <summary>Workers per client (default 4): the requests in flight together on each client's connection.</summary>
    public const string WorkersVariable = "ANYFS_E2E_SMB_STRESS_WORKERS";

    /// <summary>The largest large file in MiB (default 32).</summary>
    public const string LargeMbVariable = "ANYFS_E2E_SMB_STRESS_LARGE_MB";

    /// <summary>A run's seed, to repeat it (default random).</summary>
    public const string SeedVariable = "ANYFS_E2E_SMB_STRESS_SEED";

    private const int OpTimeoutSeconds = 120;

    /// <summary>The 4 KiB requests in one of SmbStress's randio operations.</summary>
    private const int RandIoCount = 64;

    [GeneratedRegex(@"(?i)reconnect|not respond|signature|sign fail|send error|error -\d+|status code returned|invalid|overflow|unmatched")]
    private static partial Regex LinuxBad();

    [GeneratedRegex(@"(?i)bad smb|signature|sign.*fail")]
    private static partial Regex MacBad();

    [GeneratedRegex(@"SESSION_RECONNECT_COUNT\s+(\d+)")]
    private static partial Regex MacReconnects();

    private static int Setting(string variable, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(variable), out var v) && v > 0 ? v : fallback;

    private sealed class StressClient(string name, Machine machine, bool local)
    {
        public string Name { get; } = name;

        public Machine Machine { get; } = machine;

        public bool Local { get; } = local;

        public string? Worker { get; set; }

        public string? Vers { get; set; }

        public SmbMount? Mount { get; set; }

        public string? Root { get; set; }

        public RunResult? Result { get; set; }

        public string? Trouble { get; set; }
    }

    public static void Run(AnyFsHost anyfs, IReadOnlyDictionary<string, Machine> clients)
    {
        var seconds = Setting(SecondsVariable, 120);
        var workers = Setting(WorkersVariable, 4);
        var largeMb = Setting(LargeMbVariable, 32);
        var seed = (ulong)Setting(SeedVariable, Random.Shared.Next(1, 1 << 30));
        var host = anyfs.Host;
        var report = new StringBuilder();
        var problems = new List<string>();
        var infrastructure = new List<string>();

        void Say(string line)
        {
            report.AppendLine(line);
            Console.WriteLine(line);
        }

        // the worker on every machine, the host's own first (it seeds the corpus and audits)
        var members = clients.Select(c => new StressClient(c.Key, c.Value, c.Key == "local")).ToList();
        var machines = members.Select(m => m.Machine).Append(host).Distinct().ToList();
        var workerPaths = new Dictionary<Machine, string>();
        Parallel.ForEach(machines, m =>
        {
            try
            {
                var path = DeployWorker(m);
                lock (workerPaths)
                {
                    workerPaths[m] = path;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or Renci.SshNet.Common.SshException)
            {
                lock (infrastructure)
                {
                    infrastructure.Add($"{m.Name}: deploying SmbStress: {SmbOps.Tail(ex.Message, 400)}");
                }
            }
        });
        Assert.IsTrue(workerPaths.ContainsKey(host), "no SmbStress on the host: " + string.Join("; ", infrastructure));
        foreach (var m in members)
        {
            m.Worker = workerPaths.GetValueOrDefault(m.Machine);
        }

        var folder = $"stress-{seed}";
        var backing = SmbOps.Join(host, anyfs.SmbDataDir, folder);
        Invoke(host, workerPaths[host], $"seed --dir {SmbOps.Q(host, backing)} --seed {seed}", 600).Must("seeding the corpus");
        var restKiB = AnyFsResidentKiB(anyfs);
        Say($"=== STRESS  anyfs's SMB server on {host.Name} ({host.Ip})  |  {seconds} s  |  seed {seed}  |  {workers} workers per client, large files up to {largeMb} MiB ===");
        Say($"    anyfs at rest: {restKiB >> 10} MiB resident");

        try
        {
            // the mounts: the Linux clients take 2.1 and 3.02 in turn (HMAC-SHA256 and AES-CMAC signing); Windows and macOS
            // negotiate 3.0.2
            var linuxDialects = new Queue<string>(["2.1", "3.02"]);
            foreach (var m in members.Where(m => m.Worker is not null))
            {
                if (m.Machine.IsLinux)
                {
                    m.Vers = linuxDialects.Dequeue();
                    linuxDialects.Enqueue(m.Vers);
                    SmbOps.RunLax(m.Machine, "sudo dmesg -C");
                }

                SmbMount.ForceUnmountAll(m.Machine);
                m.Mount = new SmbMount(m.Machine, m.Local ? "127.0.0.1" : host.Ip, "smbdata", AnyFsHost.SmbPort) { Tag = "U", Vers = m.Vers };
                var r = m.Mount.TryMount();
                if (!r.Ok)
                {
                    var why = m.Machine.IsWindows && !m.Local && r.Output.Contains("System error 67", StringComparison.Ordinal)
                        ? "net use failed with error 67 before reaching the server (WinFsp.Np first in the network provider order)"
                        : $"mount failed (exit {r.Code}): {SmbOps.Tail(r.Output, 300)}";
                    infrastructure.Add($"{m.Name}: {why}");
                    m.Mount = null;
                    continue;
                }

                m.Root = SmbOps.Join(m.Machine, m.Mount.Root!, [.. AnyFsHost.TreePrefixOfLocalPath(anyfs.SmbDataDir).Split('/'), folder]);
            }

            var running = members.Where(m => m.Root is not null).ToList();
            Say($"    {running.Count} clients at once: {string.Join(", ", running.Select(m => m.Name + (m.Vers is null ? "" : $" (SMB {m.Vers})")))}");
            if (running.Count < 2)
            {
                Assert.Inconclusive($"fewer than two clients could mount the share: {string.Join("; ", infrastructure)}");
            }

            // the load
            var loadStarted = Stopwatch.StartNew();
            Parallel.ForEach(running, m =>
            {
                var args = $"run --root {SmbOps.Q(m.Machine, m.Root!)} --name {m.Name} --seed {seed} --seconds {seconds} --workers {workers} --large-max-mb {largeMb} --op-timeout {OpTimeoutSeconds}";
                var r = Invoke(m.Machine, m.Worker!, args, seconds + OpTimeoutSeconds + 180);
                var line = SmbOps.Lines(r.Output).LastOrDefault(l => l.TrimStart().StartsWith('{'));
                if (line is null)
                {
                    m.Trouble = $"no result from SmbStress (exit {r.Code}): {SmbOps.Tail(r.Output, 600)}";
                    return;
                }

                try
                {
                    m.Result = JsonSerializer.Deserialize(line.Trim(), StressJson.Default.RunResult);
                }
                catch (JsonException ex)
                {
                    m.Trouble = "an unreadable result from SmbStress: " + ex.Message;
                }
            });

            // the workers' own verdicts
            Say("");
            Say($"    {"client",-18}{"ops",8}{"written",12}{"read",12}{"failures",10}{"stalls",8}");
            var done = new List<RunResult>();
            foreach (var m in running)
            {
                if (m.Result is not { } r)
                {
                    Say($"    {m.Name,-18}  -- {m.Trouble}");
                    infrastructure.Add($"{m.Name}: {m.Trouble}");
                    continue;
                }

                done.Add(r);
                var hung = r.CleanExit ? "" : "   HUNG: " + string.Join("; ", r.Stuck);
                Say($"    {m.Name,-18}{r.TotalOps,8}{r.BytesWritten >> 20,9} MiB{r.BytesRead >> 20,9} MiB{r.FailureCount,10}{r.Stalls,8}{hung}");
                if (r.FailureCount > 0 || !r.CleanExit)
                {
                    problems.Add($"{m.Name}: {r.FailureCount} failure(s), {r.Stalls} stall(s){hung}");
                    problems.AddRange(r.Failures.Take(5).Select(f => $"    {m.Name} [{f.Time}] {f.Op} {f.Path}: {SmbOps.Tail(f.Err, 400)}"));
                }
            }

            if (done.Count > 0)
            {
                long ops = done.Sum(r => r.TotalOps), written = done.Sum(r => r.BytesWritten), read = done.Sum(r => r.BytesRead);
                Say($"    {"TOTAL",-18}{ops,8}{written >> 20,9} MiB{read >> 20,9} MiB      = {ops / (double)seconds:0.0} ops/s, {(written + read) / 1048576.0 / seconds:0.0} MiB/s checked, from {done.Count} clients");
                Say("");
                Say($"    {"operation",-14}{"count",8}{"errors",8}{"p50 ms",10}{"p95 ms",10}{"max ms",10}    (p50/p95: the worst client's)");
                foreach (var op in done.SelectMany(r => r.Ops.Keys).Distinct().Order(StringComparer.Ordinal))
                {
                    var stats = done.Select(r => r.Ops.GetValueOrDefault(op)).OfType<OpStats>().ToList();
                    double p50 = stats.Max(s => s.P50Ms), p95 = stats.Max(s => s.P95Ms), max = stats.Max(s => s.MaxMs);
                    Say($"    {op,-14}{stats.Sum(s => s.Count),8}{stats.Sum(s => s.Errors),8}{p50,10:0}{p95,10:0}{max,10:0}");
                    if (op == "randio")
                    {
                        // 64 requests of 4 KiB each: the round trip of one, as an application with a file open sees it
                        Say($"    {"  per request",-14}{"",8}{"",8}{p50 / RandIoCount,10:0.0}{p95 / RandIoCount,10:0.0}{max / RandIoCount,10:0.0}    (one 4 KiB request: randio / 64)");
                    }
                }
            }

            // the clients' own logs for the time under load, before the unmounts (macOS logs a burst of reconnects on every
            // unmount, from the healthiest of servers)
            Say("");
            var window = Math.Max(1, (int)loadStarted.Elapsed.TotalSeconds - 5);
            foreach (var m in running)
            {
                var (lines, bad) = ClientLogProblems(m, host.Ip, window);
                Say($"    {m.Name} client log: {lines.Count} SMB line(s), {bad.Count} suspicious");
                foreach (var l in bad.Take(10))
                {
                    Say("        ! " + SmbOps.Tail(l, 300));
                    problems.Add($"{m.Name} client log: {SmbOps.Tail(l, 300)}");
                }
            }

            foreach (var m in running)
            {
                m.Mount!.Unmount();
            }

            // anyfs: still up, and back down in memory
            Say("");
            if (!AnyFsRunning(anyfs))
            {
                problems.Add($"anyfs stopped under the load; its log ends:\n{anyfs.LogTail(20)}");
            }
            else
            {
                Thread.Sleep(10_000);
                var afterKiB = AnyFsResidentKiB(anyfs);
                Say($"    anyfs after the load: {afterKiB >> 10} MiB resident (at rest: {restKiB >> 10} MiB)");
                if (afterKiB > (restKiB * 4) + (200 << 10))
                {
                    problems.Add($"anyfs holds {afterKiB >> 10} MiB after the load, from {restKiB >> 10} MiB at rest: a leak?");
                }
            }

            // the audit of the share's folder, on the host
            var manifest = done.SelectMany(r => r.Expected).ToList();
            var manifestPath = anyfs.SmbDataDir + "-stress-manifest.json";
            host.UploadText(JsonSerializer.Serialize(manifest, StressJson.Default.ListExpectedFile), SmbOps.SftpPath(host, manifestPath));
            var a = Invoke(host, workerPaths[host], $"audit --dir {SmbOps.Q(host, backing)} --seed {seed} --manifest {SmbOps.Q(host, manifestPath)}", 1800);
            SmbOps.Remove(host, manifestPath);
            var auditLine = SmbOps.Lines(a.Output).LastOrDefault(l => l.TrimStart().StartsWith('{'));
            if (auditLine is null || JsonSerializer.Deserialize(auditLine.Trim(), StressJson.Default.AuditResult) is not { } audit)
            {
                infrastructure.Add($"the audit gave no result (exit {a.Code}): {SmbOps.Tail(a.Output, 400)}");
            }
            else
            {
                Say($"    audit of the share's folder: {audit.Verified} files / {audit.Bytes >> 20} MiB checked byte for byte ({manifest.Count} published by the clients + the corpus); {audit.Mismatches.Count} mismatched, {audit.Missing.Count} missing, {audit.Strays.Count} stray");
                problems.AddRange(audit.Mismatches.Take(10).Select(m => "audit: " + m));
                problems.AddRange(audit.Missing.Take(10).Select(m => "audit: a published file is missing from the host's folder: " + m));
                var allClean = done.Count == running.Count && done.All(r => r.FailureCount == 0 && r.CleanExit);
                if (audit.Strays.Count > 0)
                {
                    Say("        strays: " + string.Join(", ", audit.Strays.Take(8)));
                    if (allClean)
                    {
                        problems.Add($"audit: {audit.Strays.Count} file(s) left on the host after a clean run, such as {string.Join(", ", audit.Strays.Take(5))}");
                    }
                }
            }
        }
        finally
        {
            Parallel.ForEach(members, m => m.Mount?.Unmount());
            SmbOps.Remove(host, backing);
        }

        if (problems.Count > 0)
        {
            Say("\nPROBLEMS:\n  " + string.Join("\n  ", problems));
        }

        if (infrastructure.Count > 0)
        {
            Say("\nINFRASTRUCTURE:\n  " + string.Join("\n  ", infrastructure));
        }

        Assert.AreEqual(0, problems.Count, $"{problems.Count} problem(s):\n  " + string.Join("\n  ", problems));
        if (infrastructure.Count > 0)
        {
            Assert.Inconclusive("no failures, but not every client took part: " + string.Join("; ", infrastructure));
        }
    }

    /// <summary>Copies SmbStress to a machine from the publish folder (provisioning/publish.ps1 builds it beside MakeTree): its path there.</summary>
    private static string DeployWorker(Machine m)
    {
        switch (m.Info.Os)
        {
            case "windows":
            {
                const string exe = @"C:\anyfs-lab\SmbStress.exe";
                m.Sh($@"$ErrorActionPreference = 'Stop'; Get-Process SmbStress -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -eq '{exe}' }} | Stop-Process -Force; New-Item -ItemType Directory -Force 'C:\anyfs-lab' | Out-Null; Copy-Item '\\vboxsvr\publish\win-x64\SmbStress.exe' '{exe}' -Force", 300);
                return exe;
            }

            case "macos":
            {
                var dir = $"/Users/{m.Info.User}/anyfs-lab";
                m.Run($"pkill -f '{dir}/[S]mbStress'");
                m.Sh($"mkdir -p '{dir}'");
                // a fresh file renamed into place: macOS may refuse to run a binary overwritten where it is
                m.Upload(Path.Combine(Lab.PublishDir, "osx-arm64", "SmbStress"), dir + "/SmbStress.new");
                m.Sh($"cd '{dir}' && chmod +x SmbStress.new && mv -f SmbStress.new SmbStress && (xattr -d com.apple.quarantine SmbStress 2>/dev/null; true)");
                return dir + "/SmbStress";
            }

            default:
            {
                var dir = $"/home/{m.Info.User}/anyfs-lab";
                var source = $"{Lab.LinuxPublishDir}/linux-x64/SmbStress";
                // a command of its own: pkill -f matches every command line, the shell's that runs it included, and the
                // bracket keeps its own pattern from matching itself but not the names further along a longer command
                m.Run($"pkill -f '{dir}/[S]mbStress'");
                m.Sh($"set -e; mkdir -p '{dir}'; test -f '{source}' || {{ echo 'no SmbStress in the publish folder: run provisioning/publish.ps1 on the PC' >&2; exit 2; }}; cp '{source}' '{dir}/SmbStress.new'; chmod +x '{dir}/SmbStress.new'; mv -f '{dir}/SmbStress.new' '{dir}/SmbStress'");
                return dir + "/SmbStress";
            }
        }
    }

    /// <summary>Runs SmbStress on a machine; its stderr (the progress lines) passes through untouched.</summary>
    private static SmbResult Invoke(Machine m, string exe, string args, int timeoutSeconds)
    {
        var (code, output) = m.Run(m.IsWindows ? $"& {SmbOps.Q(m, exe)} {args}; exit $LASTEXITCODE" : $"{SmbOps.Q(m, exe)} {args}", timeoutSeconds);
        return new SmbResult(code, output);
    }

    private static bool AnyFsRunning(AnyFsHost anyfs)
        => anyfs.Host.IsWindows
            ? SmbOps.Run(anyfs.Host, $"@(Get-Process anyfs -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -eq '{anyfs.Exe}' }}).Count").Output.Trim() != "0"
            : SmbOps.Run(anyfs.Host, $"pgrep -f '{anyfs.Exe} [s]erve' >/dev/null && echo up || echo down").Output.Trim() == "up";

    /// <summary>anyfs's resident memory on its host, KiB (0 when it can't be read).</summary>
    private static long AnyFsResidentKiB(AnyFsHost anyfs)
    {
        var r = anyfs.Host.IsWindows
            ? SmbOps.Run(anyfs.Host, $"[long]((Get-Process anyfs -ErrorAction SilentlyContinue | Where-Object {{ $_.Path -eq '{anyfs.Exe}' }} | Select-Object -First 1).WorkingSet64 / 1KB)")
            : SmbOps.Run(anyfs.Host, $"ps -o rss= -p $(pgrep -f '{anyfs.Exe} [s]erve' | head -1)");
        return long.TryParse(r.Output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var kib) ? kib : 0;
    }

    /// <summary>A client's SMB log for the last <paramref name="window"/> seconds: every line, and the suspicious ones.</summary>
    private static (IReadOnlyList<string> Lines, List<string> Bad) ClientLogProblems(StressClient c, string serverIp, int window)
    {
        var h = c.Machine;
        switch (h.Info.Os)
        {
            case "linux":
            {
                var lines = SmbOps.Lines(SmbOps.RunLax(h, "sudo dmesg | grep -iE 'cifs|smb' | tail -n 200; true").Output);
                return (lines, lines.Where(l => LinuxBad().IsMatch(l)).ToList());
            }

            case "macos":
            {
                // the session under test is the one behind this mount, and smbutil counts its reconnects; the smbfs log mixes in
                // every other session on the Mac, so it is read for protocol complaints only. (In zsh a bare `log` is a
                // builtin, hence the full path.)
                var shares = SmbOps.RunLax(h, $"smbutil statshares -m {SmbOps.Q(h, c.Mount!.Root!)} 2>&1; true").Output;
                var lines = SmbOps.Lines(SmbOps.RunLax(h, $"/usr/bin/log show --start \"$(date -v-{window}S '+%Y-%m-%d %H:%M:%S')\" --style compact --predicate 'sender == \"smbfs\"' 2>/dev/null | grep -v '^Timestamp' | sed -E 's/^[0-9-]+ [0-9:.]+ +[A-Za-z]+ +//; s/0x[0-9a-fA-F]+/0xN/g; s/[0-9]+/N/g' | sort | uniq -c | sort -rn | head -n 40; true", 600).Output);
                var bad = lines.Where(l => MacBad().IsMatch(l)).ToList();
                if (MacReconnects().Match(shares) is { Success: true } m && m.Groups[1].Value != "0")
                {
                    bad.Insert(0, $"the mount's session reconnected {m.Groups[1].Value} time(s) (smbutil statshares SESSION_RECONNECT_COUNT)");
                }

                return (lines, bad);
            }

            default:
            {
                var lines = SmbOps.Lines(SmbOps.RunLax(h, $$"""$since = (Get-Date).AddSeconds(-{{window}}); Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-SmbClient/Connectivity', 'Microsoft-Windows-SmbClient/Security', 'Microsoft-Windows-SmbClient/Operational'; StartTime = $since; Level = 1, 2, 3 } -ErrorAction SilentlyContinue | Select-Object -First 100 | ForEach-Object { '{0} id={1} {2}: {3}' -f $_.TimeCreated.ToString('HH:mm:ss'), $_.Id, $_.LevelDisplayName, ((($_.Message -split '\r?\n') | Where-Object { $_.Trim() }) -join ' | ') }""", 120).Output);
                var address = c.Local ? "127.0.0.1" : serverIp;
                return (lines, lines.Where(l => l.Contains(address, StringComparison.Ordinal)).ToList());
            }
        }
    }
}
