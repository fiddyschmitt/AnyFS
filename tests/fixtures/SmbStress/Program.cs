using System.Globalization;
using System.Text.Json;
using AnyFS.Fixtures.SmbStress;

// SmbStress: the load generator and checker behind LibSmb.E2E's stress run, ported from portable-smb-server's psmbstress
// (test/rig/worker). The harness can't load a server by driving file operations over SSH one command at a time, so it
// puts this on every client machine and lets it hammer the mounted share for a set time, checking every byte it reads:
//
//   SmbStress run --root <share folder> --name <client> --seed N --seconds S [--workers 4] [--large-max-mb 32]
//                 [--op-timeout 120] [--max-pub 12] [--out result.json]
//
// and on the host, to prepare and then check the share's folder:
//
//   SmbStress seed  --dir <folder> --seed N                          the shared read-only corpus
//   SmbStress audit --dir <folder> --seed N --manifest m.json        what the clients left behind
return args.FirstOrDefault() switch
{
    "run" => RunCommand(Options(args)),
    "seed" => SeedCommand(Options(args)),
    "audit" => AuditCommand(Options(args)),
    "version" => Print("SmbStress 1"),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: SmbStress run|seed|audit|version [--name value]...");
    return 2;
}

static int Print(string text)
{
    Console.WriteLine(text);
    return 0;
}

static Dictionary<string, string> Options(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i + 1 < args.Length; i += 2)
    {
        options[args[i].TrimStart('-')] = args[i + 1];
    }

    return options;
}

static int Int(Dictionary<string, string> options, string name, int fallback)
    => options.TryGetValue(name, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

static ulong Seed(Dictionary<string, string> options) => options.TryGetValue("seed", out var v) ? ulong.Parse(v, CultureInfo.InvariantCulture) : 1;

static int RunCommand(Dictionary<string, string> options)
{
    if (!options.TryGetValue("root", out var root))
    {
        Console.Error.WriteLine("run: --root is required");
        return 2;
    }

    var settings = new Settings(
        root,
        options.GetValueOrDefault("name", "client"),
        Seed(options),
        TimeSpan.FromSeconds(Int(options, "seconds", 300)),
        Int(options, "workers", 4),
        TimeSpan.FromSeconds(Int(options, "op-timeout", 120)),
        Int(options, "large-max-mb", 32),
        Int(options, "max-pub", 12));
    var recorder = new Recorder();
    var workers = Enumerable.Range(0, settings.Workers).Select(i => new Worker(settings, recorder, i)).ToList();
    var started = DateTime.UtcNow;
    var deadline = started + settings.Duration;
    using var stop = new CancellationTokenSource();
    var watch = recorder.WatchAsync(settings.OpTimeout, stop.Token);
    var progress = ProgressAsync(recorder, stop.Token);
    var threads = workers.Select(w => new Thread(() => w.Run(deadline)) { IsBackground = true }).ToList();
    threads.ForEach(t => t.Start());

    // a worker blocked on a hung mount never comes back: give up on it after a grace period, so the run still reports (as
    // a failure) rather than hang the harness
    var giveUp = deadline + settings.OpTimeout + TimeSpan.FromSeconds(30);
    var clean = threads.All(t => t.Join(giveUp - DateTime.UtcNow is { Ticks: > 0 } left ? left : TimeSpan.Zero));
    stop.Cancel();
    Task.WaitAll(watch, progress);

    var (ops, failures, failureCount, stalls) = recorder.Snapshot();
    var result = new RunResult
    {
        Name = settings.Name,
        Os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux",
        Seed = settings.Seed,
        Workers = settings.Workers,
        DurationSec = (DateTime.UtcNow - started).TotalSeconds,
        Ops = ops,
        Failures = failures,
        FailureCount = failureCount,
        Stalls = stalls,
        CleanExit = clean,
        TotalOps = ops.Values.Sum(s => s.Count),
        BytesWritten = ops.Values.Sum(s => s.BytesWritten),
        BytesRead = ops.Values.Sum(s => s.BytesRead),
    };
    if (clean)
    {
        // the workers' state is still only once every one of them has returned
        result.Expected = workers.SelectMany(w => w.Expected).ToList();
    }
    else
    {
        result.Stuck = recorder.Stuck();
    }

    var json = JsonSerializer.Serialize(result, StressJson.Default.RunResult);
    if (options.TryGetValue("out", out var output))
    {
        File.WriteAllText(output, json);
    }

    Console.WriteLine(json);
    Console.Out.Flush();
    var code = failureCount > 0 || !clean ? 1 : 0;
    if (!clean)
    {
        // the stuck threads would keep the process alive in a read that never returns
        Environment.Exit(code);
    }

    return code;
}

// a heartbeat on stderr, so a long run can be followed from the harness
static async Task ProgressAsync(Recorder recorder, CancellationToken stop)
{
    var started = DateTime.UtcNow;
    try
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stop).ConfigureAwait(false);
            var (count, failures) = recorder.Totals();
            Console.Error.WriteLine($"[{(DateTime.UtcNow - started).TotalSeconds,4:0}s] ops={count} failures={failures}");
        }
    }
    catch (OperationCanceledException)
    {
    }
}

