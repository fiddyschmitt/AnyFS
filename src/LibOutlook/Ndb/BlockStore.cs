using System.Buffers.Binary;
using System.IO.Compression;

namespace LibOutlook.Ndb;

/// <summary>
/// Blocks ([MS-PST] 2.2.2.8): each read from the file by its BBT entry, its trailer checked (size, signature, back
/// pointer, CRC), inflated when a 4 KiB file stored it compressed, and decoded by the file's obfuscation when it is a
/// data block (internal blocks, those with bit 1 of the BID set, are stored plain). On top of that the two trees made
/// of blocks: a node's data (XBLOCK/XXBLOCK) and a node's subnodes (SLBLOCK/SIBLOCK).
/// </summary>
internal sealed class BlockStore
{
    private const int CacheLimit = 2048;

    private readonly NodeDatabase _ndb;
    private readonly FileLayout _layout;
    private readonly Dictionary<ulong, byte[]> _blocks = [];
    private readonly object _gate = new();

    public BlockStore(NodeDatabase ndb)
    {
        _ndb = ndb;
        _layout = ndb.Layout;
    }

    public FileLayout Layout => _layout;

    /// <summary>A node's data blocks, each with its logical size, from the B-trees alone: none of the data is read.</summary>
    public List<(ulong Bid, int Size)> ListDataBlocks(ulong bidData)
    {
        var list = new List<(ulong, int)>();
        AddDataBlockIds(bidData, list, 0);
        return list;
    }

    /// <summary>A block's data bytes, decoded and inflated: exactly the block's logical content.</summary>
    public byte[] ReadBlock(ulong bid)
    {
        lock (_gate)
        {
            if (_blocks.TryGetValue(bid, out var cached))
            {
                return cached;
            }
        }

        var entry = _ndb.FindBlock(bid) ?? throw new PstException($"block {bid} is not in the block B-tree");
        var total = _layout.BlockTotalSize(entry.Cb);
        string? problem = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                _ndb.Source.Refresh();
                Thread.Sleep(15);
            }

            var raw = _ndb.Source.Read((long)entry.Ib, total);
            problem = Validate(raw, entry);
            if (problem is not null)
            {
                continue;
            }

            var data = raw.AsSpan(0, entry.Cb).ToArray();
            if (_layout.Format == PstFormat.Unicode4K && entry.CbInflated != entry.Cb && entry.CbInflated > 0)
            {
                data = Inflate(data, entry.CbInflated, bid);
            }

            if ((bid & 2) == 0)
            {
                Crypt.Decode(data, _ndb.Header.Encoding, bid);
            }

            lock (_gate)
            {
                if (_blocks.Count >= CacheLimit)
                {
                    _blocks.Clear();
                }

                _blocks[bid] = data;
            }

