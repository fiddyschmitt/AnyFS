using System.Collections.Concurrent;
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
/// A dropped connection is retried once on a fresh one, and the source turns Degraded until something succeeds; a file
/// open on it is not carried over, and fails until it is closed and opened again (<see cref="CutOff"/>); while
/// the server cannot be reached the pool refuses its callers at once, but for one every ten seconds who connects for real.
/// "Count files on start" lists many folders at once over the pool instead of the host's one-at-a-time walk. Each call to
/// the server is told to <see cref="ISourceContext.UpstreamWatcher"/>, an open file as one from its open to its close.
/// </summary>
public sealed class SshfsSource : SourceBase, ICountable
{
    private readonly SshfsSettings _settings;
    private SftpPool? _pool;
    private ListingCache? _listings;
    private string _root = "/";
    private bool _driveRoot;

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
            var attributes = await AttributesAsync(client, _root, ct).ConfigureAwait(false);
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

        return await KnownAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>From how many entries a folder's listing is long (<see cref="KnownAsync(string, CancellationToken)"/>); a test's to lower.</summary>
    internal int LongListing { get; set; } = 1000;

    // the folders whose last listing was long, and how long
    private readonly ConcurrentDictionary<string, int> _longListings = new(StringComparer.Ordinal);

    /// <summary>
    /// What is at <paramref name="path"/>, for a stat or an open (<see cref="KnownAsync(ListingCache, string, Func{string, bool}, Func{Task{VfsEntry}}, CancellationToken)"/>):
    /// in a folder whose last listing was long and is no longer held, the server's answer about the one path.
    /// </summary>
    private Task<VfsEntry?> KnownAsync(string path, CancellationToken ct)
        => KnownAsync(Listings, path, folder => _longListings.ContainsKey(folder) && !(_driveRoot && folder.Length == 0), () =>
        {
            var remote = Remote(path);
            return RunAsync(path, async client =>
            {
                try
                {
                    // (as a listing says it: a link by what it points at)
                    var found = await AttributesAsync(client, remote, ct).ConfigureAwait(false);
                    return (VfsEntry?)ToEntry(VfsPath.Name(path), found.IsDirectory, found.Size, found.LastWriteTimeUtc, found.OwnerCanWrite || found.GroupCanWrite || found.OthersCanWrite);
                }
                catch (SftpPathNotFoundException)
                {
                    return null;
                }
            }, ct);
        }, ct);

