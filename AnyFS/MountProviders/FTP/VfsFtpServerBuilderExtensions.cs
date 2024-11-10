using FubarDev.FtpServer.FileSystem;
using FubarDev.FtpServer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using libVirtualFileSystem.FileSystemAdapters;

namespace AnyFS.MountProviders.FTP
{
    public static class VFSFtpServerBuilderExtensions
    {
        public static IFtpServerBuilder UseVFS(this IFtpServerBuilder builder)
        {
            builder
                .Services
                .AddSingleton<IFileSystemClassFactory, VfsProvider>();

            return builder;
        }
    }
}
