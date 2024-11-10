using libCommon;
using libCommon.Streams;
using libVirtualFileSystem;
using libVirtualFileSystem.Files;
using libVirtualFileSystem.FileSystemAdapters;
using libVirtualFileSystem.Folders;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnyFS.FileSystems
{
    public class ExternalFilesystemAdapter : IFileSystemAdapter
    {
        Process? externalProc;
        SendUtil? sendUtil;

        public void Initialise(string command, string args)
        {
            var psi = new ProcessStartInfo(command, args)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true
            };
            externalProc = Process.Start(psi);

            sendUtil = new SendUtil(externalProc.StandardInput);
        }

        public FileEntry? GetFile(string path)
        {
            if (externalProc == null) return null;

            lock (externalProc)
            {
                //externalProc.StandardInput.WriteLine($"get file: {path}");
                sendUtil.WriteLine($"get file: {path}");

                var responseJson = externalProc.StandardOutput.ReadLine();

                if (string.IsNullOrEmpty(responseJson)) return null;
                if (responseJson == "null") return null;

                try
                {
                    var result = responseJson.FromJson<FileEntry>();

                    return result;
                }
                catch
                {
                    //Debugger.Break();
                }

                return null;
            }
        }

        public Folder? GetFolder(string path)
        {
            if (externalProc == null) return null;

            lock (externalProc)
            {
                //externalProc.StandardInput.WriteLine($"get folder: {path}");
                sendUtil.WriteLine($"get folder: {path}");

                var responseJson = externalProc.StandardOutput.ReadLine();

                if (string.IsNullOrEmpty(responseJson)) return null;

                try
                {
                    var result = responseJson.FromJson<Folder>();

                    return result;
                }
                catch
                {
                    //Debugger.Break();
                }

                return null;
            }
        }

        public List<FileEntry> GetFiles(string path)
        {
            if (externalProc == null) return [];

            lock (externalProc)
            {
                //externalProc.StandardInput.WriteLine($"get files: {path}");
                sendUtil.WriteLine($"get files: {path}");

                var responseJson = externalProc.StandardOutput.ReadLine();

                if (string.IsNullOrEmpty(responseJson)) return [];

                var remoteFiles = responseJson.FromJson<List<FileEntry>>() ?? [];

                try
                {
                    var result = remoteFiles
                                    .OfType<FileEntry>()
                                    .ToList();

                    return result;
                }
                catch
                {
                    Debugger.Break();
                }

                return [];
            }
        }

        public List<Folder> GetFolders(string path)
        {
            if (externalProc == null) return [];

            lock (externalProc)
            {
                //externalProc.StandardInput.WriteLine($"get folders: {path}");
                sendUtil.WriteLine($"get folders: {path}");

                var responseJson = externalProc.StandardOutput.ReadLine();

                if (string.IsNullOrEmpty(responseJson)) return [];

                try
                {
                    var result = responseJson.FromJson<List<Folder>>() ?? [];

                    return result;
                }
                catch
                {
                    //Debugger.Break();
                }

                return [];
            }
        }

        public Stream Download(string path)
        {
            if (externalProc == null) return Stream.Null;

            lock (externalProc)
            {
                //externalProc.StandardInput.WriteLine($"download file: {path}");
                sendUtil.WriteLine($"download file as base64: {path}");

                var sizeStr = externalProc.StandardOutput.ReadLine();
                var size = long.Parse(sizeStr);

                var tempFilePath = Path.GetTempFileName();
                var fs = new FileStreamDeleteOnClose(tempFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

                var totalRead = 0L;
                while (true)
                {
                    var str = externalProc.StandardOutput.ReadLine();
                    var bytes = Convert.FromBase64String(str);

                    fs.Write(bytes);
                    totalRead += bytes.Length;

                    if (totalRead == size) break;
                }

                fs.Seek(0, SeekOrigin.Begin);

                return fs;
            }
        }

        //public Stream Download(string path)
        //{
        //    if (externalProc == null) return Stream.Null;

        //    lock (externalProc)
        //    {
        //        //externalProc.StandardInput.WriteLine($"download file: {path}");
        //        sendUtil.WriteLine($"download file: {path}");

        //        var sizeStr = externalProc.StandardOutput.ReadLine();
        //        var size = long.Parse(sizeStr);

        //        var tempFilePath = Path.GetTempFileName();
        //        var fs = new FileStreamDeleteOnClose(tempFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        //        //externalProc.StandardOutput.BaseStream.CopyTo(fs, size, 65535);
        //        var stdStreams = new StandardStreams(externalProc.StandardOutput.BaseStream, Stream.Null);
        //        stdStreams.CopyTo(fs, size, 65535);

        //        stdStreams.Stop();

        //        fs.Seek(0, SeekOrigin.Begin);

        //        return fs;
        //    }
        //}
    }
}
