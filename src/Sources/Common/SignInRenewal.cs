using System.Runtime.ExceptionServices;

namespace AnyFS.Sources.Common;

/// <summary>
/// A sign-in made again when the server no longer takes it, once for all the requests that were refused with it. Five
/// sessions had a copy of this each (the review of 21-Nov-2026, C2 and R9b), and each copy ran the renewal as part of
/// the request that happened to be refused first:
/// <list type="bullet">
/// <item>That request given up (a search cancelled, a call the host stopped waiting for) cancelled the renewal for all
/// the others. In Artifactory, whose refresh token is good for one use, that could lose the new pair.</item>
/// <item>The time of the renewal was written down before it ran. One that was cancelled or failed then counted as made,
/// and for the next half minute every request was told the server had "refused the session again just after it was
/// renewed", which it had not been.</item>
/// </list>
/// Here the renewal is one task on the session's own lifetime, whoever began it. A request waits for it only as long
/// as the request itself lasts. A renewal that failed is what the requests after it are told, as it failed, and it is
/// not tried again until <c>again</c> has passed: each try may be a browser started. One that worked and is followed by
/// another refusal within that time is not made again either (<see cref="RenewAsync"/> answers false): the server does
/// not take what it just gave.
/// </summary>
public sealed class SignInRenewal : IDisposable
{
    private readonly Func<CancellationToken, Task> _renew;
    private readonly TimeSpan _again;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _stopped;
    private readonly Lock _gate = new();
    private int _generation;
    private Task? _running;          // the renewal under way
    private long _renewedAt;         // when the last one worked; 0 for none yet
    private ExceptionDispatchInfo? _failure;   // what the last one failed with, until one is tried again
    private long _failedAt;

    /// <param name="renew">Signs in again, and keeps what it got; throws what the requests are to be told where it cannot. Given the session's lifetime, not a request's.</param>
    /// <param name="again">How long after a renewal, made or failed, before another is tried.</param>
    public SignInRenewal(Func<CancellationToken, Task> renew, TimeSpan again, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(renew);
        _renew = renew;
        _again = again;
        _time = time ?? TimeProvider.System;
        _stopped = _lifetime.Token;
    }

    /// <summary>A number that moves each time the sign-in is made again: read as a request is signed.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>The sign-in was made again: what failed for want of one may be asked again.</summary>
    public event Action? Renewed;

    /// <summary>
    /// After a request signed at <paramref name="seen"/> was refused. True once the sign-in is newer than that one: made
    /// again now, or by another request since. False where it was made again just before and the server refuses it
    /// still. Throws what the renewal failed with, to the request that began it and to those that come while that
    /// stands; and the request's own cancellation, which leaves the renewal running for the others.
    /// </summary>
    public async Task<bool> RenewAsync(int seen, CancellationToken ct)
    {
        Task running;
        ExceptionDispatchInfo? failed = null;
        lock (_gate)
        {
            if (Generation != seen)
            {
                return true;
            }

            if (_running is null)
            {
                if (_failure is not null && _time.GetElapsedTime(_failedAt) < _again)
                {
                    failed = _failure;
                }
                else if (_renewedAt != 0 && _time.GetElapsedTime(_renewedAt) < _again)
                {
                    return false;
                }
                else
                {
                    _failure = null;

                    // nobody's request: not a walk's, not a person's, and not to be given up with either
                    using (ExecutionContext.SuppressFlow())
                    {
                        _running = Task.Run(RunAsync);
                    }

                    // looked at here, for when the request that began it has gone and no other waits
                    _running.ContinueWith(static done => _ = done.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }

            running = _running!;
        }

        failed?.Throw();
        await running.WaitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The sign-in came new by another road than a renewal of this one (the cookies of several services, new as a whole
    /// when one of them was renewed): requests signed before it are sent again without a renewal of their own.
    /// </summary>
    public void Moved() => Interlocked.Increment(ref _generation);

    /// <summary>Forgets a renewal that failed, so the next refusal tries again at once: the person has signed in meanwhile.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _failure = null;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await _renew(_stopped).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _failure = ExceptionDispatchInfo.Capture(ex);
                _failedAt = _time.GetTimestamp();
                _running = null;
            }

            throw;
        }

        lock (_gate)
        {
            Interlocked.Increment(ref _generation);
            _renewedAt = Math.Max(1, _time.GetTimestamp());
            _running = null;
        }

        Renewed?.Invoke();
    }

    /// <summary>Stops a renewal under way; those waiting for it are told it was cancelled.</summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
