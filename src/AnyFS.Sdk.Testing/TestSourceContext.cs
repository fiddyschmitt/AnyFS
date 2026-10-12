using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sdk.Testing;

/// <summary>
/// An <see cref="ISourceContext"/> for tests: temp directories, in-memory secrets, recorded progress and
/// health, and prompts answered by a function you supply (unanswered by default).
/// </summary>
public sealed class TestSourceContext : ISourceContext, IDisposable
{
    private readonly string _root;
    private readonly Dictionary<string, string?> _secrets = new(StringComparer.Ordinal);

    public TestSourceContext(string? sourceName = null, ILogger? logger = null)
    {
        SourceName = sourceName ?? "test";
        Logger = logger ?? NullLogger.Instance;
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", Guid.NewGuid().ToString("N"));
        Secrets = new MemorySecretStore(_secrets);
    }

    public ILogger Logger { get; }

    public Guid SourceId { get; } = Guid.NewGuid();

    public string SourceName { get; }

    public string CacheDirectory => Directory.CreateDirectory(Path.Combine(_root, "cache")).FullName;

    public string StateDirectory => Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;

    public ISecretStore Secrets { get; }

    public List<(string Message, double? Fraction)> Progress { get; } = [];

    public List<(ComponentHealth Health, string? Detail)> HealthReports { get; } = [];

    public ComponentHealth? LastHealth => HealthReports.Count == 0 ? null : HealthReports[^1].Health;

    /// <summary>Answers prompts; by default every prompt comes back unanswered.</summary>
    public Func<PromptRequest, PromptResult> PromptHandler { get; set; } = _ => PromptResult.Cancelled;

    public List<PromptRequest> Prompts { get; } = [];

    public void ReportProgress(string message, double? fraction = null) => Progress.Add((message, fraction));

    public void ReportHealth(ComponentHealth health, string? detail = null) => HealthReports.Add((health, detail));

    public Task<PromptResult> PromptAsync(PromptRequest request, CancellationToken ct)
    {
        Prompts.Add(request);
        return Task.FromResult(PromptHandler(request));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class MemorySecretStore(Dictionary<string, string?> store) : ISecretStore
    {
        public string? Get(string key) => store.TryGetValue(key, out var v) ? v : null;

        public void Set(string key, string? value) => store[key] = value;
    }
}
