using System.Collections.Concurrent;
using System.Globalization;
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
/// A service that says which of its limits an answer came under (<c>RateLimit-Name</c>, as GitLab does on every answer) is
/// held by that limit alone, for the kinds of request it covers, and the rest go on; one that says what is left of a limit
/// (<c>RateLimit-Remaining</c>, with <c>RateLimit-Reset</c>) is asked no more than that until the reset, counting what is in
/// flight, so it is never asked what it would refuse (6-Nov-2026).
/// </summary>
public sealed class RequestPace
{
    // a Retry-After or a reset past this is taken as this (GitLab's windows are an hour at the most as it comes; the longest hold seen in the SharePoint lab was 120 s)
    private static readonly TimeSpan LongestHold = TimeSpan.FromHours(1);

    private static readonly ConcurrentDictionary<string, RequestPace> Paces = new(StringComparer.OrdinalIgnoreCase);

    private readonly TokenBucketRateLimiter? _limiter;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, long> _held = new(StringComparer.Ordinal);        // by a kind of request or a limit's name: UTC ticks
    private readonly ConcurrentDictionary<string, string> _limitOf = new(StringComparer.Ordinal);   // a kind of request → the limit the service last named for it
    private readonly Dictionary<string, Quota> _quotas = new(StringComparer.Ordinal);               // by a limit's name: what is left of it (under _gate)
    private string? _lastLimit;   // the limit the service named last, for a kind of request not answered yet
    private long _heldUntil;      // every request: UTC ticks

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
    /// answer (<see cref="Hold(HttpResponseMessage)"/>), which every request of the service then waits out together.
    /// </summary>
    public static RequestPace Unpaced(string key) => Paces.GetOrAdd(key, _ => new RequestPace());

    /// <summary>Whether requests go at a pace of their own, or only as fast as they are made.</summary>
    public bool IsPaced => _limiter is not null;

    /// <summary>Sets the pace for <paramref name="key"/>: a test's stand-in service, which need not be spared as the real one must.</summary>
    internal static void Use(string key, RequestPace pace) => Paces[key] = pace;

    /// <summary>Waits for a request's turn: past any hold, then for a token where there is a pace, then past a hold that began meanwhile.</summary>
    public ValueTask WaitAsync(CancellationToken ct) => WaitAsync(null, ct);

    /// <summary>The same for a request of a <paramref name="kind"/>, without a turn to give back: for a caller that does not count what it has in flight.</summary>
    public async ValueTask WaitAsync(string? kind, CancellationToken ct)
    {
        using (await TakeAsync(kind, ct).ConfigureAwait(false))
        {
        }
    }

