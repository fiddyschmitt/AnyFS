using System.Runtime.InteropServices;
using AnyFS.Core.Model;
using AnyFS.Core.Settings;
using AnyFS.Core.Stats;
using AnyFS.Core.Status;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Core.Runtime;

/// <summary>Runs one mount of one collection. A mount never crashes the app: exceptions become Failed (spec 8.8).</summary>
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
    private ComponentStatus _status = ComponentStatus.DisabledStatus;

    public MountRunner(
        MountDefinition definition,
        IMountProviderPlugin? provider,
        object? settings,
        string? configurationError,
        ICollectionInfo collection,
        SettingsPaths paths,
        SharedResources shared,
        StatsCollector stats,
        ILogger logger)
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

    public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public async Task StartAsync(IVirtualFileSystem fs, CancellationToken ct)
    {
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

        Status = new ComponentStatus(ComponentState.Starting, "Starting");
        IMount? mount = null;
        try
        {
            mount = _provider.CreateMount(_settings ?? _provider.CreateDefaultSettings(_collection), _collection);
            var context = new MountContextImpl(this, Definition, _collection, _paths, _shared, _stats, _logger);
            await mount.StartAsync(fs, context, ct).ConfigureAwait(false);
            Mount = mount;
            if (Status.State == ComponentState.Starting)
            {
                var paths = mount.AccessPaths;
                Status = new ComponentStatus(ComponentState.Ready, paths.Count > 0 ? paths[0].Value : "Ready");
            }

            _logger.LogInformation("mount {Mount} of {Collection} ({Provider}) is up at {Paths}", Definition.Name, _collection.Name, Definition.Provider, string.Join(", ", mount.AccessPaths.Select(p => p.Value)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisposeQuietlyAsync(mount).ConfigureAwait(false);
            Status = new ComponentStatus(ComponentState.Disabled, "Stopped");
            throw;
        }
        catch (Exception ex)
        {
            await DisposeQuietlyAsync(mount).ConfigureAwait(false);
            var message = ex is VfsException ? ex.Message : ex.GetBaseException().Message;
            Status = new ComponentStatus(ComponentState.Failed, message);
            _logger.LogError(ex, "mount {Mount} of {Collection} failed to start", Definition.Name, _collection.Name);
        }
    }

    public async Task StopAsync()
    {
        var mount = Mount;
        Mount = null;
        if (mount is null)
        {
            if (Status.State != ComponentState.Failed)
            {
                Status = new ComponentStatus(ComponentState.Disabled, "Stopped");
            }

            return;
        }

        Status = new ComponentStatus(ComponentState.Stopping, "Stopping");
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
        Status = new ComponentStatus(ComponentState.Disabled, "Stopped");
    }

    internal void OnHealth(ComponentHealth health, string? detail)
    {
        Status = health switch
        {
            ComponentHealth.Degraded => new ComponentStatus(ComponentState.Degraded, detail ?? "Degraded"),
            ComponentHealth.Failed => new ComponentStatus(ComponentState.Failed, detail ?? "Failed"),
            _ => new ComponentStatus(ComponentState.Ready, detail ?? (AccessPaths.Count > 0 ? AccessPaths[0].Value : "Ready")),
        };
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

        public IMountStatsSink Stats => this;

        public void ReportHealth(ComponentHealth health, string? detail = null) => runner.OnHealth(health, detail);

        public Task<ISharedLease<T>> AcquireSharedAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CancellationToken ct)
            where T : class, IAsyncDisposable
            => shared.AcquireAsync(key, factory, ct);

        public void ConnectionOpened() => stats.ConnectionOpened(collection.Name);

        public void ConnectionClosed() => stats.ConnectionClosed(collection.Name);
    }
}
