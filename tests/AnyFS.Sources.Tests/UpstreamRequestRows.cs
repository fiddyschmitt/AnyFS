using AnyFS.Sdk.Testing;

namespace AnyFS.Sources.Tests;

/// <summary>What the sources' tests check of the rows their requests make: that each ends, whichever way the source read its answer.</summary>
internal static class UpstreamRequestRows
{
    /// <summary>Waits a moment for what is still on its way behind the source's answers, and fails on what never ends: such a row would say Running for good.</summary>
    public static async Task AllEndAsync(TestSourceContext context)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (context.UpstreamRequests.Running.Count > 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        Assert.AreEqual(0, context.UpstreamRequests.Running.Count, string.Join("; ", context.UpstreamRequests.Running.Select(r => r.ToString())));
    }

    /// <summary>The requests whose address has this in it.</summary>
    public static List<RecordingUpstreamWatcher.Request> To(TestSourceContext context, string part)
        => context.UpstreamRequests.Requests.Where(r => r.Target.Contains(part, StringComparison.Ordinal)).ToList();
}
