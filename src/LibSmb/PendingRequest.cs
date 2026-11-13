using System.Diagnostics;
using LibSmb.Protocol;

namespace LibSmb;

/// <summary>
/// A request handled on its own that the client may cancel ([MS-SMB2] 3.3.5.16) and that goes asynchronous when it runs
/// long (3.3.4.2): an interim STATUS_PENDING response first, then the real one under the same AsyncId. Whether its reply is
/// asynchronous is settled once: by the connection's sweep finding it still running, or by the request finishing first.
/// </summary>
internal sealed class PendingRequest(Header header) : IDisposable
{
    private const int Running = 0;
    private const int Finished = 1;
    private const int Asynchronous = 2;
    private int _state;

    public Header Header { get; } = header;

    /// <summary>When it was read, for the sweep to tell how long it has run.</summary>
    public long Started { get; } = Stopwatch.GetTimestamp();

    /// <summary>Cancelled by the client's CANCEL, and when the connection goes.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>Completes once its interim response is in line for the wire, which its real reply must follow.</summary>
    public TaskCompletionSource InterimSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Its AsyncId once it went asynchronous; 0 before.</summary>
    public ulong AsyncId { get; private set; }

    /// <summary>Whether it went asynchronous, so its reply is an asynchronous one that follows the interim response.</summary>
    public bool WentAsync => Volatile.Read(ref _state) == Asynchronous;

    /// <summary>Whether neither has happened yet: finished, or gone asynchronous.</summary>
    public bool IsRunning => Volatile.Read(ref _state) == Running;

    /// <summary>
    /// Settles it asynchronous under <paramref name="asyncId"/>: false when it finished first, or went asynchronous already
    /// (its id is never changed). Called by the connection's one sweep only.
    /// </summary>
    public bool TryGoAsync(ulong asyncId)
    {
        if (!IsRunning)
        {
            return false;
        }

        // the id is in place before the state says asynchronous, for the request finishing on another thread to read
        AsyncId = asyncId;
        return Interlocked.CompareExchange(ref _state, Asynchronous, Running) == Running;
    }

    /// <summary>Settles it finished: false when it went asynchronous first, so its reply must be an asynchronous one.</summary>
    public bool TryFinish() => Interlocked.CompareExchange(ref _state, Finished, Running) == Running;

    /// <summary>Cancels its file system call, unless it was answered meanwhile.</summary>
    public void Cancel()
    {
        try
        {
            Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose() => Cancellation.Dispose();
}