            return data;
        }

        throw new PstException($"block {bid} at {entry.Ib} is not sound after three reads: {problem}");
    }

    /// <summary>A node's data as the sequence of data blocks it is spread over ([MS-PST] 2.2.2.8.3.2, the data tree).</summary>
    public List<byte[]> ReadDataBlocks(ulong bidData)
    {
        var blocks = new List<byte[]>();
        AddDataBlocks(bidData, blocks, 0);
        return blocks;
    }

    /// <summary>A node's data in one piece.</summary>
    public byte[] ReadNodeData(ulong bidData)
    {
        var blocks = ReadDataBlocks(bidData);
        if (blocks.Count == 1)
        {
            return blocks[0];
        }

        var all = new byte[blocks.Sum(b => b.Length)];
        var at = 0;
        foreach (var b in blocks)
        {
            b.CopyTo(all, at);
            at += b.Length;
        }

        return all;
    }

    /// <summary>A node's subnodes by NID ([MS-PST] 2.2.2.8.3.3, the subnode B-tree); empty when it has none.</summary>
    public Dictionary<uint, NodeEntry> ReadSubnodes(ulong bidSub)
    {
        var map = new Dictionary<uint, NodeEntry>();
        if (bidSub != 0)
        {
            AddSubnodes(bidSub, map, 0);
        }

        return map;
    }

    public void ClearCache()
    {
        lock (_gate)
        {
            _blocks.Clear();
        }
    }

    private void AddDataBlocks(ulong bid, List<byte[]> into, int depth)
    {
        if (bid == 0)
        {
            return;
        }

        if ((bid & 2) == 0)
        {
            into.Add(ReadBlock(bid));
            return;
        }

        if (depth > 2)
        {
            throw new PstException("a data tree deeper than XXBLOCK → XBLOCK → data");
        }

        // XBLOCK / XXBLOCK: btype 1, cLevel, cEnt, lcbTotal, then the BIDs
        var block = ReadBlock(bid);
        if (block.Length < 8 || block[0] != 1)
        {
            throw new PstException($"block {bid} should be an XBLOCK but starts with 0x{(block.Length > 0 ? block[0] : 0):X2}");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(2));
        for (var i = 0; i < count; i++)
        {
            AddDataBlocks(ReadBid(block, 8 + i * _layout.BidSize), into, depth + 1);
        }
    }

    private void AddDataBlockIds(ulong bid, List<(ulong, int)> into, int depth)
    {
        if (bid == 0)
        {
            return;
        }

        if ((bid & 2) == 0)
        {
            var entry = _ndb.FindBlock(bid) ?? throw new PstException($"block {bid} is not in the block B-tree");
            into.Add((bid, entry.CbInflated > 0 ? entry.CbInflated : entry.Cb));
            return;
        }

        if (depth > 2)
        {
            throw new PstException("a data tree deeper than XXBLOCK → XBLOCK → data");
        }

        var block = ReadBlock(bid);
        if (block.Length < 8 || block[0] != 1)
        {
            throw new PstException($"block {bid} should be an XBLOCK but starts with 0x{(block.Length > 0 ? block[0] : 0):X2}");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(2));
        for (var i = 0; i < count; i++)
        {
            AddDataBlockIds(ReadBid(block, 8 + i * _layout.BidSize), into, depth + 1);
        }
    }

    private void AddSubnodes(ulong bid, Dictionary<uint, NodeEntry> into, int depth)
    {
        if (depth > 2)
        {
            throw new PstException("a subnode tree deeper than SIBLOCK → SLBLOCK");
        }

        var block = ReadBlock(bid);
        if (block.Length < 4 || block[0] != 2)
        {
            throw new PstException($"block {bid} should be an SLBLOCK or SIBLOCK but starts with 0x{(block.Length > 0 ? block[0] : 0):X2}");
        }

        var level = block[1];
        var count = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(2));
        var at = _layout.SubnodeEntriesOffset;
        var b = _layout.BidSize;
        if (level == 0)
        {
            // SLENTRY: nid, bidData, bidSub
            for (var i = 0; i < count; i++, at += _layout.SlEntrySize)
            {
                var nid = (uint)ReadBid(block, at);
                into[nid] = new NodeEntry(nid, ReadBid(block, at + b), ReadBid(block, at + 2 * b), 0);
            }
        }
        else
        {
            // SIENTRY: nid, the BID of an SLBLOCK
            for (var i = 0; i < count; i++, at += _layout.SiEntrySize)
            {
                AddSubnodes(ReadBid(block, at + b), into, depth + 1);
            }
        }
    }

    private string? Validate(byte[] raw, BlockEntry entry)
    {
        var t = raw.Length - _layout.BlockTrailerSize;
        var cb = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(t));
        if (cb != entry.Cb)
        {
            return $"trailer says {cb} bytes, the B-tree {entry.Cb}";
        }

        var sig = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(t + 2));
        var expectedSig = Crypt.Signature(entry.Ib, entry.Bid);
        if (sig != expectedSig)
        {
            return $"signature 0x{sig:X4}, 0x{expectedSig:X4} expected";
        }

        ulong bid;
        uint crc;
        if (_layout.Is64)
        {
            crc = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(t + 4));
            bid = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(t + 8));
        }
        else
        {
            bid = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(t + 4));
            crc = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(t + 8));
        }

        if (bid != entry.Bid)
        {
            return $"back pointer {bid}, {entry.Bid} expected";
        }

        var computed = Crypt.Crc(raw.AsSpan(0, entry.Cb));
        return computed == crc ? null : $"CRC 0x{computed:X8} computed, 0x{crc:X8} stored";
    }

    private static byte[] Inflate(byte[] data, int inflatedSize, ulong bid)
    {
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            var result = new byte[inflatedSize];
            zlib.ReadExactly(result);
            return result;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new PstException($"block {bid} did not inflate to the {inflatedSize} bytes its B-tree entry says", ex);
        }
    }

    private ulong ReadBid(byte[] block, int offset)
        => _layout.Is64 ? BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(offset));
}
