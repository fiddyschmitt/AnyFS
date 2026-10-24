using System.Buffers.Binary;
using LibOutlook.Ndb;

namespace LibOutlook.Ltp;

/// <summary>
/// A node and what the layers above need of it: its data blocks, its subnodes (read once, on first use), and the
/// data of a subnode in one piece. A subnode's own tree is reached by making a context of its entry.
/// </summary>
internal sealed class NodeContext
{
    private readonly Lazy<Dictionary<uint, NodeEntry>> _subnodes;

    public NodeContext(BlockStore blocks, NodeEntry node)
    {
        Blocks = blocks;
        Node = node;
        _subnodes = new Lazy<Dictionary<uint, NodeEntry>>(() => blocks.ReadSubnodes(node.BidSub));
    }

    public BlockStore Blocks { get; }

    public NodeEntry Node { get; }

    /// <summary>Whether HIDs in this file are the 4 KiB format's wider kind.</summary>
    public bool WideHids => Blocks.Layout.Format == PstFormat.Unicode4K;

    public IReadOnlyDictionary<uint, NodeEntry> Subnodes => _subnodes.Value;

    public NodeEntry? Subnode(uint nid) => Subnodes.TryGetValue(nid, out var e) ? e : null;

    /// <summary>The data of the subnode with this NID, in one piece.</summary>
    public byte[] SubnodeData(uint nid)
    {
        var sub = Subnode(nid) ?? throw new PstException($"node {Node.Nid:X} has no subnode {nid:X}");
        return Blocks.ReadNodeData(sub.BidData);
    }

    /// <summary>The node's own heap: its data blocks as a heap-on-node.</summary>
    public HeapNode Heap() => new(Blocks.ReadDataBlocks(Node.BidData), WideHids);
}

/// <summary>
/// A heap-on-node ([MS-PST] 2.3.1): a node's data blocks, each a page of allocations, addressed by HID (block index,
/// allocation index). The first block starts with the HNHDR: who the heap is for (a PC's BTH, a TC) and its user root.
/// In the 4 KiB format blocks are bigger and an HID gives 14 bits to the allocation and 13 to the block, not 11 and 16.
/// </summary>
internal sealed class HeapNode
{
    private const byte Signature = 0xEC;

    private readonly List<byte[]> _blocks;
    private readonly bool _wideHids;

    public HeapNode(List<byte[]> blocks, bool wideHids)
    {
        if (blocks.Count == 0 || blocks[0].Length < 12)
        {
            throw new PstException("a heap node without its header block");
        }

        _blocks = blocks;
        _wideHids = wideHids;
        var first = blocks[0];
        if (first[2] != Signature)
        {
            throw new PstException($"a heap node whose signature is 0x{first[2]:X2}, not 0x{Signature:X2}: the block is not decoded as it should be");
        }

        ClientSignature = first[3];
        UserRoot = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(4));
    }

    /// <summary>What the heap holds: 0xBC a property context, 0x7C a table context, 0xB5 a BTH on its own, and others.</summary>
    public byte ClientSignature { get; }

    /// <summary>The HID of the structure the heap was made for (a PC's BTH header, a TC's TCINFO).</summary>
    public uint UserRoot { get; }

    /// <summary>The allocation an HID names; empty for HID 0.</summary>
    public ArraySegment<byte> Get(uint hid)
    {
        if (hid == 0)
        {
            return ArraySegment<byte>.Empty;
        }

        if ((hid & 0x1F) != 0)
        {
            throw new PstException($"0x{hid:X8} is a NID, not an HID");
        }

        var index = _wideHids ? (int)((hid >> 5) & 0x3FFF) : (int)((hid >> 5) & 0x7FF);
        var blockIndex = _wideHids ? (int)(hid >> 19) : (int)(hid >> 16);
        if (blockIndex >= _blocks.Count)
        {
            throw new PstException($"HID 0x{hid:X8} names block {blockIndex} of a heap with {_blocks.Count}");
        }

        var block = _blocks[blockIndex];
        var map = BinaryPrimitives.ReadUInt16LittleEndian(block);
        if (map + 4 > block.Length)
        {
            throw new PstException($"heap block {blockIndex}: its page map is past the block's end");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(map));
        if (index < 1 || index > count)
        {
            throw new PstException($"HID 0x{hid:X8} names allocation {index} of {count} in heap block {blockIndex}");
        }

        var table = map + 4;
        if (table + (index + 1) * 2 > block.Length)
        {
            throw new PstException($"heap block {blockIndex}: its allocation table is past the block's end");
        }

        int start = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(table + (index - 1) * 2));
        int end = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(table + index * 2));
        if (start > end || end > block.Length)
        {
            throw new PstException($"HID 0x{hid:X8}: allocation {start}-{end} is outside heap block {blockIndex} of {block.Length} bytes");
        }

        return new ArraySegment<byte>(block, start, end - start);
    }
}
