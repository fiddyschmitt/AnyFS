using System.Collections.Concurrent;

namespace AnyFS.Core.Operations;

/// <summary>
/// Every operation asked of every source, and everything each source asked of its server, as it runs and for a while after
/// it ends (the Operations tab), and counters per source and kind for the session (<c>anyfs status --operations</c>,
/// status.json, <see cref="Stats"/>). Sources are recorded by the <see cref="TrackedSource"/> the runner wraps them in, so
/// nothing a source does is missed and no source has to know. Cheap enough to be always on: a begin and an end take a short
/// lock each, and the counters are atomic.
/// </summary>
public sealed class OperationTracker
{
    public static readonly TimeSpan DefaultKeepFinishedFor = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<long, Operation> _running = [];

    // by who was asked, each with the cap to itself: a storm of quick calls to AnyFS answered from what is kept asks no server, and leaves what the servers were asked in sight
    private readonly Queue<Operation>[] _finished = [new(), new()];
    private readonly ConcurrentDictionary<Guid, SourceCounters> _counters = new();
    private long _nextId;

    /// <param name="keepFinishedFor">How long a finished operation stays in <see cref="Recent"/>: five seconds by default.</param>
    /// <param name="keepFinishedAtMost">A cap on finished operations kept, of those asked of AnyFS and of those asked of a server each, for a storm of quick calls; the oldest go first.</param>
    public OperationTracker(TimeProvider? time = null, TimeSpan? keepFinishedFor = null, int keepFinishedAtMost = 5000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keepFinishedAtMost, 1);
        _time = time ?? TimeProvider.System;
        KeepFinishedFor = keepFinishedFor ?? DefaultKeepFinishedFor;
        KeepFinishedAtMost = keepFinishedAtMost;
    }

    public TimeSpan KeepFinishedFor { get; }

    public int KeepFinishedAtMost { get; }

    /// <summary>Raised on the thread that ended the operation, with it as it ended: for logging and tests.</summary>
    public event Action<OperationRecord>? Finished;

    /// <summary>Records an operation that has just begun; end it on the <see cref="Operation"/> returned.</summary>
    /// <param name="what">What a server is being asked, in a word or two (GET, SFTP list); null for what is named by its kind alone.</param>
    public Operation Begin(Guid sourceId, string source, OperationKind kind, string path, string? detail = null, string? what = null)
    {
        var op = new Operation(this, Interlocked.Increment(ref _nextId), sourceId, source, kind, path, detail, what, _time.GetUtcNow(), _time.GetTimestamp());
        var counters = _counters.GetOrAdd(sourceId, _ => new SourceCounters());
        counters.Name = source;
        counters.For(kind).Begun();
        lock (_gate)
        {
            _running[op.Id] = op;
        }

        return op;
    }

    /// <summary>Everything running, and everything that finished within <see cref="KeepFinishedFor"/>, as it stood, in no particular order.</summary>
    public IReadOnlyList<OperationRecord> Recent()
    {
        var ops = Live();
        var records = new List<OperationRecord>(ops.Count);
        foreach (var op in ops)
        {
            records.Add(op.Snapshot(_time));
        }

        return records;
    }

    /// <summary>
    /// Everything running, and everything that finished within <see cref="KeepFinishedFor"/>, as the operations themselves,
    /// in no particular order: for a view that picks the few it shows before it reads them. The window's Operations tab,
    /// refreshed four times a second, had <see cref="Recent"/> copy every one of the thousands a start-up's counts have in
    /// flight each time, and the window's thread with them (5-Nov-2026).
    /// </summary>
    public IReadOnlyList<Operation> Live()
    {
        lock (_gate)
        {
            Prune();
            var ops = new List<Operation>(_running.Count + _finished.Sum(f => f.Count));
            ops.AddRange(_running.Values);
            foreach (var finished in _finished)
            {
                ops.AddRange(finished);
            }

            return ops;
        }
    }

    /// <summary>The operation as it stands now.</summary>
    public OperationRecord Snapshot(Operation op) => op.Snapshot(_time);

    internal TimeProvider Time => _time;

    /// <summary>The session's counters, per source (by name) and per kind, kinds that were never asked left out.</summary>
    public OperationStatsSnapshot Stats()
    {
        var snapshot = new OperationStatsSnapshot();
        foreach (var (id, counters) in _counters.ToArray().OrderBy(p => p.Value.Name, StringComparer.OrdinalIgnoreCase))
        {
            var source = new SourceOperationStats { Id = id, Name = counters.Name };
            foreach (var kind in Enum.GetValues<OperationKind>())
            {
                var stats = counters.For(kind).Snapshot(kind, _time);
                if (stats.Finished > 0 || stats.Running > 0)
                {
                    source.Operations.Add(stats);
                }
            }

            if (source.Operations.Count > 0)
            {
                snapshot.Sources.Add(source);
            }
        }

        return snapshot;
    }

    /// <summary>A deleted source: its counters go, and what it finished leaves <see cref="Recent"/> (what still runs ends as it will).</summary>
    public void Forget(Guid sourceId)
    {
        _counters.TryRemove(sourceId, out _);
        lock (_gate)
        {
            foreach (var finished in _finished)
            {
                if (finished.Any(op => op.SourceId == sourceId))
                {
                    var kept = finished.Where(op => op.SourceId != sourceId).ToList();
                    finished.Clear();
                    foreach (var op in kept)
                    {
                        finished.Enqueue(op);
                    }
                }
            }
        }
    }

    internal long Timestamp() => _time.GetTimestamp();

    internal void OnFinished(Operation op)
    {
        lock (_gate)
        {
            _running.Remove(op.Id);
            _finished[(int)op.Kind.AskedOf()].Enqueue(op);
            Prune();
        }

        if (_counters.TryGetValue(op.SourceId, out var counters))
        {
            counters.For(op.Kind).Ended(op, _time.GetElapsedTime(op.StartedAt, op.FinishedAt));
        }

        Finished?.Invoke(op.Snapshot(_time));
    }

    // under _gate: each finished queue is in the order the operations ended, so the expired ones are at its head
    private void Prune()
    {
        foreach (var finished in _finished)
        {
            while (finished.Count > 0 && (finished.Count > KeepFinishedAtMost || _time.GetElapsedTime(finished.Peek().FinishedAt) >= KeepFinishedFor))
            {
                finished.Dequeue();
            }
        }
    }

    private sealed class SourceCounters
    {
        private readonly KindCounters[] _kinds = Enum.GetValues<OperationKind>().Select(_ => new KindCounters()).ToArray();
        private string _name = "";

        public string Name
        {
            get => Volatile.Read(ref _name);
            set => Volatile.Write(ref _name, value);
        }

        public KindCounters For(OperationKind kind) => _kinds[(int)kind];
    }

    private sealed class KindCounters
    {
        private long _begun;
        private long _succeeded;
        private long _failed;
        private long _cancelled;
        private long _totalTicks;
        private long _maxTicks;
        private long _bytes;
        private long _items;

        public void Begun() => Interlocked.Increment(ref _begun);

        public void Ended(Operation op, TimeSpan duration)
        {
            switch (op.Outcome)
            {
                case OperationOutcome.Failed:
                    Interlocked.Increment(ref _failed);
                    break;
                case OperationOutcome.Cancelled:
                    Interlocked.Increment(ref _cancelled);
                    break;
                default:
                    Interlocked.Increment(ref _succeeded);
                    break;
            }

            Interlocked.Add(ref _totalTicks, duration.Ticks);
            Interlocked.Add(ref _bytes, op.Bytes);
            Interlocked.Add(ref _items, op.Items);
            long seen;
            while (duration.Ticks > (seen = Interlocked.Read(ref _maxTicks)) && Interlocked.CompareExchange(ref _maxTicks, duration.Ticks, seen) != seen)
            {
            }
        }

        public OperationKindStats Snapshot(OperationKind kind, TimeProvider time)
        {
            var succeeded = Interlocked.Read(ref _succeeded);
            var failed = Interlocked.Read(ref _failed);
            var cancelled = Interlocked.Read(ref _cancelled);
            var finished = succeeded + failed + cancelled;
            return new OperationKindStats
            {
                Kind = kind,
                Finished = finished,
                Failed = failed,
                Cancelled = cancelled,
                Running = Math.Max(0, Interlocked.Read(ref _begun) - finished),
                TotalSeconds = TimeSpan.FromTicks(Interlocked.Read(ref _totalTicks)).TotalSeconds,
                MaxSeconds = TimeSpan.FromTicks(Interlocked.Read(ref _maxTicks)).TotalSeconds,
                Bytes = Interlocked.Read(ref _bytes),
                Items = Interlocked.Read(ref _items),
            };
        }
    }
}

/// <summary>The session's operation counters, as status.json carries them.</summary>
public sealed class OperationStatsSnapshot
{
    public List<SourceOperationStats> Sources { get; set; } = [];

    public long Running => Sources.Sum(s => s.Operations.Sum(o => o.Running));

    public long Finished => Sources.Sum(s => s.Operations.Sum(o => o.Finished));

    public long Failed => Sources.Sum(s => s.Operations.Sum(o => o.Failed));
}

public sealed class SourceOperationStats
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public List<OperationKindStats> Operations { get; set; } = [];
}

public sealed class OperationKindStats
{
    public OperationKind Kind { get; set; }

    /// <summary>Succeeded, failed and cancelled together.</summary>
    public long Finished { get; set; }

    public long Failed { get; set; }

    public long Cancelled { get; set; }

    public long Running { get; set; }

    /// <summary>Of the finished ones.</summary>
    public double TotalSeconds { get; set; }

    public double MaxSeconds { get; set; }

    public double AverageSeconds => Finished > 0 ? TotalSeconds / Finished : 0;

    public long Bytes { get; set; }

    public long Items { get; set; }
}
