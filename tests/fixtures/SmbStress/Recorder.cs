using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;

namespace AnyFS.Fixtures.SmbStress;

/// <summary>What a worker reports per kind of operation.</summary>
public sealed class OpStats
{
    public long Count { get; set; }

    public long Errors { get; set; }

    public long BytesWritten { get; set; }

    public long BytesRead { get; set; }

    public double P50Ms { get; set; }

    public double P95Ms { get; set; }

    public double P99Ms { get; set; }

    public double MaxMs { get; set; }

    [JsonIgnore]
    internal List<double> Samples { get; } = [];
}

public sealed record Failure(string Time, int Worker, string Op, string Path, string Err);

/// <summary>A file a worker published: share-relative, slash-separated (its content key), and its size.</summary>
public sealed record ExpectedFile(string Path, long Size);

/// <summary>What <c>run</c> reports (the last line of its output).</summary>
public sealed class RunResult
{
    public string Name { get; set; } = "";

    public string Os { get; set; } = "";

    public ulong Seed { get; set; }

    public int Workers { get; set; }

    public double DurationSec { get; set; }

    public long TotalOps { get; set; }

    public long BytesWritten { get; set; }

    public long BytesRead { get; set; }

    public Dictionary<string, OpStats> Ops { get; set; } = [];

    public int FailureCount { get; set; }

    public int Stalls { get; set; }

    public List<string> Stuck { get; set; } = [];

    public List<Failure> Failures { get; set; } = [];

    public List<ExpectedFile> Expected { get; set; } = [];

    public bool CleanExit { get; set; }
}

/// <summary>What <c>audit</c> reports.</summary>
public sealed class AuditResult
{
    public int Verified { get; set; }

    public long Bytes { get; set; }

    public List<string> Mismatches { get; set; } = [];

    public List<string> Missing { get; set; } = [];

    public List<string> Strays { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RunResult))]
[JsonSerializable(typeof(AuditResult))]
[JsonSerializable(typeof(List<ExpectedFile>))]
public sealed partial class StressJson : JsonSerializerContext;

/// <summary>
/// Every worker's operations: counts, latencies, failures, and what is in flight. An operation past the timeout is a stall,
/// reported even when it returns in the end; one that never returns is a hang (test/rig/worker/stats.go).
/// </summary>
internal sealed class Recorder
{
    private const int MaxSamples = 20000;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, OpStats> _ops = [];
    private readonly List<Failure> _failures = [];
    private readonly Dictionary<int, (string Op, string Path, Stopwatch Since)> _inFlight = [];
    private readonly HashSet<int> _stalled = [];
    private int _failureCount;
    private int _stalls;

    private static string Now() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public void Begin(int worker, string op, string path)
    {
        lock (_lock)
        {
            _inFlight[worker] = (op, path, Stopwatch.StartNew());
        }
    }

    public void End(int worker, string op, string path, Stopwatch started, long written, long read, Exception? error)
    {
        lock (_lock)
        {
            _inFlight.Remove(worker);
            _stalled.Remove(worker);
            if (!_ops.TryGetValue(op, out var s))
            {
                _ops[op] = s = new OpStats();
            }

            s.Count++;
            s.BytesWritten += written;
            s.BytesRead += read;
            var ms = started.Elapsed.TotalMilliseconds;
            s.MaxMs = Math.Max(s.MaxMs, ms);
            if (s.Samples.Count < MaxSamples)
            {
                s.Samples.Add(ms);
            }
            else
            {
                // bounded, without favouring the start of the run
                s.Samples[(int)(s.Count % MaxSamples)] = ms;
            }

            if (error is not null)
            {
                s.Errors++;
                _failureCount++;
                if (_failures.Count < 50)
                {
                    _failures.Add(new Failure(Now(), worker, op, path, error.Message));
                }
            }
        }
    }

    /// <summary>Counts each operation running past the timeout once, until <paramref name="stop"/> is cancelled.</summary>
    public async Task WatchAsync(TimeSpan timeout, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
                lock (_lock)
                {
                    foreach (var (worker, op) in _inFlight)
                    {
                        if (op.Since.Elapsed > timeout && _stalled.Add(worker))
                        {
                            _stalls++;
                            _failureCount++;
                            if (_failures.Count < 50)
                            {
                                _failures.Add(new Failure(Now(), worker, op.Op, op.Path, $"STALL: running for {op.Since.Elapsed.TotalSeconds:0} s"));
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The operations still in flight, for a run that had to give up on its workers.</summary>
    public List<string> Stuck()
    {
        lock (_lock)
        {
            return _inFlight.Select(kv => $"{kv.Value.Since.Elapsed.TotalSeconds:0} s in {kv.Value.Op} {kv.Value.Path} (worker {kv.Key})").ToList();
        }
    }

    public (long Ops, int Failures) Totals()
    {
        lock (_lock)
        {
            return (_ops.Values.Sum(s => s.Count), _failureCount);
        }
    }

    /// <summary>The percentiles worked out, and everything recorded.</summary>
    public (Dictionary<string, OpStats> Ops, List<Failure> Failures, int FailureCount, int Stalls) Snapshot()
    {
        lock (_lock)
        {
            foreach (var s in _ops.Values.Where(s => s.Samples.Count > 0))
            {
                var sorted = s.Samples.Order().ToArray();
                double Percentile(double p) => sorted[(int)((sorted.Length - 1) * p)];
                (s.P50Ms, s.P95Ms, s.P99Ms) = (Percentile(0.50), Percentile(0.95), Percentile(0.99));
            }

            return (new Dictionary<string, OpStats>(_ops), [.. _failures], _failureCount, _stalls);
        }
    }
}