    /// <summary>
    /// A turn for a request of a <paramref name="kind"/> (<see cref="Hold(HttpResponseMessage, string?)"/>): past the holds on every
    /// request, on that kind, and on the limit the service last named for that kind; and where the service has said what is left
    /// of that limit, not before there is some for it, counting the requests in flight under the limit. Dispose the turn when the
    /// request is answered or given up on; give the answer to <see cref="Hold(HttpResponseMessage, string?)"/> first.
    /// </summary>
    public async ValueTask<Turn> TakeAsync(string? kind, CancellationToken ct)
    {
        while (true)
        {
            await PastHoldAsync(kind, ct).ConfigureAwait(false);
            if (_limiter is not null)
            {
                using (await _limiter.AcquireAsync(1, ct).ConfigureAwait(false))
                {
                }

                await PastHoldAsync(kind, ct).ConfigureAwait(false);
            }

            // a kind not answered yet is taken to be under the limit the service named last: once, and never a 429 for it
            var limit = kind is null ? null : _limitOf.TryGetValue(kind, out var named) ? named : Volatile.Read(ref _lastLimit);
            TimeSpan wait;
            Task changed;
            lock (_gate)
            {
                if (limit is null || !_quotas.TryGetValue(limit, out var quota) || quota.Reset <= DateTime.UtcNow.Ticks)
                {
                    // nothing known of what is left, or the window it was known for is over: the answers say
                    return new Turn(this, null);
                }

                if (quota.Remaining - quota.InFlight > 0)
                {
                    quota.InFlight++;
                    return new Turn(this, limit);
                }

                // until the reset, or until one in flight is answered (there may be some left for this one then)
                wait = TimeSpan.FromTicks(quota.Reset - DateTime.UtcNow.Ticks);
                changed = quota.Changed.Task;
            }

            await Task.WhenAny(changed, Task.Delay(wait, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// After a throttled answer (429 or 503 with Retry-After), holds every request until the time it gave has passed.
    /// The time it gave when that starts a hold or lengthens one by a second or more (the many answers of a burst do
    /// not each say so); null otherwise.
    /// </summary>
    public TimeSpan? Hold(HttpResponseMessage response) => Hold(response, null);

    /// <summary>
    /// The same for an answer to a request of a <paramref name="kind"/> (its method and the shape of its path): what is held
    /// is that kind, or, where the service names the limit the answer came under (<c>RateLimit-Name</c>), that limit, for every
    /// kind of request it has named it for. What the answer says is left of the limit (<c>RateLimit-Remaining</c>, until
    /// <c>RateLimit-Reset</c>) is what <see cref="TakeAsync"/> keeps to from then; at none left, the wait until the reset is
    /// returned as a hold is. Given every answer, it learns which limit covers which kind, and what is left, as the service
    /// tells it.
    /// </summary>
    public TimeSpan? Hold(HttpResponseMessage response, string? kind)
    {
        ArgumentNullException.ThrowIfNull(response);
        var now = DateTime.UtcNow;
        var limit = Header(response, "RateLimit-Name");
        if (limit is not null)
        {
            Volatile.Write(ref _lastLimit, limit);
            if (kind is not null)
            {
                _limitOf[kind] = limit;
            }
        }

        TimeSpan? quotaWait = null;
        if (limit is not null && long.TryParse(Header(response, "RateLimit-Remaining"), NumberStyles.None, CultureInfo.InvariantCulture, out var remaining)
            && long.TryParse(Header(response, "RateLimit-Reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var resetAt))
        {
            var reset = Math.Min(DateTimeOffset.FromUnixTimeSeconds(resetAt).UtcTicks, now.Ticks + LongestHold.Ticks);
            lock (_gate)
            {
                if (!_quotas.TryGetValue(limit, out var quota) || quota.Reset != reset)
                {
                    // a window not seen before: what is in flight under the old one has been counted by now
                    var old = quota;
                    _quotas[limit] = quota = new Quota { Reset = reset, Remaining = remaining };
                    old?.Changed.TrySetResult();
                }
                else
                {
                    // answers come back in any order: the smallest figure is the latest word
                    quota.Remaining = Math.Min(quota.Remaining, remaining);
                }

                if (quota.Remaining <= 0 && reset > now.Ticks && !quota.Said)
                {
                    quota.Said = true;
                    quotaWait = TimeSpan.FromTicks(reset - now.Ticks);
                }
            }
        }

        TimeSpan? wait = null;
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            wait = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - now;
        }

        if (wait is not { } span || span <= TimeSpan.Zero)
        {
            return quotaWait;
        }

        span = span < LongestHold ? span : LongestHold;
        var until = now.Ticks + span.Ticks;
        var key = limit ?? kind;
        if (key is null)
        {
            return Lengthen(ref _heldUntil, until, now.Ticks) ? span : quotaWait;
        }

        var seen = _held.GetOrAdd(key, 0L);
        return Lengthen(key, until, now.Ticks, seen) ? span : quotaWait;
    }

    /// <summary>How long a request of <paramref name="kind"/> is held for now: zero where it is not.</summary>
    public TimeSpan HeldFor(string? kind)
    {
        var left = HeldUntil(kind) - DateTime.UtcNow.Ticks;
        return left > 0 ? TimeSpan.FromTicks(left) : TimeSpan.Zero;
    }

    private void Let(string? limit)
    {
        if (limit is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_quotas.TryGetValue(limit, out var quota))
            {
                quota.InFlight = Math.Max(0, quota.InFlight - 1);
                var waited = quota.Changed;
                quota.Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waited.TrySetResult();
            }
        }
    }

    private static bool Lengthen(ref long held, long until, long now)
    {
        for (var seen = Volatile.Read(ref held); until > seen; seen = Volatile.Read(ref held))
        {
            if (Interlocked.CompareExchange(ref held, until, seen) == seen)
            {
                return seen <= now || until - seen >= TimeSpan.TicksPerSecond;
            }
        }

        return false;
    }

    private bool Lengthen(string key, long until, long now, long seen)
    {
        while (until > seen)
        {
            if (_held.TryUpdate(key, until, seen))
            {
                return seen <= now || until - seen >= TimeSpan.TicksPerSecond;
            }

            seen = _held.GetOrAdd(key, 0L);
        }

        return false;
    }

    private long HeldUntil(string? kind)
    {
        var until = Volatile.Read(ref _heldUntil);
        if (kind is not null)
        {
            if (_held.TryGetValue(kind, out var own))
            {
                until = Math.Max(until, own);
            }

            if (_limitOf.TryGetValue(kind, out var limit) && _held.TryGetValue(limit, out var named))
            {
                until = Math.Max(until, named);
            }
        }

        return until;
    }

    private async ValueTask PastHoldAsync(string? kind, CancellationToken ct)
    {
        for (var left = HeldUntil(kind) - DateTime.UtcNow.Ticks; left > 0; left = HeldUntil(kind) - DateTime.UtcNow.Ticks)
        {
            await Task.Delay(TimeSpan.FromTicks(left), ct).ConfigureAwait(false);
        }
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    /// <summary>What a service last said is left of one of its limits, and until when.</summary>
    private sealed class Quota
    {
        public long Reset;        // UTC ticks

        public long Remaining;

        public int InFlight;      // requests sent under it and not answered yet

        public bool Said;         // that it is used up, once a window

        public TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);   // one in flight answered, or the window over: for a turn that waits
    }

    /// <summary>A request's turn under a limit, from <see cref="TakeAsync"/>: say when it is answered, and dispose it when it is over.</summary>
    public sealed class Turn : IDisposable
    {
        private readonly RequestPace _pace;
        private string? _limit;

        internal Turn(RequestPace pace, string? limit)
        {
            _pace = pace;
            _limit = limit;
        }

        /// <summary>The request was answered, the answer given to <see cref="Hold(HttpResponseMessage, string?)"/> first: no longer in flight.</summary>
        public void Answered()
        {
            _pace.Let(_limit);
            _limit = null;
        }

        public void Dispose() => Answered();
    }
}
