using libVirtualFileSystem.Files;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libVirtualFileSystem.Folders
{
    public class Folder
    {
        public string? Name { get; set; }
        public string? FullPath { get; set; }

        public DateTime ModifiedUTC { get; set; }
        public DateTime CreatedUTC { get; set; }
        public DateTime AccessedUTC { get; set; }
    }
}
