using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.ThreeDX;

/// <summary>
/// A 3DX source (spec 7.6): the documents of a 3DEXPERIENCE platform as a folder each, with their files, read-only, as
/// Mount3DX shows them. 3DX is slow to list and to serve, so the source keeps what it knows: every document and file is
/// listed at the start (<see cref="DocumentLister"/>) into a tree held in memory (<see cref="DocumentTree"/>), which answers
/// every listing and stat without a request; the tree is saved in the source's state folder, so a restart serves the last
/// one at once while it is listed afresh in the background, as it is every <see cref="ThreeDXSettings.RefreshMinutes"/>. A
/// file up to the spool size is downloaded whole on open into the <see cref="SpoolCache"/>, served as it arrives and kept
/// for the next open while its version stands; a larger one is read in ranges as the client asks
/// (<see cref="RandomAccessOverStream"/>), several at a time. The sessions are kept alive as Mount3DX keeps them, and
/// renewed when they lapse (<see cref="ThreeDXSession"/>); trouble with 3DX shows as Degraded until a request succeeds
/// again.
/// </summary>
public sealed class ThreeDXSource : SourceBase, ICountable
{
    // a document's metadata is made again after this, when read again
    private static readonly TimeSpan MetadataFor = TimeSpan.FromMinutes(1);

    private readonly ThreeDXSettings _settings;
    private readonly SessionOptions? _options;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<string, (byte[] Content, DateTime At)> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _sizes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _rangeIgnoredSaid = new(StringComparer.Ordinal);
    private volatile DocumentTree _tree = DocumentTree.Empty;
    private ThreeDXSession? _session;
    private ThreeDXClient? _client;
    private SpoolCache? _spool;
    private Task _refreshing = Task.CompletedTask;
    private Task _keepingAlive = Task.CompletedTask;
    private bool _degraded;
    private DateOrder? _dates;

    public ThreeDXSource(ThreeDXSettings settings)
        : this(settings, null)
    {
    }

    /// <param name="options">What a test changes about the session; null for the real thing.</param>
    internal ThreeDXSource(ThreeDXSettings settings, SessionOptions? options)
    {
        _settings = settings;
        _options = options;
    }

    public override SourceCapabilities Capabilities => new(CanWrite: false, CaseInsensitive: true);

    /// <summary>The tree served now (for tests).</summary>
    internal DocumentTree Tree => _tree;

    /// <summary>The session (for tests).</summary>
    internal ThreeDXSession Session => _session ?? throw new InvalidOperationException("the source has not started");

    private ThreeDXClient Client => _client ?? throw VfsException.Unavailable("3DX is not signed in to yet");

    private Uri? Links => _settings.MetadataFiles ? _settings.Space : null;

    private string IndexPath => Path.Combine(Context.StateDirectory, IndexFile.FileName);

    private TimeSpan RefreshEvery => TimeSpan.FromMinutes(Math.Max(0, _settings.RefreshMinutes));

    private long SpoolBelowBytes => (long)Math.Max(0, _settings.SpoolBelowMiB) << 20;

    // the order 3DX's numeric dates are read in, decided once per start (the PC's, unless the settings say)
    private DateOrder Dates => _dates ??= ThreeDXDates.Resolve(_settings.DateOrder);

