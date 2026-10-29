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
/// (<see cref="StartWalks"/>: SharePoint does, from its start). Answer listings and stats from <see cref="Cache"/>, and keep
/// it right after a write as any <see cref="ListingCache"/>. Not kept (<see cref="KeptListingsOptions.Keep"/> off),
/// listings last <see cref="KeptListingsOptions.UnkeptFor"/> (two seconds) and the count is one walk.
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
    private readonly object _gate = new();
    private Task _walking = Task.CompletedTask;
    private bool _started;
    private bool _disposed;

    // the count's state after the last walk ended (an EnumerationState), -1 before the first has
    private volatile int _walked = -1;

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
        Cache = _options.Keep
            ? new ListingCache(ListingCache.Kept, lister, _options.MaxFolders, _time) { RefreshAfter = _options.RefreshAfter }
            : new ListingCache(_options.UnkeptFor, lister, time: _time);
        if (_options.Keep && _options.SaveTo is { } saved)
        {
            Load(saved);
        }
    }

    private const int SavedVersion = 1;

    /// <summary>The listings an earlier run saved for the same <see cref="KeptListingsOptions.SaveKey"/>, kept as old (refreshed as they are opened).</summary>
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
                Cache.Restore(folder, listing);
            }

            _logger.LogInformation("{Name}: {Count:N0} folder listings from the last run answer at once, each listed afresh as it is opened", _options.Name, saved.Listings.Count);
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
            var saved = new SavedListings(SavedVersion, _options.SaveKey ?? "", Cache.Snapshot().ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.Ordinal));
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
            if (_started || _disposed || !_options.Keep)
            {
                return;
            }

            _started = true;
            var stopping = _stopping.Token;
            _walking = Task.Run(() => LoopAsync(stopping), CancellationToken.None);
        }
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
    /// <see cref="KeptListingsOptions.MaxInFlight"/> in all. A folder that cannot be listed is left out, with what is below
    /// it, and logged; anything else that fails ends the walk.
    /// </summary>
    /// <returns>Whether it went into every folder: false where <see cref="KeptListingsOptions.LeftOut"/> kept it out of some.</returns>
    public async Task<bool> WalkAsync(SourceCounters counters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(counters);
        using var inFlight = new SemaphoreSlim(_options.MaxInFlight, _options.MaxInFlight);
        var walk = new Walk(counters, inFlight);
        void StartPart(string path)
        {
            lock (walk.Parts)
            {
                walk.Parts.Add(Task.Run(() => WalkPartAsync(path, walk, StartPart, ct), CancellationToken.None));
            }
        }

        StartPart("");
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

        return !walk.LeftSomeOut;
    }

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

    /// <summary>The walks: the first counting into the source's counters, then one every <see cref="KeptListingsOptions.WalkEvery"/> after the last ended.</summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            var counters = first ? _counters : new SourceCounters();
            var started = _time.GetTimestamp();
            try
            {
                var whole = await WalkAsync(counters, ct).ConfigureAwait(false);
                var state = whole ? EnumerationState.Complete : EnumerationState.Unknown;
                _counters.Set(counters.FilesKnown, counters.DirectoriesKnown, state);
                _walked = (int)state;
                var seconds = _time.GetElapsedTime(started).TotalSeconds;
                if (whole)
                {
                    _logger.LogInformation("{Name}: walked every folder in {Seconds:F0} s: {Files:N0} files in {Folders:N0} folders", _options.Name, seconds, counters.FilesKnown, counters.DirectoriesKnown);
                }
                else
                {
                    _logger.LogInformation("{Name}: walked in {Seconds:F0} s: {Files:N0} files in {Folders:N0} folders, and the folders it leaves out are listed when opened", _options.Name, seconds, counters.FilesKnown, counters.DirectoriesKnown);
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
                _walked = (int)EnumerationState.Unknown;
                if (first)
                {
                    _counters.SetState(EnumerationState.Unknown);
                }

                _logger.LogWarning(ex, "{Name}: walking every folder failed", _options.Name);
            }
            finally
            {
                if (first)
                {
                    first = false;
                    _firstWalk.TrySetResult();
                }
            }

            if (_options.WalkEvery <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                await Task.Delay(_options.WalkEvery, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _firstWalk.TrySetResult();
    }

    /// <summary>One part's share of a walk (<see cref="WalkAsync"/>): its folders, <see cref="KeptListingsOptions.Parallelism"/> at a time; the parts below it start walks of their own.</summary>
    private async Task WalkPartAsync(string root, Walk walk, Action<string> startPart, CancellationToken ct)
    {
        var pending = Channel.CreateUnbounded<string>();
        var outstanding = 1;
        pending.Writer.TryWrite(root);

        async Task WorkerAsync()
        {
            await foreach (var folder in pending.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>? tree;
                    IReadOnlyList<VfsEntry> entries;
                    await walk.InFlight.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        tree = _options.ListTree is { } listTree ? await listTree(folder, ct).ConfigureAwait(false) : null;
                        entries = tree is null ? await Cache.RefreshAsync(folder, ct).ConfigureAwait(false) : [];
                    }
                    finally
                    {
                        walk.InFlight.Release();
                    }

                    if (tree is not null)
                    {
                        // the folder and everything below it at once: all kept and counted, nothing left to go into
                        foreach (var (path, listing) in tree)
                        {
                            Cache.Keep(path, listing);
                            walk.Counters.Add(listing.Count(e => !e.IsDirectory), listing.Count(e => e.IsDirectory));
                        }

                        continue;
                    }

                    var hasFolders = entries.Any(e => e.IsDirectory);
                    var partsBelow = hasFolders && _options.PartsBelow is { } parts && await parts(folder, ct).ConfigureAwait(false);
                    var leftOut = hasFolders && _options.LeftOut is { } left ? await left(folder, ct).ConfigureAwait(false) : null;
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
                        if (leftOut?.Contains(entry.Name) == true)
                        {
                            walk.LeftSomeOut = true;
                            continue;
                        }

                        var child = VfsPath.Join(folder, entry.Name);
                        if (partsBelow)
                        {
                            startPart(child);
                        }
                        else
                        {
                            Interlocked.Increment(ref outstanding);
                            pending.Writer.TryWrite(child);
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

    /// <summary>One walk's shared state: what it counts into, the listings in flight, its parts, and whether it left any folder out.</summary>
    private sealed class Walk(SourceCounters counters, SemaphoreSlim inFlight)
    {
        public SourceCounters Counters { get; } = counters;

        public SemaphoreSlim InFlight { get; } = inFlight;

        public List<Task> Parts { get; } = [];

        public volatile bool LeftSomeOut;
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
    /// a folder it does not, which the walk lists alone.
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

    /// <summary>What the saved listings must have been saved for to be read back (the account, the server): another's are not.</summary>
    public string? SaveKey { get; init; }
}

/// <summary>The listings a run saved (<see cref="KeptListingsOptions.SaveTo"/>): what they are for, and each folder's.</summary>
internal sealed record SavedListings(int Version, string Key, Dictionary<string, List<VfsEntry>> Listings);

[System.Text.Json.Serialization.JsonSerializable(typeof(SavedListings))]
internal sealed partial class SavedListingsJson : System.Text.Json.Serialization.JsonSerializerContext;
