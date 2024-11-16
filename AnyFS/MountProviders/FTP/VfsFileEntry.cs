using FubarDev.FtpServer.FileSystem;
using FubarDev.FtpServer.FileSystem.Generic;
using libVirtualFileSystem;
using libVirtualFileSystem.Files;
using libVirtualFileSystem.FileSystemAdapters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnyFS.MountProviders.FTP
{
    public class VfsFileEntry : IUnixFileEntry
    {
        public VfsFileEntry(FileEntry fileEntry, IFileSystemAdapter fileSystemAdapter)
        {
            FileEntry = fileEntry;
            FileSystemAdapter = fileSystemAdapter;
            var accessMode = new GenericAccessMode(true, true, true);
            Permissions = new GenericUnixPermissions(accessMode, accessMode, accessMode);
        }

        public bool IsRoot => string.IsNullOrEmpty(FileEntry.Name);

        public bool IsDeletable => false;

        public string Name => FileEntry.Name;

        public IUnixPermissions Permissions { get; private set; }

        public DateTimeOffset? LastWriteTime => FileEntry.ModifiedUTC;

        public DateTimeOffset? CreatedTime => FileEntry.CreatedUTC;

        public long NumberOfLinks => throw new NotImplementedException();

        public string Owner => null;

        public string Group => null;

        public FileEntry FileEntry { get; }
        public IFileSystemAdapter FileSystemAdapter { get; }

        public long Size => FileEntry.Size;

        public Stream OpenRead()
        {
            var result = FileSystemAdapter.Download(FileEntry.FullPath);

            return result;
        }
    }
}
