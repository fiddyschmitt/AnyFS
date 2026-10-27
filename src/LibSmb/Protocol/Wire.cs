using System.Buffers.Binary;
using System.Text;

namespace LibSmb.Protocol;

/// <summary>The fields of an SMB2 request header the server acts on ([MS-SMB2] 2.2.1.2).</summary>
internal readonly record struct Header(
    ushort CreditCharge,
    Command Command,
    ushort CreditRequest,
    HeaderFlags Flags,
    uint NextCommand,
    ulong MessageId,
    uint TreeId,
    ulong SessionId)
{
    public bool Related => (Flags & HeaderFlags.RelatedOperations) != 0;

    public bool Signed => (Flags & HeaderFlags.Signed) != 0;

    /// <summary>The 64-byte header at the start of <paramref name="pdu"/>, or false when it is not one.</summary>
    public static bool TryParse(ReadOnlySpan<byte> pdu, out Header header)
    {
        if (!Smb2.IsSmb2(pdu))
        {
            header = default;
            return false;
        }

        header = new Header(
            Wire.U16(pdu, 6),
            (Command)Wire.U16(pdu, 12),
            Wire.U16(pdu, 14),
            (HeaderFlags)Wire.U32(pdu, 16),
            Wire.U32(pdu, 20),
            Wire.U64(pdu, 24),
            Wire.U32(pdu, 36),
            Wire.U64(pdu, 40));
        return true;
    }

    /// <summary>
    /// Writes the header of the response to this request into <paramref name="pdu"/>: the request's MessageId and
    /// CreditCharge echoed, the status, session and tree, and the credits granted. NextCommand and the signature are left
    /// zero (compounding and signing come later).
    /// </summary>
    public void WriteResponse(Span<byte> pdu, uint status, ulong sessionId, uint treeId, ushort credits)
    {
        pdu[..Smb2.HeaderSize].Clear();
        Smb2.Magic.CopyTo(pdu);
        Wire.Put16(pdu, 4, Smb2.HeaderSize);
        Wire.Put16(pdu, 6, CreditCharge);
        Wire.Put32(pdu, 8, status);
        Wire.Put16(pdu, 12, (ushort)Command);
        Wire.Put16(pdu, 14, credits);
        Wire.Put32(pdu, 16, (uint)HeaderFlags.ServerToRedirector);
        Wire.Put64(pdu, 24, MessageId);
        Wire.Put32(pdu, 36, treeId);
        Wire.Put64(pdu, 40, sessionId);
    }
}

/// <summary>Little-endian fields, UTF-16LE strings and FILETIMEs: the SMB2 wire's encodings.</summary>
internal static class Wire
{
    /// <summary>100-nanosecond intervals from the FILETIME epoch (1601-01-01) to the Unix epoch (1970-01-01).</summary>
    private const long FileTimeEpochDelta = 116444736000000000;

    public static ushort U16(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b[offset..]);

    public static uint U32(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b[offset..]);

    public static ulong U64(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(b[offset..]);

    public static void Put16(Span<byte> b, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b[offset..], value);

    public static void Put32(Span<byte> b, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b[offset..], value);

    public static void Put64(Span<byte> b, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(b[offset..], value);

    /// <summary>A UTF-16LE string (an odd last byte dropped).</summary>
    public static string Utf16(ReadOnlySpan<byte> b) => Encoding.Unicode.GetString(b[..(b.Length & ~1)]);

    public static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);

    public static int Utf16Length(string s) => s.Length * 2;

    public static void PutUtf16(Span<byte> b, int offset, string s) => Encoding.Unicode.GetBytes(s, b[offset..]);

    /// <summary>A time as a FILETIME; 0 for none.</summary>
    public static ulong FileTime(DateTimeOffset? time)
        => time is { } t && t.UtcTicks > DateTimeOffset.UnixEpoch.UtcTicks - FileTimeEpochDelta
            ? (ulong)(t.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks + FileTimeEpochDelta)
            : 0;

    /// <summary>A FILETIME as a time; null for 0 and for -1 (which a SET_INFO uses to mean "leave as is").</summary>
    public static DateTimeOffset? FromFileTime(ulong fileTime)
    {
        if (fileTime == 0 || fileTime >= long.MaxValue)
        {
            return null;
        }

        var ticks = (long)fileTime - FileTimeEpochDelta + DateTimeOffset.UnixEpoch.UtcTicks;
        return ticks is >= 0 and <= 3155378975999999999 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    }

    /// <summary>
    /// The slice of a request body that an offset (counted from the start of the SMB2 header, as every SMB2 offset is)
    /// and a length point at; empty when the length is 0 or the slice falls outside the body.
    /// </summary>
    public static ReadOnlySpan<byte> At(ReadOnlySpan<byte> body, int offsetFromHeader, int length)
    {
        var start = offsetFromHeader - Smb2.HeaderSize;
        return start < 0 || length <= 0 || (long)start + length > body.Length ? default : body.Slice(start, length);
    }

    public static int RoundUp(int n, int alignment) => (n + alignment - 1) & ~(alignment - 1);
}
