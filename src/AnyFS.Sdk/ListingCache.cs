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
/// A cache that keeps its listings holds them as bytes (<see cref="ListingCodec"/>), in memory or on disk
/// (<see cref="IListingStore"/>), and makes a folder's entries when a client asks for them, holding the last few thousand
/// made (<see cref="HotListings"/>): clients open a folder and stat what is in it, and a search goes through each folder once.
/// </summary>
public sealed class ListingCache
{
    /// <summary>The time a listing is kept for when it is kept until listed afresh or invalidated.</summary>
    public static readonly TimeSpan Kept = Timeout.InfiniteTimeSpan;

    // how many folders' entries, made from their bytes for answering, are held at once (the first in, the first out)
    private const int HotListings = 2_048;

    private readonly TimeSpan _ttl;
    private readonly Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> _lister;
    private readonly int _maxDirectories;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly IListingStore _store;
    private readonly bool _packed;
    private readonly ConcurrentDictionary<string, Failure> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Fetch> _fetches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HotListing> _hot = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _hotOrder = new();
    private int _hotCount;
    private long _version;

    /// <param name="ttl">How long a listing is good for, or <see cref="Kept"/>.</param>
    public ListingCache(TimeSpan ttl, Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> lister, int maxDirectories = 10_000, TimeProvider? time = null)
        : this(ttl, lister, maxDirectories, time, null)
    {
    }

    /// <param name="store">Where the listings are held; in memory when null.</param>
    internal ListingCache(TimeSpan ttl, Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> lister, int maxDirectories, TimeProvider? time, IListingStore? store)
    {
        ArgumentNullException.ThrowIfNull(lister);
        _ttl = ttl;
        _lister = lister;
        _maxDirectories = maxDirectories;
        _time = time ?? TimeProvider.System;
        _store = store ?? new MemoryListingStore();
        _packed = ttl == Kept;
    }

    /// <summary>How many listings are held.</summary>
    public int Count => _store.Count;

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
    public IReadOnlyList<VfsEntry>? Held(string path) => Hot(path)?.List;

    /// <summary>Whether <paramref name="path"/> has been asked for: a listing of it is held, or its listing failed and that is kept (<see cref="FailureKeptFor"/>).</summary>
    public bool Asked(string path) => Held(path) is not null || _failures.ContainsKey(path);

    /// <summary>The listing of <paramref name="path"/> (a normalised directory path), from the cache when fresh.</summary>
    public ValueTask<IReadOnlyList<VfsEntry>> GetAsync(string path, CancellationToken ct)
    {
        if (Hot(path) is { } hot)
        {
            return ValueTask.FromResult(hot.List);
        }

        return Ask(path, ct);
    }

    /// <summary>The listing of a folder not held: its failure kept, where it has one, else the upstream's answer.</summary>
    private ValueTask<IReadOnlyList<VfsEntry>> Ask(string path, CancellationToken ct)
    {
        if (_failures.TryGetValue(path, out var failed) && _time.GetElapsedTime(failed.At) < failed.For)
        {
            return ValueTask.FromException<IReadOnlyList<VfsEntry>>(new VfsException(failed.Error.Error, failed.Error.Message, failed.Error.InnerException));
        }

        return FetchAsync(path, ct, null, failed is not null
            ? $"it failed {_time.GetElapsedTime(failed.At).TotalSeconds:F0} s ago and that failure was kept {failed.For.TotalSeconds:F0} s ({failed.Error.Message})"
            : _store.Get(path) is not null ? "its listing is past its time" : "no listing is held");
    }

