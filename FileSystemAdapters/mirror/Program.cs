using libCommon;
using libCommon.Adapters;
using libVirtualFileSystem.Files;
using libVirtualFileSystem.Folders;
using mirror.Files;
using System.Diagnostics;
using System.IO;

namespace mirror
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var mirrorFolder = new MirrorFolder(@"E:\");

            var listener = new StandardStreamsListener(mirrorFolder);
            listener.Start();
        }

        static StreamWriter? logWriter;
        public static void Log(string msg)
        {
            logWriter ??= new StreamWriter(new FileStream("anyfs.log", FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                AutoFlush = true
            };

            logWriter.WriteLine($"{DateTime.Now} {msg}");
        }
    }
}
