using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AnyFS.Fixtures.SmbStress;

/// <summary>
/// Deterministic file content, as portable-smb-server's stress worker has it (test/rig/worker/content.go). Every byte a
/// worker writes is a function of the run's seed, a content key (the file's share-relative path, slash-separated, when it
/// was made), the offset and the block's generation, so any party (the writer, another client reading across the share,
/// the audit on the host's own disk) can check any range of a file knowing only its path; nothing is shared between
/// machines but the seed. Content is made per 4 KiB block. A file that is rewritten carries a generation per block, bumped
/// on every overwrite, so a stale read (old data from a cache) is told apart from plain corruption.
/// </summary>
public sealed class Content(ulong seed, string key)
{
    public const int BlockSize = 4096;

    private const int VerifyChunk = 1 << 20;

    private readonly ulong _keyHash = HashKey(key);
    private readonly List<uint> _generations = [];

    public string Key { get; } = key;

    /// <summary>FNV-1a 64 over the key's bytes.</summary>
    public static ulong HashKey(string s)
    {
        var h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h = unchecked(h * 1099511628211UL);
        }

        return h;
    }

    /// <summary>The splitmix64 finaliser.</summary>
    private static ulong Mix(ulong x)
    {
        unchecked
        {
            x += 0x9e3779b97f4a7c15UL;
            x = (x ^ (x >> 30)) * 0xbf58476d1ce4e5b9UL;
            x = (x ^ (x >> 27)) * 0x94d049bb133111ebUL;
            return x ^ (x >> 31);
        }
    }

    /// <summary>One whole block of the splitmix64 stream from <paramref name="state"/>.</summary>
    private static void FillBlock(Span<byte> block, ulong state)
    {
        unchecked
        {
            for (var i = 0; i < BlockSize; i += 8)
            {
                state += 0x9e3779b97f4a7c15UL;
                var z = state;
                z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
                z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
                z ^= z >> 31;
                BinaryPrimitives.WriteUInt64LittleEndian(block[i..], z);
            }
        }
    }

    private uint Generation(long block) => block < _generations.Count ? _generations[(int)block] : 0;

    /// <summary>Advances the generation of every block in [b0, b1).</summary>
    public void Bump(long b0, long b1)
    {
        while (_generations.Count < b1)
        {
            _generations.Add(0);
        }

        for (var b = b0; b < b1; b++)
        {
            _generations[(int)b]++;
        }
    }

    /// <summary>Drops the generations of the blocks wholly beyond <paramref name="size"/> (a partial last block keeps its own).</summary>
    public void Truncate(long size)
    {
        var keep = (int)((size + BlockSize - 1) / BlockSize);
        if (_generations.Count > keep)
        {
            _generations.RemoveRange(keep, _generations.Count - keep);
        }
    }

    private ulong BlockState(long block, uint generation) => Mix(seed ^ Mix(_keyHash ^ Mix((ulong)block ^ ((ulong)generation << 40))));

    /// <summary>The expected bytes of [offset, offset + destination's length).</summary>
    public void Fill(Span<byte> destination, long offset) => FillGeneration(destination, offset, 0);

    /// <summary><see cref="Fill"/> with every block's generation lowered by <paramref name="back"/> (to recognise stale data).</summary>
    private void FillGeneration(Span<byte> destination, long offset, uint back)
    {
        Span<byte> block = stackalloc byte[BlockSize];
        while (destination.Length > 0)
        {
            var b = offset / BlockSize;
            var inBlock = (int)(offset % BlockSize);
            var g = Generation(b);
            g = back > g ? 0 : g - back;
            FillBlock(block, BlockState(b, g));
            var n = Math.Min(destination.Length, BlockSize - inBlock);
            block.Slice(inBlock, n).CopyTo(destination);
            destination = destination[n..];
            offset += n;
        }
    }

    /// <summary>Writes the expected bytes of [offset, offset + count) to the stream, at most <paramref name="chunk"/> at a time.</summary>
    public void WriteTo(Stream stream, long offset, long count, int chunk)
    {
        var buffer = new byte[chunk];
        while (count > 0)
        {
            var m = (int)Math.Min(buffer.Length, count);
            Fill(buffer.AsSpan(0, m), offset);
            stream.Write(buffer, 0, m);
            offset += m;
            count -= m;
        }
    }

    /// <summary>What a wrong chunk holds: zeros, an older generation (a stale cache), or something else.</summary>
    private string Diagnose(ReadOnlySpan<byte> got, long offset)
    {
        if (got.IndexOfAnyExcept((byte)0) < 0)
        {
            return "read all zeros";
        }

        var stale = new byte[got.Length];
        for (uint back = 1; back <= 3; back++)
        {
            FillGeneration(stale, offset, back);
            if (got.SequenceEqual(stale))
            {
                return $"read STALE data ({back} generation(s) old)";
            }
        }

        return "read unexpected bytes";
    }

    private void Compare(ReadOnlySpan<byte> got, long offset, Span<byte> want)
    {
        Fill(want, offset);
        if (got.SequenceEqual(want))
        {
            return;
        }

        var i = 0;
        while (i < got.Length && got[i] == want[i])
        {
            i++;
        }

        // diagnosed on the block that holds the first difference
        var bad = offset + i;
        var blockStart = Math.Max(bad - (bad % BlockSize), offset);
        var blockEnd = Math.Min(blockStart + BlockSize - (blockStart % BlockSize), offset + got.Length);
        throw new IOException($"content mismatch in \"{Key}\" at offset {bad} (block {bad / BlockSize}): {Diagnose(got[(int)(blockStart - offset)..(int)(blockEnd - offset)], blockStart)}");
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes from the stream and checks them against [offset, offset + count); with
    /// <paramref name="atEnd"/> the stream must end there too, which catches a file longer than expected. The bytes read.
    /// </summary>
    public long VerifyReader(Stream stream, long offset, long count, bool atEnd)
    {
        var chunk = (int)Math.Min(VerifyChunk, Math.Max(count, 1));
        var got = new byte[chunk];
        var want = new byte[chunk];
        long total = 0;
        while (count > 0)
        {
            var m = (int)Math.Min(got.Length, count);
            var k = 0;
            while (k < m)
            {
                var r = stream.Read(got, k, m - k);
                if (r <= 0)
                {
                    break;
                }

                k += r;
            }

            total += k;
            if (k > 0)
            {
                Compare(got.AsSpan(0, k), offset, want.AsSpan(0, k));
            }

            if (k < m)
            {
                throw new IOException($"short read of \"{Key}\": got {k} bytes, want {m} at offset {offset}");
            }

            offset += m;
            count -= m;
        }

        if (atEnd && stream.Read(new byte[1], 0, 1) > 0)
        {
            throw new IOException($"\"{Key}\" is longer than expected: data past offset {offset}");
        }

        return total;
    }

    /// <summary>Checks [offset, offset + count) with reads at offsets (random access).</summary>
    public void VerifyAt(SafeFileHandle handle, long offset, long count)
    {
        var chunk = (int)Math.Min(VerifyChunk, Math.Max(count, 1));
        var got = new byte[chunk];
        var want = new byte[chunk];
        while (count > 0)
        {
            var m = (int)Math.Min(got.Length, count);
            var k = 0;
            while (k < m)
            {
                var r = RandomAccess.Read(handle, got.AsSpan(k, m - k), offset + k);
                if (r <= 0)
                {
                    break;
                }

                k += r;
            }

            if (k > 0)
            {
                Compare(got.AsSpan(0, k), offset, want.AsSpan(0, k));
            }

            if (k < m)
            {
                throw new IOException($"short read of \"{Key}\": got {k} bytes, want {m} at offset {offset}");
            }

            offset += m;
            count -= m;
        }
    }
}
