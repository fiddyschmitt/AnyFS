namespace AnyFS.Sources.Common;

/// <summary>
/// A sign-in found to need the person, remembered for a while: a renewal that could not be had without a window is not
/// tried again by each request that comes after it, every one launching a browser of its own to learn the same. Until
/// 22-Nov-2026 Teams' and Outlook's web sessions kept no such memory: once lapsed, the k-th request waiting had k
/// headless launches before it, each of up to a minute. The sign-in read again (a start the person asked for) forgets it.
/// </summary>
/// <param name="remembered">How long a lapse answers by itself (two minutes by default, as SharePoint's web session's).</param>
public sealed class SignInLapse(TimeSpan? remembered = null, TimeProvider? time = null)
{
    private readonly TimeSpan _for = remembered ?? TimeSpan.FromMinutes(2);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private (long At, string Message, bool Permanent)? _lapse;

    /// <summary>Throws the lapse remembered, as it was said, while it is remembered; nothing otherwise.</summary>
    public void ThrowIfRemembered()
    {
        (long At, string Message, bool Permanent)? lapse;
        lock (_gate)
        {
            lapse = _lapse;
        }

        if (lapse is { } known && _time.GetElapsedTime(known.At) < _for)
        {
            throw new SignInRequiredException(known.Message, permanent: known.Permanent);
        }
    }

    /// <summary>Remembers <paramref name="lapse"/> from now, and hands it back to be thrown.</summary>
    public SignInRequiredException Remember(SignInRequiredException lapse)
    {
        ArgumentNullException.ThrowIfNull(lapse);
        lock (_gate)
        {
            _lapse = (_time.GetTimestamp(), lapse.Message, lapse.Permanent);
        }

        return lapse;
    }

    /// <summary>The sign-in was read: nothing is remembered.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _lapse = null;
        }
    }
}
