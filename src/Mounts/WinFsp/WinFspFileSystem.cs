using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using AnyFS.Sdk;
using Fsp;
using Microsoft.Extensions.Logging;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace AnyFS.Mounts.WinFsp;

/// <summary>
/// The WinFsp callbacks (spec 8.1) over a collection's <see cref="IVirtualFileSystem"/>: case-insensitive, preserving,
/// no reparse points, streams or security (a fixed descriptor granting everyone everything; the read-only layers
/// decide what writes succeed and show as FILE_ATTRIBUTE_READONLY). WinFsp calls these on its dispatcher threads
/// (<see cref="WinFspMount.DispatcherThreads"/>), so each one waits for the asynchronous VFS call. A failure is the
/// request's alone: each callback answers it with a status, and whatever still escapes one is logged and answered
/// by <see cref="ExceptionHandler"/>, so the volume serves on.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinFspFileSystem(IVirtualFileSystem fs, string label, string prefix, ILogger logger) : FileSystemBase
{
    private const uint AttrReadonly = 0x1;
    private const uint AttrHidden = 0x2;
    private const uint AttrSystem = 0x4;
    private const uint AttrDirectory = 0x10;
    private const uint AttrNormal = 0x80;
    private const uint AccessWriteData = 0x2;
    private const uint AccessAppendData = 0x4;
    private const uint AccessDelete = 0x10000;
    private const uint AccessGenericAll = 0x10000000;
    private const uint AccessGenericWrite = 0x40000000;
    private const ulong AllocationUnit = 4096;
    private static readonly byte[] Everyone = BuildDescriptor("O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)");
    private readonly ulong _mountedAt = (ulong)DateTime.UtcNow.ToFileTimeUtc();

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        var space = Try(() => fs.GetSpaceAsync("", CancellationToken.None));
        volumeInfo.TotalSize = (ulong)(space?.TotalBytes ?? 1L << 40);
        volumeInfo.FreeSize = (ulong)(space?.FreeBytes ?? 1L << 39);
        volumeInfo.SetVolumeLabel(label.Length > 32 ? label[..32] : label);
        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        fileAttributes = 0;
        if (!TryPath(fileName, out var path))
        {
            logger.LogDebug("winfsp: security by name '{Name}': invalid", fileName);
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        var entry = Stat(path, out var status);
        if (entry is null)
        {
            logger.LogDebug("winfsp: security by name '{Name}': 0x{Status:X8}", fileName, status);
            return status;
        }

        fileAttributes = Attributes(path, entry);
        if (securityDescriptor is not null)
        {
            securityDescriptor = Everyone;
        }

        return STATUS_SUCCESS;
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor, ulong allocationSize, out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;
        if (!TryPath(fileName, out var path) || path.Length == 0)
        {
            return STATUS_OBJECT_NAME_INVALID;
        }

        var parent = Stat(VfsPath.Parent(path), out var parentStatus);
        if (parent is null)
        {
            return parentStatus == STATUS_OBJECT_NAME_NOT_FOUND ? STATUS_OBJECT_PATH_NOT_FOUND : parentStatus;
        }

        if (!parent.IsDirectory)
        {
            return STATUS_OBJECT_PATH_NOT_FOUND;
        }

        if (!fs.CanWrite(VfsPath.Parent(path)))
        {
            return WriteRefusal();
        }

        var desc = new Desc(path);
        var status = Guard(() =>
        {
            if ((createOptions & FILE_DIRECTORY_FILE) != 0)
            {
                Wait(fs.CreateDirectoryAsync(path, CancellationToken.None));
                desc.IsDirectory = true;
            }
            else
            {
                desc.File = Wait(fs.OpenAsync(path, VfsOpenMode.CreateNew, CancellationToken.None));
            }

            return STATUS_SUCCESS;
        });
        if (status < 0)
        {
            return status;
        }

        fileDesc = desc;
        normalizedName = Normalized(fileName, VfsPath.Name(path));
        return InfoFor(desc, out fileInfo);
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess, out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        fileNode = null!;
        fileDesc = null!;
        fileInfo = default;
        normalizedName = null!;
        if (!TryPath(fileName, out var path))
        {
            logger.LogDebug("winfsp: open '{Name}': invalid", fileName);
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        var entry = Stat(path, out var status);
        if (entry is null)
        {
            logger.LogDebug("winfsp: open '{Name}' (access 0x{Access:X8}, options 0x{Options:X8}): 0x{Status:X8}", fileName, grantedAccess, createOptions, status);
            return status;
        }

        logger.LogDebug("winfsp: open '{Name}' (access 0x{Access:X8}, options 0x{Options:X8})", fileName, grantedAccess, createOptions);
        if (entry.IsDirectory && (createOptions & FILE_NON_DIRECTORY_FILE) != 0)
        {
            return STATUS_FILE_IS_A_DIRECTORY;
        }

        if (!entry.IsDirectory && (createOptions & FILE_DIRECTORY_FILE) != 0)
        {
            return STATUS_NOT_A_DIRECTORY;
        }

        var wantsWrite = (grantedAccess & (AccessWriteData | AccessAppendData | AccessGenericWrite | AccessGenericAll)) != 0;
        var wantsDelete = (grantedAccess & AccessDelete) != 0 || (createOptions & FILE_DELETE_ON_CLOSE) != 0;
        if ((wantsWrite || wantsDelete) && !fs.CanWrite(path))
        {
            return WriteRefusal();
        }

        var desc = new Desc(path) { IsDirectory = entry.IsDirectory };
        if (!entry.IsDirectory)
        {
            status = Guard(() =>
            {
                desc.File = Wait(fs.OpenAsync(path, wantsWrite ? VfsOpenMode.ReadWrite : VfsOpenMode.Read, CancellationToken.None));
                return STATUS_SUCCESS;
            });
            if (status < 0)
            {
                return status;
            }
        }

        fileDesc = desc;
        normalizedName = Normalized(fileName, path.Length == 0 ? "" : entry.Name);
        return InfoFor(desc, out fileInfo);
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes, ulong allocationSize, out FileInfo fileInfo)
    {
        var desc = (Desc)fileDesc;
        fileInfo = default;
        if (desc.File is null)
        {
            return STATUS_INVALID_DEVICE_REQUEST;
        }

        int status;
        lock (desc.ReadLock)
        {
            status = Guard(() =>
            {
                Wait(desc.File.SetLengthAsync(0, CancellationToken.None).AsTask());
                return STATUS_SUCCESS;
            });
            desc.ForgetReadAhead();
        }

        return status < 0 ? status : InfoFor(desc, out fileInfo);
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        var desc = (Desc)fileDesc;
        if ((flags & CleanupDelete) == 0)
        {
            return;
        }

        try
        {
            if (desc.File is not null)
            {
                Wait(desc.File.DisposeAsync().AsTask());
                desc.File = null;
            }

            Wait(fs.DeleteAsync(desc.Path, CancellationToken.None));
        }
        catch (VfsException ex)
        {
            logger.LogDebug("winfsp: delete on close of '{Path}' refused: {Error}", desc.Path, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "winfsp: delete on close of '{Path}' failed", desc.Path);
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        var desc = (Desc)fileDesc;
        lock (desc.ReadLock)
        {
            desc.ReleaseReadAhead();
        }

        if (desc.File is { } file)
        {
            desc.File = null;
            try
            {
                Wait(file.DisposeAsync().AsTask());
            }
            catch (VfsException ex)
            {
                logger.LogDebug("winfsp: close of '{Path}': {Error}", desc.Path, ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "winfsp: close of '{Path}' failed", desc.Path);
            }
        }
    }

    /// <summary>
    /// WinFsp hands every application read to user mode as it is: the volume runs with a 1 s file-info timeout, not
    /// WinFsp's full caching mode, because that mode keeps serving a file's old contents and size after it changed on
    /// the source (the lab checked). A sequential reader with a small buffer (Get-FileHash and .NET's default
    /// FileStream read 4 KB at a time) would then pay one round trip to the source per read, which is 17 MiB/s on the
    /// lab's VM. So a handle's reads go through its <see cref="SequentialReadAhead"/> (shared with the FUSE mount): a
    /// read that follows on from the last fetches 1 MiB and the handle is served from that until it runs out, seeks or
    /// writes; nothing outlives the handle.
    /// </summary>
    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var desc = (Desc)fileDesc;
        if (desc.File is null)
        {
            return STATUS_INVALID_DEVICE_REQUEST;
        }

        var at = (long)offset;
        var count = (int)length;
        lock (desc.ReadLock)
        {
            var file = desc.File;
            var rented = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                var n = 0;
                var target = rented.AsMemory(0, count);
                var status = Guard(() =>
                {
                    n = Wait(desc.ReadAhead.ReadAsync(file, at, target, CancellationToken.None).AsTask());
                    return STATUS_SUCCESS;
                });
                if (status < 0)
                {
                    return status;
                }

                if (n == 0)
                {
                    return STATUS_END_OF_FILE;
                }

                Marshal.Copy(rented, 0, buffer, n);
                bytesTransferred = (uint)n;
                return STATUS_SUCCESS;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        var desc = (Desc)fileDesc;
        if (desc.File is null)
        {
            return STATUS_INVALID_DEVICE_REQUEST;
        }

        var file = desc.File;
        var rented = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            lock (desc.ReadLock)
            {
                var written = 0;
                var status = Guard(() =>
                {
                    var current = file.Length;
                    var at = writeToEndOfFile ? current : (long)offset;
                    var count = (int)length;
                    if (constrainedIo)
                    {
                        if (at >= current)
                        {
                            return STATUS_SUCCESS;
                        }

                        count = (int)Math.Min(count, current - at);
                    }

                    Marshal.Copy(buffer, rented, 0, count);
                    Wait(file.WriteAsync(at, rented.AsMemory(0, count), CancellationToken.None).AsTask());
                    written = count;
                    return STATUS_SUCCESS;
                });
                desc.ForgetReadAhead();
                if (status < 0)
                {
                    return status;
                }

                bytesTransferred = (uint)written;
            }

            return InfoFor(desc, out fileInfo);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;
        if (fileDesc is not Desc desc)
        {
            return STATUS_SUCCESS; // a volume flush
        }

        if (desc.File is { } file)
        {
            var status = Guard(() =>
            {
                Wait(file.FlushAsync(CancellationToken.None).AsTask());
                return STATUS_SUCCESS;
            });
            if (status < 0)
            {
                return status;
            }
        }

        return InfoFor(desc, out fileInfo);
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo) => InfoFor((Desc)fileDesc, out fileInfo);

    /// <summary>What winfsp.net calls with an exception that left a callback: logged (it would otherwise leave no trace) and answered as an I/O error, so only that request fails.</summary>
    public override int ExceptionHandler(Exception ex)
    {
        logger.LogWarning(ex, "winfsp: a request failed");
        return STATUS_UNEXPECTED_IO_ERROR;
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo)
    {
        var desc = (Desc)fileDesc;
        fileInfo = default;
        var created = TimeOrNull(creationTime);
        var modified = TimeOrNull(lastWriteTime);
        if (created is not null || modified is not null)
        {
            var status = Guard(() =>
            {
                Wait(fs.SetTimesAsync(desc.Path, created, modified, CancellationToken.None));
                return STATUS_SUCCESS;
            });
            if (status < 0 && status != STATUS_ACCESS_DENIED && status != STATUS_MEDIA_WRITE_PROTECTED)
            {
                return status;
            }
        }

        return InfoFor(desc, out fileInfo);
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        var desc = (Desc)fileDesc;
        fileInfo = default;
        if (desc.File is null)
        {
            return STATUS_INVALID_DEVICE_REQUEST;
        }

        if (!setAllocationSize)
        {
            int status;
            lock (desc.ReadLock)
            {
                status = Guard(() =>
                {
                    Wait(desc.File.SetLengthAsync((long)newSize, CancellationToken.None).AsTask());
                    return STATUS_SUCCESS;
                });
                desc.ForgetReadAhead();
            }

            if (status < 0)
            {
                return status;
            }
        }

        return InfoFor(desc, out fileInfo);
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        var desc = (Desc)fileDesc;
        if (!fs.CanWrite(desc.Path))
        {
            return WriteRefusal();
        }

        if (!desc.IsDirectory)
        {
            return STATUS_SUCCESS;
        }

        return Guard(() =>
        {
            var any = Wait(AnyChildAsync(desc.Path));
            return any ? STATUS_DIRECTORY_NOT_EMPTY : STATUS_SUCCESS;
        });
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        var desc = (Desc)fileDesc;
        // for a network file system the new name arrives with the volume prefix in front (\anyfs\<collection>\...)
        var name = newFileName;
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (name.Length == prefix.Length || name[prefix.Length] == '\\'))
        {
            name = name[prefix.Length..];
        }

        if (!TryPath(name, out var target) || target.Length == 0)
        {
            logger.LogDebug("winfsp: rename '{From}' to '{To}': invalid name", desc.Path, newFileName);
            return STATUS_OBJECT_NAME_INVALID;
        }

        var status = Guard(() =>
        {
            Wait(fs.RenameAsync(desc.Path, target, replaceIfExists, CancellationToken.None));
            desc.Path = target;
            return STATUS_SUCCESS;
        });
        logger.LogDebug("winfsp: rename '{From}' (file name '{FileName}') to '{To}' (raw '{Raw}', replace {Replace}): 0x{Status:X8}", desc.Path, fileName, target, newFileName, replaceIfExists, status);
        return status;
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker, ref object context, out string fileName, out FileInfo fileInfo)
    {
        var desc = (Desc)fileDesc;
        fileName = null!;
        fileInfo = default;
        if (desc.Entries is null)
        {
            var list = new List<(string Name, FileInfo Info)>();
            try
            {
                if (desc.Path.Length > 0)
                {
                    if (Stat(desc.Path, out _) is { } self)
                    {
                        list.Add((".", Info(desc.Path, self)));
                    }

                    var parentPath = VfsPath.Parent(desc.Path);
                    if (Stat(parentPath, out _) is { } parent)
                    {
                        list.Add(("..", Info(parentPath, parent)));
                    }
                }

                foreach (var child in Wait(ListAsync(desc.Path)))
                {
                    list.Add((child.Name, Info(VfsPath.Join(desc.Path, child.Name), child)));
                }
            }
            catch (VfsException ex)
            {
                logger.LogDebug("winfsp: listing '{Path}': {Error}", desc.Path, ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "winfsp: listing '{Path}' failed", desc.Path);
            }

            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            desc.Entries = list;
        }

        int index;
        if (context is null)
        {
            index = 0;
            if (marker is not null)
            {
                index = desc.Entries.FindIndex(e => string.CompareOrdinal(e.Name, marker) > 0);
                if (index < 0)
                {
                    index = desc.Entries.Count;
                }
            }
        }
        else
        {
            index = (int)context;
        }

        if (index >= desc.Entries.Count)
        {
            return false;
        }

        context = index + 1;
        (fileName, fileInfo) = desc.Entries[index];
        return true;
    }

    // ----- helpers -----

    private int InfoFor(Desc desc, out FileInfo fileInfo)
    {
        fileInfo = default;
        var entry = Stat(desc.Path, out var status);
        if (entry is null)
        {
            return status;
        }

        fileInfo = Info(desc.Path, entry);
        if (desc.File is { } file && !entry.IsDirectory)
        {
            // the handle knows the current length better than a cached stat might
            fileInfo.FileSize = (ulong)file.Length;
            fileInfo.AllocationSize = (fileInfo.FileSize + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
        }

        return STATUS_SUCCESS;
    }

    private FileInfo Info(string path, VfsEntry entry)
    {
        var modified = entry.Modified is { } m ? (ulong)m.ToFileTime() : _mountedAt;
        var created = entry.Created is { } c ? (ulong)c.ToFileTime() : modified;
        var size = entry.IsDirectory ? 0UL : (ulong)entry.Length;
        return new FileInfo
        {
            FileAttributes = Attributes(path, entry),
            FileSize = size,
            AllocationSize = (size + AllocationUnit - 1) / AllocationUnit * AllocationUnit,
            CreationTime = created,
            LastAccessTime = modified,
            LastWriteTime = modified,
            ChangeTime = modified,
        };
    }

    private uint Attributes(string path, VfsEntry entry)
    {
        uint attributes = 0;
        if (entry.IsDirectory)
        {
            attributes |= AttrDirectory;
        }
        else if (entry.Attributes.HasFlag(VfsAttributes.ReadOnly) || !fs.CanWrite(path))
        {
            attributes |= AttrReadonly;
        }

        if (entry.Attributes.HasFlag(VfsAttributes.Hidden))
        {
            attributes |= AttrHidden;
        }

        if (entry.Attributes.HasFlag(VfsAttributes.System))
        {
            attributes |= AttrSystem;
        }

        return attributes == 0 ? AttrNormal : attributes;
    }

    private VfsEntry? Stat(string path, out int status)
    {
        try
        {
            var entry = Wait(fs.StatAsync(path, CancellationToken.None));
            status = entry is null ? STATUS_OBJECT_NAME_NOT_FOUND : STATUS_SUCCESS;
            return entry;
        }
        catch (VfsException ex)
        {
            status = Translate(ex);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "winfsp: stat '{Path}' failed", path);
            status = STATUS_IO_DEVICE_ERROR;
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

    private async Task<bool> AnyChildAsync(string path)
    {
        await foreach (var _ in fs.ListAsync(path, CancellationToken.None).ConfigureAwait(false))
        {
            return true;
        }

        return false;
    }

    private int Guard(Func<int> body)
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
            logger.LogWarning(ex, "winfsp: operation failed");
            return STATUS_IO_DEVICE_ERROR;
        }
    }

    private int WriteRefusal() => fs.ReadOnly ? STATUS_MEDIA_WRITE_PROTECTED : STATUS_ACCESS_DENIED;

    private int Translate(VfsException ex) => ex.Error switch
    {
        VfsError.NotFound => STATUS_OBJECT_NAME_NOT_FOUND,
        VfsError.AlreadyExists => STATUS_OBJECT_NAME_COLLISION,
        VfsError.NotADirectory => STATUS_NOT_A_DIRECTORY,
        VfsError.IsADirectory => STATUS_FILE_IS_A_DIRECTORY,
        VfsError.DirectoryNotEmpty => STATUS_DIRECTORY_NOT_EMPTY,
        VfsError.ReadOnly => WriteRefusal(),
        VfsError.AccessDenied => STATUS_ACCESS_DENIED,
        VfsError.InvalidPath => STATUS_OBJECT_NAME_INVALID,
        VfsError.NotSupported => STATUS_NOT_SUPPORTED,
        VfsError.Unavailable => STATUS_DEVICE_NOT_READY,
        _ => STATUS_IO_DEVICE_ERROR,
    };

    private static T? Try<T>(Func<Task<T?>> action)
        where T : class
    {
        try
        {
            return Wait(action());
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void Wait(Task task) => task.GetAwaiter().GetResult();

    private static bool TryPath(string fileName, out string path)
    {
        var text = fileName.Replace('\\', '/');
        return VfsPath.TryNormalize(text, out path, out _);
    }

    /// <summary>
    /// The name WinFsp should remember for an opened file: as typed, with the last segment in the case the source
    /// stores it. Without a normalized name WinFsp upper-cases names on a case-insensitive volume, and a later rename
    /// would ask the sources for MADE\moved.txt.
    /// </summary>
    private static string Normalized(string typed, string lastSegment)
    {
        var text = typed.Replace('/', '\\').TrimEnd('\\');
        if (text.Length == 0)
        {
            return "\\";
        }

        if (!text.StartsWith('\\'))
        {
            text = "\\" + text;
        }

        var cut = text.LastIndexOf('\\');
        return lastSegment.Length == 0 ? text : text[..(cut + 1)] + lastSegment;
    }

    private static DateTimeOffset? TimeOrNull(ulong fileTime)
        => fileTime == 0 || fileTime == ulong.MaxValue ? null : DateTimeOffset.FromFileTime((long)fileTime);

    private static byte[] BuildDescriptor(string sddl)
    {
        var raw = new RawSecurityDescriptor(sddl);
        var bytes = new byte[raw.BinaryLength];
        raw.GetBinaryForm(bytes, 0);
        return bytes;
    }

    /// <summary>
    /// One open handle: the path (updated by Rename), the VFS file for files, the listing snapshot for directories, and
    /// the read-ahead state of <see cref="Read"/>.
    /// </summary>
    private sealed class Desc(string path)
    {
        public string Path { get; set; } = path;

        public bool IsDirectory { get; set; }

        public IVfsFile? File { get; set; }

        public List<(string Name, FileInfo Info)>? Entries { get; set; }

        /// <summary>Reads and writes on one handle run one at a time, so the read-ahead never holds what a write replaced.</summary>
        public object ReadLock { get; } = new();

        public SequentialReadAhead ReadAhead { get; } = new();

        /// <summary>After a write or a resize through this handle: the buffer and the position mean nothing now.</summary>
        public void ForgetReadAhead() => ReadAhead.Forget();

        public void ReleaseReadAhead() => ReadAhead.Dispose();
    }
}
