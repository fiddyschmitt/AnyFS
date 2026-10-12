using System.Diagnostics;
using System.Reflection;
using AnyFS.Core.Model;
using AnyFS.Core.Plugins;
using AnyFS.Core.Settings;
using AnyFS.Core.Stats;
using AnyFS.Core.Status;
using AnyFS.Core.Vfs;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Core.Runtime;

/// <summary>
/// The whole thing in process: settings, secrets, stats, plugin registry, one runner per source, one
/// runner per collection. <c>anyfs serve</c> and the GUI both drive this.
/// </summary>
public sealed class AnyFsEngine : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly IPromptHandler _prompts;
    private readonly TimeProvider _time;
    private readonly List<SourceRunner> _sources = [];
    private readonly List<CollectionRunner> _collections = [];
    private SecretStore? _secrets;
    private bool _started;

    public AnyFsEngine(SettingsPaths paths, PluginRegistry registry, ILoggerFactory loggerFactory, IPromptHandler? prompts = null, TimeProvider? time = null)
    {
        Paths = paths;
        Registry = registry;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("AnyFS.Engine");
        _prompts = new SerialisingPromptHandler(prompts ?? NoPromptHandler.Instance);
        _time = time ?? TimeProvider.System;
        Settings = new SettingsStore(paths, loggerFactory.CreateLogger("AnyFS.Settings"));
        Stats = new StatsCollector(_time);
        Shared = new SharedResources();
        StartedAt = _time.GetUtcNow();
    }

    public SettingsPaths Paths { get; }

    public PluginRegistry Registry { get; }

    public SettingsStore Settings { get; }

    public StatsCollector Stats { get; }

    public SharedResources Shared { get; }

    public SecretStore Secrets => _secrets ??= new SecretStore(Paths, _loggerFactory.CreateLogger("AnyFS.Secrets"));

    public IReadOnlyList<SourceRunner> Sources => _sources;

    public IReadOnlyList<CollectionRunner> Collections => _collections;

    public DateTimeOffset StartedAt { get; }

    public static string Version { get; } = typeof(AnyFsEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AnyFsEngine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Settings, Collection 1 on a first start, lifetime stats.</summary>
    public void Load()
    {
        Settings.Load();
        if (Settings.EnsureDefaultCollection(Registry))
        {
            _logger.LogInformation("first start: created 'Collection 1' with a {Provider} mount in {Dir}", SettingsStore.NativeProviderForThisOs(), Paths.Settings);
        }

        try
        {
            Stats.LoadLifetime(JsonFiles.Read(Paths.StatsFile, CoreJsonContext.Default.StatsFile));
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "stats.json could not be read; lifetime totals start again");
            Stats.LoadLifetime(null);
        }
    }

    /// <summary>Starts sources, then collections (whose mounts wait for a usable source). <paramref name="collectionNames"/> null means every enabled collection.</summary>
    public async Task StartAsync(IReadOnlyCollection<string>? collectionNames, CancellationToken ct)
    {
        if (_started)
        {
            throw new InvalidOperationException("already started");
        }

        _started = true;
        var selected = SelectCollections(collectionNames);
        var handles = new Dictionary<Guid, SourceHandle>();
        var neededSources = collectionNames is null
            ? Settings.Sources.Select(s => s.Id).ToHashSet()
            : selected.Where(c => c.Enabled).SelectMany(c => c.Sources.Select(m => m.Id)).ToHashSet();

        foreach (var definition in Settings.Sources.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var handle = new SourceHandle(definition.Id, definition.Name, definition.Type);
            handles[definition.Id] = handle;
            var plugin = Registry.FindSource(definition.Type);
            object? settings = null;
            string? error = null;
            if (plugin is not null)
            {
                try
                {
                    settings = PluginSettingsBinder.Materialise(plugin.SettingsType, definition.Settings, name => Secrets.Get(definition.Id, name));
                }
                catch (Exception ex)
                {
                    error = $"Settings could not be read: {ex.GetBaseException().Message}";
                }
            }

            var runner = new SourceRunner(definition, plugin, settings, error, handle, Paths, _secrets ?? (plugin is null ? null : Secrets), _prompts, _loggerFactory.CreateLogger($"AnyFS.Source.{definition.Name}"), _time);
            _sources.Add(runner);
            if (neededSources.Contains(definition.Id))
            {
                runner.Start();
            }
            else
            {
                handle.Status = new ComponentStatus(ComponentState.Disabled, "Not in a started collection");
            }
        }

        foreach (var definition in selected)
        {
            var vfs = new CollectionVfs(definition.Id, definition.Name, Stats, _loggerFactory.CreateLogger($"AnyFS.Collection.{definition.Name}"));
            var members = definition.Sources
                .Where(m => handles.ContainsKey(m.Id))
                .Select(m => new CollectionSource(handles[m.Id], m, Settings.FindSource(m.Id)!))
                .ToList();
            var info = new CollectionInfo(definition.Id, definition.Name);
            var mounts = new List<MountRunner>();
            foreach (var mountDefinition in definition.Mounts)
            {
                var provider = Registry.FindMountProvider(mountDefinition.Provider);
                object? settings = null;
                string? error = null;
                if (provider is not null)
                {
                    try
                    {
                        settings = PluginSettingsBinder.Materialise(provider.SettingsType, mountDefinition.Settings, name => Secrets.Get(mountDefinition.Id, name));
                    }
                    catch (Exception ex)
                    {
                        error = $"Settings could not be read: {ex.GetBaseException().Message}";
                    }
                }

                mounts.Add(new MountRunner(mountDefinition, provider, settings, error, info, Paths, Shared, Stats, _loggerFactory.CreateLogger($"AnyFS.Mount.{definition.Name}.{mountDefinition.Name}")));
            }

            _collections.Add(new CollectionRunner(definition, vfs, members, mounts, _loggerFactory.CreateLogger($"AnyFS.Collection.{definition.Name}"), _time));
        }

        await Task.WhenAll(_collections.Select(c => c.StartAsync(ct))).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        foreach (var collection in _collections)
        {
            try
            {
                await collection.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "collection {Collection}: stop failed", collection.Name);
            }
        }

        foreach (var source in _sources)
        {
            try
            {
                await source.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "source {Source}: stop failed", source.Definition.Name);
            }
        }

        FlushStats();
        try
        {
            File.Delete(Paths.StatusFile);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Start, keep status.json and stats.json fresh, stop on cancellation.</summary>
    public async Task RunAsync(IReadOnlyCollection<string>? collectionNames, CancellationToken ct)
    {
        await StartAsync(collectionNames, ct).ConfigureAwait(false);
        WriteStatus();
        var lastFlush = _time.GetTimestamp();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _time, ct).ConfigureAwait(false);
                WriteStatus();
                if (_time.GetElapsedTime(lastFlush) >= TimeSpan.FromSeconds(30))
                {
                    FlushStats();
                    lastFlush = _time.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    public void FlushStats()
    {
        try
        {
            JsonFiles.WriteAtomic(Paths.StatsFile, Stats.ToFile(), CoreJsonContext.Default.StatsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "stats.json could not be written");
        }
    }

    public void WriteStatus()
    {
        try
        {
            JsonFiles.WriteAtomic(Paths.StatusFile, Snapshot(), CoreJsonContext.Default.StatusSnapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "status.json could not be written");
        }
    }

    public StatusSnapshot Snapshot()
    {
        var snapshot = new StatusSnapshot
        {
            Pid = Environment.ProcessId,
            Version = Version,
            StartedAt = StartedAt,
            UpdatedAt = _time.GetUtcNow(),
            SettingsDirectory = Paths.Settings,
            Stats = Stats.Snapshot(),
        };
        foreach (var plugin in Registry.Plugins)
        {
            snapshot.Plugins.Add(new PluginStatus { TypeId = plugin.TypeId, Kind = plugin.Kind, DisplayName = plugin.DisplayName, Origin = plugin.Origin, Error = plugin.Error });
        }

        foreach (var collection in _collections)
        {
            var (colour, text) = collection.Status;
            var status = new CollectionStatus { Id = collection.Id, Name = collection.Name, Enabled = collection.Definition.Enabled, Colour = colour.ToString(), Text = text };
            foreach (var source in collection.Sources.OrderBy(s => s.Definition.Name, StringComparer.OrdinalIgnoreCase))
            {
                var s = source.Handle.EffectiveStatus;
                var (files, complete) = source.Handle.FileCount();
                status.Sources.Add(new SourceStatus
                {
                    Id = source.Definition.Id,
                    Name = source.Definition.Name,
                    Type = source.Definition.Type,
                    Path = source.Membership.Path,
                    ReadOnly = source.Membership.ReadOnly,
                    Enabled = source.Definition.Enabled,
                    State = s.State.ToString(),
                    Text = s.Text,
                    Progress = s.Progress,
                    Files = files,
                    FilesComplete = complete,
                    Counting = s.State is ComponentState.Starting or ComponentState.Enumerating,
                });
            }

            foreach (var mount in collection.Mounts.OrderBy(m => m.Definition.Name, StringComparer.OrdinalIgnoreCase))
            {
                status.Mounts.Add(new MountStatus
                {
                    Id = mount.Definition.Id,
                    Name = mount.Definition.Name,
                    Provider = mount.Definition.Provider,
                    Enabled = mount.Definition.Enabled,
                    ReadOnly = mount.Definition.ReadOnly,
                    State = mount.Status.State.ToString(),
                    Text = mount.Status.Text,
                    AccessPaths = mount.AccessPaths.Select(p => new AccessPathStatus { Label = p.Label, Value = p.Value }).ToList(),
                });
            }

            snapshot.Collections.Add(status);
        }

        return snapshot;
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    private List<CollectionDefinition> SelectCollections(IReadOnlyCollection<string>? names)
    {
        if (names is null || names.Count == 0)
        {
            return Settings.Collections.ToList();
        }

        var selected = new List<CollectionDefinition>();
        foreach (var name in names)
        {
            var collection = Settings.FindCollection(name) ?? throw new ArgumentException($"there is no collection named '{name}'");
            if (!selected.Contains(collection))
            {
                selected.Add(collection);
            }
        }

        return selected;
    }

    private sealed record CollectionInfo(Guid Id, string Name) : ICollectionInfo;
}

/// <summary>Reads what a running <c>anyfs serve</c> left in state/status.json.</summary>
public static class StatusFileReader
{
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(10);

    /// <summary>The snapshot and whether it's fresh enough to mean "running now".</summary>
    public static (StatusSnapshot? Snapshot, bool Running) Read(SettingsPaths paths, TimeProvider? time = null)
    {
        StatusSnapshot? snapshot;
        try
        {
            snapshot = JsonFiles.Read(paths.StatusFile, CoreJsonContext.Default.StatusSnapshot);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, false);
        }

        if (snapshot is null)
        {
            return (null, false);
        }

        var now = (time ?? TimeProvider.System).GetUtcNow();
        var fresh = now - snapshot.UpdatedAt < Freshness;
        var alive = fresh && IsProcessAlive(snapshot.Pid);
        return (snapshot, alive);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
