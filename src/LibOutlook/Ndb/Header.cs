using System.Buffers.Binary;
using System.Text;

namespace LibOutlook.Ndb;

/// <summary>A block reference: a block's id and its byte offset in the file ([MS-PST] 2.2.2.4).</summary>
internal readonly record struct Bref(ulong Bid, ulong Ib);

/// <summary>The file header ([MS-PST] 2.2.2.6), as much of it as reading needs.</summary>
internal sealed class Header
{
    /// <summary>The most bytes a header takes (the Unicode one; ANSI's is 512).</summary>
    public const int Size = 564;

    private Header(FileLayout layout, bool isOst, ushort version, PstEncoding encoding, ulong cbFile, Bref nbt, Bref bbt)
    {
        Layout = layout;
        IsOst = isOst;
        Version = version;
        Encoding = encoding;
        CbFile = cbFile;
        Nbt = nbt;
        Bbt = bbt;
    }

    public FileLayout Layout { get; }

    public bool IsOst { get; }

    /// <summary>wVer: 14/15 ANSI, 23 Unicode, 36+ the 4 KiB-page OST.</summary>
    public ushort Version { get; }

    public PstEncoding Encoding { get; }

    /// <summary>The file's size as the header says (a live OST may be larger on disk, pre-allocated).</summary>
    public ulong CbFile { get; }

    /// <summary>The root of the node B-tree.</summary>
    public Bref Nbt { get; }

    /// <summary>The root of the block B-tree.</summary>
    public Bref Bbt { get; }

    public static Header Parse(ReadOnlySpan<byte> h)
    {
        if (h.Length < 512 || h[0] != (byte)'!' || h[1] != (byte)'B' || h[2] != (byte)'D' || h[3] != (byte)'N')
        {
            throw new PstException("not an Outlook data file: the header does not start with !BDN");
        }

        var client = System.Text.Encoding.ASCII.GetString(h.Slice(8, 2));
        var isOst = client switch
        {
            "SM" => false,
            "SO" => true,
            _ => throw new PstException($"not an Outlook data file: wMagicClient is '{client}', not SM (PST) or SO (OST)"),
        };
        var version = BinaryPrimitives.ReadUInt16LittleEndian(h.Slice(10));
        var layout = version switch
        {
            14 or 15 => FileLayout.Ansi,
            23 => FileLayout.Unicode,
            >= 36 => FileLayout.Unicode4K,
            _ => throw new PstException($"an Outlook data file of version {version}, which this reader does not know (14, 15, 23 or 36+ expected)"),
        };
        if (h.Length < layout.CryptMethodOffset + 1)
        {
            throw new PstException("the header is cut short");
        }

        var encoding = (PstEncoding)h[layout.CryptMethodOffset];
        var root = layout.RootOffset;
        ulong cbFile;
        Bref nbt;
        Bref bbt;
        if (layout.Is64)
        {
            // ROOT: dwReserved(4) cbFile(8) ibAMapLast(8) ibAMapFree(8) ibPMapFree(8) BREFNBT(16) BREFBBT(16) ...
            cbFile = BinaryPrimitives.ReadUInt64LittleEndian(h.Slice(root + 4));
            nbt = new Bref(BinaryPrimitives.ReadUInt64LittleEndian(h.Slice(root + 36)), BinaryPrimitives.ReadUInt64LittleEndian(h.Slice(root + 44)));
            bbt = new Bref(BinaryPrimitives.ReadUInt64LittleEndian(h.Slice(root + 52)), BinaryPrimitives.ReadUInt64LittleEndian(h.Slice(root + 60)));
        }
        else
        {
            // ROOT: dwReserved(4) cbFile(4) ibAMapLast(4) ibAMapFree(4) ibPMapFree(4) BREFNBT(8) BREFBBT(8) ...
            cbFile = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(root + 4));
            nbt = new Bref(BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(root + 20)), BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(root + 24)));
            bbt = new Bref(BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(root + 28)), BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(root + 32)));
        }

        return new Header(layout, isOst, version, encoding, cbFile, nbt, bbt);
    }
}
