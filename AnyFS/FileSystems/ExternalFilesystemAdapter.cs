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
        //StandardStreams? standardStreams;

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
            //standardStreams = new StandardStreams(externalProc.StandardOutput.BaseStream, Stream.Null);
        }

        readonly Semaphore transferInProgress = new Semaphore(1, 1);

        public FileEntry? GetFile(string path)
        {
            if (externalProc == null) return null;

            lock (externalProc)
            {
                //externalProc.StandardInput.WriteLine($"get file: {path}");
                sendUtil.WriteLine($"get file: {path}");

                var responseJson = "";
                transferInProgress.WaitOne();
                try
                {
                    responseJson = externalProc.StandardOutput.ReadLine();
                }
                finally
                {
                    transferInProgress.Release();
                }


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
                catch (Exception ex)
                {
                    Debugger.Break();
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

                var responseJson = "";
                transferInProgress.WaitOne();
                try
                {
                    responseJson = externalProc.StandardOutput.ReadLine();
                }
                finally
                {
                    transferInProgress.Release();
                }

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
                catch (Exception ex)
                {
                    Debugger.Break();
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

                var responseJson = "";
                transferInProgress.WaitOne();
                try
                {
                    responseJson = externalProc.StandardOutput.ReadLine();
                }
                finally
                {
                    transferInProgress.Release();
                }

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
                catch (Exception ex)
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

                var responseJson = "";
                transferInProgress.WaitOne();
                try
                {
                    responseJson = externalProc.StandardOutput.ReadLine();
                }
                finally
                {
                    transferInProgress.Release();
                }

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

        public Stream Download(string path)
        {
            if (externalProc == null) return Stream.Null;

            lock (externalProc)
            {
                try
                {
                    //externalProc.StandardInput.WriteLine($"download file: {path}");
                    sendUtil.WriteLine($"download file: {path}");

                    transferInProgress.WaitOne();
                    var sizeStr = externalProc.StandardOutput.BaseStream.ReadLine();
                    var size = long.Parse(sizeStr.ToString());

                    //var tempFilePath = Path.GetTempFileName();
                    //var result = new FileStreamDeleteOnClose(tempFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    //externalProc.StandardOutput.BaseStream.CopyTo(result, size, 65535);
                    //result.Seek(0, SeekOrigin.Begin);
                    //transferInProgress.Release();

                    var result = new SubStream(externalProc.StandardOutput.BaseStream, 0, size);
                    result.Closed += (s, a) =>
                    {
                        transferInProgress.Release();
                    };

                    return result;
                }
                catch (Exception ex)
                {
                    Debugger.Break();
                    return Stream.Null;
                }
            }
        }
    }
}
