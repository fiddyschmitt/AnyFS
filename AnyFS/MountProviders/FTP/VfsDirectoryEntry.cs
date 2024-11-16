using FubarDev.FtpServer.FileSystem;
using FubarDev.FtpServer.FileSystem.Generic;
using libVirtualFileSystem;
using libVirtualFileSystem.Files;
using libVirtualFileSystem.Folders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnyFS.MountProviders.FTP
{
    public class VfsDirectoryEntry : IUnixDirectoryEntry
    {
        public VfsDirectoryEntry(Folder folder)
        {
            Folder = folder;

            var accessMode = new GenericAccessMode(true, true, true);
            Permissions = new GenericUnixPermissions(accessMode, accessMode, accessMode);
        }

        public bool IsRoot => string.IsNullOrEmpty(Folder.Name);

        public bool IsDeletable => false;

        public string Name => Folder.Name;

        public IUnixPermissions Permissions { get; private set; }

        public DateTimeOffset? LastWriteTime => Folder.ModifiedUTC;

        public DateTimeOffset? CreatedTime => Folder.CreatedUTC;

        public long NumberOfLinks => throw new NotImplementedException();

        public string Owner => null;

        public string Group => null;

        public Folder Folder { get; }
    }
}
