using System.Net.Sockets;
using System.Runtime.CompilerServices;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace AnyFS.Sources.Sshfs;

/// <summary>
/// SFTP through a <see cref="SftpPool"/>. Paths map to <c>root/path</c>; listings go through a two-second
/// <see cref="ListingCache"/> and stats are answered from it; a symbolic link shows as what it points at (a
/// dangling one as an empty file); names starting with a dot are hidden; an entry nobody may write is read-only.
/// A dropped connection is retried once on a fresh one, and the source turns Degraded until something succeeds.
/// "Count files on start" lists many folders at once over the pool instead of the host's one-at-a-time walk.
/// </summary>
public sealed class SshfsSource : SourceBase, ICountable
{
    /// <summary>How many symbolic links in one listing are resolved at the same time.</summary>
    private const int LinkResolveParallelism = 8;

    private readonly SshfsSettings _settings;
    private SftpPool? _pool;
    private ListingCache? _listings;
    private string _root = "/";
    private bool _driveRoot;
    private bool _degraded;

    public SshfsSource(SshfsSettings settings)
    {
        _settings = settings;
    }

    public override SourceCapabilities Capabilities => SourceCapabilities.Writable;

    /// <summary>The absolute remote root after ~ is resolved (for tests).</summary>
    public string RemoteRoot => _root;

    protected override async Task OnInitialiseAsync(CancellationToken ct)
    {
        if (!RemotePath.TryParseRoot(_settings.Root, out var root, out var reason))
        {
            throw new SourceFailedException(reason, permanent: true);
        }

        if (string.IsNullOrWhiteSpace(_settings.Host) || string.IsNullOrWhiteSpace(_settings.Username))
        {
            throw new SourceFailedException("The host and the user name are required.", permanent: true);
        }

        var pool = new SftpPool(_settings, Context, new HostKeyStore(Path.Combine(Context.StateDirectory, "hostkeys.txt")));
        _pool = pool;
        Context.ReportProgress($"Connecting to {_settings.Host}:{_settings.Port}");
        var client = await pool.ConnectFirstAsync(ct).ConfigureAwait(false);
        _root = RemotePath.Resolve(root, pool.Home);
        _driveRoot = _root == "/" && RemotePath.IsDriveHome(pool.Home);

        Context.ReportProgress("Checking the remote root");
        try
        {
            var attributes = await client.GetAttributesAsync(_root, ct).ConfigureAwait(false);
            if (!attributes.IsDirectory)
            {
                throw new SourceFailedException($"{_root} on {_settings.Host} is not a folder", permanent: true);
            }
        }
        catch (SftpPathNotFoundException)
        {
            throw new SourceFailedException($"{_root} does not exist on {_settings.Host}", permanent: true);
        }
        catch (SftpPermissionDeniedException)
        {
            throw new SourceFailedException($"{_settings.Username} may not read {_root} on {_settings.Host}", permanent: true);
        }

        _listings = new ListingCache(TimeSpan.FromSeconds(2), ListRemoteAsync);
        Logger.LogInformation("sshfs: serving {Root} on {Host} with up to {Connections} connection(s)", _root, _settings.Host, _settings.Connections);
    }

    public override async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
        {
            return VfsEntry.Directory("");
        }

