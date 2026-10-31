using System.Collections.Concurrent;

namespace AnyFS.Sdk;

/// <summary>
/// Memoises directory listings, because clients stat every file they list. Concurrent misses for the same directory
/// share one upstream call. A listing is kept for a time (a couple of seconds, so it is near enough current), or, for a
/// source that is slow to list, until it is listed afresh (<see cref="Kept"/>): the source then answers from what it
/// knows and walks its tree again now and then (<see cref="RefreshAsync(string, CancellationToken)"/>, or <see cref="KeptListings"/>, which does the
/// walking), or lists a folder afresh behind the answer once its listing is older than <see cref="RefreshAfter"/>. Writes
/// through the same source keep the listings right (<see cref="Update"/>, <see cref="Invalidate"/>), also a listing still
/// on its way: what changed while it was being fetched is applied to it when it lands, so it never brings back what a
/// write replaced.
/// </summary>
public sealed class ListingCache
{
    /// <summary>The time a listing is kept for when it is kept until listed afresh or invalidated.</summary>
    public static readonly TimeSpan Kept = Timeout.InfiniteTimeSpan;

    private const string NoRefreshOnRepeat = "A folder listed again soon after is no sign of a refresh: programs list a folder over and over (Adobe Acrobat opening a file does), so the window is ignored. List afresh with RefreshAsync when the source has its own reason to.";

    private readonly TimeSpan _ttl;
    private readonly Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> _lister;
    private readonly int _maxDirectories;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Fetch> _fetches = new(StringComparer.Ordinal);

