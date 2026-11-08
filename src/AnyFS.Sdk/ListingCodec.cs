using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace AnyFS.Sdk;

/// <summary>
/// A folder's listing as bytes: the form a source that keeps its listings holds them in (<see cref="ListingCache"/>), writes
/// them to its file in (<see cref="ListingsFile"/>) and reads them back in, with nothing made of them until a client asks.
/// An entry is its flags (seven-bit groups), its name (UTF-8 after its byte count), then only what the flags say is there:
/// the length, the created time, the modified time unless it is the created time, the attributes, the etag (as bytes where
/// it is lowercase hex of even length, a git blob's id), the key. Times are ticks and the offset in minutes. Held so, a
/// server of tens of millions of files fits in memory (an entry as a record is a hundred bytes and more; as bytes, forty
/// to sixty), the file is written from what is held without a pass over it, and the start reads the file without making a
/// record of every entry.
/// </summary>
internal static class ListingCodec
{
    [Flags]
    private enum EntryFlags
    {
        None = 0,
        Directory = 1,
        HasLength = 2,
        HasCreated = 4,
        HasModified = 8,
        ModifiedIsCreated = 16,
        HasAttributes = 32,
        HasETag = 64,
        HasKey = 128,
        ETagHex = 256,
    }

    /// <summary>The entries as bytes, and how many of them are folders.</summary>
    public static byte[] Pack(IReadOnlyList<VfsEntry> listing, out int folders)
    {
        folders = 0;
        var writer = new ByteWriter(Math.Max(64, listing.Count * 48));
        try
        {
            foreach (var entry in listing)
            {
                if (entry.IsDirectory)
                {
                    folders++;
                }

                WriteEntry(ref writer, entry);
            }

            return writer.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>The entries back from their bytes.</summary>
    public static VfsEntry[] Unpack(ReadOnlySpan<byte> packed, int count)
    {
        var reader = new ByteReader(packed);
        var listing = new VfsEntry[count];
        for (var i = 0; i < count; i++)
        {
            listing[i] = ReadEntry(ref reader);
        }

        return listing;
    }

    /// <summary>The names of the entries that are folders, in order, nothing else made.</summary>
    public static List<string> FolderNames(ReadOnlySpan<byte> packed, int count, int folders)
    {
        var names = new List<string>(folders);
        var reader = new ByteReader(packed);
        for (var i = 0; i < count && names.Count < folders; i++)
        {
            var flags = (EntryFlags)reader.Read7BitInt();
            var name = reader.ReadString();
            if (flags.HasFlag(EntryFlags.Directory))
            {
                names.Add(name);
            }

            SkipRest(ref reader, flags);
        }

        return names;
    }

    private static void WriteEntry(ref ByteWriter writer, VfsEntry entry)
    {
        var flags = EntryFlags.None;
        if (entry.IsDirectory)
        {
            flags |= EntryFlags.Directory;
        }

        if (entry.Length != 0)
        {
            flags |= EntryFlags.HasLength;
        }

        if (entry.Created.HasValue)
        {
            flags |= EntryFlags.HasCreated;
        }

        if (entry.Modified is { } modified)
        {
            flags |= entry.Created is { } created && created.Ticks == modified.Ticks && created.Offset == modified.Offset ? EntryFlags.ModifiedIsCreated : EntryFlags.HasModified;
        }

        if (entry.Attributes != VfsAttributes.None)
        {
            flags |= EntryFlags.HasAttributes;
        }

        var hex = false;
        if (entry.ETag is { } tag)
        {
            flags |= EntryFlags.HasETag;
            hex = IsLowerHex(tag);
            if (hex)
            {
                flags |= EntryFlags.ETagHex;
            }
        }

        if (entry.Key is not null)
        {
            flags |= EntryFlags.HasKey;
        }

        writer.Write7Bit((ulong)flags);
        writer.WriteString(entry.Name);
        if (flags.HasFlag(EntryFlags.HasLength))
        {
            writer.Write7Bit((ulong)entry.Length);
        }

        if (entry.Created is { } c)
        {
            writer.WriteTime(c);
        }

        if (flags.HasFlag(EntryFlags.HasModified))
        {
            writer.WriteTime(entry.Modified!.Value);
        }

        if (flags.HasFlag(EntryFlags.HasAttributes))
        {
            writer.Write7Bit((ulong)(int)entry.Attributes);
        }

        if (entry.ETag is { } etag)
        {
            if (hex)
            {
                writer.WriteHex(etag);
            }
            else
            {
                writer.WriteString(etag);
            }
        }

        if (entry.Key is { } key)
        {
            writer.WriteString(key);
        }
    }

    private static VfsEntry ReadEntry(ref ByteReader reader)
    {
        var flags = (EntryFlags)reader.Read7BitInt();
        var name = reader.ReadString();
        var length = flags.HasFlag(EntryFlags.HasLength) ? reader.Read7BitLong() : 0;
        DateTimeOffset? created = flags.HasFlag(EntryFlags.HasCreated) ? reader.ReadTime() : null;
        DateTimeOffset? modified = flags.HasFlag(EntryFlags.HasModified) ? reader.ReadTime() : flags.HasFlag(EntryFlags.ModifiedIsCreated) ? created : null;
        var attributes = flags.HasFlag(EntryFlags.HasAttributes) ? (VfsAttributes)reader.Read7BitInt() : VfsAttributes.None;
        var etag = flags.HasFlag(EntryFlags.HasETag) ? flags.HasFlag(EntryFlags.ETagHex) ? reader.ReadHex() : reader.ReadString() : null;
        var key = flags.HasFlag(EntryFlags.HasKey) ? reader.ReadString() : null;
        return new VfsEntry(name, flags.HasFlag(EntryFlags.Directory), length, created, modified, attributes, etag) { Key = key };
    }

    private static void SkipRest(ref ByteReader reader, EntryFlags flags)
    {
        if (flags.HasFlag(EntryFlags.HasLength))
        {
            reader.Read7BitLong();
        }

        if (flags.HasFlag(EntryFlags.HasCreated))
        {
            reader.Skip(10);
        }

        if (flags.HasFlag(EntryFlags.HasModified))
        {
            reader.Skip(10);
        }

        if (flags.HasFlag(EntryFlags.HasAttributes))
        {
            reader.Read7BitInt();
        }

        if (flags.HasFlag(EntryFlags.HasETag))
        {
            reader.Skip(reader.Read7BitInt());
        }

        if (flags.HasFlag(EntryFlags.HasKey))
        {
            reader.Skip(reader.Read7BitInt());
        }
    }

    private static bool IsLowerHex(string s)
    {
        if (s.Length is < 2 or > 256 || (s.Length & 1) != 0)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Bytes written to a buffer from the pool: numbers in seven-bit groups, strings in UTF-8 after their byte count.</summary>
    public struct ByteWriter(int capacity)
    {
        private byte[] _bytes = ArrayPool<byte>.Shared.Rent(capacity);
        private int _at;

        public readonly int Length => _at;

        public byte[] ToArray() => _bytes.AsSpan(0, _at).ToArray();

        public readonly ReadOnlySpan<byte> Written => _bytes.AsSpan(0, _at);

        public void Write7Bit(ulong value)
        {
            while (value >= 0x80)
            {
                Byte((byte)(value | 0x80));
                value >>= 7;
            }

            Byte((byte)value);
        }

        public void WriteString(string s)
        {
            var count = Encoding.UTF8.GetByteCount(s);
            Write7Bit((ulong)count);
            Ensure(count);
            Encoding.UTF8.GetBytes(s, _bytes.AsSpan(_at));
            _at += count;
        }

        public void WriteHex(string hex)
        {
            var count = hex.Length / 2;
            Write7Bit((ulong)count);
            Ensure(count);
            Convert.FromHexString(hex, _bytes.AsSpan(_at, count), out _, out _);
            _at += count;
        }

        public void WriteTime(DateTimeOffset time)
        {
            Ensure(10);
            BinaryPrimitives.WriteInt64LittleEndian(_bytes.AsSpan(_at), time.Ticks);
            BinaryPrimitives.WriteInt16LittleEndian(_bytes.AsSpan(_at + 8), (short)time.Offset.TotalMinutes);
            _at += 10;
        }

        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            Ensure(bytes.Length);
            bytes.CopyTo(_bytes.AsSpan(_at));
            _at += bytes.Length;
        }

        private void Byte(byte b)
        {
            Ensure(1);
            _bytes[_at++] = b;
        }

        private void Ensure(int more)
        {
            if (_bytes.Length - _at >= more)
            {
                return;
            }

            var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(_bytes.Length * 2, _at + more));
            _bytes.AsSpan(0, _at).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_bytes);
            _bytes = bigger;
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_bytes);
            _bytes = [];
        }
    }

    /// <summary>Bytes read as <see cref="ByteWriter"/> wrote them; past the end is a failure, never a wrong value.</summary>
    public ref struct ByteReader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _at;

        public readonly bool AtEnd => _at >= _bytes.Length;

        public readonly int Position => _at;

        public byte ReadByte() => _at < _bytes.Length ? _bytes[_at++] : throw new EndOfStreamException();

        public bool ReadBoolean() => ReadByte() != 0;

        public int Read7BitInt() => checked((int)Read7BitLong());

        public long Read7BitLong()
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                var b = ReadByte();
                value |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                {
                    return (long)value;
                }
            }

            throw new FormatException("a seven-bit number too long");
        }

        public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

        public DateTimeOffset ReadTime()
        {
            var ticks = ReadInt64();
            var minutes = ReadInt16();
            return new DateTimeOffset(ticks, TimeSpan.FromMinutes(minutes));
        }

        public string ReadString() => Encoding.UTF8.GetString(Take(Read7BitInt()));

        /// <summary>The string's bytes, for a reader that shares strings (<see cref="ListingsFile"/>).</summary>
        public ReadOnlySpan<byte> ReadStringBytes() => Take(Read7BitInt());

        public string ReadHex() => Convert.ToHexStringLower(Take(Read7BitInt()));

        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _bytes.Length - _at < count)
            {
                throw new EndOfStreamException();
            }

            var span = _bytes.Slice(_at, count);
            _at += count;
            return span;
        }

        public void Skip(int count) => Take(count);
    }
}
