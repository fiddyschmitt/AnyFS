using libVirtualFileSystem.FileSystemAdapters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Adapters
{
    public class StandardStreamsListener
    {
        public StandardStreamsListener(IFileSystemAdapter adapter)
        {
            Adapter = adapter;
        }

        public IFileSystemAdapter Adapter { get; }

        public void Start()
        {
            var stdout = Console.OpenStandardOutput();

            while (true)
            {
                var commandStr = Console.ReadLine();

                if (string.IsNullOrEmpty(commandStr))
                {
                    break;
                }

                var tokens = commandStr.Split([":"], StringSplitOptions.TrimEntries);

                if (tokens.Length == 2)
                {
                    var command = tokens[0];

                    if (command == "get file")
                    {
                        var file = Adapter.GetFile(tokens[1]);

                        var fileJson = file.ToJson(false);
                        Console.WriteLine(fileJson);
                    }

                    if (command == "get folder")
                    {
                        var folder = Adapter.GetFolder(tokens[1]);

                        var folderJson = folder.ToJson(false);
                        Console.WriteLine(folderJson);
                    }

                    if (command == "get files")
                    {
                        var files = Adapter
                                        .GetFiles(tokens[1])
                                        .ToList();

                        var filesJson = files.ToJson(false);
                        Console.WriteLine(filesJson);
                    }

                    if (command == "get folders")
                    {
                        var folders = Adapter
                                                .GetFolders(tokens[1])
                                                .ToList();

                        var foldersJson = folders.ToJson(false);
                        Console.WriteLine(foldersJson);
                    }

                    if (command == "download file")
                    {
                        //FPS 10/11/2024: This experiences deadlocks with the command read above. Use base64 for now

                        var fileStream = Adapter.Download(tokens[1]);

                        Console.WriteLine(fileStream.Length);

                        fileStream.CopyTo(stdout);
                        stdout.Flush();
                    }

                    if (command == "download file as base64")
                    {
                        var fileStream = Adapter.Download(tokens[1]);

                        Console.WriteLine(fileStream.Length);

                        var buffer = new byte[65535];
                        while (true)
                        {
                            var read = fileStream.Read(buffer, 0, buffer.Length);
                            if (read == 0) break;

                            var str = Convert.ToBase64String(buffer, 0, read);
                            Console.WriteLine(str);
                        }
                    }
                }
            }
        }
    }
}
