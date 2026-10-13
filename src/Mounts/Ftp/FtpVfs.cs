using AnyFS.Sdk;
using FubarDev.FtpServer.BackgroundTransfer;
using FubarDev.FtpServer.FileSystem;
using FubarDev.FtpServer.FileSystem.Generic;

namespace AnyFS.Mounts.Ftp;

/// <summary>One collection served by the shared FTP server, with the login that may see it.</summary>
internal sealed record FtpCollection(string Name, IVirtualFileSystem Fs, IMountStatsSink Stats, string? User, string? Password, bool AllowAnonymous);

/// <summary>
/// The file system a logged-in FTP user sees: a virtual root listing the collections the login may use, each
/// collection's tree beneath it, every operation going through the collection's <see cref="IVirtualFileSystem"/>
/// (so the read-only layers and stats apply). Paths are kept in the entries; nothing is cached.
/// </summary>
internal sealed class CompositeFtpFileSystem(IReadOnlyList<FtpCollection> collections) : IUnixFileSystem
{
    private static readonly IAccessMode ReadExecute = new GenericAccessMode(true, false, true);
    private static readonly IAccessMode AllAccess = new GenericAccessMode(true, true, true);

    public StringComparer FileSystemEntryComparer => StringComparer.Ordinal;

    public IUnixDirectoryEntry Root { get; } = new RootEntry();

    public bool SupportsAppend => true;

    public bool SupportsNonEmptyDirectoryDelete => false;

    public IReadOnlyList<FtpCollection> Collections => collections;

    public async Task<IReadOnlyList<IUnixFileSystemEntry>> GetEntriesAsync(IUnixDirectoryEntry directoryEntry, CancellationToken cancellationToken)
    {
        if (directoryEntry is RootEntry)
        {
            return collections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => (IUnixFileSystemEntry)new VfsDirectoryEntry(c, "", VfsEntry.Directory(c.Name))).ToList();
        }

        var dir = (VfsDirectoryEntry)directoryEntry;
        var entries = new List<IUnixFileSystemEntry>();
        await foreach (var child in dir.Collection.Fs.ListAsync(dir.Path, cancellationToken).ConfigureAwait(false))
        {
            entries.Add(Wrap(dir.Collection, VfsPath.Join(dir.Path, child.Name), child));
        }

