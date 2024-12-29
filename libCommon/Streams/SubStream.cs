using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams
{
    public class SubStream : Stream
    {
        public event EventHandler<EventArgs>? Closed;

        public SubStream(Stream baseStream, long startByte, long endByte)
        {
            BaseStream = baseStream;
            StartByte = startByte;
            EndByte = endByte;
        }

        public Stream BaseStream { get; }
        public long StartByte { get; }
        public long EndByte { get; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => EndByte - StartByte;

        long pos = 0;
        public override long Position
        {
            get => pos;
            set
            {
                pos = value;
            }
        }

        public override void Flush() => BaseStream.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var left = (int)Math.Min(Length - pos, int.MaxValue);

            int read = 0;
            if (left > 0)
            {
                var toRead = Math.Min(count, left);
                read = BaseStream.Read(buffer, offset, toRead);

                pos += read;
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    pos = offset;
                    break;

                case SeekOrigin.Current:
                    pos += offset;
                    break;

                case SeekOrigin.End:
                    pos = Length - offset;
                    break;
            }
            BaseStream.Seek(offset, origin);
            return pos;
        }

        public override void Close()
        {
            Closed?.Invoke(this, null);
        }

        public override void SetLength(long value) => BaseStream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => BaseStream.Write(buffer, offset, count);
    }
}
