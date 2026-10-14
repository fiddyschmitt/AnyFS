using AnyFS.Core.Model;
using AnyFS.Core.Settings;
using AnyFS.Core.Status;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Core.Runtime;

/// <summary>Spec 6.4: 10 s, 30 s, 1 min, 5 min, then every 5 min.</summary>
public static class RetryPolicy
{
    private static readonly TimeSpan[] Schedule =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
    ];

    public static TimeSpan Delay(int attempt) => attempt < Schedule.Length ? Schedule[attempt] : Schedule[^1];

    public static string Describe(TimeSpan delay)
        => delay.TotalMinutes >= 1 ? $"{delay.TotalMinutes:0} min" : $"{delay.TotalSeconds:0} s";
}

/// <summary>
/// Runs one configured source: creates it from its settings, initialises it on the thread pool with a
/// cancellation token tied to disable/delete/exit, retries failures with backoff unless they're
/// permanent, applies the source's progress and health reports to its status, and optionally walks
/// the tree to count files at start.
/// </summary>
public sealed class SourceRunner : IAsyncDisposable
{
    private readonly ISourcePlugin? _plugin;
    private readonly object? _settings;
    private readonly string? _configurationError;
    private readonly SettingsPaths _paths;
    private readonly SecretStore? _secrets;
    private readonly IPromptHandler _prompts;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ComponentHealth? _pendingHealth;
    private string? _pendingHealthDetail;

    public SourceRunner(
        SourceDefinition definition,
        ISourcePlugin? plugin,
        object? settings,
        string? configurationError,
        SourceHandle handle,
        SettingsPaths paths,
        SecretStore? secrets,
        IPromptHandler prompts,
        ILogger logger,
        TimeProvider? time = null)
    {
        Definition = definition;
        _plugin = plugin;
        _settings = settings;
        _configurationError = configurationError;
        Handle = handle;
        _paths = paths;
        _secrets = secrets;
        _prompts = prompts;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public SourceDefinition Definition { get; }

    public SourceHandle Handle { get; }

    public ISourcePlugin? Plugin => _plugin;

    /// <summary>Overridable for tests.</summary>
    public Func<int, TimeSpan> RetryDelay { get; set; } = RetryPolicy.Delay;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _cts is not null;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                return;
            }

            if (!Definition.Enabled)
            {
                Handle.Status = ComponentStatus.DisabledStatus;
                return;
            }

            if (_plugin is null)
            {
                Handle.Status = new ComponentStatus(ComponentState.Failed, $"Unknown source type '{Definition.Type}'");
                _logger.LogError("source {Source}: unknown source type '{Type}'", Definition.Name, Definition.Type);
                return;
            }

            if (_configurationError is not null)
            {
                Handle.Status = new ComponentStatus(ComponentState.Failed, _configurationError);
                _logger.LogError("source {Source}: {Error}", Definition.Name, _configurationError);
                return;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    /// <summary>Toggling Enabled or the Retry menu item: start again at once, forgetting the backoff.</summary>
    public async Task RetryNowAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Start();
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is null)
        {
            return;
        }

        if (Handle.Status.State != ComponentState.Failed)
        {
            Handle.Status = new ComponentStatus(ComponentState.Stopping, "Stopping");
        }

        await cts.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await DisposeCurrentAsync().ConfigureAwait(false);
        cts.Dispose();
        Handle.Status = new ComponentStatus(ComponentState.Disabled, "Stopped");
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    internal void OnProgress(string message, double? fraction)
    {
        if (Handle.Status.State == ComponentState.Starting)
        {
            Handle.Status = new ComponentStatus(ComponentState.Starting, message, fraction);
        }
    }

    internal void OnHealth(ComponentHealth health, string? detail)
    {
        if (Handle.Current is null)
        {
            _pendingHealth = health;
            _pendingHealthDetail = detail;
            return;
        }

        Handle.Status = StatusFor(health, detail);
    }

