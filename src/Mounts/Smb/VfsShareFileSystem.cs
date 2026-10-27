using System.Runtime.CompilerServices;
using AnyFS.Sdk;
using LibSmb;

namespace AnyFS.Mounts.Smb;

/// <summary>
/// A collection's virtual file system as LibSmb serves it (spec 8.2). Every call counts as a connection in the mount's
/// stats while it runs. A file is read-only when it is read-only itself; not when only its source, collection or mount
/// refuses writes, since the share shows it as FILE_ATTRIBUTE_READONLY, which every copy of the file would keep: those
/// writes are refused when tried. An open file reads through a <see cref="SequentialReadAhead"/>: clients read in
/// pieces of 64 KiB, and on a source where a read is a round trip that caps a copy far below the link.
/// </summary>
internal sealed class VfsShareFileSystem(IVirtualFileSystem fs, IMountStatsSink stats) : ISmbFileSystem
{
    /// <summary>Names are matched as they are spelt: the sources decide whether another case finds an entry.</summary>
    public bool CaseInsensitive => false;

    public ValueTask<SmbEntry?> StatAsync(string path, CancellationToken cancellationToken)
        => Counted(async () => await fs.StatAsync(path, cancellationToken).ConfigureAwait(false) is { } entry ? EntryOf(entry) : null);

