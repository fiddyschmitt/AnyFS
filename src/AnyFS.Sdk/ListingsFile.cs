using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sdk;

/// <summary>
/// The file a slow source's kept listings are written to (<see cref="KeptListingsOptions.SaveTo"/>), as they change: a log,
/// appended to every little while as the walks go and when the source stops, so a source stopped, ended or cut off half way
/// through a walk starts again from what it had, not from nothing, and stopping costs only what changed since the last
/// write (the user, 6-Nov-2026: two GitLab servers of tens of millions of files, closed a fifth of the way through their
/// first walks, walked again from the start). Written whole again when most of it says what later records undo.
/// Layout: <c>AFKL</c>, the format, then frames: the compressed length and the plain length (four bytes each, little
/// endian), then a gzip of records, the first of the file naming what the listings are for. A frame cut short (the process
/// ended while it was written) is left out, and the next write goes where it began. A listing's entries are the bytes the
/// cache holds (<see cref="ListingCodec"/>), written as they are and read back as they are. A file of another format is
/// not read (the formats before this one had no users; the user, 8-Nov-2026: no migration code), and the first save
/// writes this one in its place.
/// </summary>
internal sealed class ListingsFile
{
    private const int FormatVersion = 2;

    // a frame's records, before they are compressed, at most about this (a write of the whole is many frames)
    private const int FrameBytes = 4 << 20;

    // a frame saying it holds more than this is no frame of this format
    private const int LargestFrame = 1 << 30;

    private static ReadOnlySpan<byte> Magic => "AFKL"u8;

    private readonly string _path;
    private readonly string _key;
    private readonly ILogger _logger;
    private readonly string _name;

    // where the file's whole frames end, the next frame's place; -1 while the file must be written whole first (none yet,
    // another's, of another format, or a write that failed)
    private long _length = -1;

    public ListingsFile(string path, string key, ILogger logger, string name)
    {
        _path = path;
        _key = key;
        _logger = logger;
        _name = name;
    }

    /// <summary>How many listing records the file holds, superseded ones too: the file is written whole again when they are many more than the listings held.</summary>
    public long Records { get; private set; }

    /// <summary>Whether the next write must be the whole (<see cref="Rewrite"/>), not an append.</summary>
    public bool MustRewrite => _length < 0;

    /// <summary>Whether there is a file: for a source that keeps its listings in the database, where a run without the database left one.</summary>
    public bool Exists => File.Exists(_path);

