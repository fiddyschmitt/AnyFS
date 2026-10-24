using System.Buffers.Binary;

namespace LibOutlook.Ltp;

/// <summary>A property as a PC or a TC holds it: its type and its raw bytes, as stored, not yet decoded.</summary>
internal readonly record struct RawProperty(ushort Id, ushort Type, byte[] Bytes)
{
    public uint Tag => ((uint)Id << 16) | Type;
}

/// <summary>Where an item's raw properties come from: its property context, a table row about it, or both.</summary>
internal interface IPropertySource
{
    IEnumerable<ushort> Ids { get; }

    bool Contains(ushort id);

    RawProperty? Get(ushort id);

    IEnumerable<RawProperty> All();
}

/// <summary>A table row's cells first (already in hand), the item's own property context for the rest (opened on first need).</summary>
internal sealed class RowBackedSource(TableRow? row, Lazy<PropertyContext> context) : IPropertySource
{
    public IEnumerable<ushort> Ids => context.Value.Ids;

    public bool Contains(ushort id) => (row?.Contains(id) ?? false) || context.Value.Contains(id);

    public RawProperty? Get(ushort id) => row?.Get(id) ?? context.Value.Get(id);

    public IEnumerable<RawProperty> All() => context.Value.All();
}

/// <summary>
/// A property context ([MS-PST] 2.3.3): a node's properties, in a BTH keyed by property id whose records give the
/// type and either the value itself (4 bytes and under) or an HNID: an allocation of the node's heap, or a subnode
/// holding the value when it is too big for the heap. A PtypObject's HNID leads to 8 bytes: the NID of the subnode
/// holding the object and its size.
/// </summary>
internal sealed class PropertyContext : IPropertySource
{
    private const byte ClientSignature = 0xBC;

    private readonly NodeContext _node;
    private readonly HeapNode _heap;
    private readonly Dictionary<ushort, (ushort Type, uint Hnid)> _records = [];

    public PropertyContext(NodeContext node)
    {
        _node = node;
        _heap = node.Heap();
        if (_heap.ClientSignature != ClientSignature)
        {
            throw new PstException($"node {node.Node.Nid:X} holds a heap of kind 0x{_heap.ClientSignature:X2}, not a property context");
        }

        var bth = new BTreeOnHeap(_heap, _heap.UserRoot);
        if (bth.KeySize != 2 || bth.DataSize != 6)
        {
            throw new PstException($"node {node.Node.Nid:X}: a property context's BTH has {bth.KeySize}-byte keys and {bth.DataSize}-byte records");
        }

        foreach (var record in bth.Records())
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(record.Data.AsSpan(0));
            var hnid = BinaryPrimitives.ReadUInt32LittleEndian(record.Data.AsSpan(2));
            _records[(ushort)record.Key] = (type, hnid);
        }
    }

    public NodeContext Node => _node;

    public IEnumerable<ushort> Ids => _records.Keys;

    public bool Contains(ushort id) => _records.ContainsKey(id);

    /// <summary>The property's type and bytes; null when the node has no such property.</summary>
    public RawProperty? Get(ushort id)
    {
        if (!_records.TryGetValue(id, out var r))
        {
            return null;
        }

        return new RawProperty(id, r.Type, Resolve(r.Type, r.Hnid));
    }

    /// <summary>Every property, bytes and all.</summary>
    public IEnumerable<RawProperty> All()
    {
        foreach (var (id, r) in _records)
        {
            yield return new RawProperty(id, r.Type, Resolve(r.Type, r.Hnid));
        }
    }

    /// <summary>The property's type and the HNID its record holds, unresolved: for reading a large value as a stream.</summary>
    public bool TryGetReference(ushort id, out ushort type, out uint hnid)
    {
        if (_records.TryGetValue(id, out var r) && !PropertyValues.IsInline(r.Type))
        {
            (type, hnid) = r;
            return true;
        }

        type = 0;
        hnid = 0;
        return false;
    }

    /// <summary>What an HNID of this node names: nothing, an allocation of its heap, or the whole data of a subnode.</summary>
    public byte[] Bytes(uint hnid) => ResolveHnid(_node, _heap, hnid);

    private byte[] Resolve(ushort type, uint hnid)
    {
        if (PropertyValues.IsInline(type))
        {
            var inline = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(inline, hnid);
            return inline;
        }

        return ResolveHnid(_node, _heap, hnid);
    }

    /// <summary>What an HNID names: nothing, an allocation of the heap, or the whole data of a subnode.</summary>
    public static byte[] ResolveHnid(NodeContext node, HeapNode heap, uint hnid)
    {
        if (hnid == 0)
        {
            return [];
        }

        if ((hnid & 0x1F) == 0)
        {
            var segment = heap.Get(hnid);
            return segment.AsSpan().ToArray();
        }

        return node.SubnodeData(hnid);
    }
}
