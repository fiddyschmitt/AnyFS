using System.Collections.Concurrent;

namespace LibSmb.Tests;

/// <summary>The calls of a file system a test can hold (<see cref="HeldFileSystem.Hold"/>).</summary>
public enum HeldCall
{
    /// <summary>The reads of a file.</summary>
    Read,

    /// <summary>The listings of a folder.</summary>
    Listing,

    /// <summary>The lookups of a path.</summary>
    Lookup,

    /// <summary>The opens of a file.</summary>
    Open,
}

/// <summary>
/// A folder served as <see cref="LocalFileSystem"/> serves it, except that the calls the test holds (the reads of a file,
/// as a rule; a folder's listings, a path's lookups or a file's opens as well) wait until the test lets them go: a backend
/// call as slow as a cold fetch from a remote store, for as long as the test likes. It counts what a test needs to see:
/// the calls in flight on each open (one open's calls must never overlap), the held calls whose cancellation came, and
/// the files closed.
/// </summary>
internal sealed class HeldFileSystem(string root) : ISmbFileSystem
{
    private readonly LocalFileSystem _inner = new(root);
    private readonly ConcurrentDictionary<(HeldCall Call, string Path), Held> _held = new();
    private readonly ConcurrentDictionary<string, byte> _givingUp = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Countdown> _lookupsGivingUp = new(StringComparer.Ordinal);
    private int _mostAtOnce;
    private int _cancelled;
    private int _closed;
    private int _listed;
    private int _reads;

    public bool CaseInsensitive => _inner.CaseInsensitive;

    /// <summary>The most calls seen in flight at once on any one open.</summary>
    public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

    /// <summary>Held calls that ended because their cancellation token fired.</summary>
    public int Cancelled => Volatile.Read(ref _cancelled);

    /// <summary>Opens closed (disposed).</summary>
    public int Closed => Volatile.Read(ref _closed);

    /// <summary>Calls to read a file, all opens'.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>Directories listed.</summary>
    public int Listed => Volatile.Read(ref _listed);

    /// <summary>
    /// How long a held read whose cancellation came takes to come back (no time unless set): a backend across a network
    /// does not stop the moment it is told to.
    /// </summary>
    public TimeSpan StopDelay { get; set; }

    /// <summary>Holds every read of <paramref name="path"/> (or every call of another kind on it) from now until <see cref="Release"/>.</summary>
    public void Hold(string path, HeldCall call = HeldCall.Read) => _held[(call, path)] = new Held();

    /// <summary>Makes every read of <paramref name="path"/> give up on its own, as a backend's call that times out does.</summary>
    public void GiveUp(string path) => _givingUp[path] = 0;

    /// <summary>
    /// Makes the lookups of <paramref name="path"/> give up on their own, as <see cref="GiveUp"/> does its reads, once
    /// <paramref name="after"/> more of them were answered (a CREATE looks its file up twice: to see whether it is there, and
    /// for its reply), until <see cref="AnswerLookups"/>.
    /// </summary>
    public void GiveUpLookups(string path, int after = 0) => _lookupsGivingUp[path] = new Countdown(after);

    /// <summary>Lets the lookups of <paramref name="path"/> through again.</summary>
    public void AnswerLookups(string path) => _lookupsGivingUp.TryRemove(path, out _);

    /// <summary>Lets the held calls of <paramref name="path"/> go, and every later one through at once.</summary>
    public void Release(string path, HeldCall call = HeldCall.Read)
    {
        if (_held.TryRemove((call, path), out var held))
        {
            held.Gate.TrySetResult();
        }
    }

    /// <summary>Completes once a held call of <paramref name="path"/> is waiting.</summary>
    public Task Reached(string path, HeldCall call = HeldCall.Read) => _held[(call, path)].Reached.Task;

    public async ValueTask<SmbEntry?> StatAsync(string path, CancellationToken cancellationToken)
    {
        if (_lookupsGivingUp.TryGetValue(path, out var left) && left.Passed())
        {
            throw new TaskCanceledException("the backend gave up waiting for the remote store");
        }

        await WhileHeldAsync(HeldCall.Lookup, path, cancellationToken);
        return await _inner.StatAsync(path, cancellationToken);
    }

    public IAsyncEnumerable<SmbEntry> ListAsync(string path, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _listed);
        return ListHeldAsync(path, cancellationToken);
    }

    public async ValueTask<ISmbFile> OpenAsync(string path, SmbOpenMode mode, CancellationToken cancellationToken)
    {
        await WhileHeldAsync(HeldCall.Open, path, cancellationToken);
        return new File(this, path, await _inner.OpenAsync(path, mode, cancellationToken));
    }

    private async IAsyncEnumerable<SmbEntry> ListHeldAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await WhileHeldAsync(HeldCall.Listing, path, cancellationToken);
        await foreach (var entry in _inner.ListAsync(path, cancellationToken))
        {
            yield return entry;
        }
    }

    /// <summary>Waits while the test holds the calls of this kind on <paramref name="path"/>; a wait that was cancelled is counted.</summary>
    private async ValueTask WhileHeldAsync(HeldCall call, string path, CancellationToken cancellationToken)
    {
        if (!_held.TryGetValue((call, path), out var held))
        {
            return;
        }

        held.Reached.TrySetResult();
        try
        {
            await held.Gate.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            if (StopDelay > TimeSpan.Zero)
            {
                await Task.Delay(StopDelay, CancellationToken.None);
            }

            throw;
        }
    }

    public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken) => _inner.CreateDirectoryAsync(path, cancellationToken);

    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken) => _inner.DeleteAsync(path, cancellationToken);

    public ValueTask RenameAsync(string fromPath, string toPath, bool replace, CancellationToken cancellationToken) => _inner.RenameAsync(fromPath, toPath, replace, cancellationToken);

    public ValueTask SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken cancellationToken) => _inner.SetTimesAsync(path, created, modified, cancellationToken);

    public ValueTask<SmbSpace?> GetSpaceAsync(CancellationToken cancellationToken) => _inner.GetSpaceAsync(cancellationToken);

    /// <summary>So many calls pass before the rest are affected.</summary>
    private sealed class Countdown(int passing)
    {
        private int _left = passing;

        /// <summary>Whether the calls that pass are used up; each call counts one.</summary>
        public bool Passed() => Interlocked.Decrement(ref _left) < 0;
    }

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

                await owner.WhileHeldAsync(HeldCall.Read, path, cancellationToken);
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
