using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// The file-system half shared by the SharePoint flavours, over an <see cref="IDocumentStore"/>. Source paths
/// are <c>library/folder/file</c>, or just <c>folder/file</c> when one library is pinned. Listings sit in a
/// two-second <see cref="ListingCache"/> and stats are answered from it; a listing also remembers each item's
/// ids (and download URL, where the flavour has one), so an open needs no extra round trip. Files below the
/// spool threshold are downloaded whole into the <see cref="SpoolCache"/> (and served again from it while
/// their version is unchanged); larger ones are read in ranges through <see cref="RandomAccessOverStream"/>,
/// several at a time. Writes go to a local temp file and are uploaded when the handle closes. Trouble with the
/// upstream shows as Degraded until a call succeeds again. A flavour supplies the store (after signing in)
/// and the settings that shape the tree.
/// </summary>
public abstract class SharePointSource : SourceBase
{
    private static readonly TimeSpan ListingTtl = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, LibraryInfo> _libraries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ItemRef> _items = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Signs in and connects; throw <see cref="SourceFailedException"/> for a reason the user must act on.</summary>
    private protected abstract Task<IDocumentStore> ConnectAsync(CancellationToken ct);

    /// <summary>The store, once connected (for tests).</summary>
    internal IDocumentStore Store => _store ?? throw VfsException.Unavailable($"{Target} is not connected yet");

    protected sealed override async Task OnInitialiseAsync(CancellationToken ct)
    {
        var store = await ConnectAsync(ct).ConfigureAwait(false);
        _store = store;
        try
        {
            await RefreshLibrariesAsync(ct).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            throw new SourceFailedException($"{Target}: {ex.Message}", permanent: ex.Error is VfsError.AccessDenied or VfsError.NotFound, ex);
        }

        if (PinnedLibrary is { } pinned && !_libraries.ContainsKey(pinned))
        {
            throw new SourceFailedException($"{store.Description} has no {store.LibraryNoun} named {pinned}; it has: {string.Join(", ", _libraries.Values.Select(l => l.Name))}", permanent: true);
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

        Logger.LogInformation("sharepoint: serving {Site} ({Count} librar{Plural}){Pinned}", store.Description, _libraries.Count, _libraries.Count == 1 ? "y" : "ies", PinnedLibrary is { } p ? $", pinned to {p}" : "");
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

        if (IsLibraryLevel(path))
        {
            if (!_libraries.ContainsKey(path))
            {
                await Listings.GetAsync("", ct).ConfigureAwait(false);   // refreshes the library list
            }

            return _libraries.TryGetValue(path, out var library) ? VfsEntry.Directory(library.Name) : null;
        }

        return await Listings.StatAsync(path, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false);
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
        if (path.Length == 0 && PinnedLibrary is null)
        {
            await RefreshLibrariesAsync(ct).ConfigureAwait(false);
            foreach (var library in _libraries.Values.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            {
                yield return VfsEntry.Directory(library.Name);
            }

            yield break;
        }

        var (library0, itemPath) = Locate(path);
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

        if (IsLibraryLevel(path))
        {
            throw VfsException.InvalidPath(path, $"files live inside a {Store.LibraryNoun}");
        }

        if (existing is not null && mode == VfsOpenMode.CreateNew)
        {
            throw VfsException.AlreadyExists(path);
        }

        if (mode == VfsOpenMode.Read)
        {
            return await OpenForReadAsync(path, existing!, ct).ConfigureAwait(false);
        }

        RequireWrite("writing");
        SharePointNames.Require(path);
        var (library, itemPath) = Locate(path);
        var parentPath = VfsPath.Parent(path);
        if (existing is null && !IsLibraryLevel(parentPath))
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
            await DownloadToAsync(path, existing!, tempPath, ct).ConfigureAwait(false);
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

    private async Task<IVfsFile> OpenForReadAsync(string path, VfsEntry entry, CancellationToken ct)
    {
        var (library, itemPath) = Locate(path);
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

    private async Task DownloadToAsync(string path, VfsEntry entry, string tempPath, CancellationToken ct)
    {
        var (library, itemPath) = Locate(path);
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

        if (IsLibraryLevel(path))
        {
            throw VfsException.NotSupported($"creating a {Store.LibraryNoun} (create folders inside one)");
        }

        SharePointNames.Require(path);
        var (library, itemPath) = Locate(path);
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
        if (path.Length == 0 || IsLibraryLevel(path))
        {
            throw VfsException.AccessDenied(path);
        }

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
                var (pendingLibrary, pendingItemPath) = Locate(path);
                try
                {
                    await Run(() => Store.DeleteAsync(pendingLibrary, pendingItemPath, false, ct)).ConfigureAwait(false);
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

        var (library, itemPath) = Locate(path);
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
        if (fromPath.Length == 0 || toPath.Length == 0 || IsLibraryLevel(fromPath) || IsLibraryLevel(toPath))
        {
            throw VfsException.AccessDenied(fromPath.Length == 0 || IsLibraryLevel(fromPath) ? fromPath : toPath);
        }

        if (string.Equals(fromPath, toPath, StringComparison.Ordinal))
        {
            return;
        }

        SharePointNames.Require(toPath);
        await SettlePendingAsync(fromPath, "renaming").ConfigureAwait(false);
        await SettlePendingAsync(toPath, "renaming over").ConfigureAwait(false);
        var (fromLibrary, fromItemPath) = Locate(fromPath);
        var (toLibrary, toItemPath) = Locate(toPath);
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
        if (created is null && modified is null || path.Length == 0 || IsLibraryLevel(path))
        {
            return;
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

        var (library, itemPath) = Locate(path);
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
        path = VfsPath.Normalize(path);
        LibraryInfo? library = null;
        if (PinnedLibrary is { } pinned)
        {
            _libraries.TryGetValue(pinned, out library);
        }
        else if (path.Length > 0)
        {
            _libraries.TryGetValue(SharePointPaths.Split(path, null).Library, out library);
        }

        library ??= _libraries.Values.FirstOrDefault();
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

    /// <summary>True for a path that names a library (and nothing inside it) when the libraries are the top level.</summary>
    private bool IsLibraryLevel(string path) => PinnedLibrary is null && path.Length > 0 && !path.Contains('/');

    private async Task RefreshLibrariesAsync(CancellationToken ct)
    {
        var libraries = await Run(() => Store.ListLibrariesAsync(ct)).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            _libraries[library.Name] = library;
            seen.Add(library.Name);
        }

        foreach (var gone in _libraries.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _libraries.TryRemove(gone, out _);
        }
    }

    /// <summary>The library and the path inside it for a source path (the library must exist).</summary>
    private (LibraryInfo Library, string ItemPath) Locate(string path)
    {
        var (name, itemPath) = SharePointPaths.Split(path, PinnedLibrary);
        if (!_libraries.TryGetValue(name, out var library))
        {
            throw VfsException.NotFound(PinnedLibrary is null ? name : path);
        }

        return (library, itemPath);
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
