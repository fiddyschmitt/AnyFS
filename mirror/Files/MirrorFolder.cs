using libVirtualFileSystem;
using libVirtualFileSystem.Files;
using libVirtualFileSystem.FileSystemAdapters;
using libVirtualFileSystem.Folders;
using mirror.Folders;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace mirror.Files
{
    public class MirrorFolder : IFileSystemAdapter
    {
        public MirrorFolder(string rootFolder)
        {
            RootFolder = rootFolder;
        }

        public string RootFolder { get; }



        public void Initialise(string command, string args)
        {

        }

        public Folder? GetFolder(string path)
        {
            var fullRealPath = Path.Combine(RootFolder, path);

            if (Directory.Exists(fullRealPath))
            {
                try
                {
                    var folder = new FolderBackedByRealFolder(fullRealPath, path);
                    return folder;
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        public FileEntry? GetFile(string path)
        {
            var fullRealPath = Path.Combine(RootFolder, path);

            if (File.Exists(fullRealPath))
            {
                try
                {
                    var file = new FileBackedByRealFile(fullRealPath, path);
                    return file;
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        public List<FileEntry> GetFiles(string path)
        {
            var fullRealPath = Path.Combine(RootFolder, path);

            try
            {
                var files = Directory
                            .GetFiles(fullRealPath)
                            .Select(fullFilename =>
                            {
                                try
                                {
                                    var filename = Path.GetFileName(fullFilename);
                                    return new FileBackedByRealFile(fullFilename, Path.Combine(path, filename));
                                }
                                catch
                                {
                                    return null;
                                }
                            })
                            .Where(file => file != null)
                            .OfType<FileEntry>()
                            .ToList();

                return files;
            }
            catch (Exception ex)
            {
                return [];
            }


            return [];
        }

        public List<Folder> GetFolders(string path)
        {
            var fullRealPath = Path.Combine(RootFolder, path);

            try
            {
                var folders = Directory
                        .GetDirectories(fullRealPath)
                        .Select(fullFolderName =>
                        {
                            var folderName = Path.GetFileName(fullFolderName);
                            try
                            {
                                var folder = new FolderBackedByRealFolder(fullFolderName, Path.Combine(path, folderName));
                                return folder;
                            }
                            catch
                            {
                                return null;
                            }
                        })
                        .Where(folder => folder != null)
                        .OfType<Folder>()
                        .ToList();

                return folders;
            }
            catch (Exception ex)
            {
                //Console.WriteLine(ex.Message);
            }

            return [];
        }

        public Stream Download(string path)
        {
            try
            {
                var fullRealPath = Path.Combine(RootFolder, path);

                var result = File.OpenRead(fullRealPath);
                return result;
            }
            catch (Exception ex)
            {
                return Stream.Null;
            }

            return Stream.Null;
        }
    }
}
