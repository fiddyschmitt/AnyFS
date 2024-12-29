using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace gitlab.Gitlab.Model
{
    public class Project
    {
        public int Id;
        public DateTime CreatedUTC;
        public DateTime UpdatedUTC;
        public string Description;
        public string Name;
        public string Path;
        public string Namespace;
        public string NamespacePath;
    }
}
