using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// The file-system half shared by the SharePoint flavours, over an <see cref="IDocumentStore"/>. Source paths
/// are <c>library/folder/file</c>, with a site's subsites in a <c>Subsites</c> folder beside its libraries, each with
/// its own inside it (<c>Subsites/subsite/library/folder/file</c>, and so on down), at the tenant's root site the
/// tenant's other sites in a <c>Sites</c> folder the same way (<c>Sites/Axle/library/...</c>, found by search), or
/// just <c>folder/file</c> when one library is pinned.
/// Listings sit in a two-second <see cref="ListingCache"/> and stats are answered from it; a listing also remembers each item's
/// ids (and download URL, where the flavour has one), so an open needs no extra round trip. Files below the
/// spool threshold are downloaded whole into the <see cref="SpoolCache"/> (and served again from it while
/// their version is unchanged); larger ones are read in ranges through <see cref="RandomAccessOverStream"/>,
/// several at a time. Writes go to a local temp file and are uploaded when the handle closes. Trouble with the
/// upstream shows as Degraded until a call succeeds again. A flavour supplies the store (after signing in)
/// and the settings that shape the tree.
/// </summary>
public abstract class SharePointSource : SourceBase
{
    /// <summary>The folder beside a site's libraries that holds its subsites, one folder each.</summary>
    public const string SubsitesFolder = "Subsites";

    /// <summary>The folder beside the tenant's root site's libraries that holds the tenant's other sites, one folder each.</summary>
    public const string SitesFolder = "Sites";

    private static readonly TimeSpan ListingTtl = TimeSpan.FromSeconds(2);

    // search finds a new site only minutes after it is made, so asking more often than this gains nothing
    private static readonly TimeSpan OtherSitesTtl = TimeSpan.FromMinutes(5);

    // the source's own site; its subsites (and, at the tenant's root, the other sites) hang below it as they are listed
    private readonly WebNode _root = new(null, "");
    private readonly ConcurrentDictionary<string, ItemRef> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _otherSitesGate = new(1, 1);
    private IReadOnlyList<WebInfo> _otherSites = [];
    private DateTimeOffset _otherSitesSearched = DateTimeOffset.MinValue;

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

    /// <summary>Signs in and connects; throw <see cref="SourceFailedException"/> for a reason the user must act on.</summary>
    private protected abstract Task<IDocumentStore> ConnectAsync(CancellationToken ct);

    /// <summary>The store, once connected (for tests).</summary>
    internal IDocumentStore Store => _store ?? throw VfsException.Unavailable($"{Target} is not connected yet");

    protected sealed override async Task OnInitialiseAsync(CancellationToken ct)
    {
        var store = await ConnectAsync(ct).ConfigureAwait(false);
        _store = store;
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

        _listings = new ListingCache(ListingTtl, ListRemoteAsync);
        _spool = new SpoolCache(Path.Combine(Context.CacheDirectory, "spool"), logger: Logger);
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

        var subsites = site.Of(subsites: true) is { } s ? $", {s.Webs.Count} subsite{(s.Webs.Count == 1 ? "" : "s")}" : "";
        var others = site.Of(subsites: false) is { } o ? $", {o.Webs.Count} other site{(o.Webs.Count == 1 ? "" : "s")}" : "";
        Logger.LogInformation("sharepoint: serving {Site} ({Count} librar{Plural}{Subsites}{Others}){Pinned}", store.Description, site.Libraries.Count, site.Libraries.Count == 1 ? "y" : "ies", subsites, others, PinnedLibrary is { } p ? $", pinned to {p}" : "");
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
        foreach (var entry in await Listings.GetAsync(path, ct).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

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
                { Library: null } => VfsException.NotSupported($"creating a {Store.LibraryNoun} (create folders inside one)"),
                _ => VfsException.NotFound(VfsPath.Parent(path)),
            };
        }

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
            var site = await KnownAsync(web, s => s.Libraries.ContainsKey(name) || s.Folder(name) is not null, ct).ConfigureAwait(false);
            if (site.Libraries.TryGetValue(name, out var library))
            {
                return new Place(web, library, rest);
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
        IReadOnlyList<WebInfo> none = [];
        var librariesCall = Run(() => Store.ListLibrariesAsync(web.Info, ct));
        var subsitesCall = PinnedLibrary is null ? SubsitesAsync(web, ct) : Task.FromResult(none);
        var sitesCall = PinnedLibrary is null && web.Info is null && ShowsOtherSites ? OtherSitesAsync(ct) : Task.FromResult(none);
        await Task.WhenAll(librariesCall, subsitesCall, sitesCall).ConfigureAwait(false);

        var libraries = new Dictionary<string, LibraryInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in await librariesCall.ConfigureAwait(false))
        {
            libraries[library.Name] = web.Info is null ? library : library with { Folder = VfsPath.Join(web.Path, library.Name) };
        }

        var taken = new HashSet<string>(libraries.Keys, StringComparer.OrdinalIgnoreCase);
        var folders = new List<WebFolder>();
        foreach (var (baseName, found, subsites) in new[] { (SubsitesFolder, await subsitesCall.ConfigureAwait(false), true), (SitesFolder, await sitesCall.ConfigureAwait(false), false) })
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

    /// <summary>
    /// The tenant's other sites, from search: kept for five minutes and searched again when the root or its Sites folder
    /// is listed after that. A search that fails (a farm without a search service, a refusal) keeps the last list, or
    /// none, and leaves the source's health alone: the libraries still serve.
    /// </summary>
    private async Task<IReadOnlyList<WebInfo>> OtherSitesAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _otherSitesSearched < OtherSitesTtl)
        {
            return _otherSites;
        }

        await _otherSitesGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow - _otherSitesSearched < OtherSitesTtl)
            {
                return _otherSites;
            }

            try
            {
                _otherSites = await Store.ListSitesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is VfsException or SignInRequiredException)
            {
                var first = _otherSitesSearched == DateTimeOffset.MinValue;
                Logger.Log(first ? LogLevel.Information : LogLevel.Debug, "sharepoint: the tenant's other sites could not be listed through search: {Message}", ex.Message);
            }

            _otherSitesSearched = DateTimeOffset.UtcNow;
            return _otherSites;
        }
        finally
        {
            _otherSitesGate.Release();
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

    /// <summary>What is in a site's folder: its libraries by title and its folders of sites (only those with sites in them).</summary>
    private sealed record WebContents(IReadOnlyDictionary<string, LibraryInfo> Libraries, IReadOnlyList<WebFolder> Folders)
    {
        public WebFolder? Folder(string name) => Folders.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        public WebFolder? Of(bool subsites) => Folders.FirstOrDefault(f => f.Subsites == subsites);
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