    /// <summary>
    /// The listing held of <paramref name="path"/> with its entries made, from those made lately where it is among them;
    /// null where none is held or it is past its time.
    /// </summary>
    private HotListing? Hot(string path)
    {
        if (_hot.TryGetValue(path, out var hot))
        {
            if (!IsExpired(hot.Source))
            {
                return hot;
            }

            _hot.TryRemove(path, out _);
        }

        var seen = Volatile.Read(ref _version);
        var stored = _store.Get(path);
        if (stored is null || IsExpired(stored))
        {
            return null;
        }

        hot = new HotListing(stored.Entries(), stored);
        if (_hot.TryAdd(path, hot))
        {
            // the first in is the first out: a client opens a folder and stats what is in it, a search goes through each once
            _hotOrder.Enqueue(path);
            if (Interlocked.Increment(ref _hotCount) > HotListings && _hotOrder.TryDequeue(out var oldest))
            {
                Interlocked.Decrement(ref _hotCount);
                _hot.TryRemove(oldest, out _);
            }
        }
        else if (_hot.TryGetValue(path, out var there))
        {
            hot = there;
        }

        // stored again between the reading and the holding (something changed meanwhile, and it may have been this):
        // what was made is answered, and not held
        if (Volatile.Read(ref _version) != seen && (_store.Get(path) is not { } now || !(ReferenceEquals(now, stored) || (now.Packed is not null && now.SameAs(stored)))))
        {
            _hot.TryRemove(path, out _);
        }

        return hot;
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
        var stored = Make(listing, stamp);
        lock (_gate)
        {
            StoreLocked(path, stored);
            _failures.TryRemove(path, out _);
            TrimIfNeededLocked();
        }
    }

    /// <summary>The listings held now, by folder path (every one made from its bytes: for a cache that keeps a big tree, a lot).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>> Snapshot()
    {
        var all = new Dictionary<string, IReadOnlyList<VfsEntry>>(StringComparer.Ordinal);
        foreach (var (path, listing) in _store.All())
        {
            if (!IsExpired(listing))
            {
                all[path] = listing.Entries();
            }
        }

        return all;
    }