        return entries;
    }

    public async Task<IUnixFileSystemEntry?> GetEntryByNameAsync(IUnixDirectoryEntry directoryEntry, string name, CancellationToken cancellationToken)
    {
        if (directoryEntry is RootEntry)
        {
            var collection = collections.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return collection is null ? null : new VfsDirectoryEntry(collection, "", VfsEntry.Directory(collection.Name));
        }

        var dir = (VfsDirectoryEntry)directoryEntry;
        var path = VfsPath.Join(dir.Path, name);
        var entry = await dir.Collection.Fs.StatAsync(path, cancellationToken).ConfigureAwait(false);
        return entry is null ? null : Wrap(dir.Collection, path, entry);
    }

    public async Task<Stream> OpenReadAsync(IUnixFileEntry fileEntry, long startPosition, CancellationToken cancellationToken)
    {
        var file = (VfsFileEntry)fileEntry;
        var handle = await file.Collection.Fs.OpenAsync(file.Path, VfsOpenMode.Read, cancellationToken).ConfigureAwait(false);
        return new VfsFileStream(handle, ownsFile: true) { Position = startPosition };
    }

    public async Task<IBackgroundTransfer?> CreateAsync(IUnixDirectoryEntry targetDirectory, string fileName, Stream data, CancellationToken cancellationToken)
    {
        var dir = Writable(targetDirectory);
        var path = VfsPath.Join(dir.Path, fileName);
        await using var handle = await dir.Collection.Fs.OpenAsync(path, VfsOpenMode.Create, cancellationToken).ConfigureAwait(false);
        await CopyAsync(data, handle, 0, cancellationToken).ConfigureAwait(false);
        return null;
    }

    public async Task<IBackgroundTransfer?> AppendAsync(IUnixFileEntry fileEntry, long? startPosition, Stream data, CancellationToken cancellationToken)
    {
        var file = (VfsFileEntry)fileEntry;
        await using var handle = await file.Collection.Fs.OpenAsync(file.Path, VfsOpenMode.ReadWrite, cancellationToken).ConfigureAwait(false);
        await CopyAsync(data, handle, startPosition ?? handle.Length, cancellationToken).ConfigureAwait(false);
        return null;
    }

    public async Task<IBackgroundTransfer?> ReplaceAsync(IUnixFileEntry fileEntry, Stream data, CancellationToken cancellationToken)
    {
        var file = (VfsFileEntry)fileEntry;
        await using var handle = await file.Collection.Fs.OpenAsync(file.Path, VfsOpenMode.Create, cancellationToken).ConfigureAwait(false);
        await CopyAsync(data, handle, 0, cancellationToken).ConfigureAwait(false);
        return null;
    }

    public async Task<IUnixDirectoryEntry> CreateDirectoryAsync(IUnixDirectoryEntry targetDirectory, string directoryName, CancellationToken cancellationToken)
    {
        var dir = Writable(targetDirectory);
        var path = VfsPath.Join(dir.Path, directoryName);
        await dir.Collection.Fs.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        var entry = await dir.Collection.Fs.StatAsync(path, cancellationToken).ConfigureAwait(false) ?? VfsEntry.Directory(directoryName);
        return new VfsDirectoryEntry(dir.Collection, path, entry);
    }

    public async Task<IUnixFileSystemEntry> MoveAsync(IUnixDirectoryEntry parent, IUnixFileSystemEntry source, IUnixDirectoryEntry target, string fileName, CancellationToken cancellationToken)
    {
        var from = (VfsEntryBase)source;
        var to = Writable(target);
        if (!ReferenceEquals(from.Collection, to.Collection))
        {
            throw VfsException.NotSupported("moving between collections");
        }

        if (from.Path.Length == 0)
        {
            throw VfsException.ReadOnly(from.Name, "a collection cannot be renamed through FTP");
        }

        var toPath = VfsPath.Join(to.Path, fileName);
        await from.Collection.Fs.RenameAsync(from.Path, toPath, replace: true, cancellationToken).ConfigureAwait(false);
        var entry = await from.Collection.Fs.StatAsync(toPath, cancellationToken).ConfigureAwait(false) ?? from.Entry with { Name = fileName };
        return Wrap(from.Collection, toPath, entry);
    }

    public Task UnlinkAsync(IUnixFileSystemEntry entry, CancellationToken cancellationToken)
    {
        var item = (VfsEntryBase)entry;
        if (item.Path.Length == 0)
        {
            throw VfsException.ReadOnly(item.Name, "a collection cannot be deleted through FTP");
        }

        return item.Collection.Fs.DeleteAsync(item.Path, cancellationToken);
    }

    public async Task<IUnixFileSystemEntry> SetMacTimeAsync(IUnixFileSystemEntry entry, DateTimeOffset? modify, DateTimeOffset? access, DateTimeOffset? create, CancellationToken cancellationToken)
    {
        var item = (VfsEntryBase)entry;
        if (item.Path.Length == 0)
        {
            return entry;
        }

        await item.Collection.Fs.SetTimesAsync(item.Path, create, modify, cancellationToken).ConfigureAwait(false);
        var refreshed = await item.Collection.Fs.StatAsync(item.Path, cancellationToken).ConfigureAwait(false) ?? item.Entry;
        return Wrap(item.Collection, item.Path, refreshed);
    }

    private static VfsDirectoryEntry Writable(IUnixDirectoryEntry directory)
        => directory as VfsDirectoryEntry ?? throw VfsException.ReadOnly("/", "the server root only lists collections; create things inside one");

    private static IUnixFileSystemEntry Wrap(FtpCollection collection, string path, VfsEntry entry)
        => entry.IsDirectory ? new VfsDirectoryEntry(collection, path, entry) : new VfsFileEntry(collection, path, entry);

    private static async Task CopyAsync(Stream data, IVfsFile handle, long offset, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        int n;
        while ((n = await data.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await handle.WriteAsync(offset, buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            offset += n;
        }

        await handle.FlushAsync(ct).ConfigureAwait(false);
    }

    internal abstract class VfsEntryBase(FtpCollection collection, string path, VfsEntry entry) : IUnixFileSystemEntry
    {
        public FtpCollection Collection => collection;

        public string Path => path;

        public VfsEntry Entry => entry;

        public string Name => path.Length == 0 ? collection.Name : entry.Name;

        public IUnixPermissions Permissions
        {
            get
            {
                var writable = path.Length > 0 && collection.Fs.CanWrite(path);
                var mode = new GenericAccessMode(true, writable, entry.IsDirectory);
                return new GenericUnixPermissions(mode, mode, mode);
            }
        }

        public DateTimeOffset? LastWriteTime => entry.Modified;

        public DateTimeOffset? CreatedTime => entry.Created;

        public long NumberOfLinks => 1;

        public string Owner => "anyfs";

        public string Group => "anyfs";
    }

    internal sealed class VfsDirectoryEntry(FtpCollection collection, string path, VfsEntry entry) : VfsEntryBase(collection, path, entry), IUnixDirectoryEntry
    {
        public bool IsRoot => false;

        public bool IsDeletable => Path.Length > 0;
    }

    internal sealed class VfsFileEntry(FtpCollection collection, string path, VfsEntry entry) : VfsEntryBase(collection, path, entry), IUnixFileEntry
    {
        public long Size => Entry.Length;
    }

    /// <summary>The server root: lists the collections, takes no writes.</summary>
    internal sealed class RootEntry : IUnixDirectoryEntry
    {
        public bool IsRoot => true;

        public bool IsDeletable => false;

        public string Name => "";

        public IUnixPermissions Permissions { get; } = new GenericUnixPermissions(ReadExecute, ReadExecute, ReadExecute);

        public DateTimeOffset? LastWriteTime => null;

        public DateTimeOffset? CreatedTime => null;

        public long NumberOfLinks => 1;

        public string Owner => "anyfs";

        public string Group => "anyfs";
    }
}
