using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libCommon.Streams
{
    public class LargeMemoryStream : Stream
    {
        private readonly List<MemoryStream> _chunks;
        private readonly long? _maxSize;
        private readonly int _chunkSize;

        private long _length;
        private long _position;

        public LargeMemoryStream(int chunkSize, long? maxSize = null)
        {
            if (chunkSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be positive.");

            if (maxSize.HasValue && maxSize.Value < 0)
                throw new ArgumentOutOfRangeException(nameof(maxSize), "Max size cannot be negative.");

            _chunks = new List<MemoryStream>();
            _chunkSize = chunkSize;
            _maxSize = maxSize;

            // If a max size is provided, we do not necessarily need to pre-allocate.
            // We can allocate chunks on demand. If you wish, you can pre-allocate:
            // int numChunks = (int)((maxSize.Value + chunkSize - 1) / chunkSize);
            // for (int i = 0; i < numChunks; i++) _chunks.Add(new MemoryStream(new byte[chunkSize], 0, chunkSize, true, true));
            // But here, we'll just allocate on demand to save memory if not needed.
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => true;
        public override bool CanTimeout => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "Position cannot be negative.");
                if (_maxSize.HasValue && value > _maxSize.Value)
                    throw new IOException("Position beyond the maximum size of the stream.");
                _position = value;
            }
        }

        public override void Flush()
        {
            // MemoryStreams don't need flushing; no-op.
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer is null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset >= buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            if (_position >= _length)
                return 0; // End of stream.

            int bytesRead = 0;
            while (count > 0 && _position < _length)
            {
                int chunkIndex = (int)(_position / _chunkSize);
                int chunkOffset = (int)(_position % _chunkSize);

                MemoryStream chunk = GetChunk(chunkIndex, false); // Should never be null if reading within length.
                chunk.Position = chunkOffset;

                int bytesToReadInThisChunk = (int)Math.Min(count, (int)(chunk.Length - chunkOffset));
                bytesToReadInThisChunk = (int)Math.Min(bytesToReadInThisChunk, _length - _position);
                if (bytesToReadInThisChunk <= 0) break;

                int read = chunk.Read(buffer, offset, bytesToReadInThisChunk);

                bytesRead += read;
                offset += read;
                count -= read;
                _position += read;
            }

            Debug.WriteLine($"{_position:N0}");
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPos;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    newPos = offset;
                    break;
                case SeekOrigin.Current:
                    newPos = _position + offset;
                    break;
                case SeekOrigin.End:
                    newPos = _length + offset;
                    break;
                default:
                    throw new ArgumentException("Invalid seek origin.", nameof(origin));
            }

            if (newPos < 0)
                throw new IOException("Cannot seek to a negative position.");
            if (_maxSize.HasValue && newPos > _maxSize.Value)
                throw new IOException("Cannot seek beyond the maximum size of the stream.");

            _position = newPos;
            return _position;
        }

        public override void SetLength(long value)
        {
            if (_maxSize.HasValue && value > _maxSize.Value)
                throw new IOException("Cannot set length beyond the maximum size of the stream.");
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));

            // If length is increased, we may need to allocate more chunks.
            // If decreased, we may need to trim chunks.
            if (value > _length)
            {
                // Increase length
                EnsureCapacity(value);
                _length = value;
            }
            else if (value < _length)
            {
                // Decrease length
                _length = value;
                TrimExcessChunks();
            }

            if (_position > _length)
            {
                // Adjust position if it's beyond the new length
                _position = _length;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!CanWrite)
                throw new NotSupportedException("Stream does not support writing.");

            if (buffer is null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset >= buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            if (count == 0) return;

            // If the write goes beyond max size, throw
            if (_maxSize.HasValue && _position + count > _maxSize.Value)
                throw new IOException("Writing beyond the maximum size of the stream.");

            long endPosition = _position + count;
            if (endPosition > _length)
            {
                // Extend length
                EnsureCapacity(endPosition);
                _length = endPosition;
            }

            int bytesWritten = 0;
            while (count > 0)
            {
                int chunkIndex = (int)(_position / _chunkSize);
                int chunkOffset = (int)(_position % _chunkSize);

                MemoryStream chunk = GetChunk(chunkIndex, true);
                chunk.Position = chunkOffset;

                int bytesToWriteInThisChunk = Math.Min(count, _chunkSize - chunkOffset);
                chunk.Write(buffer, offset, bytesToWriteInThisChunk);

                offset += bytesToWriteInThisChunk;
                count -= bytesToWriteInThisChunk;
                _position += bytesToWriteInThisChunk;
                bytesWritten += bytesToWriteInThisChunk;
            }
        }

        private void EnsureCapacity(long requiredLength)
        {
            // Calculate how many chunks we need
            int requiredChunks = (int)((requiredLength + _chunkSize - 1) / _chunkSize);

            // Add chunks if needed
            while (_chunks.Count < requiredChunks)
            {
                // Allocate new chunk
                _chunks.Add(new MemoryStream(new byte[_chunkSize], 0, _chunkSize, writable: true, publiclyVisible: true));
            }
        }

        private void TrimExcessChunks()
        {
            // Remove extra chunks that are no longer needed.
            int requiredChunks = (int)((_length + _chunkSize - 1) / _chunkSize);
            if (_length == 0) requiredChunks = 0; // If length is 0, no chunks needed.

            while (_chunks.Count > requiredChunks)
            {
                _chunks[_chunks.Count - 1].Dispose();
                _chunks.RemoveAt(_chunks.Count - 1);
            }

            // Also trim the last chunk's length if necessary
            if (_chunks.Count > 0)
            {
                int lastChunkIndex = _chunks.Count - 1;
                long lastChunkUsedBytes = (_length % _chunkSize);
                if (lastChunkUsedBytes == 0 && _length > 0)
                    lastChunkUsedBytes = _chunkSize;

                MemoryStream lastChunk = _chunks[lastChunkIndex];
                if (lastChunk.Length != lastChunkUsedBytes)
                {
                    lastChunk.SetLength(lastChunkUsedBytes);
                }
            }
        }

        private MemoryStream GetChunk(int chunkIndex, bool createIfNeeded)
        {
            if (chunkIndex < _chunks.Count)
            {
                return _chunks[chunkIndex];
            }
            else if (createIfNeeded)
            {
                // Create missing chunks up to chunkIndex
                while (_chunks.Count <= chunkIndex)
                {
                    _chunks.Add(new MemoryStream(new byte[_chunkSize], 0, _chunkSize, true, true));
                }
                return _chunks[chunkIndex];
            }
            else
            {
                // No chunk available and not creating
                return null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var chunk in _chunks)
                {
                    chunk.Dispose();
                }
                _chunks.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