    /// <summary>Deletes the file, and a write of it cut short, quietly: for a source that keeps its listings in the database.</summary>
    public void Delete()
    {
        foreach (var path in new[] { _path, _path + ".tmp" })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug("{Name}: {File} could not be deleted ({Error})", _name, path, ex.Message);
            }
        }
    }

    private enum Record : byte
    {
        Header = 1,
        Listing = 2,
        Removed = 3,
        Cleared = 4,
        FullWalkAt = 5,
        LeftOut = 6,
    }

    /// <summary>What the file holds for this <c>key</c>: nothing where there is no file, or it is another's or unreadable.</summary>
    public SavedState Load()
    {
        var state = new SavedState();
        Records = 0;
        FileStream file;
        try
        {
            file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogInformation("{Name}: the listings saved by the last run could not be read ({Error}); listing afresh", _name, ex.Message);
            return state;
        }

        using (file)
        {
            Span<byte> head = stackalloc byte[8];
            var read = file.ReadAtLeast(head, 8, throwOnEndOfStream: false);
            if (read < 8 || !head[..4].SequenceEqual(Magic) || BinaryPrimitives.ReadInt32LittleEndian(head[4..]) != FormatVersion)
            {
                _logger.LogInformation("{Name}: the listings saved by the last run are of another format; listing afresh", _name);
                return state;
            }

            // the strings read, each text once, for this reading alone
            var pool = new Dictionary<string, string>(StringComparer.Ordinal);
            var valid = 8L;
            var first = true;
            var header = new byte[8];
            while (true)
            {
                if (file.ReadAtLeast(header, 8, throwOnEndOfStream: false) < 8)
                {
                    break;
                }

                var packed = BinaryPrimitives.ReadInt32LittleEndian(header);
                var plain = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                if (packed <= 0 || plain <= 0 || packed > LargestFrame || plain > LargestFrame || file.Length - file.Position < packed)
                {
                    break;
                }

                var bytes = new byte[packed];
                file.ReadExactly(bytes);
                try
                {
                    var records = Decompress(bytes, plain);
                    if (!Apply(records, state, first, pool))
                    {
                        // another's listings, or of another source's settings: none of it is this run's
                        _logger.LogInformation("{Name}: the listings saved by the last run are for something else; listing afresh", _name);
                        return new SavedState();
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or FormatException or ArgumentException or OverflowException)
                {
                    break;
                }

                first = false;
                valid = file.Position;
            }

            if (first)
            {
                return new SavedState();
            }

            if (valid < file.Length)
            {
                _logger.LogInformation("{Name}: the end of the listings saved by the last run was cut short (it ended while writing it); what came before it is kept", _name);
            }

            _length = valid;
            return state;
        }
    }

    /// <summary>Appends a frame of what changed: listings held now (or held no more), the time of the last whole walk, what walks leave out.</summary>
    public void Append(bool cleared, IReadOnlyList<(string Path, StoredListing? Listing)> changes, DateTimeOffset? fullWalkAt, bool fullWalkChanged, IReadOnlyList<(string Folder, IReadOnlySet<string>? Names)> leftOut)
    {
        if (_length < 0)
        {
            throw new InvalidOperationException("the file is to be written whole first");
        }

        try
        {
            if (Path.GetDirectoryName(_path) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            using var file = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1 << 16);
            if (file.Length != _length)
            {
                // a frame cut short by a run that ended while writing it: written over
                file.SetLength(_length);
            }

            file.Position = _length;

            // frames go out as they fill: a run that ends during a long write (a first walk's first save, a million
            // listings) leaves the whole ones for the next start, and holds none of them in memory meanwhile
            using var frames = new FrameWriter(file);
            if (cleared)
            {
                frames.Writer.Write((byte)Record.Cleared);
            }

            foreach (var (path, listing) in changes)
            {
                WriteListing(frames.Writer, path, listing);
                frames.Next();
            }

            if (fullWalkChanged)
            {
                WriteFullWalkAt(frames.Writer, fullWalkAt);
            }

            foreach (var (left, names) in leftOut)
            {
                WriteLeftOut(frames.Writer, left, names);
            }

            frames.WriteTo(file);
            file.Flush(flushToDisk: true);
            _length = file.Position;
            Records += changes.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // what is in the file is not known now: the next write is the whole
            _length = -1;
            throw;
        }
    }

    /// <summary>Writes the whole file afresh (beside it, then moved into place): every listing held, the time of the last whole walk, what walks leave out.</summary>
    public void Rewrite(IEnumerable<(string Path, StoredListing Listing)> listings, DateTimeOffset? fullWalkAt, IEnumerable<(string Folder, IReadOnlySet<string> Names)> leftOut)
    {
        if (Path.GetDirectoryName(_path) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        var temp = _path + ".tmp";
        long records = 0;
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            Span<byte> head = stackalloc byte[8];
            Magic.CopyTo(head);
            BinaryPrimitives.WriteInt32LittleEndian(head[4..], FormatVersion);
            file.Write(head);
            using var frames = new FrameWriter(file);
            frames.Writer.Write((byte)Record.Header);
            frames.Writer.Write(_key);
            foreach (var (path, listing) in listings)
            {
                WriteListing(frames.Writer, path, listing);
                records++;
                frames.Next();
            }

            WriteFullWalkAt(frames.Writer, fullWalkAt);
            foreach (var (left, names) in leftOut)
            {
                WriteLeftOut(frames.Writer, left, names);
            }

            frames.WriteTo(file);
            file.Flush(flushToDisk: true);
        }

        // an antivirus holding the old file refuses the replace for a moment
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, _path, overwrite: true);
                break;
            }
            catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException) && attempt < 6)
            {
                Thread.Sleep(10 << (attempt - 1));
            }
        }

        _length = new FileInfo(_path).Length;
        Records = records;
    }

    // --- records -----------------------------------------------------------------------------------------------------

    private bool Apply(byte[] records, SavedState state, bool first, Dictionary<string, string> pool)
    {
        var reader = new RecordReader(records, pool);
        var atStart = true;
        while (!reader.AtEnd)
        {
            var kind = (Record)reader.ReadByte();
            if (first && atStart && (kind != Record.Header || reader.ReadString() != _key))
            {
                return false;
            }

            atStart = false;
            switch (kind)
            {
                case Record.Header:
                    if (!first)
                    {
                        throw new InvalidDataException("a header in the middle");
                    }

                    break;
                case Record.Listing:
                    var path = reader.ReadString();
                    var stamp = reader.ReadBoolean() ? reader.ReadPooled() : null;
                    var count = reader.Read7BitInt();
                    var folders = reader.Read7BitInt();
                    var packed = reader.Take(reader.Read7BitInt()).ToArray();
                    state.Listings[path] = new StoredListing(0, stamp, packed, count, folders);
                    Records++;
                    break;
                case Record.Removed:
                    state.Listings.Remove(reader.ReadString());
                    break;
                case Record.Cleared:
                    state.Listings.Clear();
                    break;
                case Record.FullWalkAt:
                    state.FullWalkAt = reader.ReadBoolean() ? reader.ReadTime() : null;
                    break;
                case Record.LeftOut:
                    var folder = reader.ReadString();
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    for (var n = reader.Read7BitInt(); n > 0; n--)
                    {
                        names.Add(reader.ReadPooled());
                    }

                    if (names.Count == 0)
                    {
                        state.LeftOut.Remove(folder);
                    }
                    else
                    {
                        state.LeftOut[folder] = names;
                    }

                    break;
                default:
                    throw new InvalidDataException($"a record of kind {(byte)kind}");
            }
        }

        return !atStart || !first;
    }

    private static void WriteListing(BinaryWriter writer, string path, StoredListing? listing)
    {
        if (listing is null)
        {
            writer.Write((byte)Record.Removed);
            writer.Write(path);
            return;
        }

        writer.Write((byte)Record.Listing);
        writer.Write(path);
        writer.Write(listing.Stamp is not null);
        if (listing.Stamp is not null)
        {
            writer.Write(listing.Stamp);
        }

        var packed = listing.Packed ?? ListingCodec.Pack(listing.List!, out _);
        writer.Write7BitEncodedInt(listing.Count);
        writer.Write7BitEncodedInt(listing.Folders);
        writer.Write7BitEncodedInt(packed.Length);
        writer.Write(packed);
    }

    private static void WriteFullWalkAt(BinaryWriter writer, DateTimeOffset? at)
    {
        writer.Write((byte)Record.FullWalkAt);
        writer.Write(at.HasValue);
        if (at is { } time)
        {
            writer.Write(time.Ticks);
            writer.Write((short)time.Offset.TotalMinutes);
        }
    }

    private static void WriteLeftOut(BinaryWriter writer, string folder, IReadOnlySet<string>? names)
    {
        writer.Write((byte)Record.LeftOut);
        writer.Write(folder);
        writer.Write7BitEncodedInt(names?.Count ?? 0);
        foreach (var name in names ?? (IEnumerable<string>)[])
        {
            writer.Write(name);
        }
    }

    /// <summary>
    /// Records read from a frame's bytes, as <see cref="BinaryWriter"/> wrote them (UTF-8 strings after their byte count in
    /// seven-bit groups, numbers little endian). The strings that come again and again (a commit's id as a stamp, a folder's
    /// left-out names) are made once from the bytes and shared, a repeat looked up without being made.
    /// </summary>
    private ref struct RecordReader(byte[] bytes, Dictionary<string, string> pool)
    {
        private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _pooled = pool.GetAlternateLookup<ReadOnlySpan<char>>();
        private int _at;

        public readonly bool AtEnd => _at >= bytes.Length;

        public byte ReadByte() => _at < bytes.Length ? bytes[_at++] : throw new EndOfStreamException();

        public bool ReadBoolean() => ReadByte() != 0;

        public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));

        public DateTimeOffset ReadTime()
        {
            var ticks = ReadInt64();
            var minutes = ReadInt16();
            return new DateTimeOffset(ticks, TimeSpan.FromMinutes(minutes));
        }

        public int Read7BitInt() => checked((int)Read7BitLong());

        public long Read7BitLong()
        {
            long value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                var b = ReadByte();
                value |= (long)(b & 0x7F) << shift;
                if (b < 0x80)
                {
                    return value;
                }
            }

            throw new FormatException("a seven-bit number too long");
        }

        /// <summary>A string made afresh (a path: each is a folder's own).</summary>
        public string ReadString() => Encoding.UTF8.GetString(Take(Read7BitInt()));

        /// <summary>A string shared with every other of the same text read before it.</summary>
        public string ReadPooled()
        {
            var utf8 = Take(Read7BitInt());
            if (utf8.Length > 512)
            {
                return Encoding.UTF8.GetString(utf8);
            }

            Span<char> chars = stackalloc char[512];
            var count = Encoding.UTF8.GetChars(utf8, chars);
            var text = chars[..count];
            if (_pooled.TryGetValue(text, out var known))
            {
                return known;
            }

            var made = new string(text);
            pool[made] = made;
            return made;
        }

        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || bytes.Length - _at < count)
            {
                throw new EndOfStreamException();
            }

            var span = bytes.AsSpan(_at, count);
            _at += count;
            return span;
        }
    }

    private static byte[] Decompress(byte[] packed, int plain)
    {
        var records = new byte[plain];
        using var zip = new GZipStream(new MemoryStream(packed, writable: false), CompressionMode.Decompress);
        zip.ReadExactly(records);
        return records;
    }

    /// <summary>Records gathered into frames: each compressed and written out once it holds <see cref="FrameBytes"/>, or at the end.</summary>
    private sealed class FrameWriter : IDisposable
    {
        private readonly MemoryStream _plain = new();
        private readonly Stream? _out;
        private readonly List<byte[]> _done = [];

        /// <param name="output">Where whole frames go as they fill; none, and they wait for <see cref="WriteTo"/>.</param>
        public FrameWriter(Stream? output = null)
        {
            _out = output;
            Writer = new BinaryWriter(_plain, Encoding.UTF8, leaveOpen: true);
        }

        public BinaryWriter Writer { get; }

        /// <summary>After a record: a frame that has grown to its size is finished.</summary>
        public void Next()
        {
            Writer.Flush();
            if (_plain.Length >= FrameBytes)
            {
                Finish();
            }
        }

        /// <summary>The frames not written yet, the last one finished, to <paramref name="file"/>.</summary>
        public void WriteTo(Stream file)
        {
            Writer.Flush();
            if (_plain.Length > 0)
            {
                Finish();
            }

            foreach (var frame in _done)
            {
                file.Write(frame);
            }

            _done.Clear();
        }

        private void Finish()
        {
            using var packed = new MemoryStream();
            packed.Write(new byte[8]);
            using (var zip = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zip.Write(_plain.GetBuffer(), 0, (int)_plain.Length);
            }

            var frame = packed.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(frame, frame.Length - 8);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), (int)_plain.Length);
            _plain.SetLength(0);
            if (_out is not null)
            {
                _out.Write(frame);
            }
            else
            {
                _done.Add(frame);
            }
        }

        public void Dispose()
        {
            Writer.Dispose();
            _plain.Dispose();
        }
    }
}

/// <summary>What a run left in <see cref="ListingsFile"/>: each folder's listing (as bytes, with its stamp), what walks leave out, when every folder was last listed.</summary>
internal sealed class SavedState
{
    public Dictionary<string, StoredListing> Listings { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, IReadOnlySet<string>> LeftOut { get; } = new(StringComparer.Ordinal);

    public DateTimeOffset? FullWalkAt { get; set; }
}
