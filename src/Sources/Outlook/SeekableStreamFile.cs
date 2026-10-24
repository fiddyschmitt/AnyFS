using AnyFS.Sdk;

namespace AnyFS.Sources.Outlook;

/// <summary>An attachment's content as a file: a seekable stream opened on the first read, positioned per read, read-only.</summary>
internal sealed class SeekableStreamFile(long length, Func<CancellationToken, Task<Stream>> open, string what) : IVfsFile
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Stream? _stream;

    public long Length => length;

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        if (offset >= length || buffer.Length == 0)
        {
            return 0;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _stream ??= await open(ct).ConfigureAwait(false);
            _stream.Position = offset;
            var want = (int)Math.Min(buffer.Length, length - offset);
            var total = 0;
            while (total < want)
            {
                var n = await _stream.ReadAsync(buffer.Slice(total, want - total), ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }

                total += n;
            }

            return total;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => throw VfsException.NotSupported($"writing {what}");

    public ValueTask SetLengthAsync(long length, CancellationToken ct) => throw VfsException.NotSupported($"writing {what}");

    public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
