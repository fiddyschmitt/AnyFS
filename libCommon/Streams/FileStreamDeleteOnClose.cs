using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams
{
    public class FileStreamDeleteOnClose : FileStream
    {
        public FileStreamDeleteOnClose(string path, FileMode mode, FileAccess access, FileShare share) : base(path, mode, access, share)
        {
            AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;
        }

        private void CurrentDomain_ProcessExit(object? sender, EventArgs e)
        {
            base.Close();
            try
            {
                File.Delete(base.Name);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        public override void Close()
        {

        }

        public override bool CanRead => base.CanRead;

        public override bool CanSeek => base.CanSeek;

        public override bool CanWrite => base.CanWrite;

        public override long Length => base.Length;

        public override long Position { get => base.Position; set => base.Position = value; }

        public override void Flush() => base.Flush();

        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => base.Seek(offset, origin);

        public override void SetLength(long value) => base.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => base.Write(buffer, offset, count);
    }
}
