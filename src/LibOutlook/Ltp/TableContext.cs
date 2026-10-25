using System.Buffers.Binary;

namespace LibOutlook.Ltp;

/// <summary>A table context's column ([MS-PST] 2.3.4.2, TCOLDESC).</summary>
internal readonly record struct TableColumn(ushort Id, ushort Type, int Offset, int Size, int Bit)
{
    public uint Tag => ((uint)Id << 16) | Type;
}

/// <summary>What a row holds for a property.</summary>
internal enum CellKind
{
    /// <summary>The table has no such column.</summary>
    NoColumn,

    /// <summary>The column is the table's, the row's cell is not set: the item has no such property.</summary>
    Unset,

    /// <summary>The cell is set and its value read.</summary>
    Value,

    /// <summary>The cell is set but names a value the table does not hold: read as unset, as libpff reads it.</summary>
    Missing,
}

/// <summary>
/// A table context ([MS-PST] 2.3.4): rows of fixed layout (the TCINFO's columns), each with a cell-existence bitmap,
/// kept in the node's heap when small or in a subnode's data blocks when not; a row index BTH maps row ids to rows.
/// Folder hierarchies and contents, recipients and attachments are such tables.
///
/// In the 4 KiB OST format Outlook keeps some columns of a folder's contents table (the sender's name and the
/// conversation id: what its views group and sort by) in a <em>column store</em>: a subnode of the table holding a
/// heap of client signature 0xA5 with one allocation per value, the row's cell an HID into that heap rather than the
/// table's own. [MS-PST] does not describe it, and a 7C column definition has no room to name its store (libpff's
/// notes on the 0xAC table show the shape with a store named per column), so each store is placed from the rows
/// themselves: see <see cref="FindStores"/>. Read as the table's heap, such a cell gives another value or none: a
/// real OST listed one sender in five as another mail's subject or as noise (25-Oct-2026).
/// </summary>
internal sealed class TableContext
{
    private const byte ClientSignature = 0x7C;
    private const byte StoreSignature = 0xA5;

    /// <summary>The subnode ids Outlook has been seen to keep column stores under, for a table whose rows cannot place them (an empty folder's, a damaged item's).</summary>
    private static readonly Dictionary<uint, ushort> KnownStores = new()
    {
        [0x521] = 0x3013,   // PidTagConversationId
        [0x541] = 0x0042,   // PidTagSentRepresentingName
    };

    private readonly NodeContext _node;
    private readonly HeapNode _heap;
    private readonly Func<uint, IPropertySource?>? _openRow;
    private readonly List<TableColumn> _columns = [];
    private readonly Dictionary<ushort, TableColumn> _byId = [];
    private readonly int _rowSize;
    private readonly int _bitmapOffset;
    private readonly uint _rowsHnid;
    private readonly Lazy<List<(uint RowId, byte[] Block, int Offset)>> _rows;
    private readonly Lazy<Dictionary<ushort, HeapNode>> _stores;

    /// <param name="node">The table's node.</param>
    /// <param name="openRow">A row's own properties by its row id (an item's, for a folder's table), for placing the table's column stores; null when the rows are no nodes of their own.</param>
    public TableContext(NodeContext node, Func<uint, IPropertySource?>? openRow = null)
        : this(node, HeapOf(node), openRow)
    {
    }