    private static ComponentStatus StatusFor(ComponentHealth health, string? detail) => health switch
    {
        ComponentHealth.Degraded => new ComponentStatus(ComponentState.Degraded, detail ?? "Degraded"),
        ComponentHealth.Failed => new ComponentStatus(ComponentState.Failed, detail ?? "Failed"),
        _ => new ComponentStatus(ComponentState.Ready, detail ?? "Ready"),
    };

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Handle.Status = new ComponentStatus(ComponentState.Starting, attempt == 0 ? "Starting" : $"Retrying ({attempt})");
            Handle.ForgetListings();
            _pendingHealth = null;
            ISource? source = null;
            try
            {
                var context = new SourceContextImpl(this, Definition, _paths, _secrets, _prompts, _logger);
                source = _plugin!.CreateSource(_settings ?? _plugin.CreateDefaultSettings());
                await source.InitialiseAsync(context, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                Handle.Current = source;
                Handle.Status = _pendingHealth is { } pending ? StatusFor(pending, _pendingHealthDetail) : ComponentStatus.ReadyStatus;
                _logger.LogInformation("source {Source} ({Type}) is {State}", Definition.Name, Definition.Type, Handle.Status.State.ToString().ToLowerInvariant());
                if (Definition.CountFilesOnStart && source.Counters.State != EnumerationState.Complete)
                {
                    await CountAsync(source, ct).ConfigureAwait(false);
                }

                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Handle.Current = null;
                await DisposeQuietlyAsync(source).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                Handle.Current = null;
                await DisposeQuietlyAsync(source).ConfigureAwait(false);
                var message = ex is VfsException or SourceFailedException ? ex.Message : ex.GetBaseException().Message;
                if (ex is SourceFailedException { Permanent: true })
                {
                    Handle.Status = new ComponentStatus(ComponentState.Failed, message);
                    _logger.LogError("source {Source} failed: {Error}", Definition.Name, message);
                    return;
                }

                var delay = RetryDelay(attempt);
                Handle.Status = new ComponentStatus(ComponentState.Failed, $"{message} (retrying in {RetryPolicy.Describe(delay)})");
                _logger.LogWarning(ex, "source {Source} failed to start (attempt {Attempt}); retrying in {Delay}", Definition.Name, attempt + 1, RetryPolicy.Describe(delay));
                attempt++;
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The opt-in "Count files on start" walk (spec 6.8), with the spinner on: the source's own
    /// <see cref="ICountable.CountFilesAsync"/> when it has one, else a directory-by-directory walk with no pause between folders.
    /// </summary>
    private async Task CountAsync(ISource source, CancellationToken ct)
    {
        var healthy = Handle.Status;
        Handle.Status = new ComponentStatus(ComponentState.Enumerating, "Counting files");
        source.Counters.SetState(EnumerationState.InProgress);
        try
        {
            if (source is ICountable countable)
            {
                await CountViaSourceAsync(source, countable, ct).ConfigureAwait(false);
            }
            else
            {
                await WalkAsync(source, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (source.Counters.State != EnumerationState.Complete)
            {
                source.Counters.SetState(EnumerationState.Unknown);
            }

            if (Handle.Status.State == ComponentState.Enumerating)
            {
                Handle.Status = healthy;
            }
        }
    }

    /// <summary>The status stays "Counting files" throughout: the number itself is the source's counters, which the lists show under it.</summary>
    private static async Task CountViaSourceAsync(ISource source, ICountable countable, CancellationToken ct)
    {
        await countable.CountFilesAsync(ct).ConfigureAwait(false);
        source.Counters.SetState(EnumerationState.Complete);
    }

    private async Task WalkAsync(ISource source, CancellationToken ct)
    {
        long files = 0, directories = 0;
        var queue = new Queue<string>();
        queue.Enqueue("");
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = queue.Dequeue();
            try
            {
                await foreach (var entry in source.ListAsync(dir, ct).ConfigureAwait(false))
                {
                    if (entry.IsDirectory)
                    {
                        directories++;
                        queue.Enqueue(VfsPath.Join(dir, entry.Name));
                    }
                    else
                    {
                        files++;
                    }
                }
            }
            catch (VfsException ex)
            {
                _logger.LogDebug("source {Source}: count skipped '{Dir}': {Error}", Definition.Name, dir, ex.Message);
            }

            source.Counters.Set(files, directories, EnumerationState.InProgress);
        }

        source.Counters.Set(files, directories, EnumerationState.Complete);
    }

    private async Task DisposeCurrentAsync()
    {
        var current = Handle.Current;
        Handle.Current = null;
        await DisposeQuietlyAsync(current).ConfigureAwait(false);
    }

    private async Task DisposeQuietlyAsync(ISource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "source {Source}: dispose failed", Definition.Name);
        }
    }

    private sealed class SourceContextImpl : ISourceContext
    {
        private readonly SourceRunner _runner;
        private readonly SourceDefinition _definition;
        private readonly SettingsPaths _paths;
        private readonly IPromptHandler _prompts;
        private readonly Lazy<ISecretStore> _secrets;

        public SourceContextImpl(SourceRunner runner, SourceDefinition definition, SettingsPaths paths, SecretStore? secrets, IPromptHandler prompts, ILogger logger)
        {
            _runner = runner;
            _definition = definition;
            _paths = paths;
            _prompts = prompts;
            Logger = logger;
            _secrets = new Lazy<ISecretStore>(() => secrets?.For(definition.Id) ?? new VolatileSecrets());
        }

        public ILogger Logger { get; }

        public Guid SourceId => _definition.Id;

        public string SourceName => _definition.Name;

        public string CacheDirectory => Directory.CreateDirectory(_paths.SourceCache(_definition.Id)).FullName;

        public string StateDirectory => Directory.CreateDirectory(_paths.SourceState(_definition.Id)).FullName;

        public ISecretStore Secrets => _secrets.Value;

        public void ReportProgress(string message, double? fraction = null) => _runner.OnProgress(message, fraction);

        public void ReportHealth(ComponentHealth health, string? detail = null) => _runner.OnHealth(health, detail);

        public Task<PromptResult> PromptAsync(PromptRequest request, CancellationToken ct) => _prompts.PromptAsync(_definition.Name, request, ct);

        private sealed class VolatileSecrets : ISecretStore
        {
            private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

            public string? Get(string key) => _values.GetValueOrDefault(key);

            public void Set(string key, string? value) => _values[key] = value;
        }
    }
}
