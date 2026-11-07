using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sdk;

/// <summary>
/// The folder listings of a source that is slow to list, kept, and the walks of the whole tree that fill them (spec 6.8):
/// what the source last saw of a folder answers at once, and a walk lists every folder afresh, counting files and folders
/// as it goes. A change upstream shows when a walk comes to it and at no other time: a folder asked for is answered from what
/// is held and nothing is checked behind the answer, so a client going through a kept tree asks nothing of the upstream.
/// "Count files on start" is the first walk (<see cref="CountAsync"/>, from <see cref="ICountable.CountFilesAsync"/>),
/// so after the count every folder opens at once instead of being listed again one by one; a walk follows every
/// <see cref="KeptListingsOptions.WalkEvery"/> after the last ends. A source can start the walks without the count
/// (<see cref="StartWalks"/>: SharePoint does, from its start), or have them go only through the folders that have been asked
/// for until the count is (<see cref="StartWalksOfHeld"/>). Where the source stamps its folders
/// (<see cref="KeptListingsOptions.StampOf"/>), a walk asks each stamped folder for its stamp first and, where it still
/// holds, keeps the folder's listing and every listing below it as they are, so a walk over a tree that has not changed
/// asks for the stamps alone, from one start to the next too (<see cref="KeptListingsOptions.SaveTo"/>); once a
/// <see cref="KeptListingsOptions.FullWalkEvery"/>, a walk lists every folder whatever its stamp. Answer listings and stats
/// from <see cref="Cache"/>, and keep it right after a write as any <see cref="ListingCache"/>. Not kept
/// (<see cref="KeptListingsOptions.Keep"/> off), listings last <see cref="KeptListingsOptions.UnkeptFor"/> (two seconds)
/// and the count is one walk.
/// </summary>
public sealed class KeptListings : IAsyncDisposable
{
    /// <summary>The setting that turns keeping on and off, as the built-in sources call it.</summary>
    public const string KeepSetting = "Keep folder listings";

    /// <summary>The setting for <see cref="KeptListingsOptions.WalkEvery"/>, in minutes, as the built-in sources call it.</summary>
    public const string WalkSetting = "Walk every folder every (minutes)";

    private readonly KeptListingsOptions _options;
    private readonly SourceCounters _counters;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _firstWalk = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _everyFolder = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Task _walking = Task.CompletedTask;
    private bool _started;
    private bool _restored;
    private bool _disposed;

    // the count's state after the last walk ended (an EnumerationState), -1 before the first has
    private volatile int _walked = -1;

    // when the last walk that listed every folder whatever its stamp ended, or the first walk (saved with the listings); null before it
    private DateTimeOffset? _fullWalkAt;

    // what a walk last left out of each folder it left some out of (KeptListingsOptions.LeftOut), saved with the listings: keeping a
    // folder by its stamp, it knows without asking that one below it with no listing was left out on purpose. Asking cost a big
    // GitLab the listing of everything above each of those at every start.
    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> _leftOut = new(StringComparer.Ordinal);

    // where the listings are written as they change (KeptListingsOptions.SaveTo), and what changed beside them since the last
    // write: the folders whose left-out names changed, and whether the time of the last whole walk did
    private readonly ListingsFile? _file;

    // the database that holds the listings (the default; KeptListingsOptions.OnDisk false keeps them in memory, with the
    // file): the file then only ever holds what an earlier run left before, moved in once
    private readonly SqliteListingStore? _disk;
    private readonly Lock _saveGate = new();
    private readonly ConcurrentDictionary<string, byte> _leftOutChanged = new(StringComparer.Ordinal);
    private volatile bool _fullWalkChanged;
    private Task _saveLoop = Task.CompletedTask;

    // the walk of what is held going on now, which the count, when it is asked for, cancels (under _gate)
    private CancellationTokenSource? _heldWalk;

    /// <param name="lister">Lists one folder from the upstream ("" for the root), as for a <see cref="ListingCache"/>.</param>
    /// <param name="counters">The source's own counters: the first walk counts into them as it goes, and each walk sets its total there when done.</param>
    public KeptListings(Func<string, CancellationToken, IAsyncEnumerable<VfsEntry>> lister, SourceCounters counters, ILogger logger, KeptListingsOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(lister);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options ?? new KeptListingsOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.Parallelism, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxInFlight, 1);
        _counters = counters;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        void Listing(string path, string why) => _logger.LogDebug("{Name}: listing '{Path}' from the service: {Why}", _options.Name, path, why);
        var untilNextWalk = _options.WalkEvery > TimeSpan.Zero ? _options.WalkEvery : new KeptListingsOptions().WalkEvery;
        if (_options.Keep && _options.SaveTo is { } saved)
        {
            _file = new ListingsFile(saved, _options.SaveKey ?? "", _logger, _options.Name);
            var onDisk = _options.OnDisk ?? Environment.GetEnvironmentVariable("ANYFS_LISTINGS_ON_DISK") is not ("0" or "false");
            if (onDisk)
            {
                try
                {
                    _disk = new SqliteListingStore(Path.Combine(Path.GetDirectoryName(saved) ?? "", "listings.db"), _options.SaveKey ?? "", _logger, _options.Name);
                }
                catch (IOException ex)
                {
                    // the folder refused, the disk full: this run keeps its listings in memory and the file, as without OnDisk
                    _logger.LogWarning("{Name}: the listings database could not be opened ({Error}); the listings are kept in memory for this run", _options.Name, ex.Message);
                }
            }
        }

