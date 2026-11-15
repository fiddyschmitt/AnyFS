using System.Buffers;

namespace AnyFS.Sdk;

/// <summary>
/// A per-handle read-ahead for a mount whose driver hands every application read to user mode as it is: WinFsp
/// (the volume runs without its full caching mode, which kept serving a file's old contents after it changed on the
/// source), FUSE (the kernel asks in pieces of 128 KiB) and SMB (clients read in pieces of the server's largest, 64 KiB).
/// On a source where a read is a round trip that caps a sequential reader far below the link: 17 MiB/s for WinFsp on
/// the lab's SSHFS, 8 MiB/s for FUSE on SharePoint (18-Oct-2026) against 26 MiB/s from a plain download. So a read that
/// goes on from where one of the handle's reads ended (or starts at the beginning) gets <see cref="AheadBytes"/> from
/// the file in one read, and the handle is served from that until a read runs past it, which takes the rest from the
/// next fill, made from where this one ended. A read out of sequence or bigger than <see cref="Threshold"/> goes to the
/// file as it is.
/// <para>
/// A handle may be read at several places in turn: the Mac's SMB client reads a big file at two, where it is and a
/// read-ahead of its own 16 MiB further on, 64 KiB at each in turn. Each place keeps a fill of its own, up to
/// <see cref="Tracks"/> of them; a new place takes the one used longest ago. With one fill for the handle, every read at
/// the other place threw away a fill made for this one, and the source's stream stood a fill further on than the next
/// read, so another was opened: 0.5 MiB/s from SharePoint (15-Nov-2026). For the same reason a fill starts where the one
/// before it ended, where the stream stands, never back at the start of the read that ran past it.
/// </para>
/// <para>
/// Nothing outlives the handle, so a reopen is as fresh as before; while a handle is open it may see what it read ahead,
/// like a client holding an SMB lease. One instance serves one handle and its calls must not overlap: the mount runs the
/// handle's reads and writes one at a time.
/// </para>
/// </summary>
public sealed class SequentialReadAhead : IDisposable
{
    /// <summary>What a sequential read fetches from the file at once.</summary>
    public const int DefaultAheadBytes = 1 << 20;

    /// <summary>Reads up to this size go through the fills; bigger ones reach the file as they are.</summary>
    public const int DefaultThreshold = 256 * 1024;

    /// <summary>How many places of one handle keep a fill of their own at once.</summary>
    public const int DefaultTracks = 4;

    private readonly Track[] _tracks;
    private long _uses;

    public SequentialReadAhead(int aheadBytes = DefaultAheadBytes, int threshold = DefaultThreshold, int tracks = DefaultTracks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(aheadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(threshold, aheadBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(tracks, 1);
        AheadBytes = aheadBytes;
        Threshold = threshold;
        _tracks = new Track[tracks];
        for (var i = 0; i < tracks; i++)
        {
            _tracks[i] = new Track();
        }
    }

    public int AheadBytes { get; }

    public int Threshold { get; }

    /// <summary>How many places of the handle keep a fill of their own at once.</summary>
    public int Tracks => _tracks.Length;

    /// <summary>
    /// Fills <paramref name="buffer"/> from <paramref name="file"/> at <paramref name="offset"/>, through a fill when one
    /// holds where the read starts or the read goes on from where one of the handle's reads ended; as many bytes as the
    /// buffer holds unless the file ends first (as <see cref="VfsFileExtensions.ReadFullyAsync"/>), so a short count
    /// means the end of the file.
    /// </summary>
    public async ValueTask<int> ReadAsync(IVfsFile file, long offset, Memory<byte> buffer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        var count = buffer.Length;
        if (count == 0)
        {
            return 0;
        }

        if (Holding(offset) is { } held)
        {
            held.Used = ++_uses;
            var have = (int)Math.Min(count, held.Offset + held.Length - offset);
            held.Buffer.AsMemory((int)(offset - held.Offset), have).CopyTo(buffer);
            if (have == count)
            {
                held.Next = offset + count;
                return count;
            }

            if (count > Threshold)
            {
                // a big read takes the rest from the file as it is, and the fill behind it is done with
                var rest = await file.ReadFullyAsync(offset + have, buffer[have..], ct).ConfigureAwait(false);
                held.Length = 0;
                held.Next = offset + have + rest;
                return have + rest;
            }

            // the rest from the next fill, from where this one ended
            var from = held.Offset + held.Length;
            var got = await FillAsync(file, held, from, ct).ConfigureAwait(false);
            var more = Math.Min(got, count - have);
            held.Buffer.AsMemory(0, more).CopyTo(buffer[have..]);
            held.Next = from + more;
            return have + more;
        }

        // a read that goes on from where one of the handle's reads ended, or one at the start, fills; any other goes to
        // the file as it is, and the place used longest ago moves to where it ends
        var track = GoingOnFrom(offset);
        var sequential = (track is not null || offset == 0) && count <= Threshold;
        track ??= LongestUnused();
        track.Used = ++_uses;
        if (!sequential)
        {
            track.Length = 0;
            var direct = await file.ReadFullyAsync(offset, buffer, ct).ConfigureAwait(false);
            track.Next = offset + direct;
            return direct;
        }

        var n = await FillAsync(file, track, offset, ct).ConfigureAwait(false);
        var served = Math.Min(n, count);
        track.Buffer.AsMemory(0, served).CopyTo(buffer);
        track.Next = offset + served;
        return served;
    }

    /// <summary>After a write or a resize through the handle: the fills and the places mean nothing now.</summary>
    public void Forget()
    {
        foreach (var track in _tracks)
        {
            track.Length = 0;
            track.Next = -1;
        }
    }

    /// <summary>Returns the buffers to the pool; the instance is not used again.</summary>
    public void Dispose()
    {
        foreach (var track in _tracks)
        {
            if (track.Buffer.Length > 0)
            {
                var rented = track.Buffer;
                track.Buffer = [];
                track.Length = 0;
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private Track? Holding(long offset)
    {
        foreach (var track in _tracks)
        {
            if (track.Length > 0 && offset >= track.Offset && offset < track.Offset + track.Length)
            {
                return track;
            }
        }

        return null;
    }

    private Track? GoingOnFrom(long offset)
    {
        foreach (var track in _tracks)
        {
            if (track.Next == offset)
            {
                return track;
            }
        }

        return null;
    }

    private Track LongestUnused()
    {
        var oldest = _tracks[0];
        foreach (var track in _tracks)
        {
            if (track.Used < oldest.Used)
            {
                oldest = track;
            }
        }

        return oldest;
    }

    private async ValueTask<int> FillAsync(IVfsFile file, Track track, long from, CancellationToken ct)
    {
        if (track.Buffer.Length == 0)
        {
            track.Buffer = ArrayPool<byte>.Shared.Rent(AheadBytes);
        }

        track.Length = 0;
        var n = await file.ReadFullyAsync(from, track.Buffer.AsMemory(0, AheadBytes), ct).ConfigureAwait(false);
        track.Offset = from;
        track.Length = n;
        return n;
    }

    /// <summary>One place of the handle: its fill, and where its last read ended.</summary>
    private sealed class Track
    {
        public byte[] Buffer = [];
        public long Offset;
        public int Length;
        public long Next = -1;
        public long Used;
    }
}
