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
    private static HttpResponseMessage Throttled(HttpStatusCode status, int? seconds)
    {
        var response = new HttpResponseMessage(status);
        if (seconds is { } s)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        }

        return response;
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
