using System.Collections.Concurrent;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Mounts.Smb;

/// <summary>
/// Open read handles kept between the bridge's <c>/read</c> requests. The SMB server asks for a file in pieces,
/// one request each, and opening the file for every piece costs what an open costs in the source: nothing for
/// a local file, a round trip for SSHFS, a whole HTTP request to SharePoint for each 64 KiB (18-Oct-2026: the
/// E2E's 200 MiB file came through at 2 MiB/s). A handle stays open while requests keep coming and for
/// <see cref="_idle"/> after the last one; a write, truncate, create or delete through the bridge closes it,
/// so the next read opens the file again and sees the change. <see cref="IVfsFile"/> reads carry their offset
/// and may run concurrently, so one handle serves parallel requests.
/// </summary>
internal sealed class ReadHandles : IAsyncDisposable
{
    private readonly TimeSpan _idle;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<(IVirtualFileSystem Fs, string Path), Entry> _entries = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _sweeper;

    public ReadHandles(TimeSpan idle, ILogger logger)
    {
        _idle = idle;
        _logger = logger;
        _sweeper = SweepAsync();
    }

    /// <summary>The file, open for reading, shared with other requests; <see cref="Lease.Dispose"/> when done with it.</summary>
    public async ValueTask<Lease> LeaseAsync(IVirtualFileSystem fs, string path, CancellationToken ct)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd((fs, path), key => new Entry(key.Fs, key.Path));
            if (entry.TryAcquire())
            {
                try
                {
                    var file = await entry.Opened.Value.WaitAsync(ct).ConfigureAwait(false);
                    return new Lease(entry, file);
                }
                catch
                {
                    entry.Release();
                    _entries.TryRemove(new KeyValuePair<(IVirtualFileSystem, string), Entry>((fs, path), entry));
                    throw;
                }
            }

            // closed between our lookup and the acquire: it is gone from the dictionary (or about to be), try again
            _entries.TryRemove(new KeyValuePair<(IVirtualFileSystem, string), Entry>((fs, path), entry));
        }
    }

    /// <summary>Closes the handle on <paramref name="path"/> (after its last user lets go), because the file is about to change.</summary>
    public void Invalidate(IVirtualFileSystem fs, string path)
    {
        if (_entries.TryRemove((fs, path), out var entry))
        {
            entry.Close(_logger);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _sweeper.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var key in _entries.Keys.ToList())
        {
            if (_entries.TryRemove(key, out var entry))
            {
                await entry.CloseNowAsync(_logger).ConfigureAwait(false);
            }
        }
    }

    private async Task SweepAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _idle.TotalSeconds / 3)));
        while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
        {
            var cutoff = Environment.TickCount64 - (long)_idle.TotalMilliseconds;
            foreach (var pair in _entries)
            {
                if (pair.Value.IsIdleSince(cutoff) && _entries.TryRemove(pair))
                {
                    pair.Value.Close(_logger);
                }
            }
        }
    }

    /// <summary>A borrowed handle; disposing it lets the handle go idle (it is not closed).</summary>
    public readonly struct Lease(Entry entry, IVfsFile file) : IDisposable
    {
        public IVfsFile File { get; } = file;

        public void Dispose() => entry.Release();
    }

    internal sealed class Entry(IVirtualFileSystem fs, string path)
    {
        private readonly Lock _gate = new();
        private int _users;
        private bool _closed;
        private long _lastUsed = Environment.TickCount64;

        public Lazy<Task<IVfsFile>> Opened { get; } = new(() => fs.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication);

        public bool TryAcquire()
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return false;
                }

                _users++;
                return true;
            }
        }

        public void Release()
        {
            bool closeNow;
            lock (_gate)
            {
                _users--;
                _lastUsed = Environment.TickCount64;
                closeNow = _closed && _users == 0;
            }

            if (closeNow)
            {
                _ = DisposeFileAsync(null);
            }
        }

        public bool IsIdleSince(long cutoffTicks)
        {
            lock (_gate)
            {
                return _users == 0 && _lastUsed < cutoffTicks;
            }
        }

        /// <summary>No new users; the file is disposed once the current ones are done.</summary>
        public void Close(ILogger logger)
        {
            bool closeNow;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                closeNow = _users == 0;
            }

            if (closeNow)
            {
                _ = DisposeFileAsync(logger);
            }
        }

        public async ValueTask CloseNowAsync(ILogger logger)
        {
            lock (_gate)
            {
                _closed = true;
            }

            await DisposeFileAsync(logger).ConfigureAwait(false);
        }

        private async Task DisposeFileAsync(ILogger? logger)
        {
            if (!Opened.IsValueCreated)
            {
                return;
            }

            try
            {
                var file = await Opened.Value.ConfigureAwait(false);
                await file.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is VfsException or IOException or ObjectDisposedException)
            {
                logger?.LogDebug(ex, "smb: closing the read handle on '{Path}' failed", path);
            }
        }
    }
}
