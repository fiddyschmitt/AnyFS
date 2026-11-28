using System.Runtime.InteropServices;
using AnyFS.Core.Model;
using AnyFS.Core.Settings;
using AnyFS.Core.Stats;
using AnyFS.Core.Status;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Core.Runtime;

/// <summary>
/// Runs one mount of one collection. A mount never crashes the app: exceptions become Failed (spec 8.8). A start that
/// failed is tried again in its own time, as a source's is (spec 6.4), until the mount is up or the runner is stopped: a
/// port still held as AnyFS started again left the mount down until someone looked (the review of 21-Nov-2026, R7e).
/// </summary>
public sealed class MountRunner
{
    private readonly IMountProviderPlugin? _provider;
    private readonly object? _settings;
    private readonly string? _configurationError;
    private readonly ICollectionInfo _collection;
    private readonly SettingsPaths _paths;
    private readonly SharedResources _shared;
    private readonly StatsCollector _stats;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ComponentStatus _status = ComponentStatus.DisabledStatus;

    // the tries that follow a start that failed, and what stops them
    private CancellationTokenSource? _retrying;
    private Task? _retries;

    // what the last try that failed said, so that the log says it once and not at every try
    private string? _lastFailure;

    // the context of the try under way or of the mount it brought up, under _gate: what any other says is not heard. A mount
    // that has stopped, or a try given up, can still report, and that set the status of the runner that had moved on
    private MountContextImpl? _live;