    /// <summary>
    /// What is at <paramref name="path"/>, for a stat or an open. From the listing held of its folder; where none is
    /// held, from a listing made now, which then answers for everything in the folder for its two seconds. That is
    /// the cheapest way through a folder of a few hundred files, and it was the only way: in a folder of 20,000 on
    /// the lab's server each stat and each open that came after the two seconds listed the 20,000 again, 330 to 720 ms
    /// each where the server's answer about one path takes 5 (the review of 21-Nov-2026, P12). So where the folder's
    /// last listing was long (<paramref name="isLong"/>) and none is held, the server is asked about the one path
    /// (<paramref name="one"/>); whoever lists the folder gets a listing as before, and that listing answers while held.
    /// </summary>
    internal static async Task<VfsEntry?> KnownAsync(ListingCache listings, string path, Func<string, bool> isLong, Func<Task<VfsEntry?>> one, CancellationToken ct)
    {
        var folder = VfsPath.Parent(path);
        return isLong(folder) && listings.Held(folder) is null
            ? await one().ConfigureAwait(false)
            : await listings.StatAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>Notes how long a folder's listing came out, for the stats and opens after it.</summary>
    internal static void Listed(ConcurrentDictionary<string, int> longListings, string folder, int entries, int longFrom)
    {
        if (entries >= longFrom)
        {
            longListings[folder] = entries;
        }
        else
        {
            longListings.TryRemove(folder, out _);
        }
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
        var known = await KnownAsync(path, ct).ConfigureAwait(false);
        if (known is { IsDirectory: true })
        {
            throw VfsException.IsADirectory(path);
        }

        if (known is null && !mode.MayCreate())
        {
            throw VfsException.NotFound(path);
        }

        // the request stays open with the file, its bytes going by on it; each try at the open is one
        IWatchedRequest? asked = null;
        var stream = await RunAsync(path, async client =>
        {
            asked = Context.UpstreamWatcher?.Asked("SFTP open", remote);
            try
            {
                return await client.OpenAsync(remote, fileMode, access, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                asked?.Failed(ex, ct);
                asked?.Done();
                if (ex is SftpException { StatusCode: StatusCode.Failure } && fileMode == FileMode.CreateNew)
                {
                    throw VfsException.AlreadyExists(path);
                }

                throw;
            }
        }, ct).ConfigureAwait(false);

        if (mode.MayCreate())
        {
            Made(Listings, path, mode, known);
        }

        long length;
        try
        {
            length = known is not null && !mode.IsWrite() ? known.Length : stream.Length;
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException)
        {
            // (a session gone since the open is SSH.NET's InvalidOperationException: the file says so at its first call)
            length = known?.Length ?? 0;
        }

        if (access == FileAccess.Read)
        {
            return new SshfsFile(stream, path, length, writable: false, () => Task.CompletedTask, asked, CutOff);
        }

        SshfsFile? file = null;
        file = new SshfsFile(stream, path, length, writable: true, () =>
        {
            _writing.Remove(file!);
            return KeepAsync(Listings, path, () => EntryAsync(path));
        }, asked, CutOff);
        _writing.Add(file, path);
        return file;
    }

    public override async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var remote = Remote(path);
        await RunAsync(path, async client =>
        {
            if (await Context.UpstreamWatcher.AskAsync("SFTP stat", remote, () => client.ExistsAsync(remote, ct), ct).ConfigureAwait(false))
            {
                throw VfsException.AlreadyExists(path);
            }

            try
            {
                await Context.UpstreamWatcher.AskAsync("SFTP mkdir", remote, () => client.CreateDirectoryAsync(remote, ct), ct).ConfigureAwait(false);
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
            var attributes = await AttributesAsync(client, remote, ct).ConfigureAwait(false);
            if (attributes.IsDirectory)
            {
                await foreach (var child in ListingAsync(client, remote, ct).ConfigureAwait(false))
                {
                    if (child.Name is not ("." or ".."))
                    {
                        throw VfsException.DirectoryNotEmpty(path);
                    }
                }

                await Context.UpstreamWatcher.AskAsync("SFTP rmdir", remote, () => client.DeleteDirectoryAsync(remote, ct), ct).ConfigureAwait(false);
                return true;
            }

            await Context.UpstreamWatcher.AskAsync("SFTP delete", remote, () => client.DeleteFileAsync(remote, ct), ct).ConfigureAwait(false);
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
            var from = await AttributesAsync(client, fromRemote, ct).ConfigureAwait(false);
            SftpFileAttributes? to = null;
            try
            {
                to = await AttributesAsync(client, toRemote, ct).ConfigureAwait(false);
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
                await Context.UpstreamWatcher.AskAsync("SFTP rename", fromRemote, () => Task.Run(() => client.RenameFile(fromRemote, toRemote, isPosix: true), ct), ct).ConfigureAwait(false);
            }
            else
            {
                await Context.UpstreamWatcher.AskAsync("SFTP rename", fromRemote, () => client.RenameFileAsync(fromRemote, toRemote, ct), ct).ConfigureAwait(false);
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

        // what the file's writer has written and its handle still keeps goes to the server first (OpenWrites)
        await _writing.SendAsync(path, ct).ConfigureAwait(false);
        var remote = Remote(path);
        await RunAsync(path, async client =>
        {
            await Context.UpstreamWatcher.AskAsync("SFTP set time", remote, () => Task.Run(() => client.SetLastWriteTimeUtc(remote, modified.Value.UtcDateTime), ct), ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
        await KeepAsync(Listings, path, () => EntryAsync(path)).ConfigureAwait(false);
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
                await foreach (var item in ListingAsync(client, Remote(dir), token).ConfigureAwait(false))
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
            Math.Max(4, _settings.Connections * 4),
            (dir, ex) => Logger.LogDebug("sshfs: count skipped '{Dir}': {Error}", dir, ex.Message),
            ct).ConfigureAwait(false);
    }

    public override async Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct)
    {
        try
        {
            return await RunAsync(path, async client =>
            {
                var remote = Remote(VfsPath.Normalize(path));
                var status = await Context.UpstreamWatcher.AskAsync("SFTP statvfs", remote, () => client.GetStatusAsync(remote, ct), ct).ConfigureAwait(false);
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

    /// <summary>
    /// What an open that may make a file leaves in the listing held of its folder: the file as it is from then on, new or
    /// cut to nothing, made now. The listing used to be forgotten here, and at each close and each time set, so that
    /// copying files into a folder listed the folder again for each of them, with every file copied so far (the review of
    /// 21-Nov-2026, N17). What the server says of the file takes this entry's place as the file closes
    /// (<see cref="KeepAsync"/>).
    /// </summary>
    internal static void Made(ListingCache listings, string path, VfsOpenMode mode, VfsEntry? known)
    {
        if (mode == VfsOpenMode.OpenOrCreate)
        {
            // opened as it was where the listing has it. Where it has not, the open made the file, or found one made
            // since the folder was listed, and which is not known here: the folder is listed again when next asked
            if (known is null)
            {
                listings.InvalidateParentOf(path);
            }

            return;
        }

        listings.Update(path, ToEntry(VfsPath.Name(path), isDirectory: false, 0, DateTime.UtcNow, anyoneCanWrite: true));
    }

    /// <summary>
    /// Puts what the server now says of <paramref name="path"/> into the listing held of its folder
    /// (<paramref name="ask"/>: null where it has nothing there), as a file opened for writing closes and as a time is
    /// set, where the listing was forgotten (<see cref="Made"/>). The listing is no newer for it: it is listed afresh
    /// when its two seconds are over, as before. With nothing held of the folder there is nothing to put right and the
    /// server is not asked; where it cannot be asked, the listing is forgotten as it was.
    /// </summary>
    internal static async Task KeepAsync(ListingCache listings, string path, Func<Task<VfsEntry?>> ask)
    {
        if (listings.Held(VfsPath.Parent(path)) is null)
        {
            // and one on its way, which may have been made before the write, is not kept
            listings.InvalidateParentOf(path);
            return;
        }

        VfsEntry? now;
        try
        {
            now = await ask().ConfigureAwait(false);
        }
        catch (VfsException)
        {
            listings.InvalidateParentOf(path);
            return;
        }

        listings.Update(path, now);
    }

    /// <summary>
    /// What the server has at <paramref name="path"/> now, as a listing of its folder would show it; null for nothing.
    /// Asked whatever its caller has given up since: the file is closed, or its time set, and the listing follows.
    /// </summary>
    private Task<VfsEntry?> EntryAsync(string path)
    {
        var remote = Remote(path);
        return RunAsync<VfsEntry?>(path, async client =>
        {
            try
            {
                var attributes = await AttributesAsync(client, remote, CancellationToken.None).ConfigureAwait(false);
                return ToEntry(VfsPath.Name(path), attributes.IsDirectory, attributes.Size, attributes.LastWriteTimeUtc, attributes.OwnerCanWrite || attributes.GroupCanWrite || attributes.OthersCanWrite);
            }
            catch (SftpPathNotFoundException)
            {
                return null;
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// An open file has found its connection gone (<see cref="SshfsFile"/>): the source says so as it does when a call of
    /// its own finds the server out of reach, until a call next reaches it. The file itself stays as it was, failing
    /// until it is closed and opened again: it is one handle on the connection it was opened on, what was written through
    /// it and not yet sent went with that connection, and SFTP has nothing to tell a file opened again by its path from
    /// another put in its place. Its source used to hear nothing of it, the tile Ready throughout (the review of
    /// 21-Nov-2026, N17).
    /// </summary>
    private void CutOff(VfsException lost) => MarkDegraded($"{_settings.Host}: {lost.Message}");

    // the files open for writing: a time set on one of them sends what its handle still keeps first (SetTimesAsync)
    private readonly OpenWrites _writing = new();

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
                await foreach (var item in ListingAsync(client, remote, ct).ConfigureAwait(false))
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
                    attributes = await AttributesAsync(client, remote, ct).ConfigureAwait(false);
                }
                catch (SftpPathNotFoundException)
                {
                }

                throw attributes is { IsDirectory: false } ? VfsException.NotADirectory(path) : VfsException.NotFound(path);
            }

            // links need a stat each; several at once (as many as the count walks, four a connection), the rest is immediate
            var atOnce = Math.Max(4, _settings.Connections * 4);
            using var gate = new SemaphoreSlim(atOnce, atOnce);
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

        Listed(_longListings, path, entries.Count, LongListing);
        foreach (var entry in entries)
        {
            yield return entry;
        }
    }

    /// <summary>A path's attributes from the server.</summary>
    private Task<SftpFileAttributes> AttributesAsync(SftpClient client, string remote, CancellationToken ct)
        => Context.UpstreamWatcher.AskAsync("SFTP stat", remote, () => client.GetAttributesAsync(remote, ct), ct);

    /// <summary>A folder's entries from the server as they come, "." and ".." among them, however far the caller reads: one request to its watcher.</summary>
    private async IAsyncEnumerable<ISftpFile> ListingAsync(SftpClient client, string remote, [EnumeratorCancellation] CancellationToken ct)
    {
        var asked = Context.UpstreamWatcher?.Asked("SFTP list", remote);
        var items = client.ListDirectoryAsync(remote, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await items.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    asked?.Failed(ex, ct);
                    throw;
                }

                if (!more)
                {
                    break;
                }

                if (items.Current.Name is not ("." or ".."))
                {
                    asked?.AddEntries(1);
                }

                yield return items.Current;
            }
        }
        finally
        {
            asked?.Done();
            await items.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<VfsEntry> ToEntryAsync(SftpClient client, ISftpFile item, CancellationToken ct)
    {
        if (item.IsSymbolicLink)
        {
            try
            {
                var target = await AttributesAsync(client, item.FullName, ct).ConfigureAwait(false);
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
                MarkDegraded($"{_settings.Host}: {ex.Message}");
                throw VfsException.Unavailable($"{_settings.Host}: {ex.Message}");
            }
            catch (VfsException ex)
            {
                // refused at once by the pool, which remembers that the server could not be reached: that is this call's
                // answer, on its first try or on the second after a connection dropped, and nothing here waits for another
                MarkDegraded($"{_settings.Host}: {ex.Message}");
                throw;
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

                MarkDegraded($"{_settings.Host}: {ex.Message}");
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

}

/// <summary>
/// The files a source has open for writing, by their paths: so that a time set on one of them sends what its handle
/// still keeps before the time is set. SSH.NET keeps the last of what is written until its buffer fills or the file
/// closes, and a mount sets a copied file's time while the file is still open (WinFsp and SMB, by its path, before
/// their close): the kept bytes then went to the server after the time, at the close, and the server put the file's
/// time to that moment. A file copied in through a mount had the time of its copying and not its own (asked of a real
/// server, 27-Nov-2026: every file, of 5 bytes and of a megabyte alike).
/// </summary>
internal sealed class OpenWrites
{
    private readonly ConcurrentDictionary<SshfsFile, string> _open = new();

    /// <summary>How many files are open for writing.</summary>
    public int Count => _open.Count;

    public void Add(SshfsFile file, string path) => _open[file] = path;

    public void Remove(SshfsFile file) => _open.TryRemove(file, out _);

    /// <summary>
    /// Sends what the files open for writing at <paramref name="path"/> still keep. Where that fails the failure is
    /// thrown as the file's writer would have been told it, and what was kept stays kept for the close to try.
    /// </summary>
    public async Task SendAsync(string path, CancellationToken ct)
    {
        foreach (var (file, at) in _open)
        {
            // in either case: a server that tells names apart by case has two files there, and sending early harms neither
            if (string.Equals(at, path, StringComparison.OrdinalIgnoreCase))
            {
                await file.SendKeptAsync(ct).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// An open SFTP file: one <see cref="SftpFileStream"/>, positioned per call under a lock (the handle is
/// positionless to callers, SFTP reads carry offsets anyway). The length is what the listing said at open, kept
/// up to date by this handle's own writes. The stream is taken as a <see cref="Stream"/>, which is all that is used of
/// it, so that a test can hand over one that fails as a server or a lost connection would.
/// </summary>
/// <param name="path">The file's path in the source, for what a failed close says.</param>
/// <param name="onClose">Waited for as a file opened for writing closes, the handle given up: its source puts the folder's listing right.</param>
/// <param name="onCutOff">Told the failure when something asked through the file fails for want of its connection, once.</param>
internal sealed class SshfsFile(Stream stream, string path, long length, bool writable, Func<Task> onClose, IWatchedRequest? asked = null, Action<VfsException>? onCutOff = null) : IVfsFile
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private long _length = length;
    private bool _disposed;

    // a write was taken through this handle that may not be on the server yet, and nothing since has told its writer so:
    // SSH.NET keeps a write until its buffer fills (about 32 KB with OpenSSH) or the stream is flushed. Under the lock
    private bool _unsent;

    // the source has been told that this file's connection has gone (Told): once, since a file cut off asks the server
    // nothing more, and what it fails with after that says nothing new of the server. Under the lock
    private bool _cutOff;

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

            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            asked?.AddBytes(read);
            return read;
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            asked?.Failed(ex, ct);
            throw Told(Translate(ex));
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

            // a write that fails tells its caller itself; one that is taken may only be kept, which the close answers for
            _unsent = false;
            if (stream.Position != offset)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }

            await stream.WriteAsync(data, ct).ConfigureAwait(false);
            _unsent = true;
            asked?.AddBytes(data.Length);
            if (offset + data.Length > _length)
            {
                Volatile.Write(ref _length, offset + data.Length);
            }
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            asked?.Failed(ex, ct);
            throw Told(Translate(ex));
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
        catch (Exception ex) when (IsFailure(ex))
        {
            throw Told(Translate(ex));
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

            // sent, or its caller told here that it was not
            _unsent = false;
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            throw Told(Translate(ex));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Sends what was written through the file and is still kept here, for its source to set the file's time after
    /// (<see cref="OpenWrites"/>). Nothing for a file closed meanwhile, or with nothing kept. Where it fails, what is
    /// kept stays so: the close tries again, and tells the writer as it would have.
    /// </summary>
    internal async ValueTask SendKeptAsync(CancellationToken ct)
    {
        if (!writable)
        {
            return;
        }

        try
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // closed, and what it kept was sent by its close
            return;
        }

        try
        {
            if (_disposed || !_unsent)
            {
                return;
            }

            await stream.FlushAsync(ct).ConfigureAwait(false);
            _unsent = false;
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            throw Told(Translate(ex));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Closes the file, and fails when what was written through it did not all reach the server: SSH.NET sends what it
    /// still keeps as its stream closes, so a write the server refuses (a full disk, a quota) is refused only then, and
    /// with the session gone its close sends nothing and says nothing. Until 22-Nov-2026 the close took both for success:
    /// a small file saved to a full server was an empty file there, and saved to a mount that flushes only when its
    /// client asks (WinFsp, SMB). What is kept is sent here first, where a failure can be told from the close's own, and
    /// the handle is given up either way before it is thrown. A writer already told by a write or a flush that failed is
    /// not told again: the close after that is the plain one a mount's delete of the half-made file goes on from.
    /// </summary>
    public ValueTask DisposeAsync() => CloseAsync(givenUp: false);

    /// <summary>
    /// The close of a write that was given up, which says nothing of what did not arrive, as none of it was wanted: the
    /// mounts delete such a file next, and a close that threw would leave it there.
    /// </summary>
    public ValueTask AbandonAsync() => CloseAsync(givenUp: true);

    private async ValueTask CloseAsync(bool givenUp)
    {
        Exception? lost = null;
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_unsent && !givenUp)
            {
                try
                {
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SshException or SocketException or IOException or InvalidOperationException)
                {
                    // SSH.NET says "The session is not open." as an InvalidOperationException, and that its client was
                    // disposed (a connection the pool dropped) as an ObjectDisposedException, which is one too
                    lost = ex;
                    asked?.Failed(ex, CancellationToken.None);
                }
            }

            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SshException or SocketException or IOException or ObjectDisposedException)
            {
                asked?.Failed(ex, CancellationToken.None);
            }
        }
        finally
        {
            asked?.Done();
            _lock.Release();
            _lock.Dispose();
        }

        if (writable)
        {
            await onClose().ConfigureAwait(false);
        }

        if (lost is not null)
        {
            throw Told(new VfsException(lost is SftpException ? VfsError.IoError : VfsError.Unavailable, $"what was written to '{path}' did not all reach the server: {lost.Message}", lost));
        }
    }

    /// <summary>
    /// Whether an exception is the server's or the connection's answer to what was asked through the file, to be put as
    /// a <see cref="VfsException"/>. SSH.NET says a session that has gone as an InvalidOperationException ("The session
    /// is not open."), and a client disposed of (a connection the pool dropped) as an ObjectDisposedException, which is
    /// one too. The close alone took those for what they are: from a read, a write, a flush or a length set they went to
    /// the mount as they came, which it answers as a fault of its own (WebDAV a 500, FTP by ending the session) and not
    /// as a server out of reach. A file used after its own close is its caller's mistake, and is told as that.
    /// </summary>
    private bool IsFailure(Exception ex)
        => ex is SshException or SocketException or IOException || (ex is InvalidOperationException && !_disposed);

    /// <summary>A failure as it is, its source told first where it is the connection's (<c>onCutOff</c>, once).</summary>
    private VfsException Told(VfsException failure)
    {
        if (failure.Error == VfsError.Unavailable && !_cutOff)
        {
            _cutOff = true;
            onCutOff?.Invoke(failure);
        }

        return failure;
    }

    private static VfsException Translate(Exception ex) => ex switch
    {
        SftpPermissionDeniedException => new VfsException(VfsError.AccessDenied, ex.Message, ex),
        SftpPathNotFoundException => new VfsException(VfsError.NotFound, ex.Message, ex),
        SftpException sftp => new VfsException(VfsError.IoError, sftp.Message, ex),
        _ => new VfsException(VfsError.Unavailable, $"the connection failed: {ex.Message}", ex),
    };
}
