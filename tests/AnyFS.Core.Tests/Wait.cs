namespace AnyFS.Core.Tests;

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail($"timed out waiting for {what ?? "condition"}");
            }

            await Task.Delay(10);
        }
    }
}
