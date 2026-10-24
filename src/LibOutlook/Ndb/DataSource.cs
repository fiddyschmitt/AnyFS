using System.IO.MemoryMappedFiles;

namespace LibOutlook.Ndb;

/// <summary>Where the file's bytes come from: a memory-mapped file, or any seekable stream.</summary>
internal interface IDataSource : IDisposable
{
    /// <summary>The file's length as last seen; a live file grows, see <see cref="Refresh"/>.</summary>
    long Length { get; }

    /// <summary>Reads exactly <paramref name="count"/> bytes at <paramref name="offset"/>; throws past the end.</summary>
    byte[] Read(long offset, int count);

    /// <summary>Takes in a file that grew since it was opened (Outlook extends its OST); nothing for a stream.</summary>
    void Refresh();
}

/// <summary>
/// A file read through a memory-mapped view, opened with full sharing: a file Outlook has open can be read too. Outlook
/// keeps a byte-range lock on the header (bytes 0-532 of an OST), which an ordinary read fails on with a lock violation,
/// but Windows does not apply such locks to mapped views (seen on the lab's OST, 24-Oct-2026).
/// </summary>
internal sealed class MappedFileData : IDataSource
{
    private readonly FileStream _file;
    private readonly object _gate = new();
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private long _length;

    public MappedFileData(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
        Map();
    }

    public long Length => _length;

    public byte[] Read(long offset, int count)
    {
        if (offset < 0 || count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (offset + count > _length)
        {
            Refresh();
            if (offset + count > _length)
            {
                throw new PstException($"a read of {count} bytes at {offset} runs past the end of the file ({_length} bytes)");
            }
        }

        var buffer = new byte[count];
        var view = _view ?? throw new ObjectDisposedException(nameof(MappedFileData));
        var read = view.ReadArray(offset, buffer, 0, count);
        if (read != count)
        {
            throw new PstException($"the mapped view gave {read} bytes of the {count} asked for at {offset}");
        }

        return buffer;
    }

    public void Refresh()
    {
        lock (_gate)
        {
            var now = _file.Length;
            if (now != _length)
            {
                _view?.Dispose();
                _map?.Dispose();
                _view = null;
                _map = null;
                Map();
            }
        }
    }

    private void Map()
    {
        _length = _file.Length;
        if (_length == 0)
        {
            throw new PstException("the file is empty");
        }

        _map = MemoryMappedFile.CreateFromFile(_file, null, _length, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        _view = _map.CreateViewAccessor(0, _length, MemoryMappedFileAccess.Read);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _view?.Dispose();
            _map?.Dispose();
            _view = null;
            _map = null;
        }

        _file.Dispose();
    }
}

/// <summary>Any seekable stream (a file read the ordinary way, a decompressed fixture in memory). Reads are serialised.</summary>
internal sealed class StreamData : IDataSource
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly object _gate = new();

    public StreamData(Stream stream, bool ownsStream)
    {
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("the stream must be readable and seekable", nameof(stream));
        }

        _stream = stream;
        _ownsStream = ownsStream;
    }

    public long Length => _stream.Length;

    public byte[] Read(long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > _stream.Length)
        {
            throw new PstException($"a read of {count} bytes at {offset} runs past the end of the data ({_stream.Length} bytes)");
        }

        var buffer = new byte[count];
        lock (_gate)
        {
            _stream.Position = offset;
            _stream.ReadExactly(buffer);
        }

        return buffer;
    }

    public void Refresh()
    {
    }

    public void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
