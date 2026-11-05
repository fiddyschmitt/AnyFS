using System.Collections.Concurrent;
using System.Net;
using System.Threading.RateLimiting;

namespace AnyFS.Sources.Common;

/// <summary>
/// How fast AnyFS sends requests to one service (a host, or an account where there is none), from every source, client
/// and worker together: so many a second on average, with up to a burst at once after a quiet spell (a token bucket), so a
/// folder opened in a hurry is not kept waiting while a crawl stays within what the service allows; and when the service
/// throttles all the same (429 or 503 with Retry-After: a person's browser and other apps share its limit), none at all
/// until the time it gave has passed, as Microsoft asks of a client with many threads. Requests wait their turn,
/// oldest first, rather than fail. Only Microsoft 365 gives numbers of its own, <c>MicrosoftPace</c>'s (spec 7.3, within
/// what SharePoint Online and Graph publish); every other service is <see cref="Unpaced"/>: asked as fast as its settings
/// allow, and held only when it asks (the user, 5-Nov-2026: no caps that are not in the settings).
/// </summary>
public sealed class RequestPace
{
    // a Retry-After past this is taken as this (the longest seen in the SharePoint lab was 120 s)
    private static readonly TimeSpan LongestHold = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, RequestPace> Paces = new(StringComparer.OrdinalIgnoreCase);

    private readonly TokenBucketRateLimiter? _limiter;
    private long _heldUntil;   // UTC ticks

    /// <param name="burst">How many may go at once, after a quiet spell.</param>
    /// <param name="perSecond">How many a second on average: one turn added every 1/<paramref name="perSecond"/> second.</param>
    internal RequestPace(int burst, int perSecond)
    {
        _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = burst,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1.0 / perSecond),
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    }

    private RequestPace()
    {
    }

    /// <summary>
    /// The pace for <paramref name="key"/> (a service's host, or an account), shared by all that ask for it. The first to
    /// ask sets its numbers: a key belongs to one service, which always gives the same.
    /// </summary>
    public static RequestPace For(string key, int burst, int perSecond) => Paces.GetOrAdd(key, _ => new RequestPace(burst, perSecond));

    /// <summary>
    /// No pace for <paramref name="key"/>: requests go as fast as whoever makes them allows, and are held only after a throttled
    /// answer (<see cref="Hold"/>), which every request of the service then waits out together.
    /// </summary>
    public static RequestPace Unpaced(string key) => Paces.GetOrAdd(key, _ => new RequestPace());

    /// <summary>Whether requests go at a pace of their own, or only as fast as they are made.</summary>
    public bool IsPaced => _limiter is not null;

    /// <summary>Sets the pace for <paramref name="key"/>: a test's stand-in service, which need not be spared as the real one must.</summary>
    internal static void Use(string key, RequestPace pace) => Paces[key] = pace;

    /// <summary>Waits for a request's turn: past any hold, then for a token where there is a pace, then past a hold that began meanwhile.</summary>
    public async ValueTask WaitAsync(CancellationToken ct)
    {
        await PastHoldAsync(ct).ConfigureAwait(false);
        if (_limiter is null)
        {
            return;
        }

        using (await _limiter.AcquireAsync(1, ct).ConfigureAwait(false))
        {
        }

        await PastHoldAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// After a throttled answer (429 or 503 with Retry-After), holds every request until the time it gave has passed.
    /// The time it gave when that starts a hold or lengthens one by a second or more (the many answers of a burst do
    /// not each say so); null otherwise.
    /// </summary>
    public TimeSpan? Hold(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            || (response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) is not { } wait
            || wait <= TimeSpan.Zero)
        {
            return null;
        }

        wait = wait < LongestHold ? wait : LongestHold;
        var now = DateTime.UtcNow.Ticks;
        var until = now + wait.Ticks;
        for (var seen = Volatile.Read(ref _heldUntil); until > seen; seen = Volatile.Read(ref _heldUntil))
        {
            if (Interlocked.CompareExchange(ref _heldUntil, until, seen) == seen)
            {
                return seen <= now || until - seen >= TimeSpan.TicksPerSecond ? wait : null;
            }
        }

        return null;
    }

    private async ValueTask PastHoldAsync(CancellationToken ct)
    {
        for (var left = Volatile.Read(ref _heldUntil) - DateTime.UtcNow.Ticks; left > 0; left = Volatile.Read(ref _heldUntil) - DateTime.UtcNow.Ticks)
        {
            await Task.Delay(TimeSpan.FromTicks(left), ct).ConfigureAwait(false);
        }
    }
}
