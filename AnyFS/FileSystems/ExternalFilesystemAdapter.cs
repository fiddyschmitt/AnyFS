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

        //Windows doesn't show dates earlier than 1/1/1980
        static readonly DateTime MinDate = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public void Initialise(string command, string args)
        {
            var psi = new ProcessStartInfo(command, args)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(command)
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

                    if (result != null)
                    {
                        if (result.CreatedUTC < MinDate) result.CreatedUTC = MinDate;
                        if (result.ModifiedUTC < MinDate) result.ModifiedUTC = MinDate;
                        if (result.AccessedUTC < MinDate) result.AccessedUTC = MinDate;
                    }

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

                    if (result != null)
                    {
                        if (result.CreatedUTC < MinDate) result.CreatedUTC = MinDate;
                        if (result.ModifiedUTC < MinDate) result.ModifiedUTC = MinDate;
                        if (result.AccessedUTC < MinDate) result.AccessedUTC = MinDate;
                    }

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
                //check if the requested path is actually a folder
                var f = GetFolder(path);
                if (f == null) return [];

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

                    foreach (var fileEntry in result)
                    {
                        if (fileEntry.CreatedUTC < MinDate) fileEntry.CreatedUTC = MinDate;
                        if (fileEntry.ModifiedUTC < MinDate) fileEntry.ModifiedUTC = MinDate;
                        if (fileEntry.AccessedUTC < MinDate) fileEntry.AccessedUTC = MinDate;
                    };

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
                //check if the requested path is actually a folder
                var f = GetFolder(path);
                if (f == null) return [];

                //externalProc.StandardInput.WriteLine($"get folders: {path}");
                sendUtil.WriteLine($"get folders: {path}");

                var responseJson = externalProc.StandardOutput.ReadLine();

                if (string.IsNullOrEmpty(responseJson)) return [];

                try
                {
                    var result = responseJson.FromJson<List<Folder>>() ?? [];

                    foreach (var folder in result)
                    {
                        if (folder.CreatedUTC < MinDate) folder.CreatedUTC = MinDate;
                        if (folder.ModifiedUTC < MinDate) folder.ModifiedUTC = MinDate;
                        if (folder.AccessedUTC < MinDate) folder.AccessedUTC = MinDate;
                    };

                    return result;
                }
                catch (Exception ex)
                {
                    Debugger.Break();
                }

                return [];
            }
        }

        //public Stream Download(string path)
        //{
        //    if (externalProc == null) return Stream.Null;

        //    lock (externalProc)
        //    {
        //        var fileInfo = GetFile(path);
        //        if (fileInfo == null || fileInfo.Size == 0)
        //        {
        //            return Stream.Null;
        //        }

        //        //externalProc.StandardInput.WriteLine($"download file: {path}");
        //        sendUtil.WriteLine($"download file as base64: {path}");

        //        var sizeStr = externalProc.StandardOutput.ReadLine();
        //        var size = long.Parse(sizeStr);

        //        var tempFilePath = Path.GetTempFileName();
        //        var fs = new FileStreamDeleteOnClose(tempFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        //        var totalRead = 0L;
        //        while (true)
        //        {
        //            var str = externalProc.StandardOutput.ReadLine();
        //            var bytes = Convert.FromBase64String(str);

        //            fs.Write(bytes);
        //            totalRead += bytes.Length;

        //            if (totalRead == size) break;
        //        }

        //        fs.Seek(0, SeekOrigin.Begin);

        //        return fs;
        //    }
        //}

        //public Stream Download(string path)
        //{
        //    if (externalProc == null) return Stream.Null;

        //    lock (externalProc)
        //    {
        //        //externalProc.StandardInput.WriteLine($"download file: {path}");
        //        sendUtil.WriteLine($"download file: {path}");

        //        var sizeStr = externalProc.StandardOutput.ReadLine();
        //        var stdStreams = new StandardStreams(externalProc.StandardOutput.BaseStream, Stream.Null);

        //        var size = long.Parse(sizeStr);

        //        var tempFilePath = Path.GetTempFileName();
        //        var fs = new FileStreamDeleteOnClose(tempFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        //        //externalProc.StandardOutput.BaseStream.CopyTo(fs, size, 65535);

        //        stdStreams.CopyTo(fs, size, 65535);
        //        stdStreams.Stop();

        //        fs.Seek(0, SeekOrigin.Begin);

        //        return fs;
        //    }
        //}

        public Stream Download(string path)
        {
            if (externalProc == null) return Stream.Null;

            lock (externalProc)
            {
                try
                {
                    //externalProc.StandardInput.WriteLine($"download file: {path}");
                    sendUtil.WriteLine($"download file: {path}");

                    var sizeStr = externalProc.StandardOutput.BaseStream.ReadLine();

                    //var stdStreams = new StandardStreams(externalProc.StandardOutput.BaseStream, Stream.Null);
                    //var sizeStr = stdStreams.ReadLine();    //use this extension method because using a TextReader consumes more bytes than the line, eating into the file contents

                    var size = long.Parse(sizeStr.ToString());

                    var tempFilePath = Path.GetTempFileName();
                    var fs = new FileStreamDeleteOnClose(tempFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

                    externalProc.StandardOutput.BaseStream.CopyTo(fs, size, 65535);

                    //stdStreams.CopyTo(fs, size, 65535);
                    //stdStreams.Stop();

                    fs.Seek(0, SeekOrigin.Begin);

                    return fs;
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    return Stream.Null;
                }
            }
        }
    }
}
