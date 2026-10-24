namespace LibOutlook.Ndb;

/// <summary>
/// A node's data as a read-only, seekable stream: the data blocks are located up front from the block B-tree (their
/// sizes, not their bytes) and each is read when a read reaches it, so a large attachment costs only what is read.
/// </summary>
internal sealed class NodeDataStream : Stream
{
    private readonly BlockStore _blocks;
    private readonly List<(ulong Bid, long Start, int Size)> _parts;
    private readonly long _length;
    private long _position;

    public NodeDataStream(BlockStore blocks, ulong bidData)
    {
        _blocks = blocks;
        _parts = [];
        long start = 0;
        foreach (var (bid, size) in blocks.ListDataBlocks(bidData))
        {
            _parts.Add((bid, start, size));
            start += size;
        }

        _length = start;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length || buffer.Length == 0)
        {
            return 0;
        }

        var index = Find(_position);
        var total = 0;
        while (buffer.Length > 0 && index < _parts.Count)
        {
            var (bid, start, size) = _parts[index];
            var within = (int)(_position - start);
            var n = Math.Min(size - within, buffer.Length);
            if (n <= 0)
            {
                index++;
                continue;
            }

            var block = _blocks.ReadBlock(bid);
            if (block.Length < within + n)
            {
                throw new PstException($"block {bid} holds {block.Length} bytes, {size} were expected");
            }

            block.AsSpan(within, n).CopyTo(buffer);
            buffer = buffer[n..];
            _position += n;
            total += n;
            index++;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
        {
            throw new IOException("a seek before the start of the stream");
        }

        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>The index of the part holding <paramref name="position"/>.</summary>
    private int Find(long position)
    {
        int lo = 0, hi = _parts.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_parts[mid].Start <= position)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }
}