    /// <summary>Reads the table's columns and row index; a fault says which node it is in.</summary>
    private TableContext(NodeContext node, HeapNode heap, Func<uint, IPropertySource?>? openRow)
    {
        _node = node;
        _heap = heap;
        _openRow = openRow;
        _stores = new Lazy<Dictionary<ushort, HeapNode>>(FindStores);
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
    public static TableContext? TryOpen(NodeContext node, Func<uint, IPropertySource?>? openRow = null)
    {
        var heap = HeapOf(node);
        return heap.ClientSignature == ClientSignature ? new TableContext(node, heap, openRow) : null;
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

    /// <summary>A cell of a row, as bytes: inline for the fixed-size types, else from the heap or a subnode; null when the cell is not set or names a value the table does not hold.</summary>
    internal byte[]? Cell(byte[] block, int rowOffset, ushort id) => ReadCell(block, rowOffset, id).Bytes;

    /// <summary>
    /// What a row holds for a property. A set cell that names a value the table does not hold (a reference past its heap's
    /// allocations, a subnode it has not: met in a real OST's contents tables, hundreds of them) is Missing, as libpff takes
    /// such a cell; it is noted in the file's <see cref="MissingValueLog"/>, and the item's own property stands in for it.
    /// </summary>
    internal (CellKind Kind, byte[]? Bytes) ReadCell(byte[] block, int rowOffset, ushort id)
    {
        if (!_byId.TryGetValue(id, out var column))
        {
            return (CellKind.NoColumn, null);
        }

        if (!HasCell(block, rowOffset, id))
        {
            return (CellKind.Unset, null);
        }

        var at = rowOffset + column.Offset;
        var rowId = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(rowOffset));
        if (at + column.Size > block.Length)
        {
            throw new PstException($"{_node.Where}, the row of 0x{rowId:X}, {PropertyValues.Describe(id, column.Type)}: its cell lies outside the row");
        }

        var fixedSize = PropertyValues.FixedSize(column.Type);
        if (fixedSize > 0 && fixedSize <= 8 && column.Size >= fixedSize)
        {
            return (CellKind.Value, block.AsSpan(at, column.Size).ToArray());
        }

        var hnid = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(at));
        try
        {
            // a column with a store of its own: the cell's HID is into the store's heap, not the table's
            var heap = (hnid & 0x1F) == 0 && _stores.Value.TryGetValue(id, out var store) ? store : _heap;
            return (CellKind.Value, PropertyContext.ResolveHnid(_node, heap, hnid));
        }
        catch (PstException ex)
        {
            _node.Missing.Add($"{_node.Where}, the row of 0x{rowId:X}, {PropertyValues.Describe(id, column.Type)} in a cell of {column.Size} bytes: {ex.Message}");
            return (CellKind.Missing, null);
        }
    }

    /// <summary>A set cell's first four bytes as stored, for diagnostics.</summary>
    internal uint? CellReference(byte[] block, int rowOffset, ushort id)
    {
        if (!_byId.TryGetValue(id, out var column) || !HasCell(block, rowOffset, id) || column.Size < 4 || rowOffset + column.Offset + 4 > block.Length)
        {
            return null;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(rowOffset + column.Offset));
    }

    /// <summary>The row blocks' sizes and the row size, for diagnostics.</summary>
    internal (int RowSize, IReadOnlyList<int> BlockSizes) RowLayout => (_rowSize, _rows.Value.Select(r => r.Block).Distinct().Select(b => b.Length).ToList());

    internal ushort TypeOf(ushort id) => _byId.TryGetValue(id, out var c) ? c.Type : (ushort)0;

    internal bool HasColumn(ushort id) => _byId.ContainsKey(id);

    /// <summary>
    /// The table's column stores, by column. Each store is placed from the rows: a column whose set cell, taken as an
    /// HID into the store, gives the row's own value of the property while the table's heap gives something else or
    /// nothing is the store's (three rows are asked, the first that read). A store the rows cannot place (an empty
    /// folder's, rows that do not read) takes the subnode id Outlook has been seen to use; one that neither places is
    /// noted among the table's missing values and left, its column's cells read from the table's heap as before.
    /// </summary>
    private Dictionary<ushort, HeapNode> FindStores()
    {
        var found = new Dictionary<ushort, HeapNode>();
        List<(uint Nid, HeapNode Heap)> stores;
        try
        {
            stores = ColumnStores();
        }
        catch (PstException)
        {
            return found;
        }

        if (stores.Count == 0)
        {
            return found;
        }

        var candidates = _columns.Where(c => c.Size == 4 && PropertyValues.FixedSize(c.Type) is 0 or > 8).ToList();
        var votes = new Dictionary<(uint Nid, ushort Column), int>();
        try
        {
            var sampled = 0;
            foreach (var (rowId, block, offset) in _rows.Value)
            {
                if (sampled >= 3 || _openRow is null)
                {
                    break;
                }

                var own = _openRow(rowId);
                if (own is null)
                {
                    continue;
                }

                sampled++;
                foreach (var column in candidates)
                {
                    if (!HasCell(block, offset, column.Id))
                    {
                        continue;
                    }

                    var hnid = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(offset + column.Offset));
                    if (hnid == 0 || (hnid & 0x1F) != 0 || own.Get(column.Id)?.Bytes is not { } value)
                    {
                        continue;
                    }

                    foreach (var (nid, heap) in stores)
                    {
                        if (Resolves(heap, hnid) is { } fromStore && fromStore.AsSpan().SequenceEqual(value) && !(Resolves(_heap, hnid) is { } fromTable && fromTable.AsSpan().SequenceEqual(value)))
                        {
                            votes[(nid, column.Id)] = votes.GetValueOrDefault((nid, column.Id)) + 1;
                        }
                    }
                }
            }
        }
        catch (PstException)
        {
            // what was sampled before the fault still counts
        }

