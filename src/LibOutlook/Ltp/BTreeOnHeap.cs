using System.Buffers.Binary;

namespace LibOutlook.Ltp;

/// <summary>A BTH's record: its key and its data bytes.</summary>
internal readonly record struct HeapRecord(ulong Key, ArraySegment<byte> Data);

/// <summary>A B-tree on a heap ([MS-PST] 2.3.2): sorted records of a fixed key and data size, in heap allocations.</summary>
internal sealed class BTreeOnHeap
{
    private const byte Signature = 0xB5;

    private readonly HeapNode _heap;
    private readonly int _keySize;
    private readonly int _dataSize;
    private readonly int _levels;
    private readonly uint _root;

    public BTreeOnHeap(HeapNode heap, uint headerHid)
    {
        _heap = heap;
        var header = heap.Get(headerHid);
        if (header.Count < 8 || header[0] != Signature)
        {
            throw new PstException($"a BTH header with signature 0x{(header.Count > 0 ? header[0] : 0):X2}, not 0x{Signature:X2}");
        }

        _keySize = header[1];
        _dataSize = header[2];
        _levels = header[3];
        _root = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (_keySize is not (2 or 4 or 8 or 16))
        {
            throw new PstException($"a BTH with keys of {_keySize} bytes");
        }
    }

    public int KeySize => _keySize;

    public int DataSize => _dataSize;

    /// <summary>Every leaf record, in key order.</summary>
    public IEnumerable<HeapRecord> Records() => Walk(_root, _levels);

    private IEnumerable<HeapRecord> Walk(uint hid, int level)
    {
        if (hid == 0)
        {
            yield break;
        }

        var page = _heap.Get(hid);
        if (level > 0)
        {
            var size = _keySize + 4;
            for (var at = 0; at + size <= page.Count; at += size)
            {
                foreach (var record in Walk(BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(at + _keySize)), level - 1))
                {
                    yield return record;
                }
            }

            yield break;
        }

        var recordSize = _keySize + _dataSize;
        for (var at = 0; at + recordSize <= page.Count; at += recordSize)
        {
            yield return new HeapRecord(ReadKey(page, at), page.Slice(at + _keySize, _dataSize));
        }
    }

    private ulong ReadKey(ArraySegment<byte> page, int at) => _keySize switch
    {
        2 => BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(at)),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(at)),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(at)),
    };
}
