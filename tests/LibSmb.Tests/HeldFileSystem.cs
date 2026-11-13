using System.Collections.Concurrent;

namespace LibSmb.Tests;

/// <summary>
/// A folder served as <see cref="LocalFileSystem"/> serves it, except that the reads of a file the test holds wait until
/// the test lets them go: a backend call as slow as a cold fetch from a remote store, for as long as the test likes. It
/// counts what a test needs to see: the calls in flight on each open (one open's calls must never overlap), the held reads
/// whose cancellation came, and the files closed.
/// </summary>
internal sealed class HeldFileSystem(string root) : ISmbFileSystem
{
    private readonly LocalFileSystem _inner = new(root);
    private readonly ConcurrentDictionary<string, Held> _held = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _givingUp = new(StringComparer.Ordinal);
    private int _mostAtOnce;
    private int _cancelled;
    private int _closed;
    private int _listed;
    private int _reads;

    public bool CaseInsensitive => _inner.CaseInsensitive;

    /// <summary>The most calls seen in flight at once on any one open.</summary>
    public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

    /// <summary>Held reads that ended because their cancellation token fired.</summary>
    public int Cancelled => Volatile.Read(ref _cancelled);

    /// <summary>Opens closed (disposed).</summary>
    public int Closed => Volatile.Read(ref _closed);

    /// <summary>Calls to read a file, all opens'.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Directories listed.</summary>
    public int Listed => Volatile.Read(ref _listed);

    /// <summary>Holds every read of <paramref name="path"/> from now until <see cref="Release"/>.</summary>
    public void Hold(string path) => _held[path] = new Held();

    /// <summary>Makes every read of <paramref name="path"/> give up on its own, as a backend's call that times out does.</summary>
    public void GiveUp(string path) => _givingUp[path] = 0;

    /// <summary>Lets the held reads of <paramref name="path"/> go, and every later one through at once.</summary>
    public void Release(string path)
    {
        if (_held.TryRemove(path, out var held))
        {
            held.Gate.TrySetResult();
        }
    }

    /// <summary>Completes once a read of the held <paramref name="path"/> is waiting.</summary>
    public Task Reached(string path) => _held[path].Reached.Task;

    public ValueTask<SmbEntry?> StatAsync(string path, CancellationToken cancellationToken) => _inner.StatAsync(path, cancellationToken);

    public IAsyncEnumerable<SmbEntry> ListAsync(string path, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _listed);
        return _inner.ListAsync(path, cancellationToken);
    }

    public async ValueTask<ISmbFile> OpenAsync(string path, SmbOpenMode mode, CancellationToken cancellationToken)
        => new File(this, path, await _inner.OpenAsync(path, mode, cancellationToken));

    public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken) => _inner.CreateDirectoryAsync(path, cancellationToken);

    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken) => _inner.DeleteAsync(path, cancellationToken);

    public ValueTask RenameAsync(string fromPath, string toPath, bool replace, CancellationToken cancellationToken) => _inner.RenameAsync(fromPath, toPath, replace, cancellationToken);

    public ValueTask SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken cancellationToken) => _inner.SetTimesAsync(path, created, modified, cancellationToken);

    public ValueTask<SmbSpace?> GetSpaceAsync(CancellationToken cancellationToken) => _inner.GetSpaceAsync(cancellationToken);

    private sealed class Held
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class File(HeldFileSystem owner, string path, ISmbFile inner) : ISmbFile
    {
        private int _inFlight;

        public long Length => inner.Length;

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            Enter();
            Interlocked.Increment(ref owner._reads);
            try
            {
                if (owner._givingUp.ContainsKey(path))
                {
                    throw new TaskCanceledException("the backend gave up waiting for the remote store");
                }

                if (owner._held.TryGetValue(path, out var held))
                {
                    held.Reached.TrySetResult();
                    try
                    {
                        await held.Gate.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref owner._cancelled);
                        throw;
                    }
                }

                return await inner.ReadAsync(offset, buffer, cancellationToken);
            }
            finally
            {
                Leave();
            }
        }

        public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                await inner.WriteAsync(offset, data, cancellationToken);
            }
            finally
            {
                Leave();
            }
        }

        public async ValueTask SetLengthAsync(long length, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                await inner.SetLengthAsync(length, cancellationToken);
            }
            finally
            {
                Leave();
            }
        }

        public async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                await inner.FlushAsync(cancellationToken);
            }
            finally
            {
                Leave();
            }
        }

        public async ValueTask DisposeAsync()
        {
            Enter();
            try
            {
                await inner.DisposeAsync();
                Interlocked.Increment(ref owner._closed);
            }
            finally
            {
                Leave();
            }
        }

        private void Enter()
        {
            var now = Interlocked.Increment(ref _inFlight);
            for (var most = Volatile.Read(ref owner._mostAtOnce); now > most; most = Volatile.Read(ref owner._mostAtOnce))
            {
                if (Interlocked.CompareExchange(ref owner._mostAtOnce, now, most) == most)
                {
                    break;
                }
            }
        }

        private void Leave() => Interlocked.Decrement(ref _inFlight);
    }
}
