using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using libVirtualFileSystem.FileSystemAdapters;
using libVirtualFileSystem.Folders;
using libVirtualFileSystem.MountProviders;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnyFS.MountProviders.FTP
{
    public class FTP : IMountProvider
    {
        public void Start(string[] args, IFileSystemAdapter fileSystemAdapter)
        {
            // Setup dependency injection
            var services = new ServiceCollection();

            // Add FTP server services
            services.AddFtpServer(builder =>
            {
                builder
                    .UseVFS();
                //.EnableAnonymousAuthentication()

                builder.Services.AddSingleton<IMembershipProvider, AnonymousMembershipProvider>();
            });

            services.Configure<VfsOptions>(opt =>
            {
                opt.FileSystemAdapter = fileSystemAdapter;
            });

            // Configure the FTP server
            services.Configure<FtpServerOptions>(opt =>
            {
                opt.ServerAddress = "127.0.0.1";
            });

            // Build the service provider
            using (var serviceProvider = services.BuildServiceProvider())
            {
                // Initialize the FTP server
                var ftpServerHost = serviceProvider.GetRequiredService<IFtpServerHost>();

                // Start the FTP server
                ftpServerHost.StartAsync(CancellationToken.None).Wait();

                Console.WriteLine("Press ENTER/RETURN to close the test application.");
                Console.ReadLine();

                // Stop the FTP server
                ftpServerHost.StopAsync(CancellationToken.None).Wait();
            }
        }
    }
}
