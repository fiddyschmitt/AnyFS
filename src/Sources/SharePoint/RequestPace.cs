using System.Collections.Concurrent;
using System.Net;
using System.Threading.RateLimiting;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// How fast AnyFS sends requests to one SharePoint Online tenant (by its host, or by the account where there is none),
/// from every source, client and worker together: <see cref="PerSecond"/> a second on average, with up to
/// <see cref="Burst"/> at once after a quiet spell (a token bucket), so a folder opened in a hurry is not kept waiting
/// while a crawl stays within SharePoint Online's published default of 3,000 requests in 5 minutes for a user
/// (spec 7.3); and when SharePoint throttles all the same (429 or 503 with Retry-After: a person's browser and other
/// apps share that limit), none at all until the time it gave has passed, as Microsoft asks of a client with many
/// threads. Requests wait their turn, oldest first, rather than fail. SharePoint Server has no such limit and goes
/// unpaced.
/// </summary>
internal sealed class RequestPace
{
    public const int PerSecond = 10;

    public const int Burst = 100;

    // a Retry-After past this is taken as this (the longest seen in the lab was 120 s)
    private static readonly TimeSpan LongestHold = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, RequestPace> Paces = new(StringComparer.OrdinalIgnoreCase);

    private readonly TokenBucketRateLimiter _limiter;
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

    /// <summary>The pace for <paramref name="key"/> (a SharePoint Online host, or an account), shared by all that ask for it.</summary>
    public static RequestPace For(string key) => Paces.GetOrAdd(key, _ => new RequestPace(Burst, PerSecond));

    /// <summary>The pace for a site's host when it is SharePoint Online's (.sharepoint.com, .sharepoint.us); null for SharePoint Server.</summary>
    public static RequestPace? ForHost(string host) => MicrosoftCloud.FromSiteHost(host) is null ? null : For(host);

    /// <summary>Sets the pace for <paramref name="key"/>: a test's stand-in tenant, which need not be spared as SharePoint Online must.</summary>
    internal static void Use(string key, RequestPace pace) => Paces[key] = pace;

    /// <summary>Waits for a request's turn: past any hold, then for a token, then past a hold that began meanwhile.</summary>
    public async ValueTask WaitAsync(CancellationToken ct)
    {
        await PastHoldAsync(ct).ConfigureAwait(false);
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
