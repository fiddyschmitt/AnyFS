namespace LibOutlook.Ndb;

/// <summary>[MS-PST] section 5: the block obfuscations (5.1, 5.2), the CRC (5.3) and the block signature (5.5).</summary>
internal static class Crypt
{
    private static readonly uint[] CrcTable = MakeCrcTable();

    /// <summary>Decodes a data block in place by the file's method; <paramref name="bid"/> keys the cyclic one.</summary>
    public static void Decode(Span<byte> data, PstEncoding method, ulong bid)
    {
        switch (method)
        {
            case PstEncoding.None:
                return;
            case PstEncoding.Permute:
                // CryptPermute with fEncrypt false: every byte through mpbbI
                var i = CryptTables.I;
                for (var n = 0; n < data.Length; n++)
                {
                    data[n] = i[data[n]];
                }

                return;
            case PstEncoding.Cyclic:
                // CryptCyclic: symmetric; dwKey is the low DWORD of the BID
                var key = (uint)bid;
                var w = (ushort)(key ^ (key >> 16));
                var r = CryptTables.R;
                var s = CryptTables.S;
                var inv = CryptTables.I;
                for (var n = 0; n < data.Length; n++)
                {
                    var b = data[n];
                    b = (byte)(b + (byte)w);
                    b = r[b];
                    b = (byte)(b + (byte)(w >> 8));
                    b = s[b];
                    b = (byte)(b - (byte)(w >> 8));
                    b = inv[b];
                    b = (byte)(b - (byte)w);
                    data[n] = b;
                    w++;
                }

                return;
            default:
                throw new PstException($"the file's data is protected with a method this reader cannot undo (bCryptMethod {(int)method})");
        }
    }

    /// <summary>ComputeCRC: the standard CRC-32 table, seed 0, no final inversion ("a weak CRC32").</summary>
    public static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>ComputeSig: the block's offset XOR its id, folded to 16 bits.</summary>
    public static ushort Signature(ulong ib, ulong bid)
    {
        var x = (uint)(ib ^ bid);
        return (ushort)((x >> 16) ^ x);
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
