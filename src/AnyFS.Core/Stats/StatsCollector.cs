using System.Collections.Concurrent;

namespace AnyFS.Core.Stats;

/// <summary>Lifetime counters as persisted in state/stats.json.</summary>
public sealed class StatsFile
{
    public int Version { get; set; } = 1;

    public DateTimeOffset? Since { get; set; }

    public long FilesServed { get; set; }

    public long BytesRead { get; set; }

    public long BytesWritten { get; set; }

    public Dictionary<string, CollectionCounters> Collections { get; set; } = new(StringComparer.Ordinal);
}

public sealed class CollectionCounters
{
    public long FilesServed { get; set; }

    public long BytesRead { get; set; }

    public long BytesWritten { get; set; }
}

/// <summary>What the Stats tab and <c>anyfs status</c> show.</summary>
public sealed class StatsSnapshot
{
    public DateTimeOffset? TotalsSince { get; set; }

    public DateTimeOffset SessionStarted { get; set; }

    public CollectionCounters Total { get; set; } = new();

    public CollectionCounters Session { get; set; } = new();

    public int ActiveConnections { get; set; }

    public Dictionary<string, CollectionStats> Collections { get; set; } = new(StringComparer.Ordinal);
}

public sealed class CollectionStats
{
    public CollectionCounters Total { get; set; } = new();

    public CollectionCounters Session { get; set; } = new();

    public int ActiveConnections { get; set; }
}

/// <summary>
/// Counted in the router so every mount and source gets them for free (spec 11.3, 13): a file is served
/// when a read handle delivers its first byte; bytes read and bytes written are kept apart; active
/// connections are what providers report.
/// </summary>
public sealed class StatsCollector
{
    private readonly ConcurrentDictionary<string, Counters> _collections = new(StringComparer.Ordinal);
    private readonly Counters _total = new();
    private DateTimeOffset? _totalsSince;

    public StatsCollector(TimeProvider? time = null)
    {
        SessionStarted = (time ?? TimeProvider.System).GetUtcNow();
    }

    public DateTimeOffset SessionStarted { get; }

    public void LoadLifetime(StatsFile? file)
    {
        if (file is null)
        {
            _totalsSince ??= SessionStarted;
            return;
        }

        _totalsSince = file.Since ?? SessionStarted;
        _total.LifetimeFiles = file.FilesServed;
        _total.LifetimeRead = file.BytesRead;
        _total.LifetimeWritten = file.BytesWritten;
        foreach (var (name, c) in file.Collections)
        {
            var counters = _collections.GetOrAdd(name, _ => new Counters());
            counters.LifetimeFiles = c.FilesServed;
            counters.LifetimeRead = c.BytesRead;
            counters.LifetimeWritten = c.BytesWritten;
        }
    }

    public StatsFile ToFile()
    {
        var file = new StatsFile
        {
            Since = _totalsSince ?? SessionStarted,
            FilesServed = _total.LifetimeFiles + _total.SessionFiles,
            BytesRead = _total.LifetimeRead + _total.SessionRead,
            BytesWritten = _total.LifetimeWritten + _total.SessionWritten,
        };
        foreach (var (name, c) in _collections)
        {
            file.Collections[name] = new CollectionCounters
            {
                FilesServed = c.LifetimeFiles + c.SessionFiles,
                BytesRead = c.LifetimeRead + c.SessionRead,
                BytesWritten = c.LifetimeWritten + c.SessionWritten,
            };
        }

        return file;
    }

    /// <summary>"Reset totals": lifetime counters go to zero; the session keeps counting.</summary>
    public void ResetTotals(TimeProvider? time = null)
    {
        _totalsSince = (time ?? TimeProvider.System).GetUtcNow();
        _total.ResetLifetime();
        foreach (var c in _collections.Values)
        {
            c.ResetLifetime();
        }

        // what the session already counted would otherwise be re-added to the fresh totals
        _total.LifetimeFiles = -_total.SessionFiles;
        _total.LifetimeRead = -_total.SessionRead;
        _total.LifetimeWritten = -_total.SessionWritten;
        foreach (var c in _collections.Values)
        {
            c.LifetimeFiles = -c.SessionFiles;
            c.LifetimeRead = -c.SessionRead;
            c.LifetimeWritten = -c.SessionWritten;
        }
    }

