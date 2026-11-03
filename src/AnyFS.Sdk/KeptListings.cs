using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sdk;

/// <summary>
/// The folder listings of a source that is slow to list, kept, and the walks of the whole tree that fill them (spec 6.8):
/// what the source last saw of a folder answers at once, and a walk lists every folder afresh, counting files and folders
/// as it goes. "Count files on start" is the first walk (<see cref="CountAsync"/>, from <see cref="ICountable.CountFilesAsync"/>),
/// so after the count every folder opens at once instead of being listed again one by one; a walk follows every
/// <see cref="KeptListingsOptions.WalkEvery"/> after the last ends, and a source can start the walks without the count
/// (<see cref="StartWalks"/>: SharePoint does, from its start). Where the source stamps its folders
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
        Cache = _options.Keep
            ? new ListingCache(ListingCache.Kept, lister, _options.MaxFolders, _time) { OnListing = Listing, RefreshAfter = _options.RefreshAfter, MaxRefreshesBehind = _options.MaxRefreshesBehind, StampOf = _options.StampOf, FailureKeptFor = _options.FailureKeptFor }
            : new ListingCache(_options.UnkeptFor, lister, time: _time) { OnListing = Listing, StampOf = _options.StampOf };
        if (_options.Keep && _options.SaveTo is { } saved)
        {
            Load(saved);
        }
    }

    private const int SavedVersion = 2;

    /// <summary>
    /// The listings an earlier run saved for the same <see cref="KeptListingsOptions.SaveKey"/>, kept as old (listed afresh
    /// as they are opened, or by the first walk) with their stamps (a folder whose stamp holds is not), and when the last
    /// walk that listed every folder was.
    /// </summary>
    private void Load(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var zip = new GZipStream(file, CompressionMode.Decompress);
            if (JsonSerializer.Deserialize(zip, SavedListingsJson.Default.SavedListings) is not { Version: SavedVersion } saved || saved.Key != (_options.SaveKey ?? ""))
            {
                return;
            }

            foreach (var (folder, listing) in saved.Listings)
            {
                Cache.Restore(folder, listing, saved.Stamps?.GetValueOrDefault(folder));
            }

            _restored = saved.Listings.Count > 0;
            _fullWalkAt = saved.FullWalkAt;
            _logger.LogInformation("{Name}: {Count:N0} folder listings from the last run answer at once, {Stamped:N0} of them stamped; the rest are listed afresh as they are opened", _options.Name, saved.Listings.Count, saved.Stamps?.Count ?? 0);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // the first run
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            _logger.LogInformation("{Name}: the listings saved by the last run could not be read ({Error}); listing afresh", _options.Name, ex.Message);
        }
    }

    /// <summary>Saves the listings held now (<see cref="KeptListingsOptions.SaveTo"/>): written whole beside the file and moved into place, so a crash leaves the last good one.</summary>
    private void Save()
    {
        if (!_options.Keep || _options.SaveTo is not { } path)
        {
            return;
        }

        try
        {
            var saved = new SavedListings(SavedVersion, _options.SaveKey ?? "", Cache.Snapshot().ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.Ordinal))
            {
                Stamps = Cache.Stamps().ToDictionary(StringComparer.Ordinal),
                FullWalkAt = _fullWalkAt,
            };
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            var temp = path + ".tmp";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new GZipStream(file, CompressionLevel.Fastest))
            {
                JsonSerializer.Serialize(zip, saved, SavedListingsJson.Default.SavedListings);
            }

            // an antivirus holding the old file refuses the replace for a moment
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException) && attempt < 6)
                {
                    Thread.Sleep(10 << (attempt - 1));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogInformation("{Name}: the listings could not be saved for the next run ({Error})", _options.Name, ex.Message);
        }
    }

    /// <summary>The listings: answer <see cref="IVfsOperations.ListAsync"/> and stats from it, and keep it right after a write.</summary>
    public ListingCache Cache { get; }

    public KeptListingsOptions Options => _options;

    /// <summary>
    /// With listings kept, starts the walks: the first now, counting into the source's counters as it goes, then one every
    /// <see cref="KeptListingsOptions.WalkEvery"/> after the last ends, which counts aside and shows its total when done. A
    /// walk that fails is logged and the next comes as usual. Nothing once they have started, or without keeping.
    /// </summary>
    public void StartWalks()
    {
        lock (_gate)
        {
            _everyFolder.TrySetResult();
            StartLoop();
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
    /// <paramref name="counters"/> as it goes, <see cref="KeptListingsOptions.Parallelism"/> folders at a time (in each part,
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
        using var inFlight = new SemaphoreSlim(_options.MaxInFlight, _options.MaxInFlight);
        var walk = new Walk(counters, inFlight, full);
        void StartPart(string path, bool held)
        {
            lock (walk.Parts)
            {
                walk.Parts.Add(Task.Run(() => WalkPartAsync(path, held, walk, StartPart, ct), CancellationToken.None));
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
        _firstWalk.TrySetResult();
        _stopping.Dispose();
        Save();
    }

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
            try
            {
                var full = FullWalkDue;
                var whole = await WalkAsync(counters, full, !everyFolder, ct).ConfigureAwait(false);
                if (full || _fullWalkAt is null)
                {
                    _fullWalkAt = _time.GetUtcNow();
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

    /// <summary>One part's share of a walk (<see cref="WalkAsync(SourceCounters, bool, CancellationToken)"/>): its folders, <see cref="KeptListingsOptions.Parallelism"/> at a time; the parts below it start walks of their own.</summary>
    private async Task WalkPartAsync(string root, bool rootHeldAlone, Walk walk, Action<string, bool> startPart, CancellationToken ct)
    {
        var pending = Channel.CreateUnbounded<(string Folder, bool HeldAlone)>();
        var outstanding = 1;
        pending.Writer.TryWrite((root, rootHeldAlone));

        void Enqueue(string child, bool heldAlone)
        {
            Interlocked.Increment(ref outstanding);
            pending.Writer.TryWrite((child, heldAlone));
        }

        async Task WorkerAsync()
        {
            await foreach (var (folder, heldAlone) in pending.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
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
                    try
                    {
                        // the folder's stamp first: one whose stamp holds keeps its listing and every listing held below it
                        var held = Cache.StampHeld(folder);
                        var (read, stamp) = await StampAsync(folder, held, ct).ConfigureAwait(false);
                        if (!walk.Full && read && held is not null && stamp == held)
                        {
                            await KeepHeldAsync(folder, heldAlone, walk, Enqueue, ct).ConfigureAwait(false);
                            continue;
                        }

                        tree = heldAlone ? null : await TreeOfAsync(folder, walk, ct).ConfigureAwait(false);
                        entries = tree is null ? await Cache.RefreshAsync(folder, stamp, ct).ConfigureAwait(false) : [];
                        if (tree is not null)
                        {
                            // the folder and everything below it at once: all kept and counted, nothing left to go into
                            foreach (var (path, listing) in tree)
                            {
                                // what was read with the folder goes by the folder's stamp too
                                Cache.Keep(path, listing, stamp);
                                walk.Counters.Add(listing.Count(e => !e.IsDirectory), listing.Count(e => e.IsDirectory));
                            }
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
                    long files = 0;
                    var folders = 0;
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
                            Enqueue(child, heldAlone || isLeftOut);
                        }
                    }

                    walk.Counters.Add(files, folders);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // one gone since its parent was listed is nothing left out
                    if (ex is not VfsException { Error: VfsError.NotFound or VfsError.NotADirectory })
                    {
                        Interlocked.Increment(ref walk.Failed);
                    }

                    _logger.LogDebug("{Name}: the walk left out '{Folder}': {Error}", _options.Name, folder, ex.Message);
                }
                finally
                {
                    if (Interlocked.Decrement(ref outstanding) == 0)
                    {
                        pending.Writer.TryComplete();
                    }
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, _options.Parallelism).Select(_ => Task.Run(WorkerAsync, CancellationToken.None))).ConfigureAwait(false);
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
        var held = Cache.Under(folder);
        foreach (var (path, listing) in held)
        {
            Cache.Touch(path);
            Interlocked.Increment(ref walk.Kept);
            long files = 0;
            var folders = 0;
            IReadOnlySet<string>? leftOut = null;
            var asked = false;
            foreach (var entry in listing)
            {
                if (!entry.IsDirectory)
                {
                    files++;
                    continue;
                }

                folders++;
                var child = VfsPath.Join(path, entry.Name);
                if (held.ContainsKey(child))
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
                    leftOut = _options.LeftOut is { } left ? await left(path, ct).ConfigureAwait(false) : null;
                }

                if (leftOut?.Contains(entry.Name) == true)
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
    /// <summary>Whether listings are kept until listed afresh (by a walk, or behind an answer, <see cref="RefreshAfter"/>); off, they last <see cref="UnkeptFor"/> and the count is one walk.</summary>
    public bool Keep { get; init; } = true;

    /// <summary>Without keeping, how long a listing lasts (two seconds: clients stat every file they list); zero lists every time, for a source whose own caches decide.</summary>
    public TimeSpan UnkeptFor { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>With listings kept, how long after one walk ends the next begins; zero (or less) for the first walk alone.</summary>
    public TimeSpan WalkEvery { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>With listings kept, the age past which a listing is answered and listed afresh behind the answer (<see cref="ListingCache.RefreshAfter"/>); null keeps it until the next walk.</summary>
    public TimeSpan? RefreshAfter { get; init; }

    /// <summary>With listings kept, how many listings afresh behind answers run at once at most (<see cref="ListingCache.MaxRefreshesBehind"/>); null starts them all.</summary>
    public int? MaxRefreshesBehind { get; init; }

    /// <summary>
    /// With listings kept, how long a folder whose listing failed answers with that failure at once, by the failure
    /// (<see cref="ListingCache.FailureKeptFor"/>), counted from the end of a walk that met it, so <see cref="WalkEvery"/> holds it until
    /// the next walk, which lists it again. Null (the default) asks whenever asked.
    /// </summary>
    public Func<VfsException, TimeSpan?>? FailureKeptFor { get; init; }

    /// <summary>Folders listed at a time in a walk: in each part, where <see cref="PartsBelow"/> makes parts.</summary>
    public int Parallelism { get; init; } = TreeCount.DefaultParallelism;

    /// <summary>Listings in flight in a walk in all, whatever the number of parts.</summary>
    public int MaxInFlight { get; init; } = 64;

    /// <summary>With listings kept, the folders whose listings are held at most; past that the oldest go.</summary>
    public int MaxFolders { get; init; } = 1_000_000;

    /// <summary>
    /// Whether the folders in a folder are each walked as a part of their own, <see cref="Parallelism"/> folders of each at a
    /// time and every part at once (SharePoint's sites, so a big site does not hold up the others); null for one part.
    /// </summary>
    public Func<string, CancellationToken, ValueTask<bool>>? PartsBelow { get; init; }

    /// <summary>
    /// Of the folders in a folder, the names of those a walk does not go into, because listing them costs requests of their
    /// own (GitLab's issues): they are listed when opened, and the count ends as a lower bound
    /// (<see cref="EnumerationState.Unknown"/>). Null, or none, goes into every folder.
    /// </summary>
    public Func<string, CancellationToken, ValueTask<IReadOnlySet<string>?>>? LeftOut { get; init; }

    /// <summary>
    /// The listings of a folder and of every folder below it at once, by path (the folder's own among them), where the source
    /// knows a quicker way than folder by folder (GitLab's repository: its whole tree a hundred entries a request); null for
    /// a folder it does not, which the walk lists alone. All of them are kept with the folder's stamp (<see cref="StampOf"/>),
    /// read before them.
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>?>>? ListTree { get; init; }

    /// <summary>What the log calls the source, at the start of its lines ("sharepoint").</summary>
    public string Name { get; init; } = "source";

    /// <summary>
    /// With listings kept, the file they are saved to (in the source's state folder) after each walk and when the source
    /// stops, and read back from at its next start: folders then open at once, each listed afresh behind its first answer.
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
    /// A folder's stamp (<see cref="ListingCache.StampOf"/>): what its listing and every listing below it depend on, as the
    /// upstream tells it cheaply. A walk keeps a stamped folder whose stamp holds, and everything held below it, as they
    /// are, and a listing afresh behind an answer (<see cref="RefreshAfter"/>) is spared the same way. Null (the default)
    /// stamps nothing: every walk lists every folder.
    /// </summary>
    public Func<string, string?, CancellationToken, ValueTask<string?>>? StampOf { get; init; }

    /// <summary>
    /// With <see cref="StampOf"/>: how often a walk lists every folder whatever its stamp, so what a stamp does not cover
    /// (a reaction on an old message, a file changed deep in a library whose stamp was read a moment early) shows by then;
    /// a day by default. Zero or less makes every walk such a walk.
    /// </summary>
    public TimeSpan FullWalkEvery { get; init; } = TimeSpan.FromDays(1);
}

/// <summary>The listings a run saved (<see cref="KeptListingsOptions.SaveTo"/>): what they are for, each folder's, the stamps held with them, and when every folder was last listed.</summary>
internal sealed record SavedListings(int Version, string Key, Dictionary<string, List<VfsEntry>> Listings)
{
    public Dictionary<string, string>? Stamps { get; init; }

    public DateTimeOffset? FullWalkAt { get; init; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SavedListings))]
internal sealed partial class SavedListingsJson : System.Text.Json.Serialization.JsonSerializerContext;
