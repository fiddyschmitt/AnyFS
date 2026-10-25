using System.Buffers.Binary;

namespace LibOutlook.Ltp;

/// <summary>A table context's column ([MS-PST] 2.3.4.2, TCOLDESC).</summary>
internal readonly record struct TableColumn(ushort Id, ushort Type, int Offset, int Size, int Bit)
{
    public uint Tag => ((uint)Id << 16) | Type;
}

/// <summary>
/// A table context ([MS-PST] 2.3.4): rows of fixed layout (the TCINFO's columns), each with a cell-existence bitmap,
/// kept in the node's heap when small or in a subnode's data blocks when not; a row index BTH maps row ids to rows.
/// Folder hierarchies and contents, recipients and attachments are such tables.
/// </summary>
internal sealed class TableContext
{
    private const byte ClientSignature = 0x7C;

    private readonly NodeContext _node;
    private readonly HeapNode _heap;
    private readonly List<TableColumn> _columns = [];
    private readonly Dictionary<ushort, TableColumn> _byId = [];
    private readonly int _rowSize;
    private readonly int _bitmapOffset;
    private readonly uint _rowsHnid;
    private readonly Lazy<List<(uint RowId, byte[] Block, int Offset)>> _rows;

    public TableContext(NodeContext node)
        : this(node, HeapOf(node))
    {
    }

    /// <summary>Reads the table's columns and row index; a fault says which node it is in.</summary>
    private TableContext(NodeContext node, HeapNode heap)
    {
        _node = node;
        _heap = heap;
        try
        {
            if (_heap.ClientSignature != ClientSignature)
            {
                throw new PstException($"it holds a heap of kind 0x{_heap.ClientSignature:X2}, not a table context");
            }

            var info = _heap.Get(_heap.UserRoot);
            if (info.Count < 22 || info[0] != ClientSignature)
            {
                throw new PstException($"a TCINFO of {info.Count} bytes with signature 0x{(info.Count > 0 ? info[0] : 0):X2}");
            }

            int columnCount = info[1];
            // rgib: where each group of a row ends: 4/8-byte values, 2-byte, 1-byte, then the cell-existence bitmap
            _bitmapOffset = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(6));   // TCI_1b: the bitmap starts where the 1-byte values end
            _rowSize = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(8));        // TCI_bm: the bitmap's end is the row's size
            if (_rowSize < _bitmapOffset + (columnCount + 7) / 8)
            {
                throw new PstException($"rows of {_rowSize} bytes cannot hold a bitmap for {columnCount} columns at {_bitmapOffset}");
            }

