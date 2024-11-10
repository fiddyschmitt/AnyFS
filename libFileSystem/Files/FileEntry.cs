using libVirtualFileSystem.Folders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libVirtualFileSystem.Files
{
    public class FileEntry
    {
        public string? Name { get; init; }
        public string? FullPath { get; init; }
        public long Size { get; init; }

        public DateTime Modified { get; init; }
        public DateTime Created { get; init; }
        public DateTime Accessed { get; init; }
    }
}
