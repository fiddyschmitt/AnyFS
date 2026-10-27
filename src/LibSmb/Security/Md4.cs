using System.Buffers.Binary;
using System.Numerics;

namespace LibSmb.Security;

/// <summary>
/// MD4 ([RFC 1320]), which NTLM needs for the NT hash of a password and .NET does not offer. It is broken as a hash;
/// here it only ever hashes a password for the protocol that requires it.
/// </summary>
internal static class Md4
{
    public static byte[] Hash(ReadOnlySpan<byte> data)
    {
        uint a = 0x67452301, b = 0xEFCDAB89, c = 0x98BADCFE, d = 0x10325476;

        // the message, a 1 bit, zeros to 56 mod 64, and its length in bits
        var length = ((data.Length + 8) / 64 + 1) * 64;
        var message = new byte[length];
        data.CopyTo(message);
        message[data.Length] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(length - 8), (ulong)data.Length * 8);

        Span<uint> x = stackalloc uint[16];
        for (var block = 0; block < length; block += 64)
        {
            for (var i = 0; i < 16; i++)
            {
                x[i] = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(block + i * 4));
            }

            uint aa = a, bb = b, cc = c, dd = d;

            // round 1: F(x, y, z) = (x & y) | (~x & z)
            foreach (var i in (ReadOnlySpan<int>)[0, 4, 8, 12])
            {
                a = Rotate(a + F(b, c, d) + x[i], 3);
                d = Rotate(d + F(a, b, c) + x[i + 1], 7);
                c = Rotate(c + F(d, a, b) + x[i + 2], 11);
                b = Rotate(b + F(c, d, a) + x[i + 3], 19);
            }

            // round 2: G(x, y, z) = (x & y) | (x & z) | (y & z), plus 0x5A827999
            foreach (var i in (ReadOnlySpan<int>)[0, 1, 2, 3])
            {
                a = Rotate(a + G(b, c, d) + x[i] + 0x5A827999, 3);
                d = Rotate(d + G(a, b, c) + x[i + 4] + 0x5A827999, 5);
                c = Rotate(c + G(d, a, b) + x[i + 8] + 0x5A827999, 9);
                b = Rotate(b + G(c, d, a) + x[i + 12] + 0x5A827999, 13);
            }

            // round 3: H(x, y, z) = x ^ y ^ z, plus 0x6ED9EBA1
            foreach (var i in (ReadOnlySpan<int>)[0, 2, 1, 3])
            {
                a = Rotate(a + H(b, c, d) + x[i] + 0x6ED9EBA1, 3);
                d = Rotate(d + H(a, b, c) + x[i + 8] + 0x6ED9EBA1, 9);
                c = Rotate(c + H(d, a, b) + x[i + 4] + 0x6ED9EBA1, 11);
                b = Rotate(b + H(c, d, a) + x[i + 12] + 0x6ED9EBA1, 15);
            }

            a += aa;
            b += bb;
            c += cc;
            d += dd;
        }

        var digest = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(0), a);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4), b);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8), c);
        BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12), d);
        return digest;
    }

    private static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);

    private static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);

    private static uint H(uint x, uint y, uint z) => x ^ y ^ z;

    private static uint Rotate(uint value, int bits) => BitOperations.RotateLeft(value, bits);
}

/// <summary>RC4, which NTLM's key exchange uses to carry the client's session key and .NET does not offer.</summary>
internal static class Rc4
{
    public static byte[] Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        Span<byte> s = stackalloc byte[256];
        for (var i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }

        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }

        return output;
    }
}
