namespace AnyFS.Sdk;

/// <summary>
/// A seekable <see cref="Stream"/> over an <see cref="IVfsFile"/>, for mounts and libraries that want one
/// (WebDAV, FTP, HTTP responses). Each stream keeps its own position; the file stays positionless.
/// </summary>
public sealed class VfsFileStream : Stream
{
    private readonly IVfsFile _file;
    private readonly bool _ownsFile;
    private readonly bool _writable;
    private long _position;
    private long _length;
    private bool _disposed;

    public VfsFileStream(IVfsFile file, bool writable = false, bool ownsFile = false)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        _writable = writable;
        _ownsFile = ownsFile;
        _length = file.Length;
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
        var n = await _file.ReadAsync(_position, buffer, cancellationToken).ConfigureAwait(false);
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

        await _file.WriteAsync(_position, buffer, cancellationToken).ConfigureAwait(false);
        _position += buffer.Length;
        _length = Math.Max(_length, _position);
    }

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => _writable ? _file.FlushAsync(cancellationToken).AsTask() : Task.CompletedTask;

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

        _file.SetLengthAsync(value, CancellationToken.None).AsTask().GetAwaiter().GetResult();
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
            if (_ownsFile)
            {
                _file.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_ownsFile)
            {
                await _file.DisposeAsync().ConfigureAwait(false);
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
