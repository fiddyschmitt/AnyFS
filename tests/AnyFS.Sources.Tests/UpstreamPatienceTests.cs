using System.Net;
using System.Net.Http.Headers;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using AnyFS.Sources.Outlook.Ews;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A wait the server asked for, which a source makes before it sends anything (a hold after a 429, a quota used up), is
/// the one silence the host cannot see: no request is out. A walk waits it out, as before. A person's request waited it
/// out too, up to the hour a hold may be; one longer than the request's patience is refused at once, saying until when
/// (the review of 21-Nov-2026, R6).
/// </summary>
[TestClass]
public class UpstreamPatienceTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    private static Task<T> AsAPersonAsync<T>(TimeSpan patience, Func<Task<T>> call) => Task.Run(async () =>
    {
        UpstreamPatience.Current = patience;
        return await call();
    });

    [TestMethod]
    public async Task AHoldLongerThanAPersonsPatienceIsRefusedAtOnceAndAWalkWaitsItOut()
    {
        var pace = new RequestPace(burst: 100, perSecond: 100);
        using var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        Assert.IsNotNull(pace.Hold(throttled), "the server asked for half a minute without requests");

        using var stop = new CancellationTokenSource();
        var walking = pace.WaitAsync(stop.Token).AsTask();
        var patient = AsAPersonAsync(TimeSpan.FromMinutes(5), async () =>
        {
            await pace.WaitAsync(stop.Token);
            return true;
        });

        var refused = await AsAPersonAsync(TimeSpan.FromSeconds(5), () => Assert.ThrowsExactlyAsync<VfsException>(() => pace.WaitAsync(CancellationToken.None).AsTask().WaitAsync(Soon)));
        Assert.AreEqual(VfsError.Unavailable, refused.Error);
        StringAssert.Contains(refused.Message, "asked for no requests until");

        await Task.Delay(300);
        Assert.IsFalse(walking.IsCompleted, "what has no limit on it waits the hold out");
        Assert.IsFalse(patient.IsCompleted, "and so does a person's request with patience enough");
        await stop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => walking);
        await Assert.ThrowsAsync<OperationCanceledException>(() => patient);
        Assert.IsNull(UpstreamPatience.Current);
    }

    [TestMethod]
    public async Task AMailboxsHoldIsRefusedTheSameWay()
    {
        var pace = MailboxPace.For($"patience-{Guid.NewGuid():N}@example.com");
        pace.Hold(TimeSpan.FromSeconds(30));
        var refused = await AsAPersonAsync(TimeSpan.FromSeconds(5), () => Assert.ThrowsExactlyAsync<VfsException>(() => pace.EnterAsync(CancellationToken.None).WaitAsync(Soon)));
        Assert.AreEqual(VfsError.Unavailable, refused.Error);
        StringAssert.Contains(refused.Message, "asked for no requests until");

        using var stop = new CancellationTokenSource();
        var refreshing = pace.EnterAsync(stop.Token);
        await Task.Delay(300);
        Assert.IsFalse(refreshing.IsCompleted, "a refresh waits it out");
        await stop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => refreshing);
    }
}
