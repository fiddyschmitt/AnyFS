using System.Collections.Concurrent;

namespace AnyFS.Sdk;

/// <summary>
/// Memoises directory listings, because clients stat every file they list. Concurrent misses for the same directory
/// share one upstream call. A listing is kept for a time (a couple of seconds, so it is near enough current), or, for a
/// source that is slow to list, until it is listed afresh (<see cref="Kept"/>): the source then answers from what it
/// knows and walks its tree again now and then (<see cref="RefreshAsync(string, CancellationToken)"/>, or <see cref="KeptListings"/>, which does the
/// walking). Writes through the same source keep the listings right (<see cref="Update"/>, <see cref="Invalidate"/>), also a listing still
/// on its way: what changed while it was being fetched is applied to it when it lands, so it never brings back what a
/// write replaced. A folder listed again soon after is no sign of a refresh (programs list a folder over and over: Adobe
/// Acrobat opening a file does): a source lists afresh with <see cref="RefreshAsync(string, CancellationToken)"/> when it
/// has a reason of its own.
/// </summary>
public sealed class ListingCache
{
    /// <summary>The time a listing is kept for when it is kept until listed afresh or invalidated.</summary>
    public static readonly TimeSpan Kept = Timeout.InfiniteTimeSpan;

    private readonly TimeSpan _ttl;
    private readonly Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> _lister;
    private readonly int _maxDirectories;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Failure> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Fetch> _fetches = new(StringComparer.Ordinal);

    // the paths held, in order, so the folders below one are found by a range of them, not by a pass over them all
    private readonly SortedSet<string> _keys = new(StringComparer.Ordinal);
    private long _version;

