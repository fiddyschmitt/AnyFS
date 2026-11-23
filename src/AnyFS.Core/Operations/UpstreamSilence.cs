namespace AnyFS.Core.Operations;

/// <summary>
/// Whether one of a source's upstreams is answering, from what the source reports of its requests to it
/// (<see cref="AnyFS.Sdk.ISourceContext.UpstreamWatcher"/>): how many are out, and when anything last came back (an
/// answer, bytes, entries, a call that ended well). It is silent while requests are out and nothing comes. That is what
/// a person's call is measured by (<see cref="TrackedSource"/>): not how long the call has taken, which a great folder
/// or a great file makes long while the server answers all the while, but how long the server has said nothing. One
/// for each server a source asks (a source of several sites, or of a service with several hosts, has several), so that
/// one that answers does not hide one that does not.
/// <para>
/// Once a call has been given up for it, the upstream is taken for silent until something comes from it: the calls
/// after that would wait on it are given up at once, where each waiting the limit out in turn made a search over a tree
/// wait it for every folder. One call every <see cref="ProbeEvery"/> is let wait the whole limit, which is how an
/// upstream that answers again is found; and anything that comes from it for any request, a walk's included, ends it.
/// </para>
/// </summary>
/// <param name="name">The server's name as its addresses give it; empty for a source's one upstream that is no address (a path, a mail file).</param>
internal sealed class UpstreamSilence(string name, TimeProvider time)
{
    /// <summary>How often one call is let wait the whole limit while the upstream is taken for silent.</summary>
    public static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a call is let run while the upstream is taken for silent before it is given up: time for one answered
    /// from what is kept, which asks nothing.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private int _out;
    private long _outSince;                 // when the first of the requests now out went out
    private long _arrived;                  // when anything last came back; 0 before the first
    private bool _silent;                   // a call has been given up, and nothing has come since
    private DateTimeOffset _silentSince;    // by the clock: when it last said anything, or the oldest request went out
    private long _probed;                   // when a call was last let wait the whole limit while silent

    public string Name { get; } = name;

    /// <summary>A request has gone out.</summary>
    public void Began()
    {
        lock (_gate)
        {
            if (_out++ == 0)
            {
                _outSince = time.GetTimestamp();
            }
        }
    }

    /// <summary>A request is over, whichever way it ended.</summary>
    public void Ended()
    {
        lock (_gate)
        {
            if (_out > 0)
            {
                _out--;
            }
        }
    }

    /// <summary>Something has come back. True when the upstream had been taken for silent: it is not any more.</summary>
    public bool Arrived()
    {
        lock (_gate)
        {
            _arrived = time.GetTimestamp();
            if (!_silent)
            {
                return false;
            }

            _silent = false;
            return true;
        }
    }

    /// <summary>How long requests have been out with nothing coming back; null with none out.</summary>
    public TimeSpan? SilentFor()
    {
        lock (_gate)
        {
            return _out == 0 ? null : time.GetElapsedTime(Math.Max(_outSince, _arrived));
        }
    }

    /// <summary>Whether a call has been given up and nothing has come since.</summary>
    public bool IsSilent
    {
        get
        {
            lock (_gate)
            {
                return _silent;
            }
        }
    }

    /// <summary>Since when, by the clock, it has said nothing: as of the last time it was found silent.</summary>
    public DateTimeOffset SilentSince
    {
        get
        {
            lock (_gate)
            {
                return _silentSince;
            }
        }
    }

    /// <summary>
    /// A call is being given up for the silence. True for the first to say so since anything came: the upstream is taken
    /// for silent from now. With the requests out and how long nothing has come, for the log.
    /// </summary>
    public bool Find(out int requestsOut, out TimeSpan silentFor)
    {
        lock (_gate)
        {
            requestsOut = _out;
            silentFor = _out == 0 ? TimeSpan.Zero : time.GetElapsedTime(Math.Max(_outSince, _arrived));
            if (_silent)
            {
                return false;
            }

            _silent = true;
            _silentSince = time.GetUtcNow() - silentFor;
            _probed = time.GetTimestamp();
            return true;
        }
    }

    /// <summary>
    /// Whether a call may wait the whole limit on it: always while it is not taken for silent, and while it is, one call
    /// every <see cref="ProbeEvery"/>.
    /// </summary>
    public bool MayWait()
    {
        lock (_gate)
        {
            if (!_silent)
            {
                return true;
            }

            if (time.GetElapsedTime(_probed) < ProbeEvery)
            {
                return false;
            }

            _probed = time.GetTimestamp();
            return true;
        }
    }
}