        return await Listings.StatAsync(path, ct).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        foreach (var entry in await Listings.GetAsync(path, ct).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    public override async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
        {
            throw VfsException.IsADirectory(path);
        }

        var remote = Remote(path);
        var (fileMode, access) = mode switch
        {
            VfsOpenMode.Read => (FileMode.Open, FileAccess.Read),
            VfsOpenMode.ReadWrite => (FileMode.Open, FileAccess.ReadWrite),
            VfsOpenMode.CreateNew => (FileMode.CreateNew, FileAccess.ReadWrite),
            VfsOpenMode.Create => (FileMode.Create, FileAccess.ReadWrite),
            _ => (FileMode.OpenOrCreate, FileAccess.ReadWrite),
        };

        // SFTP opens a folder for reading without complaint and fails on the first read; the (cached) stat says first
        var known = await Listings.StatAsync(path, ct).ConfigureAwait(false);
        if (known is { IsDirectory: true })
        {
            throw VfsException.IsADirectory(path);
        }

        if (known is null && !mode.MayCreate())
        {
            throw VfsException.NotFound(path);
        }

        var stream = await RunAsync(path, async client =>
        {
            try
            {
                return await client.OpenAsync(remote, fileMode, access, ct).ConfigureAwait(false);
            }
            catch (SftpException ex) when (ex.StatusCode == StatusCode.Failure && fileMode == FileMode.CreateNew)
            {
                throw VfsException.AlreadyExists(path);
            }
        }, ct).ConfigureAwait(false);

        if (mode.MayCreate())
        {
            Listings.InvalidateParentOf(path);
        }

        long length;
        try
        {
            length = known is not null && !mode.IsWrite() ? known.Length : stream.Length;
        }
        catch (SshException)
        {
            length = known?.Length ?? 0;
        }

        return new SshfsFile(stream, length, access != FileAccess.Read, () => Listings.InvalidateParentOf(path));
    }

