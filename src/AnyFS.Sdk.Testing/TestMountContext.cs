using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sdk.Testing;

/// <summary>
/// An <see cref="IMountContext"/> for testing a mount provider without the AnyFS host: a temp state
/// directory, a temp provider cache, a connection counter, recorded health reports, and a per-context
/// shared-resource table (share one instance between mounts that should share a listener; they share the
/// provider cache too, as mounts of one provider do in the host).
/// </summary>
public sealed class TestMountContext : IMountContext, IMountStatsSink, IDisposable
{
    private readonly string _root;
    private readonly string _providerCache;
    private readonly bool _ownsProviderCache;
    private readonly SharedTable _shared;
    private int _connections;

    public TestMountContext(string collectionName = "test", string mountName = "mount", ILogger? logger = null, TestMountContext? shareWith = null)
    {
        Collection = new Info(Guid.NewGuid(), collectionName);
        MountName = mountName;
        Logger = logger ?? NullLogger.Instance;
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", Guid.NewGuid().ToString("N"));
        _shared = shareWith?._shared ?? new SharedTable();
        _ownsProviderCache = shareWith is null;
        _providerCache = shareWith?._providerCache ?? Path.Combine(Path.GetTempPath(), "anyfs-tests", Guid.NewGuid().ToString("N"));
    }

    public ILogger Logger { get; }

    public Guid MountId { get; } = Guid.NewGuid();

    public string MountName { get; }

    public ICollectionInfo Collection { get; }

    public string StateDirectory => Directory.CreateDirectory(_root).FullName;

    public string ProviderCacheDirectory => Directory.CreateDirectory(_providerCache).FullName;

    public IMountStatsSink Stats => this;

    public int ActiveConnections => Volatile.Read(ref _connections);

    public List<(ComponentHealth Health, string? Detail)> HealthReports { get; } = [];

    public int SharedCount => _shared.Count;

    public void ReportHealth(ComponentHealth health, string? detail = null) => HealthReports.Add((health, detail));

    public Task<ISharedLease<T>> AcquireSharedAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CancellationToken ct)
        where T : class, IAsyncDisposable
        => _shared.AcquireAsync(key, factory, ct);

    public void ConnectionOpened() => Interlocked.Increment(ref _connections);

    public void ConnectionClosed() => Interlocked.Decrement(ref _connections);

    public void Dispose()
    {
        DeleteQuietly(_root);
        if (_ownsProviderCache)
        {
            DeleteQuietly(_providerCache);
        }
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Info(Guid Id, string Name) : ICollectionInfo;

    private sealed class SharedTable
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (IAsyncDisposable Value, int Refs)> _entries = new(StringComparer.Ordinal);

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
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var existing))
                {
                    _entries[key] = (existing.Value, existing.Refs + 1);
                    return new Lease<T>(this, key, (T)existing.Value);
                }
            }

            var value = await factory(ct).ConfigureAwait(false);
            lock (_gate)
            {
                _entries[key] = (value, 1);
            }

            return new Lease<T>(this, key, value);
        }

        private async ValueTask ReleaseAsync(string key)
        {
            IAsyncDisposable? dispose = null;
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    if (entry.Refs <= 1)
                    {
                        _entries.Remove(key);
                        dispose = entry.Value;
                    }
                    else
                    {
                        _entries[key] = (entry.Value, entry.Refs - 1);
                    }
                }
            }

            if (dispose is not null)
            {
                await dispose.DisposeAsync().ConfigureAwait(false);
            }
        }

        private sealed class Lease<T>(SharedTable owner, string key, T value) : ISharedLease<T>
            where T : class
        {
            private int _disposed;

            public T Value => value;

            public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 ? owner.ReleaseAsync(key) : ValueTask.CompletedTask;
        }
    }
}