        Cache = _options.Keep
            ? new ListingCache(ListingCache.Kept, lister, 10_000, _time, _disk) { OnListing = Listing, StampOf = _options.StampOf, FailureKeptFor = _options.KeepFailures ? _ => untilNextWalk : null }
            : new ListingCache(_options.UnkeptFor, lister, time: _time) { OnListing = Listing, StampOf = _options.StampOf };
        if (_file is not null)
        {
            Load();

            // what is held from here on is written down as it changes, what the last run left not among it
            if (_disk is null)
            {
                Cache.FollowChanges();

                // the file in this format from the start (made where there is none, written whole where it is of a format
                // before), so every save from here on appends whole frames: a write of the whole, to a file beside it moved
                // into place at the end, leaves nothing if the run ends during it, and a first walk's first one, under the
                // walk's load, took longer than the time to the next
                if (_file.MustRewrite)
                {
                    Save();
                }
            }

            if (_options.SaveEvery > TimeSpan.Zero)
            {
                var stopping = _stopping.Token;
                _saveLoop = Task.Run(() => SaveLoopAsync(stopping), CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// The listings an earlier run left for the same <see cref="KeptListingsOptions.SaveKey"/>, however far its walk had got,
    /// with their stamps (a walk keeps a folder whose stamp holds, and everything held below it), what walks leave out, and
    /// when the last walk that listed every folder was.
    /// </summary>
    private void Load()
    {
        if (_disk is not null)
        {
            // what an earlier run left in the file, before the listings were kept on disk, goes into the database once
            if (_disk.Count == 0 && _file!.Exists)
            {
                var moved = _file.Load();
                _disk.Import(moved);
                _file.Delete();
                if (moved.Listings.Count > 0)
                {
                    _logger.LogInformation("{Name}: {Count:N0} folder listings from the last run moved into the database", _options.Name, moved.Listings.Count);
                }
            }
            else if (_file!.Exists)
            {
                // a run that could not open the database kept its listings in the file: the database is the store
                _file.Delete();
            }

            foreach (var (folder, names) in _disk.LeftOut())
            {
                _leftOut[folder] = names;
            }

            _restored = _disk.Count > 0;
            _fullWalkAt = _disk.FullWalkAt;
            if (_restored)
            {
                _logger.LogInformation("{Name}: {Count:N0} folder listings from the last run answer at once", _options.Name, _disk.Count);
            }

            return;
        }

        var saved = _file!.Load();
        foreach (var (folder, listing) in saved.Listings)
        {
            Cache.Restore(folder, listing);
        }

        foreach (var (folder, names) in saved.LeftOut)
        {
            _leftOut[folder] = names;
        }

        _restored = saved.Listings.Count > 0;
        _fullWalkAt = saved.FullWalkAt;
        if (_restored)
        {
            _logger.LogInformation("{Name}: {Count:N0} folder listings from the last run answer at once, {Stamped:N0} of them stamped", _options.Name, saved.Listings.Count, saved.Listings.Values.Count(v => v.Stamp is not null));
        }
    }

    /// <summary>Every <see cref="KeptListingsOptions.SaveEvery"/>, what changed is written down, so a run that ends without stopping (killed, cut off) loses only what changed since.</summary>
    private async Task SaveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_options.SaveEvery, _time, ct).ConfigureAwait(false);
                Save();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Writes down what changed since the last write (<see cref="KeptListingsOptions.SaveTo"/>), appended to the file: a
    /// walk adds what it listed since; a walk over a tree that has not changed, nothing. The whole is written afresh where the
    /// file is another's, of another format or unknown after a failed write, or holds many more records than listings.
    /// </summary>
    private void Save()
    {
        if (_file is null)
        {
            return;
        }

        lock (_saveGate)
        {
            try
            {
                if (_disk is null && (_file.MustRewrite || _file.Records > 2L * Cache.Count + 10_000))
                {
                    // the whole says everything that changed so far
                    Cache.TakeChanges();
                    _leftOutChanged.Clear();
                    _fullWalkChanged = false;
                    _file.Rewrite(Cache.AllHeld(), _fullWalkAt, _leftOut.ToArray().Select(p => (p.Key, p.Value)));
                    return;
                }

                var leftOut = new List<(string Folder, IReadOnlySet<string>? Names)>();
                foreach (var folder in _leftOutChanged.Keys)
                {
                    if (_leftOutChanged.TryRemove(folder, out _))
                    {
                        leftOut.Add((folder, _leftOut.TryGetValue(folder, out var names) ? names : null));
                    }
                }

                var fullWalk = _fullWalkChanged;
                _fullWalkChanged = false;
                if (_disk is not null)
                {
                    // the listings are in the database as they are stored: what is in flight is committed, with the rest
                    _disk.Save(_fullWalkAt, fullWalk, leftOut);
                    return;
                }

                var (cleared, changes) = Cache.TakeChanges();
                if (cleared || changes.Count > 0 || leftOut.Count > 0 || fullWalk)
                {
                    _file.Append(cleared, changes, _fullWalkAt, fullWalk, leftOut);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // the next write is the whole: what the file says after this is not known
                _logger.LogInformation("{Name}: the listings could not be saved for the next run ({Error})", _options.Name, ex.Message);
            }
        }
    }

    /// <summary>The listings: answer <see cref="IVfsOperations.ListAsync"/> and stats from it, and keep it right after a write.</summary>
    public ListingCache Cache { get; }

    public KeptListingsOptions Options => _options;

    /// <summary>What the listings database has done since it was opened (<see cref="KeptListingsOptions.OnDisk"/>); null where the listings are in memory.</summary>
    public ListingStoreStatistics? DiskStatistics => _disk?.Statistics();

    /// <summary>
    /// With listings kept, starts the walks: the first now, counting into the source's counters as it goes, then one every
    /// <see cref="KeptListingsOptions.WalkEvery"/> after the last ends, which counts aside and shows its total when done. A
    /// walk that fails is logged and the next comes as usual. Nothing once they have started, or without keeping.
    /// </summary>
    public void StartWalks()
    {
        CancellationTokenSource? heldWalk;
        lock (_gate)
        {
            _everyFolder.TrySetResult();
            heldWalk = _heldWalk;
            StartLoop();
        }

        // a walk of what is held gives way: the first of every folder begins at once
        try
        {
            heldWalk?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // it ended meanwhile
        }
    }

    /// <summary>
    /// With listings kept, starts walks that go through the folders asked for and no others, for a source that reads nothing
    /// nobody asked for until the count is asked for: one every <see cref="KeptListingsOptions.WalkEvery"/>, and the first at once
    /// where an earlier run saved listings to check. Each checks a stamped folder by its stamp and lists the rest afresh, and
    /// asks a folder whose listing failed again. From the count on (<see cref="CountAsync"/>), the walks go into every folder.
    /// Nothing once they have started, or without keeping.
    /// </summary>
    public void StartWalksOfHeld()
    {
        lock (_gate)
        {
            StartLoop();
        }
    }

    private void StartLoop()
    {
        if (_started || _disposed || !_options.Keep)
        {
            return;
        }

        _started = true;
        var stopping = _stopping.Token;
        _walking = Task.Run(() => LoopAsync(stopping), CancellationToken.None);
    }

    /// <summary>
    /// "Count files on start", for <see cref="ICountable.CountFilesAsync"/>: with listings kept, the first walk (started now
    /// if it has not been) to its end; else one walk now. Either counts into the source's counters, and ends as a lower
    /// bound (<see cref="EnumerationState.Unknown"/>) where the walk left folders out (<see cref="KeptListingsOptions.LeftOut"/>).
    /// </summary>
    public async Task CountAsync(CancellationToken ct)
    {
        if (_options.Keep)
        {
            StartWalks();
            await _firstWalk.Task.WaitAsync(ct).ConfigureAwait(false);

            // the host marks the count in progress as it asks: a walk that ended before then says again how it ended
            if (_walked >= 0)
            {
                _counters.SetState((EnumerationState)_walked);
            }

            return;
        }

        _counters.Set(0, 0, EnumerationState.InProgress);
        if (!await WalkAsync(_counters, ct).ConfigureAwait(false))
        {
            _counters.SetState(EnumerationState.Unknown);
        }
    }

    /// <summary>
    /// One walk: every folder listed afresh into <see cref="Cache"/>, counting files and folders into
    /// <paramref name="counters"/> as it goes and saying there which folder it is in
    /// (<see cref="SourceCounters.CurrentFolder"/>), <see cref="KeptListingsOptions.Parallelism"/> folders at a time (in each part,
    /// all parts at once, where <see cref="KeptListingsOptions.PartsBelow"/> makes parts) and at most
    /// <see cref="KeptListingsOptions.MaxInFlight"/> in all, every folder whatever its stamp. A folder that cannot be listed
    /// is left out, with what is below it, and logged; anything else that fails ends the walk.
    /// </summary>
    /// <returns>Whether it went into every folder: false where <see cref="KeptListingsOptions.LeftOut"/> kept it out of some, or one could not be listed.</returns>
    public Task<bool> WalkAsync(SourceCounters counters, CancellationToken ct) => WalkAsync(counters, full: true, ct);

    /// <summary>
    /// As above; unless <paramref name="full"/>, a stamped folder whose stamp still holds (<see cref="KeptListingsOptions.StampOf"/>)
    /// is kept with everything held below it, counted, and not listed.
    /// </summary>
    public Task<bool> WalkAsync(SourceCounters counters, bool full, CancellationToken ct) => WalkAsync(counters, full, heldAlone: false, ct);

    /// <summary>The walk; with <paramref name="heldAlone"/>, through the folders asked for and no others.</summary>
    private async Task<bool> WalkAsync(SourceCounters counters, bool full, bool heldAlone, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(counters);

        // everything the walk starts from here is the walk's: a client's listing that joins one of its requests waits for it, but its own go first
        Walks.Current = true;
        using var inFlight = new SemaphoreSlim(_options.MaxInFlight, _options.MaxInFlight);
        using var partSlots = _options.PartsAtOnce > 0 ? new SemaphoreSlim(_options.PartsAtOnce, _options.PartsAtOnce) : null;
        var walk = new Walk(counters, inFlight, full) { HeldBelow = heldAlone && _options.ListTree is not null ? CountHeldBelow() : null, PartSlots = partSlots };
        void StartPart(string path, bool held)
        {
            // a part below the first takes its turn now, in the order the parts are found, and walks when it comes
            var turn = path.Length > 0 ? partSlots?.WaitAsync(ct) : null;
            lock (walk.Parts)
            {
                walk.Parts.Add(Task.Run(() => WalkPartAsync(path, held, turn, walk, StartPart, ct), CancellationToken.None));
            }
        }

        StartPart("", heldAlone);
        Exception? failure = null;
        while (true)
        {
            // every part's walk ends before this does, those started while waiting too: none outlives the semaphore
            Task[] started;
            lock (walk.Parts)
            {
                started = [.. walk.Parts];
            }

            try
            {
                await Task.WhenAll(started).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }

            lock (walk.Parts)
            {
                if (walk.Parts.Count == started.Length)
                {
                    break;
                }
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        _lastWalk = (walk.Listed, walk.Kept, walk.Failed);
        return !walk.LeftSomeOut && walk.Failed == 0;
    }

    /// <summary>How many folders are held at each folder and below it.</summary>
    private Dictionary<string, int> CountHeldBelow()
    {
        var below = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in Cache.Paths())
        {
            for (var at = path; ; at = VfsPath.Parent(at))
            {
                below[at] = below.GetValueOrDefault(at) + 1;
                if (at.Length == 0)
                {
                    break;
                }
            }
        }

        return below;
    }

    // how many folders the last walk listed afresh, how many it kept as they were by their stamps, and how many it could not list (for the log)
    private (long Listed, long Kept, long Failed) _lastWalk;

    /// <summary>
    /// Whether a walk now lists every folder whatever its stamp: one each <see cref="KeptListingsOptions.FullWalkEvery"/>
    /// from the first walk (which lists whatever is not held anyway, and keeps what a run before saved by its stamps).
    /// </summary>
    private bool FullWalkDue => _options.StampOf is null || _options.FullWalkEvery <= TimeSpan.Zero || (_fullWalkAt is { } last && _time.GetUtcNow() - last >= _options.FullWalkEvery);

    public async ValueTask DisposeAsync()
    {
        Task walking;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            walking = _walking;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await walking.ConfigureAwait(false);
        await _saveLoop.ConfigureAwait(false);
        _firstWalk.TrySetResult();
        _stopping.Dispose();

        // what changed since the last write: a stop part way through a walk keeps all it listed, and costs only that
        Save();
        _disk?.Dispose();
    }

    // how many folders held below a folder make a walk of what is held read it at once (ListTree) where it can: a client has been
    // through it, and that is the quicker way by far; with fewer, it lists those few and reads nothing nobody asked for
    private const int ReadAtOnceFrom = 50;

    // how long after the source starts a walk of what an earlier run saved begins: a count, asked for at once, makes it one of every folder
    private static readonly TimeSpan CountComesWithin = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The walks: those of what is held until the count is asked for (<see cref="StartWalksOfHeld"/>), then the first of every
    /// folder counting into the source's counters, and one every <see cref="KeptListingsOptions.WalkEvery"/> after the last ended.
    /// </summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        if (!_everyFolder.Task.IsCompleted)
        {
            // with nothing saved, nothing is held that a walk could check yet
            var first = _restored ? CountComesWithin : _options.WalkEvery;
            try
            {
                await (first > TimeSpan.Zero ? Task.WhenAny(_everyFolder.Task, Task.Delay(first, _time, ct)) : _everyFolder.Task.WaitAsync(ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _firstWalk.TrySetResult();
                return;
            }
        }

        var counted = false;
        while (!ct.IsCancellationRequested)
        {
            var everyFolder = _everyFolder.Task.IsCompleted;
            var counting = everyFolder && !counted;
            var counters = counting ? _counters : new SourceCounters();
            var started = _time.GetTimestamp();

            // a walk of what is held gives way to the count when it is asked for (StartWalks), which goes through all of it and
            // more: run to its end, it went through every folder a restart had brought back before the count could begin, and
            // the count then went through them all again
            var givesWay = everyFolder ? null : CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (givesWay is not null)
            {
                lock (_gate)
                {
                    _heldWalk = givesWay;
                    if (_everyFolder.Task.IsCompleted)
                    {
                        givesWay.Cancel();
                    }
                }
            }

            try
            {
                var full = FullWalkDue;
                bool whole;
                try
                {
                    whole = await WalkAsync(counters, full, !everyFolder, givesWay?.Token ?? ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (givesWay is not null && !ct.IsCancellationRequested)
                {
                    _logger.LogInformation("{Name}: the walk of the folders asked for gave way to the count", _options.Name);
                    continue;
                }
                finally
                {
                    if (givesWay is not null)
                    {
                        lock (_gate)
                        {
                            _heldWalk = null;
                        }

                        givesWay.Dispose();
                    }
                }
                if (full || _fullWalkAt is null)
                {
                    _fullWalkAt = _time.GetUtcNow();
                    _fullWalkChanged = true;
                }

                // what this walk could not list stays as it is until the next one, which begins a WalkEvery from now
                Cache.RenewFailures();
                var seconds = _time.GetElapsedTime(started).TotalSeconds;
                var (listed, kept, failed) = _lastWalk;
                var how = kept > 0 ? $"listed {listed:N0} folders afresh and kept {kept:N0} whose stamps hold" : failed > 0 ? $"listed {listed:N0} folders" : "listed every folder";
                if (!everyFolder)
                {
                    if (listed + kept + failed > 0)
                    {
                        _logger.LogInformation("{Name}: checked the folders asked for in {Seconds:F0} s: listed {Listed:N0} afresh, kept {Kept:N0} whose stamps hold, and {Failed:N0} could not be listed", _options.Name, seconds, listed, kept, failed);
                    }
                }
                else
                {
                    var state = whole ? EnumerationState.Complete : EnumerationState.Unknown;
                    _counters.Set(counters.FilesKnown, counters.DirectoriesKnown, state);
                    _walked = (int)state;
                    if (whole)
                    {
                        _logger.LogInformation("{Name}: walked every folder in {Seconds:F0} s ({How}): {Files:N0} files in {Folders:N0} folders", _options.Name, seconds, how, counters.FilesKnown, counters.DirectoriesKnown);
                    }
                    else if (failed > 0)
                    {
                        _logger.LogInformation("{Name}: walked in {Seconds:F0} s ({How}): {Files:N0} files in {Folders:N0} folders, and {Failed:N0} folders could not be listed, which the next walk asks for again", _options.Name, seconds, how, counters.FilesKnown, counters.DirectoriesKnown, failed);
                    }
                    else
                    {
                        _logger.LogInformation("{Name}: walked in {Seconds:F0} s ({How}): {Files:N0} files in {Folders:N0} folders, and the folders it leaves out are listed when opened", _options.Name, seconds, how, counters.FilesKnown, counters.DirectoriesKnown);
                    }
                }

                Save();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // a count that broke off is a lower bound
                if (everyFolder)
                {
                    _walked = (int)EnumerationState.Unknown;
                    if (counting)
                    {
                        _counters.SetState(EnumerationState.Unknown);
                    }
                }

                _logger.LogWarning(ex, "{Name}: walking every folder failed", _options.Name);
            }
            finally
            {
                if (counting)
                {
                    counted = true;
                    _firstWalk.TrySetResult();
                }
            }

            try
            {
                if (_options.WalkEvery > TimeSpan.Zero)
                {
                    // a count asked for meanwhile does not wait for the next walk's time
                    var next = Task.Delay(_options.WalkEvery, _time, ct);
                    await (everyFolder ? next : Task.WhenAny(next, _everyFolder.Task)).ConfigureAwait(false);
                }
                else if (everyFolder)
                {
                    break;
                }
                else
                {
                    await _everyFolder.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _firstWalk.TrySetResult();
    }

    /// <summary>
    /// One part's share of a walk (<see cref="WalkAsync(SourceCounters, bool, CancellationToken)"/>): its folders,
    /// <see cref="KeptListingsOptions.Parallelism"/> at a time; the parts below it start walks of their own, each waiting its turn
    /// where <see cref="KeptListingsOptions.PartsAtOnce"/> gives turns.
    /// </summary>
    private async Task WalkPartAsync(string root, bool rootHeldAlone, Task? turn, Walk walk, Action<string, bool> startPart, CancellationToken ct)
    {
        if (turn is not null)
        {
            await turn.ConfigureAwait(false);
        }

        try
        {
            await WalkPartCoreAsync(root, rootHeldAlone, walk, startPart, ct).ConfigureAwait(false);
        }
        finally
        {
            if (turn is not null)
            {
                walk.PartSlots!.Release();
            }
        }
    }

    private async Task WalkPartCoreAsync(string root, bool rootHeldAlone, Walk walk, Action<string, bool> startPart, CancellationToken ct)
    {
        using var pending = new PendingFolders(_options.Parallelism);
        pending.Add(root, rootHeldAlone);

        async Task WorkerAsync()
        {
            while (await pending.NextAsync(ct).ConfigureAwait(false) is { } next)
            {
                var (folder, heldAlone) = next;
                CountedFolder? at = null;
                try
                {
                    // nobody has asked for it, and nothing says to read what nobody asked for
                    if (heldAlone && !Cache.Asked(folder))
                    {
                        continue;
                    }

                    IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>? tree;
                    IReadOnlyList<VfsEntry> entries;
                    await walk.InFlight.WaitAsync(ct).ConfigureAwait(false);
                    at = walk.Counters.EnterFolder(folder);
                    try
                    {
                        // the folder's stamp first: one whose stamp holds keeps its listing and every listing held below it
                        var held = Cache.StampHeld(folder);
                        var (read, stamp) = await StampAsync(folder, held, ct).ConfigureAwait(false);
                        if (!walk.Full && read && held is not null && stamp == held)
                        {
                            at.Kept();
                            await KeepHeldAsync(folder, heldAlone, walk, pending.Add, ct).ConfigureAwait(false);
                            continue;
                        }

                        tree = heldAlone && (walk.HeldBelow?.GetValueOrDefault(folder) ?? 0) < ReadAtOnceFrom ? null : await TreeOfAsync(folder, walk, ct).ConfigureAwait(false);
                        entries = tree is null ? await Cache.RefreshAsync(folder, stamp, ct).ConfigureAwait(false) : [];
                        if (tree is not null)
                        {
                            // the folder and everything below it at once: all kept and counted, nothing left to go into
                            Cache.KeepTree(folder, tree, stamp);
                            foreach (var listing in tree.Values)
                            {
                                walk.Counters.Add(listing.Count(e => !e.IsDirectory), listing.Count(e => e.IsDirectory));
                                at.Listed(listing.Count);
                            }
                        }
                        else
                        {
                            at.Listed(entries.Count);
                        }
                    }
                    finally
                    {
                        walk.InFlight.Release();
                    }

                    Interlocked.Increment(ref walk.Listed);
                    if (tree is not null)
                    {
                        continue;
                    }

                    var hasFolders = entries.Any(e => e.IsDirectory);
                    var partsBelow = hasFolders && _options.PartsBelow is { } parts && await parts(folder, ct).ConfigureAwait(false);
                    var leftOut = hasFolders && !heldAlone && _options.LeftOut is { } left ? await left(folder, ct).ConfigureAwait(false) : null;
                    if (leftOut is { Count: > 0 })
                    {
                        if (!_leftOut.TryGetValue(folder, out var had) || !had.SetEquals(leftOut))
                        {
                            _leftOut[folder] = leftOut;
                            _leftOutChanged[folder] = 0;
                        }
                    }
                    else if (!heldAlone && _leftOut.TryRemove(folder, out _))
                    {
                        _leftOutChanged[folder] = 0;
                    }

                    long files = 0;
                    var folders = 0;
                    var below = new List<(string Folder, bool HeldAlone)>();
                    foreach (var entry in entries)
                    {
                        if (!entry.IsDirectory)
                        {
                            files++;
                            continue;
                        }

                        folders++;
                        var child = VfsPath.Join(folder, entry.Name);
                        var isLeftOut = leftOut?.Contains(entry.Name) == true;
                        if (isLeftOut)
                        {
                            // not worth a walk, but what has been opened of it is held like the rest, and is checked like the rest
                            walk.LeftSomeOut = true;
                            if (!Cache.Asked(child))
                            {
                                continue;
                            }
                        }

                        if (partsBelow)
                        {
                            startPart(child, heldAlone || isLeftOut);
                        }
                        else
                        {
                            below.Add((child, heldAlone || isLeftOut));
                        }
                    }

                    pending.AddAll(below);
                    walk.Counters.Add(files, folders);
                }
                catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
                {
                    at?.Failed(ex, ct);
                    throw;
                }
                catch (Exception ex)
                {
                    // one gone since its parent was listed is nothing left out
                    if (ex is not VfsException { Error: VfsError.NotFound or VfsError.NotADirectory })
                    {
                        Interlocked.Increment(ref walk.Failed);
                    }

                    at?.Failed(ex, ct);
                    _logger.LogDebug("{Name}: the walk left out '{Folder}': {Error}", _options.Name, folder, ex.Message);
                }
                finally
                {
                    at?.Dispose();
                    pending.Done();
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, _options.Parallelism).Select(_ => Task.Run(WorkerAsync, CancellationToken.None))).ConfigureAwait(false);
    }

    /// <summary>
    /// The folders one part of a walk has still to go into: the last found first, those of one folder in the order it listed them,
    /// so the walk has done with a folder and all that is below it before it goes on to the next. Level by level, a big tree's
    /// walk came back to each folder long after what it had asked about it had aged out, and asked it all again.
    /// </summary>
    private sealed class PendingFolders(int workers) : IDisposable
    {
        private readonly Stack<(string Folder, bool HeldAlone)> _folders = new();
        private readonly SemaphoreSlim _ready = new(0);
        private int _outstanding;

        public void Add(string folder, bool heldAlone)
        {
            Interlocked.Increment(ref _outstanding);
            lock (_folders)
            {
                _folders.Push((folder, heldAlone));
            }

            _ready.Release();
        }

        public void AddAll(List<(string Folder, bool HeldAlone)> folders)
        {
            if (folders.Count == 0)
            {
                return;
            }

            Interlocked.Add(ref _outstanding, folders.Count);
            lock (_folders)
            {
                for (var i = folders.Count - 1; i >= 0; i--)
                {
                    _folders.Push(folders[i]);
                }
            }

            _ready.Release(folders.Count);
        }

        /// <summary>The next folder to go into; null once every folder is done, which is when no more can be found.</summary>
        public async ValueTask<(string Folder, bool HeldAlone)?> NextAsync(CancellationToken ct)
        {
            await _ready.WaitAsync(ct).ConfigureAwait(false);
            lock (_folders)
            {
                return _folders.TryPop(out var next) ? next : null;
            }
        }

        /// <summary>One taken is done, with what it found added: the last of them ends every worker's wait.</summary>
        public void Done()
        {
            if (Interlocked.Decrement(ref _outstanding) == 0)
            {
                _ready.Release(workers);
            }
        }

        public void Dispose() => _ready.Dispose();
    }

    /// <summary>
    /// The listings of a folder and of every folder below it at once (<see cref="KeptListingsOptions.ListTree"/>); null where there
    /// is no quicker way than folder by folder. Where that fails (too much at once for the upstream, or one thing in it that it
    /// cannot give), the rest of this walk goes folder by folder, so what can be listed is.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>?> TreeOfAsync(string folder, Walk walk, CancellationToken ct)
    {
        if (_options.ListTree is not { } listTree || walk.FolderByFolder)
        {
            return null;
        }

        try
        {
            return await listTree(folder, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            walk.FolderByFolder = true;
            _logger.LogInformation("{Name}: '{Folder}' could not be read at once ({Error}); the walk goes on folder by folder", _options.Name, folder, ex.Message);
            return null;
        }
    }

    /// <summary>A folder's stamp now (<see cref="KeptListingsOptions.StampOf"/>, given the one held): whether it could be read, and what it is. Nothing without stamps; one that cannot be read is logged and counts as none.</summary>
    private async ValueTask<(bool Read, string? Stamp)> StampAsync(string folder, string? held, CancellationToken ct)
    {
        if (_options.StampOf is not { } stampOf)
        {
            return (false, null);
        }

        try
        {
            return (true, await stampOf(folder, held, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("{Name}: the stamp of '{Folder}' could not be read ({Error}); listed afresh", _options.Name, folder, ex.Message);
            return (false, null);
        }
    }

    /// <summary>
    /// Keeps a folder's listing and every listing held below it as they are (a walk found its stamp holds), counting them;
    /// a folder below it with no listing held (never listed, or let go) is walked as usual, unless it is one a walk leaves
    /// out (<see cref="KeptListingsOptions.LeftOut"/>) or the walk goes through what is held alone, where only one whose listing
    /// failed is.
    /// </summary>
    private async Task KeepHeldAsync(string folder, bool heldAlone, Walk walk, Action<string, bool> enqueue, CancellationToken ct)
    {
        var held = Cache.HeldUnder(folder);
        var paths = new HashSet<string>(held.Count, StringComparer.Ordinal);
        foreach (var (path, _) in held)
        {
            paths.Add(path);
        }

        foreach (var (path, listing) in held)
        {
            Interlocked.Increment(ref walk.Kept);
            long files = listing.Files;
            var folders = listing.Folders;
            if (folders == 0)
            {
                walk.Counters.Add(files, folders);
                continue;
            }

            IReadOnlySet<string>? leftOut = null;
            var asked = false;
            foreach (var name in listing.FolderNames())
            {
                var child = VfsPath.Join(path, name);
                if (paths.Contains(child))
                {
                    continue;
                }

                if (heldAlone)
                {
                    enqueue(child, true);
                    continue;
                }

                if (!asked)
                {
                    asked = true;
                    leftOut = _leftOut.TryGetValue(path, out var known) ? known : _options.LeftOut is { } left ? await left(path, ct).ConfigureAwait(false) : null;
                }

                if (leftOut?.Contains(name) == true)
                {
                    walk.LeftSomeOut = true;
                    enqueue(child, true);
                    continue;
                }

                enqueue(child, false);
            }

            walk.Counters.Add(files, folders);
        }
    }

    /// <summary>One walk's shared state: what it counts into, the listings in flight, its parts, whether it lists every folder whatever its stamp, and what it left out, listed, kept and could not list.</summary>
    private sealed class Walk(SourceCounters counters, SemaphoreSlim inFlight, bool full)
    {
        public SourceCounters Counters { get; } = counters;

        public SemaphoreSlim InFlight { get; } = inFlight;

        public bool Full { get; } = full;

        /// <summary>In a walk of what is held, how many folders are held at each folder and below it (<see cref="ReadAtOnceFrom"/>); null otherwise.</summary>
        public Dictionary<string, int>? HeldBelow { get; init; }

        /// <summary>The turns the parts below the first take (<see cref="KeptListingsOptions.PartsAtOnce"/>); null where every part walks at once.</summary>
        public SemaphoreSlim? PartSlots { get; init; }

        public List<Task> Parts { get; } = [];

        public volatile bool LeftSomeOut;

        public volatile bool FolderByFolder;

        public long Listed;

        public long Kept;

        public long Failed;
    }
}

/// <summary>How a source's listings are kept and its tree walked (<see cref="KeptListings"/>).</summary>
public sealed record KeptListingsOptions
{
    /// <summary>Whether listings are kept until a walk lists them afresh; off, they last <see cref="UnkeptFor"/> and the count is one walk.</summary>
    public bool Keep { get; init; } = true;

    /// <summary>Without keeping, how long a listing lasts (two seconds: clients stat every file they list); zero lists every time, for a source whose own caches decide.</summary>
    public TimeSpan UnkeptFor { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>With listings kept, how long after one walk ends the next begins; zero (or less) for the first walk alone.</summary>
    public TimeSpan WalkEvery { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// With listings kept, whether a folder whose listing failed answers with that failure at once until the next walk, which
    /// lists it again, so a client that lists the tree over and over pays for it once; where no walk is to come
    /// (<see cref="WalkEvery"/> zero), for as long as the walks' usual interval. <see cref="ListingCache.ForgetFailures"/> lets
    /// them all go (a sign-in renewed), and a folder not found is never kept so. Off (the default) asks whenever asked.
    /// </summary>
    public bool KeepFailures { get; init; }

    /// <summary>Folders listed at a time in a walk: in each part, where <see cref="PartsBelow"/> makes parts.</summary>
    public int Parallelism { get; init; } = TreeCount.DefaultParallelism;

    /// <summary>Listings in flight in a walk in all, whatever the number of parts.</summary>
    public int MaxInFlight { get; init; } = 64;

    /// <summary>
    /// With <see cref="SaveTo"/>, whether the listings are kept on disk, in a database beside the file, with the last
    /// few thousand asked for in memory (a source of tens of millions of files fits, and the start reads nothing back),
    /// or in memory with the file written as they change (false). Null, the default, keeps them on disk, unless the
    /// environment says <c>ANYFS_LISTINGS_ON_DISK=0</c>. What a file holds from before is moved into the database once.
    /// </summary>
    public bool? OnDisk { get; init; }

    /// <summary>
    /// Whether the folders in a folder are each walked as a part of their own, <see cref="Parallelism"/> folders of each at a
    /// time and every part at once (SharePoint's sites, so a big site does not hold up the others), or <see cref="PartsAtOnce"/>
    /// of them at once; null for one part.
    /// </summary>
    public Func<string, CancellationToken, ValueTask<bool>>? PartsBelow { get; init; }

    /// <summary>
    /// With <see cref="PartsBelow"/>, how many parts walk at once, the rest waiting their turn in the order they were found
    /// (GitLab's projects: a server's walk goes through a few at a time, each one's folders together, and comes to each in turn);
    /// zero, the default, walks every part at once. The part a walk starts from takes no turn.
    /// </summary>
    public int PartsAtOnce { get; init; }

    /// <summary>
    /// Of the folders in a folder, the names of those a walk does not go into, because listing them costs requests of their
    /// own (GitLab's issues): they are listed when opened, what has been opened of them is checked by the walks like the rest, and
    /// the count ends as a lower bound (<see cref="EnumerationState.Unknown"/>). Null, or none, goes into every folder.
    /// </summary>
    public Func<string, CancellationToken, ValueTask<IReadOnlySet<string>?>>? LeftOut { get; init; }

    /// <summary>
    /// The listings of a folder and of every folder below it at once, by path (the folder's own among them), where the source
    /// knows a quicker way than folder by folder (GitLab's repository: its whole tree a hundred entries a request); null for
    /// a folder it does not, which the walk lists alone.
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>?>>? ListTree { get; init; }

    /// <summary>What the log calls the source, at the start of its lines ("sharepoint").</summary>
    public string Name { get; init; } = "source";

    /// <summary>
    /// With listings kept, the file they are saved to (in the source's state folder) after each walk and when the source
    /// stops, and read back from at its next start: folders then open at once, until the first walk lists them afresh.
    /// Null keeps them for the run alone.
    /// </summary>
    public string? SaveTo { get; init; }

    /// <summary>
    /// What the saved listings must have been saved for to be read back (the account, the server), with whatever else the
    /// listings depend on (the settings that shape the tree, a version of the source's own that moves when what it lists or
    /// how it measures its files changes): another's are not.
    /// </summary>
    public string? SaveKey { get; init; }

    /// <summary>
    /// With <see cref="SaveTo"/>, how often what changed is written down (thirty seconds): appended to the file as a walk goes,
    /// so a source stopped part way through its first walk starts the next run from what it had, and one that ends without
    /// stopping (its process killed, the machine turned off) loses only what changed since. Zero writes only after each walk
    /// and when the source stops.
    /// </summary>
    public TimeSpan SaveEvery { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A folder's stamp (<see cref="ListingCache.StampOf"/>): what its listing and every listing below it depend on, as the
    /// upstream tells it cheaply. A walk keeps a stamped folder whose stamp holds, and everything held below it, as they
    /// are. Null (the default) stamps nothing: every walk lists every folder.
    /// </summary>
    public Func<string, string?, CancellationToken, ValueTask<string?>>? StampOf { get; init; }

    /// <summary>
    /// With <see cref="StampOf"/>: how often a walk lists every folder whatever its stamp, so what a stamp does not cover
    /// (a reaction on an old message, a file changed deep in a library whose stamp was read a moment early) shows by then;
    /// a day by default. Zero or less makes every walk such a walk.
    /// </summary>
    public TimeSpan FullWalkEvery { get; init; } = TimeSpan.FromDays(1);
}

/// <summary>The listings a run saved before the file became a log (<see cref="ListingsFile"/>): read once, and written in the new format at the first save.</summary>
internal sealed record SavedListings(int Version, string Key, Dictionary<string, List<VfsEntry>> Listings)
{
    public Dictionary<string, string>? Stamps { get; init; }

    public DateTimeOffset? FullWalkAt { get; init; }

    public Dictionary<string, List<string>>? LeftOut { get; init; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SavedListings))]
internal sealed partial class SavedListingsJson : System.Text.Json.Serialization.JsonSerializerContext;
