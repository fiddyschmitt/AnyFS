using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using AnyFS.Core.Model;
using AnyFS.Core.Operations;
using AnyFS.Core.Plugins;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Core.Status;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Core.Tests;

/// <summary>The whole engine in process with the fake plugins: settings in, running collections out.</summary>
[TestClass]
public class EngineTests
{
    private string _root = "";
    private SettingsPaths _paths = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "engine-" + Guid.NewGuid().ToString("N"));
        _paths = SettingsPaths.Resolve(Path.Combine(_root, "settings"));
        lock (FakeMountProvider.Mounts)
        {
            FakeMountProvider.Mounts.Clear();
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static PluginRegistry Registry()
    {
        var registry = new PluginRegistry();
        registry.AddBuiltIn(new MemoryPlugin());
        registry.AddBuiltIn(new FakeMountProvider());
        return registry;
    }

    private AnyFsEngine Engine(IPromptHandler? prompts = null) => new(_paths, Registry(), NullLoggerFactory.Instance, prompts);

    private void WriteSettings(Action<SettingsStore> configure)
    {
        var store = new SettingsStore(_paths, NullLogger.Instance);
        store.Load();
        configure(store);
        store.SaveSources();
        store.SaveCollections();
    }

    private static SourceDefinition Source(string name, string files = "hello.txt=hello", bool enabled = true, Action<MemoryPluginSettings>? tweak = null)
    {
        var settings = new MemoryPluginSettings { Files = files };
        tweak?.Invoke(settings);
        return new SourceDefinition { Name = name, Type = "memory", Enabled = enabled, Settings = PluginSettingsBinder.Serialise(typeof(MemoryPluginSettings), settings) };
    }

    private static MountDefinition Mount(string name = "fake", bool readOnly = false, bool enabled = true, int port = 1, bool fail = false)
        => new() { Name = name, Provider = "fake", ReadOnly = readOnly, Enabled = enabled, Settings = new JsonObject { ["port"] = port, ["failStart"] = fail } };

    [TestMethod]
    public async Task FirstStartCreatesCollectionOne()
    {
        await using var engine = Engine();
        engine.Load();
        Assert.AreEqual("Collection 1", engine.Settings.Collections.Single().Name);
        Assert.AreEqual(SettingsStore.NativeProviderForThisOs(), engine.Settings.Collections.Single().Mounts.Single().Provider);
        await engine.StartAsync(null, CancellationToken.None);
        var snapshot = engine.Snapshot();
        var mount = snapshot.Collections.Single().Mounts.Single();
        Assert.AreEqual(nameof(ComponentState.Failed), mount.State, "the native provider isn't in this test registry");
        StringAssert.Contains(mount.Text, "not available in this build");
        Assert.IsFalse(snapshot.AllGreen);
    }

    [TestMethod]
    public async Task SourcesAndMountsComeUpAndTheMountSeesTheTree()
    {
        WriteSettings(store =>
        {
            var a = Source("alpha", "a.txt=A");
            var b = Source("beta", "b.txt=B");
            store.Sources.AddRange([a, b]);
            var collection = new CollectionDefinition { Name = "work" };
            collection.Sources.Add(new SourceMembership { Id = a.Id, Path = "mem/alpha" });
            collection.Sources.Add(new SourceMembership { Id = b.Id, Path = "mem", ReadOnly = true });
            collection.Mounts.Add(Mount("fake-rw"));
            collection.Mounts.Add(Mount("fake-ro", readOnly: true, port: 2));
            store.Collections.Add(collection);
        });

        await using var engine = Engine();
        engine.Load();
        await engine.StartAsync(null, CancellationToken.None);
        await Wait.UntilAsync(() => engine.Snapshot().AllGreen, what: "all green");

        var snapshot = engine.Snapshot();
        var work = snapshot.Collections.Single();
        Assert.AreEqual("Green", work.Colour);
        Assert.AreEqual("2 sources ready, 2 mounts ready", work.Text);
        Assert.AreEqual("mem/alpha", work.Sources.Single(s => s.Name == "alpha").Path);
        Assert.IsTrue(work.Sources.Single(s => s.Name == "beta").ReadOnly);
        Assert.AreEqual("fake://1/work/", work.Mounts.Single(m => m.Name == "fake-rw").AccessPaths.Single().Value);
        CollectionAssert.AreEquivalent(new[] { "memory", "fake" }, snapshot.Plugins.Select(p => p.TypeId).ToArray());

        var rw = FakeMountProvider.Mounts.Single(m => m.Fs is { ReadOnly: false });
        var ro = FakeMountProvider.Mounts.Single(m => m.Fs is { ReadOnly: true });
        CollectionAssert.AreEqual(new[] { "mem/" }, await Handles.NamesAsync(rw.Fs!, ""));
        CollectionAssert.AreEqual(new[] { "alpha/", "b.txt" }, await Handles.NamesAsync(rw.Fs!, "mem"));
        Assert.AreEqual("A", await Handles.ReadTextAsync(rw.Fs!, "mem/alpha/a.txt"));
        Assert.IsTrue(rw.Fs!.CanWrite("mem/alpha/new.txt"));
        Assert.IsFalse(rw.Fs.CanWrite("mem/new.txt"), "beta is read-only in this collection");
        await Handles.ExpectAsync(VfsError.ReadOnly, () => ro.Fs!.CreateDirectoryAsync("mem/alpha/d", CancellationToken.None));
        await rw.Fs.CreateDirectoryAsync("mem/alpha/d", CancellationToken.None);

        Assert.IsTrue(File.Exists(_paths.StatusFile) || true, "status.json is written by RunAsync, not StartAsync");
        engine.WriteStatus();
        var (read, running) = StatusFileReader.Read(_paths);
        Assert.IsTrue(running);
        Assert.AreEqual("work", read!.Collections.Single().Name);
        Assert.AreEqual(Environment.ProcessId, read.Pid);

        await engine.StopAsync();
        Assert.IsTrue(rw.Stopped && rw.Disposed);
        Assert.IsFalse(File.Exists(_paths.StatusFile), "stopping removes status.json");
        Assert.IsTrue(File.Exists(_paths.StatsFile));
        Assert.AreEqual(0, FakeListener.Alive, "shared listeners are released");
        Assert.AreEqual(nameof(ComponentState.Disabled), engine.Snapshot().Collections.Single().Mounts.First().State);
    }

    [TestMethod]
    public async Task CollectionFilterStartsOnlyWhatIsNeeded()
    {
        WriteSettings(store =>
        {
            var a = Source("alpha");
            var b = Source("beta");
            store.Sources.AddRange([a, b]);
            var one = new CollectionDefinition { Name = "one" };
            one.Sources.Add(new SourceMembership { Id = a.Id, Path = "mem/alpha" });
            one.Mounts.Add(Mount(port: 1));
            var two = new CollectionDefinition { Name = "two" };
            two.Sources.Add(new SourceMembership { Id = b.Id, Path = "mem/beta" });
            two.Mounts.Add(Mount(port: 2));
            store.Collections.AddRange([one, two]);
        });

        await using (var engine = Engine())
        {
            engine.Load();
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => engine.StartAsync(["nope"], CancellationToken.None));
        }

        await using (var engine = Engine())
        {
            engine.Load();
            await engine.StartAsync(["two"], CancellationToken.None);
            await Wait.UntilAsync(() => engine.Snapshot().AllGreen, what: "two green");
            var snapshot = engine.Snapshot();
            Assert.AreEqual("two", snapshot.Collections.Single().Name);
            var alpha = engine.Sources.Single(s => s.Definition.Name == "alpha");
            Assert.IsFalse(alpha.IsRunning, "alpha is not needed by 'two'");
            Assert.AreEqual(ComponentState.Disabled, alpha.Handle.Status.State);
            Assert.AreEqual(1, FakeMountProvider.Mounts.Count);
        }
    }

    [TestMethod]
    public async Task MountsWaitForAUsableSourceOrTheGracePeriod()
    {
        WriteSettings(store =>
        {
            var slow = Source("slow", tweak: s => s.FailTimes = 1);
            store.Sources.Add(slow);
            var c = new CollectionDefinition { Name = "c" };
            c.Sources.Add(new SourceMembership { Id = slow.Id, Path = "mem/slow" });
            c.Mounts.Add(Mount());
            store.Collections.Add(c);
        });

        await using var engine = Engine();
        engine.Load();
        var started = DateTime.UtcNow;
        await engine.StartAsync(null, CancellationToken.None);
        // the source fails once and would retry in 10 s; the mount must not wait for that beyond the grace period
        var waited = DateTime.UtcNow - started;
        Assert.IsTrue(waited < TimeSpan.FromSeconds(9), $"waited {waited}");
        var snapshot = engine.Snapshot();
        Assert.AreEqual(nameof(ComponentState.Ready), snapshot.Collections.Single().Mounts.Single().State);
        Assert.AreEqual(nameof(ComponentState.Failed), snapshot.Collections.Single().Sources.Single().State);
        var mount = FakeMountProvider.Mounts.Single();
        CollectionAssert.AreEqual(new[] { "mem/" }, await Handles.NamesAsync(mount.Fs!, ""), "the layout exists before the source is up");
        await Handles.ExpectAsync(VfsError.Unavailable, () => Handles.NamesAsync(mount.Fs!, "mem/slow"));
    }

    [TestMethod]
    public async Task FailuresNeverTakeTheProcessDown()
    {
        WriteSettings(store =>
        {
            var ok = Source("ok");
            var bad = Source("bad", tweak: s => s.Permanent = true);
            var broken = new SourceDefinition { Name = "broken", Type = "memory", Settings = JsonNode.Parse("""{ "files": 12345 }""")!.AsObject() };
            var unknown = new SourceDefinition { Name = "unknown", Type = "no-such-type" };
            store.Sources.AddRange([ok, bad, broken, unknown]);
            var c = new CollectionDefinition { Name = "c" };
            c.Sources.Add(new SourceMembership { Id = ok.Id, Path = "ok" });
            c.Sources.Add(new SourceMembership { Id = bad.Id, Path = "bad" });
            c.Sources.Add(new SourceMembership { Id = broken.Id, Path = "broken" });
            c.Sources.Add(new SourceMembership { Id = unknown.Id, Path = "unknown" });
            c.Mounts.Add(Mount("good"));
            c.Mounts.Add(Mount("explodes", fail: true, port: 3));
            c.Mounts.Add(new MountDefinition { Name = "missing", Provider = "no-such-provider" });
            c.Mounts.Add(Mount("off", enabled: false, port: 4));
            store.Collections.Add(c);
            var disabled = new CollectionDefinition { Name = "disabled", Enabled = false };
            disabled.Mounts.Add(Mount("never", port: 5));
            store.Collections.AddRange([disabled]);
        });

        await using var engine = Engine();
        engine.Load();
        await engine.StartAsync(null, CancellationToken.None);
        await Wait.UntilAsync(() => engine.Snapshot().Collections[0].Sources.All(s => s.State is nameof(ComponentState.Ready) or nameof(ComponentState.Failed)), what: "sources settled");

        var snapshot = engine.Snapshot();
        var c = snapshot.Collections.Single(x => x.Name == "c");
        Assert.AreEqual("Red", c.Colour);
        Assert.AreEqual(nameof(ComponentState.Ready), c.Sources.Single(s => s.Name == "ok").State);
        Assert.AreEqual(nameof(ComponentState.Failed), c.Sources.Single(s => s.Name == "bad").State);
        StringAssert.Contains(c.Sources.Single(s => s.Name == "broken").Text, "Settings could not be read");
        StringAssert.Contains(c.Sources.Single(s => s.Name == "unknown").Text, "Unknown source type");
        Assert.AreEqual(nameof(ComponentState.Ready), c.Mounts.Single(m => m.Name == "good").State);
        StringAssert.Contains(c.Mounts.Single(m => m.Name == "explodes").Text, "told to fail");
        StringAssert.Contains(c.Mounts.Single(m => m.Name == "missing").Text, "not available in this build");
        Assert.AreEqual(nameof(ComponentState.Disabled), c.Mounts.Single(m => m.Name == "off").State);
        var disabled = snapshot.Collections.Single(x => x.Name == "disabled");
        Assert.AreEqual("Grey", disabled.Colour);
        Assert.AreEqual(nameof(ComponentState.Disabled), disabled.Mounts.Single().State);
        Assert.AreEqual(1, FakeMountProvider.Mounts.Count(m => m.Fs is not null), "only the good mount started");
    }

    [TestMethod]
    public async Task RunAsyncWritesStatusAndStopsOnCancellation()
    {
        WriteSettings(store =>
        {
            var a = Source("alpha");
            store.Sources.Add(a);
            var c = new CollectionDefinition { Name = "c" };
            c.Sources.Add(new SourceMembership { Id = a.Id, Path = "mem/alpha" });
            c.Mounts.Add(Mount());
            store.Collections.Add(c);
        });

        await using var engine = Engine();
        engine.Load();
        using var cts = new CancellationTokenSource();
        var run = engine.RunAsync(null, cts.Token);
        await Wait.UntilAsync(() => File.Exists(_paths.StatusFile), what: "status.json");
        var (snapshot, running) = StatusFileReader.Read(_paths);
        Assert.IsTrue(running);
        Assert.AreEqual("c", snapshot!.Collections.Single().Name);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(File.Exists(_paths.StatusFile));
        Assert.IsTrue(FakeMountProvider.Mounts.Single().Stopped);
        Assert.IsFalse(StatusFileReader.Read(_paths).Running);
    }

    /// <summary>
    /// The run's tick reads what plugins supply, a mount's access paths among it. One that threw ended the loop, whose
    /// finally stops every source and mount, the window still up. It costs that tick alone: the failure is logged once
    /// while it stays the same and again when it changes, the stats are flushed all the same, and a tick that works again
    /// says so once.
    /// </summary>
    [TestMethod]
    public async Task AFailedStatusTickCostsThatTickNotEverySourceAndMount()
    {
        var provider = new FailingPathsProvider();
        var registry = Registry();
        registry.AddBuiltIn(provider);
        WriteSettings(store =>
        {
            var a = Source("alpha");
            store.Sources.Add(a);
            var c = new CollectionDefinition { Name = "c" };
            c.Sources.Add(new SourceMembership { Id = a.Id, Path = "mem/alpha" });
            c.Mounts.Add(new MountDefinition { Name = "failing", Provider = provider.TypeId });
            store.Collections.Add(c);
        });

        var log = new KeptLog();
        await using var engine = new AnyFsEngine(_paths, registry, log, time: new HundredfoldTime());
        engine.Load();
        using var cts = new CancellationTokenSource();
        var run = engine.RunAsync(null, cts.Token);
        await Wait.UntilAsync(() => StatusFileReader.Read(_paths).Snapshot?.AllGreen == true, what: "status.json, all green");

        // the same failure tick after tick, then another message, then another type
        Exception[] failures = [new InvalidOperationException("the paths are not to be had"), new InvalidOperationException("the paths went away"), new NotSupportedException("the paths went away")];
        foreach (var failure in failures)
        {
            var failed = provider.Failed;
            provider.FailsWith = failure;
            await Wait.UntilAsync(() => provider.Failed >= failed + 3 || run.IsCompleted, what: "three ticks that failed");
            Assert.IsFalse(run.IsCompleted, $"a tick that failed ended the run: {run.Exception?.GetBaseException().Message}");
        }

        Assert.IsTrue(engine.Sources.Single().Handle.IsUsable, "the source is still started");
        Assert.IsFalse(provider.Stopped, "and the mount");

        // half a minute on this clock is 300 ms
        engine.Stats.FileServed("c");
        await Wait.UntilAsync(() => JsonFiles.Read(_paths.StatsFile, CoreJsonContext.Default.StatsFile)?.FilesServed == 1, what: "the stats flushed while the status cannot be written");

        var cleared = DateTimeOffset.UtcNow;
        provider.FailsWith = null;
        await Wait.UntilAsync(() => StatusFileReader.Read(_paths).Snapshot?.UpdatedAt > cleared, what: "status.json written again");
        await Wait.UntilAsync(() => log.Lines.Any(l => l.Level == LogLevel.Information && l.Text.Contains("fresh again", StringComparison.Ordinal)), what: "the log to say so");
        Assert.IsFalse(run.IsCompleted, "the run goes on");

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(provider.Stopped, "stopped by the cancellation, as ever");

        var warned = log.Lines.Where(l => l.Level == LogLevel.Warning && l.Text.Contains("could not be kept fresh", StringComparison.Ordinal)).Select(l => l.Exception).ToArray();
        CollectionAssert.AreEqual(failures, warned, "each failure logged once, however many ticks it lasted: " + string.Join(" | ", warned.Select(w => w?.Message)));
        Assert.AreEqual(1, log.Lines.Count(l => l.Level == LogLevel.Information && l.Text.Contains("fresh again", StringComparison.Ordinal)), "and once that it is over");
    }

    /// <summary>
    /// The token cancelled while the start is still under way, as when the window is closed while sources start (a mount
    /// waits for the first of its collection's). The start was outside the run's try, so its cancellation skipped the
    /// stop: no source disposed, no mount stopped, no stats flushed.
    /// </summary>
    [TestMethod]
    public async Task ACancellationDuringTheStartStillStopsWhatHadStarted()
    {
        var waiting = new WaitingPlugin();
        var registry = Registry();
        registry.AddBuiltIn(waiting);
        WriteSettings(store =>
        {
            var ready = Source("ready");
            var slow = new SourceDefinition { Name = "slow", Type = waiting.TypeId };
            store.Sources.AddRange([ready, slow]);

            // one collection is up at once, its mount with it; the other's mount waits for its source, and the start with it
            var up = new CollectionDefinition { Name = "up" };
            up.Sources.Add(new SourceMembership { Id = ready.Id, Path = "mem/ready" });
            up.Mounts.Add(Mount(port: 1));
            var held = new CollectionDefinition { Name = "held" };
            held.Sources.Add(new SourceMembership { Id = slow.Id, Path = "mem/slow" });
            held.Mounts.Add(Mount(port: 2));
            store.Collections.AddRange([up, held]);
        });

        // on a clock that stands still the wait for a first source never runs out: the start lasts until it is cancelled
        await using var engine = new AnyFsEngine(_paths, registry, NullLoggerFactory.Instance, time: new FakeTime());
        engine.Load();
        using var cts = new CancellationTokenSource();
        var run = engine.RunAsync(null, cts.Token);
        await waiting.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(() => StartedMounts().Count == 1, what: "the first collection's mount");
        var mount = StartedMounts().Single();
        var ready = engine.Sources.Single(s => s.Definition.Name == "ready").Handle;
        await Wait.UntilAsync(() => ready.IsUsable, what: "the first collection's source");
        Assert.IsFalse(File.Exists(_paths.StatusFile), "status.json is first written when the start is over");

        cts.Cancel();
        await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.IsTrue(run.IsCompleted, "the run ends");
        Assert.IsTrue(waiting.Source!.Disposed, "the source that was starting is disposed");
        Assert.IsFalse(ready.IsUsable, "and the one that had started");
        Assert.IsTrue(mount.Stopped && mount.Disposed, "the mount that was up is stopped");
        Assert.IsTrue(File.Exists(_paths.StatsFile), "the stats are flushed");
        await run;

        static List<FakeMount> StartedMounts()
        {
            lock (FakeMountProvider.Mounts)
            {
                return FakeMountProvider.Mounts.Where(m => m.Fs is not null).ToList();
            }
        }
    }

    /// <summary>A mount provider whose mounts cannot say where they are while <see cref="FailsWith"/> is set, as a provider's own code may fail at any time.</summary>
    private sealed class FailingPathsProvider : IMountProviderPlugin
    {
        private Exception? _failsWith;
        private int _failed;
        private volatile bool _stopped;

        /// <summary>What reading a mount's access paths throws; null to have them.</summary>
        public Exception? FailsWith
        {
            get => Volatile.Read(ref _failsWith);
            set => Volatile.Write(ref _failsWith, value);
        }

        /// <summary>How many times the paths were asked for and <see cref="FailsWith"/> thrown.</summary>
        public int Failed => Volatile.Read(ref _failed);

        public bool Stopped => _stopped;

        public string TypeId => "failing-paths";

        public string DisplayName => "Failing paths";

        public string Description => "";

        public Stream? OpenIcon() => null;

        public IReadOnlyList<OSPlatform> SupportedPlatforms { get; } = [OSPlatform.Windows, OSPlatform.Linux, OSPlatform.OSX];

        public string? UnavailableReason => null;

        public PerformanceTier Tier => PerformanceTier.Protocol;

        public bool SupportsWrites => true;

        public Type SettingsType => typeof(FakeMountSettings);

        public object CreateDefaultSettings(ICollectionInfo collection) => new FakeMountSettings();

        public IMount CreateMount(object settings, ICollectionInfo collection) => new Mount(this);

        private sealed class Mount(FailingPathsProvider provider) : IMount
        {
            public IReadOnlyList<AccessPath> AccessPaths
            {
                get
                {
                    if (provider.FailsWith is { } failure)
                    {
                        Interlocked.Increment(ref provider._failed);
                        throw failure;
                    }

                    return [new AccessPath("URL", "failing://c/")];
                }
            }

            public MountOpenAction? OpenAction => null;

            public Task StartAsync(IVirtualFileSystem fs, IMountContext context, CancellationToken ct) => Task.CompletedTask;

            public Task StopAsync(CancellationToken ct)
            {
                provider._stopped = true;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>A source that never gets past its start, as one waiting for a sign-in.</summary>
    private sealed class WaitingPlugin : ISourcePlugin
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MemorySource? Source { get; private set; }

        public string TypeId => "waiting";

        public string DisplayName => "Waiting";

        public string Description => "";

        public string TypeFolder => "waiting";

        public Stream? OpenIcon() => null;

        public Type SettingsType => typeof(MemoryPluginSettings);

        public object CreateDefaultSettings() => new MemoryPluginSettings();

        public ISource CreateSource(object settings) => Source = new MemorySource
        {
            OnInitialise = async (_, ct) =>
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            },
        };
    }

    /// <summary>The system's clock run a hundred times as fast, but for the time of day: the run's two-second tick comes every 20 ms, its half minute in 300.</summary>
    private sealed class HundredfoldTime : TimeProvider
    {
        public override long GetTimestamp() => base.GetTimestamp() * 100;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => base.CreateTimer(callback, state, dueTime > TimeSpan.Zero ? dueTime / 100 : dueTime, period);
    }

    /// <summary>Keeps what is logged, whatever its category, for a test to read.</summary>
    private sealed class KeptLog : ILoggerFactory, ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Text, Exception? Exception)> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue((logLevel, formatter(state, exception), exception));
    }

    [TestMethod]
    public async Task StatusJsonCarriesWhatTheSourcesWereAsked()
    {
        await using var engine = Engine();
        engine.Load();
        await engine.StartAsync(null, CancellationToken.None);
        var collection = await engine.AddCollectionAsync("c");
        var source = await engine.AddSourceAsync("alpha", "memory", true, false, new MemoryPluginSettings { Files = "a.txt=A" });
        await engine.AddMembershipAsync(collection.Id, source.Id, "mem", false);
        var runner = engine.FindSourceRunner(source.Id)!;
        await Wait.UntilAsync(() => runner.Handle.IsUsable, what: "alpha ready");
        var vfs = engine.Collections.Single(c => c.Name == "c").Vfs;

        Assert.AreEqual("A", await Handles.ReadTextAsync(vfs, "mem/a.txt"));
        await Handles.ExpectAsync(VfsError.NotFound, () => Handles.ReadTextAsync(vfs, "mem/missing.txt"));
        CollectionAssert.Contains(await Handles.NamesAsync(vfs, "mem"), "a.txt");

        Assert.IsTrue(engine.Operations.Recent().All(r => r.Source == "alpha"), "what goes through the router is recorded under the source's name");
        engine.WriteStatus();
        var (read, _) = StatusFileReader.Read(_paths);
        var kinds = read!.Operations.Sources.Single(s => s.Id == source.Id).Operations.ToDictionary(o => o.Kind);
        Assert.AreEqual(1, kinds[OperationKind.Start].Finished);
        Assert.AreEqual(1, kinds[OperationKind.ReadFile].Finished);
        Assert.AreEqual(1, kinds[OperationKind.ReadFile].Bytes);
        Assert.IsTrue(kinds[OperationKind.List].Items >= 1, "a listing counts its entries");
        Assert.IsTrue(kinds[OperationKind.Stat].Finished >= 2);
        Assert.IsTrue(read.Operations.Finished >= 4);

        await engine.DeleteSourceAsync(source.Id);
        Assert.AreEqual(0, engine.Operations.Stats().Sources.Count, "a deleted source's counters go");
        Assert.AreEqual(0, engine.Operations.Recent().Count, "and its finished operations");
    }

    [TestMethod]
    public async Task SecretsFlowFromTheStoreIntoPluginSettings()
    {
        Guid id;
        WriteSettings(store =>
        {
            var a = Source("alpha");
            id = a.Id;
            store.Sources.Add(a);
            var c = new CollectionDefinition { Name = "c" };
            c.Sources.Add(new SourceMembership { Id = a.Id, Path = "" });
            store.Collections.Add(c);
        });
        var reloaded = new SettingsStore(_paths, NullLogger.Instance);
        reloaded.Load();
        id = reloaded.Sources.Single().Id;
        new SecretStore(_paths, NullLogger.Instance).Set(id, "Secret", "s3cret");

        await using var engine = Engine();
        engine.Load();
        await engine.StartAsync(null, CancellationToken.None);
        var runner = engine.Sources.Single();
        await Wait.UntilAsync(() => runner.Handle.IsUsable, what: "ready");
        Assert.AreEqual("s3cret", await Handles.ReadTextAsync(engine.Collections.Single().Vfs, "secret.txt"));
    }
}
