using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace AnyFS.Sdk;

/// <summary>
/// For sources whose upstream only streams sequentially (an HTTP download, an attachment API). The first
/// open of a key starts the download into a file under the cache directory; any sub-range is served as
/// soon as the file has reached it, so a client reading the start doesn't wait for the end. Concurrent
/// readers share one download. When the last reader closes an unfinished spool, the download stops and
/// the partial file is deleted; finished spools stay until evicted by size or age. A download whose upstream
/// sends nothing for a while fails like one that broke off, so its readers are not left waiting.
/// <para>
/// It holds as many finished files as fit its bytes, which for a source of small files (a repository's) is hundreds of
/// thousands, so what it does at each open does not grow with how many it holds: the bytes are counted as they
/// arrive, the finished files nobody reads are kept in the order they were last used and in the order they finished,
/// and a file nobody reads holds no handle. Until 22-Nov-2026 every open of a new key copied, summed and (once full)
/// sorted every spool held, under the one lock, and each spool kept two handles open for as long as it was held.
/// </para>
/// </summary>
public sealed class SpoolCache : IAsyncDisposable
{
    // how long a stop goes on deleting the files it holds: what is left goes at the next start, in the background
    private static readonly TimeSpan StopDeletesFor = TimeSpan.FromSeconds(2);

    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly TimeSpan _maxAge;
    private readonly TimeSpan _stallLimit;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Spool> _spools = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    // under the gate: the finished spools nobody reads, the one used longest ago first (what goes when the bytes are over), and
    // the finished spools in the order they finished (what goes by age)
    private readonly LinkedList<Spool> _idle = new();
    private readonly LinkedList<Spool> _finished = new();

    // what this run's files' names begin with: a file of another beginning is an earlier run's, and goes
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly Task _leftovers;
    private long _bytes;
    private long _serial;
    private volatile bool _disposed;

    /// <param name="stallLimit">How long the upstream may send nothing before the download fails (a minute by default).</param>
    public SpoolCache(string directory, long maxBytes = 2L << 30, TimeSpan? maxAge = null, ILogger? logger = null, TimeProvider? time = null, TimeSpan? stallLimit = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _directory = directory;
        _maxBytes = maxBytes;
        _maxAge = maxAge ?? TimeSpan.FromDays(1);
        _stallLimit = stallLimit ?? StalledUpstream.DefaultLimit;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(directory);

        // what an earlier run left (a stop deletes for a moment only, a run killed nothing) goes beside the source's start,
        // not before it: hundreds of thousands of files take a while
        _leftovers = Task.Run(RemoveLeftovers);
    }

    public int Count => _spools.Count;

    public long BytesOnDisk => Interlocked.Read(ref _bytes);