    /// <param name="ttl">How long a listing is good for, or <see cref="Kept"/>.</param>
    public ListingCache(TimeSpan ttl, Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> lister, int maxDirectories = 10_000, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(lister);
        _ttl = ttl;
        _lister = lister;
        _maxDirectories = maxDirectories;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How many listings are held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _keys.Count;
            }
        }
    }

    /// <summary>
    /// Moves with every change to what is held (a listing kept, replaced or let go, a stamp taken off): a source that saves
    /// the listings (<see cref="KeptListings"/>) can tell whether there is anything new to save.
    /// </summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>
    /// A folder's stamp: what its listing, and every listing below it, depends on, as the upstream tells it cheaply (a
    /// chat's version, a library's last change and item count, a project's last activity), given the stamp held for the
    /// folder (null when none): the same string back when nothing has changed since, another when something has, null
    /// for a folder the source has no stamp for (every walk lists it). Read before a folder is listed and kept with the
    /// listing, so a change during the listing shows as a changed stamp next time. Null (the default) stamps nothing.
    /// </summary>
    public Func<string, string?, CancellationToken, ValueTask<string?>>? StampOf { get; init; }

    /// <summary>
    /// How long a folder whose listing failed, with none held, answers with that failure at once, by the failure (a
    /// refusal, say, longer than a service that did not answer); null or zero for not at all. A client that lists the tree
    /// over and over (a search) then pays for a folder the upstream refuses once, not every time. Listed again when asked
    /// after that, by <see cref="RefreshAsync(string, CancellationToken)"/> (a walk), and after <see cref="ForgetFailures"/>.
    /// Never for a folder not found or not a folder: whether a path is there is its parent's listing's to say. Null (the
    /// default) asks the upstream again whenever asked.
    /// </summary>
    public Func<VfsException, TimeSpan?>? FailureKeptFor { get; init; }

    /// <summary>Told of each folder as it goes to the upstream, and why (no listing held, a failure's time over, a listing past its time, a walk): for the log.</summary>
    public Action<string, string>? OnListing { get; init; }

    /// <summary>The listing held of <paramref name="path"/> as it is, nothing asked of the upstream; null where none is held.</summary>
    public IReadOnlyList<VfsEntry>? Held(string path) => _entries.TryGetValue(path, out var held) && !IsExpired(held) ? held.List : null;

    /// <summary>Whether <paramref name="path"/> has been asked for: a listing of it is held, or its listing failed and that is kept (<see cref="FailureKeptFor"/>).</summary>
    public bool Asked(string path) => Held(path) is not null || _failures.ContainsKey(path);

    /// <summary>The listing of <paramref name="path"/> (a normalised directory path), from the cache when fresh.</summary>
    public ValueTask<IReadOnlyList<VfsEntry>> GetAsync(string path, CancellationToken ct)
    {
        if (_entries.TryGetValue(path, out var held) && !IsExpired(held))
        {
            return ValueTask.FromResult(held.List);
        }

        if (_failures.TryGetValue(path, out var failed) && _time.GetElapsedTime(failed.At) < failed.For)
        {
            return ValueTask.FromException<IReadOnlyList<VfsEntry>>(new VfsException(failed.Error.Error, failed.Error.Message, failed.Error.InnerException));
        }

        return FetchAsync(path, ct, null, failed is not null
            ? $"it failed {_time.GetElapsedTime(failed.At).TotalSeconds:F0} s ago and that failure was kept {failed.For.TotalSeconds:F0} s ({failed.Error.Message})"
            : held is not null ? "its listing is past its time" : "no listing is held");
    }

    /// <summary>Lets go of the failures kept (<see cref="FailureKeptFor"/>): for a source that knows what refused them may not now (a sign-in renewed), so each is listed again when next asked.</summary>
    public void ForgetFailures() => _failures.Clear();

    /// <summary>Counts each failure kept as noted now: a walk that has just ended asks this, so that a failure it met lasts until the next walk.</summary>
    public void RenewFailures()
    {
        var now = _time.GetTimestamp();
        foreach (var (path, failure) in _failures)
        {
            _failures.TryUpdate(path, failure with { At = now }, failure);
        }
    }

    /// <summary>
    /// Keeps <paramref name="listing"/> as the listing of <paramref name="path"/>, as if it had just come from the upstream:
    /// for a source that learnt it otherwise than folder by folder (a walk that lists a whole subtree at once), with the
    /// folder's stamp (<see cref="StampOf"/>) as read before the listing was made. A listing of the folder on its way still
    /// lands after it.
    /// </summary>
    public void Keep(string path, IReadOnlyList<VfsEntry> listing, string? stamp = null)
    {
        ArgumentNullException.ThrowIfNull(listing);
        lock (_gate)
        {
            StoreLocked(path, new Entry(_time.GetTimestamp(), listing) { Stamp = stamp });
            _failures.TryRemove(path, out _);
            TrimIfNeededLocked();
        }
    }

    /// <summary>The listings held now, by folder path: what a source saves to answer at once after its next start (<see cref="Restore"/>).</summary>
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
            var held = new Dictionary<string, IReadOnlyList<VfsEntry>>(StringComparer.Ordinal);
            foreach (var path in KeysUnderLocked(folder))
            {
                if (_entries.TryGetValue(path, out var entry) && !IsExpired(entry))
                {
                    held[path] = entry.List;
                }
            }

            return held;
        }
    }

    /// <summary>
    /// The paths held at <paramref name="folder"/> and below it, in order: the folder's own, then the range its children's
    /// paths fall in (in ordinal order every "folder/…" lies between "folder/" and "folder0", '0' being the character after
    /// '/'); every path for the root. Enumerated under the gate, and copied before anything is removed.
    /// </summary>
    private IEnumerable<string> KeysUnderLocked(string folder)
    {
        if (folder.Length == 0)
        {
            return _keys;
        }

        var below = _keys.GetViewBetween(folder + "/", folder + "0").Where(path => path.Length > folder.Length && path[folder.Length] == '/');
        return _keys.Contains(folder) ? below.Prepend(folder) : below;
    }

    /// <summary>
    /// Holds <paramref name="entry"/> for <paramref name="path"/> (under the gate): the path joins the sorted keys, and the
    /// version moves. The same listing again, with the same stamp (a walk that found a folder as it was), moves only when
    /// it was listed: a walk over a tree that has not changed leaves nothing to save.
    /// </summary>
    private void StoreLocked(string path, Entry entry)
    {
        if (_entries.TryGetValue(path, out var held) && held.Stamp == entry.Stamp && held.List.Count == entry.List.Count && held.List.SequenceEqual(entry.List))
        {
            _entries[path] = held with { Fetched = entry.Fetched };
            return;
        }

        _entries[path] = entry;
        _keys.Add(path);
        _changes?.Add(path);
        Interlocked.Increment(ref _version);
    }

    /// <summary>Lets go of what is held for <paramref name="path"/> (under the gate); false where nothing was.</summary>
    private bool RemoveLocked(string path)
    {
        if (!_entries.TryRemove(path, out _))
        {
            return false;
        }

        _keys.Remove(path);
        _changes?.Add(path);
        Interlocked.Increment(ref _version);
        return true;
    }

    // with changes followed (KeptListings writing them down as they come), the paths whose listing or stamp changed since
    // they were last taken, and whether everything was let go meanwhile
    private HashSet<string>? _changes;
    private bool _clearedSinceTaken;

    /// <summary>From now on, every change to what is held is remembered until <see cref="TakeChanges"/> takes it.</summary>
    internal void FollowChanges()
    {
        lock (_gate)
        {
            _changes ??= new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// What changed since the last time (with <see cref="FollowChanges"/>): whether everything was let go first, then each
    /// changed folder as it is held now, its listing and stamp, or no listing where it is held no more. Taken, so the next
    /// time says only what changes after this.
    /// </summary>
    internal (bool Cleared, List<(string Path, IReadOnlyList<VfsEntry>? Listing, string? Stamp)> Changes) TakeChanges()
    {
        lock (_gate)
        {
            var changes = new List<(string, IReadOnlyList<VfsEntry>?, string?)>(_changes?.Count ?? 0);
            foreach (var path in _changes ?? [])
            {
                changes.Add(_entries.TryGetValue(path, out var held) ? (path, held.List, held.Stamp) : (path, null, null));
            }

            _changes?.Clear();
            var cleared = _clearedSinceTaken;
            _clearedSinceTaken = false;
            return (cleared, changes);
        }
    }

    /// <summary>Every listing held now with its stamp, nothing copied: for writing them all down (a moment's view; changes meanwhile are followed as usual).</summary>
    internal IEnumerable<(string Path, IReadOnlyList<VfsEntry> Listing, string? Stamp)> AllHeld()
    {
        foreach (var (path, entry) in _entries)
        {
            if (!IsExpired(entry))
            {
                yield return (path, entry.List, entry.Stamp);
            }
        }
    }

    /// <summary>
    /// Takes the stamp off the listing of <paramref name="path"/>, the one on its way (kept without a stamp when it lands)
    /// and the one held: for a lister that finds what it listed is not yet all there is (a conversation whose older
    /// messages are still being read), so the next walk lists it afresh, not keeping it by its stamp.
    /// </summary>
    public void Unstamp(string path)
    {
        lock (_gate)
        {
            if (_fetches.TryGetValue(path, out var fetch))
            {
                fetch.Unstamped = true;
            }

            if (_entries.TryGetValue(path, out var held) && held.Stamp is not null)
            {
                StoreLocked(path, held with { Stamp = null });
            }
        }
    }

    /// <summary>Counts the listing held of <paramref name="path"/> as listed afresh now (a walk found its stamp unchanged); nothing where none is held.</summary>
    public void Touch(string path)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(path, out var held) && !IsExpired(held))
            {
                // when it was listed is not saved: no change to the version
                _entries[path] = held with { Fetched = _time.GetTimestamp() };
            }
        }
    }

    /// <summary>
    /// Keeps a listing an earlier run saved (<see cref="Snapshot"/>) as the listing of <paramref name="path"/>, with the stamp
    /// saved with it (<see cref="Stamps"/>): it answers at once, until the next walk lists it afresh, or keeps it while the
    /// stamp holds. A listing this run already has wins.
    /// </summary>
    public void Restore(string path, IReadOnlyList<VfsEntry> listing, string? stamp = null)
    {
        ArgumentNullException.ThrowIfNull(listing);
        lock (_gate)
        {
            if (_entries.ContainsKey(path))
            {
                return;
            }

            StoreLocked(path, new Entry(_time.GetTimestamp(), listing) { Stamp = stamp });
            TrimIfNeededLocked();
        }
    }

    /// <summary>Lists <paramref name="path"/> from the upstream now (or joins a listing of it on its way) and keeps the result in place of what was held.</summary>
    public ValueTask<IReadOnlyList<VfsEntry>> RefreshAsync(string path, CancellationToken ct) => FetchAsync(path, ct, null, "a walk");

    /// <summary>The same, keeping <paramref name="stamp"/> with the listing: the folder's stamp as the caller read it before asking (<see cref="StampOf"/> is not asked again).</summary>
    public ValueTask<IReadOnlyList<VfsEntry>> RefreshAsync(string path, string? stamp, CancellationToken ct) => FetchAsync(path, ct, (true, stamp), "a walk");

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
                StoreLocked(parent, held with { List = Apply(held.List, [change]) });
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
            RemoveLocked(directoryPath);
            _failures.TryRemove(directoryPath, out _);
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
            foreach (var key in KeysUnderLocked(directoryPath).ToList())
            {
                RemoveLocked(key);
            }

            foreach (var key in _failures.Keys)
            {
                if (VfsPath.IsUnder(key, directoryPath))
                {
                    _failures.TryRemove(key, out _);
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
            _keys.Clear();
            Interlocked.Increment(ref _version);
            if (_changes is not null)
            {
                _changes.Clear();
                _clearedSinceTaken = true;
            }

            _failures.Clear();
            foreach (var fetch in _fetches.Values)
            {
                fetch.Invalidated = true;
            }
        }
    }

    private bool IsExpired(Entry entry) => _ttl != Kept && _time.GetElapsedTime(entry.Fetched) > _ttl;

    /// <summary>The listing from the upstream: the one on its way when there is one, else a new one, shared by every caller until it lands.</summary>
    /// <param name="stamp">The folder's stamp as the caller read it, to keep with the listing; null to read it here (<see cref="StampOf"/>).</param>
    /// <param name="why">Why the upstream is asked: told to <see cref="OnListing"/> if this starts a listing.</param>
    private ValueTask<IReadOnlyList<VfsEntry>> FetchAsync(string path, CancellationToken ct, (bool Read, string? Value)? stamp, string why)
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
            OnListing?.Invoke(path, why);
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
                    // unstamped: every walk lists it
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
                    StoreLocked(path, new Entry(_time.GetTimestamp(), result) { Stamp = fetch.Unstamped ? null : stamp });
                    _failures.TryRemove(path, out _);
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

                // a refusal or a failure of the folder's own, where nothing is held to answer instead (FailureKeptFor)
                if (FailureKeptFor is { } keptFor && ex is VfsException { Error: not (VfsError.NotFound or VfsError.NotADirectory) } failure
                    && !fetch.Invalidated && !_entries.ContainsKey(path) && keptFor(failure) is { } span && span > TimeSpan.Zero)
                {
                    _failures[path] = new Failure(_time.GetTimestamp(), span, failure);
                }
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
        // the paths' index counts what is held at no cost: the dictionary's own Count takes every one of its locks, at every
        // listing stored, which made bringing back a big run's listings take half a minute
        if (_keys.Count <= _maxDirectories)
        {
            return;
        }

        foreach (var pair in _entries)
        {
            if (IsExpired(pair.Value))
            {
                RemoveLocked(pair.Key);
            }
        }

        if (_keys.Count <= _maxDirectories)
        {
            return;
        }

        // still over: drop the oldest quarter
        var oldest = _entries.OrderBy(p => p.Value.Fetched).Take(Math.Max(1, _keys.Count / 4)).ToList();
        foreach (var pair in oldest)
        {
            RemoveLocked(pair.Key);
        }
    }

    /// <summary>
    /// A listing held: when it arrived from the upstream (it expires from then) and what it said, with the writes since
    /// applied; and the folder's stamp as read before it (<see cref="StampOf"/>), where the source has one.
    /// </summary>
    private sealed record Entry(long Fetched, IReadOnlyList<VfsEntry> List)
    {
        public string? Stamp { get; init; }
    }

    /// <summary>A write's change to one name in a listing.</summary>
    private sealed record Change(string Name, VfsEntry? Entry, StringComparison Comparison);

    /// <summary>A listing that failed, with nothing held for the folder: when, for how long it answers, and how (<see cref="FailureKeptFor"/>).</summary>
    private sealed record Failure(long At, TimeSpan For, VfsException Error);

    /// <summary>A listing on its way from the upstream, what changed meanwhile, and the folder's stamp where the caller read it.</summary>
    private sealed class Fetch
    {
        public TaskCompletionSource<IReadOnlyList<VfsEntry>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<Change> Changes { get; } = [];

        public bool Invalidated { get; set; }

        public (bool Read, string? Value)? Stamp { get; set; }

        public bool Unstamped { get; set; }
    }
}
