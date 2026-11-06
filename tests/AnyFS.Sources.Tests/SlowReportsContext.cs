using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A <see cref="TestSourceContext"/> whose progress reports take a while, some longer than others, as a busy host's can:
/// reports made from several tasks at once come through in no set order, so a source that reports from several must keep
/// them in the order of its count itself. They are recorded in <see cref="Inner"/>.
/// </summary>
internal sealed class SlowReportsContext(TestSourceContext inner) : ISourceContext
{
    public TestSourceContext Inner => inner;

    public ILogger Logger => inner.Logger;

    public Guid SourceId => inner.SourceId;

    public string SourceName => inner.SourceName;

    public string CacheDirectory => inner.CacheDirectory;

    public string StateDirectory => inner.StateDirectory;

    public ISecretStore Secrets => inner.Secrets;

    public IUpstreamWatcher? UpstreamWatcher => inner.UpstreamWatcher;

    public void ReportProgress(string message, double? fraction = null)
    {
        Thread.Sleep(Random.Shared.Next(0, 4));
        inner.ReportProgress(message, fraction);
    }

    public void ReportHealth(ComponentHealth health, string? detail = null) => inner.ReportHealth(health, detail);

    public Task<PromptResult> PromptAsync(PromptRequest request, CancellationToken ct) => inner.PromptAsync(request, ct);

    /// <summary>What was said whose message starts with <paramref name="prefix"/>, in the order it was recorded.</summary>
    public List<(string Message, double? Fraction)> Said(string prefix)
    {
        lock (inner.Progress)
        {
            return [.. inner.Progress.Where(p => p.Message.StartsWith(prefix, StringComparison.Ordinal))];
        }
    }
}
