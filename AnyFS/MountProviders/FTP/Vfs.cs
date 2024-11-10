using FubarDev.FtpServer.BackgroundTransfer;
using FubarDev.FtpServer.FileSystem;
using libVirtualFileSystem.Files;
using libVirtualFileSystem.FileSystemAdapters;
using libVirtualFileSystem.Folders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnyFS.MountProviders.FTP
{
    public class Vfs : IUnixFileSystem
    {
        IUnixDirectoryEntry root;

        public Vfs(IFileSystemAdapter fileSystemAdapter)
        {
            FileSystemAdapter = fileSystemAdapter;

            var folderInfo = fileSystemAdapter.GetFolder("");

            root = new VfsDirectoryEntry(folderInfo);
        }

        public bool SupportsAppend => false;

        public bool SupportsNonEmptyDirectoryDelete => false;

        public StringComparer FileSystemEntryComparer { get; }

        public IUnixDirectoryEntry Root => root;

        public IFileSystemAdapter FileSystemAdapter { get; }

        public Task<IBackgroundTransfer?> AppendAsync(IUnixFileEntry fileEntry, long? startPosition, Stream data, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<IBackgroundTransfer?> CreateAsync(IUnixDirectoryEntry targetDirectory, string fileName, Stream data, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<IUnixDirectoryEntry> CreateDirectoryAsync(IUnixDirectoryEntry targetDirectory, string directoryName, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<IReadOnlyList<IUnixFileSystemEntry>> GetEntriesAsync(IUnixDirectoryEntry directoryEntry, CancellationToken cancellationToken)
        {
            var subfolders = FileSystemAdapter
                                .GetFolders(directoryEntry.Name)
                                .Select(fol => new VfsDirectoryEntry(fol))
                                .Cast<IUnixFileSystemEntry>()
                                .ToList();

            var files = FileSystemAdapter
                            .GetFiles(directoryEntry.Name)
                            .Select(file => new VfsFileEntry(file, FileSystemAdapter))
                            .Cast<IUnixFileSystemEntry>()
                            .ToList();

            IReadOnlyList<IUnixFileSystemEntry> result = subfolders
                                                            .Concat(files)
                                                            .ToList();

            return Task.FromResult(result);
        }

        public Task<IUnixFileSystemEntry?> GetEntryByNameAsync(IUnixDirectoryEntry directoryEntry, string name, CancellationToken cancellationToken)
        {
            var folder = FileSystemAdapter.GetFolder(directoryEntry.Name);
            if (folder != null)
            {
                IUnixFileSystemEntry result = new VfsDirectoryEntry(folder);
                return Task.FromResult(result);
            }

            var file = FileSystemAdapter.GetFile(directoryEntry.Name);
            if (file != null)
            {
                IUnixFileSystemEntry result = new VfsFileEntry(file, FileSystemAdapter);
                return Task.FromResult(result);
            }

            return Task.FromResult<IUnixFileSystemEntry>(null);
        }

        public Task<IUnixFileSystemEntry> MoveAsync(IUnixDirectoryEntry parent, IUnixFileSystemEntry source, IUnixDirectoryEntry target, string fileName, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<Stream> OpenReadAsync(IUnixFileEntry fileEntry, long startPosition, CancellationToken cancellationToken)
        {
            Stream result = Stream.Null;
            if (fileEntry is VfsFileEntry vfsEntry)
            {
                result = vfsEntry.OpenRead();
            }

            return Task.FromResult(result);
        }

        public Task<IBackgroundTransfer?> ReplaceAsync(IUnixFileEntry fileEntry, Stream data, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<IUnixFileSystemEntry> SetMacTimeAsync(IUnixFileSystemEntry entry, DateTimeOffset? modify, DateTimeOffset? access, DateTimeOffset? create, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task UnlinkAsync(IUnixFileSystemEntry entry, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