            var rowIndexHid = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(10));
            _rowsHnid = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(14));
            if (info.Count < 22 + columnCount * 8)
            {
                throw new PstException($"a TCINFO of {info.Count} bytes for {columnCount} columns");
            }

            for (var i = 0; i < columnCount; i++)
            {
                var at = 22 + i * 8;
                var tag = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(at));
                var column = new TableColumn((ushort)(tag >> 16), (ushort)tag, BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(at + 4)), info[at + 6], info[at + 7]);
                _columns.Add(column);
                _byId[column.Id] = column;
            }

            var rowIndex = new BTreeOnHeap(_heap, rowIndexHid);
            _rows = new Lazy<List<(uint, byte[], int)>>(() => LocateRows(rowIndex));
        }
        catch (PstException ex)
        {
            throw new PstException($"{node.Where}: {ex.Message}", ex);
        }
    }

    /// <summary>The node's table, or null when the node holds some other kind of heap (a search folder's contents are not a TC).</summary>
    public static TableContext? TryOpen(NodeContext node)
    {
        var heap = HeapOf(node);
        return heap.ClientSignature == ClientSignature ? new TableContext(node, heap) : null;
    }

    private static HeapNode HeapOf(NodeContext node)
    {
        try
        {
            return node.Heap();
        }
        catch (PstException ex)
        {
            throw new PstException($"{node.Where}: {ex.Message}", ex);
        }
    }

    public IReadOnlyList<TableColumn> Columns => _columns;

    public int RowCount => _rows.Value.Count;

    /// <summary>The rows in row-index order: each with its id (the item's NID in a folder's or message's tables).</summary>
    public IEnumerable<TableRow> Rows()
    {
        foreach (var (rowId, block, offset) in _rows.Value)
        {
            yield return new TableRow(this, rowId, block, offset);
        }
    }

    /// <summary>Whether the row has the cell: the table has the column and the row's bitmap marks it set.</summary>
    internal bool HasCell(byte[] block, int rowOffset, ushort id)
    {
        if (!_byId.TryGetValue(id, out var column))
        {
            return false;
        }

        var bitByte = rowOffset + _bitmapOffset + column.Bit / 8;
        return bitByte < block.Length && (block[bitByte] & (1 << (7 - column.Bit % 8))) != 0;
    }

    /// <summary>A cell of a row, as bytes: inline for the fixed-size types, else from the heap or a subnode; null when the cell is not set.</summary>
    internal byte[]? Cell(byte[] block, int rowOffset, ushort id)
    {
        if (!HasCell(block, rowOffset, id))
        {
            return null;
        }

        var column = _byId[id];
        var at = rowOffset + column.Offset;
        try
        {
            if (at + column.Size > block.Length)
            {
                throw new PstException("its cell lies outside the row");
            }

            var fixedSize = PropertyValues.FixedSize(column.Type);
            if (fixedSize > 0 && fixedSize <= 8 && column.Size >= fixedSize)
            {
                return block.AsSpan(at, column.Size).ToArray();
            }

            var hnid = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(at));
            return PropertyContext.ResolveHnid(_node, _heap, hnid);
        }
        catch (PstException ex)
        {
            var rowId = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(rowOffset));
            throw new PstException($"{_node.Where}, the row of 0x{rowId:X}, {PropertyValues.Describe(id, column.Type)}: {ex.Message}", ex);
        }
    }

    internal ushort TypeOf(ushort id) => _byId.TryGetValue(id, out var c) ? c.Type : (ushort)0;

    private List<(uint RowId, byte[] Block, int Offset)> LocateRows(BTreeOnHeap rowIndex)
    {
        try
        {
            return ReadRows(rowIndex);
        }
        catch (PstException ex)
        {
            throw new PstException($"{_node.Where}: {ex.Message}", ex);
        }
    }

    private List<(uint RowId, byte[] Block, int Offset)> ReadRows(BTreeOnHeap rowIndex)
    {
        // dwRowID → dwRowIndex (4 bytes; 2 in ANSI files), inverted: which row id each row position holds
        var ids = new SortedDictionary<int, uint>();
        foreach (var record in rowIndex.Records())
        {
            var index = rowIndex.DataSize >= 4
                ? BinaryPrimitives.ReadInt32LittleEndian(record.Data.AsSpan(0))
                : BinaryPrimitives.ReadUInt16LittleEndian(record.Data.AsSpan(0));
            ids[index] = (uint)record.Key;
        }

        var rows = new List<(uint, byte[], int)>(ids.Count);
        if (ids.Count == 0 || _rowsHnid == 0)
        {
            return rows;
        }

        List<byte[]> blocks;
        if ((_rowsHnid & 0x1F) == 0)
        {
            blocks = [_heap.Get(_rowsHnid).AsSpan().ToArray()];
        }
        else
        {
            var sub = _node.Subnode(_rowsHnid) ?? throw new PstException($"its rows are in subnode {_rowsHnid:X}, which it has not");
            blocks = _node.Blocks.ReadDataBlocks(sub.BidData);
        }

        // every block but the last is full: the same number of rows in each, which the first block's size gives
        var rowsPerBlock = blocks.Count > 1 ? Math.Max(1, blocks[0].Length / _rowSize) : int.MaxValue;
        foreach (var (index, rowId) in ids)
        {
            var blockIndex = index / rowsPerBlock;
            var offset = (index % rowsPerBlock) * _rowSize;
            if (blockIndex >= blocks.Count || offset + _rowSize > blocks[blockIndex].Length)
            {
                throw new PstException($"row {index} lies outside its row blocks");
            }

            var block = blocks[blockIndex];
            var storedId = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(offset));
            if (storedId != rowId)
            {
                throw new PstException($"row {index} holds row id {storedId:X}, the index says {rowId:X}");
            }

            rows.Add((rowId, block, offset));
        }

        return rows;
    }
}

/// <summary>One row of a table context.</summary>
internal sealed class TableRow : IPropertySource
{
    private readonly TableContext _table;
    private readonly byte[] _block;
    private readonly int _offset;

    internal TableRow(TableContext table, uint rowId, byte[] block, int offset)
    {
        _table = table;
        RowId = rowId;
        _block = block;
        _offset = offset;
    }

    /// <summary>dwRowID: in a folder's tables the child's NID, in a message's the recipient's row id or the attachment's NID.</summary>
    public uint RowId { get; }

    public IEnumerable<ushort> Ids => _table.Columns.Where(c => _table.HasCell(_block, _offset, c.Id)).Select(c => c.Id);

    public bool Contains(ushort id) => _table.HasCell(_block, _offset, id);

    public RawProperty? Get(ushort id)
    {
        var bytes = _table.Cell(_block, _offset, id);
        return bytes is null ? null : new RawProperty(id, _table.TypeOf(id), bytes);
    }

    public IEnumerable<RawProperty> All()
    {
        foreach (var column in _table.Columns)
        {
            var bytes = _table.Cell(_block, _offset, column.Id);
            if (bytes is not null)
            {
                yield return new RawProperty(column.Id, column.Type, bytes);
            }
        }
    }
}
