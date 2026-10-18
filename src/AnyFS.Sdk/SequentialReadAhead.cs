using System.Buffers;

namespace AnyFS.Sdk;

/// <summary>
/// A per-handle read-ahead for a mount whose driver hands every application read to user mode as it is: WinFsp
/// (the volume runs without its full caching mode, which kept serving a file's old contents after it changed on the
/// source) and FUSE (the kernel asks in pieces of 128 KiB). On a source where a read is a round trip that caps a
/// sequential reader far below the link: 17 MiB/s for WinFsp on the lab's SSHFS, 8 MiB/s for FUSE on SharePoint
/// (18-Oct-2026) against 26 MiB/s from a plain download. So a handle that reads on from where its previous read
/// ended (or starts at the beginning) gets <see cref="AheadBytes"/> from the file in one read and is served from
/// that until it runs out, seeks or writes; a read out of sequence or bigger than <see cref="Threshold"/> goes to
/// the file as it is and forgets what was read ahead. Nothing outlives the handle, so a reopen is as fresh as
/// before; while a handle is open it may see what it read ahead, like a client holding an SMB lease. One instance
/// serves one handle and its calls must not overlap: the mount runs the handle's reads and writes one at a time.
/// </summary>
public sealed class SequentialReadAhead : IDisposable
{
    /// <summary>What a sequential read fetches from the file at once.</summary>
    public const int DefaultAheadBytes = 1 << 20;

    /// <summary>Reads up to this size go through the buffer; bigger ones reach the file as they are.</summary>
    public const int DefaultThreshold = 256 * 1024;

    private byte[]? _ahead;
    private long _aheadOffset;
    private int _aheadLength;
    private long _nextRead = -1;

    public SequentialReadAhead(int aheadBytes = DefaultAheadBytes, int threshold = DefaultThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(aheadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threshold, aheadBytes);
        AheadBytes = aheadBytes;
        Threshold = threshold;
    }

    public int AheadBytes { get; }

    public int Threshold { get; }

    /// <summary>
    /// Fills <paramref name="buffer"/> from <paramref name="file"/> at <paramref name="offset"/>, through the read-ahead
    /// when the read follows on from the last one; as many bytes as the buffer holds unless the file ends first (as
    /// <see cref="VfsFileExtensions.ReadFullyAsync"/>), so a short count means the end of the file.
    /// </summary>
    public async ValueTask<int> ReadAsync(IVfsFile file, long offset, Memory<byte> buffer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        var count = buffer.Length;
        if (_ahead is { } held && count > 0 && offset >= _aheadOffset && offset + count <= _aheadOffset + _aheadLength)
        {
            held.AsMemory((int)(offset - _aheadOffset), count).CopyTo(buffer);
            _nextRead = offset + count;
            return count;
        }

        var sequential = (offset == _nextRead || (_nextRead < 0 && offset == 0)) && count <= Threshold;
        if (!sequential)
        {
            // a seek or a big read: whatever was read ahead no longer says where this handle is
            _aheadLength = 0;
            var direct = await file.ReadFullyAsync(offset, buffer, ct).ConfigureAwait(false);
            _nextRead = offset + direct;
            return direct;
        }

        _ahead ??= ArrayPool<byte>.Shared.Rent(AheadBytes);
        _aheadLength = 0;
        var n = await file.ReadFullyAsync(offset, _ahead.AsMemory(0, AheadBytes), ct).ConfigureAwait(false);
        _aheadOffset = offset;
        _aheadLength = n;
        var served = Math.Min(n, count);
        _ahead.AsMemory(0, served).CopyTo(buffer);
        _nextRead = offset + served;
        return served;
    }

    /// <summary>After a write or a resize through the handle: the buffer and the position mean nothing now.</summary>
    public void Forget()
    {
        _aheadLength = 0;
        _nextRead = -1;
    }

    /// <summary>Returns the buffer to the pool; the instance is not used again.</summary>
    public void Dispose()
    {
        if (_ahead is { } rented)
        {
            _ahead = null;
            _aheadLength = 0;
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
