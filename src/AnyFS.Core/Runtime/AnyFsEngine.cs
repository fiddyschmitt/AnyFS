using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json.Nodes;
using AnyFS.Core.Model;
using AnyFS.Core.Operations;
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
/// while it runs (the Add/Edit/Delete/Enabled controls of the window): each change is persisted to the
/// settings files and made to the lists of runners in one go, serialised so two changes never
/// interleave. What a change stops and starts follows in the turn of the source or the collection it
/// is about (<see cref="Lane"/>), in the order of the changes, and holds up no change to another.
/// </summary>
public sealed class AnyFsEngine : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly IPromptHandler _prompts;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _mutations = new(1, 1);

    /// <summary>
    /// Waits for the mutations' lock, then goes on off the caller's thread. The window calls mutations on its own thread,
    /// and with the lock free the rest ran there: settings and secrets written, Windows' data protection, a plugin's
    /// hooks, for as long as they take, the window held up meanwhile (the user, 23-Oct-2026, after adding three SharePoint
    /// sources: "window: its thread was busy for 18059 ms").
    /// </summary>
    private async Task EnterAsync() => await _mutations.WaitAsync().ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<Guid, SourceHandle> _handles = [];

    // one for each source and each collection, by its id: what a change stops and starts goes through them (see InTurn)
    private readonly ConcurrentDictionary<Guid, Lane> _lanes = new();

    // what the change now being made has asked for in turn, for it to wait for once it has let the lock go
    private List<(string What, Task Done)>? _following;
    private SourceRunner[] _sources = [];
    private CollectionRunner[] _collections = [];
    private SecretStore? _secrets;
    private IReadOnlyCollection<string>? _filter;
    private volatile bool _started;
    private volatile bool _stopped;

    // status.json writes that failed in a row (every 2 s): a warning once they have for half a minute
    private int _statusFailures;

    // what the run's last tick threw (its type and message), null after one that threw nothing
    private string? _tickFailure;

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
        Operations = new OperationTracker(_time);
        Operations.Finished += OnOperationFinished;
        Shared = new SharedResources();
        StartedAt = _time.GetUtcNow();
    }

    /// <summary>An operation that took this long gets a debug line in the log when it ends, as a failed one does.</summary>
    public static readonly TimeSpan SlowOperation = TimeSpan.FromSeconds(5);

    /// <summary>Raised (on any thread) when a status changed or something was added, edited or removed.</summary>
    public event Action? Changed;

    public SettingsPaths Paths { get; }

    public PluginRegistry Registry { get; }

    public SettingsStore Settings { get; }

    public StatsCollector Stats { get; }

    /// <summary>Every operation asked of every source: what runs now, what just finished, and the session's counters.</summary>
    public OperationTracker Operations { get; }

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
        RehomeSecretsAtLoad();
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
        // Under the gate a change takes. The window is open while the engine starts and takes changes: one made in that
        // moment now either comes first, and is in the settings the start reads, or waits and finds what was started.
        // Without the gate a collection added just then was written to the settings by one thread while this one read
        // them and kept its runners, and ended with two runners or none. The gate is let go before the collections
        // start, which may wait long for a source; they are the ones made here, not whatever is there by then.
        await _mutations.WaitAsync(ct).ConfigureAwait(false);
        Task[] starting;
        try
        {
            // each collection's start in its own turn, so that what a change asks of the collection meanwhile comes after it
            starting = Begin(collectionNames).Select(c => LaneOf(c.Id).Then(() => c.StartAsync(ct))).ToArray();
        }
        finally
        {
            _mutations.Release();
        }

        await Task.WhenAll(starting).ConfigureAwait(false);
        OnChanged();
    }

    /// <summary>What a start reads of the settings and keeps of them, with the gate held: the sources started, the collections' runners made.</summary>
    private CollectionRunner[] Begin(IReadOnlyCollection<string>? collectionNames)
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
        var collections = selected.Select(CreateCollectionRunner).ToArray();
        Volatile.Write(ref _collections, collections);
        return collections;
    }

    public async Task StopAsync()
    {
        // with the gate a change takes: no change is part way through its lists while they are read here, and one that
        // comes after finds the engine stopped
        IReadOnlyList<CollectionRunner> collections;
        IReadOnlyList<SourceRunner> sources;
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopped = true;
            collections = Collections;
            sources = Sources;
        }
        finally
        {
            _mutations.Release();
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        // the mounts first, so nothing is asked of a source while it stops; then every source at once: one after another, a
        // window closing gave its 15 s to the first, and the others were cut off before they had written what they kept.
        // Each in its own turn: after what a change still has going on for it, which starts nothing more now
        await Task.WhenAll(collections.Select(collection => LaneOf(collection.Id).Then(async () =>
        {
            try
            {
                await collection.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "collection {Collection}: stop failed", collection.Name);
            }
        }))).ConfigureAwait(false);

        await Task.WhenAll(sources.Select(source => LaneOf(source.Definition.Id).Then(async () =>
        {
            try
            {
                await source.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "source {Source}: stop failed", source.Definition.Name);
            }
        }))).ConfigureAwait(false);

        // and what a change left going on for something no longer in the lists: a source deleted a moment ago still stops,
        // and keeps what it keeps
        await TurnsEndedAsync().ConfigureAwait(false);

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
        // refused here, outside the try, so that a second run does not stop what the first started
        if (_started)
        {
            throw new InvalidOperationException("already started");
        }

        // the start is under the finally too: cancelled part way (the window closed while sources start), it left what it
        // had started running, no source disposed, no mount stopped, no stats flushed
        try
        {
            await StartAsync(collectionNames, ct).ConfigureAwait(false);
            var lastFlush = _time.GetTimestamp();
            Tick(ref lastFlush, ct);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _time, ct).ConfigureAwait(false);
                Tick(ref lastFlush, ct);
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

    /// <summary>
    /// One tick of <see cref="RunAsync"/>: stats.json when half a minute has gone by, then status.json. Much of what the
    /// status is made of is a plugin's to supply (a mount's access paths, a source's counters, a progress fraction that may
    /// be no number JSON can carry), so a tick may throw anything. That costs the tick: it used to end the run, and with it
    /// every source and mount while the window stayed up. A failure is logged when it begins and when it changes, not every
    /// two seconds, and once more when a tick works again.
    /// </summary>
    private void Tick(ref long lastFlush, CancellationToken ct)
    {
        try
        {
            // the stats first, so that they are kept while a status cannot be made
            if (_time.GetElapsedTime(lastFlush) >= TimeSpan.FromSeconds(30))
            {
                lastFlush = _time.GetTimestamp();
                FlushStats();
            }

            WriteStatus();
            if (_tickFailure is not null && _statusFailures == 0)
            {
                _tickFailure = null;
                _logger.LogInformation("status.json and stats.json are kept fresh again");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failure = $"{ex.GetType().FullName}: {ex.Message}";
            if (failure != _tickFailure)
            {
                _tickFailure = failure;
                _logger.LogWarning(ex, "status.json and stats.json could not be kept fresh; the sources and mounts go on, and it is tried again every two seconds (logged again only when the failure changes)");
            }
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

    /// <summary>
    /// Writes status.json, as <see cref="RunAsync"/> does every two seconds. A write that fails (something else held the file
    /// a moment longer than <see cref="JsonFiles.WriteAtomic"/> waits, an antivirus scanning it) leaves the last one in
    /// place until the next: logged at debug, and as a warning only once the file has stayed unwritable for half a minute.
    /// </summary>
    public void WriteStatus()
    {
        try
        {
            JsonFiles.WriteAtomic(Paths.StatusFile, Snapshot(), CoreJsonContext.Default.StatusSnapshot);
            _statusFailures = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (++_statusFailures == 15)
            {
                _logger.LogWarning(ex, "status.json could not be written for half a minute ({Path}); anyfs status may not see this AnyFS running", Paths.StatusFile);
            }
            else
            {
                _logger.LogDebug("status.json could not be written ({Error}); it is written again in two seconds", ex.Message);
            }
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
            Operations = Operations.Stats(),
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
            return Materialise(plugin.SettingsType, definition.Id, definition.Settings);
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
            return Materialise(provider.SettingsType, definition.Id, definition.Settings);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NotSupportedException)
        {
            return provider.CreateDefaultSettings(collection);
        }
    }

    // ----- a plugin's settings and the secrets among them, the same for a source and a mount: its owner is either's id -----

    /// <summary>A plugin's settings object from its JSON, the secrets filled in from the secret store.</summary>
    private object Materialise([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)] Type settingsType, Guid owner, JsonObject? json)
        => PluginSettingsBinder.Materialise(settingsType, json, name => Secrets.Get(owner, name));

    /// <summary>The JSON of a settings object, its secrets put in the secret store and not in the JSON; whether one of them differs from what the store held.</summary>
    private JsonObject Serialise([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)] Type settingsType, Guid owner, object settings, out bool secretsChanged)
    {
        var changed = false;
        var json = PluginSettingsBinder.Serialise(settingsType, settings, (property, value) =>
        {
            changed |= !string.Equals(Secrets.Get(owner, property), string.IsNullOrEmpty(value) ? null : value, StringComparison.Ordinal);
            Secrets.Set(owner, property, value);
        });
        secretsChanged = changed;
        return json;
    }

    /// <summary>
    /// JSON that came with secrets in it (a file written by hand, an import) without them, and the secrets in the secret
    /// store. JSON the plugin cannot read is left as it is: the runner then says "Settings could not be read".
    /// </summary>
    private JsonObject? Rehome([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)] Type settingsType, Guid owner, JsonObject? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            var settings = PluginSettingsBinder.Materialise(settingsType, json);
            return PluginSettingsBinder.Serialise(settingsType, settings, (property, value) =>
            {
                if (!string.IsNullOrEmpty(value))
                {
                    Secrets.Set(owner, property, value);
                }
            });
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or NotSupportedException)
        {
            return json;
        }
    }

    /// <summary>Removes what the secret store holds for a source or a mount. The store is not made, nor its file, only to find nothing in it.</summary>
    private void ForgetSecrets(Guid owner)
    {
        if (_secrets is not null || File.Exists(Paths.SecretsFile))
        {
            Secrets.RemoveAll(owner);
        }
    }

    // ----- changes, one at a time; what they stop and start, in turn -----

    /// <summary>
    /// How long a change waits for what it stops and starts (a source disposed, a mount unmounted) before it comes back
    /// with that still going on. The change is made by then, and what is left of it follows in its own time: a dialog is
    /// busy for this long at most.
    /// </summary>
    public TimeSpan ChangePatience { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One change: <paramref name="change"/> runs with the mutations' lock held and off the caller's thread, and does what
    /// is quick, the settings and the lists of runners. What may take long, a source or a mount stopped or started, it asks
    /// for with <see cref="InTurn"/>, and that is waited for with the lock let go, for <see cref="ChangePatience"/> at
    /// most. The lock was held across the stops (the review of 21-Nov-2026, R7a): while one source took its time to stop,
    /// and for ever when it never did, every other Add, Edit, Enable and Retry waited, its dialog busy.
    /// </summary>
    private async Task<T> MutateAsync<T>(Func<T> change)
    {
        List<(string What, Task Done)> following = [];
        T result;
        await EnterAsync().ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            _following = following;
            result = change();
        }
        catch
        {
            // what it had asked for goes on all the same, and no one waits for it: a failure there is the log's
            foreach (var (what, done) in following)
            {
                Watch(what, done);
            }

            throw;
        }
        finally
        {
            _following = null;
            _mutations.Release();
        }

        await FollowAsync(following).ConfigureAwait(false);
        return result;
    }

    private Task MutateAsync(Action change) => MutateAsync(() =>
    {
        change();
        return true;
    });

    private Lane LaneOf(Guid id) => _lanes.GetOrAdd(id, static _ => new Lane());

    /// <summary>Ends when everything asked for in turn so far has ended, however it ended.</summary>
    internal async Task TurnsEndedAsync()
        => await Task.WhenAll(_lanes.Values.Select(lane => lane.Last)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    /// <summary>
    /// Asks for <paramref name="work"/> in the turn of a source or a collection, by its id: after whatever was asked of it
    /// before, and never under the mutations' lock. Called with the lock held, so the turns are in the order of the
    /// changes. What the work does is decided here, by the change; when its turn comes it stops and starts what it was
    /// given, and starts nothing that has left the lists since or once the engine stops.
    /// </summary>
    private void InTurn(Guid id, string what, Func<Task> work)
    {
        var done = LaneOf(id).Then(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopped)
            {
                // the engine stopped while it started something
            }
            finally
            {
                OnChanged();
            }
        });
        if (_following is { } following)
        {
            following.Add((what, done));
        }
        else
        {
            Watch(what, done);
        }
    }

    /// <summary>Waits, with the lock let go, for what a change asked for in turn: until it is done or <see cref="ChangePatience"/> has passed.</summary>
    private async Task FollowAsync(List<(string What, Task Done)> following)
    {
        if (following.Count == 0)
        {
            return;
        }

        var all = Task.WhenAll(following.Select(f => f.Done));
        try
        {
            await all.WaitAsync(ChangePatience, _time).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!all.IsCompleted)
        {
            foreach (var (what, done) in following.Where(f => !f.Done.IsCompleted))
            {
                _logger.LogWarning("{What} is taking longer than {Seconds:0.#} s over a change. The change is made and kept; what is left of it follows when that is done, and nothing else waits for it", what, ChangePatience.TotalSeconds);
                Watch(what, done);
            }
        }
    }

    /// <summary>For work no one waits for: what it throws is a warning in the log.</summary>
    private void Watch(string what, Task done)
        => _ = done.ContinueWith(
            t => _logger.LogWarning(t.Exception!.GetBaseException(), "{What}: what a change asked of it failed", what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static string Describe(SourceRunner runner) => $"source '{runner.Definition.Name}'";

    private static string Describe(CollectionRunner runner) => $"collection '{runner.Name}'";

    /// <summary>
    /// Starts a source's runner, in the source's turn, when something started needs it (every enabled source without a
    /// filter). Whether it is needed is decided now, with the lock held.
    /// </summary>
    private void StartInTurn(SourceRunner runner)
    {
        var needed = IsNeeded(runner.Definition.Id);
        InTurn(runner.Definition.Id, Describe(runner), () =>
        {
            Start(runner, needed);
            return Task.CompletedTask;
        });
    }

    /// <summary>Stops a source's runner in the source's turn.</summary>
    private void StopInTurn(SourceRunner runner) => InTurn(runner.Definition.Id, Describe(runner), runner.StopAsync);

    /// <summary>Stops a source's runner and starts it again, as Retry does and a collection enabled over a failed source.</summary>
    private void RestartInTurn(SourceRunner runner)
    {
        var needed = IsNeeded(runner.Definition.Id);
        InTurn(runner.Definition.Id, Describe(runner), async () =>
        {
            await runner.StopAsync().ConfigureAwait(false);
            Start(runner, needed);
        });
    }

    /// <summary>
    /// Puts a runner made from the definition as it is now in a running one's place, at once. In the source's turn the old
    /// one is stopped, and the new one is started when that has stopped and not before: both work through one handle, and
    /// in one state folder.
    /// </summary>
    private void ReplaceInTurn(SourceRunner runner, SourceDefinition definition)
    {
        var replacement = CreateSourceRunner(definition);
        Volatile.Write(ref _sources, Sources.Select(s => ReferenceEquals(s, runner) ? replacement : s).ToArray());
        var needed = IsNeeded(definition.Id);
        InTurn(definition.Id, Describe(replacement), async () =>
        {
            await runner.StopAsync().ConfigureAwait(false);
            Start(replacement, needed);
        });
    }

    /// <summary>A source's turn has come to start it: not when it has left the list since (replaced, deleted), nor once the engine stops.</summary>
    private void Start(SourceRunner runner, bool needed)
    {
        if (_stopped || !Sources.Contains(runner))
        {
            return;
        }

        if (!runner.Definition.Enabled)
        {
            runner.Handle.Status = ComponentStatus.DisabledStatus;
        }
        else if (needed)
        {
            runner.Start();
        }
        else
        {
            runner.Handle.Status = new ComponentStatus(ComponentState.Disabled, "Not in a started collection");
        }
    }

    /// <summary>Starts a collection, in its turn: its mounts, when a source is usable or the grace has passed.</summary>
    private void StartInTurn(CollectionRunner runner)
        => InTurn(runner.Id, Describe(runner), async () =>
        {
            if (!_stopped && Collections.Contains(runner))
            {
                await runner.StartAsync(_shutdown.Token).ConfigureAwait(false);
            }
        });

    /// <summary>Starts one mount in its collection's turn: not when it or the collection has left the lists since, nor once the engine stops.</summary>
    private Task StartAsync(CollectionRunner runner, MountRunner mount)
        => _stopped || !Collections.Contains(runner) || !runner.Mounts.Contains(mount)
            ? Task.CompletedTask
            : runner.StartMountAsync(mount, _shutdown.Token);

    /// <summary>
    /// Puts a runner made from the definition as it is now in a mount's place, at once. In the collection's turn the old
    /// one is stopped and the new one started when that has stopped: both would hold one port, drive or folder.
    /// </summary>
    private void ReplaceInTurn(CollectionRunner runner, MountRunner old, MountDefinition definition)
    {
        var replacement = CreateMountRunner(definition, runner);
        if (old.Mount is not null)
        {
            replacement.TakesOver();
        }

        runner.ReplaceMount(old, replacement);
        InTurn(runner.Id, $"mount '{definition.Name}' of '{runner.Name}'", async () =>
        {
            await old.StopAsync().ConfigureAwait(false);
            await StartAsync(runner, replacement).ConfigureAwait(false);
        });
    }

    // ----- sources -----

    /// <summary>Defines a source (sources are global, spec 9.4) and starts it. Secrets go to the secret store, never to sources.json.</summary>
    /// <param name="silenceLimitSeconds">The source's <see cref="SourceDefinition.SilenceLimitSeconds"/>; null for the default.</param>
    public Task<SourceDefinition> AddSourceAsync(string name, string typeId, bool enabled, bool countFilesOnStart, object settings, int? silenceLimitSeconds = null) => MutateAsync(() =>
    {
        name = RequireName(name, "A source");
        RequireUniqueSourceName(name, null);
        var plugin = Registry.FindSource(typeId) ?? throw new ArgumentException($"There is no source type '{typeId}'.");
        var definition = new SourceDefinition { Name = name, Type = plugin.TypeId, Enabled = enabled, CountFilesOnStart = countFilesOnStart };
        if (silenceLimitSeconds is { } silence)
        {
            definition.SilenceLimitSeconds = Math.Clamp(silence, 0, SourceDefinition.MostSilenceLimitSeconds);
        }

        plugin.NormaliseSettings(settings);
        definition.Settings = Serialise(plugin.SettingsType, definition.Id, settings, out _);
        Settings.Sources.Add(definition);
        Settings.SaveSources();

        if (_started)
        {
            var runner = CreateSourceRunner(definition);
            Volatile.Write(ref _sources, [.. Sources, runner]);
            StartInTurn(runner);
        }

        _logger.LogInformation("source {Source} ({Type}) added", name, plugin.TypeId);
        OnChanged();
        return definition;
    });

    /// <summary>Edits a source wherever it's used; it restarts when its settings changed (spec 11.1).</summary>
    /// <param name="silenceLimitSeconds">The source's <see cref="SourceDefinition.SilenceLimitSeconds"/>; null leaves it as it is. It takes effect at the next call, with no restart.</param>
    public Task UpdateSourceAsync(Guid id, string name, bool enabled, bool countFilesOnStart, object settings, int? silenceLimitSeconds = null) => MutateAsync(() =>
    {
        var definition = Settings.FindSource(id) ?? throw new ArgumentException("That source no longer exists.");
        name = RequireName(name, "A source");
        RequireUniqueSourceName(name, id);
        var plugin = Registry.FindSource(definition.Type) ?? throw new ArgumentException($"The source type '{definition.Type}' is not available, so its settings can't be edited.");

        plugin.NormaliseSettings(settings);
        var json = Serialise(plugin.SettingsType, id, settings, out var secretsChanged);
        var settingsChanged = secretsChanged || !JsonNode.DeepEquals(definition.Settings, json);
        var enabledChanged = definition.Enabled != enabled;
        var countChanged = definition.CountFilesOnStart != countFilesOnStart;
        var renamed = !definition.Name.Equals(name, StringComparison.Ordinal);

        definition.Name = name;
        definition.Enabled = enabled;
        definition.CountFilesOnStart = countFilesOnStart;
        if (silenceLimitSeconds is { } silence)
        {
            definition.SilenceLimitSeconds = Math.Clamp(silence, 0, SourceDefinition.MostSilenceLimitSeconds);
        }

        definition.Settings = json;
        Settings.SaveSources();

        if (_started && FindSourceRunner(id) is { } runner)
        {
            runner.Handle.Name = name;
            if (settingsChanged || countChanged || (runner.Plugin is null && plugin is not null))
            {
                ReplaceInTurn(runner, definition);
            }
            else if (enabledChanged)
            {
                if (enabled)
                {
                    StartInTurn(runner);
                }
                else
                {
                    StopInTurn(runner);
                }
            }

            if (enabledChanged || renamed)
            {
                RefreshCollectionsUsing(id);
            }
        }

        _logger.LogInformation("source {Source} updated ({What})", name, settingsChanged ? "settings changed, restarted" : enabledChanged ? (enabled ? "enabled" : "disabled") : "no restart needed");
        OnChanged();
    });

    /// <summary>The Enabled checkbox: a disabled source is absent from every tree; enabling starts it at once (spec 4.1, 6.4).</summary>
    public Task SetSourceEnabledAsync(Guid id, bool enabled) => MutateAsync(() =>
    {
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
                StartInTurn(runner);
            }
            else
            {
                StopInTurn(runner);
            }

            RefreshCollectionsUsing(id);
        }

        OnChanged();
    });

    /// <summary>The Retry item: start again at once, forgetting the backoff.</summary>
    public Task RetrySourceAsync(Guid id) => MutateAsync(() =>
    {
        if (FindSourceRunner(id) is { } runner && runner.Definition.Enabled)
        {
            RestartInTurn(runner);
            OnChanged();
        }
    });

    /// <summary>
    /// Deletes the definition everywhere: memberships, secrets, per-source state and cache. The definition goes at once;
    /// what the source kept goes when it has stopped, with what it kept as it stopped.
    /// </summary>
    public Task DeleteSourceAsync(Guid id) => MutateAsync(() =>
    {
        var definition = Settings.FindSource(id) ?? throw new ArgumentException("That source no longer exists.");
        var runner = FindSourceRunner(id);
        if (runner is not null)
        {
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
        InTurn(id, $"source '{definition.Name}'", async () =>
        {
            if (runner is not null)
            {
                await runner.StopAsync().ConfigureAwait(false);
            }

            Operations.Forget(id);
            ForgetSecrets(id);
            DeleteQuietly(Paths.SourceState(id));
            DeleteQuietly(Paths.SourceCache(id));
        });
        _logger.LogInformation("source {Source} deleted", definition.Name);
        OnChanged();
    });

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

    private Task ImportNewAsync(SourceDefinition source, bool keepId) => MutateAsync(() =>
    {
        var definition = SourceTransfer.Copy(source);

        // an id is not free while the source that had it still stops: its folders and secrets go when it has
        if (!keepId || definition.Id == Guid.Empty || Settings.FindSource(definition.Id) is not null || (_lanes.TryGetValue(definition.Id, out var turn) && turn.Busy))
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
            StartInTurn(runner);
        }

        _logger.LogInformation("source {Source} ({Type}) imported", definition.Name, definition.Type);
        OnChanged();
    });

    private Task ReplaceImportedAsync(Guid id, SourceDefinition source) => MutateAsync(() =>
    {
        var definition = Settings.FindSource(id) ?? throw new ArgumentException("That source no longer exists.");
        var incoming = SourceTransfer.Copy(source);
        incoming.Id = id;
        definition.Name = UniqueSourceName(RequireName(incoming.Name, "A source"), id);
        definition.Type = incoming.Type;
        definition.Enabled = incoming.Enabled;
        definition.CountFilesOnStart = incoming.CountFilesOnStart;
        definition.SilenceLimitSeconds = incoming.SilenceLimitSeconds;
        definition.Settings = RehomeSecrets(incoming);
        Settings.SaveSources();

        if (_started && FindSourceRunner(id) is { } runner)
        {
            runner.Handle.Name = definition.Name;
            ReplaceInTurn(runner, definition);
            RefreshCollectionsUsing(id);
        }

        _logger.LogInformation("source {Source} replaced from an import", definition.Name);
        OnChanged();
    });

    /// <summary>
    /// A secret written into sources.json or collections.json by hand, or by a script (the E2E lab's settings), moves
    /// to the secret store on the next load and the file is rewritten without it (spec 9.3): the files stay
    /// shareable, and the value still reaches the plugin. The same as Import does for a sources file.
    /// </summary>
    private void RehomeSecretsAtLoad()
    {
        var sourcesChanged = 0;
        foreach (var source in Settings.Sources)
        {
            if (Registry.FindSource(source.Type) is { } plugin && HasSecretsInJson(plugin.SettingsType, source.Settings))
            {
                source.Settings = RehomeSecrets(source);
                sourcesChanged++;
            }
        }

        var mountsChanged = 0;
        foreach (var mount in Settings.Collections.SelectMany(c => c.Mounts))
        {
            if (Registry.FindMountProvider(mount.Provider) is { } provider && HasSecretsInJson(provider.SettingsType, mount.Settings))
            {
                mount.Settings = Rehome(provider.SettingsType, mount.Id, mount.Settings);
                mountsChanged++;
            }
        }

        if (sourcesChanged > 0)
        {
            Settings.SaveSources();
            _logger.LogInformation("secrets found in sources.json for {Count} source(s) were moved to the secret store and removed from the file", sourcesChanged);
        }

        if (mountsChanged > 0)
        {
            Settings.SaveCollections();
            _logger.LogInformation("secrets found in collections.json for {Count} mount(s) were moved to the secret store and removed from the file", mountsChanged);
        }
    }

    private static bool HasSecretsInJson([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] Type settingsType, JsonObject? settings)
        => settings is not null && PluginSettingsBinder.Describe(settingsType).Any(p => p.IsSecret && (settings.ContainsKey(p.JsonName) || settings.ContainsKey(p.Name)));

    /// <summary>A sources file normally carries no secrets; one that does (hand-written) has them moved to the secret store here.</summary>
    private JsonObject? RehomeSecrets(SourceDefinition definition)
        => Registry.FindSource(definition.Type) is { } plugin ? Rehome(plugin.SettingsType, definition.Id, definition.Settings) : definition.Settings;

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

    public Task<CollectionDefinition> AddCollectionAsync(string name) => MutateAsync(() =>
    {
        name = RequireCollectionName(name, null);
        var definition = new CollectionDefinition { Name = name };
        Settings.Collections.Add(definition);
        Settings.SaveCollections();
        if (_started && (_filter is null || _filter.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            var runner = CreateCollectionRunner(definition);
            Volatile.Write(ref _collections, [.. Collections, runner]);
            StartInTurn(runner);
        }

        _logger.LogInformation("collection {Collection} added", name);
        OnChanged();
        return definition;
    });

    /// <summary>Renaming restarts the collection's mounts: the prefix, URL or export changes (spec 8.8).</summary>
    public Task RenameCollectionAsync(Guid id, string name) => MutateAsync(() =>
    {
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
            InTurn(id, Describe(runner), async () =>
            {
                if (!_stopped && Collections.Contains(runner))
                {
                    await runner.RestartMountsAsync(_shutdown.Token).ConfigureAwait(false);
                }
            });
        }

        _logger.LogInformation("collection {Old} renamed to {New}", old, name);
        OnChanged();
    });

    public Task SetCollectionEnabledAsync(Guid id, bool enabled) => MutateAsync(() =>
    {
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
                        StartInTurn(source);
                    }
                }

                StartInTurn(runner);
            }
            else
            {
                InTurn(id, Describe(runner), runner.StopAsync);
            }
        }

        OnChanged();
    });

    /// <summary>
    /// Deletes the collection and its mounts; its sources stay defined (the window asks about orphans, spec 9.4). The
    /// collection goes at once; what its mounts kept goes when they have stopped.
    /// </summary>
    public Task DeleteCollectionAsync(Guid id) => MutateAsync(() =>
    {
        var definition = FindCollectionDefinition(id);
        var runner = FindCollectionRunner(id);
        if (runner is not null)
        {
            Volatile.Write(ref _collections, Collections.Where(c => !ReferenceEquals(c, runner)).ToArray());
        }

        Settings.Collections.Remove(definition);
        Settings.SaveCollections();
        Stats.RemoveCollection(definition.Name);
        var mounts = definition.Mounts.Select(m => m.Id).ToArray();
        InTurn(id, $"collection '{definition.Name}'", async () =>
        {
            if (runner is not null)
            {
                await runner.StopAsync().ConfigureAwait(false);
            }

            foreach (var mount in mounts)
            {
                ForgetSecrets(mount);
                DeleteQuietly(Paths.MountState(mount));
            }
        });
        _logger.LogInformation("collection {Collection} deleted", definition.Name);
        OnChanged();
    });

    // ----- memberships (a source's place in a collection) -----

    public Task AddMembershipAsync(Guid collectionId, Guid sourceId, string path, bool readOnly) => MutateAsync(() =>
    {
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
                StartInTurn(sourceRunner);
            }
        }

        OnChanged();
    });

    /// <summary>The path and Read-only of a source in one collection only (spec 4.2, 11.1).</summary>
    public Task UpdateMembershipAsync(Guid collectionId, Guid sourceId, string path, bool readOnly) => MutateAsync(() =>
    {
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
    });

    public Task RemoveMembershipAsync(Guid collectionId, Guid sourceId) => MutateAsync(() =>
    {
        var collection = FindCollectionDefinition(collectionId);
        if (collection.Sources.RemoveAll(m => m.Id == sourceId) == 0)
        {
            return;
        }

        Settings.SaveCollections();
        FindCollectionRunner(collectionId)?.RemoveSource(sourceId);
        if (_filter is not null && FindSourceRunner(sourceId) is { } runner && !IsNeeded(sourceId))
        {
            InTurn(sourceId, Describe(runner), async () =>
            {
                await runner.StopAsync().ConfigureAwait(false);
                runner.Handle.Status = new ComponentStatus(ComponentState.Disabled, "Not in a started collection");
            });
        }

        OnChanged();
    });

    // ----- mounts -----

    public Task<MountDefinition> AddMountAsync(Guid collectionId, string name, string providerId, bool enabled, bool readOnly, object settings) => MutateAsync(() =>
    {
        var collection = FindCollectionDefinition(collectionId);
        var provider = Registry.FindMountProvider(providerId) ?? throw new ArgumentException($"There is no mount provider '{providerId}'.");
        name = RequireMountName(collection, name, null);
        var definition = new MountDefinition { Name = name, Provider = provider.TypeId, Enabled = enabled, ReadOnly = readOnly || !provider.SupportsWrites };
        definition.Settings = Serialise(provider.SettingsType, definition.Id, settings, out _);
        collection.Mounts.Add(definition);
        Settings.SaveCollections();
        if (FindCollectionRunner(collectionId) is { } runner)
        {
            var mount = CreateMountRunner(definition, runner);
            runner.AddMount(mount);
            InTurn(collectionId, $"mount '{name}' of '{collection.Name}'", () => StartAsync(runner, mount));
        }

        _logger.LogInformation("mount {Mount} ({Provider}) added to {Collection}", name, provider.TypeId, collection.Name);
        OnChanged();
        return definition;
    });

    /// <summary>Editing a mount restarts it (spec 8.8); a name-only edit doesn't.</summary>
    public Task UpdateMountAsync(Guid collectionId, Guid mountId, string name, bool enabled, bool readOnly, object settings) => MutateAsync(() =>
    {
        var collection = FindCollectionDefinition(collectionId);
        var definition = collection.Mounts.FirstOrDefault(m => m.Id == mountId) ?? throw new ArgumentException("That mount no longer exists.");
        var provider = Registry.FindMountProvider(definition.Provider) ?? throw new ArgumentException($"The mount provider '{definition.Provider}' is not available, so its settings can't be edited.");
        name = RequireMountName(collection, name, mountId);
        readOnly |= !provider.SupportsWrites;

        var json = Serialise(provider.SettingsType, mountId, settings, out var secretsChanged);
        var restart = secretsChanged || !JsonNode.DeepEquals(definition.Settings, json) || definition.ReadOnly != readOnly || definition.Enabled != enabled;
        definition.Name = name;
        definition.Enabled = enabled;
        definition.ReadOnly = readOnly;
        definition.Settings = json;
        Settings.SaveCollections();

        if (restart && FindCollectionRunner(collectionId) is { } runner && runner.Mounts.FirstOrDefault(m => m.Definition.Id == mountId) is { } old)
        {
            ReplaceInTurn(runner, old, definition);
        }

        _logger.LogInformation("mount {Mount} of {Collection} updated ({What})", name, collection.Name, restart ? "restarted" : "no restart needed");
        OnChanged();
    });

    /// <summary>Stops and starts a mount again now (after a failure, or after its provider was installed).</summary>
    public Task RetryMountAsync(Guid collectionId, Guid mountId) => MutateAsync(() =>
    {
        if (FindCollectionRunner(collectionId) is { } runner && runner.Mounts.FirstOrDefault(m => m.Definition.Id == mountId) is { } old && old.Definition.Enabled)
        {
            ReplaceInTurn(runner, old, old.Definition);
            OnChanged();
        }
    });

    public Task SetMountEnabledAsync(Guid collectionId, Guid mountId, bool enabled) => MutateAsync(() =>
    {
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
            InTurn(collectionId, $"mount '{definition.Name}' of '{collection.Name}'", enabled ? () => StartAsync(runner, mount) : mount.StopAsync);
        }

        OnChanged();
    });

    /// <summary>Deletes a mount. It goes at once; what it kept goes when it has stopped.</summary>
    public Task DeleteMountAsync(Guid collectionId, Guid mountId) => MutateAsync(() =>
    {
        var collection = FindCollectionDefinition(collectionId);
        var definition = collection.Mounts.FirstOrDefault(m => m.Id == mountId) ?? throw new ArgumentException("That mount no longer exists.");
        var mount = FindCollectionRunner(collectionId) is { } runner ? runner.Mounts.FirstOrDefault(m => m.Definition.Id == mountId) : null;
        if (mount is not null)
        {
            FindCollectionRunner(collectionId)!.RemoveMount(mount);
        }

        collection.Mounts.Remove(definition);
        Settings.SaveCollections();
        InTurn(collectionId, $"mount '{definition.Name}' of '{collection.Name}'", async () =>
        {
            if (mount is not null)
            {
                await mount.StopAsync().ConfigureAwait(false);
            }

            ForgetSecrets(mountId);
            DeleteQuietly(Paths.MountState(mountId));
        });
        _logger.LogInformation("mount {Mount} of {Collection} deleted", definition.Name, collection.Name);
        OnChanged();
    });

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
                settings = Materialise(plugin.SettingsType, definition.Id, definition.Settings);
            }
            catch (Exception ex)
            {
                error = $"Settings could not be read: {ex.GetBaseException().Message}";
            }
        }

        return new SourceRunner(definition, plugin, settings, error, handle, Paths, plugin is null ? _secrets : Secrets, _prompts, _loggerFactory.CreateLogger($"AnyFS.Source.{definition.Name}"), _time, Operations);
    }

    /// <summary>A failed or slow operation is worth a debug line: with the log at debug, a sluggish mount says which source and call.</summary>
    private void OnOperationFinished(Operation ended)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var op = Operations.Snapshot(ended);
        if (op.Outcome == OperationOutcome.Failed)
        {
            _logger.LogDebug("source {Source}: {Operation} '{Path}' failed after {Seconds:F2} s: {Error}", op.Source, op.What ?? op.Kind.Describe().ToLowerInvariant(), op.Path, op.Duration.TotalSeconds, op.Error);
        }
        else if (op.Duration >= SlowOperation)
        {
            _logger.LogDebug("source {Source}: {Operation} '{Path}' took {Seconds:F1} s ({Outcome})", op.Source, op.What ?? op.Kind.Describe().ToLowerInvariant(), op.Path, op.Duration.TotalSeconds, op.Outcome.ToString().ToLowerInvariant());
        }
    }

    private CollectionRunner CreateCollectionRunner(CollectionDefinition definition)
    {
        var vfs = new CollectionVfs(definition.Id, definition.Name, Stats, _loggerFactory.CreateLogger($"AnyFS.Collection.{definition.Name}"));
        var members = definition.Sources
            .Where(m => _handles.ContainsKey(m.Id))
            .Select(m => new CollectionSource(_handles[m.Id], m, Settings.FindSource(m.Id)!))
            .ToList();
        // the mounts know the collection through its runner, whose name follows a rename, as the mounts added later do: given
        // a copy of the name, a mount restarted for the rename came back under the old one (the user, 4-Nov-2026)
        var runner = new CollectionRunner(definition, vfs, members, [], _loggerFactory.CreateLogger($"AnyFS.Collection.{definition.Name}"), _time);
        foreach (var mount in definition.Mounts)
        {
            runner.AddMount(CreateMountRunner(mount, runner));
        }

        return runner;
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
                settings = Materialise(provider.SettingsType, definition.Id, definition.Settings);
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

    /// <summary>Whether something started needs a source: every source without a filter. Asked with the mutations' lock held.</summary>
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

    /// <summary>
    /// Deletes a source's folder, trying again for a moment while a file in it is still held (an antivirus scanning what the
    /// source wrote as it stopped); what is still held then is left behind, which nothing reads again.
    /// </summary>
    private static void DeleteQuietly(string directory)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 4)
            {
                Thread.Sleep(50 * attempt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
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
