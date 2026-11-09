using AnyFS.Sdk.Testing;

namespace AnyFS.Sources.Tests;

/// <summary>What the sources' tests check of the rows their requests make: that each ends, whichever way the source read its answer.</summary>
internal static class UpstreamRequestRows
{
    /// <summary>
    /// Waits a moment for what is still on its way behind the source's answers, and fails on what never ends: such a row would
    /// say Running for good. The check is the last look the wait took: a row the source began after it (work of its own ahead
    /// of what was asked, as Outlook's next batch of items) was failed by a second look (9-Nov-2026, CI on Windows: one
    /// "Read item" begun in between, the test 12 ms long).
    /// </summary>
    public static async Task AllEndAsync(TestSourceContext context)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        var running = context.UpstreamRequests.Running;
        while (running.Count > 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
            running = context.UpstreamRequests.Running;
        }

        Assert.AreEqual(0, running.Count, string.Join("; ", running.Select(r => r.ToString())));
    }

    /// <summary>The requests whose address has this in it.</summary>
    public static List<RecordingUpstreamWatcher.Request> To(TestSourceContext context, string part)
        => context.UpstreamRequests.Requests.Where(r => r.Target.Contains(part, StringComparison.Ordinal)).ToList();
}
