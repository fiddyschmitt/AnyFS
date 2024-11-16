using libVirtualFileSystem.Files;
using libVirtualFileSystem.FileSystemAdapters;
using libVirtualFileSystem.Folders;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnyFS.FileSystems
{
    public class MultiWorker : IFileSystemAdapter
    {
        BlockingCollection<IFileSystemAdapter> Workers = [];

        public void Initialise(string command, string args)
        {

        }

        public void UseWorkers(List<IFileSystemAdapter> workers)
        {
            workers
                .ForEach(worker => Workers.Add(worker));
        }

        public Stream Download(string path)
        {
            var worker = Workers.Take();

            Stream? result = null;
            try
            {
                result = worker.Download(path);
            }
            catch (Exception ex)
            {
                Debugger.Break();
            }
            finally
            {
                Workers.Add(worker);
            }

            result ??= Stream.Null;

            return result;
        }

        public FileEntry? GetFile(string path)
        {
            var worker = Workers.Take();

            FileEntry? result = null;
            try
            {
                result = worker.GetFile(path);
            }
            catch (Exception ex)
            {
                Debugger.Break();
            }
            finally
            {
                Workers.Add(worker);
            }

            return result;
        }

        public List<FileEntry> GetFiles(string path)
        {
            var worker = Workers.Take();

            List<FileEntry>? result = null;
            try
            {
                result = worker.GetFiles(path);
            }
            catch (Exception ex)
            {
                Debugger.Break();
            }
            finally
            {
                Workers.Add(worker);
            }

            result ??= [];

            return result;
        }

        public Folder? GetFolder(string path)
        {
            var worker = Workers.Take();

            Folder? result = null;
            try
            {
                result = worker.GetFolder(path);
            }
            catch (Exception ex)
            {
                Debugger.Break();
            }
            finally
            {
                Workers.Add(worker);
            }

            return result;
        }

        public List<Folder> GetFolders(string path)
        {
            var worker = Workers.Take();

            List<Folder>? result = null;
            try
            {
                result = worker.GetFolders(path);
            }
            catch (Exception ex)
            {
                Debugger.Break();
            }
            finally
            {
                Workers.Add(worker);
            }

            result ??= [];

            return result;
        }
    }
}