    protected override async Task OnInitialiseAsync(CancellationToken ct)
    {
        var space = _settings.Space ?? throw new SourceFailedException("The 3DSpace URL is not an http or https address.", permanent: true);
        if (_settings.EffectiveSignIn == ThreeDXSignIn.WindowsCurrent && !OperatingSystem.IsWindows())
        {
            throw new SourceFailedException("Signing in as yourself works on Windows only: choose Windows account and give a user name and password, or Web session.", permanent: true);
        }

        _session = new ThreeDXSession(_settings, Logger, Context.Secrets, _options);
        try
        {
            await _session.SignInAsync(interactive: true, message => Context.ReportProgress(message), ct).ConfigureAwait(false);
        }
        catch (SignInRequiredException ex)
        {
            throw new SourceFailedException(ex.Message, permanent: ex.Permanent, ex);
        }

        _client = new ThreeDXClient(_session, Dates, Logger, MarkDegraded, MarkHealthy);
        _spool = new SpoolCache(Path.Combine(Context.CacheDirectory, "spool"), logger: Logger);

        DateTimeOffset? due;
        var saved = IndexFile.Load(IndexPath, space.AbsoluteUri, _settings.Query, _settings.EffectiveTenant, Dates.ToString());
        if (saved is not null)
        {
            Swap(DocumentTree.Build(saved.Documents, Links, saved.Taken));
            var age = DateTimeOffset.UtcNow - saved.Taken;
            due = RefreshEvery > TimeSpan.Zero ? saved.Taken + RefreshEvery : DateTimeOffset.UtcNow;
            Logger.LogInformation("3dx: serving the {Count} documents ({Files} files) listed {Minutes:F0} minutes ago; listing them afresh {When}",
                _tree.Folders.Count, _tree.FileCount, age.TotalMinutes, due <= DateTimeOffset.UtcNow ? "now, in the background" : $"at {due.Value.ToLocalTime():HH:mm}");
        }
        else
        {
            try
            {
                await RefreshAsync(report: true, ct).ConfigureAwait(false);
            }
            catch (SignInRequiredException ex)
            {
                throw new SourceFailedException(ex.Message, permanent: ex.Permanent, ex);
            }
            catch (VfsException ex)
            {
                throw new SourceFailedException($"Listing the documents on {space.Host}: {ex.Message}", permanent: ex.Error == VfsError.AccessDenied, ex);
            }

            due = RefreshEvery > TimeSpan.Zero ? DateTimeOffset.UtcNow + RefreshEvery : null;
        }

        var stopping = _stopping.Token;
        _refreshing = Task.Run(() => RefreshLoopAsync(due, stopping), CancellationToken.None);
        if (_settings.KeepAliveMinutes > 0)
        {
            _keepingAlive = Task.Run(() => KeepAliveLoopAsync(TimeSpan.FromMinutes(_settings.KeepAliveMinutes), stopping), CancellationToken.None);
        }

        Logger.LogInformation("3dx: serving {Space} ({How}): {Count} documents, {Files} files", space, _session.How, _tree.Folders.Count, _tree.FileCount);
    }

    // --- the document list ------------------------------------------------------------------------------------------

    /// <summary>The tree's counts are exact: the source knows every document once listed.</summary>
    public Task CountFilesAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Lists every document afresh and serves the new tree, saving it for the next start; one listing at a time. With
    /// <paramref name="report"/> (the start), its progress shows on the source's tile.
    /// </summary>
    internal async Task RefreshAsync(bool report, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var taken = DateTimeOffset.UtcNow;
            var started = Stopwatch.GetTimestamp();
            Action<int, int, int>? progress = report
                ? (done, pages, documents) => Context.ReportProgress($"Listing the documents: page {done:N0} of {pages:N0}, {documents:N0} documents", pages == 0 ? null : (double)done / pages)
                : null;
            var documents = await DocumentLister.ListAsync(Client, _settings.Query, _settings.EffectiveTenant, Math.Clamp(_settings.QueryThreads, 1, 32), progress, Logger, ct).ConfigureAwait(false);
            var tree = DocumentTree.Build(documents, Links, taken);
            Swap(tree);
            Save(tree);
            Logger.LogInformation("3dx: listed {Count} documents ({Files} files) in {Seconds:F0} s", tree.Folders.Count, tree.FileCount, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// The listings after the first, each when it is due (every <see cref="ThreeDXSettings.RefreshMinutes"/> after the last;
    /// none when that is 0). A listing that fails leaves the last tree served and shows the source as Degraded until the next
    /// one succeeds.
    /// </summary>
    private async Task RefreshLoopAsync(DateTimeOffset? due, CancellationToken ct)
    {
        while (due is { } at && !ct.IsCancellationRequested)
        {
            try
            {
                var wait = at - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }

                await RefreshAsync(report: false, ct).ConfigureAwait(false);
                MarkHealthy();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "3dx: listing the documents afresh failed; the last list is still served");
                MarkDegraded($"the document list could not be refreshed: {ex.Message}");
            }

            due = RefreshEvery > TimeSpan.Zero ? DateTimeOffset.UtcNow + RefreshEvery : null;
        }
    }

