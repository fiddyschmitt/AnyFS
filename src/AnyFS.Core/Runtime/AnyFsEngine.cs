using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
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
/// runner per collection. <c>anyfs serve</c> and the window both drive this. Everything can be changed
/// while it runs (the Add/Edit/Delete/Enabled controls of the window): each change is applied to the
/// running pieces and persisted to the settings files in one go, serialised so two changes never
/// interleave.
/// </summary>
public sealed class AnyFsEngine : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly IPromptHandler _prompts;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<Guid, SourceHandle> _handles = [];
    private SourceRunner[] _sources = [];
    private CollectionRunner[] _collections = [];
    private SecretStore? _secrets;
    private IReadOnlyCollection<string>? _filter;
    private bool _started;
    private bool _stopped;

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

    /// <summary>Raised (on any thread) when a status changed or something was added, edited or removed.</summary>
    public event Action? Changed;

    public SettingsPaths Paths { get; }

    public PluginRegistry Registry { get; }

    public SettingsStore Settings { get; }

    public StatsCollector Stats { get; }

    public SharedResources Shared { get; }

    public SecretStore Secrets => _secrets ??= new SecretStore(Paths, _loggerFactory.CreateLogger("AnyFS.Secrets"));

    public IReadOnlyList<SourceRunner> Sources => Volatile.Read(ref _sources);

    public IReadOnlyList<CollectionRunner> Collections => Volatile.Read(ref _collections);

    public DateTimeOffset StartedAt { get; }

    public bool IsStarted => _started;

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

        // rows of collections deleted while anyfs wasn't running (or before deletes pruned them) go
        Stats.KeepOnly(Settings.Collections.Select(c => c.Name));
    }

    /// <summary>Starts sources, then collections (whose mounts wait for a usable source). <paramref name="collectionNames"/> null means every enabled collection.</summary>
    public async Task StartAsync(IReadOnlyCollection<string>? collectionNames, CancellationToken ct)
    {
        if (_started)
        {
            throw new InvalidOperationException("already started");
        }

        _started = true;
        _filter = collectionNames is { Count: > 0 } ? collectionNames : null;
        var selected = SelectCollections(_filter);
        var needed = _filter is null
            ? Settings.Sources.Select(s => s.Id).ToHashSet()
            : selected.Where(c => c.Enabled).SelectMany(c => c.Sources.Select(m => m.Id)).ToHashSet();

        var sources = new List<SourceRunner>();
        foreach (var definition in Settings.Sources.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var runner = CreateSourceRunner(definition);
            sources.Add(runner);
            if (needed.Contains(definition.Id))
            {
                runner.Start();
            }
            else
            {
                runner.Handle.Status = new ComponentStatus(ComponentState.Disabled, "Not in a started collection");
            }
        }

        Volatile.Write(ref _sources, sources.ToArray());
        Volatile.Write(ref _collections, selected.Select(CreateCollectionRunner).ToArray());
        await Task.WhenAll(Collections.Select(c => c.StartAsync(ct))).ConfigureAwait(false);
        OnChanged();
    }

    public async Task StopAsync()
    {
        _stopped = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var collection in Collections)
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

        foreach (var source in Sources)
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

        foreach (var collection in Collections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var (colour, text) = collection.Status;
            var status = new CollectionStatus { Id = collection.Id, Name = collection.Name, Enabled = collection.Definition.Enabled, Colour = colour.ToString(), Text = text };
            foreach (var source in collection.Sources.OrderBy(s => s.Definition.Name, StringComparer.OrdinalIgnoreCase))
            {
                status.Sources.Add(SourceStatusOf(source.Handle, source.Definition, source.Membership));
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

    /// <summary>A source's status line as the lists show it, with its file count; <paramref name="membership"/> null for the All sources tab.</summary>
    public static SourceStatus SourceStatusOf(SourceHandle handle, SourceDefinition definition, SourceMembership? membership)
    {
        var s = handle.EffectiveStatus;
        var (files, complete) = handle.FileCount();
        return new SourceStatus
        {
            Id = definition.Id,
            Name = definition.Name,
            Type = definition.Type,
            Path = membership?.Path ?? "",
            ReadOnly = membership?.ReadOnly ?? false,
            Enabled = definition.Enabled,
            State = s.State.ToString(),
            Text = s.Text,
            Progress = s.Progress,
            Files = files,
            FilesComplete = complete,
            Counting = s.State is ComponentState.Starting or ComponentState.Enumerating,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_started && !_stopped)
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    // ----- lookups for the window -----

    public SourceRunner? FindSourceRunner(Guid id) => Sources.FirstOrDefault(s => s.Definition.Id == id);

    public CollectionRunner? FindCollectionRunner(Guid id) => Collections.FirstOrDefault(c => c.Id == id);

    /// <summary>The source's settings POCO with secrets filled in, for the edit dialog (a fresh default when the type is unknown or the JSON is broken).</summary>
    public object? SourceSettingsFor(SourceDefinition definition)
    {
        var plugin = Registry.FindSource(definition.Type);
        if (plugin is null)
        {
            return null;
        }

        try
        {
            return PluginSettingsBinder.Materialise(plugin.SettingsType, definition.Settings, name => Secrets.Get(definition.Id, name));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NotSupportedException)
        {
            return plugin.CreateDefaultSettings();
        }
    }

    public object? MountSettingsFor(MountDefinition definition, ICollectionInfo collection)
    {
        var provider = Registry.FindMountProvider(definition.Provider);
        if (provider is null)
        {
            return null;
        }

        try
        {
            return PluginSettingsBinder.Materialise(provider.SettingsType, definition.Settings, name => Secrets.Get(definition.Id, name));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NotSupportedException)
        {
            return provider.CreateDefaultSettings(collection);
        }
    }

    // ----- sources -----

    /// <summary>Defines a source (sources are global, spec 9.4) and starts it. Secrets go to the secret store, never to sources.json.</summary>
    public async Task<SourceDefinition> AddSourceAsync(string name, string typeId, bool enabled, bool countFilesOnStart, object settings)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            name = RequireName(name, "A source");
            RequireUniqueSourceName(name, null);
            var plugin = Registry.FindSource(typeId) ?? throw new ArgumentException($"There is no source type '{typeId}'.");
            var definition = new SourceDefinition { Name = name, Type = plugin.TypeId, Enabled = enabled, CountFilesOnStart = countFilesOnStart };
            definition.Settings = PluginSettingsBinder.Serialise(plugin.SettingsType, settings, (property, value) => Secrets.Set(definition.Id, property, value));
            Settings.Sources.Add(definition);
            Settings.SaveSources();

            if (_started)
            {
                var runner = CreateSourceRunner(definition);
                Volatile.Write(ref _sources, [.. Sources, runner]);
                StartIfNeeded(runner);
            }

            _logger.LogInformation("source {Source} ({Type}) added", name, plugin.TypeId);
            OnChanged();
            return definition;
        }
        finally
        {
            _mutations.Release();
        }
    }

    /// <summary>Edits a source wherever it's used; it restarts when its settings changed (spec 11.1).</summary>
    public async Task UpdateSourceAsync(Guid id, string name, bool enabled, bool countFilesOnStart, object settings)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var definition = Settings.FindSource(id) ?? throw new ArgumentException("That source no longer exists.");
            name = RequireName(name, "A source");
            RequireUniqueSourceName(name, id);
            var plugin = Registry.FindSource(definition.Type) ?? throw new ArgumentException($"The source type '{definition.Type}' is not available, so its settings can't be edited.");

            var secretsChanged = false;
            var json = PluginSettingsBinder.Serialise(plugin.SettingsType, settings, (property, value) =>
            {
                secretsChanged |= !string.Equals(Secrets.Get(id, property), string.IsNullOrEmpty(value) ? null : value, StringComparison.Ordinal);
                Secrets.Set(id, property, value);
            });
            var settingsChanged = secretsChanged || !JsonNode.DeepEquals(definition.Settings, json);
            var enabledChanged = definition.Enabled != enabled;
            var countChanged = definition.CountFilesOnStart != countFilesOnStart;
            var renamed = !definition.Name.Equals(name, StringComparison.Ordinal);

            definition.Name = name;
            definition.Enabled = enabled;
            definition.CountFilesOnStart = countFilesOnStart;
            definition.Settings = json;
            Settings.SaveSources();

            if (_started && FindSourceRunner(id) is { } runner)
            {
                runner.Handle.Name = name;
                if (settingsChanged || countChanged || (runner.Plugin is null && plugin is not null))
                {
                    await runner.StopAsync().ConfigureAwait(false);
                    var replacement = CreateSourceRunner(definition);
                    Volatile.Write(ref _sources, Sources.Select(s => ReferenceEquals(s, runner) ? replacement : s).ToArray());
                    StartIfNeeded(replacement);
                }
                else if (enabledChanged)
                {
                    if (enabled)
                    {
                        StartIfNeeded(runner);
                    }
                    else
                    {
                        await runner.StopAsync().ConfigureAwait(false);
                    }
                }

                if (enabledChanged || renamed)
                {
                    RefreshCollectionsUsing(id);
                }
            }

            _logger.LogInformation("source {Source} updated ({What})", name, settingsChanged ? "settings changed, restarted" : enabledChanged ? (enabled ? "enabled" : "disabled") : "no restart needed");
            OnChanged();
        }
        finally
        {
            _mutations.Release();
        }
    }

    /// <summary>The Enabled checkbox: a disabled source is absent from every tree; enabling starts it at once (spec 4.1, 6.4).</summary>
    public async Task SetSourceEnabledAsync(Guid id, bool enabled)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var definition = Settings.FindSource(id) ?? throw new ArgumentException("That source no longer exists.");
            if (definition.Enabled == enabled)
            {
                return;
            }

            definition.Enabled = enabled;
            Settings.SaveSources();
            if (_started && FindSourceRunner(id) is { } runner)
            {
                if (enabled)
                {
                    StartIfNeeded(runner);
                }
                else
                {
                    await runner.StopAsync().ConfigureAwait(false);
                }

                RefreshCollectionsUsing(id);
            }

            OnChanged();
        }
        finally
        {
            _mutations.Release();
        }
    }

    /// <summary>The Retry item: start again at once, forgetting the backoff.</summary>
    public async Task RetrySourceAsync(Guid id)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            if (FindSourceRunner(id) is { } runner && runner.Definition.Enabled)
            {
                await runner.StopAsync().ConfigureAwait(false);
                StartIfNeeded(runner);
                OnChanged();
            }
        }
        finally
        {
            _mutations.Release();
        }
    }

    /// <summary>Deletes the definition everywhere: memberships, secrets, per-source state and cache.</summary>
    public async Task DeleteSourceAsync(Guid id)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var definition = Settings.FindSource(id) ?? throw new ArgumentException("That source no longer exists.");
            if (FindSourceRunner(id) is { } runner)
            {
                await runner.StopAsync().ConfigureAwait(false);
                Volatile.Write(ref _sources, Sources.Where(s => !ReferenceEquals(s, runner)).ToArray());
            }

            foreach (var collection in Collections)
            {
                collection.RemoveSource(id);
            }

            var touched = false;
            foreach (var collection in Settings.Collections)
            {
                touched |= collection.Sources.RemoveAll(m => m.Id == id) > 0;
            }

            Settings.Sources.Remove(definition);
            Settings.SaveSources();
            if (touched)
            {
                Settings.SaveCollections();
            }

            _handles.Remove(id);
            if (_secrets is not null || File.Exists(Paths.SecretsFile))
            {
                Secrets.RemoveAll(id);
            }

            DeleteQuietly(Paths.SourceState(id));
            DeleteQuietly(Paths.SourceCache(id));
            _logger.LogInformation("source {Source} deleted", definition.Name);
            OnChanged();
        }
        finally
        {
            _mutations.Release();
        }
    }

    // ----- import and export (spec 9.5) -----

    /// <summary>Writes the chosen sources as a sources file for someone else to import; secrets are never in it.</summary>
    public void ExportSources(IEnumerable<Guid> ids, string path)
    {
        var wanted = ids.ToHashSet();
        var sources = Settings.Sources.Where(s => wanted.Contains(s.Id)).ToList();
        SourceTransfer.Write(path, sources);
        _logger.LogInformation("exported {Count} source(s) to {Path}", sources.Count, path);
    }

    /// <summary>
    /// Merges sources from a file by id: a new id is added as it is, a known one goes to <paramref name="resolve"/>
    /// (replace mine, keep mine, or import as a copy under a new id). Names are kept unique with a suffix.
    /// Secret settings are never in a file, so an imported source keeps the secrets its id already had here, and a
    /// new one has none until it is edited (its plugin asks for what it needs, or fails saying so).
    /// </summary>
    public async Task<ImportSummary> ImportSourcesAsync(IReadOnlyList<SourceDefinition> incoming, Func<SourceDefinition, SourceDefinition, Task<ImportConflict>> resolve)
    {
        int added = 0, replaced = 0, kept = 0, copied = 0;
        foreach (var source in incoming)
        {
            var existing = Settings.FindSource(source.Id);
            if (existing is null)
            {
                await ImportNewAsync(source, keepId: true).ConfigureAwait(false);
                added++;
                continue;
            }

            // the question is asked outside the mutation lock: it waits for the user
            switch (await resolve(existing, source).ConfigureAwait(false))
            {
                case ImportConflict.Replace:
                    await ReplaceImportedAsync(existing.Id, source).ConfigureAwait(false);
                    replaced++;
                    break;
                case ImportConflict.Copy:
                    await ImportNewAsync(source, keepId: false).ConfigureAwait(false);
                    copied++;
                    break;
                default:
                    kept++;
                    break;
            }
        }

        _logger.LogInformation("import: {Added} added, {Replaced} replaced, {Kept} kept, {Copied} copied", added, replaced, kept, copied);
        return new ImportSummary(added, replaced, kept, copied);
    }

    private async Task ImportNewAsync(SourceDefinition source, bool keepId)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var definition = SourceTransfer.Copy(source);
            if (!keepId || definition.Id == Guid.Empty || Settings.FindSource(definition.Id) is not null)
            {
                definition.Id = Guid.NewGuid();
            }

            definition.Name = UniqueSourceName(RequireName(definition.Name, "A source"), null);
            definition.Settings = RehomeSecrets(definition);
            Settings.Sources.Add(definition);
            Settings.SaveSources();
            if (_started)
            {
                var runner = CreateSourceRunner(definition);
                Volatile.Write(ref _sources, [.. Sources, runner]);
                StartIfNeeded(runner);
            }

            _logger.LogInformation("source {Source} ({Type}) imported", definition.Name, definition.Type);
            OnChanged();
        }
        finally
        {
            _mutations.Release();
        }
    }

    private async Task ReplaceImportedAsync(Guid id, SourceDefinition source)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var definition = Settings.FindSource(id) ?? throw new ArgumentException("That source no longer exists.");
            var incoming = SourceTransfer.Copy(source);
            incoming.Id = id;
            definition.Name = UniqueSourceName(RequireName(incoming.Name, "A source"), id);
            definition.Type = incoming.Type;
            definition.Enabled = incoming.Enabled;
            definition.CountFilesOnStart = incoming.CountFilesOnStart;
            definition.Settings = RehomeSecrets(incoming);
            Settings.SaveSources();

            if (_started && FindSourceRunner(id) is { } runner)
            {
                runner.Handle.Name = definition.Name;
                await runner.StopAsync().ConfigureAwait(false);
                var replacement = CreateSourceRunner(definition);
                Volatile.Write(ref _sources, Sources.Select(s => ReferenceEquals(s, runner) ? replacement : s).ToArray());
                StartIfNeeded(replacement);
                RefreshCollectionsUsing(id);
            }

            _logger.LogInformation("source {Source} replaced from an import", definition.Name);
            OnChanged();
        }
        finally
        {
            _mutations.Release();
        }
    }

    /// <summary>A sources file normally carries no secrets; one that does (hand-written) has them moved to the secret store here.</summary>
    private JsonObject? RehomeSecrets(SourceDefinition definition)
    {
        var plugin = Registry.FindSource(definition.Type);
        if (plugin is null || definition.Settings is null)
        {
            return definition.Settings;
        }

        try
        {
            var settings = PluginSettingsBinder.Materialise(plugin.SettingsType, definition.Settings);
            return PluginSettingsBinder.Serialise(plugin.SettingsType, settings, (property, value) =>
            {
                if (!string.IsNullOrEmpty(value))
                {
                    Secrets.Set(definition.Id, property, value);
                }
            });
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NotSupportedException)
        {
            // unreadable settings start the source with the runner's "Settings could not be read" instead
            return definition.Settings;
        }
    }

    private string UniqueSourceName(string name, Guid? except)
    {
        var candidate = name;
        for (var n = 2; Settings.Sources.Any(s => s.Id != except && s.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); n++)
        {
            candidate = $"{name} ({n})";
        }

        return candidate;
    }

    // ----- collections -----

    public async Task<CollectionDefinition> AddCollectionAsync(string name)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            name = RequireCollectionName(name, null);
            var definition = new CollectionDefinition { Name = name };
            Settings.Collections.Add(definition);
            Settings.SaveCollections();
            if (_started && (_filter is null || _filter.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                var runner = CreateCollectionRunner(definition);
                Volatile.Write(ref _collections, [.. Collections, runner]);
                await runner.StartAsync(_shutdown.Token).ConfigureAwait(false);
            }

            _logger.LogInformation("collection {Collection} added", name);
            OnChanged();
            return definition;
        }
        finally
        {
            _mutations.Release();
        }
    }

    /// <summary>Renaming restarts the collection's mounts: the prefix, URL or export changes (spec 8.8).</summary>
    public async Task RenameCollectionAsync(Guid id, string name)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var definition = FindCollectionDefinition(id);
            name = RequireCollectionName(name, id);
            if (definition.Name.Equals(name, StringComparison.Ordinal))
            {
                return;
            }

            var old = definition.Name;
            definition.Name = name;
            Settings.SaveCollections();
            Stats.RenameCollection(old, name);
            if (FindCollectionRunner(id) is { } runner)
            {
                runner.Rename(name);
                await runner.RestartMountsAsync(_shutdown.Token).ConfigureAwait(false);
            }

            _logger.LogInformation("collection {Old} renamed to {New}", old, name);
            OnChanged();
        }
        finally
        {
            _mutations.Release();
        }
    }

    public async Task SetCollectionEnabledAsync(Guid id, bool enabled)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var definition = FindCollectionDefinition(id);
            if (definition.Enabled == enabled)
            {
                return;
            }

            definition.Enabled = enabled;
            Settings.SaveCollections();
            if (FindCollectionRunner(id) is { } runner)
            {
                if (enabled)
                {
                    foreach (var member in runner.Sources)
                    {
                        if (FindSourceRunner(member.Handle.Id) is { } source)
                        {
                            StartIfNeeded(source);
                        }
                    }

                    await runner.StartAsync(_shutdown.Token).ConfigureAwait(false);
                }
                else
                {
                    await runner.StopAsync().ConfigureAwait(false);
                }
            }

            OnChanged();
        }
        finally
        {
            _mutations.Release();
        }
    }

    /// <summary>Deletes the collection and its mounts; its sources stay defined (the window asks about orphans, spec 9.4).</summary>
    public async Task DeleteCollectionAsync(Guid id)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var definition = FindCollectionDefinition(id);
            if (FindCollectionRunner(id) is { } runner)
            {
                await runner.StopAsync().ConfigureAwait(false);
                Volatile.Write(ref _collections, Collections.Where(c => !ReferenceEquals(c, runner)).ToArray());
            }

            Settings.Collections.Remove(definition);
            Settings.SaveCollections();
            Stats.RemoveCollection(definition.Name);
            foreach (var mount in definition.Mounts)
            {
                if (_secrets is not null || File.Exists(Paths.SecretsFile))
                {
                    Secrets.RemoveAll(mount.Id);
                }

                DeleteQuietly(Paths.MountState(mount.Id));
            }

            _logger.LogInformation("collection {Collection} deleted", definition.Name);
            OnChanged();
        }
        finally
        {
            _mutations.Release();
        }
    }

    // ----- memberships (a source's place in a collection) -----

    public async Task AddMembershipAsync(Guid collectionId, Guid sourceId, string path, bool readOnly)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var collection = FindCollectionDefinition(collectionId);
            var source = Settings.FindSource(sourceId) ?? throw new ArgumentException("That source no longer exists.");
            if (collection.Sources.Any(m => m.Id == sourceId))
            {
                throw new ArgumentException($"'{source.Name}' is already in '{collection.Name}'.");
            }

            var membership = new SourceMembership { Id = sourceId, Path = NormalisePath(path), ReadOnly = readOnly };
            collection.Sources.Add(membership);
            Settings.SaveCollections();
            if (FindCollectionRunner(collectionId) is { } runner && _handles.TryGetValue(sourceId, out var handle))
            {
                runner.AddSource(new CollectionSource(handle, membership, source));
                if (FindSourceRunner(sourceId) is { } sourceRunner)
                {
                    StartIfNeeded(sourceRunner);
                }
            }

            OnChanged();
        }
        finally
        {
            _mutations.Release();
        }
    }

    /// <summary>The path and Read-only of a source in one collection only (spec 4.2, 11.1).</summary>
    public async Task UpdateMembershipAsync(Guid collectionId, Guid sourceId, string path, bool readOnly)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var collection = FindCollectionDefinition(collectionId);
            var membership = collection.Sources.FirstOrDefault(m => m.Id == sourceId) ?? throw new ArgumentException("That source is not in this collection.");
            var normalised = NormalisePath(path);
            if (membership.Path == normalised && membership.ReadOnly == readOnly)
            {
                return;
            }

            membership.Path = normalised;
            membership.ReadOnly = readOnly;
            Settings.SaveCollections();
            FindCollectionRunner(collectionId)?.RefreshMembers();
            OnChanged();
        }
        finally
        {
            _mutations.Release();
        }
    }

    public async Task RemoveMembershipAsync(Guid collectionId, Guid sourceId)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var collection = FindCollectionDefinition(collectionId);
            if (collection.Sources.RemoveAll(m => m.Id == sourceId) == 0)
            {
                return;
            }

            Settings.SaveCollections();
            FindCollectionRunner(collectionId)?.RemoveSource(sourceId);
            if (_filter is not null && FindSourceRunner(sourceId) is { } runner && !IsNeeded(sourceId))
            {
                await runner.StopAsync().ConfigureAwait(false);
                runner.Handle.Status = new ComponentStatus(ComponentState.Disabled, "Not in a started collection");
            }

            OnChanged();
        }
        finally
        {
            _mutations.Release();
        }
    }

    // ----- mounts -----

    public async Task<MountDefinition> AddMountAsync(Guid collectionId, string name, string providerId, bool enabled, bool readOnly, object settings)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var collection = FindCollectionDefinition(collectionId);
            var provider = Registry.FindMountProvider(providerId) ?? throw new ArgumentException($"There is no mount provider '{providerId}'.");
            name = RequireMountName(collection, name, null);
            var definition = new MountDefinition { Name = name, Provider = provider.TypeId, Enabled = enabled, ReadOnly = readOnly || !provider.SupportsWrites };
            definition.Settings = PluginSettingsBinder.Serialise(provider.SettingsType, settings, (property, value) => Secrets.Set(definition.Id, property, value));
            collection.Mounts.Add(definition);
            Settings.SaveCollections();
            if (FindCollectionRunner(collectionId) is { } runner)
            {
                var mount = CreateMountRunner(definition, runner);
                runner.AddMount(mount);
                await runner.StartMountAsync(mount, _shutdown.Token).ConfigureAwait(false);
            }

            _logger.LogInformation("mount {Mount} ({Provider}) added to {Collection}", name, provider.TypeId, collection.Name);
            OnChanged();
            return definition;
        }
        finally
        {
            _mutations.Release();
        }
    }

    /// <summary>Editing a mount restarts it (spec 8.8); a name-only edit doesn't.</summary>
    public async Task UpdateMountAsync(Guid collectionId, Guid mountId, string name, bool enabled, bool readOnly, object settings)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var collection = FindCollectionDefinition(collectionId);
            var definition = collection.Mounts.FirstOrDefault(m => m.Id == mountId) ?? throw new ArgumentException("That mount no longer exists.");
            var provider = Registry.FindMountProvider(definition.Provider) ?? throw new ArgumentException($"The mount provider '{definition.Provider}' is not available, so its settings can't be edited.");
            name = RequireMountName(collection, name, mountId);
            readOnly |= !provider.SupportsWrites;

            var secretsChanged = false;
            var json = PluginSettingsBinder.Serialise(provider.SettingsType, settings, (property, value) =>
            {
                secretsChanged |= !string.Equals(Secrets.Get(mountId, property), string.IsNullOrEmpty(value) ? null : value, StringComparison.Ordinal);
                Secrets.Set(mountId, property, value);
            });
            var restart = secretsChanged || !JsonNode.DeepEquals(definition.Settings, json) || definition.ReadOnly != readOnly || definition.Enabled != enabled;
            definition.Name = name;
            definition.Enabled = enabled;
            definition.ReadOnly = readOnly;
            definition.Settings = json;
            Settings.SaveCollections();

            if (restart && FindCollectionRunner(collectionId) is { } runner && runner.Mounts.FirstOrDefault(m => m.Definition.Id == mountId) is { } old)
            {
                await old.StopAsync().ConfigureAwait(false);
                var replacement = CreateMountRunner(definition, runner);
                runner.ReplaceMount(old, replacement);
                await runner.StartMountAsync(replacement, _shutdown.Token).ConfigureAwait(false);
            }

            _logger.LogInformation("mount {Mount} of {Collection} updated ({What})", name, collection.Name, restart ? "restarted" : "no restart needed");
            OnChanged();
        }
        finally
        {
            _mutations.Release();
        }
    }

    /// <summary>Stops and starts a mount again now (after a failure, or after its provider was installed).</summary>
    public async Task RetryMountAsync(Guid collectionId, Guid mountId)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            if (FindCollectionRunner(collectionId) is { } runner && runner.Mounts.FirstOrDefault(m => m.Definition.Id == mountId) is { } old && old.Definition.Enabled)
            {
                await old.StopAsync().ConfigureAwait(false);
                var replacement = CreateMountRunner(old.Definition, runner);
                runner.ReplaceMount(old, replacement);
                await runner.StartMountAsync(replacement, _shutdown.Token).ConfigureAwait(false);
                OnChanged();
            }
        }
        finally
        {
            _mutations.Release();
        }
    }

    public async Task SetMountEnabledAsync(Guid collectionId, Guid mountId, bool enabled)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var collection = FindCollectionDefinition(collectionId);
            var definition = collection.Mounts.FirstOrDefault(m => m.Id == mountId) ?? throw new ArgumentException("That mount no longer exists.");
            if (definition.Enabled == enabled)
            {
                return;
            }

            definition.Enabled = enabled;
            Settings.SaveCollections();
            if (FindCollectionRunner(collectionId) is { } runner && runner.Mounts.FirstOrDefault(m => m.Definition.Id == mountId) is { } mount)
            {
                if (enabled)
                {
                    await runner.StartMountAsync(mount, _shutdown.Token).ConfigureAwait(false);
                }
                else
                {
                    await mount.StopAsync().ConfigureAwait(false);
                }
            }

            OnChanged();
        }
        finally
        {
            _mutations.Release();
        }
    }

    public async Task DeleteMountAsync(Guid collectionId, Guid mountId)
    {
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            var collection = FindCollectionDefinition(collectionId);
            var definition = collection.Mounts.FirstOrDefault(m => m.Id == mountId) ?? throw new ArgumentException("That mount no longer exists.");
            if (FindCollectionRunner(collectionId) is { } runner && runner.Mounts.FirstOrDefault(m => m.Definition.Id == mountId) is { } mount)
            {
                await mount.StopAsync().ConfigureAwait(false);
                runner.RemoveMount(mount);
            }

            collection.Mounts.Remove(definition);
            Settings.SaveCollections();
            if (_secrets is not null || File.Exists(Paths.SecretsFile))
            {
                Secrets.RemoveAll(mountId);
            }

            DeleteQuietly(Paths.MountState(mountId));
            _logger.LogInformation("mount {Mount} of {Collection} deleted", definition.Name, collection.Name);
            OnChanged();
        }
        finally
        {
            _mutations.Release();
        }
    }

    // ----- building blocks -----

    private SourceRunner CreateSourceRunner(SourceDefinition definition)
    {
        if (!_handles.TryGetValue(definition.Id, out var handle))
        {
            handle = new SourceHandle(definition.Id, definition.Name, definition.Type);
            handle.Changed += _ => OnChanged();
            _handles[definition.Id] = handle;
        }

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

        return new SourceRunner(definition, plugin, settings, error, handle, Paths, plugin is null ? _secrets : Secrets, _prompts, _loggerFactory.CreateLogger($"AnyFS.Source.{definition.Name}"), _time);
    }

    private CollectionRunner CreateCollectionRunner(CollectionDefinition definition)
    {
        var vfs = new CollectionVfs(definition.Id, definition.Name, Stats, _loggerFactory.CreateLogger($"AnyFS.Collection.{definition.Name}"));
        var members = definition.Sources
            .Where(m => _handles.ContainsKey(m.Id))
            .Select(m => new CollectionSource(_handles[m.Id], m, Settings.FindSource(m.Id)!))
            .ToList();
        var info = new CollectionInfo(definition.Id, definition.Name);
        var mounts = definition.Mounts.Select(m => CreateMountRunner(m, info)).ToList();
        return new CollectionRunner(definition, vfs, members, mounts, _loggerFactory.CreateLogger($"AnyFS.Collection.{definition.Name}"), _time);
    }

    private MountRunner CreateMountRunner(MountDefinition definition, ICollectionInfo collection)
    {
        var provider = Registry.FindMountProvider(definition.Provider);
        object? settings = null;
        string? error = null;
        if (provider is not null)
        {
            try
            {
                settings = PluginSettingsBinder.Materialise(provider.SettingsType, definition.Settings, name => Secrets.Get(definition.Id, name));
            }
            catch (Exception ex)
            {
                error = $"Settings could not be read: {ex.GetBaseException().Message}";
            }
        }

        var runner = new MountRunner(definition, provider, settings, error, collection, Paths, Shared, Stats, _loggerFactory.CreateLogger($"AnyFS.Mount.{collection.Name}.{definition.Name}"));
        runner.Changed += _ => OnChanged();
        return runner;
    }

    /// <summary>Starts a source runner when something started needs it (every enabled source without a filter).</summary>
    private void StartIfNeeded(SourceRunner runner)
    {
        if (!runner.Definition.Enabled)
        {
            runner.Handle.Status = ComponentStatus.DisabledStatus;
            return;
        }

        if (IsNeeded(runner.Definition.Id))
        {
            runner.Start();
        }
        else
        {
            runner.Handle.Status = new ComponentStatus(ComponentState.Disabled, "Not in a started collection");
        }
    }

    private bool IsNeeded(Guid sourceId)
        => _filter is null || Collections.Any(c => c.Definition.Enabled && c.Definition.Sources.Any(m => m.Id == sourceId));

    private void RefreshCollectionsUsing(Guid sourceId)
    {
        foreach (var collection in Collections.Where(c => c.Sources.Any(s => s.Handle.Id == sourceId)))
        {
            collection.RefreshMembers();
        }
    }

    private CollectionDefinition FindCollectionDefinition(Guid id)
        => Settings.Collections.FirstOrDefault(c => c.Id == id) ?? throw new ArgumentException("That collection no longer exists.");

    private void ThrowIfStopped()
    {
        if (_stopped)
        {
            throw new InvalidOperationException("AnyFS is shutting down.");
        }
    }

    private static string RequireName(string name, string what)
    {
        var trimmed = name?.Trim() ?? "";
        return trimmed.Length == 0 ? throw new ArgumentException($"{what} needs a name.") : trimmed;
    }

    private void RequireUniqueSourceName(string name, Guid? except)
    {
        if (Settings.Sources.Any(s => s.Id != except && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"There is already a source named '{name}'.");
        }
    }

    private string RequireCollectionName(string name, Guid? except)
    {
        name = RequireName(name, "A collection");
        if (!VfsPath.IsValidSegment(name, out var reason))
        {
            throw new ArgumentException($"'{name}' can't be a collection name: {reason}. It becomes part of every access path (\\\\anyfs\\{name}, http://host/{name}/).");
        }

        if (Settings.Collections.Any(c => c.Id != except && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"There is already a collection named '{name}'.");
        }

        return name;
    }

    private static string RequireMountName(CollectionDefinition collection, string name, Guid? except)
    {
        name = RequireName(name, "A mount");
        if (collection.Mounts.Any(m => m.Id != except && m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"'{collection.Name}' already has a mount named '{name}'.");
        }

        return name;
    }

    private static string NormalisePath(string path)
        => VfsPath.TryNormalizeSourcePath(path ?? "", out var normalised, out var reason) ? normalised : throw new ArgumentException($"The path '{path}' isn't valid: {reason}");

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void OnChanged() => Changed?.Invoke();

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

/// <summary>Reads what a running <c>anyfs serve</c> (or window) left in state/status.json.</summary>
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
