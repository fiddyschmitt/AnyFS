using System.Buffers.Binary;

namespace LibOutlook.Ndb;

/// <summary>A node B-tree leaf entry ([MS-PST] 2.2.2.7.7.4): the node's data block, its subnode tree, its parent.</summary>
internal readonly record struct NodeEntry(uint Nid, ulong BidData, ulong BidSub, uint NidParent);

/// <summary>A block B-tree leaf entry ([MS-PST] 2.2.2.7.7.3): where a block is, its data size and, in 4 KiB files, its size once inflated.</summary>
internal readonly record struct BlockEntry(ulong Bid, ulong Ib, int Cb, int CbInflated, int RefCount);

/// <summary>
/// The NDB layer's two B-trees ([MS-PST] 2.2.2.7): nodes by NID and blocks by BID, read a page at a time from the
/// file with every page checked (type, back pointer, CRC) and read again when a live file gave a torn one.
/// </summary>
internal sealed class NodeDatabase
{
    private const byte PtypeBbt = 0x80;
    private const byte PtypeNbt = 0x81;
    private const int PageCacheLimit = 4096;

    private readonly IDataSource _source;
    private readonly Dictionary<ulong, byte[]> _pages = [];
    private readonly object _gate = new();

    public NodeDatabase(IDataSource source, Header header)
    {
        _source = source;
        Header = header;
        Layout = header.Layout;
    }

    public Header Header { get; }

    public FileLayout Layout { get; }

    public IDataSource Source => _source;

    /// <summary>The node with this NID, or null when the file has none.</summary>
    public NodeEntry? FindNode(uint nid)
    {
        var leaf = Lookup(Header.Nbt, PtypeNbt, nid);
        if (leaf is not { } found)
        {
            return null;
        }

        var (page, offset) = found;
        var b = Layout.BidSize;
        var entryNid = (uint)ReadKey(page, offset);
        return new NodeEntry(entryNid, ReadKey(page, offset + b), ReadKey(page, offset + 2 * b), BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(offset + 3 * b)));
    }

    /// <summary>The block with this BID (its reserved low bit ignored), or null.</summary>
    public BlockEntry? FindBlock(ulong bid)
    {
        var key = bid & ~1UL;
        var leaf = Lookup(Header.Bbt, PtypeBbt, key);
        if (leaf is not { } found)
        {
            return null;
        }

        var (page, offset) = found;
        var b = Layout.BidSize;
        var entryBid = ReadKey(page, offset);
        var ib = ReadKey(page, offset + b);
        var cb = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(offset + 2 * b));
        int inflated;
        int refs;
        if (Layout.Format == PstFormat.Unicode4K)
        {
            inflated = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(offset + 2 * b + 2));
            refs = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(offset + 2 * b + 4));
        }
        else
        {
            inflated = cb;
            refs = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(offset + 2 * b + 2));
        }

        return new BlockEntry(entryBid, ib, cb, inflated, refs);
    }

    /// <summary>Forgets the pages read so far (after the file changed under us).</summary>
    public void ClearCache()
    {
        lock (_gate)
        {
            _pages.Clear();
        }
    }

    /// <summary>Walks a B-tree down to the leaf entry whose key is <paramref name="key"/>: the leaf page and the entry's offset in it.</summary>
    private (byte[] Page, int Offset)? Lookup(Bref root, byte ptype, ulong key)
    {
        var bref = root;
        for (var depth = 0; depth < 16; depth++)
        {
            var page = ReadPage(bref, ptype);
            var (count, cbEnt, level) = Counts(page);
            if (level > 0)
            {
                // BTENTRY: btkey, BREF; the child holding key is the last entry whose btkey <= key
                var child = -1;
                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    var mid = (lo + hi) / 2;
                    if (ReadKey(page, mid * cbEnt) <= key)
                    {
                        child = mid;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                if (child < 0)
                {
                    return null;
                }

                var b = Layout.BidSize;
                bref = new Bref(ReadKey(page, child * cbEnt + b), ReadKey(page, child * cbEnt + 2 * b));
                continue;
            }

            int l = 0, h = count - 1;
            while (l <= h)
            {
                var mid = (l + h) / 2;
                var k = ReadKey(page, mid * cbEnt);
                if (ptype == PtypeNbt)
                {
                    k &= 0xFFFFFFFF;   // a NID: the entry's high bytes are not part of the key
                }

                if (k == key)
                {
                    return (page, mid * cbEnt);
                }

                if (k < key)
                {
                    l = mid + 1;
                }
                else
                {
                    h = mid - 1;
                }
            }

            return null;
        }

        throw new PstException("a B-tree deeper than 16 levels: the file's index loops");
    }

    /// <summary>
    /// A B-tree page, checked (type byte and its repeat, the back pointer against the BREF that led here, the CRC) and,
    /// when a check fails, read again twice after a short pause: a live file's page can be caught mid-write.
    /// </summary>
    private byte[] ReadPage(Bref bref, byte ptype)
    {
        lock (_gate)
        {
            if (_pages.TryGetValue(bref.Ib, out var cached))
            {
                return cached;
            }
        }

        string? problem = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                _source.Refresh();
                Thread.Sleep(15);
            }

            var page = _source.Read((long)bref.Ib, Layout.PageSize);
            problem = Validate(page, bref, ptype);
            if (problem is null)
            {
                lock (_gate)
                {
                    if (_pages.Count >= PageCacheLimit)
                    {
                        _pages.Clear();
                    }

                    _pages[bref.Ib] = page;
                }

                return page;
            }
        }

        throw new PstException($"the B-tree page at {bref.Ib} is not sound after three reads: {problem}");
    }

    private string? Validate(byte[] page, Bref bref, byte ptype)
    {
        var t = Layout.PageTrailerOffset;
        if (page[t] != ptype || page[t + 1] != ptype)
        {
            return $"page type 0x{page[t]:X2}/0x{page[t + 1]:X2}, 0x{ptype:X2} expected";
        }

        ulong bid;
        uint crc;
        if (Layout.Is64)
        {
            crc = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(t + 4));
            bid = BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(t + 8));
        }
        else
        {
            bid = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(t + 4));
            crc = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(t + 8));
        }

        if (bid != bref.Bid)
        {
            return $"back pointer {bid}, {bref.Bid} expected";
        }

        var computed = Crypt.Crc(page.AsSpan(0, Layout.PageCrcSize));
        if (computed != crc)
        {
            return $"CRC 0x{computed:X8} computed, 0x{crc:X8} stored";
        }

        var (count, cbEnt, _) = Counts(page);
        if (cbEnt == 0 || count * cbEnt > Layout.PageEntriesSize)
        {
            return $"{count} entries of {cbEnt} bytes do not fit the page";
        }

        return null;
    }

    /// <summary>cEnt, cbEnt and cLevel of a B-tree page (one byte each after the entries; cEnt and cEntMax two bytes in 4 KiB pages).</summary>
    private (int Count, int EntrySize, int Level) Counts(byte[] page)
    {
        var e = Layout.PageEntriesSize;
        return Layout.WideCounts
            ? (BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(e)), page[e + 4], page[e + 5])
            : (page[e], page[e + 2], page[e + 3]);
    }

    /// <summary>A BID, IB or key at <paramref name="offset"/>: 8 bytes in the 64-bit formats, 4 in ANSI.</summary>
    private ulong ReadKey(byte[] page, int offset)
        => Layout.Is64 ? BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(offset)) : BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(offset));
}