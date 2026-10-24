using System.Buffers.Binary;
using System.Text;
using LibOutlook.Ndb;

namespace LibOutlook;

/// <summary>
/// The compression of an RTF body as MAPI stores it ([MS-OXRTFCP]): LZ77 against a 4 KiB ring buffer seeded with the
/// usual RTF preamble, behind a 16-byte header; or stored plain behind the same header.
/// </summary>
public static class RtfCompression
{
    private const uint Compressed = 0x75465A4C;   // "LZFu"
    private const uint Uncompressed = 0x414C454D; // "MELA"

    /// <summary>The 207 bytes every ring buffer starts with ([MS-OXRTFCP] 2.1.3.1.3).</summary>
    private static readonly byte[] InitialDictionary = Encoding.Latin1.GetBytes(
        "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx");

    /// <summary>The RTF text held in a PidTagRtfCompressed value.</summary>
    /// <exception cref="PstException">The header is not one of the two known kinds, or the data is cut short or fails its checksum.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16)
        {
            throw new PstException($"compressed RTF of {data.Length} bytes: shorter than its 16-byte header");
        }

        var compressedSize = BinaryPrimitives.ReadInt32LittleEndian(data);
        var rawSize = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var kind = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (rawSize < 0 || compressedSize < 12)
        {
            throw new PstException($"compressed RTF whose header says {compressedSize} compressed and {rawSize} plain bytes");
        }

        // the first field counts itself out: the data runs from byte 4 for compressedSize bytes, i.e. to 4 + compressedSize
        var end = Math.Min(data.Length, 4 + compressedSize);
        var body = data[16..end];
        if (kind == Uncompressed)
        {
            return body[..Math.Min(rawSize, body.Length)].ToArray();
        }

        if (kind != Compressed)
        {
            throw new PstException($"compressed RTF with the signature 0x{kind:X8}, neither LZFu nor MELA");
        }

        var computed = Crypt.Crc(body);
        if (computed != crc)
        {
            throw new PstException($"compressed RTF whose checksum is 0x{computed:X8} where the header says 0x{crc:X8}");
        }

        var dictionary = new byte[4096];
        InitialDictionary.CopyTo(dictionary, 0);
        var writeAt = InitialDictionary.Length;
        var output = new byte[rawSize];
        var written = 0;
        var at = 0;
        while (at < body.Length && written < rawSize)
        {
            var flags = body[at++];
            for (var bit = 0; bit < 8 && written < rawSize; bit++, flags >>= 1)
            {
                if ((flags & 1) == 0)
                {
                    if (at >= body.Length)
                    {
                        return output.AsSpan(0, written).ToArray();
                    }

                    var b = body[at++];
                    output[written++] = b;
                    dictionary[writeAt] = b;
                    writeAt = (writeAt + 1) & 0xFFF;
                    continue;
                }

                if (at + 2 > body.Length)
                {
                    return output.AsSpan(0, written).ToArray();
                }

                var offset = (body[at] << 4) | (body[at + 1] >> 4);
                var length = (body[at + 1] & 0x0F) + 2;
                at += 2;
                if (offset == writeAt)
                {
                    // a reference to where the next byte would go marks the end
                    return output.AsSpan(0, written).ToArray();
                }

                for (var i = 0; i < length && written < rawSize; i++)
                {
                    var b = dictionary[(offset + i) & 0xFFF];
                    output[written++] = b;
                    dictionary[writeAt] = b;
                    writeAt = (writeAt + 1) & 0xFFF;
                }
            }
        }

        return written == rawSize ? output : output.AsSpan(0, written).ToArray();
    }

    /// <summary>The RTF text held in a PidTagRtfCompressed value, as a string (RTF is 7-bit, with escapes for the rest).</summary>
    public static string DecompressToString(ReadOnlySpan<byte> data) => Encoding.Latin1.GetString(Decompress(data));
}
