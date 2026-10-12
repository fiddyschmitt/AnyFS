using System.Collections.Concurrent;

namespace AnyFS.Sdk;

/// <summary>
/// Memoises directory listings for a short time, because clients stat every file they list. Concurrent
/// misses for the same directory share one upstream call. Invalidate on writes through the same source.
/// </summary>
public sealed class ListingCache
{
    private readonly TimeSpan _ttl;
    private readonly Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> _lister;
    private readonly int _maxDirectories;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public ListingCache(TimeSpan ttl, Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> lister, int maxDirectories = 10_000, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(lister);
        _ttl = ttl;
        _lister = lister;
        _maxDirectories = maxDirectories;
        _time = time ?? TimeProvider.System;
    }

    public int Count => _entries.Count;

    /// <summary>The listing of <paramref name="path"/> (a normalised directory path), from the cache when fresh.</summary>
    public async ValueTask<IReadOnlyList<VfsEntry>> GetAsync(string path, CancellationToken ct)
    {
        var now = _time.GetTimestamp();
        if (_entries.TryGetValue(path, out var existing) && !existing.IsExpired(_time, _ttl))
        {
            return await existing.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        var created = new Entry(now, _lister, path);
        var entry = _entries.AddOrUpdate(path, created, (_, old) => old.IsExpired(_time, _ttl) ? created : old);
        if (ReferenceEquals(entry, created))
        {
            TrimIfNeeded();
        }

        try
        {
            return await entry.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _entries.TryRemove(new KeyValuePair<string, Entry>(path, entry));
            throw;
        }
    }

    /// <summary>Finds <paramref name="path"/> in its parent's cached listing; null when absent.</summary>
    public async ValueTask<VfsEntry?> StatAsync(string path, CancellationToken ct, StringComparison comparison = StringComparison.Ordinal)
    {
        if (path.Length == 0)
        {
            return VfsEntry.Directory("");
        }

        var (parent, name) = VfsPath.SplitLast(path);
        IReadOnlyList<VfsEntry> listing;
        try
        {
            listing = await GetAsync(parent, ct).ConfigureAwait(false);
        }
        catch (VfsException ex) when (ex.Error is VfsError.NotFound or VfsError.NotADirectory)
        {
            return null;
        }

        foreach (var entry in listing)
        {
            if (string.Equals(entry.Name, name, comparison))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Forgets the listing of a directory.</summary>
    public void Invalidate(string directoryPath) => _entries.TryRemove(directoryPath, out _);

    /// <summary>Forgets the listing of the directory that holds <paramref name="path"/>.</summary>
    public void InvalidateParentOf(string path) => Invalidate(VfsPath.Parent(path));

    /// <summary>Forgets a directory and everything cached under it (after a rename or delete of a directory).</summary>
    public void InvalidateTree(string directoryPath)
    {
        foreach (var key in _entries.Keys)
        {
            if (VfsPath.IsUnder(key, directoryPath))
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    public void InvalidateAll() => _entries.Clear();

    private void TrimIfNeeded()
    {
        if (_entries.Count <= _maxDirectories)
        {
            return;
        }

        foreach (var pair in _entries)
        {
            if (pair.Value.IsExpired(_time, _ttl))
            {
                _entries.TryRemove(pair);
            }
        }

        if (_entries.Count <= _maxDirectories)
        {
            return;
        }

        // still over: drop the oldest quarter
        var oldest = _entries.OrderBy(p => p.Value.CreatedTimestamp).Take(Math.Max(1, _entries.Count / 4)).ToList();
        foreach (var pair in oldest)
        {
            _entries.TryRemove(pair);
        }
    }

    private sealed class Entry
    {
        public Entry(long createdTimestamp, Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> lister, string path)
        {
            CreatedTimestamp = createdTimestamp;
            Task = ListAsync(lister, path);
        }

        public long CreatedTimestamp { get; }

        public Task<IReadOnlyList<VfsEntry>> Task { get; }

        public bool IsExpired(TimeProvider time, TimeSpan ttl) => time.GetElapsedTime(CreatedTimestamp) > ttl;

        private static async Task<IReadOnlyList<VfsEntry>> ListAsync(Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> lister, string path)
        {
            // the shared task must outlive any one caller's cancellation, so it runs with no token
            var list = new List<VfsEntry>();
            await foreach (var entry in lister(path, CancellationToken.None).ConfigureAwait(false))
            {
                list.Add(entry);
            }

            return list;
        }
    }
}
