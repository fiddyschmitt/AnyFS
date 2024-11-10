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
        public string? Name { get; init; }
        public string? FullPath { get; init; }

        public DateTime Modified { get; init; }
        public DateTime Created { get; init; }
        public DateTime Accessed { get; init; }
    }
}
