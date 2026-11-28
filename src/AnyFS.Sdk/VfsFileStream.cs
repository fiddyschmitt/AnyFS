namespace AnyFS.Sdk;

/// <summary>
/// A seekable <see cref="Stream"/> over an <see cref="IVfsFile"/>, for mounts and libraries that want one
/// (WebDAV, FTP, HTTP responses). Each stream keeps its own position; the file stays positionless.
/// <para>
/// One that is only read may read ahead (<see cref="SequentialReadAhead"/>): what copies a stream out asks for some
/// tens of kilobytes at a time, each of them a round trip on many a source, where the mounts that serve a volume read
/// a megabyte ahead. And one may be made of a file not yet opened, which is opened when it is first read and not at
/// all if it never is: an answer that has headers and no body (a HEAD, a "not modified") needs the file's length and
/// nothing else of it.
/// </para>
/// </summary>
public sealed class VfsFileStream : Stream
{
    private readonly Func<CancellationToken, Task<IVfsFile>>? _open;
    private readonly SequentialReadAhead? _ahead;
    private readonly bool _ownsFile;
    private readonly bool _writable;
    private IVfsFile? _file;
    private long _position;
    private long _length;
    private bool _disposed;

    /// <param name="readAhead">For a stream that is only read: its reads go through a read-ahead of its own.</param>
    public VfsFileStream(IVfsFile file, bool writable = false, bool ownsFile = false, bool readAhead = false)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        _writable = writable;
        _ownsFile = ownsFile;
        _length = file.Length;
        _ahead = readAhead && !writable ? new SequentialReadAhead() : null;
    }

    /// <summary>A stream to read a file of <paramref name="length"/> bytes that <paramref name="open"/> opens when it is first read; the stream's to close.</summary>
    public VfsFileStream(long length, Func<CancellationToken, Task<IVfsFile>> open, bool readAhead = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentNullException.ThrowIfNull(open);
        _open = open;
        _ownsFile = true;
        _length = length;
        _ahead = readAhead ? new SequentialReadAhead() : null;
    }

    /// <summary>Whether the file has been opened: always, but for a stream made of one not yet opened and never read.</summary>
    public bool IsOpened => _file is not null;

    /// <summary>
    /// The stream a mount answers a request for a file from. An answer that has no body (<paramref name="bodiless"/>:
    /// a HEAD, a client whose copy still stands) is made of the file unopened: its length is the listing's, and the
    /// file is opened only if the answer turns out to want it after all. Any other is opened now, so the answer has
    /// the file's length as it is, and is read a megabyte ahead unless a small piece of it is all that is asked for
    /// (<paramref name="piece"/>, its length). A HEAD and a "not modified" opened the file, a request of the source's
    /// server each, and a download asked the source 64 KiB at a time (the review of 21-Nov-2026, P13).
    /// </summary>
    public static async Task<VfsFileStream> ForAnswerAsync(IVirtualFileSystem fs, string path, VfsEntry entry, bool bodiless, long? piece, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(entry);
        return bodiless
            ? new VfsFileStream(entry.Length, c => fs.OpenAsync(path, VfsOpenMode.Read, c))
            : new VfsFileStream(await fs.OpenAsync(path, VfsOpenMode.Read, ct).ConfigureAwait(false), ownsFile: true, readAhead: piece is not < SequentialReadAhead.DefaultThreshold);
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => !_disposed;

    public override bool CanWrite => _writable && !_disposed;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var rented = new byte[buffer.Length];
        var n = ReadAsync(rented, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        rented.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var file = _file ??= await _open!(cancellationToken).ConfigureAwait(false);
        var n = _ahead is null
            ? await file.ReadAsync(_position, buffer, cancellationToken).ConfigureAwait(false)
            : await _ahead.ReadAsync(file, _position, buffer, cancellationToken).ConfigureAwait(false);
        _position += n;
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
        => WriteAsync(buffer.ToArray(), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_writable)
        {
            throw new NotSupportedException("the stream is read-only");
        }

        await _file!.WriteAsync(_position, buffer, cancellationToken).ConfigureAwait(false);
        _position += buffer.Length;
        _length = Math.Max(_length, _position);
    }

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => _writable ? _file!.FlushAsync(cancellationToken).AsTask() : Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0)
        {
            throw new IOException("cannot seek before the start of the file");
        }

        _position = target;
        return target;
    }

    public override void SetLength(long value)
    {
        if (!_writable)
        {
            throw new NotSupportedException("the stream is read-only");
        }

        _file!.SetLengthAsync(value, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        _length = value;
        if (_position > value)
        {
            _position = value;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _ahead?.Dispose();
            if (_ownsFile && _file is { } file)
            {
                file.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _ahead?.Dispose();
            if (_ownsFile && _file is { } file)
            {
                await file.DisposeAsync().ConfigureAwait(false);
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>A read-only file over bytes already in memory: small synthetic files (a readme, a JSON view).</summary>
public sealed class BytesVfsFile(ReadOnlyMemory<byte> bytes) : IVfsFile
{
    public long Length => bytes.Length;

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (offset >= bytes.Length)
        {
            return ValueTask.FromResult(0);
        }

        var n = (int)Math.Min(buffer.Length, bytes.Length - offset);
        bytes.Slice((int)offset, n).CopyTo(buffer);
        return ValueTask.FromResult(n);
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => throw VfsException.NotSupported("writing");

    public ValueTask SetLengthAsync(long length, CancellationToken ct) => throw VfsException.NotSupported("writing");

    public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