    public override async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var remote = Remote(path);
        await RunAsync(path, async client =>
        {
            if (await client.ExistsAsync(remote, ct).ConfigureAwait(false))
            {
                throw VfsException.AlreadyExists(path);
            }

            try
            {
                await client.CreateDirectoryAsync(remote, ct).ConfigureAwait(false);
            }
            catch (SftpPathNotFoundException)
            {
                throw VfsException.NotFound(VfsPath.Parent(path));
            }

            return true;
        }, ct).ConfigureAwait(false);
        Listings.InvalidateParentOf(path);
    }

    public override async Task DeleteAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
        {
            throw VfsException.AccessDenied(path);
        }

        var remote = Remote(path);
        var wasDirectory = await RunAsync(path, async client =>
        {
            var attributes = await client.GetAttributesAsync(remote, ct).ConfigureAwait(false);
            if (attributes.IsDirectory)
            {
                await foreach (var child in client.ListDirectoryAsync(remote, ct).ConfigureAwait(false))
                {
                    if (child.Name is not ("." or ".."))
                    {
                        throw VfsException.DirectoryNotEmpty(path);
                    }
                }

                await client.DeleteDirectoryAsync(remote, ct).ConfigureAwait(false);
                return true;
            }

            await client.DeleteFileAsync(remote, ct).ConfigureAwait(false);
            return false;
        }, ct).ConfigureAwait(false);

        Listings.InvalidateParentOf(path);
        if (wasDirectory)
        {
            Listings.InvalidateTree(path);
        }
    }

    public override async Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct)
    {
        fromPath = VfsPath.Normalize(fromPath);
        toPath = VfsPath.Normalize(toPath);
        if (fromPath.Length == 0 || toPath.Length == 0)
        {
            throw VfsException.AccessDenied(fromPath.Length == 0 ? fromPath : toPath);
        }

        var fromRemote = Remote(fromPath);
        var toRemote = Remote(toPath);
        var movedDirectory = await RunAsync(fromPath, async client =>
        {
            var from = await client.GetAttributesAsync(fromRemote, ct).ConfigureAwait(false);
            SftpFileAttributes? to = null;
            try
            {
                to = await client.GetAttributesAsync(toRemote, ct).ConfigureAwait(false);
            }
            catch (SftpPathNotFoundException)
            {
            }

            if (to is not null && fromRemote != toRemote && (!replace || from.IsDirectory || to.IsDirectory))
            {
                throw VfsException.AlreadyExists(toPath);
            }

            if (to is not null && fromRemote != toRemote)
            {
                // posix-rename@openssh.com replaces the target in one step; the plain SFTP rename refuses an existing name
                await Task.Run(() => client.RenameFile(fromRemote, toRemote, isPosix: true), ct).ConfigureAwait(false);
            }
            else
            {
                await client.RenameFileAsync(fromRemote, toRemote, ct).ConfigureAwait(false);
            }

            return from.IsDirectory;
        }, ct).ConfigureAwait(false);

        Listings.InvalidateParentOf(fromPath);
        Listings.InvalidateParentOf(toPath);
        if (movedDirectory)
        {
            Listings.InvalidateTree(fromPath);
            Listings.InvalidateTree(toPath);
        }
    }

    public override async Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (modified is null)
        {
            // SFTP has no creation time to set; a modified time is the one thing it keeps
            return;
        }

        var remote = Remote(path);
        await RunAsync(path, async client =>
        {
            await Task.Run(() => client.SetLastWriteTimeUtc(remote, modified.Value.UtcDateTime), ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
        Listings.InvalidateParentOf(path);
    }

    /// <summary>
    /// "Count files on start": the SDK's <see cref="TreeCount"/> over SFTP listings, four per connection at a time (so 16
    /// with the default pool; SFTP multiplexes them). Symbolic links are counted as entries but not followed (a link to a
    /// parent would never end); a folder that can't be listed contributes nothing.
    /// </summary>
    public async Task CountFilesAsync(CancellationToken ct)
    {
        Counters.Set(0, 0, EnumerationState.InProgress);
        await TreeCount.CountAsync(
            (dir, token) => RunAsync(dir, async client =>
            {
                long files = 0;
                var folders = new List<string>();
                await foreach (var item in client.ListDirectoryAsync(Remote(dir), token).ConfigureAwait(false))
                {
                    if (item.Name is "." or "..")
                    {
                        continue;
                    }

                    if (item.IsDirectory && !item.IsSymbolicLink)
                    {
                        folders.Add(item.Name);
                    }
                    else
                    {
                        files++;
                    }
                }

                return (files, (IReadOnlyList<string>)folders);
            }, token),
            Counters,
            Math.Clamp(_settings.Connections * 4, 4, 32),
            (dir, ex) => Logger.LogDebug("sshfs: count skipped '{Dir}': {Error}", dir, ex.Message),
            ct).ConfigureAwait(false);
    }

    public override async Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct)
    {
        try
        {
            return await RunAsync(path, async client =>
            {
                var status = await client.GetStatusAsync(Remote(VfsPath.Normalize(path)), ct).ConfigureAwait(false);
                var block = status.FileSystemBlockSize != 0 ? status.FileSystemBlockSize : status.BlockSize;
                return block == 0 ? null : new VfsSpace((long)Math.Min(status.TotalBlocks * block, long.MaxValue), (long)Math.Min(status.AvailableBlocks * block, long.MaxValue));
            }, ct).ConfigureAwait(false);
        }
        catch (VfsException)
        {
            return null;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_pool is { } pool)
        {
            _pool = null;
            await pool.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>An entry from what SFTP reports (pure, for tests): dot names are hidden, no write bit at all is read-only.</summary>
    internal static VfsEntry ToEntry(string name, bool isDirectory, long length, DateTime modifiedUtc, bool anyoneCanWrite)
    {
        var attributes = VfsAttributes.None;
        if (name.StartsWith('.'))
        {
            attributes |= VfsAttributes.Hidden;
        }

        if (!anyoneCanWrite)
        {
            attributes |= VfsAttributes.ReadOnly;
        }

        var modified = modifiedUtc.Year > 1970 ? new DateTimeOffset(DateTime.SpecifyKind(modifiedUtc, DateTimeKind.Utc)) : (DateTimeOffset?)null;
        return new VfsEntry(name, isDirectory, isDirectory ? 0 : length, null, modified, attributes);
    }

    /// <summary>The server path of a source path: under the root, or the drive mapping on a Windows server served from "/".</summary>
    private string Remote(string path) => _driveRoot ? RemotePath.JoinDriveRoot(path) : RemotePath.Join(_root, path);

    private ListingCache Listings => _listings ?? throw VfsException.Unavailable($"{_settings.Host} is not connected yet");

    private async IAsyncEnumerable<VfsEntry> ListRemoteAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var remote = Remote(path);
        var entries = await RunAsync(path, async client =>
        {
            List<ISftpFile> items;
            try
            {
                items = [];
                await foreach (var item in client.ListDirectoryAsync(remote, ct).ConfigureAwait(false))
                {
                    if (item.Name is not ("." or ".."))
                    {
                        items.Add(item);
                    }
                }
            }
            catch (SftpPathNotFoundException)
            {
                // OpenSSH answers "no such file" for a file as much as for nothing; tell them apart for the caller
                SftpFileAttributes? attributes = null;
                try
                {
                    attributes = await client.GetAttributesAsync(remote, ct).ConfigureAwait(false);
                }
                catch (SftpPathNotFoundException)
                {
                }

                throw attributes is { IsDirectory: false } ? VfsException.NotADirectory(path) : VfsException.NotFound(path);
            }

            // links need a stat each; several at once, the rest is immediate
            using var gate = new SemaphoreSlim(LinkResolveParallelism, LinkResolveParallelism);
            var result = await Task.WhenAll(items.Select(async item =>
            {
                if (!item.IsSymbolicLink)
                {
                    var name = _driveRoot && path.Length == 0 ? RemotePath.DriveEntryName(item.Name) ?? item.Name : item.Name;
                    return ToEntry(name, item.IsDirectory, item.Length, item.LastWriteTimeUtc, item.OwnerCanWrite || item.GroupCanWrite || item.OthersCanWrite);
                }

                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    return await ToEntryAsync(client, item, ct).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);

            return (IReadOnlyList<VfsEntry>)result;
        }, ct).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            yield return entry;
        }
    }

    private static async Task<VfsEntry> ToEntryAsync(SftpClient client, ISftpFile item, CancellationToken ct)
    {
        if (item.IsSymbolicLink)
        {
            try
            {
                var target = await client.GetAttributesAsync(item.FullName, ct).ConfigureAwait(false);
                return ToEntry(item.Name, target.IsDirectory, target.Size, target.LastWriteTimeUtc, target.OwnerCanWrite || target.GroupCanWrite || target.OthersCanWrite);
            }
            catch (SshException)
            {
                // dangling: there is a name but nothing behind it
                return ToEntry(item.Name, false, 0, item.LastWriteTimeUtc, anyoneCanWrite: true);
            }
        }

        return ToEntry(item.Name, item.IsDirectory, item.Length, item.LastWriteTimeUtc, item.OwnerCanWrite || item.GroupCanWrite || item.OthersCanWrite);
    }

    /// <summary>Runs an SFTP operation on a pooled client, once more on a fresh connection when the first one had died.</summary>
    private async Task<T> RunAsync<T>(string path, Func<SftpClient, Task<T>> operation, CancellationToken ct)
    {
        var pool = _pool ?? throw VfsException.Unavailable($"{_settings.Host} is not connected");
        for (var attempt = 0; ; attempt++)
        {
            SftpClient client;
            try
            {
                client = await pool.GetAsync(ct).ConfigureAwait(false);
            }
            catch (SourceFailedException ex)
            {
                MarkDegraded(ex.Message);
                throw VfsException.Unavailable($"{_settings.Host}: {ex.Message}");
            }
            catch (ObjectDisposedException)
            {
                throw VfsException.Unavailable($"{_settings.Host} is not connected");
            }

            try
            {
                var result = await operation(client).ConfigureAwait(false);
                MarkHealthy();
                return result;
            }
            catch (VfsException)
            {
                MarkHealthy();
                throw;
            }
            catch (SftpPathNotFoundException)
            {
                MarkHealthy();
                throw VfsException.NotFound(path);
            }
            catch (SftpPermissionDeniedException)
            {
                MarkHealthy();
                throw VfsException.AccessDenied(path);
            }
            catch (SftpException ex)
            {
                MarkHealthy();
                throw Translate(ex, path);
            }
            catch (Exception ex) when (IsConnectionLoss(ex, client))
            {
                pool.MarkBroken(client);
                if (attempt == 0 && !ct.IsCancellationRequested)
                {
                    Logger.LogWarning("sshfs: the connection to {Host} dropped ({Error}); reconnecting", _settings.Host, ex.Message);
                    continue;
                }

                MarkDegraded(ex.Message);
                throw VfsException.Unavailable($"{_settings.Host} is not reachable: {ex.Message}");
            }
            catch (SshException ex)
            {
                throw new VfsException(VfsError.IoError, $"{path}: {ex.Message}", ex);
            }
        }
    }

    private static bool IsConnectionLoss(Exception ex, SftpClient client)
        => ex is SshConnectionException or SocketException or SshOperationTimeoutException or ObjectDisposedException
            || (ex is IOException && ex is not FileNotFoundException and not DirectoryNotFoundException)
            || (ex is SshException && !client.IsConnected)
            || (ex is InvalidOperationException && !client.IsConnected);

    private static VfsException Translate(SftpException ex, string path) => ex.StatusCode switch
    {
        StatusCode.NoSuchFile => VfsException.NotFound(path),
        StatusCode.PermissionDenied => VfsException.AccessDenied(path),
        StatusCode.OperationUnsupported => VfsException.NotSupported("this operation"),
        StatusCode.NoConnection or StatusCode.ConnectionLost => VfsException.Unavailable(ex.Message),
        _ => new VfsException(VfsError.IoError, $"{path}: {ex.Message}", ex),
    };

    private void MarkDegraded(string reason)
    {
        if (!_degraded && IsInitialised)
        {
            _degraded = true;
            Context.ReportHealth(ComponentHealth.Degraded, $"{_settings.Host}: {reason}");
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
}

/// <summary>
/// An open SFTP file: one <see cref="SftpFileStream"/>, positioned per call under a lock (the handle is
/// positionless to callers, SFTP reads carry offsets anyway). The length is what the listing said at open, kept
/// up to date by this handle's own writes.
/// </summary>
internal sealed class SshfsFile(SftpFileStream stream, long length, bool writable, Action onClose) : IVfsFile
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private long _length = length;
    private bool _disposed;

    public long Length => Volatile.Read(ref _length);

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (buffer.Length == 0)
        {
            return 0;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (stream.Position != offset)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }

            return await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshException or SocketException or IOException)
        {
            throw Translate(ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (!writable)
        {
            throw VfsException.NotSupported("writing to a file opened for reading");
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (stream.Position != offset)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }

            await stream.WriteAsync(data, ct).ConfigureAwait(false);
            if (offset + data.Length > _length)
            {
                Volatile.Write(ref _length, offset + data.Length);
            }
        }
        catch (Exception ex) when (ex is SshException or SocketException or IOException)
        {
            throw Translate(ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask SetLengthAsync(long length, CancellationToken ct)
    {
        if (!writable)
        {
            throw VfsException.NotSupported("writing to a file opened for reading");
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            stream.SetLength(length);
            Volatile.Write(ref _length, length);
        }
        catch (Exception ex) when (ex is SshException or SocketException or IOException)
        {
            throw Translate(ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask FlushAsync(CancellationToken ct)
    {
        if (!writable)
        {
            return;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SshException or SocketException or IOException)
        {
            throw Translate(ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SshException or SocketException or IOException or ObjectDisposedException)
            {
            }
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }

        if (writable)
        {
            onClose();
        }
    }

    private static VfsException Translate(Exception ex) => ex switch
    {
        SftpPermissionDeniedException => new VfsException(VfsError.AccessDenied, ex.Message, ex),
        SftpPathNotFoundException => new VfsException(VfsError.NotFound, ex.Message, ex),
        SftpException sftp => new VfsException(VfsError.IoError, sftp.Message, ex),
        _ => new VfsException(VfsError.Unavailable, $"the connection failed: {ex.Message}", ex),
    };
}
