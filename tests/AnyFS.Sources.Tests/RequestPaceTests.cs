using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using AnyFS.Sources.Common;
using AnyFS.Sources.Microsoft365;
using AnyFS.Sources.SharePoint;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The pace of requests to SharePoint Online (spec 7.3): so many a second on average from all the workers together, a
/// burst at once after a quiet spell, and none while SharePoint has said to wait (a throttled answer's Retry-After), as
/// Microsoft asks of a client with many threads.
/// </summary>
[TestClass]
public class RequestPaceTests
{
    private static HttpResponseMessage Throttled(HttpStatusCode status, int? seconds, string? limit = null)
    {
        var response = new HttpResponseMessage(status);
        if (seconds is { } s)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        }

        if (limit is not null)
        {
            response.Headers.TryAddWithoutValidation("RateLimit-Name", limit);
        }

        return response;
    }

    /// <summary>An answer as GitLab gives every one under a limit: the limit's name and what is left of it, reset at a time.</summary>
    private static HttpResponseMessage Counted(string limit, int remaining, DateTimeOffset reset)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("RateLimit-Name", limit);
        response.Headers.TryAddWithoutValidation("RateLimit-Limit", "100");
        response.Headers.TryAddWithoutValidation("RateLimit-Observed", (100 - remaining).ToString());
        response.Headers.TryAddWithoutValidation("RateLimit-Remaining", remaining.ToString());
        response.Headers.TryAddWithoutValidation("RateLimit-Reset", reset.ToUnixTimeSeconds().ToString());
        return response;
    }

    [TestMethod]
    public async Task AHoldOnOneKindOfRequestLeavesTheOthersFree()
    {
        var pace = new RequestPace(burst: 100, perSecond: 100);
        Assert.AreEqual(TimeSpan.FromSeconds(1), pace.Hold(Throttled(HttpStatusCode.TooManyRequests, 1), "GET projects/*/repository/tree"));
        Assert.AreEqual(TimeSpan.Zero, pace.HeldFor("GET projects/*/issues"), "another kind of request goes on");
        Assert.IsTrue(pace.HeldFor("GET projects/*/repository/tree") > TimeSpan.FromMilliseconds(500));

        var clock = Stopwatch.StartNew();
        await pace.WaitAsync("GET projects/*/issues", CancellationToken.None);
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromMilliseconds(300), $"not held: {clock.ElapsedMilliseconds} ms");
        await pace.WaitAsync("GET projects/*/repository/tree", CancellationToken.None);
        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromMilliseconds(900), $"the throttled kind waited out the second: {clock.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    public async Task ALimitTheServerNamesHoldsEveryKindOfRequestItHasNamedItFor()
    {
        // GitLab names the limit each answer came under; a 429 under it holds every kind of request known to be under it
        var pace = new RequestPace(burst: 100, perSecond: 100);
        var reset = DateTimeOffset.UtcNow.AddMinutes(5);
        Assert.IsNull(pace.Hold(Counted("throttle_authenticated_api", 50, reset), "GET projects/*/issues"), "plenty left: no hold");
        Assert.IsNull(pace.Hold(Counted("throttle_authenticated_files_api", 50, reset), "GET projects/*/repository/files/*"));
        Assert.AreEqual(TimeSpan.FromSeconds(1), pace.Hold(Throttled(HttpStatusCode.TooManyRequests, 1, "throttle_authenticated_api"), "GET projects/*/repository/tree"));
        Assert.IsTrue(pace.HeldFor("GET projects/*/issues") > TimeSpan.FromMilliseconds(500), "under the same limit, as its answers said");
        Assert.IsTrue(pace.HeldFor("GET projects/*/repository/tree") > TimeSpan.FromMilliseconds(500));
        Assert.AreEqual(TimeSpan.Zero, pace.HeldFor("GET projects/*/repository/files/*"), "under another limit");
        Assert.AreEqual(TimeSpan.Zero, pace.HeldFor("GET groups"), "not known to be under any yet");
        Assert.AreEqual(TimeSpan.Zero, pace.HeldFor(null), "the holds on every request are another matter");

        var clock = Stopwatch.StartNew();
        await pace.WaitAsync("GET projects/*/issues", CancellationToken.None);
        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromMilliseconds(900), $"held with the rest of its limit: {clock.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    public async Task AQuotaUsedUpHoldsItsLimitUntilTheResetBeforeTheServerNeedSay429()
    {
        var pace = new RequestPace(burst: 100, perSecond: 100);
        var reset = DateTimeOffset.UtcNow.AddSeconds(2);   // a whole second, as the header gives it: one to two seconds on
        var wait = pace.Hold(Counted("throttle_authenticated_api", 0, reset), "GET projects/*/repository/tree");
        Assert.IsNotNull(wait);
        Assert.IsTrue(wait.Value > TimeSpan.FromMilliseconds(900) && wait.Value <= TimeSpan.FromSeconds(2), wait.ToString());
        Assert.IsNull(pace.Hold(Counted("throttle_authenticated_api", 0, reset), "GET projects/*/repository/tree"), "the same again is not news");

        var clock = Stopwatch.StartNew();
        await pace.WaitAsync("GET projects/*/repository/tree", CancellationToken.None);
        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromMilliseconds(900), $"waited for the reset: {clock.ElapsedMilliseconds} ms");
        Assert.AreEqual(TimeSpan.Zero, pace.HeldFor("GET projects/*/repository/tree"), "and then went on");
        Assert.IsNull(pace.Hold(Counted("throttle_authenticated_api", 0, DateTimeOffset.UtcNow.AddSeconds(-5)), "GET projects/*/repository/tree"), "a reset already past holds nothing");
    }

    [TestMethod]
    public async Task WhatIsInFlightCountsAgainstWhatIsLeftSoABurstNeverGoesOverIt()
    {
        // two left of the limit: two turns go, the third waits for the reset, and a kind not answered yet counts under the same limit
        var pace = new RequestPace(burst: 100, perSecond: 100);
        var reset = DateTimeOffset.UtcNow.AddSeconds(2);
        Assert.IsNull(pace.Hold(Counted("throttle_authenticated_api", 2, reset), "GET projects/*/issues"));
        using var first = await pace.TakeAsync("GET projects/*/issues", CancellationToken.None);
        using var second = await pace.TakeAsync("GET projects/*/wikis", CancellationToken.None);
        var third = pace.TakeAsync("GET projects/*/issues", CancellationToken.None).AsTask();
        await Task.Delay(100);
        Assert.IsFalse(third.IsCompleted, "nothing left for a third while two are in flight");

        // one answered with one left: the third goes
        first.Answered();
        Assert.IsNull(pace.Hold(Counted("throttle_authenticated_api", 1, reset), "GET projects/*/issues"));
        await Task.Delay(100);
        Assert.IsFalse(third.IsCompleted, "one left and one in flight: still none to spare");
        second.Answered();
        Assert.IsNull(pace.Hold(Counted("throttle_authenticated_api", 1, reset), "GET projects/*/wikis"));
        using var went = await third.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task AQuietPaceLetsABurstGoAtOnceThenKeepsToItsRate()
    {
        var pace = new RequestPace(burst: 5, perSecond: 10);
        var waits = Enumerable.Range(0, 15).Select(_ => pace.WaitAsync(CancellationToken.None)).ToList();
        Assert.AreEqual(5, waits.Count(w => w.IsCompleted), "the burst goes at once, the rest wait their turn");

        var clock = Stopwatch.StartNew();
        foreach (var wait in waits)
        {
            await wait;
        }

        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromMilliseconds(800), $"the other ten, one a tenth of a second: {clock.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    public async Task AThrottledAnswerHoldsEveryRequestForItsRetryAfter()
    {
        var pace = new RequestPace(burst: 100, perSecond: 100);
        Assert.IsNull(pace.Hold(new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.IsNull(pace.Hold(Throttled(HttpStatusCode.ServiceUnavailable, null)), "a 503 that gives no time holds nothing");
        Assert.AreEqual(TimeSpan.FromSeconds(1), pace.Hold(Throttled(HttpStatusCode.TooManyRequests, 1)));
        Assert.IsNull(pace.Hold(Throttled(HttpStatusCode.TooManyRequests, 1)), "another answer of the same burst is not news");

        var clock = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => pace.WaitAsync(CancellationToken.None).AsTask()));
        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromMilliseconds(900), $"every request waited out the second: {clock.ElapsedMilliseconds} ms");

        clock.Restart();
        await pace.WaitAsync(CancellationToken.None);
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromMilliseconds(500), "and then went on at the pace");
    }

    [TestMethod]
    public void OnlySharePointOnlineIsPacedAndOneTenantSharesItsPace()
    {
        Assert.IsNull(MicrosoftPace.ForHost("intranet.contoso.local"), "SharePoint Server has no such limit");
        Assert.IsNull(MicrosoftPace.ForHost("192.168.0.92"));
        Assert.IsNotNull(MicrosoftPace.ForHost("acme.sharepoint.us"));
        Assert.AreSame(MicrosoftPace.ForHost("contoso.sharepoint.com"), MicrosoftPace.ForHost("CONTOSO.SharePoint.com"), "every source and client for one tenant together");
        Assert.AreNotSame(MicrosoftPace.ForHost("contoso.sharepoint.com"), MicrosoftPace.ForHost("fabrikam.sharepoint.com"));
    }

    [TestMethod]
    public async Task OneThrottledRequestHoldsTheOthersOfItsTenant()
    {
        // SharePoint Online throttles the first request for a second; one sent meanwhile waits that second out
        var host = $"t{Guid.NewGuid():N}.sharepoint.com";
        var throttledAt = new TaskCompletionSource<TimeSpan>();
        var clock = Stopwatch.StartNew();
        var arrivals = new List<(string Path, TimeSpan At)>();
        var server = new Server(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (arrivals)
            {
                arrivals.Add((path, clock.Elapsed));
                if (path.EndsWith("/first", StringComparison.Ordinal) && arrivals.Count(a => a.Path == path) == 1)
                {
                    throttledAt.TrySetResult(clock.Elapsed);
                    return Throttled(HttpStatusCode.TooManyRequests, 1);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        using var rest = new SharePointRestClient(new Uri($"https://{host}/sites/a"), null, false, NullLogger.Instance, handler: server);
        var first = rest.GetJsonAsync("web/first", "first", CancellationToken.None);
        var at = await throttledAt.Task;
        using (await rest.GetJsonAsync("web/second", "second", CancellationToken.None))
        {
        }

        using (await first)
        {
        }

        TimeSpan second;
        lock (arrivals)
        {
            second = arrivals.Single(a => a.Path.EndsWith("/second", StringComparison.Ordinal)).At;
        }

        Assert.IsTrue(second - at >= TimeSpan.FromMilliseconds(900), $"sent {(second - at).TotalMilliseconds:F0} ms after the throttled answer");
    }

    private sealed class Server(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(answer(request));
    }
}
