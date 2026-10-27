using System.Buffers.Binary;
using System.Text;

namespace AnyFS.Sources.GitLab;

/// <summary>One entry of a zip archive's central directory: its path (with <c>/</c>), its uncompressed size, whether it is a folder.</summary>
internal sealed record ZipEntry(string Path, long Size, bool IsDirectory, DateTimeOffset? Modified);

/// <summary>
/// A zip archive's list of entries, read from its central directory at the archive's end (spec 7.8): a job's artifacts are
/// listed from a few kilobytes read at an offset, never by downloading the archive. Zip64 archives (over 4 GiB, or over
/// 65,535 entries) too. The bytes come through <c>read(offset, length)</c>, ranged requests or a local file alike.
/// </summary>
internal static class ZipDirectory
{
    private const uint EndOfCentralDirectory = 0x06054b50;
    private const uint Zip64EndLocator = 0x07064b50;
    private const uint Zip64End = 0x06064b50;
    private const uint CentralHeader = 0x02014b50;
    private const int EndRecordSize = 22;
    private const int MaxComment = 0xFFFF;

    /// <summary>The entries of an archive of <paramref name="length"/> bytes; throws <see cref="InvalidDataException"/> for something that is no zip.</summary>
    public static async Task<IReadOnlyList<ZipEntry>> ReadAsync(long length, Func<long, int, CancellationToken, Task<byte[]>> read, CancellationToken ct)
    {
        if (length < EndRecordSize)
        {
            throw new InvalidDataException("too short for a zip archive");
        }

        // the end record is the last 22 bytes, unless a comment (at most 65,535 bytes) follows it
        var tailLength = (int)Math.Min(length, EndRecordSize + MaxComment + 20);
        var tailStart = length - tailLength;
        var tail = await read(tailStart, tailLength, ct).ConfigureAwait(false);
        var end = -1;
        for (var i = tail.Length - EndRecordSize; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndOfCentralDirectory)
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            throw new InvalidDataException("no end of central directory record: not a zip archive");
        }

        long count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
        if (count == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF)
        {
            // Zip64: a locator just before the end record says where the Zip64 end record is
            var locator = end - 20;
            if (locator < 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator)) != Zip64EndLocator)
            {
                throw new InvalidDataException("a Zip64 archive without its Zip64 end locator");
            }

            var zip64At = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(locator + 8));
            var zip64 = await read(zip64At, 56, ct).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != Zip64End)
            {
                throw new InvalidDataException("the Zip64 end record is not where its locator says");
            }

            count = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32));
            size = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(40));
            offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48));
        }

        if (offset + size > length || size > int.MaxValue)
        {
            throw new InvalidDataException("the central directory is not inside the archive");
        }

        var directory = offset >= tailStart && offset + size <= length
            ? tail.AsSpan((int)(offset - tailStart), (int)size).ToArray()
            : await read(offset, (int)size, ct).ConfigureAwait(false);
        return Parse(directory, count);
    }

    private static List<ZipEntry> Parse(byte[] directory, long count)
    {
        var entries = new List<ZipEntry>((int)Math.Min(count, 100_000));
        var at = 0;
        while (at + 46 <= directory.Length && BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at)) == CentralHeader)
        {
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 8));
            var time = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 12));
            var date = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 14));
            long uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at + 24));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 32));
            if (at + 46 + nameLength + extraLength + commentLength > directory.Length)
            {
                break;
            }

            // bit 11: the name is UTF-8; otherwise code page 437, whose printable ASCII is the same
            var name = (flags & 0x800) != 0 ? Encoding.UTF8.GetString(directory, at + 46, nameLength) : Latin(directory.AsSpan(at + 46, nameLength));
            if (uncompressed == 0xFFFFFFFF)
            {
                uncompressed = Zip64Size(directory.AsSpan(at + 46 + nameLength, extraLength)) ?? uncompressed;
            }

            var isDirectory = name.EndsWith('/');
            entries.Add(new ZipEntry(name.TrimEnd('/'), isDirectory ? 0 : uncompressed, isDirectory, DosTime(date, time)));
            at += 46 + nameLength + extraLength + commentLength;
        }

        return entries;
    }

    /// <summary>The uncompressed size from a Zip64 extra field (header 0x0001), whose first value it is.</summary>
    private static long? Zip64Size(ReadOnlySpan<byte> extra)
    {
        while (extra.Length >= 4)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
            if (id == 0x0001 && size >= 8 && extra.Length >= 12)
            {
                return (long)BinaryPrimitives.ReadUInt64LittleEndian(extra[4..]);
            }

            extra = extra.Length >= 4 + size ? extra[(4 + size)..] : [];
        }

        return null;
    }

    private static string Latin(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i] = bytes[i] < 0x80 ? (char)bytes[i] : '_';
        }

        return new string(chars);
    }

    private static DateTimeOffset? DosTime(ushort date, ushort time)
    {
        try
        {
            var local = new DateTime(1980 + (date >> 9), (date >> 5) & 0xF, date & 0x1F, time >> 11, (time >> 5) & 0x3F, (time & 0x1F) * 2, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
