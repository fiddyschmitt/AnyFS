using FubarDev.FtpServer;
using FubarDev.FtpServer.FileSystem;
using libVirtualFileSystem.FileSystemAdapters;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnyFS.MountProviders.FTP
{
    public class VfsProvider : IFileSystemClassFactory
    {
        public VfsProvider(IOptions<VfsOptions> options)
        {
            Options = options;
        }

        public IOptions<VfsOptions> Options { get; }

        public Task<IUnixFileSystem> Create(IAccountInformation accountInformation)
        {
            var vfs = new Vfs(Options.Value.FileSystemAdapter);
            return Task.FromResult<IUnixFileSystem>(vfs);
        }
    }
}
