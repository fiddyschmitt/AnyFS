using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// The file-system half shared by the SharePoint flavours, over an <see cref="IDocumentStore"/>. Source paths
/// are <c>library/folder/file</c>, with a site's subsites in a <c>Subsites</c> folder beside its libraries, each with
/// its own inside it (<c>Subsites/subsite/library/folder/file</c>, and so on down), at the tenant's root site the
/// tenant's other sites in a <c>Sites</c> folder the same way (<c>Sites/Axle/library/...</c>, found by search), or
/// just <c>folder/file</c> when one library is pinned. A source given several sites has a folder per site first
/// (<c>Axle/library/...</c>), each like a source's own; given the root site of the one server they are on with them, the
/// root site is the source's own and the others are in its Sites and Subsites folders. The site's pages (a library of
/// <see cref="LibraryInfo.Pages"/>) are read-only Markdown files made by the store.
/// Listings sit in a <see cref="ListingCache"/>, for two seconds or, with <see cref="KeepListings"/>, until listed afresh
/// (refresh pressed, or the walk of the whole tree every <see cref="WalkEvery"/>), and stats are answered from it; a
/// listing also remembers each item's ids (and download URL, where the flavour has one), so an open needs no extra round trip. Files below the
/// spool threshold are downloaded whole into the <see cref="SpoolCache"/> (and served again from it while
/// their version is unchanged); larger ones are read in ranges through <see cref="RandomAccessOverStream"/>,
/// several at a time. Writes go to a local temp file and are uploaded when the handle closes. Trouble with the
/// upstream shows as Degraded until a call succeeds again. A flavour supplies the store (after signing in)
/// and the settings that shape the tree.
/// </summary>
public abstract class SharePointSource : SourceBase, ICountable
{
    /// <summary>The folder beside a site's libraries that holds its subsites, one folder each.</summary>
    public const string SubsitesFolder = "Subsites";

    /// <summary>The folder beside the tenant's root site's libraries that holds the tenant's other sites, one folder each.</summary>
    public const string SitesFolder = "Sites";

    /// <summary>Folders of one site listed at a time in a walk of the tree (<see cref="WalkAsync"/>); every site is walked at once.</summary>
    internal const int WalkPerSite = 4;

    /// <summary>Listings in flight in a walk in all, whatever the number of sites: SharePoint throttles a client that asks too much at once.</summary>
    internal const int WalkInFlight = 64;

    private static readonly TimeSpan ListingTtl = TimeSpan.FromSeconds(2);

    // with listings kept: a folder listed again this soon after a client's last listing of it is listed afresh (refresh pressed)
    private static readonly TimeSpan RefreshOnRepeat = TimeSpan.FromSeconds(5);

    // search finds a new site only minutes after it is made, so asking more often than this gains nothing
    private static readonly TimeSpan OtherSitesTtl = TimeSpan.FromMinutes(5);

    // the source's own site, or the sites it was given; their subsites (and, at a tenant's root, the other sites) hang
    // below as they are listed
    private WebNode _root = new(null, "");

    // a source given the root site of the one server its sites are on, among others: those others, which show in the root
    // site's Sites folder (site collections) or Subsites folder (subsites); null otherwise. Guarded by _leftOut.
    private List<WebInfo>? _extras;
    private readonly ConcurrentDictionary<string, ItemRef> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _otherSitesGate = new(1, 1);

    // the other sites search found, by the root site they show under ("" for the source's own)
    private readonly Dictionary<string, (IReadOnlyList<WebInfo> Sites, DateTimeOffset Searched)> _otherSites = new(StringComparer.OrdinalIgnoreCase);

    // a source of several sites: those of them SharePoint did not open, with what it answered, looked up again now and
    // then in the background (a server down for a while, or a site the account is given access to later), and the
    // lookups still going (those slow to answer at the start, or the latest try)
    private readonly List<(SiteAddress Site, string Problem)> _leftOut = [];
    private readonly CancellationTokenSource _stopping = new();
    private DateTimeOffset _leftOutTried;
    private Task _retrying = Task.CompletedTask;

    // with listings kept: the walks of the whole tree, one at the start and one every WalkEvery after the last
    private readonly TaskCompletionSource _firstWalk = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _walking = Task.CompletedTask;

    // files open for writing that SharePoint has not got yet (the upload comes when the handle closes): stats,
    // listings and times are answered here meanwhile, since the upstream knows nothing of them
    private readonly ConcurrentDictionary<string, PendingUploadFile> _pending = new(StringComparer.OrdinalIgnoreCase);
    private IDocumentStore? _store;
    private ListingCache? _listings;
    private SpoolCache? _spool;
    private string _uploadDir = "";
    private bool _degraded;

    public override SourceCapabilities Capabilities => new(CanWrite: true, CaseInsensitive: true);

    /// <summary>The library the whole source is inside, or null when every library is a top-level folder.</summary>
    protected abstract string? PinnedLibrary { get; }

    /// <summary>Parallel ranged reads per open file.</summary>
    protected abstract int Connections { get; }

    /// <summary>Files up to this size are downloaded whole on open.</summary>
    protected abstract long SpoolBelowBytes { get; }

    /// <summary>What the source is called in messages before the store exists (the site URL).</summary>
    protected abstract string Target { get; }

    /// <summary>Whether the source's root shows the tenant's other sites in a Sites folder: true for the tenant's root site.</summary>
    protected virtual bool ShowsOtherSites => false;

    /// <summary>
    /// The sites a source of several was given, each to be a folder at its root once it opens; null for a source of one
    /// site. The store is then one for several sites (<see cref="IDocumentStore.FindSiteAsync"/>).
    /// </summary>
    protected virtual IReadOnlyList<SiteAddress>? GivenSites => null;

    /// <summary>The account the store signs in as, where messages about sites it could not open should name it.</summary>
    protected virtual string? SignedInAs => null;

    /// <summary>How long a site the source was given but could not open waits before it is tried again.</summary>
    protected virtual TimeSpan LeftOutRetry => TimeSpan.FromMinutes(5);

    /// <summary>How long a source of several sites waits at the start for every one of them to answer (<see cref="OpenWait"/>).</summary>
    internal static readonly TimeSpan DefaultOpenWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the start waits for every site a source of several was given; past that it starts once one has opened, and
    /// the rest show as they answer.
    /// </summary>
    protected virtual TimeSpan OpenWait => DefaultOpenWait;

    /// <summary>
    /// Whether folder listings are kept until listed afresh rather than for two seconds: the source answers from what it
    /// last saw, lists a folder again when a client lists it twice within a few seconds (refresh), and walks the whole
    /// tree at the start and again every <see cref="WalkEvery"/>.
    /// </summary>
    protected virtual bool KeepListings => false;

    /// <summary>With listings kept: how long after one walk of the whole tree the next begins; zero for none after the first.</summary>
    protected virtual TimeSpan WalkEvery => TimeSpan.FromMinutes(30);

    /// <summary>Signs in and connects; throw <see cref="SourceFailedException"/> for a reason the user must act on.</summary>
    private protected abstract Task<IDocumentStore> ConnectAsync(CancellationToken ct);

