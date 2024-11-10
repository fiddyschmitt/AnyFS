using libVirtualFileSystem.Folders;
using mirror.Files;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace mirror.Folders
{
    public class FolderBackedByRealFolder : Folder
    {
        public FolderBackedByRealFolder(string fullRealPath, string fullVirtualPath)
        {
            var di = new DirectoryInfo(fullRealPath);

            Name = di.Name;
            FullPath = fullVirtualPath;

            Created = di.CreationTime;
            Modified = di.LastWriteTime;
            Accessed = di.LastAccessTime;
        }
    }
}
