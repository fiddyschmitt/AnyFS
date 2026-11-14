using System.Buffers;
using LibSmb.Protocol;

namespace LibSmb;

/// <summary>
/// A response being built: a buffer from the shared pool holding the 4-byte Direct TCP frame header, the 64-byte SMB2
/// header and the body, so a READ's data goes straight into the message that is sent. Returned to the pool once sent.
/// </summary>
internal sealed class Reply : IDisposable
{
    /// <summary>Room for the Direct TCP frame header ahead of the PDU.</summary>
    public const int Headroom = 4;

    private byte[]? _buffer;
    private SemaphoreSlim? _lease;

    private Reply(byte[] buffer, int bodyLength)
    {
        _buffer = buffer;
        BodyLength = bodyLength;
    }

    public int BodyLength { get; private set; }

    /// <summary>
    /// Makes it one of the big replies its connection keeps in memory at once (a READ's data): the lease is given back with
    /// the buffer, when the reply has been sent or is dropped.
    /// </summary>
    public SemaphoreSlim? Lease
    {
        set => _lease = value;
    }

    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(Reply));

    /// <summary>The body: the response's command-specific part.</summary>
    public Span<byte> Body => Buffer.AsSpan(Headroom + Smb2.HeaderSize, BodyLength);

    /// <summary>The whole PDU: the SMB2 header and the body.</summary>
    public Span<byte> Pdu => Buffer.AsSpan(Headroom, Smb2.HeaderSize + BodyLength);

    public int PduLength => Smb2.HeaderSize + BodyLength;

    /// <summary>A reply with a zeroed body of <paramref name="bodyLength"/> bytes.</summary>
    public static Reply Rent(int bodyLength)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Headroom + Smb2.HeaderSize + bodyLength);
        buffer.AsSpan(0, Headroom + Smb2.HeaderSize + bodyLength).Clear();
        return new Reply(buffer, bodyLength);
    }

    /// <summary>A reply whose body is <paramref name="body"/>.</summary>
    public static Reply Of(ReadOnlySpan<byte> body)
    {
        var reply = Rent(body.Length);
        body.CopyTo(reply.Body);
        return reply;
    }

    /// <summary>The ERROR response ([MS-SMB2] 2.2.2): StructureSize 9, no error data.</summary>
    public static Reply Error()
    {
        var reply = Rent(9);
        Wire.Put16(reply.Body, 0, 9);
        return reply;
    }

    /// <summary>A small fixed response with nothing but its StructureSize (ECHO, LOGOFF, TREE_DISCONNECT, FLUSH, LOCK: 4; SET_INFO: 2).</summary>
    public static Reply Fixed(ushort structureSize)
    {
        var reply = Rent(structureSize);
        Wire.Put16(reply.Body, 0, structureSize);
        return reply;
    }

    /// <summary>Makes the body shorter, keeping its start (a READ that read less than it asked for).</summary>
    public void Shrink(int bodyLength)
    {
        if (bodyLength > BodyLength)
        {
            throw new ArgumentOutOfRangeException(nameof(bodyLength));
        }

        BodyLength = bodyLength;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _buffer, null) is { } buffer)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        Interlocked.Exchange(ref _lease, null)?.Release();
    }

    /// <summary>Hands the buffer over to whoever sends it, who returns it to the pool, and its lease with it; the reply is done with.</summary>
    public SemaphoreSlim? ReleaseBuffer()
    {
        _buffer = null;
        return Interlocked.Exchange(ref _lease, null);
    }
}

/// <summary>A message on its way out: a frame (the 4-byte header and the message after it) in a pooled buffer.</summary>
internal sealed class Outbound(byte[] buffer, int length, SemaphoreSlim? lease = null) : IDisposable
{
    private byte[]? _buffer = buffer;
    private SemaphoreSlim? _lease = lease;

    /// <summary>Whether it is one of the big replies its connection counts already (see <see cref="Reply.Lease"/>).</summary>
    public bool Leased => _lease is not null;

    public ReadOnlyMemory<byte> Frame => (_buffer ?? throw new ObjectDisposedException(nameof(Outbound))).AsMemory(0, length);

    /// <summary>A single reply, sent from its own buffer.</summary>
    public static Outbound From(Reply reply)
    {
        var length = reply.PduLength;
        WriteFrameHeader(reply.Buffer, length);
        var buffer = reply.Buffer;
        return new Outbound(buffer, Reply.Headroom + length, reply.ReleaseBuffer());
    }

    /// <summary>Several replies chained into one compound message (already laid out by the caller).</summary>
    public static Outbound FromBuffer(byte[] pooledBuffer, int messageLength)
    {
        WriteFrameHeader(pooledBuffer, messageLength);
        return new Outbound(pooledBuffer, Reply.Headroom + messageLength);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _buffer, null) is { } b)
        {
            ArrayPool<byte>.Shared.Return(b);
        }

        Interlocked.Exchange(ref _lease, null)?.Release();
    }

    /// <summary>The Direct TCP frame header ([MS-SMB2] 2.1): a zero byte and the message's length in 24 bits, big-endian.</summary>
    private static void WriteFrameHeader(byte[] buffer, int messageLength)
    {
        buffer[0] = 0;
        buffer[1] = (byte)(messageLength >> 16);
        buffer[2] = (byte)(messageLength >> 8);
        buffer[3] = (byte)messageLength;
    }
}
