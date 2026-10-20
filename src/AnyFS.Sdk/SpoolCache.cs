using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
/// </summary>
public sealed class SpoolCache : IAsyncDisposable
{
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly TimeSpan _maxAge;
    private readonly TimeSpan _stallLimit;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Spool> _spools = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _serial;
    private bool _disposed;

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
        foreach (var stale in Directory.EnumerateFiles(directory, "*.spool"))
        {
            TryDelete(stale);
        }
    }

    public int Count => _spools.Count;

    public long BytesOnDisk => _spools.Values.Sum(s => s.Available);

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

        Spool spool;
        lock (_gate)
        {
            if (!_spools.TryGetValue(key, out var existing) || existing.IsDead)
            {
                if (existing is not null)
                {
                    // a failed download that readers may still hold: it goes when the last of them lets go
                    _spools.TryRemove(new KeyValuePair<string, Spool>(key, existing));
                    existing.Detach();
                }

                EvictLocked();
                // a file of its own: a dead spool of the key may still be open
                var path = Path.Combine(_directory, $"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]}-{Interlocked.Increment(ref _serial)}.spool");
                existing = new Spool(this, key, path, length, openUpstream, _stallLimit, _logger, _time);
                _spools[key] = existing;
                existing.Start();
            }

            spool = existing;
            spool.AddRef();
        }

        return Task.FromResult<IVfsFile>(new SpoolingFile(spool));
    }

    /// <summary>Forgets a key (the upstream changed); readers already open keep what they have.</summary>
    public void Invalidate(string key)
    {
        lock (_gate)
        {
            if (_spools.TryRemove(key, out var spool))
            {
                spool.Detach();
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
        }

        foreach (var spool in all)
        {
            await spool.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal void Release(Spool spool)
    {
        lock (_gate)
        {
            if (spool.ReleaseRef() == 0 && (!spool.IsComplete || spool.IsDead))
            {
                // nobody is reading and the download isn't done: stop it rather than pull the rest for nobody; a
                // failed or replaced one has nothing more to give, so its file goes
                _spools.TryRemove(new KeyValuePair<string, Spool>(spool.Key, spool));
                spool.Detach();
            }
        }
    }

    private void EvictLocked()
    {
        var now = _time.GetUtcNow();
        foreach (var pair in _spools.ToList())
        {
            var s = pair.Value;
            if (s.Refs == 0 && s.IsComplete && now - s.CompletedAt > _maxAge)
            {
                _spools.TryRemove(pair);
                s.Detach();
            }
        }

        var total = _spools.Values.Sum(s => s.Available);
        if (total <= _maxBytes)
        {
            return;
        }

        foreach (var s in _spools.Values.Where(s => s.Refs == 0 && s.IsComplete).OrderBy(s => s.LastUsed).ToList())
        {
            _spools.TryRemove(new KeyValuePair<string, Spool>(s.Key, s));
            total -= s.Available;
            s.Detach();
            if (total <= _maxBytes)
            {
                break;
            }
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
    internal sealed class Spool : IAsyncDisposable
    {
        private readonly SpoolCache _owner;
        private readonly Func<CancellationToken, Task<Stream>> _openUpstream;
        private readonly TimeSpan _stallLimit;
        private readonly ILogger _logger;
        private readonly TimeProvider _time;
        private readonly CancellationTokenSource _cts = new();
        private readonly SafeFileHandle _readHandle;
        private readonly SafeFileHandle _writeHandle;
        private readonly object _gate = new();
        private TaskCompletionSource _progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _available;
        private int _refs;
        private bool _detached;
        private bool _disposed;

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
            _writeHandle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete, FileOptions.Asynchronous);
            _readHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
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
            if (!IsComplete)
            {
                _cts.Cancel();
            }

            if (_refs == 0)
            {
                _ = DisposeAsync();
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
                        return RandomAccess.Read(_readHandle, buffer.Span[..n], offset);
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

        public void ReleaseFromFile() => _owner.Release(this);

        public async ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            await _cts.CancelAsync().ConfigureAwait(false);
            _readHandle.Dispose();
            _writeHandle.Dispose();
            _cts.Dispose();
            TryDelete(Path);
        }

        private async Task PumpAsync()
        {
            var ct = _cts.Token;
            try
            {
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

                    await RandomAccess.WriteAsync(_writeHandle, buffer.AsMemory(0, n), written, ct).ConfigureAwait(false);
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
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "spool {Key}: download failed", Key);
                Signal(Available, complete: true, error: ex);
            }
        }

        private void Signal(long available, bool complete, Exception? error)
        {
            TaskCompletionSource previous;
            lock (_gate)
            {
                Interlocked.Exchange(ref _available, available);
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
