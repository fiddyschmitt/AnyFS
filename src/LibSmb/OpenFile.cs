using System.Security.Cryptography;
using System.Text;
using LibSmb.Protocol;

namespace LibSmb;

/// <summary>An SMB2 FileId ([MS-SMB2] 2.2.14.1): 16 bytes, here a persistent and a volatile half with the same value.</summary>
internal readonly record struct FileId(ulong Persistent, ulong Volatile)
{
    public static FileId Read(ReadOnlySpan<byte> b) => new(Wire.U64(b, 0), Wire.U64(b, 8));

    public void Write(Span<byte> b)
    {
        Wire.Put64(b, 0, Persistent);
        Wire.Put64(b, 8, Volatile);
    }
}

/// <summary>An open on a share: a file (with its <see cref="ISmbFile"/>) or a directory (with its enumeration's place).</summary>
internal sealed class OpenFile(FileId id, SmbShare share, string path, ISmbFile? file, bool isDirectory, bool canWrite)
{
    public FileId Id { get; } = id;

    public SmbShare Share { get; } = share;

    /// <summary>Its share-relative path, which a rename through it changes.</summary>
    public string Path { get; set; } = path;

    /// <summary>The open file; null for a directory, and for a file opened without access to its data until it reads.</summary>
    public ISmbFile? File { get; private set; } = file;

    public bool IsDirectory { get; } = isDirectory;

    /// <summary>Whether it was opened with write access.</summary>
    public bool CanWrite { get; } = canWrite;

    public bool DeleteOnClose { get; set; }

    /// <summary>A QUERY_DIRECTORY's entries, once listed; the next enumeration reply starts at <see cref="DirectoryPosition"/>.</summary>
    public List<SmbEntry>? DirectoryEntries { get; set; }

    public int DirectoryPosition { get; set; }

    private volatile bool _closed;

    /// <summary>One request at a time works on an open's state (requests on different opens run side by side).</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Whether it was closed: a request that waited for its turn on it is answered STATUS_FILE_CLOSED.</summary>
    public bool Closed
    {
        get => _closed;
        set => _closed = value;
    }

    /// <summary>
    /// Waits for the open's turn (<see cref="Gate"/>), which the caller releases after a true: false, holding nothing, when
    /// the open was closed while the request waited.
    /// </summary>
    public async ValueTask<bool> EnterAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        if (!_closed)
        {
            return true;
        }

