using System.Buffers;
using System.Security.Cryptography;
using LibSmb.Protocol;

namespace LibSmb.Security;

/// <summary>
/// A session's message signing ([MS-SMB2] 3.1.4.1): HMAC-SHA256 with the session key for the 2.x dialects, AES-CMAC with
/// a key derived from it for 3.x. The signature covers the whole PDU with its signature field zeroed and the SIGNED flag
/// set. One instance per session; its methods do not run at the same time.
/// </summary>
internal sealed class Signer : IDisposable
{
    private readonly byte[] _key;
    private readonly bool _cmac;
    private readonly AesCmac? _aes;

    public Signer(byte[] sessionKey, ushort dialect)
    {
        _cmac = Dialect.IsSmb3(dialect);
        _key = _cmac ? Kdf(sessionKey, "SMB2AESCMAC\0"u8, "SmbSign\0"u8) : sessionKey;
        _aes = _cmac ? new AesCmac(_key) : null;
    }

    /// <summary>The key messages are signed with (for the tests).</summary>
    internal ReadOnlySpan<byte> Key => _key;

    /// <summary>Signs a PDU in place: SIGNED set in its flags, the signature in its header.</summary>
    public void Sign(Span<byte> pdu)
    {
        pdu.Slice(48, 16).Clear();
        Wire.Put32(pdu, 16, Wire.U32(pdu, 16) | (uint)HeaderFlags.Signed);
        Compute(pdu, pdu.Slice(48, 16));
    }

    /// <summary>Whether a signed PDU carries the signature this session gives it; the PDU is left as it came.</summary>
    public bool Verify(Span<byte> pdu)
    {
        if (pdu.Length < Smb2.HeaderSize)
        {
            return false;
        }

        Span<byte> received = stackalloc byte[16];
        pdu.Slice(48, 16).CopyTo(received);
        pdu.Slice(48, 16).Clear();
        Span<byte> expected = stackalloc byte[16];
        Compute(pdu, expected);
        received.CopyTo(pdu.Slice(48, 16));
        return CryptographicOperations.FixedTimeEquals(received, expected);
    }

    public void Dispose() => _aes?.Dispose();

    /// <summary>The SP 800-108 counter-mode KDF the SMB 3.x keys come from (HMAC-SHA256, r = 32, L = 128).</summary>
    public static byte[] Kdf(ReadOnlySpan<byte> key, ReadOnlySpan<byte> label, ReadOnlySpan<byte> context)
    {
        var input = new byte[4 + label.Length + 1 + context.Length + 4];
        input[3] = 1;
        label.CopyTo(input.AsSpan(4));
        context.CopyTo(input.AsSpan(4 + label.Length + 1));

        // L, the bits wanted, big-endian: 128
        input[^1] = 0x80;
        return HMACSHA256.HashData(key, input)[..16];
    }

    private void Compute(ReadOnlySpan<byte> pdu, Span<byte> signature)
    {
        if (_aes is not null)
        {
            _aes.Compute(pdu, signature);
            return;
        }

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_key, pdu, mac);
        mac[..16].CopyTo(signature);
    }
}

/// <summary>
/// AES-CMAC ([RFC 4493], NIST SP 800-38B). CMAC is CBC-MAC with the last block tweaked by a subkey, so every whole block
/// before the last goes through .NET's CBC encryption (its fastest AES path) in chunks, the chain carried over as the
/// next chunk's IV, and only the last block is done by hand. Signing a 1 MiB READ response a block at a time would cost
/// more than serving it.
/// </summary>
internal sealed class AesCmac : IDisposable
{
    private const int BlockSize = 16;
    private const int Chunk = 64 << 10;
    private readonly Aes _aes;
    private readonly byte[] _k1 = new byte[BlockSize];
    private readonly byte[] _k2 = new byte[BlockSize];

    public AesCmac(byte[] key)
    {
        _aes = Aes.Create();
        _aes.Key = key;
        Span<byte> l = stackalloc byte[BlockSize];
        _aes.EncryptEcb(new byte[BlockSize], l, PaddingMode.None);
        MultiplyByX(l, _k1);
        MultiplyByX(_k1, _k2);
    }

    public void Compute(ReadOnlySpan<byte> message, Span<byte> mac)
    {
        var full = message.Length / BlockSize;
        var rem = message.Length % BlockSize;
        if (rem == 0 && full > 0)
        {
            full--;
            rem = BlockSize;
        }

        // the chain: the last cipher block so far
        Span<byte> x = stackalloc byte[BlockSize];
        if (full > 0)
        {
            var scratch = ArrayPool<byte>.Shared.Rent(Chunk);
            try
            {
                for (int offset = 0, end = full * BlockSize; offset < end;)
                {
                    var n = Math.Min(end - offset, Chunk);
                    _aes.EncryptCbc(message.Slice(offset, n), x, scratch.AsSpan(0, n), PaddingMode.None);
                    scratch.AsSpan(n - BlockSize, BlockSize).CopyTo(x);
                    offset += n;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }

        Span<byte> last = stackalloc byte[BlockSize];
        message[(full * BlockSize)..].CopyTo(last);
        if (rem == BlockSize)
        {
            Xor(last, _k1);
        }
        else
        {
            last[rem] = 0x80;
            Xor(last, _k2);
        }

        Xor(x, last);
        _aes.EncryptEcb(x, mac, PaddingMode.None);
    }

    public void Dispose() => _aes.Dispose();

    /// <summary>Times x in GF(2^128): a left shift, reduced by the CMAC polynomial 0x87 on a carry (how RFC 4493 derives the subkeys).</summary>
    private static void MultiplyByX(ReadOnlySpan<byte> input, Span<byte> output)
    {
        byte carry = 0;
        for (var i = BlockSize - 1; i >= 0; i--)
        {
            output[i] = (byte)((input[i] << 1) | carry);
            carry = (byte)(input[i] >> 7);
        }

        if (carry != 0)
        {
            output[BlockSize - 1] ^= 0x87;
        }
    }

    private static void Xor(Span<byte> target, ReadOnlySpan<byte> other)
    {
        for (var i = 0; i < BlockSize; i++)
        {
            target[i] ^= other[i];
        }
    }
}