    public async IAsyncEnumerable<SmbEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        stats.ConnectionOpened();
        try
        {
            IAsyncEnumerator<VfsEntry> entries;
            try
            {
                entries = fs.ListAsync(path, cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (VfsException ex)
            {
                throw Translate(ex);
            }

            await using (entries.ConfigureAwait(false))
            {
                while (true)
                {
                    try
                    {
                        if (!await entries.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (VfsException ex)
                    {
                        throw Translate(ex);
                    }

                    yield return EntryOf(entries.Current);
                }
            }
        }
        finally
        {
            stats.ConnectionClosed();
        }
    }

    public ValueTask<ISmbFile> OpenAsync(string path, SmbOpenMode mode, CancellationToken cancellationToken)
        => Counted(async () =>
        {
            var vfsMode = (mode & (SmbOpenMode.Create | SmbOpenMode.Exclusive | SmbOpenMode.Truncate)) switch
            {
                var m when (m & SmbOpenMode.Create) != 0 && (m & SmbOpenMode.Exclusive) != 0 => VfsOpenMode.CreateNew,
                var m when (m & SmbOpenMode.Create) != 0 && (m & SmbOpenMode.Truncate) != 0 => VfsOpenMode.Create,
                var m when (m & SmbOpenMode.Create) != 0 => VfsOpenMode.OpenOrCreate,
                _ when (mode & (SmbOpenMode.Write | SmbOpenMode.Truncate)) != 0 => VfsOpenMode.ReadWrite,
                _ => VfsOpenMode.Read,
            };
            var file = await fs.OpenAsync(path, vfsMode, cancellationToken).ConfigureAwait(false);
            if ((mode & SmbOpenMode.Truncate) != 0 && vfsMode == VfsOpenMode.ReadWrite)
            {
                try
                {
                    await file.SetLengthAsync(0, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            return (ISmbFile)new OpenVfsFile(file, stats);
        });

    public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        => Counted(async () =>
        {
            await fs.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
            return true;
        }).Done();

    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken)
        => Counted(async () =>
        {
            await fs.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
            return true;
        }).Done();

    public ValueTask RenameAsync(string fromPath, string toPath, bool replace, CancellationToken cancellationToken)
        => Counted(async () =>
        {
            await fs.RenameAsync(fromPath, toPath, replace, cancellationToken).ConfigureAwait(false);
            return true;
        }).Done();

    /// <summary>The modification time alone, as before: not every source can set a creation time.</summary>
    public ValueTask SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken cancellationToken)
        => modified is null
            ? ValueTask.CompletedTask
            : Counted(async () =>
            {
                await fs.SetTimesAsync(path, null, modified, cancellationToken).ConfigureAwait(false);
                return true;
            }).Done();

    public ValueTask<SmbSpace?> GetSpaceAsync(CancellationToken cancellationToken)
        => Counted(async () => await fs.GetSpaceAsync("", cancellationToken).ConfigureAwait(false) is { } space ? new SmbSpace(space.TotalBytes, space.FreeBytes) : (SmbSpace?)null);

    /// <summary>The share's view of an entry: a file read-only itself carries the flag; directories never do.</summary>
    internal static SmbEntry EntryOf(VfsEntry entry)
        => new(entry.Name, entry.IsDirectory, entry.IsDirectory ? 0 : entry.Length, entry.Modified ?? DateTimeOffset.UnixEpoch, !entry.IsDirectory && entry.Attributes.HasFlag(VfsAttributes.ReadOnly), entry.Created);

    /// <summary>A VFS failure as the share's: the status the client gets follows from it.</summary>
    internal static SmbFileSystemException Translate(VfsException ex) => new(ex.Error switch
    {
        VfsError.NotFound => SmbFileSystemError.NotFound,
        VfsError.AlreadyExists => SmbFileSystemError.AlreadyExists,
        VfsError.NotADirectory => SmbFileSystemError.NotADirectory,
        VfsError.IsADirectory => SmbFileSystemError.IsADirectory,
        VfsError.DirectoryNotEmpty => SmbFileSystemError.DirectoryNotEmpty,
        VfsError.ReadOnly => SmbFileSystemError.ReadOnly,
        VfsError.AccessDenied => SmbFileSystemError.AccessDenied,
        VfsError.InvalidPath => SmbFileSystemError.InvalidName,
        VfsError.NotSupported => SmbFileSystemError.NotSupported,
        _ => SmbFileSystemError.Failed,
    }, ex.Message, ex);

    private async ValueTask<T> Counted<T>(Func<Task<T>> call)
    {
        stats.ConnectionOpened();
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            throw Translate(ex);
        }
        finally
        {
            stats.ConnectionClosed();
        }
    }

    /// <summary>An open VFS file as an open of the share; its reads through the read-ahead, which a write or resize forgets.</summary>
    private sealed class OpenVfsFile(IVfsFile file, IMountStatsSink stats) : ISmbFile
    {
        private readonly SequentialReadAhead _readAhead = new();

        public long Length => file.Length;

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            stats.ConnectionOpened();
            try
            {
                return await _readAhead.ReadAsync(file, offset, buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (VfsException ex)
            {
                // a handle that failed a read (a download that broke off) may fail every read after it: start afresh
                _readAhead.Forget();
                throw Translate(ex);
            }
            finally
            {
                stats.ConnectionClosed();
            }
        }

        public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            _readAhead.Forget();
            stats.ConnectionOpened();
            try
            {
                await file.WriteAsync(offset, data, cancellationToken).ConfigureAwait(false);
            }
            catch (VfsException ex)
            {
                throw Translate(ex);
            }
            finally
            {
                stats.ConnectionClosed();
            }
        }

        public async ValueTask SetLengthAsync(long length, CancellationToken cancellationToken)
        {
            _readAhead.Forget();
            try
            {
                await file.SetLengthAsync(length, cancellationToken).ConfigureAwait(false);
            }
            catch (VfsException ex)
            {
                throw Translate(ex);
            }
        }

        public async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            try
            {
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (VfsException ex)
            {
                throw Translate(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _readAhead.Dispose();
            try
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
            catch (VfsException ex)
            {
                throw Translate(ex);
            }
        }
    }
}

internal static class ValueTaskExtensions
{
    /// <summary>A ValueTask of a result as a plain one.</summary>
    public static async ValueTask Done<T>(this ValueTask<T> task) => await task.ConfigureAwait(false);
}