        Gate.Release();
        return false;
    }

    public string Name => SmbPath.Name(Path);

    /// <summary>
    /// The file to read from, opened now for an open that asked for no access to the data and reads all the same (some
    /// clients do): until then such an open holds nothing open in the file system. Called under <see cref="Gate"/>.
    /// </summary>
    public async ValueTask<ISmbFile> FileToReadAsync(CancellationToken ct)
        => File ??= await Share.FileSystem.OpenAsync(Path, SmbOpenMode.Read, ct).ConfigureAwait(false);

    /// <summary>
    /// The open's attributes, size and modification time as they are now (a write through it shows), looked up afresh.
    /// When the entry is gone from under it (renamed or deleted through another open), what the open last saw of it, with
    /// the length written through it: a client that asks at CLOSE (macOS always does) takes empty times, or none, for a
    /// broken server. Bare attributes for an entry it never saw. With <paramref name="orLastSeen"/> the same goes for a
    /// lookup that gives up by itself (a time-out), which otherwise fails the request: a CLOSE closes its file all the
    /// same, and says what it knows. Called under <see cref="Gate"/>, or before the open is anyone's (its CREATE).
    /// </summary>
    public async ValueTask<(uint Attributes, long Size, DateTimeOffset? Modified, DateTimeOffset? Created, bool ReadOnly)> StatAsync(CancellationToken ct, bool orLastSeen = false)
    {
        SmbEntry? entry = null;
        try
        {
            entry = await Share.FileSystem.StatAsync(Path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        catch (OperationCanceledException) when (orLastSeen && !ct.IsCancellationRequested)
        {
        }

        if (entry is null)
        {
            return _lastSeen is { } seen
                ? (seen.Attributes, File?.Length ?? seen.Size, seen.Modified, seen.Created, seen.ReadOnly)
                : (IsDirectory ? FileAttribute.Directory : FileAttribute.Normal, File?.Length ?? 0, null, null, false);
        }

        var size = entry.IsDirectory ? 0 : File?.Length ?? entry.Length;
        var stat = (Info.Attributes(entry), size, (DateTimeOffset?)entry.Modified, entry.Created, Info.IsReadOnlyFile(entry));
        _lastSeen = stat;
        return stat;
    }

    /// <summary>What <see cref="StatAsync"/> last found of the entry.</summary>
    private (uint Attributes, long Size, DateTimeOffset? Modified, DateTimeOffset? Created, bool ReadOnly)? _lastSeen;

    /// <summary>
    /// A stable 64-bit file id for a path of a share: Windows caches file ids and takes them as stable, so they must not
    /// change across restarts. The first 8 bytes of MD5(share name, NUL, path), little-endian.
    /// </summary>
    public static ulong PathFileId(string shareName, string path)
    {
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(Encoding.UTF8.GetBytes(shareName + "\0" + path), hash);
        return Wire.U64(hash, 0);
    }
}

/// <summary>How failures of a file system become NTSTATUS codes.</summary>
internal static class Errors
{
    /// <summary>The closest NTSTATUS for an exception from a file system.</summary>
    public static uint StatusOf(Exception ex) => ex switch
    {
        SmbFileSystemException e => e.Error switch
        {
            SmbFileSystemError.NotFound => Status.ObjectNameNotFound,
            SmbFileSystemError.PathNotFound => Status.ObjectPathNotFound,
            SmbFileSystemError.AlreadyExists => Status.ObjectNameCollision,
            SmbFileSystemError.AccessDenied => Status.AccessDenied,
            SmbFileSystemError.ReadOnly => Status.MediaWriteProtected,
            SmbFileSystemError.DirectoryNotEmpty => Status.DirectoryNotEmpty,
            SmbFileSystemError.IsADirectory => Status.FileIsADirectory,
            SmbFileSystemError.NotADirectory => Status.NotADirectory,
            SmbFileSystemError.InvalidName => Status.ObjectNameInvalid,
            SmbFileSystemError.SharingViolation => Status.SharingViolation,
            SmbFileSystemError.DiskFull => Status.DiskFull,
            SmbFileSystemError.NotSupported => Status.NotSupported,
            _ => Cause(e) is { } io ? StatusOfIo(io) : Status.Unsuccessful,
        },
        FileNotFoundException => Status.ObjectNameNotFound,
        DirectoryNotFoundException => Status.ObjectPathNotFound,
        UnauthorizedAccessException => Status.AccessDenied,
        NotSupportedException => Status.NotSupported,
        ArgumentException => Status.ObjectNameInvalid,
        PathTooLongException => Status.ObjectNameInvalid,
        IOException io => StatusOfIo(io),
        _ => Status.Unsuccessful,
    };

    /// <summary>
    /// The I/O error behind a failure the file system did not classify (a file another process holds open, say, wrapped in
    /// its own exception), whose code says more than STATUS_UNSUCCESSFUL would.
    /// </summary>
    private static IOException? Cause(Exception failure)
    {
        for (var inner = failure.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is IOException io and not SmbFileSystemException)
            {
                return io;
            }
        }

        return null;
    }

    /// <summary>
    /// An I/O error by its code: on Windows the HRESULT of a Win32 error, elsewhere the errno. A sharing violation on a
    /// file another process holds open is the common one when serving a live Windows volume; mapping these to a precise
    /// status rather than STATUS_UNSUCCESSFUL lets a client skip the item instead of giving up on the whole operation.
    /// </summary>
    private static uint StatusOfIo(IOException io)
    {
        var code = io.HResult;
        if (OperatingSystem.IsWindows())
        {
            return (code & 0xFFFF) switch
            {
                5 => Status.AccessDenied,             // ERROR_ACCESS_DENIED (.NET's Directory.Move says it with an IOException)
                19 => Status.MediaWriteProtected,     // ERROR_WRITE_PROTECT
                32 or 33 => Status.SharingViolation,  // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
                39 or 112 => Status.DiskFull,         // ERROR_HANDLE_DISK_FULL, ERROR_DISK_FULL
                80 or 183 => Status.ObjectNameCollision, // ERROR_FILE_EXISTS, ERROR_ALREADY_EXISTS
                123 => Status.ObjectNameInvalid,      // ERROR_INVALID_NAME
                145 => Status.DirectoryNotEmpty,      // ERROR_DIR_NOT_EMPTY
                _ => Status.Unsuccessful,
            };
        }

        // errno: ENOTEMPTY (39 Linux, 66 macOS), EROFS (30), ENOSPC (28), EEXIST (17), ENOSYS (38 Linux, 78 macOS)
        return code switch
        {
            39 when OperatingSystem.IsLinux() => Status.DirectoryNotEmpty,
            66 when OperatingSystem.IsMacOS() => Status.DirectoryNotEmpty,
            30 => Status.MediaWriteProtected,
            28 => Status.DiskFull,
            17 => Status.ObjectNameCollision,
            38 when OperatingSystem.IsLinux() => Status.NotSupported,
            78 when OperatingSystem.IsMacOS() => Status.NotSupported,
            _ => Status.Unsuccessful,
        };
    }
}
