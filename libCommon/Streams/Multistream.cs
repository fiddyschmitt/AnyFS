﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams
{
    public class Multistream : Stream
    {
        long position = 0;

        public Multistream(IEnumerable<Stream> substreams)
        {
            long sizeSum = 0;
            Substreams = substreams
                                .Select((stream, index) => new Substream(
                                    sizeSum,
                                    sizeSum += stream.Length,
                                    stream,
                                    index))
                                .ToList();
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                var result = Substreams.Sum(substream => substream.Length);
                return result;
            }
        }

        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public List<Substream> Substreams { get; }

        public override void Flush()
        {
            throw new NotImplementedException();
        }


        public override int Read(byte[] buffer, int offset, int count)
        {

            var substream = Substreams.FirstOrDefault(s => Position >= s.Start && Position < s.End);

            if (substream == null)
            {
                return 0;
            }

            //determine where we should start reading in the substream
            var positionInSubstream = Position - substream.Start;
            substream.Stream.Seek(positionInSubstream, SeekOrigin.Begin);


            var bytesRead = substream.Stream.Read(buffer, offset, count);

            position += bytesRead;

            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    position = offset;
                    break;

                case SeekOrigin.Current:
                    position += offset;
                    break;

                case SeekOrigin.End:
                    position = Length - offset;
                    break;
            }

            return position;
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
    }

    public class Substream(long start, long end, Stream stream, int streamIndex = 0)
    {
        public long Start = start;
        public long End = end;
        public long Length => End - Start;
        public Stream Stream = stream;
        public int StreamIndex = streamIndex;
    }
}
