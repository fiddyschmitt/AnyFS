namespace LibOutlook.Ndb;

/// <summary>
/// The sizes and offsets that differ between the three formats ([MS-PST] 2.2.2; the 4 KiB variant from the files
/// themselves and libpff's notes, since the specification does not describe it).
/// </summary>
internal sealed class FileLayout
{
    public static readonly FileLayout Ansi = new(PstFormat.Ansi)
    {
        PageSize = 512,
        PageEntriesSize = 496,
        PageCrcSize = 500,
        PageTrailerOffset = 500,
        WideCounts = false,
        BidSize = 4,
        BtEntrySize = 12,
        NbtEntrySize = 16,
        BbtEntrySize = 12,
        BlockTrailerSize = 12,
        BlockAlignment = 64,
        SubnodeEntriesOffset = 4,
        SlEntrySize = 12,
        SiEntrySize = 8,
        CryptMethodOffset = 461,
        RootOffset = 164,
    };

    public static readonly FileLayout Unicode = new(PstFormat.Unicode)
    {
        PageSize = 512,
        PageEntriesSize = 488,
        PageCrcSize = 496,
        PageTrailerOffset = 496,
        WideCounts = false,
        BidSize = 8,
        BtEntrySize = 24,
        NbtEntrySize = 32,
        BbtEntrySize = 24,
        BlockTrailerSize = 16,
        BlockAlignment = 64,
        SubnodeEntriesOffset = 8,
        SlEntrySize = 24,
        SiEntrySize = 16,
        CryptMethodOffset = 513,
        RootOffset = 180,
    };

    public static readonly FileLayout Unicode4K = new(PstFormat.Unicode4K)
    {
        PageSize = 4096,
        PageEntriesSize = 4056,
        PageCrcSize = 4072,
        PageTrailerOffset = 4072,
        WideCounts = true,
        BidSize = 8,
        BtEntrySize = 24,
        NbtEntrySize = 32,
        BbtEntrySize = 24,
        BlockTrailerSize = 24,
        BlockAlignment = 512,
        SubnodeEntriesOffset = 8,
        SlEntrySize = 24,
        SiEntrySize = 16,
        CryptMethodOffset = 513,
        RootOffset = 180,
    };

    private FileLayout(PstFormat format)
    {
        Format = format;
    }

    public PstFormat Format { get; }

    public bool Is64 => BidSize == 8;

    /// <summary>A BTPAGE's size.</summary>
    public int PageSize { get; private init; }

    /// <summary>The bytes of a BTPAGE that hold entries; the counts (cEnt, cEntMax, cbEnt, cLevel) follow at once.</summary>
    public int PageEntriesSize { get; private init; }

    /// <summary>How many of the page's first bytes its trailer's CRC covers.</summary>
    public int PageCrcSize { get; private init; }

    public int PageTrailerOffset { get; private init; }

    /// <summary>Whether cEnt and cEntMax are two bytes each (4 KiB pages) rather than one.</summary>
    public bool WideCounts { get; private init; }

    /// <summary>The size of a BID or an IB: 4 or 8.</summary>
    public int BidSize { get; private init; }

    /// <summary>An intermediate B-tree entry: btkey then a BREF.</summary>
    public int BtEntrySize { get; private init; }

    public int NbtEntrySize { get; private init; }

    public int BbtEntrySize { get; private init; }

    public int BlockTrailerSize { get; private init; }

    /// <summary>Blocks are stored in multiples of this.</summary>
    public int BlockAlignment { get; private init; }

    /// <summary>Where an SLBLOCK's or SIBLOCK's entries start (after btype, cLevel, cEnt and, in the 64-bit formats, padding).</summary>
    public int SubnodeEntriesOffset { get; private init; }

    public int SlEntrySize { get; private init; }

    public int SiEntrySize { get; private init; }

    public int CryptMethodOffset { get; private init; }

    /// <summary>Where the ROOT structure starts in the header.</summary>
    public int RootOffset { get; private init; }

    /// <summary>The most data a single block holds: 8192 less its trailer ([MS-PST] 2.2.2.8).</summary>
    public int MaxBlockDataSize => 8192 - BlockTrailerSize;

    /// <summary>The whole size a block of <paramref name="cb"/> data bytes takes on disk, trailer and padding included.</summary>
    public int BlockTotalSize(int cb) => (cb + BlockTrailerSize + BlockAlignment - 1) & ~(BlockAlignment - 1);

    public static FileLayout For(PstFormat format) => format switch
    {
        PstFormat.Ansi => Ansi,
        PstFormat.Unicode => Unicode,
        _ => Unicode4K,
    };
}