    /// <summary>The store, once connected (for tests).</summary>
    internal IDocumentStore Store => _store ?? throw VfsException.Unavailable($"{Target} is not connected yet");

    protected sealed override async Task OnInitialiseAsync(CancellationToken ct)
    {
        var store = await ConnectAsync(ct).ConfigureAwait(false);
        _store = store;
        if (GivenSites is { Count: > 1 } given)
        {
            await OpenSitesAsync(given, ct).ConfigureAwait(false);
        }

        WebContents site;
        try
        {
            site = await LoadWebAsync(_root, ct).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            throw new SourceFailedException($"{Target}: {ex.Message}", permanent: ex.Error is VfsError.AccessDenied or VfsError.NotFound, ex);
        }

        if (PinnedLibrary is { } pinned && !site.Libraries.ContainsKey(pinned))
        {
            throw new SourceFailedException($"{store.Description} has no {store.LibraryNoun} named {pinned}; it has: {string.Join(", ", site.Libraries.Values.Select(l => l.Name))}", permanent: true);
        }

        _listings = KeepListings
            ? new ListingCache(ListingCache.Kept, ListRemoteAsync, maxDirectories: 1_000_000, refreshOnRepeat: RefreshOnRepeat)
            : new ListingCache(ListingTtl, ListRemoteAsync);
        _spool = new SpoolCache(Path.Combine(Context.CacheDirectory, "spool"), logger: Logger);
        if (KeepListings)
        {
            var stopping = _stopping.Token;
            _walking = Task.Run(() => WalkLoopAsync(stopping), CancellationToken.None);
        }
        _uploadDir = Directory.CreateDirectory(Path.Combine(Context.CacheDirectory, "uploads")).FullName;
        foreach (var stale in Directory.EnumerateFiles(_uploadDir))
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
            }
        }

        if (site.Sites is { } opened)
        {
            Logger.LogInformation("sharepoint: serving {Count} sites: {Sites}", opened.Count, string.Join(", ", opened.Values.Select(w => $"{w.Name} ({w.Info!.Url})")));
            return;
        }

        var subsites = site.Of(subsites: true) is { } s ? $", {s.Webs.Count} subsite{(s.Webs.Count == 1 ? "" : "s")}" : "";
        var others = site.Of(subsites: false) is { } o ? $", {o.Webs.Count} other site{(o.Webs.Count == 1 ? "" : "s")}" : "";
        Logger.LogInformation("sharepoint: serving {Site} ({Count} librar{Plural}{Subsites}{Others}){Pinned}", _root.Info?.Url ?? store.Description, site.Libraries.Count, site.Libraries.Count == 1 ? "y" : "ies", subsites, others, PinnedLibrary is { } p ? $", pinned to {p}" : "");
    }

    // --- listing and stat --------------------------------------------------------------------------------------

    public override async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
        {
            return VfsEntry.Directory("");
        }

        if (_pending.TryGetValue(path, out var pending))
        {
            return PendingEntry(path, pending);
        }

        // a site's libraries and sites are answered from what the site's last listing found, without a request
        return await ResolveAsync(path, ct).ConfigureAwait(false) switch
        {
            null => null,
            { Folder: { } folder } => VfsEntry.Directory(folder.Name),
            { Library: null } place => VfsEntry.Directory(place.Web.Name),
            { Library: { } library, ItemPath.Length: 0 } => VfsEntry.Directory(library.Name),
            _ => await Listings.StatAsync(path, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false),
        };
    }

    public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        foreach (var entry in await Listings.ListAsync(path, ct).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    // --- counting and walking ----------------------------------------------------------------------------------

    /// <summary>
    /// "Count files on start": the walk of the whole tree (<see cref="WalkAsync"/>). With listings kept, the walk the source
    /// starts with anyway, which counts as it goes; otherwise one now.
    /// </summary>
    public async Task CountFilesAsync(CancellationToken ct)
    {
        if (KeepListings)
        {
            await _firstWalk.Task.WaitAsync(ct).ConfigureAwait(false);
            return;
        }

        await WalkAsync(Counters, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// With listings kept: the walk at the start, which counts in the open (the tile's number grows with it), then one every
    /// <see cref="WalkEvery"/> after the last ended, which counts aside and shows its total when done. A walk that fails is
    /// logged and the next one comes as usual.
    /// </summary>
    private async Task WalkLoopAsync(CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            var counters = first ? Counters : new SourceCounters();
            var started = Stopwatch.GetTimestamp();
            try
            {
                await WalkAsync(counters, ct).ConfigureAwait(false);
                Counters.Set(counters.FilesKnown, counters.DirectoriesKnown, EnumerationState.Complete);
                Logger.LogInformation("sharepoint: walked every folder in {Seconds:F0} s: {Files} files in {Folders} folders", Stopwatch.GetElapsedTime(started).TotalSeconds, counters.FilesKnown, counters.DirectoriesKnown);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "sharepoint: walking every folder failed");
            }
            finally
            {
                if (first)
                {
                    first = false;
                    _firstWalk.TrySetResult();
                }
            }

            if (WalkEvery <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                await Task.Delay(WalkEvery, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _firstWalk.TrySetResult();
    }

    /// <summary>
    /// Lists every folder of the tree afresh, counting files and folders into <paramref name="counters"/> as it goes: each
    /// site on its own (the source's own, or each of the sites it was given, and every site in a Subsites or Sites folder
    /// below them), <see cref="WalkPerSite"/> folders of it at a time, all sites at once, at most <see cref="WalkInFlight"/>
    /// listings in flight in all. A folder that cannot be listed is left out, with what is below it.
    /// </summary>
    internal async Task WalkAsync(SourceCounters counters, CancellationToken ct)
    {
        using var inFlight = new SemaphoreSlim(WalkInFlight, WalkInFlight);
        var gate = new object();
        var sites = new List<Task>();
        void StartSite(string path)
        {
            lock (gate)
            {
                sites.Add(Task.Run(() => WalkSiteAsync(path, StartSite, inFlight, counters, ct), CancellationToken.None));
            }
        }

        StartSite("");
        Exception? failure = null;
        while (true)
        {
            // every site's walk ends before this does, those started while waiting too: none outlives the semaphore
            Task[] started;
            lock (gate)
            {
                started = [.. sites];
            }

            try
            {
                await Task.WhenAll(started).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }

            lock (gate)
            {
                if (sites.Count == started.Length)
                {
                    break;
                }
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>One site's part of a walk (<see cref="WalkAsync"/>): its folders, <see cref="WalkPerSite"/> at a time; the sites below it start walks of their own.</summary>
    private async Task WalkSiteAsync(string site, Action<string> startSite, SemaphoreSlim inFlight, SourceCounters counters, CancellationToken ct)
    {
        var pending = Channel.CreateUnbounded<string>();
        var outstanding = 1;
        pending.Writer.TryWrite(site);

        async Task WorkerAsync()
        {
            await foreach (var folder in pending.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    IReadOnlyList<VfsEntry> entries;
                    await inFlight.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        entries = await Listings.RefreshAsync(folder, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        inFlight.Release();
                    }

                    var holdsSites = await HoldsSitesAsync(folder, ct).ConfigureAwait(false);
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
                        if (holdsSites)
                        {
                            startSite(child);
                        }
                        else
                        {
                            Interlocked.Increment(ref outstanding);
                            pending.Writer.TryWrite(child);
                        }
                    }

                    counters.Add(files, folders);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug("sharepoint: the walk left out '{Folder}': {Error}", folder, ex.Message);
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

        await Task.WhenAll(Enumerable.Range(0, WalkPerSite).Select(_ => Task.Run(WorkerAsync, CancellationToken.None))).ConfigureAwait(false);
    }

    /// <summary>Whether a folder's subfolders are sites: a Subsites or Sites folder, or the root of a source given several sites.</summary>
    private async Task<bool> HoldsSitesAsync(string folder, CancellationToken ct)
        => await ResolveAsync(folder, ct).ConfigureAwait(false) is { Library: null } place && (place.Folder is not null || (place.Web == _root && _root.Contents?.Sites is not null));

    private async IAsyncEnumerable<VfsEntry> ListRemoteAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var place = await ResolveAsync(path, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
        if (place.Library is null)
        {
            // a site's folder, or its Subsites or Sites folder: listed again each time, so a library or site made since shows up
            var site = await LoadWebAsync(place.Web, ct).ConfigureAwait(false);
            if (place.Folder is { } folder)
            {
                foreach (var name in (site.Of(folder.Subsites)?.Webs.Keys ?? []).Order(StringComparer.OrdinalIgnoreCase))
                {
                    yield return VfsEntry.Directory(name);
                }

                yield break;
            }

            foreach (var library in site.Libraries.Values.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            {
                yield return VfsEntry.Directory(library.Name);
            }

            foreach (var webs in site.Folders)
            {
                yield return VfsEntry.Directory(webs.Name);
            }

            foreach (var name in site.Sites?.Keys ?? [])
            {
                yield return VfsEntry.Directory(name);
            }

            yield break;
        }

        var (library0, itemPath) = (place.Library, place.ItemPath);
        if (itemPath.Length > 0)
        {
            // the store answers a file's children with an error of its own choosing; the parent's listing says first
            var self = await Listings.StatAsync(path, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
            if (!self.IsDirectory)
            {
                throw VfsException.NotADirectory(path);
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enumerator = Run(() => Store.ListChildrenAsync(library0, itemPath, ct)).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (SignInRequiredException ex)
                {
                    throw Lapsed(ex);
                }

                if (!more)
                {
                    break;
                }

                var (entry, item) = enumerator.Current;
                _items[VfsPath.Join(path, entry.Name)] = item;
                seen.Add(entry.Name);
                yield return entry;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        // what is being written here and is not upstream yet
        foreach (var (pendingPath, pending) in _pending)
        {
            if (string.Equals(VfsPath.Parent(pendingPath), path, StringComparison.OrdinalIgnoreCase) && !seen.Contains(VfsPath.SplitLast(pendingPath).Name))
            {
                yield return PendingEntry(pendingPath, pending);
            }
        }
    }

    private static VfsEntry PendingEntry(string path, PendingUploadFile pending)
    {
        var (created, modified) = pending.RequestedTimes;
        return VfsEntry.File(VfsPath.SplitLast(path).Name, pending.Length, modified ?? DateTimeOffset.UtcNow, created ?? pending.Opened);
    }

    // --- open ---------------------------------------------------------------------------------------------------

    public override async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
        {
            throw VfsException.IsADirectory(path);
        }

        if (_pending.TryGetValue(path, out var pendingOpen))
        {
            if (!pendingOpen.IsClosed)
            {
                // still being written, not upstream yet: reads come from the local copy (an attributes-only open, which
                // Windows does for Exists and before a rename, lands here too); a second writer waits for the first
                if (mode == VfsOpenMode.Read)
                {
                    return pendingOpen.OpenReader();
                }

                throw new VfsException(VfsError.AccessDenied, $"'{path}' is still open for writing and not yet uploaded; close it first");
            }

            await pendingOpen.Completion.ConfigureAwait(false);   // the close was posted; the upload is landing
        }

        var existing = await StatAsync(path, ct).ConfigureAwait(false);
        if (existing is { IsDirectory: true })
        {
            throw VfsException.IsADirectory(path);
        }

        if (existing is null && !mode.MayCreate())
        {
            throw VfsException.NotFound(path);
        }

        if (await ResolveAsync(path, ct).ConfigureAwait(false) is not { Library: { } library } place)
        {
            // a new name beside a site's libraries and subsites, or one under a site or library that is not there
            if (await ResolveAsync(VfsPath.Parent(path), ct).ConfigureAwait(false) is { Library: null })
            {
                throw VfsException.InvalidPath(path, $"files live inside a {Store.LibraryNoun}");
            }

            throw VfsException.NotFound(VfsPath.Parent(path));
        }

        if (existing is not null && mode == VfsOpenMode.CreateNew)
        {
            throw VfsException.AlreadyExists(path);
        }

        var itemPath = place.ItemPath;
        if (mode == VfsOpenMode.Read)
        {
            return await OpenForReadAsync(path, library, itemPath, existing!, ct).ConfigureAwait(false);
        }

        RequireWrite("writing");
        RequireNotPages(library, path);
        SharePointNames.Require(path);
        var parentPath = VfsPath.Parent(path);
        if (existing is null && itemPath.Contains('/'))
        {
            var parent = await StatAsync(parentPath, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(parentPath);
            if (!parent.IsDirectory)
            {
                throw VfsException.NotADirectory(parentPath);
            }
        }

        var tempPath = Path.Combine(_uploadDir, Guid.NewGuid().ToString("N") + ".upload");
        var keepExisting = existing is not null && mode is VfsOpenMode.ReadWrite or VfsOpenMode.OpenOrCreate;
        if (keepExisting)
        {
            await DownloadToAsync(path, library, itemPath, existing!, tempPath, ct).ConfigureAwait(false);
        }

        var file = new PendingUploadFile(path, tempPath, keepExisting, mustUpload: existing is null || mode == VfsOpenMode.Create,
            (temp, length, uploadCt) => UploadAsync(path, library, itemPath, temp, length, uploadCt),
            (p, ex) => MarkDegraded($"the upload of {p} failed: {ex.Message}"), Logger);
        _pending[path] = file;
        file.Closed = () => _pending.TryRemove(new KeyValuePair<string, PendingUploadFile>(path, file));
        if (existing is null)
        {
            // to the client the file exists from now on, though SharePoint gets it only when the handle closes
            var now = DateTimeOffset.UtcNow;
            Listings.Update(path, VfsEntry.File(VfsPath.SplitLast(path).Name, 0, now, now), StringComparison.OrdinalIgnoreCase);
        }

        return file;
    }

    private async Task<IVfsFile> OpenForReadAsync(string path, LibraryInfo library, string itemPath, VfsEntry entry, CancellationToken ct)
    {
        var item = await ItemAsync(path, library, itemPath, ct).ConfigureAwait(false);
        if (entry.Length <= SpoolBelowBytes && _spool is { } spool)
        {
            var key = $"{item.Id}:{item.Version}";
            return await spool.OpenAsync(key, entry.Length, async downloadCt =>
            {
                var response = await Run(() => Store.OpenContentAsync(library, itemPath, item, 0, downloadCt)).ConfigureAwait(false);
                return new ResponseStream(response, await response.Content.ReadAsStreamAsync(downloadCt).ConfigureAwait(false));
            }, ct).ConfigureAwait(false);
        }

        return new RandomAccessOverStream(entry.Length, async (offset, openCt) =>
        {
            var response = await Run(() => Store.OpenContentAsync(library, itemPath, item, offset, openCt)).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                response.Dispose();
                return Stream.Null;
            }

            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(openCt).ConfigureAwait(false));
        }, maxStreams: Math.Clamp(Connections, 1, 16), maxSkipBytes: 1 << 20);
    }

    private async Task DownloadToAsync(string path, LibraryInfo library, string itemPath, VfsEntry entry, string tempPath, CancellationToken ct)
    {
        var item = await ItemAsync(path, library, itemPath, ct).ConfigureAwait(false);
        using var response = await Run(() => Store.OpenContentAsync(library, itemPath, item, 0, ct)).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        await body.CopyToAsync(file, ct).ConfigureAwait(false);
        if (file.Length != entry.Length)
        {
            Logger.LogWarning("sharepoint: '{Path}' downloaded {Got} bytes, the listing said {Expected}", path, file.Length, entry.Length);
        }
    }

    // The listings are kept current entry by entry (ListingCache.Update) rather than thrown away after each write:
    // a bulk copy into a folder would otherwise list it again, in full, for every file (18-Oct-2026: the E2E
    // fixture's 5,000-file folder went in at under two files a second). A failed call forgets the listing instead.
    private async Task UploadAsync(string path, LibraryInfo library, string itemPath, string tempPath, long length, CancellationToken ct)
    {
        ItemRef item;
        try
        {
            item = await Run(() => Store.UploadAsync(library, itemPath, tempPath, length, ct)).ConfigureAwait(false);
        }
        catch
        {
            Listings.InvalidateParentOf(path);
            throw;
        }

        _items[path] = item;
        var now = DateTimeOffset.UtcNow;
        var (created, modified) = _pending.TryGetValue(path, out var pending) ? pending.RequestedTimes : (null, null);
        if (created is not null || modified is not null)
        {
            // times a client set while the file was pending (Explorer and robocopy do, right after the write)
            try
            {
                await Run(() => Store.SetTimesAsync(library, itemPath, created, modified, ct)).ConfigureAwait(false);
            }
            catch (VfsException ex)
            {
                Logger.LogWarning("sharepoint: the times set on '{Path}' while it was being written could not be applied: {Message}", path, ex.Message);
            }
        }

        Listings.Update(path, VfsEntry.File(VfsPath.SplitLast(path).Name, item.Size, modified ?? now, created ?? now, etag: item.Version), StringComparison.OrdinalIgnoreCase);
    }

    // --- writes --------------------------------------------------------------------------------------------------

    public override async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        RequireWrite("creating directories");
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
        {
            throw VfsException.AlreadyExists(path);
        }

        var place = await ResolveAsync(path, ct).ConfigureAwait(false);
        if (place is { Library: null } or { ItemPath.Length: 0 })
        {
            throw VfsException.AlreadyExists(path);   // a site's, a Subsites or Sites, or a library's own folder
        }

        if (place is not { Library: { } library })
        {
            throw await ResolveAsync(VfsPath.Parent(path), ct).ConfigureAwait(false) switch
            {
                { Folder.Subsites: true } => VfsException.NotSupported("creating a subsite"),
                { Folder.Subsites: false } => VfsException.NotSupported("creating a site"),
                { Library: null, Web.Contents.Sites: not null } => VfsException.NotSupported("creating a site (a source shows the sites its Site URLs name)"),
                { Library: null } => VfsException.NotSupported($"creating a {Store.LibraryNoun} (create folders inside one)"),
                _ => VfsException.NotFound(VfsPath.Parent(path)),
            };
        }

        RequireNotPages(library, path);
        SharePointNames.Require(path);
        var itemPath = place.ItemPath;
        if (await Listings.StatAsync(path, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false) is not null)
        {
            throw VfsException.AlreadyExists(path);
        }

        try
        {
            var item = await Run(() => Store.CreateFolderAsync(library, itemPath, ct)).ConfigureAwait(false);
            _items[path] = item;
            var now = DateTimeOffset.UtcNow;
            Listings.Update(path, VfsEntry.Directory(VfsPath.SplitLast(path).Name, now, now), StringComparison.OrdinalIgnoreCase);
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            Listings.InvalidateParentOf(path);
            throw VfsException.NotFound(VfsPath.Parent(path));
        }
        catch
        {
            Listings.InvalidateParentOf(path);
            throw;
        }
    }

    public override async Task DeleteAsync(string path, CancellationToken ct)
    {
        RequireWrite("deleting");
        path = VfsPath.Normalize(path);
        var (library, itemPath) = await InsideLibraryAsync(path, ct).ConfigureAwait(false);
        RequireNotPages(library, path);
        if (_pending.TryGetValue(path, out var pendingFile))
        {
            if (pendingFile.IsClosed)
            {
                await pendingFile.Completion.ConfigureAwait(false);
            }
            else if (_pending.TryRemove(new KeyValuePair<string, PendingUploadFile>(path, pendingFile)))
            {
                // deleted while still being written: nothing goes up when the handle closes; upstream may hold an older version
                pendingFile.Discard();
                try
                {
                    await Run(() => Store.DeleteAsync(library, itemPath, false, ct)).ConfigureAwait(false);
                }
                catch (VfsException ex) when (ex.Error == VfsError.NotFound)
                {
                }

                Forget(path, false);
                return;
            }
        }

        var entry = await Listings.StatAsync(path, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
        if (entry.IsDirectory)
        {
            // the SDK's contract (as SSHFS): a folder is deleted only when empty; clients delete trees bottom-up
            var children = await Listings.GetAsync(path, ct).ConfigureAwait(false);
            if (children.Count > 0)
            {
                throw VfsException.DirectoryNotEmpty(path);
            }
        }

        try
        {
            await Run(() => Store.DeleteAsync(library, itemPath, entry.IsDirectory, ct)).ConfigureAwait(false);
        }
        catch
        {
            Listings.InvalidateParentOf(path);
            throw;
        }

        Forget(path, entry.IsDirectory);
    }

    public override async Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct)
    {
        RequireWrite("renaming");
        fromPath = VfsPath.Normalize(fromPath);
        toPath = VfsPath.Normalize(toPath);
        var (fromLibrary, fromItemPath) = await InsideLibraryAsync(fromPath, ct).ConfigureAwait(false);
        var (toLibrary, toItemPath) = await InsideLibraryAsync(toPath, ct).ConfigureAwait(false);
        RequireNotPages(fromLibrary, fromPath);
        RequireNotPages(toLibrary, toPath);
        if (string.Equals(fromPath, toPath, StringComparison.Ordinal))
        {
            return;
        }

        SharePointNames.Require(toPath);
        await SettlePendingAsync(fromPath, "renaming").ConfigureAwait(false);
        await SettlePendingAsync(toPath, "renaming over").ConfigureAwait(false);
        if (!string.Equals(fromLibrary.Key, toLibrary.Key, StringComparison.Ordinal))
        {
            throw VfsException.NotSupported("moving between document libraries");
        }

        var from = await Listings.StatAsync(fromPath, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false) ?? throw VfsException.NotFound(fromPath);
        var sameName = string.Equals(fromPath, toPath, StringComparison.OrdinalIgnoreCase);
        var to = sameName ? null : await Listings.StatAsync(toPath, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false);
        if (to is not null)
        {
            if (!replace || from.IsDirectory || to.IsDirectory)
            {
                throw VfsException.AlreadyExists(toPath);
            }

            await DeleteAsync(toPath, ct).ConfigureAwait(false);
        }

        ItemRef moved;
        try
        {
            moved = await Run(() => Store.MoveAsync(fromLibrary, fromItemPath, toItemPath, from.IsDirectory, ct)).ConfigureAwait(false);
        }
        catch
        {
            Listings.InvalidateParentOf(fromPath);
            Listings.InvalidateParentOf(toPath);
            throw;
        }

        _items[toPath] = moved;
        Forget(fromPath, from.IsDirectory);
        Listings.Update(toPath, @from with { Name = VfsPath.SplitLast(toPath).Name }, StringComparison.OrdinalIgnoreCase);
        if (from.IsDirectory)
        {
            Listings.InvalidateTree(toPath);
        }
    }

    public override async Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct)
    {
        RequireWrite("setting times");
        path = VfsPath.Normalize(path);
        if (created is null && modified is null || path.Length == 0)
        {
            return;
        }

        var place = await ResolveAsync(path, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
        if (place is not { Library: { } library, ItemPath: { Length: > 0 } itemPath })
        {
            return;   // a subsite's or a library's own folder keeps SharePoint's times
        }

        RequireNotPages(library, path);

        if (_pending.TryGetValue(path, out var pending))
        {
            if (!pending.IsClosed)
            {
                // not upstream yet: kept and applied after the upload (18-Oct-2026: WinFsp sets the times before the
                // handle closes, and asking SharePoint then failed and threw the listing away, so the new file vanished)
                pending.RequestTimes(created, modified);
                return;
            }

            await pending.Completion.ConfigureAwait(false);
        }

        try
        {
            await Run(() => Store.SetTimesAsync(library, itemPath, created, modified, ct)).ConfigureAwait(false);
        }
        finally
        {
            Listings.InvalidateParentOf(path);
        }
    }

    public override Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct)
    {
        var library = KnownLibrary(VfsPath.Normalize(path));
        return Task.FromResult(library is { QuotaTotal: { } total, QuotaRemaining: { } remaining } ? new VfsSpace(total, remaining) : null);
    }

    public override async ValueTask DisposeAsync()
    {
        // a lookup of the sites left out, and a walk, stop before the store goes
        await _stopping.CancelAsync().ConfigureAwait(false);
        Task retrying;
        lock (_leftOut)
        {
            retrying = _retrying;
        }

        await retrying.ConfigureAwait(false);
        await _walking.ConfigureAwait(false);
        if (_spool is { } spool)
        {
            _spool = null;
            await spool.DisposeAsync().ConfigureAwait(false);
        }

        _store?.Dispose();
        _store = null;
        await base.DisposeAsync().ConfigureAwait(false);
    }

    // --- helpers -------------------------------------------------------------------------------------------------

    private ListingCache Listings => _listings ?? throw VfsException.Unavailable($"{Target} is not connected yet");

    /// <summary>
    /// Where a source path leads: a site's own folder (the source's site at the root, a subsite's or another site's
    /// below it), its Subsites or Sites folder, or a place in one of its libraries; null for a name that is none of these.
    /// </summary>
    private async Task<Place?> ResolveAsync(string path, CancellationToken ct)
    {
        if (PinnedLibrary is { } pinned)
        {
            return new Place(_root, _root.Contents!.Libraries[pinned], path);
        }

        var web = _root;
        var rest = path;
        while (rest.Length > 0)
        {
            var name = Next(ref rest);
            var site = await KnownAsync(web, s => s.Libraries.ContainsKey(name) || s.Folder(name) is not null || s.Site(name) is not null, ct).ConfigureAwait(false);
            if (site.Libraries.TryGetValue(name, out var library))
            {
                return new Place(web, library, rest);
            }

            if (site.Site(name) is { } given)
            {
                web = given;   // one of the sites the source was given, at its root
                continue;
            }

            if (site.Folder(name) is not { } folder)
            {
                return null;
            }

            if (rest.Length == 0)
            {
                return new Place(web, null, "", folder);
            }

            var webName = Next(ref rest);
            site = await KnownAsync(web, s => s.Folder(name)?.Webs.ContainsKey(webName) == true, ct).ConfigureAwait(false);
            if (site.Folder(name)?.Webs.GetValueOrDefault(webName) is not { } next)
            {
                return null;
            }

            web = next;
        }

        return new Place(web, null, "");
    }

    /// <summary>The first name of a path, taken off it.</summary>
    private static string Next(ref string rest)
    {
        var slash = rest.IndexOf('/');
        var name = slash < 0 ? rest : rest[..slash];
        rest = slash < 0 ? "" : rest[(slash + 1)..];
        return name;
    }

    /// <summary>
    /// What is in a site, as its last listing found, listed again when that is not known yet or lacks what is looked for
    /// (a library or subsite made since). The listing goes through the listing cache, so at most once in a listing's
    /// life; and it cannot wait on itself, since listing a site resolves only its parents' names through here.
    /// </summary>
    private async Task<WebContents> KnownAsync(WebNode web, Func<WebContents, bool> has, CancellationToken ct)
    {
        if (web.Contents is { } site && has(site))
        {
            return site;
        }

        await Listings.GetAsync(web.Path, ct).ConfigureAwait(false);
        return web.Contents ?? await LoadWebAsync(web, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The library and the path inside it for a path inside one (below its own folder); AccessDenied for a site's or a
    /// library's own folder and for a new name beside them, which are not the client's to make, move or delete;
    /// NotFound under a site or library that is not there.
    /// </summary>
    private async Task<(LibraryInfo Library, string ItemPath)> InsideLibraryAsync(string path, CancellationToken ct)
    {
        var place = await ResolveAsync(path, ct).ConfigureAwait(false);
        if (place is { Library: { } library, ItemPath.Length: > 0 })
        {
            return (library, place.ItemPath);
        }

        if (place is not null || await ResolveAsync(VfsPath.Parent(path), ct).ConfigureAwait(false) is { Library: null })
        {
            throw VfsException.AccessDenied(path);
        }

        throw VfsException.NotFound(VfsPath.Parent(path));
    }

    /// <summary>
    /// Lists a site's libraries and sites into its node (none but the pinned library's with one pinned). A library shows
    /// by its title; the subsites, each by the last part of its address, in a <see cref="SubsitesFolder"/> folder beside
    /// the libraries, and at the tenant's root site the tenant's other sites in a <see cref="SitesFolder"/> folder; each
    /// folder only when it has something in it, with a number where a library has its name. A site listed before keeps
    /// its node, and with it what is known below it.
    /// </summary>
    private async Task<WebContents> LoadWebAsync(WebNode web, CancellationToken ct)
    {
        if (web == _root && GivenSites is { Count: > 1 })
        {
            // a source of several sites: the sites that did not open are looked up again now and then, in the background,
            // so a listing never waits on a site that does not answer
            RetryLeftOutWhenDue();
            if (web.Contents?.Sites is not null)
            {
                return web.Contents;   // each site that opened is a top-level folder (OpenSitesAsync)
            }
        }

        IReadOnlyList<WebInfo> none = [];
        var librariesCall = Run(() => Store.ListLibrariesAsync(web.Info, ct));
        var subsitesCall = PinnedLibrary is null ? SubsitesAsync(web, ct) : Task.FromResult(none);

        // at a tenant's root site (the source's own, or one of the sites it was given) its other sites too
        var showsSites = PinnedLibrary is null && (web.Info is null ? ShowsOtherSites : web.Info.IsRoot);
        var sitesCall = showsSites ? OtherSitesAsync(web.Info, ct) : Task.FromResult(none);
        await Task.WhenAll(librariesCall, subsitesCall, sitesCall).ConfigureAwait(false);

        var libraries = new Dictionary<string, LibraryInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in await librariesCall.ConfigureAwait(false))
        {
            libraries[library.Name] = web.Info is null ? library : library with { Folder = VfsPath.Join(web.Path, library.Name) };
        }

        var foundSubsites = await subsitesCall.ConfigureAwait(false);
        var foundSites = await sitesCall.ConfigureAwait(false);
        if (web == _root && Extras() is { Count: > 0 } extras)
        {
            // the root site among the sites given: the others join what it finds, each where it belongs
            foundSubsites = WithExtras(foundSubsites, extras.Where(e => e.SiteCollection != true));
            foundSites = WithExtras(foundSites, extras.Where(e => e.SiteCollection == true));
        }

        var taken = new HashSet<string>(libraries.Keys, StringComparer.OrdinalIgnoreCase);
        var folders = new List<WebFolder>();
        foreach (var (baseName, found, subsites) in new[] { (SubsitesFolder, foundSubsites, true), (SitesFolder, foundSites, false) })
        {
            if (found.Count == 0)
            {
                continue;
            }

            var name = baseName;
            for (var n = 2; !taken.Add(name); n++)
            {
                name = $"{baseName} ({n})";
            }

            var known = web.Contents?.Of(subsites)?.Webs;
            var webs = new Dictionary<string, WebNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var found1 in found)
            {
                var path = VfsPath.Join(VfsPath.Join(web.Path, name), found1.Name);
                webs[found1.Name] = known is not null && known.TryGetValue(found1.Name, out var node) && node.Info?.Key == found1.Key && node.Path == path ? node : new WebNode(found1, path);
            }

            folders.Add(new WebFolder(name, webs, subsites));
        }

        var site = new WebContents(libraries, folders);
        web.Contents = site;
        return site;
    }

    /// <summary>The sites given besides the root site, when it is the source's own (<see cref="OpenSitesAsync"/>); null otherwise.</summary>
    private List<WebInfo>? Extras()
    {
        lock (_leftOut)
        {
            return _extras is null ? null : [.. _extras];
        }
    }

    /// <summary>What a site's listing found, with the sites given that it did not find added under names clear of the rest (<see cref="WebInfo.Joining"/>).</summary>
    private static IReadOnlyList<WebInfo> WithExtras(IReadOnlyList<WebInfo> found, IEnumerable<WebInfo> extras)
    {
        var shown = found.ToList();
        foreach (var extra in extras)
        {
            if (!shown.Any(s => s.IsSameSite(extra)))
            {
                shown.Add(WebInfo.Joining(extra, shown));
            }
        }

        return shown;
    }

    /// <summary>
    /// The tenant's other sites, from search, for a root site (the source's own, or one of the sites it was given): kept
    /// for five minutes and searched again when that root or its Sites folder is listed after that. A search that fails (a
    /// farm without a search service, a refusal) keeps the last list, or none, and leaves the source's health alone: the
    /// libraries still serve.
    /// </summary>
    private async Task<IReadOnlyList<WebInfo>> OtherSitesAsync(WebInfo? root, CancellationToken ct)
    {
        var key = root?.Key ?? "";
        bool Fresh(out IReadOnlyList<WebInfo> sites)
        {
            lock (_otherSites)
            {
                var known = _otherSites.TryGetValue(key, out var k);
                sites = known ? k.Sites : [];
                return known && DateTimeOffset.UtcNow - k.Searched < OtherSitesTtl;
            }
        }

        if (Fresh(out var cached))
        {
            return cached;
        }

        await _otherSitesGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Fresh(out cached))
            {
                return cached;
            }

            var sites = cached;
            try
            {
                sites = await Store.ListSitesAsync(root, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is VfsException or SignInRequiredException)
            {
                bool first;
                lock (_otherSites)
                {
                    first = !_otherSites.ContainsKey(key);
                }

                Logger.Log(first ? LogLevel.Information : LogLevel.Debug, "sharepoint: the tenant's other sites could not be listed through search: {Message}", ex.Message);
            }

            lock (_otherSites)
            {
                _otherSites[key] = (sites, DateTimeOffset.UtcNow);
            }

            return sites;
        }
        finally
        {
            _otherSitesGate.Release();
        }
    }

    /// <summary>
    /// Opens the sites a source of several was given, all at once (<see cref="WaitForLookupsAsync"/>: each for up to
    /// <see cref="OpenWait"/>, past that only until one has opened). Those that opened are its top-level folders (named by
    /// <see cref="WebInfo.Listed"/>; two addresses in one site are one folder). The others are left out, said in the log:
    /// one still being looked up joins when it answers, one SharePoint did not open is tried again now and then
    /// (<see cref="RetryLeftOutWhenDue"/>). The source fails only when none opens: for good when each was refused or is
    /// not there.
    /// </summary>
    private async Task OpenSitesAsync(IReadOnlyList<SiteAddress> given, CancellationToken ct)
    {
        var account = SignedInAs;
        Context.ReportProgress(account is null ? $"Opening {given.Count} sites" : $"Opening {given.Count} sites as {account}");

        // the lookups go on the source's own token: one that outlasts the start (a server slow to answer) ends with the source
        var stopping = _stopping.Token;
        var lookups = given.Select(site => (Site: site, Lookup: LookUpAsync(Store, site, stopping))).ToList();
        lock (_leftOut)
        {
            _retrying = Task.WhenAll(lookups.Select(l => l.Lookup));
        }

        await WaitForLookupsAsync([.. lookups.Select(l => l.Lookup)], OpenWait, ct).ConfigureAwait(false);
        var answered = lookups.Where(l => l.Lookup.IsCompleted).Select(l => (l.Site, l.Lookup.Result.Web, l.Lookup.Result.Problem)).ToList();
        var opened = answered.Where(a => a.Web is not null).Select(a => a.Web!).DistinctBy(w => w.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var failed = answered.Where(a => a.Problem is not null).Select(a => (a.Site, Problem: a.Problem!)).ToList();
        if (opened.Count == 0)
        {
            // every lookup has ended: the wait goes on until one opens
            if (failed.Select(f => f.Problem).OfType<SignInRequiredException>().FirstOrDefault() is { } signIn)
            {
                throw new SourceFailedException(signIn.Message, permanent: signIn.Permanent, signIn);
            }

            var who = account is null ? "" : $" (signed in as {account})";
            throw new SourceFailedException($"None of the {given.Count} sites could be opened{who}: {string.Join("; ", failed.Select(f => WhyNot(f.Site, f.Problem.Message)))}",
                permanent: failed.All(f => f.Problem is VfsException { Error: VfsError.AccessDenied or VfsError.NotFound }), failed[0].Problem);
        }

        var slow = lookups.Where(l => !l.Lookup.IsCompleted).ToList();
        var webs = WebInfo.Listed(opened);
        var root = given.Select(s => (s.Scheme, s.Host)).Distinct().Count() == 1 ? webs.FirstOrDefault(w => w.IsRoot) : null;
        if (root is not null)
        {
            // the root site of the one server they are all on: it is the source's own site, and the others show in its
            // Sites folder (site collections) or Subsites folder (subsites) with what it finds there
            _root = new WebNode(root, "");
        }
        else
        {
            _root.Contents = new WebContents(new Dictionary<string, LibraryInfo>(), [], webs.ToDictionary(w => w.Name, w => new WebNode(w, w.Name), StringComparer.OrdinalIgnoreCase));
        }

        lock (_leftOut)
        {
            _extras = root is null ? null : [.. webs.Where(w => w != root)];
            _leftOut.AddRange(failed.Select(f => (f.Site, f.Problem.Message)));
            _leftOut.AddRange(slow.Select(s => (s.Site, "no answer yet")));
            _leftOutTried = DateTimeOffset.UtcNow;
            _retrying = slow.Count == 0 ? Task.CompletedTask : SettleAsync(slow);
        }

        if (failed.Count > 0)
        {
            Logger.LogWarning("sharepoint: {Count} of the {Total} sites are left out, as SharePoint did not open them, and are tried again every {Minutes} minutes: {Problems}",
                failed.Count, given.Count, LeftOutRetry.TotalMinutes, string.Join("; ", failed.Select(f => WhyNot(f.Site, f.Problem.Message))));
        }

        if (slow.Count > 0)
        {
            Logger.LogWarning("sharepoint: {Count} of the {Total} sites did not answer in {Seconds} seconds and show when they do: {Sites}",
                slow.Count, given.Count, OpenWait.TotalSeconds, string.Join(", ", slow.Select(s => s.Site.Url)));
        }
    }

    /// <summary>One of the sites given, looked up: the site, or what SharePoint answered instead (or how the lookup ended, when called off).</summary>
    internal static async Task<(WebInfo? Web, Exception? Problem)> LookUpAsync(IDocumentStore store, SiteAddress site, CancellationToken ct)
    {
        try
        {
            return (await store.FindSiteAsync(site, ct).ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    /// <summary>
    /// Waits for the lookups of the sites given: for every one up to <paramref name="wait"/>, past that only until one has
    /// opened, or every one has ended. Those still going are left to answer later.
    /// </summary>
    internal static async Task WaitForLookupsAsync(IReadOnlyList<Task<(WebInfo? Web, Exception? Problem)>> lookups, TimeSpan wait, CancellationToken ct)
    {
        var waited = Task.Delay(wait, CancellationToken.None);
        while (lookups.Any(l => !l.IsCompleted))
        {
            if (waited.IsCompleted && lookups.Any(l => l.IsCompleted && l.Result.Web is not null))
            {
                return;
            }

            var going = lookups.Where(l => !l.IsCompleted).Cast<Task>();
            await Task.WhenAny(waited.IsCompleted ? going : going.Append(waited)).WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>What SharePoint answered for a site that did not open, naming the site once: its answer alone (as a sentence) where that names it already.</summary>
    internal static string WhyNot(SiteAddress site, string problem)
    {
        problem = problem.TrimEnd('.');
        return !problem.Contains(site.Url, StringComparison.OrdinalIgnoreCase) ? $"{site.Url}: {problem}"
            : problem.Length > 0 ? char.ToUpperInvariant(problem[0]) + problem[1..] : problem;
    }

    /// <summary>Looks the sites left out up again when that is due and no lookup of them is still going.</summary>
    private void RetryLeftOutWhenDue()
    {
        lock (_leftOut)
        {
            if (_leftOut.Count == 0 || !_retrying.IsCompleted || _stopping.IsCancellationRequested || DateTimeOffset.UtcNow - _leftOutTried < LeftOutRetry)
            {
                return;
            }

            _leftOutTried = DateTimeOffset.UtcNow;
            var stopping = _stopping.Token;
            var sites = _leftOut.Select(l => l.Site).ToList();
            _retrying = Task.Run(() => SettleAsync([.. sites.Select(s => (s, LookUpAsync(Store, s, stopping)))]), CancellationToken.None);
        }
    }

    /// <summary>Takes in the lookups of sites left out, each as it ends (<see cref="Settle"/>).</summary>
    private async Task SettleAsync(IReadOnlyList<(SiteAddress Site, Task<(WebInfo? Web, Exception? Problem)> Lookup)> lookups)
    {
        try
        {
            await Task.WhenAll(lookups.Select(async l =>
            {
                var (web, problem) = await l.Lookup.ConfigureAwait(false);
                Settle(l.Site, web, problem);
            })).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "sharepoint: looking the sites left out up again failed");
        }
    }

    /// <summary>
    /// What a lookup of a site left out found. A site that opens joins the top-level folders, under a name clear of those
    /// shown (<see cref="WebInfo.Joining"/>), and the root is listed afresh; one that does not waits for the next try.
    /// </summary>
    private void Settle(SiteAddress site, WebInfo? web, Exception? problem)
    {
        WebInfo? joined = null;
        lock (_leftOut)
        {
            _leftOut.RemoveAll(l => l.Site == site);
            if (web is null)
            {
                _leftOut.Add((site, problem?.Message ?? "no answer"));
            }
            else if (_extras is { } extras)
            {
                // into the root site's Sites or Subsites folder, when the root is listed next
                if (!web.IsSameSite(_root.Info!) && !extras.Any(e => e.IsSameSite(web)))
                {
                    extras.Add(web);
                    joined = web;
                }
            }
            else
            {
                var tops = _root.Contents!.Sites!;
                if (!tops.Values.Any(t => t.Info!.IsSameSite(web)))
                {
                    joined = WebInfo.Joining(web, [.. tops.Values.Select(t => t.Info!)]);
                    _root.Contents = _root.Contents! with { Sites = new Dictionary<string, WebNode>(tops, StringComparer.OrdinalIgnoreCase) { [joined.Name] = new WebNode(joined, joined.Name) } };
                }
            }
        }

        if (joined is not null)
        {
            Logger.LogInformation("sharepoint: {Url} opens now and shows as {Name}", site.Url, joined.Name);

            // none yet when the lookup ended as the source was starting
            _listings?.Invalidate("");
            foreach (var folder in _root.Contents?.Folders ?? [])
            {
                _listings?.Invalidate(folder.Name);
            }
        }
    }

    /// <summary>A site's subsites; none when the account may not list them, which leaves the site's libraries to it.</summary>
    private async Task<IReadOnlyList<WebInfo>> SubsitesAsync(WebNode web, CancellationToken ct)
    {
        try
        {
            return await Run(() => Store.ListSubsitesAsync(web.Info, ct)).ConfigureAwait(false);
        }
        catch (VfsException ex) when (ex.Error == VfsError.AccessDenied)
        {
            Logger.LogDebug("sharepoint: the subsites of '{Site}' could not be listed: {Message}", web.Path.Length == 0 ? Store.Description : web.Path, ex.Message);
            return [];
        }
    }

    /// <summary>
    /// The library a path is in, from what is known already (statfs makes no request): the pinned one, the one the path
    /// names, else the first library of the deepest site on the way.
    /// </summary>
    private LibraryInfo? KnownLibrary(string path)
    {
        if (_root.Contents is not { } site)
        {
            return null;
        }

        if (PinnedLibrary is { } pinned)
        {
            return site.Libraries.GetValueOrDefault(pinned);
        }

        var nearest = site.Libraries.Values.FirstOrDefault();
        var names = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < names.Length; i++)
        {
            if (site.Libraries.TryGetValue(names[i], out var library))
            {
                return library;
            }

            // one of the sites the source was given is one name down
            if (site.Site(names[i]) is { } given)
            {
                if (given.Contents is not { } top)
                {
                    break;
                }

                site = top;
                nearest = site.Libraries.Values.FirstOrDefault() ?? nearest;
                continue;
            }

            // a site is two names down: its folder (Subsites or Sites), then the site
            if (site.Folder(names[i]) is not { } folder || i + 1 == names.Length || !folder.Webs.TryGetValue(names[++i], out var next) || next.Contents is not { } below)
            {
                break;
            }

            site = below;
            nearest = site.Libraries.Values.FirstOrDefault() ?? nearest;
        }

        return nearest;
    }

    /// <summary>The site's pages show as Markdown made from them (<see cref="SharePointPages"/>): nothing there is the client's to write.</summary>
    private static void RequireNotPages(LibraryInfo library, string path)
    {
        if (library.Pages)
        {
            throw VfsException.ReadOnly(path, $"{library.Shown} shows the site's pages as Markdown, for reading");
        }
    }

    /// <summary>The item's ids, from the last listing that saw it, else from the store.</summary>
    private async Task<ItemRef> ItemAsync(string path, LibraryInfo library, string itemPath, CancellationToken ct)
    {
        if (_items.TryGetValue(path, out var known))
        {
            return known;
        }

        var item = await Run(() => Store.GetItemAsync(library, itemPath, ct)).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
        _items[path] = item;
        return item;
    }

    /// <summary>A pending file whose handle has closed is waited for (its upload is landing); one still open refuses the operation.</summary>
    private async Task SettlePendingAsync(string path, string doing)
    {
        if (!_pending.TryGetValue(path, out var pending))
        {
            return;
        }

        if (!pending.IsClosed)
        {
            throw new VfsException(VfsError.AccessDenied, $"'{path}' is still open for writing and not yet uploaded; {doing} it must wait for it to close");
        }

        await pending.Completion.ConfigureAwait(false);
    }

    private void Forget(string path, bool wasDirectory)
    {
        _items.TryRemove(path, out _);
        Listings.Update(path, null, StringComparison.OrdinalIgnoreCase);
        if (wasDirectory)
        {
            Listings.InvalidateTree(path);
            foreach (var key in _items.Keys.Where(k => VfsPath.IsUnder(k, path)).ToList())
            {
                _items.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Runs a store call: a lapsed sign-in becomes Unavailable and Degraded; a success clears Degraded.</summary>
    private async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            var result = await call().ConfigureAwait(false);
            MarkHealthy();
            return result;
        }
        catch (SignInRequiredException ex)
        {
            throw Lapsed(ex);
        }
        catch (VfsException ex) when (ex.Error == VfsError.Unavailable)
        {
            MarkDegraded(ex.Message);
            throw;
        }
    }

    private async Task Run(Func<Task> call)
    {
        await Run(async () =>
        {
            await call().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    private static IAsyncEnumerable<T> Run<T>(Func<IAsyncEnumerable<T>> call) => call();

    private VfsException Lapsed(SignInRequiredException ex)
    {
        MarkDegraded(ex.Message);
        return VfsException.Unavailable(ex.Message);
    }

    /// <summary>For the flavour's connection code: the source's health follows the store's fortunes.</summary>
    protected void MarkDegraded(string detail)
    {
        if (!_degraded && IsInitialised)
        {
            _degraded = true;
            Context.ReportHealth(ComponentHealth.Degraded, detail);
        }
    }

    protected void MarkHealthy()
    {
        if (_degraded && IsInitialised)
        {
            _degraded = false;
            Context.ReportHealth(ComponentHealth.Ready);
        }
    }

    /// <summary>A site the source shows as a folder: its own site at the root, a subsite or another site below it.</summary>
    /// <param name="info">The site, or null for the source's own.</param>
    /// <param name="path">The folder it shows as ("" for the source's own site).</param>
    private sealed class WebNode(WebInfo? info, string path)
    {
        public WebInfo? Info { get; } = info;

        public string Path { get; } = path;

        public string Name => VfsPath.SplitLast(Path).Name;

        /// <summary>Its libraries and sites as last listed; null until it is first needed.</summary>
        public volatile WebContents? Contents;
    }

    /// <summary>A folder of sites in a site's folder: its subsites, or (at the tenant's root site) the tenant's other sites.</summary>
    /// <param name="Name">The name it shows as: <see cref="SharePointSource.SubsitesFolder"/> or <see cref="SharePointSource.SitesFolder"/>, with a number where a library has that title.</param>
    /// <param name="Webs">The sites in it, by the names they show as.</param>
    /// <param name="Subsites">True for the subsites' folder, false for the other sites'.</param>
    private sealed record WebFolder(string Name, IReadOnlyDictionary<string, WebNode> Webs, bool Subsites);

    /// <summary>
    /// What is in a site's folder: its libraries by title and its folders of sites (only those with sites in them). The
    /// root of a source given several sites holds those sites instead (<see cref="Sites"/>), and nothing else.
    /// </summary>
    private sealed record WebContents(IReadOnlyDictionary<string, LibraryInfo> Libraries, IReadOnlyList<WebFolder> Folders, IReadOnlyDictionary<string, WebNode>? Sites = null)
    {
        public WebFolder? Folder(string name) => Folders.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        public WebFolder? Of(bool subsites) => Folders.FirstOrDefault(f => f.Subsites == subsites);

        public WebNode? Site(string name) => Sites?.GetValueOrDefault(name);
    }

    /// <summary>Where a source path leads (<see cref="ResolveAsync"/>).</summary>
    /// <param name="Library">The library, for a place in one ("" for its own folder); null for a site's own folder or one of its folders of sites.</param>
    /// <param name="Folder">The folder of sites when that is the place; null otherwise.</param>
    private sealed record Place(WebNode Web, LibraryInfo? Library, string ItemPath, WebFolder? Folder = null);

    /// <summary>A response body stream that disposes its response with it.</summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream body) : Stream
    {
        public override bool CanRead => body.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => body.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => body.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => body.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                body.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await body.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