    /// <summary>The stamps held with the listings (<see cref="StampOf"/>), by folder path, for the folders that have one.</summary>
    public IReadOnlyDictionary<string, string> Stamps()
    {
        var stamps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, listing) in _store.All())
        {
            if (listing.Stamp is not null && !IsExpired(listing))
            {
                stamps[path] = listing.Stamp;
            }
        }

        return stamps;
    }

    /// <summary>The stamp held with the listing of <paramref name="path"/>; null where there is none.</summary>
    public string? StampHeld(string path) => _store.Get(path) is { } held && !IsExpired(held) ? held.Stamp : null;

    /// <summary>The listings held of <paramref name="folder"/> and of every folder below it, by path (none where none is held).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>> Under(string folder)
    {
        var held = new Dictionary<string, IReadOnlyList<VfsEntry>>(StringComparer.Ordinal);
        foreach (var (path, listing) in HeldUnder(folder))
        {
            held[path] = listing.Entries();
        }

        return held;
    }

    /// <summary>What is held of <paramref name="folder"/> and of every folder below it, as held, nothing made: for a walk that keeps a subtree as it is.</summary>
    internal List<(string Path, StoredListing Listing)> HeldUnder(string folder)
    {
        List<(string Path, StoredListing Listing)> under;
        lock (_gate)
        {
            under = _store.Under(folder);
        }

        under.RemoveAll(h => IsExpired(h.Listing));
        return under;
    }

    /// <summary>Every path held, in order: for a walk that counts what is held below each folder.</summary>
    internal List<string> Paths()
    {
        lock (_gate)
        {
            return _store.Paths();
        }
    }

    /// <summary>
    /// Holds <paramref name="listing"/> for <paramref name="path"/> (under the gate): the version moves, and what was made of
    /// the folder's entries for answering is let go. The same listing again, with the same stamp (a walk that found a folder
    /// as it was), moves only when it was listed: a walk over a tree that has not changed leaves nothing to save. In a cache
    /// that keeps its listings, a folder the new listing no longer names is let go with everything below it: nothing can
    /// reach it, and it would be held, and saved, for ever.
    /// </summary>
    private void StoreLocked(string path, StoredListing listing)
    {
        var held = _store.Get(path);
        if (held is not null && held.SameAs(listing))
        {
            held.Fetched = listing.Fetched;
            return;
        }

        if (_packed && held is { Folders: > 0 })
        {
            var still = new HashSet<string>(listing.FolderNames(), StringComparer.Ordinal);
            foreach (var name in held.FolderNames())
            {
                if (!still.Contains(name))
                {
                    InvalidateTreeLocked(VfsPath.Join(path, name));
                }
            }
        }

        if (held is null)
        {
            _store.Set(path, listing);
        }
        else
        {
            _store.Replace(path, listing);
        }

        _hot.TryRemove(path, out _);
        _changes?.Add(path);
        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// Keeps the listings of <paramref name="root"/> and of every folder below it, as a walk that reads a whole subtree at once
    /// has them (<see cref="KeptListingsOptions.ListTree"/>), with <paramref name="stamp"/> on the root's: each in place of what
    /// is held, as <see cref="Keep"/> would, and whatever was held below the root that the tree does not name let go. What is
    /// held below the root is read once, in order, not folder by folder, and only what differs is stored.
    /// </summary>
    public void KeepTree(string root, IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>> tree, string? stamp = null)
    {
        ArgumentNullException.ThrowIfNull(tree);

        // made outside the gate: packing is the bulk of the work
        var made = new List<(string Path, StoredListing Listing)>(tree.Count);
        foreach (var (path, listing) in tree)
        {
            made.Add((path, Make(listing, path == root ? stamp : null)));
        }

        lock (_gate)
        {
            var held = new Dictionary<string, StoredListing>(StringComparer.Ordinal);
            foreach (var (path, listing) in _store.Under(root))
            {
                held[path] = listing;
            }

            foreach (var (path, listing) in made)
            {
                _failures.TryRemove(path, out _);
                if (held.Remove(path, out var was))
                {
                    if (was.SameAs(listing))
                    {
                        was.Fetched = listing.Fetched;
                        continue;
                    }

                    _store.Replace(path, listing);
                }
                else
                {
                    _store.Set(path, listing);
                }

                _hot.TryRemove(path, out _);
                _changes?.Add(path);
                Interlocked.Increment(ref _version);
            }

            // held below the root, and not in the tree: gone, with nothing to reach it by
            foreach (var path in held.Keys)
            {
                RemoveLocked(path);
            }

            TrimIfNeededLocked();
        }
    }

    /// <summary>Lets go of what is held for <paramref name="path"/> (under the gate); false where nothing was.</summary>
    private bool RemoveLocked(string path)
    {
        if (!_store.Remove(path))
        {
            return false;
        }

        _hot.TryRemove(path, out _);
        _changes?.Add(path);
        Interlocked.Increment(ref _version);
        return true;
    }

    /// <summary>A listing as this cache holds it: as bytes where it keeps its listings, as it is where it holds them for a time.</summary>
    private StoredListing Make(IReadOnlyList<VfsEntry> listing, string? stamp, long? fetched = null)
        => _packed ? StoredListing.Pack(fetched ?? _time.GetTimestamp(), stamp, listing) : new StoredListing(fetched ?? _time.GetTimestamp(), stamp, listing);

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
    /// changed folder as it is held now, or no listing where it is held no more. Taken, so the next time says only what
    /// changes after this.
    /// </summary>
    internal (bool Cleared, List<(string Path, StoredListing? Listing)> Changes) TakeChanges()
    {
        lock (_gate)
        {
            var changes = new List<(string, StoredListing?)>(_changes?.Count ?? 0);
            foreach (var path in _changes ?? [])
            {
                changes.Add((path, _store.Get(path)));
            }

            _changes?.Clear();
            var cleared = _clearedSinceTaken;
            _clearedSinceTaken = false;
            return (cleared, changes);
        }
    }

    /// <summary>Every listing held now, nothing copied: for writing them all down (a moment's view; changes meanwhile are followed as usual).</summary>
    internal IEnumerable<(string Path, StoredListing Listing)> AllHeld()
    {
        foreach (var (path, listing) in _store.All())
        {
            if (!IsExpired(listing))
            {
                yield return (path, listing);
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

            if (_store.Get(path) is { Stamp: not null } held)
            {
                StoreLocked(path, held.WithStamp(null));
            }
        }
    }

    /// <summary>Counts the listing held of <paramref name="path"/> as listed afresh now (a walk found its stamp unchanged); nothing where none is held.</summary>
    public void Touch(string path)
    {
        lock (_gate)
        {
            if (_store.Get(path) is { } held && !IsExpired(held))
            {
                // when it was listed is not saved: no change to the version
                held.Fetched = _time.GetTimestamp();
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
        Restore(path, Make(listing, stamp));
    }

    /// <summary>The same, with the listing as it was saved.</summary>
    internal void Restore(string path, StoredListing saved)
    {
        saved.Fetched = _time.GetTimestamp();
        lock (_gate)
        {
            if (_store.Get(path) is not null)
            {
                return;
            }

            StoreLocked(path, saved);
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
        try
        {
            if (Hot(parent) is { } hot)
            {
                return hot.Find(name, comparison);
            }

            var listing = await Ask(parent, ct).ConfigureAwait(false);
            return Hot(parent)?.Find(name, comparison) ?? HotListing.Scan(listing, name, comparison);
        }
        catch (VfsException ex) when (ex.Error is VfsError.NotFound or VfsError.NotADirectory)
        {
            return null;
        }
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
            if (_store.Get(parent) is { } held && !IsExpired(held))
            {
                StoreLocked(parent, Make(Apply(held.Entries(), [change]), held.Stamp, held.Fetched));
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
            InvalidateTreeLocked(directoryPath);
        }
    }

    private void InvalidateTreeLocked(string directoryPath)
    {
        foreach (var (path, _) in _store.Under(directoryPath))
        {
            RemoveLocked(path);
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

    public void InvalidateAll()
    {
        lock (_gate)
        {
            _store.Clear();
            _hot.Clear();
            _hotOrder.Clear();
            Volatile.Write(ref _hotCount, 0);
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

    private bool IsExpired(StoredListing listing) => _ttl != Kept && _time.GetElapsedTime(listing.Fetched) > _ttl;

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
                var held = _store.Get(path)?.Stamp;
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
                    StoreLocked(path, Make(result, fetch.Unstamped ? null : stamp));
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
                    && !fetch.Invalidated && _store.Get(path) is null && keptFor(failure) is { } span && span > TimeSpan.Zero)
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

    /// <summary>A cache that holds listings for a time holds at most so many: past that, those past their time go, then the oldest quarter. One that keeps its listings holds the tree.</summary>
    private void TrimIfNeededLocked()
    {
        if (_packed || _store.Count <= _maxDirectories)
        {
            return;
        }

        foreach (var (path, listing) in _store.All().ToList())
        {
            if (IsExpired(listing))
            {
                RemoveLocked(path);
            }
        }

        if (_store.Count <= _maxDirectories)
        {
            return;
        }

        // still over: drop the oldest quarter
        var oldest = _store.All().OrderBy(p => p.Listing.Fetched).Take(Math.Max(1, _store.Count / 4)).Select(p => p.Path).ToList();
        foreach (var path in oldest)
        {
            RemoveLocked(path);
        }
    }

    /// <summary>
    /// A folder's entries made for answering, from the listing held: with an index of their names once they have been
    /// looked up a few times, since clients stat every file they list, and a scan of a folder of thousands for each is
    /// the square of its size.
    /// </summary>
    private sealed class HotListing(IReadOnlyList<VfsEntry> list, StoredListing source)
    {
        private const int IndexFrom = 32;
        private Dictionary<string, VfsEntry>? _ordinal;
        private Dictionary<string, VfsEntry>? _ignoringCase;
        private int _lookups;

        public IReadOnlyList<VfsEntry> List { get; } = list;

        /// <summary>What the entries were made from: its time is theirs.</summary>
        public StoredListing Source { get; } = source;

        public VfsEntry? Find(string name, StringComparison comparison)
        {
            if (List.Count < IndexFrom || Interlocked.Increment(ref _lookups) < 4)
            {
                return Scan(List, name, comparison);
            }

            var index = comparison switch
            {
                StringComparison.Ordinal => _ordinal ??= Index(StringComparer.Ordinal),
                StringComparison.OrdinalIgnoreCase => _ignoringCase ??= Index(StringComparer.OrdinalIgnoreCase),
                _ => null,
            };
            if (index is null)
            {
                return Scan(List, name, comparison);
            }

            return index.TryGetValue(name, out var entry) ? entry : null;
        }

        /// <summary>The first entry of that name, as a scan finds it.</summary>
        public static VfsEntry? Scan(IReadOnlyList<VfsEntry> list, string name, StringComparison comparison)
        {
            foreach (var entry in list)
            {
                if (string.Equals(entry.Name, name, comparison))
                {
                    return entry;
                }
            }

            return null;
        }

        private Dictionary<string, VfsEntry> Index(StringComparer comparer)
        {
            var index = new Dictionary<string, VfsEntry>(List.Count, comparer);
            foreach (var entry in List)
            {
                // the first of a name wins, as in a scan
                index.TryAdd(entry.Name, entry);
            }

            return index;
        }
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
