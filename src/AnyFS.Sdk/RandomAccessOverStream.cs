namespace AnyFS.Sdk;

/// <summary>
/// For upstreams that support ranged requests but hand back streams (SFTP, HTTP Range): keeps a few open
/// streams positioned where recent reads ended, so sequential reads reuse a stream instead of reopening,
/// while a read at a new offset opens one there. Read-only.
/// </summary>
public sealed class RandomAccessOverStream : IVfsFile
{
    private readonly Func<long, CancellationToken, Task<Stream>> _openAt;
    private readonly int _maxStreams;
    private readonly int _maxSkipBytes;
    private readonly SemaphoreSlim _slots;
    private readonly object _gate = new();
    private readonly List<Slot> _streams = [];
    private long _sequence;
    private int _opened;
    private bool _disposed;

    /// <param name="length">The file's length, known from the listing.</param>
    /// <param name="openAt">Opens the upstream positioned at an offset.</param>
    /// <param name="maxStreams">How many upstream streams to keep (and the read concurrency).</param>
    /// <param name="maxSkipBytes">A read this far ahead of a stream's position reads and discards the gap instead of reopening.</param>
    public RandomAccessOverStream(long length, Func<long, CancellationToken, Task<Stream>> openAt, int maxStreams = 4, int maxSkipBytes = 256 * 1024)
    {
        ArgumentNullException.ThrowIfNull(openAt);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxStreams, 1);
        Length = length;
        _openAt = openAt;
        _maxStreams = maxStreams;
        _maxSkipBytes = maxSkipBytes;
        _slots = new SemaphoreSlim(maxStreams, maxStreams);
    }

    public long Length { get; }

    /// <summary>How many upstream streams have been opened in total (for tests and diagnostics).</summary>
    public int StreamsOpened => _opened;

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (buffer.Length == 0 || offset >= Length)
        {
            return 0;
        }

        await _slots.WaitAsync(ct).ConfigureAwait(false);
        Slot? slot = null;
        try
        {
            slot = TakeSlot(offset);
            if (slot.Stream is null)
            {
                slot.Stream = await _openAt(offset, ct).ConfigureAwait(false);
                slot.Position = offset;
                Interlocked.Increment(ref _opened);
            }
            else if (slot.Position < offset)
            {
                await SkipAsync(slot, offset - slot.Position, ct).ConfigureAwait(false);
            }

            var want = (int)Math.Min(buffer.Length, Length - offset);
            var total = 0;
            while (total < want)
            {
                var n = await slot.Stream.ReadAsync(buffer.Slice(total, want - total), ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }

                total += n;
            }

            slot.Position += total;
            return total;
        }
        catch
        {
            if (slot is not null)
            {
                await DropAsync(slot).ConfigureAwait(false);
                slot = null;
            }

            throw;
        }
        finally
        {
            if (slot is not null)
            {
                lock (_gate)
                {
                    slot.Busy = false;
                }
            }

            _slots.Release();
        }
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => throw VfsException.NotSupported("writing");

    public ValueTask SetLengthAsync(long length, CancellationToken ct) => throw VfsException.NotSupported("writing");

    public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Slot> all;
        lock (_gate)
        {
            all = _streams.ToList();
            _streams.Clear();
        }

        foreach (var slot in all)
        {
            if (slot.Stream is not null)
            {
                await slot.Stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        _slots.Dispose();
    }

    private Slot TakeSlot(long offset)
    {
        lock (_gate)
        {
            // exact position first, then one a short skip behind, else a new or recycled slot
            var candidate = _streams.FirstOrDefault(s => !s.Busy && s.Stream is not null && s.Position == offset)
                ?? _streams.Where(s => !s.Busy && s.Stream is not null && s.Position < offset && offset - s.Position <= _maxSkipBytes).OrderByDescending(s => s.Position).FirstOrDefault();
            if (candidate is null)
            {
                if (_streams.Count < _maxStreams)
                {
                    candidate = new Slot();
                    _streams.Add(candidate);
                }
                else
                {
                    candidate = _streams.Where(s => !s.Busy).OrderBy(s => s.LastUsed).First();
                    var old = candidate.Stream;
                    candidate.Stream = null;
                    if (old is not null)
                    {
                        _ = old.DisposeAsync().AsTask();
                    }
                }
            }

            candidate.Busy = true;
            candidate.LastUsed = ++_sequence;
            return candidate;
        }
    }

    private static async Task SkipAsync(Slot slot, long count, CancellationToken ct)
    {
        var buffer = new byte[(int)Math.Min(count, 64 * 1024)];
        while (count > 0)
        {
            var n = await slot.Stream!.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new EndOfStreamException("the upstream ended while skipping ahead");
            }

            count -= n;
            slot.Position += n;
        }
    }

    private async Task DropAsync(Slot slot)
    {
        Stream? stream;
        lock (_gate)
        {
            stream = slot.Stream;
            slot.Stream = null;
            slot.Busy = false;
            _streams.Remove(slot);
        }

        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Slot
    {
        public Stream? Stream { get; set; }

        public long Position { get; set; }

        public bool Busy { get; set; }

        public long LastUsed { get; set; }
    }
}
