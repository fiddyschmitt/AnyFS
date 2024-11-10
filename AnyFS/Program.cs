using AnyFS.FileSystems;
using AnyFS.MountProviders.FTP;
using AnyFS.MountProviders.WebDAV;
using libCommon;
using libVirtualFileSystem.Folders;
using mirror.Files;
using System.IO.Enumeration;

namespace AnyFS
{
    internal class Program
    {
        static void Main(string[] args)
        {
            //var mirror = new MirrorFolder(@"C:\Temp");
            //var fileInfo = mirror.GetFile("1.dat");
            //var fileContent = fileInfo.GetStream();

            //var fsProvider = new MirrorFolder(@"C:\Temp");
            //var fol = fsProvider.GetFolder("2024-04-28");
            //var folStr = fol.ToJson(true);
            //Console.WriteLine();

            var fsProvider = new ExternalFilesystemAdapter();
            fsProvider.Initialise(@"C:\Users\Smith\Desktop\dev\cs\AnyFS\mirror\bin\Debug\net8.0\mirror.exe", "");
            //var files = fsProvider.GetFiles("");
            //var filesStr = files.ToJson(true);
            //Console.WriteLine();

            //var ftp = new FTP();
            //ftp.Start(args, mirrorFs);

            var webdavMountProvider = new WebDAVMountProvider();
            webdavMountProvider.Start(args, fsProvider);
        }

        //static void Main(string[] args)
        //{
        //    var mirrorFs = new Mirror(@"C:\Temp");

        //    var rootFolder = mirrorFs.Resolve("") as Folder;

        //    rootFolder
        //        .Children
        //        .RecurseList(child =>
        //        {
        //            if (child is Folder subfolder)
        //            {
        //                var subFolderResolved = mirrorFs.Resolve(child.Name) as Folder;

        //                return subFolderResolved.Children;
        //            }

        //            return [];
        //        })
        //        .ToList()
        //        .ForEach(item =>
        //        {
        //            Console.WriteLine(item.FullPath);
        //        });
        //}
    }
}
