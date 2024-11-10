using libVirtualFileSystem.FileSystemAdapters;
using libVirtualFileSystem.Folders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libVirtualFileSystem.MountProviders
{
    public interface IMountProvider
    {
        void Start(string[] args, IFileSystemAdapter fileSystemAdapter);
    }
}