    public void FileServed(string collection)
    {
        Interlocked.Increment(ref _total.SessionFiles);
        Interlocked.Increment(ref For(collection).SessionFiles);
    }

    public void BytesRead(string collection, long count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _total.SessionRead, count);
        Interlocked.Add(ref For(collection).SessionRead, count);
    }

    public void BytesWritten(string collection, long count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _total.SessionWritten, count);
        Interlocked.Add(ref For(collection).SessionWritten, count);
    }

    public void ConnectionOpened(string collection)
    {
        Interlocked.Increment(ref _total.Connections);
        Interlocked.Increment(ref For(collection).Connections);
    }

    public void ConnectionClosed(string collection)
    {
        Interlocked.Decrement(ref _total.Connections);
        Interlocked.Decrement(ref For(collection).Connections);
    }

    public StatsSnapshot Snapshot()
    {
        var snapshot = new StatsSnapshot
        {
            TotalsSince = _totalsSince ?? SessionStarted,
            SessionStarted = SessionStarted,
            Total = _total.Lifetime(),
            Session = _total.Session(),
            ActiveConnections = Math.Max(0, _total.Connections),
        };
        foreach (var (name, c) in _collections.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            snapshot.Collections[name] = new CollectionStats { Total = c.Lifetime(), Session = c.Session(), ActiveConnections = Math.Max(0, c.Connections) };
        }

        return snapshot;
    }

    /// <summary>A deleted collection leaves the per-collection table (the overall totals keep what it served).</summary>
    public void RemoveCollection(string collection) => _collections.TryRemove(collection, out _);

    /// <summary>A renamed collection keeps its counters under the new name.</summary>
    public void RenameCollection(string from, string to)
    {
        if (from == to || !_collections.TryRemove(from, out var counters))
        {
            return;
        }

        _collections.AddOrUpdate(to, counters, (_, existing) =>
        {
            existing.LifetimeFiles += counters.LifetimeFiles + counters.SessionFiles;
            existing.LifetimeRead += counters.LifetimeRead + counters.SessionRead;
            existing.LifetimeWritten += counters.LifetimeWritten + counters.SessionWritten;
            return existing;
        });
    }

    /// <summary>Drops the rows of collections that no longer exist (deleted while anyfs wasn't running, or before this was tracked).</summary>
    public void KeepOnly(IEnumerable<string> collections)
    {
        var keep = new HashSet<string>(collections, StringComparer.Ordinal);
        foreach (var name in _collections.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _collections.TryRemove(name, out _);
        }
    }

    private Counters For(string collection) => _collections.GetOrAdd(collection, _ => new Counters());

    private sealed class Counters
    {
        public long LifetimeFiles;
        public long LifetimeRead;
        public long LifetimeWritten;
        public long SessionFiles;
        public long SessionRead;
        public long SessionWritten;
        public int Connections;

        public void ResetLifetime()
        {
            LifetimeFiles = 0;
            LifetimeRead = 0;
            LifetimeWritten = 0;
        }

        public CollectionCounters Lifetime() => new()
        {
            FilesServed = Math.Max(0, LifetimeFiles + Interlocked.Read(ref SessionFiles)),
            BytesRead = Math.Max(0, LifetimeRead + Interlocked.Read(ref SessionRead)),
            BytesWritten = Math.Max(0, LifetimeWritten + Interlocked.Read(ref SessionWritten)),
        };

        public CollectionCounters Session() => new()
        {
            FilesServed = Interlocked.Read(ref SessionFiles),
            BytesRead = Interlocked.Read(ref SessionRead),
            BytesWritten = Interlocked.Read(ref SessionWritten),
        };
    }
}