    private async Task KeepAliveLoopAsync(TimeSpan every, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(every, ct).ConfigureAwait(false);
                await Client.PingAsync(ct).ConfigureAwait(false);
                Session.Save();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SignInRequiredException ex)
            {
                Logger.LogWarning("3dx: keeping the session alive failed: {Error}", ex.Message);
                MarkDegraded(ex.Message);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("3dx: keeping the session alive failed: {Error}", ex.Message);
                MarkDegraded($"3DX did not answer the keep-alive: {ex.Message}");
            }
        }
    }

    private void Swap(DocumentTree tree)
    {
        _tree = tree;
        Counters.Set(tree.FileCount, tree.Folders.Count, EnumerationState.Complete);
    }

    private void Save(DocumentTree tree)
    {
        try
        {
            IndexFile.Save(IndexPath, new SavedIndex(IndexFile.Version, _settings.Space!.AbsoluteUri, _settings.Query, _settings.EffectiveTenant, Dates.ToString(), tree.Taken, tree.Documents));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning("3dx: the document list could not be kept for the next start: {Error}", ex.Message);
        }
    }

    // --- listing and stat -------------------------------------------------------------------------------------------------

    public override async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
        {
            return VfsEntry.Directory("");
        }

        var (folder, file) = Resolve(path);
        if (folder is null)
        {
            return null;
        }

        if (file is null)
        {
            return VfsPath.SplitLast(path).Parent.Length == 0 ? folder.Entry : null;
        }

        return file.Kind switch
        {
            // a stat opens the file, as the client is about to: the real length, made now (a listing gives a placeholder)
            FolderFileKind.Metadata => folder.EntryOf(file) with { Length = (await MetadataAsync(folder, ct).ConfigureAwait(false)).Length },
            FolderFileKind.Document when file.File!.Size is null => folder.EntryOf(file) with { Length = await SizeAsync(folder, file, path, ct).ConfigureAwait(false) },
            _ => folder.EntryOf(file),
        };
    }

    public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
        {
            foreach (var folder in _tree.Folders)
            {
                yield return folder.Entry;
            }

            yield break;
        }

        var (found, file) = Resolve(path);
        if (found is null || VfsPath.SplitLast(path).Parent.Length > 0 && file is null)
        {
            throw VfsException.NotFound(path);
        }

        if (file is not null)
        {
            throw VfsException.NotADirectory(path);
        }

        foreach (var item in found.Files)
        {
            var entry = found.EntryOf(item);
            yield return item.Kind == FolderFileKind.Metadata && _metadata.TryGetValue(found.Document.Id, out var made)
                ? entry with { Length = made.Content.Length }
                : item.Kind == FolderFileKind.Document && item.File!.Size is null && _sizes.TryGetValue(item.ContentKey, out var size)
                    ? entry with { Length = size }
                    : entry;
        }
    }

    /// <summary>The document folder and file a path names (the file null for the folder itself); both null when there is none.</summary>
    private (DocumentFolder? Folder, FolderFile? File) Resolve(string path)
    {
        var slash = path.IndexOf('/');
        var folder = _tree.Folder(slash < 0 ? path : path[..slash]);
        if (folder is null || slash < 0)
        {
            return (folder, null);
        }

        var rest = path[(slash + 1)..];
        if (rest.Contains('/'))
        {
            return (null, null);
        }

        return folder.File(rest) is { } file ? (folder, file) : (null, null);
    }

    // --- open -------------------------------------------------------------------------------------------------------------

    public override async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (mode != VfsOpenMode.Read)
        {
            // 3DX is served read-only
            RequireWrite("writing");
        }

        var (folder, file) = Resolve(path);
        if (path.Length == 0 || folder is not null && file is null)
        {
            throw VfsException.IsADirectory(path);
        }

        if (folder is null || file is null)
        {
            throw VfsException.NotFound(path);
        }

        switch (file.Kind)
        {
            case FolderFileKind.Link:
                return new MemoryFile(DocumentTree.Link(_settings.Space!, folder.Document.Id), "a link to a document");
            case FolderFileKind.Metadata:
                return new MemoryFile(await MetadataAsync(folder, ct).ConfigureAwait(false), "a document's metadata");
        }

        var length = file.File!.Size ?? await SizeAsync(folder, file, path, ct).ConfigureAwait(false);
        if (length <= SpoolBelowBytes && _spool is { } spool)
        {
            return await spool.OpenAsync(file.ContentKey, length, downloadCt => OpenAtAsync(folder, file, 0, path, downloadCt), ct).ConfigureAwait(false);
        }

        return new RandomAccessOverStream(length, (offset, openCt) => OpenAtAsync(folder, file, offset, path, openCt), maxStreams: Math.Clamp(_settings.Connections, 1, 16), maxSkipBytes: 1 << 20);
    }

    /// <summary>
    /// A file's content from <paramref name="offset"/>: the file server's answer to its download ticket; past the end, nothing.
    /// A file server that ignores the range sends the whole file, and the part before the offset is read past.
    /// </summary>
    private async Task<Stream> OpenAtAsync(DocumentFolder folder, FolderFile file, long offset, string path, CancellationToken ct)
    {
        var response = await Run(() => Client.OpenFileAsync(folder.Document.Id, file.File!.Id, offset, path, ct)).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            response.Dispose();
            return Stream.Null;
        }

        var body = await ResponseStream.OpenAsync(response, ct).ConfigureAwait(false);
        if (offset == 0)
        {
            return body;
        }

        try
        {
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (response.Content.Headers.ContentRange?.From is { } from && from != offset)
                {
                    throw new VfsException(VfsError.IoError, $"'{path}': asked for byte {offset:N0} on, the file server sent from byte {from:N0}");
                }

                return body;
            }

            if (_rangeIgnoredSaid.TryAdd(file.ContentKey, true))
            {
                Logger.LogInformation("3dx: the file server sends '{Path}' whole when asked for a range; reading past the first {Offset:N0} bytes", path, offset);
            }

            await SkipAsync(body, offset, ct).ConfigureAwait(false);
            return body;
        }
        catch
        {
            await body.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task SkipAsync(Stream body, long count, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var n = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new VfsException(VfsError.IoError, "the file ended before the place asked for");
            }

            count -= n;
        }
    }

    /// <summary>The length of a file 3DX gives no size for: from its download's headers (counted through when they give none), asked once.</summary>
    private async Task<long> SizeAsync(DocumentFolder folder, FolderFile file, string path, CancellationToken ct)
    {
        if (_sizes.TryGetValue(file.ContentKey, out var known))
        {
            return known;
        }

        using var response = await Run(() => Client.OpenFileAsync(folder.Document.Id, file.File!.Id, 0, path, ct)).ConfigureAwait(false);
        var length = response.Content.Headers.ContentLength ?? -1;
        if (length < 0)
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            length = 0;
            for (int n; (n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0;)
            {
                length += n;
            }
        }

        _sizes[file.ContentKey] = length;
        return length;
    }

    /// <summary>A document's <see cref="ThreeDXNames.MetadataFile"/>: made from 3DX's answers, kept for a minute for the reads after the open.</summary>
    private async Task<byte[]> MetadataAsync(DocumentFolder folder, CancellationToken ct)
    {
        var id = folder.Document.Id;
        if (_metadata.TryGetValue(id, out var made) && DateTime.UtcNow - made.At < MetadataFor)
        {
            return made.Content;
        }

        var content = await Run(() => Client.MetadataAsync(id, ct)).ConfigureAwait(false);
        _metadata[id] = (content, DateTime.UtcNow);
        return content;
    }

    // --- health -----------------------------------------------------------------------------------------------------------

    /// <summary>Runs a call to 3DX: a sign-in that needs a person becomes Unavailable and Degraded; a success clears Degraded.</summary>
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
            MarkDegraded(ex.Message);
            throw VfsException.Unavailable(ex.Message);
        }
        catch (VfsException ex) when (ex.Error == VfsError.Unavailable)
        {
            MarkDegraded(ex.Message);
            throw;
        }
    }

    private void MarkDegraded(string detail)
    {
        if (!_degraded && IsInitialised)
        {
            _degraded = true;
            Context.ReportHealth(ComponentHealth.Degraded, detail);
        }
    }

    private void MarkHealthy()
    {
        if (_degraded && IsInitialised)
        {
            _degraded = false;
            Context.ReportHealth(ComponentHealth.Ready);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_refreshing, _keepingAlive).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        if (_spool is not null)
        {
            await _spool.DisposeAsync().ConfigureAwait(false);
        }

        if (_session is not null)
        {
            _session.Save();
            _session.Dispose();
        }

        _stopping.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