static int SeedCommand(Dictionary<string, string> options)
{
    if (!options.TryGetValue("dir", out var dir))
    {
        Console.Error.WriteLine("seed: --dir is required");
        return 2;
    }

    var seed = Seed(options);
    Directory.CreateDirectory(Path.Combine(dir, "corpus"));
    foreach (var f in Worker.Corpus)
    {
        Worker.WriteFile(Path.Combine(dir, f.Rel.Replace('/', Path.DirectorySeparatorChar)), new Content(seed, f.Rel), f.Size, 1 << 20, false);
    }

    Console.WriteLine($"seeded {Worker.Corpus.Count} corpus files under {dir}");
    return 0;
}

// on the host itself: every file a client published (the manifest, gathered from the workers' results) is in the share's
// folder with exactly the expected bytes, the corpus is whole, and nothing else is left under stress/
static int AuditCommand(Dictionary<string, string> options)
{
    if (!options.TryGetValue("dir", out var dir))
    {
        Console.Error.WriteLine("audit: --dir is required");
        return 2;
    }

    var seed = Seed(options);
    List<ExpectedFile> expected = options.TryGetValue("manifest", out var manifest)
        ? JsonSerializer.Deserialize(File.ReadAllText(manifest), StressJson.Default.ListExpectedFile) ?? []
        : [];
    var want = expected.ToDictionary(e => e.Path, e => e.Size, StringComparer.Ordinal);
    foreach (var c in Worker.Corpus)
    {
        want[c.Rel] = c.Size;
    }

    var result = new AuditResult();
    foreach (var (rel, size) in want)
    {
        var path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            result.Missing.Add(rel);
            continue;
        }

        try
        {
            result.Bytes += Worker.VerifyFile(path, new Content(seed, rel), size);
            result.Verified++;
        }
        catch (IOException ex)
        {
            result.Mismatches.Add(ex.Message);
        }
    }

    // anything else under stress/ is a stray
    var stress = Path.Combine(dir, "stress");
    if (Directory.Exists(stress))
    {
        foreach (var file in Directory.EnumerateFiles(stress, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, file).Replace(Path.DirectorySeparatorChar, '/');
            if (!want.ContainsKey(rel) && !Worker.Ignored(Path.GetFileName(file)))
            {
                result.Strays.Add(rel);
            }
        }
    }

    result.Missing.Sort(StringComparer.Ordinal);
    result.Strays.Sort(StringComparer.Ordinal);
    Console.WriteLine(JsonSerializer.Serialize(result, StressJson.Default.AuditResult));
    return result.Mismatches.Count > 0 || result.Missing.Count > 0 ? 1 : 0;
}
