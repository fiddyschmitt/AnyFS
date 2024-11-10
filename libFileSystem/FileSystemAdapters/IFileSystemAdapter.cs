using libVirtualFileSystem.Files;
using libVirtualFileSystem.Folders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libVirtualFileSystem.FileSystemAdapters
{
    public interface IFileSystemAdapter
    {
        void Initialise(string command, string args);


        FileEntry? GetFile(string path);
        Folder? GetFolder(string path);


        List<FileEntry> GetFiles(string path);
        List<Folder> GetFolders(string path);

        public Stream Download(string path);
    }
}
