using System.Runtime.CompilerServices;

namespace LibSmb;

/// <summary>
/// A folder of this machine as a share's file system. Files are opened sharing read, write and delete, since SMB clients
/// rename and delete files they still have open. Names are case-insensitive on Windows and macOS, as their usual file
/// systems are, and case-sensitive elsewhere.
/// </summary>
public sealed class LocalFileSystem : ISmbFileSystem
{
    /// <summary>A file system serving <paramref name="root"/>, which must be an existing folder.</summary>
    public LocalFileSystem(string root)
    {
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root))
        {
            throw new DirectoryNotFoundException($"{Root} is not a folder");
        }
    }

    /// <summary>The folder served.</summary>
    public string Root { get; }

    /// <inheritdoc />
    public bool CaseInsensitive => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <inheritdoc />
    public ValueTask<SmbEntry?> StatAsync(string path, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        var name = path.Length == 0 ? "" : path[(path.LastIndexOf('/') + 1)..];
        if (Directory.Exists(full))
        {
            var info = new DirectoryInfo(full);
            return new(new SmbEntry(name, true, 0, info.LastWriteTimeUtc, false, info.CreationTimeUtc));
        }

        if (File.Exists(full))
        {
            // the name as asked for: on a case-insensitive file system, a lookup in another case finds the file
            return new(EntryOf(new FileInfo(full), name));
        }

        return new((SmbEntry?)null);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SmbEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        if (!Directory.Exists(full))
        {
            throw File.Exists(full)
                ? new SmbFileSystemException(SmbFileSystemError.NotADirectory, $"{path} is not a directory")
                : new SmbFileSystemException(SmbFileSystemError.NotFound, $"{path} does not exist");
        }

        foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            SmbEntry entry;
            try
            {
                entry = info is DirectoryInfo d
                    ? new SmbEntry(d.Name, true, 0, d.LastWriteTimeUtc, false, d.CreationTimeUtc)
                    : EntryOf((FileInfo)info, info.Name);
            }
            catch (FileNotFoundException)
            {
                // gone between the listing and the look at it
                continue;
            }

            yield return entry;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<ISmbFile> OpenAsync(string path, SmbOpenMode mode, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        var write = (mode & (SmbOpenMode.Write | SmbOpenMode.Truncate)) != 0;
        var fileMode = (mode & (SmbOpenMode.Create | SmbOpenMode.Exclusive | SmbOpenMode.Truncate)) switch
        {
            var m when (m & SmbOpenMode.Create) != 0 && (m & SmbOpenMode.Exclusive) != 0 => FileMode.CreateNew,
            var m when (m & SmbOpenMode.Create) != 0 && (m & SmbOpenMode.Truncate) != 0 => FileMode.Create,
            var m when (m & SmbOpenMode.Create) != 0 => FileMode.OpenOrCreate,
            var m when (m & SmbOpenMode.Truncate) != 0 => FileMode.Truncate,
            _ => FileMode.Open,
        };
        if (Directory.Exists(full))
        {
            throw new SmbFileSystemException(SmbFileSystemError.IsADirectory, $"{path} is a directory");
        }

        try
        {
            var stream = new FileStream(full, new FileStreamOptions
            {
                Mode = fileMode,
                Access = write ? FileAccess.ReadWrite : FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,

                // no buffering of our own: what is written is in the file at once, so a time set afterwards sticks
                BufferSize = 0,
                Options = FileOptions.Asynchronous | FileOptions.RandomAccess,
            });
            return new(new LocalFile(stream));
        }
        catch (IOException) when (fileMode == FileMode.CreateNew && File.Exists(full))
        {
            throw new SmbFileSystemException(SmbFileSystemError.AlreadyExists, $"{path} exists");
        }
    }

    /// <inheritdoc />
    public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        if (Directory.Exists(full) || File.Exists(full))
        {
            throw new SmbFileSystemException(SmbFileSystemError.AlreadyExists, $"{path} exists");
        }

        var parent = Path.GetDirectoryName(full);
        if (parent is not null && !Directory.Exists(parent))
        {
            throw new SmbFileSystemException(SmbFileSystemError.PathNotFound, $"the folder of {path} does not exist");
        }

        Directory.CreateDirectory(full);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        if (Directory.Exists(full))
        {
            if (Directory.EnumerateFileSystemEntries(full).Any())
            {
                throw new SmbFileSystemException(SmbFileSystemError.DirectoryNotEmpty, $"{path} is not empty");
            }

            Directory.Delete(full);
        }
        else if (File.Exists(full))
        {
            File.Delete(full);
        }
        else
        {
            throw new SmbFileSystemException(SmbFileSystemError.NotFound, $"{path} does not exist");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RenameAsync(string fromPath, string toPath, bool replace, CancellationToken cancellationToken)
    {
        var from = Resolve(fromPath);
        var to = Resolve(toPath);
        if (Directory.Exists(from))
        {
            if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && (Directory.Exists(to) || File.Exists(to)))
            {
                if (!replace || Directory.Exists(to))
                {
                    throw new SmbFileSystemException(SmbFileSystemError.AlreadyExists, $"{toPath} exists");
                }

                File.Delete(to);
            }

            Directory.Move(from, to);
        }
        else if (File.Exists(from))
        {
            if (Directory.Exists(to))
            {
                throw new SmbFileSystemException(SmbFileSystemError.AlreadyExists, $"{toPath} is a directory");
            }

            File.Move(from, to, replace);
        }
        else
        {
            throw new SmbFileSystemException(SmbFileSystemError.NotFound, $"{fromPath} does not exist");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken cancellationToken)
    {
        var full = Resolve(path);
        var isDirectory = Directory.Exists(full);
        if (!isDirectory && !File.Exists(full))
        {
            throw new SmbFileSystemException(SmbFileSystemError.NotFound, $"{path} does not exist");
        }

        // a creation time only where the file system keeps one apart (Linux has no call that sets it)
        if (created is { } c && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()))
        {
            if (isDirectory)
            {
                Directory.SetCreationTimeUtc(full, c.UtcDateTime);
            }
            else
            {
                File.SetCreationTimeUtc(full, c.UtcDateTime);
            }
        }

        if (modified is { } m)
        {
            if (isDirectory)
            {
                Directory.SetLastWriteTimeUtc(full, m.UtcDateTime);
            }
            else
            {
                File.SetLastWriteTimeUtc(full, m.UtcDateTime);
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<SmbSpace?> GetSpaceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var drive = new DriveInfo(Root);
            return new(new SmbSpace(drive.TotalSize, drive.AvailableFreeSpace));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new((SmbSpace?)null);
        }
    }

    /// <summary>The full local path of a share path, which never leaves the root.</summary>
    private string Resolve(string path)
    {
        if (path.Length == 0)
        {
            return Root;
        }

        var full = Path.GetFullPath(Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(Root, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new SmbFileSystemException(SmbFileSystemError.AccessDenied, $"{path} is outside the share");
        }

        return full;
    }

    private static SmbEntry EntryOf(FileInfo info, string name)
        => new(name, false, info.Length, info.LastWriteTimeUtc, info.IsReadOnly, info.CreationTimeUtc);

    private sealed class LocalFile(FileStream stream) : ISmbFile
    {
        public long Length => stream.Length;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
            => RandomAccess.ReadAsync(stream.SafeFileHandle, buffer, offset, cancellationToken);

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
            => RandomAccess.WriteAsync(stream.SafeFileHandle, data, offset, cancellationToken);

        public ValueTask SetLengthAsync(long length, CancellationToken cancellationToken)
        {
            RandomAccess.SetLength(stream.SafeFileHandle, length);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            stream.Flush(flushToDisk: true);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
