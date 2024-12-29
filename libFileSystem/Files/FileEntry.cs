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
        public string? FullPath { get; set; }
        public long Size { get; init; }

        public DateTime ModifiedUTC { get; set; }
        public DateTime CreatedUTC { get; set; }
        public DateTime AccessedUTC { get; set; }
    }
}
