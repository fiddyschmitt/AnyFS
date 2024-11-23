using libVirtualFileSystem.Folders;
using libVirtualFileSystem.MountProviders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using NWebDav.Server;
using AnyFS.MountProviders.WebDAV.Stores;
using NWebDav.Server.Stores;
using libVirtualFileSystem.FileSystemAdapters;
using Microsoft.Extensions.Logging;

namespace AnyFS.MountProviders.WebDAV
{
    public class WebDAVMountProvider : IMountProvider
    {
        public void Start(string[] args, IFileSystemAdapter fileSystemAdapter)
        {
            args = ["--urls", "http://localhost:12000"];

            //var builder = WebApplication.CreateBuilder(args);
            //builder.Services
            //    .AddNWebDav()
            //    .AddDiskStore(cfg =>
            //    {
            //        cfg.BaseDirectory = @"C:\Temp";
            //    });

            //var app = builder.Build();
            //app.UseNWebDav();
            //app.Run();

            var builder = WebApplication.CreateBuilder(args);
            //builder.Logging.SetMinimumLevel(LogLevel.Error);

            builder.Services
                .AddNWebDav()
                .AddAnyFsStore(cfg =>
                {
                    cfg.FileSystemAdapter = fileSystemAdapter;
                });

            var app = builder.Build();
            
            app.UseNWebDav();
            app.Run();
        }

        public void Mount(Folder folder)
        {

        }
    }
}