    private void RemoveLeftovers()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*.spool"))
            {
                if (_disposed)
                {
                    return;
                }

                if (!Path.GetFileName(file).StartsWith(_run, StringComparison.Ordinal))
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("spool: what an earlier run left in {Directory} could not all be removed ({Error})", _directory, ex.Message);
        }
    }

    /// <summary>
    /// Opens <paramref name="key"/> (any string identifying the upstream object and its version) as a file of
    /// <paramref name="length"/> bytes, starting the download if nobody has it yet.
    /// </summary>
    public Task<IVfsFile> OpenAsync(string key, long length, Func<CancellationToken, Task<Stream>> openUpstream, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(openUpstream);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        if (length == 0)
        {
            // a file of no bytes is that, and nothing is asked for it: nine places said so before they came here, and one
            // that did not asked its server for nothing (the review of 21-Nov-2026, C3)
            return Task.FromResult<IVfsFile>(new BytesVfsFile(ReadOnlyMemory<byte>.Empty));
        }

        Spool spool;
        var begins = false;
        lock (_gate)
        {
            if (!_spools.TryGetValue(key, out var existing) || existing.IsDead)
            {
                if (existing is not null)
                {
                    // a failed download that readers may still hold: it goes when the last of them lets go
                    LetGoLocked(existing);
                }

                EvictLocked();

                // a file of its own: a dead spool of the key may still be open
                var path = Path.Combine(_directory, $"{_run}-{Interlocked.Increment(ref _serial)}.spool");
                existing = new Spool(this, key, path, length, openUpstream, _stallLimit, _logger, _time);
                _spools[key] = existing;
                begins = true;
            }

            spool = existing;
            if (spool.IdleNode is { } idle)
            {
                _idle.Remove(idle);
                spool.IdleNode = null;
            }

            spool.AddRef();
        }

        // outside the gate: the source's own opening runs up to its first wait here, and no other open waits behind it
        if (begins)
        {
            spool.Start();
        }

        return Task.FromResult<IVfsFile>(new SpoolingFile(spool));
    }

    /// <summary>Forgets a key (the upstream changed); readers already open keep what they have.</summary>
    public void Invalidate(string key)
    {
        lock (_gate)
        {
            if (_spools.TryGetValue(key, out var spool))
            {
                LetGoLocked(spool);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        List<Spool> all;
        lock (_gate)
        {
            all = _spools.Values.ToList();
            _spools.Clear();
            _idle.Clear();
            _finished.Clear();
        }

        // an earlier run's leftovers are not this stop's to wait for: the removal sees the stop and ends
        await _leftovers.ConfigureAwait(false);

        // the downloads and the handles first, which is quick; then the files, for a moment: the rest goes at the next start
        foreach (var spool in all)
        {
            await spool.CloseAsync().ConfigureAwait(false);
        }

        var started = Stopwatch.GetTimestamp();
        foreach (var spool in all)
        {
            if (Stopwatch.GetElapsedTime(started) > StopDeletesFor)
            {
                break;
            }

            TryDelete(spool.Path);
        }
    }

    internal void Add(long bytes) => Interlocked.Add(ref _bytes, bytes);

    /// <summary>A reader has let go of <paramref name="spool"/>.</summary>
    internal void Release(Spool spool)
    {
        lock (_gate)
        {
            if (spool.ReleaseRef() != 0)
            {
                return;
            }

            if (!spool.IsComplete || spool.IsDead || spool.Aged)
            {
                // nobody is reading and the download isn't done: stop it rather than pull the rest for nobody; a failed,
                // replaced or aged one has nothing more to give, so its file goes
                LetGoLocked(spool);
                return;
            }

            // finished, and nobody reads it: it holds no handle until someone does, and is the last to go for room
            spool.CloseRead();
            spool.IdleNode = _idle.AddLast(spool);
            spool.AgeNode ??= _finished.AddLast(spool);
        }
    }

    /// <summary>A download has finished whole: from now it can go by age (it is among the finished already where its last reader let go first).</summary>
    internal void Finished(Spool spool)
    {
        lock (_gate)
        {
            if (spool.AgeNode is null && !spool.Aged && !spool.IsDead && _spools.TryGetValue(spool.Key, out var held) && ReferenceEquals(held, spool))
            {
                spool.AgeNode = _finished.AddLast(spool);
            }
        }
    }

    /// <summary>Takes <paramref name="spool"/> out of what is held (under the gate): its bytes no longer count, and its file goes once nobody reads it.</summary>
    private void LetGoLocked(Spool spool)
    {
        _spools.TryRemove(new KeyValuePair<string, Spool>(spool.Key, spool));
        if (spool.IdleNode is { } idle)
        {
            _idle.Remove(idle);
            spool.IdleNode = null;
        }

        if (spool.AgeNode is { } age)
        {
            _finished.Remove(age);
            spool.AgeNode = null;
        }

        spool.Detach();
    }

    /// <summary>Makes room (under the gate): the finished spools past their age go, then, while the bytes are over, the ones nobody reads, the one used longest ago first.</summary>
    private void EvictLocked()
    {
        var now = _time.GetUtcNow();
        while (_finished.First is { } oldest && now - oldest.Value.CompletedAt > _maxAge)
        {
            var spool = oldest.Value;
            if (spool.Refs == 0)
            {
                LetGoLocked(spool);
            }
            else
            {
                // someone reads it still: it goes when the last of them lets go
                _finished.Remove(oldest);
                spool.AgeNode = null;
                spool.Aged = true;
            }
        }

        while (Interlocked.Read(ref _bytes) > _maxBytes && _idle.First is { } unused)
        {
            LetGoLocked(unused.Value);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>One shared download: the file on disk plus how far it has got.</summary>
    internal sealed class Spool
    {
        private readonly SpoolCache _owner;
        private readonly Func<CancellationToken, Task<Stream>> _openUpstream;
        private readonly TimeSpan _stallLimit;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly CancellationTokenSource _cts = new();
        private readonly CancellationToken _stopped;
        private readonly object _gate = new();
        private TaskCompletionSource _progress = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // open while someone reads (under _gate): a finished file nobody reads holds no handle
        private SafeFileHandle? _readHandle;
        private long _available;

        // how many of its bytes the cache counts, and whether it still counts any (under _gate)
        private long _counted;
        private bool _uncounted;
        private int _refs;
        private bool _detached;
        private bool _closed;

        public Spool(SpoolCache owner, string key, string path, long length, Func<CancellationToken, Task<Stream>> openUpstream, TimeSpan stallLimit, ILogger logger, TimeProvider time)
        {
            _owner = owner;
            Key = key;
            Path = path;
            Length = length;
            _openUpstream = openUpstream;
            _stallLimit = stallLimit;
            _logger = logger;
            _time = time;
            _stopped = _cts.Token;
            LastUsed = time.GetUtcNow();
        }

        public string Key { get; }

        public string Path { get; }

        public long Length { get; }

        public long Available => Interlocked.Read(ref _available);

        public bool IsComplete { get; private set; }

        public Exception? Error { get; private set; }

        public DateTimeOffset CompletedAt { get; private set; }

        public DateTimeOffset LastUsed { get; private set; }

        public int Refs => _refs;

        /// <summary>Its place among the finished spools nobody reads; null while someone does (the cache's, under its gate).</summary>
        public LinkedListNode<Spool>? IdleNode { get; set; }

        /// <summary>Its place among the finished spools, in the order they finished (the cache's, under its gate).</summary>
        public LinkedListNode<Spool>? AgeNode { get; set; }

        /// <summary>Past its age while someone still read it: it goes when the last of them lets go (the cache's, under its gate).</summary>
        public bool Aged { get; set; }

        /// <summary>Detached (evicted/invalidated) or faulted spools are not handed to new readers.</summary>
        public bool IsDead => _detached || Error is not null;

        public void Start() => _ = PumpAsync();

        public void AddRef()
        {
            _refs++;
            LastUsed = _time.GetUtcNow();
        }

        public int ReleaseRef() => --_refs;

        public void Detach()
        {
            _detached = true;
            lock (_gate)
            {
                if (!_uncounted)
                {
                    _uncounted = true;
                    _owner.Add(-_counted);
                }
            }

            if (!IsComplete)
            {
                _cts.Cancel();
            }

            if (_refs == 0)
            {
                _ = DisposeAsync();
            }
        }

        /// <summary>Closes the handle a finished file nobody reads would otherwise hold; the next read opens it again.</summary>
        public void CloseRead()
        {
            lock (_gate)
            {
                _readHandle?.Dispose();
                _readHandle = null;
            }
        }

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (buffer.Length == 0 || offset >= Length)
            {
                return 0;
            }

            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    var available = _available;
                    if (available > offset)
                    {
                        var n = (int)Math.Min(buffer.Length, available - offset);
                        return RandomAccess.Read(ReadHandleLocked(), buffer.Span[..n], offset);
                    }

                    if (Error is not null)
                    {
                        throw new VfsException(VfsError.IoError, $"the download of '{Key}' failed: {Error.Message}", Error);
                    }

                    if (IsComplete)
                    {
                        return 0; // the upstream was shorter than announced
                    }

                    wait = _progress.Task;
                }

                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        /// <summary>The handle reads go through, opened when a read first has bytes to take (under _gate): the file is there by then.</summary>
        private SafeFileHandle ReadHandleLocked()
        {
            if (_readHandle is { } open)
            {
                return open;
            }

            try
            {
                return _readHandle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // gone from under it (something else emptied the folder): this one has no more to give, and the next open downloads again
                Error ??= ex;
                throw new VfsException(VfsError.IoError, $"the download of '{Key}' is no longer on disk: {ex.Message}", ex);
            }
        }

        public void ReleaseFromFile() => _owner.Release(this);

        /// <summary>Stops the download and closes what is open; the file stays (see <see cref="DisposeAsync"/>).</summary>
        public async ValueTask CloseAsync()
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
            }

            await _cts.CancelAsync().ConfigureAwait(false);
            CloseRead();
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync().ConfigureAwait(false);
            TryDelete(Path);
        }

        private async Task PumpAsync()
        {
            // The download is its readers', whoever opened the file: it is begun inside a person's open, and is not to
            // carry that call's patience with a wait the upstream asks for. A read has its own limit, a minute without a byte.
            UpstreamPatience.Current = null;
            var ct = _stopped;
            try
            {
                // the download's own handle, closed when it ends: a finished file is written to no more
                using var file = File.OpenHandle(Path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete, FileOptions.Asynchronous);
                await using var upstream = await _openUpstream(ct).ConfigureAwait(false);
                var buffer = new byte[256 * 1024];
                long written = 0;
                while (written < Length)
                {
                    var n = await StalledUpstream.ReadAsync(upstream, buffer.AsMemory(0, (int)Math.Min(buffer.Length, Length - written)), _stallLimit, ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }

                    await RandomAccess.WriteAsync(file, buffer.AsMemory(0, n), written, ct).ConfigureAwait(false);
                    written += n;
                    Signal(written, complete: false, error: null);
                }

                if (written < Length)
                {
                    _logger.LogWarning("spool {Key}: upstream ended after {Written} of {Length} bytes", Key, written, Length);
                }

                Signal(written, complete: true, error: null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Signal(Available, complete: true, error: new OperationCanceledException("the download was stopped"));
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "spool {Key}: download failed", Key);
                Signal(Available, complete: true, error: ex);
                return;
            }

            _owner.Finished(this);
        }

        private void Signal(long available, bool complete, Exception? error)
        {
            TaskCompletionSource previous;
            lock (_gate)
            {
                Interlocked.Exchange(ref _available, available);
                if (!_uncounted)
                {
                    _owner.Add(available - _counted);
                    _counted = available;
                }

                if (complete)
                {
                    IsComplete = true;
                    CompletedAt = _time.GetUtcNow();
                    Error ??= error;
                }

                previous = _progress;
                _progress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            previous.TrySetResult();
        }
    }
}

/// <summary>A reader's handle on a <see cref="SpoolCache"/> entry.</summary>
public sealed class SpoolingFile : IVfsFile
{
    private readonly SpoolCache.Spool _spool;
    private int _released;

    internal SpoolingFile(SpoolCache.Spool spool)
    {
        _spool = spool;
    }

    public long Length => _spool.Length;

    /// <summary>How many bytes of the file have arrived so far (for status text and tests).</summary>
    public long Available => _spool.Available;

    public bool IsComplete => _spool.IsComplete;

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) => _spool.ReadAsync(offset, buffer, ct);

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => throw VfsException.NotSupported("writing");

    public ValueTask SetLengthAsync(long length, CancellationToken ct) => throw VfsException.NotSupported("writing");

    public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _spool.ReleaseFromFile();
        }

        return ValueTask.CompletedTask;
    }
}
