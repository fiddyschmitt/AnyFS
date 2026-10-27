namespace LibSmb;

/// <summary>
/// The file system behind one share. Paths are share-relative, separated by forward slashes, with no leading or trailing
/// slash; <c>""</c> is the share's root. The server has already resolved <c>.</c> and <c>..</c> and anchored every path at
/// the root, so a path never leaves the share. Failures are <see cref="SmbFileSystemException"/>s, or the .NET exceptions
/// of file access (<see cref="FileNotFoundException"/>, <see cref="UnauthorizedAccessException"/>, <see cref="IOException"/>),
/// which the server turns into the closest NTSTATUS.
/// </summary>
public interface ISmbFileSystem
{
    /// <summary>Whether names that differ only in case are the same name, which the server's name matching follows.</summary>
    bool CaseInsensitive { get; }

    /// <summary>The entry at a path, or null when there is none.</summary>
    ValueTask<SmbEntry?> StatAsync(string path, CancellationToken cancellationToken);

    /// <summary>A directory's entries (not <c>.</c> or <c>..</c>).</summary>
    IAsyncEnumerable<SmbEntry> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>Opens or creates a file as <paramref name="mode"/> says.</summary>
    ValueTask<ISmbFile> OpenAsync(string path, SmbOpenMode mode, CancellationToken cancellationToken);

    /// <summary>Creates a directory; an existing name is <see cref="SmbFileSystemError.AlreadyExists"/>.</summary>
    ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>Deletes a file or an empty directory; a directory with anything in it is <see cref="SmbFileSystemError.DirectoryNotEmpty"/>.</summary>
    ValueTask DeleteAsync(string path, CancellationToken cancellationToken);

    /// <summary>Renames a file or directory, replacing an existing file at the target when <paramref name="replace"/>.</summary>
    ValueTask RenameAsync(string fromPath, string toPath, bool replace, CancellationToken cancellationToken);

    /// <summary>Sets an entry's times; a null one is left as it is.</summary>
    ValueTask SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken cancellationToken);

    /// <summary>The space of the volume behind the share, or null when unknown (the share then shows 1 TiB, 512 GiB of it free).</summary>
    ValueTask<SmbSpace?> GetSpaceAsync(CancellationToken cancellationToken);
}

/// <summary>An open file. One open's calls never overlap; different opens of a file may be used at the same time.</summary>
public interface ISmbFile : IAsyncDisposable
{
    /// <summary>The file's length now, with what was written through this open.</summary>
    long Length { get; }

    /// <summary>Reads up to <paramref name="buffer"/>'s length at <paramref name="offset"/>; 0 at or past the end.</summary>
    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="data"/> at <paramref name="offset"/>, extending the file as needed.</summary>
    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Truncates or extends the file to <paramref name="length"/>.</summary>
    ValueTask SetLengthAsync(long length, CancellationToken cancellationToken);

    /// <summary>Makes what was written durable (or at least visible to other opens).</summary>
    ValueTask FlushAsync(CancellationToken cancellationToken);
}

/// <summary>How a file is opened.</summary>
[Flags]
public enum SmbOpenMode
{
    /// <summary>For reading, an existing file.</summary>
    Read = 0,

    /// <summary>For reading and writing.</summary>
    Write = 1,

    /// <summary>Create the file when there is none.</summary>
    Create = 2,

    /// <summary>With <see cref="Create"/>: fail with <see cref="SmbFileSystemError.AlreadyExists"/> when there is one.</summary>
    Exclusive = 4,

    /// <summary>Truncate an existing file to nothing.</summary>
    Truncate = 8,
}

/// <summary>A file or directory as the share shows it.</summary>
/// <param name="Name">Its name (the last part of its path).</param>
/// <param name="IsDirectory">A directory, or a file.</param>
/// <param name="Length">A file's length in bytes; 0 for a directory.</param>
/// <param name="Modified">When it was last written.</param>
/// <param name="ReadOnly">
/// A file that is read-only itself: shown with FILE_ATTRIBUTE_READONLY, and opens that would write to it or delete it are
/// refused with STATUS_ACCESS_DENIED, as Windows does with its own read-only files. Directories carry no such flag.
/// </param>
/// <param name="Created">When it was created, when known; the modification time stands in otherwise.</param>
public sealed record SmbEntry(string Name, bool IsDirectory, long Length, DateTimeOffset Modified, bool ReadOnly = false, DateTimeOffset? Created = null);

/// <summary>A volume's size and free space.</summary>
/// <param name="TotalBytes">Its size in bytes.</param>
/// <param name="FreeBytes">The bytes still free for the share's user.</param>
public readonly record struct SmbSpace(long TotalBytes, long FreeBytes);

/// <summary>Why an operation of an <see cref="ISmbFileSystem"/> failed, which the server answers with the matching NTSTATUS.</summary>
public enum SmbFileSystemError
{
    /// <summary>Something else (STATUS_UNSUCCESSFUL).</summary>
    Failed,

    /// <summary>No such file or directory (STATUS_OBJECT_NAME_NOT_FOUND).</summary>
    NotFound,

    /// <summary>A folder on the way is missing (STATUS_OBJECT_PATH_NOT_FOUND).</summary>
    PathNotFound,

    /// <summary>The name is taken (STATUS_OBJECT_NAME_COLLISION).</summary>
    AlreadyExists,

    /// <summary>Not allowed (STATUS_ACCESS_DENIED).</summary>
    AccessDenied,

    /// <summary>The file system takes no changes (STATUS_MEDIA_WRITE_PROTECTED).</summary>
    ReadOnly,

    /// <summary>A directory that is not empty (STATUS_DIRECTORY_NOT_EMPTY).</summary>
    DirectoryNotEmpty,

    /// <summary>A directory where a file was meant (STATUS_FILE_IS_A_DIRECTORY).</summary>
    IsADirectory,

    /// <summary>A file where a directory was meant (STATUS_NOT_A_DIRECTORY).</summary>
    NotADirectory,

    /// <summary>A name the file system cannot hold (STATUS_OBJECT_NAME_INVALID).</summary>
    InvalidName,

    /// <summary>In use elsewhere (STATUS_SHARING_VIOLATION).</summary>
    SharingViolation,

    /// <summary>No room left (STATUS_DISK_FULL).</summary>
    DiskFull,

    /// <summary>Not something this file system does (STATUS_NOT_SUPPORTED).</summary>
    NotSupported,
}

/// <summary>A failure of an <see cref="ISmbFileSystem"/> operation, with its kind.</summary>
public sealed class SmbFileSystemException : IOException
{
    /// <summary>A failure of the given kind.</summary>
    public SmbFileSystemException(SmbFileSystemError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    /// <summary>What kind of failure it is.</summary>
    public SmbFileSystemError Error { get; }
}
