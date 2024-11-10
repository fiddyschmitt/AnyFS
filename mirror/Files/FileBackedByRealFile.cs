using libVirtualFileSystem.Files;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace mirror.Files
{
    public class FileBackedByRealFile : FileEntry
    {
        public FileBackedByRealFile(string fullRealPath, string fullVirtualPath)
        {
            Name = Path.GetFileName(fullRealPath);
            FullPath = fullVirtualPath;

            var fileInfo = new FileInfo(fullRealPath);
            Size = fileInfo.Length;
            Created = fileInfo.CreationTime;
            Modified = fileInfo.LastWriteTime;
            Accessed = fileInfo.LastAccessTime;
        }
    }
}
