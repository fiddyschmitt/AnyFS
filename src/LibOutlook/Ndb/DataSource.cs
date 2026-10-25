using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;

namespace LibOutlook.Ndb;

/// <summary>Where the file's bytes come from: a file on disk, or any seekable stream.</summary>
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
/// A file read with positional reads, opened with full sharing, so a file Outlook has open can be read too. Outlook keeps
/// a byte-range lock on the header (bytes 0-532 of an OST), which an ordinary read fails on with a lock violation; such a
/// range is read through a memory-mapped view, which Windows does not hold to the lock (seen on the lab's OST,
/// 24-Oct-2026). The rest is read the ordinary way, through the system's file cache: a mapping of the whole of a mailbox
/// of gigabytes made every page read part of the reading program's own memory, and the window of that program waited
/// behind them (a real OST, 25-Oct-2026).
/// </summary>
internal sealed class FileData : IDataSource
{
    // ERROR_LOCK_VIOLATION as the HRESULT an IOException carries
    private const int LockViolation = unchecked((int)0x80070021);

    private readonly SafeFileHandle _handle;
    private readonly object _gate = new();
    private MemoryMappedFile? _map;
    private long _length;

    public FileData(string path)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        try
        {
            _length = RandomAccess.GetLength(_handle);
            if (_length == 0)
            {
                throw new PstException("the file is empty");
            }
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public long Length => Volatile.Read(ref _length);

    public byte[] Read(long offset, int count)
    {
        if (offset < 0 || count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (offset + count > Length)
        {
            Refresh();
            if (offset + count > Length)
            {
                throw new PstException($"a read of {count} bytes at {offset} runs past the end of the file ({Length} bytes)");
            }
        }

        var buffer = new byte[count];
        try
        {
            for (var done = 0; done < count;)
            {
                var read = RandomAccess.Read(_handle, buffer.AsSpan(done), offset + done);
                if (read <= 0)
                {
                    throw new PstException($"the file gave {done} bytes of the {count} asked for at {offset}");
                }

                done += read;
            }
        }
        catch (IOException ex) when (ex.HResult == LockViolation)
        {
            ReadMapped(offset, buffer);
        }

        return buffer;
    }

    /// <summary>A range another handle has locked (Outlook's header), through a view of a mapping made when first needed.</summary>
    private void ReadMapped(long offset, byte[] buffer)
    {
        lock (_gate)
        {
            _map ??= MemoryMappedFile.CreateFromFile(_handle, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            using var view = _map.CreateViewAccessor(offset, buffer.Length, MemoryMappedFileAccess.Read);
            var read = view.ReadArray(0, buffer, 0, buffer.Length);
            if (read != buffer.Length)
            {
                throw new PstException($"the mapped view gave {read} bytes of the {buffer.Length} asked for at {offset}");
            }
        }
    }

    public void Refresh()
    {
        lock (_gate)
        {
            var now = RandomAccess.GetLength(_handle);
            if (now != _length)
            {
                // a mapping is as long as the file was when it was made
                _map?.Dispose();
                _map = null;
                Volatile.Write(ref _length, now);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _map?.Dispose();
            _map = null;
        }

        _handle.Dispose();
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
