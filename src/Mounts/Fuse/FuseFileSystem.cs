using System.Buffers;
using System.Runtime.Versioning;
using AnyFS.Sdk;
using FuseDotNet;
using FuseDotNet.Extensions;
using LTRData.Extensions.Native.Memory;
using Microsoft.Extensions.Logging;

namespace AnyFS.Mounts.Fuse;

/// <summary>
/// The libfuse3 callbacks (spec 8.3) over a collection's <see cref="IVirtualFileSystem"/>. Every entry belongs to the
/// mounting user; mode bits say what the three read-only layers allow (with <c>default_permissions</c> the kernel
/// enforces them too). Open handles travel in <see cref="FuseFileInfo.Context"/>. FUSE calls these on its own threads,
/// so each one waits for the asynchronous VFS call.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class FuseFileSystem(IVirtualFileSystem fs, uint uid, uint gid, ILogger logger) : IFuseOperations
{
    private static readonly PosixResult NotEmpty = new(39); // ENOTEMPTY
    private const PosixFileMode DirectoryBase = PosixFileMode.Directory | PosixFileMode.OwnerRead | PosixFileMode.OwnerExecute | PosixFileMode.GroupReadExecute | PosixFileMode.OthersReadExecute;
    private const PosixFileMode FileBase = PosixFileMode.Regular | PosixFileMode.OwnerRead | PosixFileMode.GroupRead | PosixFileMode.OthersRead;

    public void Init(ref FuseConnInfo fuse_conn_info)
        => logger.LogDebug("fuse: protocol {Major}.{Minor}, capable {Capable}, want {Want}", fuse_conn_info.proto_major, fuse_conn_info.proto_minor, fuse_conn_info.capable, fuse_conn_info.want);

    public PosixResult GetAttr(ReadOnlyNativeMemory<byte> fileNamePtr, out FuseFileStat stat, ref FuseFileInfo fileInfo)
    {
        stat = default;
        var path = PathOf(fileNamePtr);
        var entry = Stat(path, out var result);
        if (entry is null)
        {
            return result;
        }

        stat = StatFor(path, entry);
        return PosixResult.Success;
    }

    public PosixResult ReadDir(ReadOnlyNativeMemory<byte> fileNamePtr, out IEnumerable<FuseDirEntry> entries, ref FuseFileInfo fileInfo, long offset, FuseReadDirFlags flags)
    {
        var path = PathOf(fileNamePtr);
        var list = new List<FuseDirEntry>();
        entries = list;
        var plus = flags.HasFlag(FuseReadDirFlags.ReaddirPlus) ? FuseFillDirFlags.FillDirPlus : default;
        return Guard(() =>
        {
            var self = Wait(fs.StatAsync(path, CancellationToken.None));
            if (self is null)
            {
                return PosixResult.ENOENT;
            }

            if (!self.IsDirectory)
            {
                return PosixResult.ENOTDIR;
            }

            list.Add(new FuseDirEntry(".", 0, plus, StatFor(path, self)));
            var parentPath = path.Length == 0 ? "" : VfsPath.Parent(path);
            var parent = path.Length == 0 ? self : Wait(fs.StatAsync(parentPath, CancellationToken.None)) ?? self;
            list.Add(new FuseDirEntry("..", 0, plus, StatFor(parentPath, parent)));
            foreach (var child in Wait(ListAsync(path)))
            {
                list.Add(new FuseDirEntry(child.Name, 0, plus, StatFor(VfsPath.Join(path, child.Name), child)));
            }

            return PosixResult.Success;
        });
    }

    public PosixResult OpenDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        var entry = Stat(PathOf(fileNamePtr), out var result);
        return entry is null ? result : entry.IsDirectory ? PosixResult.Success : PosixResult.ENOTDIR;
    }

    public PosixResult ReleaseDir(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo) => PosixResult.Success;

    public PosixResult FSyncDir(ReadOnlyNativeMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo) => PosixResult.Success;

    public PosixResult Access(ReadOnlyNativeMemory<byte> fileNamePtr, PosixAccessMode mask)
    {
        var path = PathOf(fileNamePtr);
        var entry = Stat(path, out var result);
        if (entry is null)
        {
            return result;
        }

        return mask.HasFlag(PosixAccessMode.Write) && !fs.CanWrite(path) ? WriteRefusal() : PosixResult.Success;
    }

    public PosixResult Open(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        var path = PathOf(fileNamePtr);
        var entry = Stat(path, out var result);
        if (entry is null)
        {
            return result;
        }

        if (entry.IsDirectory)
        {
            return PosixResult.EISDIR;
        }

        var flags = fileInfo.flags;
        var write = WantsWrite(flags);
        if (write && !fs.CanWrite(path))
        {
            return WriteRefusal();
        }

        IVfsFile? file = null;
        var opened = Guard(() =>
        {
            var mode = write ? ((flags & PosixOpenFlags.Truncate) != 0 ? VfsOpenMode.Create : VfsOpenMode.ReadWrite) : VfsOpenMode.Read;
            file = Wait(fs.OpenAsync(path, mode, CancellationToken.None));
            return PosixResult.Success;
        });
        if (opened != PosixResult.Success)
        {
            return opened;
        }

        fileInfo.Context = new Handle(file!);
        return PosixResult.Success;
    }

    public PosixResult Create(ReadOnlyNativeMemory<byte> fileNamePtr, int mode, ref FuseFileInfo fileInfo)
    {
        var path = PathOf(fileNamePtr);
        if (path.Length == 0 || !fs.CanWrite(VfsPath.Parent(path)))
        {
            return WriteRefusal();
        }

        IVfsFile? file = null;
        var result = Guard(() =>
        {
            file = Wait(fs.OpenAsync(path, VfsOpenMode.Create, CancellationToken.None));
            return PosixResult.Success;
        });
        if (result != PosixResult.Success)
        {
            return result;
        }

        fileInfo.Context = new Handle(file!);
        return PosixResult.Success;
    }

    /// <summary>
    /// The kernel asks for a file in pieces (128 KiB with its default read-ahead) on several threads at once, and on a
    /// source where each read is a round trip that is slow: 8 MiB/s from SharePoint on the lab's VM (18-Oct-2026). So
    /// a handle's reads run one at a time through <see cref="SequentialReadAhead"/>, which fetches 1 MiB when a read
    /// follows on from the last; the same read-ahead as the WinFsp mount's.
    /// </summary>
    public PosixResult Read(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> buffer, long position, out int readLength, ref FuseFileInfo fileInfo)
    {
        readLength = 0;
        var handle = fileInfo.Context as Handle;
        var length = buffer.Length;
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var n = 0;
            var result = Guard(() =>
            {
                if (handle is null)
                {
                    using var temp = new Lease(Wait(fs.OpenAsync(PathOf(fileNamePtr), VfsOpenMode.Read, CancellationToken.None)));
                    n = Wait(temp.File.ReadFullyAsync(position, rented.AsMemory(0, length), CancellationToken.None).AsTask());
                }
                else
                {
                    lock (handle.Gate)
                    {
                        n = Wait(handle.ReadAhead.ReadAsync(handle.File, position, rented.AsMemory(0, length), CancellationToken.None).AsTask());
                    }
                }

                return PosixResult.Success;
            });
            if (result != PosixResult.Success)
            {
                return result;
            }

            rented.AsSpan(0, n).CopyTo(buffer.Span);
            readLength = n;
            return PosixResult.Success;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public PosixResult Write(ReadOnlyNativeMemory<byte> fileNamePtr, ReadOnlyNativeMemory<byte> buffer, long position, out int writtenLength, ref FuseFileInfo fileInfo)
    {
        writtenLength = 0;
        if (fileInfo.Context is not Handle handle)
        {
            return PosixResult.EBADF;
        }

        var file = handle.File;
        var length = buffer.Length;
        var append = (fileInfo.flags & PosixOpenFlags.Append) != 0;
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            buffer.Span.CopyTo(rented);
            var result = Guard(() =>
            {
                lock (handle.Gate)
                {
                    try
                    {
                        var at = append ? file.Length : position;
                        Wait(file.WriteAsync(at, rented.AsMemory(0, length), CancellationToken.None).AsTask());
                    }
                    finally
                    {
                        handle.ReadAhead.Forget();
                    }
                }

                return PosixResult.Success;
            });
            if (result != PosixResult.Success)
            {
                return result;
            }

            writtenLength = length;
            return PosixResult.Success;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public PosixResult Flush(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
        => fileInfo.Context is Handle handle ? Guard(() => { Wait(handle.File.FlushAsync(CancellationToken.None).AsTask()); return PosixResult.Success; }) : PosixResult.Success;

    public PosixResult FSync(ReadOnlyNativeMemory<byte> fileNamePtr, bool datasync, ref FuseFileInfo fileInfo) => Flush(fileNamePtr, ref fileInfo);

    public PosixResult Release(ReadOnlyNativeMemory<byte> fileNamePtr, ref FuseFileInfo fileInfo)
    {
        if (fileInfo.Context is Handle handle)
        {
            fileInfo.Context = null!;
            lock (handle.Gate)
            {
                handle.ReadAhead.Dispose();
            }

            return Guard(() => { Wait(handle.File.DisposeAsync().AsTask()); return PosixResult.Success; });
        }

        return PosixResult.Success;
    }

    public PosixResult Truncate(ReadOnlyNativeMemory<byte> fileNamePtr, long size)
    {
        var path = PathOf(fileNamePtr);
        return Guard(() =>
        {
            using var lease = new Lease(Wait(fs.OpenAsync(path, VfsOpenMode.ReadWrite, CancellationToken.None)));
            Wait(lease.File.SetLengthAsync(size, CancellationToken.None).AsTask());
            Wait(lease.File.FlushAsync(CancellationToken.None).AsTask());
            return PosixResult.Success;
        });
    }

    public PosixResult MkDir(ReadOnlyNativeMemory<byte> fileNamePtr, PosixFileMode mode)
    {
        var path = PathOf(fileNamePtr);
        return Guard(() => { Wait(fs.CreateDirectoryAsync(path, CancellationToken.None)); return PosixResult.Success; });
    }

    public PosixResult RmDir(ReadOnlyNativeMemory<byte> fileNamePtr) => Unlink(fileNamePtr);

    public PosixResult Unlink(ReadOnlyNativeMemory<byte> fileNamePtr)
    {
        var path = PathOf(fileNamePtr);
        return Guard(() => { Wait(fs.DeleteAsync(path, CancellationToken.None)); return PosixResult.Success; });
    }

    public PosixResult Rename(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to)
    {
        var fromPath = PathOf(from);
        var toPath = PathOf(to);
        return Guard(() => { Wait(fs.RenameAsync(fromPath, toPath, replace: true, CancellationToken.None)); return PosixResult.Success; });
    }

    public PosixResult UTime(ReadOnlyNativeMemory<byte> fileNamePtr, TimeSpec atime, TimeSpec mtime, ref FuseFileInfo fileInfo)
    {
        var path = PathOf(fileNamePtr);
        DateTimeOffset? modified = mtime.IsOmit ? null : mtime.IsPseudoNow ? DateTimeOffset.UtcNow : mtime.ToDateTime();
        if (modified is null)
        {
            return PosixResult.Success;
        }

        return Guard(() => { Wait(fs.SetTimesAsync(path, null, modified, CancellationToken.None)); return PosixResult.Success; });
    }

    public PosixResult StatFs(ReadOnlyNativeMemory<byte> fileNamePtr, out FuseVfsStat statvfs)
    {
        statvfs = default;
        VfsSpace? space = null;
        try
        {
            space = Wait(fs.GetSpaceAsync("", CancellationToken.None));
        }
        catch (Exception ex)
        {
            // no space known: the defaults below; an exception leaving a FUSE callback would end the process
            logger.LogDebug(ex, "fuse: statfs failed");
        }

        const ulong block = 4096;
        var total = (ulong)(space?.TotalBytes ?? 1L << 40);
        var free = (ulong)(space?.FreeBytes ?? 1L << 39);
        statvfs.f_bsize = block;
        statvfs.f_frsize = block;
        statvfs.f_blocks = total / block;
        statvfs.f_bfree = free / block;
        statvfs.f_bavail = free / block;
        statvfs.f_files = 1 << 20;
        statvfs.f_ffree = 1 << 19;
        statvfs.f_favail = 1 << 19;
        statvfs.f_namemax = 255;
        return PosixResult.Success;
    }

    // accepted and ignored: the mode bits come from the read-only layers and the owner is the mounting user
    public PosixResult ChMod(NativeMemory<byte> fileNamePtr, PosixFileMode mode) => PosixResult.Success;

    public PosixResult ChOwn(NativeMemory<byte> fileNamePtr, int uid, int gid) => PosixResult.Success;

    public PosixResult ReadLink(ReadOnlyNativeMemory<byte> fileNamePtr, NativeMemory<byte> target) => PosixResult.ENOSYS;

    public PosixResult SymLink(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to) => PosixResult.ENOSYS;

    public PosixResult Link(ReadOnlyNativeMemory<byte> from, ReadOnlyNativeMemory<byte> to) => PosixResult.ENOSYS;

    public PosixResult IoCtl(ReadOnlyNativeMemory<byte> fileNamePtr, int cmd, nint arg, ref FuseFileInfo fileInfo, FuseIoctlFlags flags, nint data) => PosixResult.ENOSYS;

    public PosixResult FAllocate(NativeMemory<byte> fileNamePtr, FuseAllocateMode mode, long offset, long length, ref FuseFileInfo fileInfo) => PosixResult.ENOSYS;

    public void Dispose()
    {
    }

    // ----- helpers -----

    private FuseFileStat StatFor(string path, VfsEntry entry)
    {
        var writable = fs.CanWrite(path) && !entry.Attributes.HasFlag(VfsAttributes.ReadOnly);
        var mode = entry.IsDirectory ? DirectoryBase : FileBase;
        if (writable)
        {
            mode |= PosixFileMode.OwnerWrite;
        }

        var modified = new TimeSpec(entry.Modified ?? entry.Created ?? DateTimeOffset.UnixEpoch);
        var created = new TimeSpec(entry.Created ?? entry.Modified ?? DateTimeOffset.UnixEpoch);
        var stat = new FuseFileStat
        {
            st_mode = mode,
            st_nlink = entry.IsDirectory ? 2 : 1,
            st_size = entry.IsDirectory ? 4096 : entry.Length,
            st_uid = uid,
            st_gid = gid,
            st_blksize = 4096,
            st_blocks = entry.IsDirectory ? 8 : (entry.Length + 511) / 512,
            st_atim = modified,
            st_mtim = modified,
            st_ctim = modified,
            st_birthtim = created,
        };
        return stat;
    }

    private VfsEntry? Stat(string path, out PosixResult result)
    {
        try
        {
            var entry = Wait(fs.StatAsync(path, CancellationToken.None));
            result = entry is null ? PosixResult.ENOENT : PosixResult.Success;
            return entry;
        }
        catch (VfsException ex)
        {
            result = Translate(ex);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "fuse: stat '{Path}' failed", path);
            result = PosixResult.EIO;
            return null;
        }
    }

    private async Task<List<VfsEntry>> ListAsync(string path)
    {
        var entries = new List<VfsEntry>();
        await foreach (var entry in fs.ListAsync(path, CancellationToken.None).ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private PosixResult Guard(Func<PosixResult> body)
    {
        try
        {
            return body();
        }
        catch (VfsException ex)
        {
            return Translate(ex);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "fuse: operation failed");
            return PosixResult.EIO;
        }
    }

    private PosixResult WriteRefusal() => fs.ReadOnly ? PosixResult.EROFS : PosixResult.EACCES;

    private PosixResult Translate(VfsException ex) => ex.Error switch
    {
        VfsError.NotFound => PosixResult.ENOENT,
        VfsError.AlreadyExists => PosixResult.EEXIST,
        VfsError.NotADirectory => PosixResult.ENOTDIR,
        VfsError.IsADirectory => PosixResult.EISDIR,
        VfsError.DirectoryNotEmpty => NotEmpty,
        VfsError.ReadOnly => WriteRefusal(),
        VfsError.AccessDenied => PosixResult.EACCES,
        VfsError.InvalidPath => PosixResult.EINVAL,
        VfsError.NotSupported => PosixResult.ENOTSUP,
        _ => PosixResult.EIO,
    };

    private static bool WantsWrite(PosixOpenFlags flags)
    {
        var access = flags & PosixOpenFlags.AccessModes;
        return access == PosixOpenFlags.Write || access == PosixOpenFlags.ReadWrite || (flags & (PosixOpenFlags.Append | PosixOpenFlags.Truncate | PosixOpenFlags.Create)) != 0;
    }

    private static string PathOf(ReadOnlyNativeMemory<byte> fileNamePtr)
    {
        var text = FuseHelper.GetString(fileNamePtr);
        return VfsPath.TryNormalize(text, out var path, out _) ? path : text.Trim('/');
    }

    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    private sealed class Lease(IVfsFile file) : IDisposable
    {
        public IVfsFile File => file;

        public void Dispose() => Wait(file.DisposeAsync().AsTask());
    }

    /// <summary>
    /// One open file handle (<see cref="FuseFileInfo.Context"/>): the VFS file and its read-ahead. Reads and writes on
    /// it run one at a time under <see cref="Gate"/>, so the read-ahead never holds what a write replaced.
    /// </summary>
    private sealed class Handle(IVfsFile file)
    {
        public IVfsFile File { get; } = file;

        public Lock Gate { get; } = new();

        public SequentialReadAhead ReadAhead { get; } = new();
    }
}