    public MountRunner(
        MountDefinition definition,
        IMountProviderPlugin? provider,
        object? settings,
        string? configurationError,
        ICollectionInfo collection,
        SettingsPaths paths,
        SharedResources shared,
        StatsCollector stats,
        ILogger logger,
        TimeProvider? time = null)
    {
        Definition = definition;
        _provider = provider;
        _settings = settings;
        _configurationError = configurationError;
        _collection = collection;
        _paths = paths;
        _shared = shared;
        _stats = stats;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public MountDefinition Definition { get; }

    public IMountProviderPlugin? Provider => _provider;

    public IMount? Mount { get; private set; }

    public ComponentStatus Status
    {
        get => Volatile.Read(ref _status);
        private set
        {
            Volatile.Write(ref _status, value);
            Changed?.Invoke(this);
        }
    }

    public event Action<MountRunner>? Changed;

    public IReadOnlyList<AccessPath> AccessPaths => Mount?.AccessPaths ?? [];

    /// <summary>How long a mount is given to stop what is under way: its provider's token is cancelled when this has passed (spec 8.8).</summary>
    public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How much longer than <see cref="StopTimeout"/> a stop is waited for. No provider is trusted to read the token it is
    /// given (none of the built-in ones did): past this the runner goes on, and the provider's stop ends in its own time.
    /// </summary>
    public TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long after a start that failed the next is tried, by how many have failed: a source's schedule (spec 6.4). Overridable for tests.</summary>
    public Func<int, TimeSpan> RetryDelay { get; set; } = RetryPolicy.Delay;

    /// <summary>
    /// For a runner put in the place of one that is up: "Stopping" until its own start has its turn, which is when the one
    /// before it has stopped. A new runner says "Disabled", which someone who has just edited the mount would take for
    /// what came of it.
    /// </summary>
    public void TakesOver() => Status = new ComponentStatus(ComponentState.Stopping, "Stopping");

    public async Task StartAsync(IVirtualFileSystem fs, CancellationToken ct)
    {
        // Up already, or being tried. The collection's start and a change may both come to start a mount: one added while
        // its collection still waited for a first source was started by each, and the first of the two was left up with no
        // one to stop it
        lock (_gate)
        {
            if (Mount is not null || _retries is not null)
            {
                return;
            }
        }

        if (!Definition.Enabled)
        {
            Status = ComponentStatus.DisabledStatus;
            return;
        }

        if (_provider is null)
        {
            Status = new ComponentStatus(ComponentState.Failed, $"Mount provider '{Definition.Provider}' is not available in this build");
            _logger.LogError("mount {Mount} of {Collection}: provider '{Provider}' is not available", Definition.Name, _collection.Name, Definition.Provider);
            return;
        }

        if (!_provider.SupportedPlatforms.Any(RuntimeInformation.IsOSPlatform))
        {
            Status = new ComponentStatus(ComponentState.Failed, $"{_provider.DisplayName} is not available on this operating system");
            return;
        }

        if (_provider.UnavailableReason is { } reason)
        {
            Status = new ComponentStatus(ComponentState.Failed, reason);
            _logger.LogWarning("mount {Mount} of {Collection}: {Reason}", Definition.Name, _collection.Name, reason);
            return;
        }

        if (_configurationError is not null)
        {
            Status = new ComponentStatus(ComponentState.Failed, _configurationError);
            return;
        }

        _lastFailure = null;
        if (await TryStartAsync(fs, 0, ct).ConfigureAwait(false))
        {
            return;
        }

        lock (_gate)
        {
            _retrying = new CancellationTokenSource();
            var stopped = _retrying.Token;
            _retries = Task.Run(() => RetryAsync(fs, stopped), CancellationToken.None);
        }
    }

    /// <summary>The tries after the first, each after its wait, until one comes up or the runner is stopped.</summary>
    private async Task RetryAsync(IVirtualFileSystem fs, CancellationToken stopped)
    {
        for (var attempt = 1; ; attempt++)
        {
            await Task.Delay(RetryDelay(attempt - 1), _time, stopped).ConfigureAwait(false);
            if (await TryStartAsync(fs, attempt, stopped).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>One try: true when the mount is up. A try that failed says so, with when the next one is.</summary>
    private async Task<bool> TryStartAsync(IVirtualFileSystem fs, int attempt, CancellationToken ct)
    {
        Status = new ComponentStatus(ComponentState.Starting, attempt == 0 ? "Starting" : $"Retrying ({attempt})");
        IMount? mount = null;
        MountContextImpl? context = null;
        var up = false;
        try
        {
            mount = _provider!.CreateMount(_settings ?? _provider.CreateDefaultSettings(_collection), _collection);
            context = new MountContextImpl(this, Definition, _collection, _paths, _shared, _stats, _logger);
            lock (_gate)
            {
                _live = context;
            }

            await mount.StartAsync(fs, context, ct).ConfigureAwait(false);
            up = true;

            // one that came up after it was told to stop, its provider not having looked at the token, is not kept
            ct.ThrowIfCancellationRequested();
            Mount = mount;
            if (Status.State == ComponentState.Starting)
            {
                var paths = mount.AccessPaths;
                Status = new ComponentStatus(ComponentState.Ready, paths.Count > 0 ? paths[0].Value : "Ready");
            }

            _logger.LogInformation("mount {Mount} of {Collection} ({Provider}) is up at {Paths}", Definition.Name, _collection.Name, Definition.Provider, string.Join(", ", mount.AccessPaths.Select(p => p.Value)));
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Retire(context);
            await (up ? StopAndDisposeAsync(mount!) : DisposeQuietlyAsync(mount)).ConfigureAwait(false);
            Status = new ComponentStatus(ComponentState.Disabled, "Stopped");
            throw;
        }
        catch (Exception ex)
        {
            Retire(context);
            await DisposeQuietlyAsync(mount).ConfigureAwait(false);
            var message = ex is VfsException ? ex.Message : ex.GetBaseException().Message;
            var delay = RetryDelay(attempt);
            Status = new ComponentStatus(ComponentState.Failed, $"{message} (retrying in {RetryPolicy.Describe(delay)})");

            // said once: a mount whose settings are wrong fails the same way every five minutes until they are edited
            if (attempt == 0)
            {
                _logger.LogError(ex, "mount {Mount} of {Collection} failed to start; it is tried again in {Delay}, and from then on by a source's schedule", Definition.Name, _collection.Name, RetryPolicy.Describe(delay));
            }
            else
            {
                _logger.Log(message == _lastFailure ? LogLevel.Debug : LogLevel.Warning, "mount {Mount} of {Collection} failed to start again (try {Attempt}): {Error}; it is tried again in {Delay}", Definition.Name, _collection.Name, attempt + 1, message, RetryPolicy.Describe(delay));
            }

            _lastFailure = message;
            return false;
        }
    }

    public async Task StopAsync()
    {
        // the tries first: none may bring the mount up behind this stop
        CancellationTokenSource? retrying;
        Task? retries;
        lock (_gate)
        {
            retrying = _retrying;
            retries = _retries;
            _retrying = null;
            _retries = null;

            // what the mount says as it stops is not its status: the runner says Stopping, then Stopped
            _live = null;
        }

        if (retrying is not null)
        {
            await retrying.CancelAsync().ConfigureAwait(false);
            if (!await EndsInTimeAsync(retries!).ConfigureAwait(false))
            {
                _logger.LogWarning("mount {Mount} of {Collection}: a start under way has not ended {Seconds:0.#} s after it was told to stop, and is waited for no longer; what it brings up is stopped again", Definition.Name, _collection.Name, (StopTimeout + StopGrace).TotalSeconds);
            }
        }

        var mount = Mount;
        Mount = null;
        if (mount is null)
        {
            // one that was being tried again is tried no more; one that could not be tried at all goes on saying why
            if (retrying is not null || Status.State != ComponentState.Failed)
            {
                Status = new ComponentStatus(ComponentState.Disabled, "Stopped");
            }

            return;
        }

        Status = new ComponentStatus(ComponentState.Stopping, "Stopping");
        var stopping = StopAndDisposeAsync(mount);
        if (!await EndsInTimeAsync(stopping).ConfigureAwait(false))
        {
            // A stop took as long as it took: a mapped drive's removal a minute, and a window closing gave the mounts the time
            // its sources needed to keep what they keep (the review of 21-Nov-2026, R7e)
            var since = _time.GetTimestamp();
            _logger.LogWarning("mount {Mount} of {Collection}: has not stopped after {Seconds:0.#} s and is waited for no longer. What it holds (a port, a drive, a folder) it holds until it has", Definition.Name, _collection.Name, (StopTimeout + StopGrace).TotalSeconds);
            _ = stopping.ContinueWith(
                _ => _logger.LogInformation("mount {Mount} of {Collection}: stopped, {Seconds:0.#} s after it was no longer waited for", Definition.Name, _collection.Name, _time.GetElapsedTime(since).TotalSeconds),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        Status = new ComponentStatus(ComponentState.Disabled, "Stopped");
    }

    /// <summary>The provider's stop, told to give up what is under way after <see cref="StopTimeout"/>, and its dispose. Nothing is thrown.</summary>
    private async Task StopAndDisposeAsync(IMount mount)
    {
        try
        {
            using var cts = new CancellationTokenSource(StopTimeout);
            await mount.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mount {Mount} of {Collection}: stop did not finish cleanly", Definition.Name, _collection.Name);
        }

        await DisposeQuietlyAsync(mount).ConfigureAwait(false);
    }

    /// <summary>Waits for something of this mount to end, for as long as a stop may take; false when it has not ended by then.</summary>
    private async Task<bool> EndsInTimeAsync(Task work)
    {
        try
        {
            await work.WaitAsync(StopTimeout + StopGrace, _time).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!work.IsCompleted)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            // the tries, told to stop
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mount {Mount} of {Collection}: what was stopped ended with a failure", Definition.Name, _collection.Name);
        }

        return true;
    }

    private void OnHealth(MountContextImpl from, ComponentHealth health, string? detail)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(from, _live))
            {
                return;
            }
        }

        Status = health switch
        {
            ComponentHealth.Degraded => new ComponentStatus(ComponentState.Degraded, detail ?? "Degraded"),
            ComponentHealth.Failed => new ComponentStatus(ComponentState.Failed, detail ?? "Failed"),
            _ => new ComponentStatus(ComponentState.Ready, detail ?? (AccessPaths.Count > 0 ? AccessPaths[0].Value : "Ready")),
        };
    }

    /// <summary>A try that failed, or was stopped: what its context says from now on is not heard.</summary>
    private void Retire(MountContextImpl? context)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_live, context))
            {
                _live = null;
            }
        }
    }

    private async Task DisposeQuietlyAsync(IMount? mount)
    {
        if (mount is null)
        {
            return;
        }

        try
        {
            await mount.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mount {Mount}: dispose failed", Definition.Name);
        }
    }

    private sealed class MountContextImpl(MountRunner runner, MountDefinition definition, ICollectionInfo collection, SettingsPaths paths, SharedResources shared, StatsCollector stats, ILogger logger) : IMountContext, IMountStatsSink
    {
        public ILogger Logger => logger;

        public Guid MountId => definition.Id;

        public string MountName => definition.Name;

        public ICollectionInfo Collection => collection;

        public string StateDirectory => Directory.CreateDirectory(paths.MountState(definition.Id)).FullName;

        public string ProviderCacheDirectory => Directory.CreateDirectory(paths.MountProviderCache(definition.Provider)).FullName;

        public IMountStatsSink Stats => this;

        public void ReportHealth(ComponentHealth health, string? detail = null) => runner.OnHealth(this, health, detail);

        public Task<ISharedLease<T>> AcquireSharedAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CancellationToken ct)
            where T : class, IAsyncDisposable
            => shared.AcquireAsync(key, factory, ct);

        public void ConnectionOpened() => stats.ConnectionOpened(collection.Name);

        public void ConnectionClosed() => stats.ConnectionClosed(collection.Name);
    }
}
