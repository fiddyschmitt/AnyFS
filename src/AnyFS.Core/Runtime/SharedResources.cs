using AnyFS.Sdk;

namespace AnyFS.Core.Runtime;

/// <summary>
/// Reference-counted resources shared across mounts by key (one Kestrel per bind address and port, one
/// Go server per port). Concurrent acquirers of the same key share one creation; the resource is
/// disposed when the last lease goes.
/// </summary>
public sealed class SharedResources
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public async Task<ISharedLease<T>> AcquireAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CancellationToken ct)
        where T : class, IAsyncDisposable
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry(key);
                _entries[key] = entry;
                entry.Creation = CreateAsync(entry, factory);
            }

            entry.Refs++;
        }

        try
        {
            var value = await entry.Creation!.WaitAsync(ct).ConfigureAwait(false);
            return new Lease<T>(this, entry, (T)value);
        }
        catch
        {
            Release(entry, disposeIfLast: false);
            throw;
        }
    }

    private async Task<object> CreateAsync<T>(Entry entry, Func<CancellationToken, Task<T>> factory)
        where T : class, IAsyncDisposable
    {
        try
        {
            // the shared creation must not be cancelled by the first caller alone
            var value = await factory(CancellationToken.None).ConfigureAwait(false);
            entry.Value = value;
            return value;
        }
        catch
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(entry.Key);
                }
            }

            throw;
        }
    }

    private void Release(Entry entry, bool disposeIfLast)
    {
        IAsyncDisposable? toDispose = null;
        lock (_gate)
        {
            entry.Refs--;
            if (entry.Refs <= 0)
            {
                if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(entry.Key);
                }

                if (disposeIfLast)
                {
                    toDispose = entry.Value;
                }
            }
        }

        if (toDispose is not null)
        {
            _ = toDispose.DisposeAsync().AsTask();
        }
    }

    private async ValueTask ReleaseAsync(Entry entry)
    {
        IAsyncDisposable? toDispose = null;
        lock (_gate)
        {
            entry.Refs--;
            if (entry.Refs <= 0)
            {
                if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(entry.Key);
                }

                toDispose = entry.Value;
            }
        }

        if (toDispose is not null)
        {
            await toDispose.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Entry(string key)
    {
        public string Key { get; } = key;

        public int Refs { get; set; }

        public Task<object>? Creation { get; set; }

        public IAsyncDisposable? Value { get; set; }
    }

    private sealed class Lease<T>(SharedResources owner, Entry entry, T value) : ISharedLease<T>
        where T : class
    {
        private int _disposed;

        public T Value => value;

        public ValueTask DisposeAsync()
            => Interlocked.Exchange(ref _disposed, 1) == 0 ? owner.ReleaseAsync(entry) : ValueTask.CompletedTask;
    }
}