        foreach (var (nid, heap) in stores)
        {
            var placed = votes.Where(v => v.Key.Nid == nid).OrderByDescending(v => v.Value).Select(v => v.Key.Column).ToList();
            ushort? column = placed.Count switch
            {
                1 => placed[0],
                > 1 when votes[(nid, placed[0])] > votes[(nid, placed[1])] => placed[0],
                _ => null,
            };
            column ??= KnownStores.TryGetValue(nid, out var known) && _byId.ContainsKey(known) ? known : null;
            if (column is { } id && !found.ContainsKey(id))
            {
                found[id] = heap;
            }
            else
            {
                _node.Missing.Add($"{_node.Where}: a column store (subnode 0x{nid:X}, {heap.Pages().Sum(p => p.Allocations)} values) that no column could be placed in, so its column's cells read from the table's heap");
            }
        }

        return found;
    }

    /// <summary>The table's column stores as they are in the file: its internal subnodes holding a heap of client signature 0xA5 with values in it.</summary>
    private List<(uint Nid, HeapNode Heap)> ColumnStores()
    {
        var stores = new List<(uint, HeapNode)>();
        foreach (var (nid, entry) in _node.Subnodes)
        {
            if (NodeId.TypeOf(nid) != NodeId.TypeInternal)
            {
                continue;
            }

            var blocks = _node.Blocks.ReadDataBlocks(entry.BidData);
            if (blocks.Count == 0 || blocks[0].Length < 12 || blocks[0][2] != 0xEC || blocks[0][3] != StoreSignature)
            {
                continue;
            }

            var heap = new HeapNode(blocks, _node.WideHids);
            if (heap.Pages().Any(p => p.Allocations > 0))
            {
                stores.Add((nid, heap));
            }
        }

        return stores;
    }

    /// <summary>What an HID names in a heap, or null when it names nothing there.</summary>
    private static byte[]? Resolves(HeapNode heap, uint hid)
    {
        try
        {
            return heap.Get(hid).ToArray();
        }
        catch (PstException)
        {
            return null;
        }
    }

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

    public RawProperty? Get(ushort id) => Read(id).Value;

    /// <summary>What the row holds for the property: no column, an unset cell, a value, or one the table does not hold.</summary>
    public (CellKind Kind, RawProperty? Value) Read(ushort id)
    {
        var (kind, bytes) = _table.ReadCell(_block, _offset, id);
        return (kind, bytes is null ? null : new RawProperty(id, _table.TypeOf(id), bytes));
    }

    /// <summary>Whether the table has the column and whether the row's cell is set, its value not read (Value for a set cell).</summary>
    public CellKind State(ushort id) => !_table.HasColumn(id) ? CellKind.NoColumn : _table.HasCell(_block, _offset, id) ? CellKind.Value : CellKind.Unset;

    /// <summary>The cell's four bytes as stored (an HNID for a variable-size column), for diagnostics; null when the cell is not set.</summary>
    internal uint? Reference(ushort id) => _table.CellReference(_block, _offset, id);

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