    /// <param name="ttl">How long a listing is good for, or <see cref="Kept"/>.</param>
    public ListingCache(TimeSpan ttl, Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> lister, int maxDirectories = 10_000, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(lister);
        _ttl = ttl;
        _lister = lister;
        _maxDirectories = maxDirectories;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>As the constructor above, with <paramref name="refreshOnRepeat"/> ignored (kept for plugins built against SDK 0.2).</summary>
    [Obsolete(NoRefreshOnRepeat)]
    public ListingCache(TimeSpan ttl, Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> lister, int maxDirectories, TimeProvider? time, TimeSpan? refreshOnRepeat)
        : this(ttl, lister, maxDirectories, time)
    {
    }

    /// <summary>How many listings are held.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// The age past which a listing held is answered and listed afresh behind the answer, one listing of a folder at a
    /// time, so a folder opens at once and a change upstream shows the next time it is listed; null (the default) never
    /// lists one afresh on its own. A listing afresh that fails leaves what was held, and the next waits as long again.
    /// With <see cref="StampOf"/>, a held listing whose stamp still holds counts as listed afresh without the upstream
    /// being asked for the listing.
    /// </summary>
    public TimeSpan? RefreshAfter { get; init; }

    /// <summary>
    /// A folder's stamp: what its listing, and every listing below it, depends on, as the upstream tells it cheaply (a
    /// chat's version, a library's last change and item count, a project's last activity), given the stamp held for the
    /// folder (null when none): the same string back when nothing has changed since, another when something has, null
    /// for a folder the source has no stamp for (listed afresh whenever asked). Read before a folder is listed and kept
    /// with the listing, so a change during the listing shows as a changed stamp next time. Null (the default) stamps
    /// nothing.
    /// </summary>
    public Func<string, string?, CancellationToken, ValueTask<string?>>? StampOf { get; init; }

    /// <summary>The listing of <paramref name="path"/> (a normalised directory path), from the cache when fresh.</summary>
    public ValueTask<IReadOnlyList<VfsEntry>> GetAsync(string path, CancellationToken ct)
    {
        if (_entries.TryGetValue(path, out var held) && !IsExpired(held))
        {
            if (RefreshAfter is { } after && _time.GetElapsedTime(held.Checked) > after)
            {
                RefreshBehind(path, held);
            }

            return ValueTask.FromResult(held.List);
        }

        return FetchAsync(path, ct, null);
    }

    /// <summary>
    /// Keeps <paramref name="listing"/> as the listing of <paramref name="path"/>, as if it had just come from the upstream:
    /// for a source that learnt it otherwise than folder by folder (a walk that lists a whole subtree at once). A listing of
    /// the folder on its way still lands after it.
    /// </summary>
    public void Keep(string path, IReadOnlyList<VfsEntry> listing) => Keep(path, listing, null);

    /// <summary>The same, with the folder's stamp (<see cref="StampOf"/>) as read before the listing was made.</summary>
    public void Keep(string path, IReadOnlyList<VfsEntry> listing, string? stamp)
    {
        ArgumentNullException.ThrowIfNull(listing);
        lock (_gate)
        {
            _entries[path] = new Entry(_time.GetTimestamp(), listing) { Stamp = stamp };
            TrimIfNeededLocked();
        }
    }

    /// <summary>The listings held now, by folder path: what a source saves to answer at once after its next start (<see cref="Restore(string, IReadOnlyList{VfsEntry})"/>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Where(e => !IsExpired(e.Value)).ToDictionary(e => e.Key, e => e.Value.List, StringComparer.Ordinal);
        }
    }

    /// <summary>The stamps held with the listings (<see cref="StampOf"/>), by folder path, for the folders that have one: saved and restored with the listings.</summary>
    public IReadOnlyDictionary<string, string> Stamps()
    {
        lock (_gate)
        {
            return _entries.Where(e => e.Value.Stamp is not null && !IsExpired(e.Value)).ToDictionary(e => e.Key, e => e.Value.Stamp!, StringComparer.Ordinal);
        }
    }

    /// <summary>The stamp held with the listing of <paramref name="path"/>; null where there is none.</summary>
    public string? StampHeld(string path)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(path, out var held) && !IsExpired(held) ? held.Stamp : null;
        }
    }

    /// <summary>The listings held of <paramref name="folder"/> and of every folder below it, by path (none where none is held).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>> Under(string folder)
    {
        lock (_gate)
        {
            return _entries.Where(e => !IsExpired(e.Value) && (e.Key == folder || VfsPath.IsUnder(e.Key, folder))).ToDictionary(e => e.Key, e => e.Value.List, StringComparer.Ordinal);
        }
    }

    /// <summary>Counts the listing held of <paramref name="path"/> as listed afresh now (a walk found its stamp unchanged); nothing where none is held.</summary>
    public void Touch(string path)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(path, out var held) && !IsExpired(held))
            {
                var now = _time.GetTimestamp();
                _entries[path] = held with { Fetched = now, Checked = now };
            }
        }
    }

    /// <summary>
    /// Keeps a listing an earlier run saved (<see cref="Snapshot"/>) as the listing of <paramref name="path"/>, as old: it
    /// answers at once and is listed afresh behind the first answer (<see cref="RefreshAfter"/>; without that, by the next
    /// walk). A listing this run already has wins.
    /// </summary>
    public void Restore(string path, IReadOnlyList<VfsEntry> listing) => Restore(path, listing, null);

    /// <summary>The same, with the stamp saved with it (<see cref="Stamps"/>): while it holds, the listing counts as fresh when it would be listed afresh.</summary>
    public void Restore(string path, IReadOnlyList<VfsEntry> listing, string? stamp)
    {
        ArgumentNullException.ThrowIfNull(listing);
        lock (_gate)
        {
            if (_entries.ContainsKey(path))
            {
                return;
            }

            // checked a year ago: past any refresh age
            var now = _time.GetTimestamp();
            _entries[path] = new Entry(now, listing) { Checked = now - (long)(TimeSpan.FromDays(365).TotalSeconds * _time.TimestampFrequency), Stamp = stamp };
            TrimIfNeededLocked();
        }
    }

    /// <summary>The same as <see cref="GetAsync"/> (kept for plugins built against SDK 0.2).</summary>
    [Obsolete(NoRefreshOnRepeat)]
    public ValueTask<IReadOnlyList<VfsEntry>> ListAsync(string path, CancellationToken ct) => GetAsync(path, ct);

    /// <summary>Lists <paramref name="path"/> from the upstream now (or joins a listing of it on its way) and keeps the result in place of what was held.</summary>
    public ValueTask<IReadOnlyList<VfsEntry>> RefreshAsync(string path, CancellationToken ct) => FetchAsync(path, ct, null);

    /// <summary>The same, keeping <paramref name="stamp"/> with the listing: the folder's stamp as the caller read it before asking (<see cref="StampOf"/> is not asked again).</summary>
    public ValueTask<IReadOnlyList<VfsEntry>> RefreshAsync(string path, string? stamp, CancellationToken ct) => FetchAsync(path, ct, (true, stamp));

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

    /// <summary>
    /// Puts <paramref name="entry"/> into the cached listing of its directory in place of any entry of that name,
    /// or takes the name out when <paramref name="entry"/> is null: for a source that has just created, replaced,
    /// renamed or deleted <paramref name="path"/> and knows what the listing should now say. Forgetting the whole
    /// directory instead costs a listing call per file in a bulk copy, and against an upstream whose listings are
    /// slow that is quadratic. A listing of the directory on its way gets the change too when it lands; nothing
    /// happens when the directory is neither held nor being listed (the next listing comes from the upstream).
    /// </summary>
    public void Update(string path, VfsEntry? entry, StringComparison comparison = StringComparison.Ordinal)
    {
        var (parent, name) = VfsPath.SplitLast(path);
        var change = new Change(name, entry, comparison);
        lock (_gate)
        {
            if (_entries.TryGetValue(parent, out var held) && !IsExpired(held))
            {
                _entries[parent] = held with { List = Apply(held.List, [change]) };
            }

            if (_fetches.TryGetValue(parent, out var fetch))
            {
                fetch.Changes.Add(change);
            }
        }
    }

    /// <summary>Forgets the listing of a directory; one on its way is handed to those waiting for it but not kept.</summary>
    public void Invalidate(string directoryPath)
    {
        lock (_gate)
        {
            _entries.TryRemove(directoryPath, out _);
            if (_fetches.TryGetValue(directoryPath, out var fetch))
            {
                fetch.Invalidated = true;
            }
        }
    }

    /// <summary>Forgets the listing of the directory that holds <paramref name="path"/>.</summary>
    public void InvalidateParentOf(string path) => Invalidate(VfsPath.Parent(path));

    /// <summary>Forgets a directory and everything cached under it (after a rename or delete of a directory).</summary>
    public void InvalidateTree(string directoryPath)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys)
            {
                if (VfsPath.IsUnder(key, directoryPath))
                {
                    _entries.TryRemove(key, out _);
                }
            }

            foreach (var (key, fetch) in _fetches)
            {
                if (VfsPath.IsUnder(key, directoryPath))
                {
                    fetch.Invalidated = true;
                }
            }
        }
    }

    public void InvalidateAll()
    {
        lock (_gate)
        {
            _entries.Clear();
            foreach (var fetch in _fetches.Values)
            {
                fetch.Invalidated = true;
            }
        }
    }

    private bool IsExpired(Entry entry) => _ttl != Kept && _time.GetElapsedTime(entry.Fetched) > _ttl;

    /// <summary>Lists <paramref name="path"/> afresh in the background unless a listing of it is on its way; meanwhile the held listing counts as checked now.</summary>
    private void RefreshBehind(string path, Entry held)
    {
        Fetch fetch;
        lock (_gate)
        {
            if (_fetches.ContainsKey(path))
            {
                return;
            }

            if (_entries.TryGetValue(path, out var current) && ReferenceEquals(current, held))
            {
                _entries[path] = held with { Checked = _time.GetTimestamp() };
            }

            fetch = new Fetch();
            _fetches[path] = fetch;
        }

        // on the pool, so the answer is not held up by the lister's first steps; RunAsync observes a failure itself
        _ = Task.Run(() => RefreshBehindAsync(path, fetch, held));
    }

    /// <summary>A listing afresh behind an answer: spared where the folder's stamp still holds (the held listing is then as good as new), else as any, with the stamp just read.</summary>
    private async Task RefreshBehindAsync(string path, Fetch fetch, Entry held)
    {
        if (StampOf is { } stampOf && held.Stamp is { } stamp)
        {
            string? now = null;
            try
            {
                now = await stampOf(path, stamp, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // unstamped: listed afresh whenever asked
            }

            if (now == stamp)
            {
                lock (_gate)
                {
                    Forget(path, fetch);
                    if (_entries.TryGetValue(path, out var current) && current.List == held.List)
                    {
                        _entries[path] = current with { Fetched = _time.GetTimestamp() };
                    }
                }

                fetch.Result.TrySetResult(held.List);
                return;
            }

            fetch.Stamp = (true, now);
        }

        await RunAsync(path, fetch).ConfigureAwait(false);
    }

    /// <summary>The listing from the upstream: the one on its way when there is one, else a new one, shared by every caller until it lands.</summary>
    /// <param name="stamp">The folder's stamp as the caller read it, to keep with the listing; null to read it here (<see cref="StampOf"/>).</param>
    private ValueTask<IReadOnlyList<VfsEntry>> FetchAsync(string path, CancellationToken ct, (bool Read, string? Value)? stamp)
    {
        Fetch fetch;
        var started = false;
        lock (_gate)
        {
            if (!_fetches.TryGetValue(path, out fetch!))
            {
                fetch = new Fetch { Stamp = stamp };
                _fetches[path] = fetch;
                started = true;
            }
        }

        if (started)
        {
            _ = RunAsync(path, fetch);
        }

        return new ValueTask<IReadOnlyList<VfsEntry>>(fetch.Result.Task.WaitAsync(ct));
    }

    private async Task RunAsync(string path, Fetch fetch)
    {
        try
        {
            // the folder's stamp before its listing, so a change during the listing shows as a changed stamp next time
            string? stamp = fetch.Stamp?.Value;
            if (fetch.Stamp is null && StampOf is { } stampOf)
            {
                string? held;
                lock (_gate)
                {
                    held = _entries.TryGetValue(path, out var current) ? current.Stamp : null;
                }

                try
                {
                    stamp = await stampOf(path, held, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // unstamped: listed afresh whenever asked
                }
            }

            // the shared listing must outlive any one caller's cancellation, so it runs with no token
            var list = new List<VfsEntry>();
            await foreach (var entry in _lister(path, CancellationToken.None).ConfigureAwait(false))
            {
                list.Add(entry);
            }

            IReadOnlyList<VfsEntry> result;
            lock (_gate)
            {
                Forget(path, fetch);
                result = fetch.Changes.Count == 0 ? list : Apply(list, fetch.Changes);
                if (!fetch.Invalidated)
                {
                    // good for its time from when it arrived: a folder slower to list than that would otherwise be stale
                    // on arrival, and every stat in it would list it again
                    _entries[path] = new Entry(_time.GetTimestamp(), result) { Stamp = stamp };
                    TrimIfNeededLocked();
                }
            }

            fetch.Result.TrySetResult(result);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                Forget(path, fetch);
            }

            fetch.Result.TrySetException(ex);
            _ = fetch.Result.Task.Exception;   // seen here: a listing nobody waited for any more is no unobserved failure
        }
    }

    private void Forget(string path, Fetch fetch)
    {
        if (_fetches.TryGetValue(path, out var current) && ReferenceEquals(current, fetch))
        {
            _fetches.Remove(path);
        }
    }

    private static List<VfsEntry> Apply(IReadOnlyList<VfsEntry> listing, IReadOnlyList<Change> changes)
    {
        var list = listing.ToList();
        foreach (var change in changes)
        {
            list.RemoveAll(e => string.Equals(e.Name, change.Name, change.Comparison));
            if (change.Entry is not null)
            {
                list.Add(change.Entry);
            }
        }

        return list;
    }

    private void TrimIfNeededLocked()
    {
        if (_entries.Count <= _maxDirectories)
        {
            return;
        }

        foreach (var pair in _entries)
        {
            if (IsExpired(pair.Value))
            {
                _entries.TryRemove(pair);
            }
        }

        if (_entries.Count <= _maxDirectories)
        {
            return;
        }

        // still over: drop the oldest quarter
        var oldest = _entries.OrderBy(p => p.Value.Fetched).Take(Math.Max(1, _entries.Count / 4)).ToList();
        foreach (var pair in oldest)
        {
            _entries.TryRemove(pair);
        }
    }

    /// <summary>
    /// A listing held: when it arrived from the upstream (it expires from then) and what it said, with the writes since
    /// applied; when it was last listed afresh or tried to be (<see cref="RefreshAfter"/> counts from then); and the
    /// folder's stamp as read before it (<see cref="StampOf"/>), where the source has one.
    /// </summary>
    private sealed record Entry(long Fetched, IReadOnlyList<VfsEntry> List)
    {
        public long Checked { get; init; } = Fetched;

        public string? Stamp { get; init; }
    }

    /// <summary>A write's change to one name in a listing.</summary>
    private sealed record Change(string Name, VfsEntry? Entry, StringComparison Comparison);

    /// <summary>A listing on its way from the upstream, what changed meanwhile, and the folder's stamp where the caller read it.</summary>
    private sealed class Fetch
    {
        public TaskCompletionSource<IReadOnlyList<VfsEntry>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<Change> Changes { get; } = [];

        public bool Invalidated { get; set; }

        public (bool Read, string? Value)? Stamp { get; set; }
    }
}
