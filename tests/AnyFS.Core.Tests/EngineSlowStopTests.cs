using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using AnyFS.Core.Plugins;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Core.Status;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Core.Tests;

/// <summary>
/// A source or a mount that takes long to stop, or never does, holds up what is changed of itself and nothing else (the
/// review of 21-Nov-2026, R7a). The engine made one change at a time and waited inside it for whatever the change
/// stopped: one source whose stop hung, and every later Add, Edit, Enable and Retry waited for ever, its dialog busy.
/// </summary>
[TestClass]
public class EngineSlowStopTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
    private string _root = "";
    private SettingsPaths _paths = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "slowstop-" + Guid.NewGuid().ToString("N"));
        _paths = SettingsPaths.Resolve(Path.Combine(_root, "settings"));
        Directory.CreateDirectory(_paths.Settings);
        File.WriteAllText(_paths.CollectionsFile, """{ "version": 1, "collections": [] }""");
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

    /// <summary>Sources that stop only when the test lets them: each waits in its dispose for <see cref="LetGo"/>.</summary>
    private sealed class HeldPlugin : ISourcePlugin
    {
        private int _running;

        public TaskCompletionSource LetGo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Sources made, sources that got as far as being initialised, and the most that were up at one time.</summary>
        public int Made;
        public int Initialised;
        public int MostAtOnce;

        public volatile bool Stopping;

        public string TypeId => "held";

        public string DisplayName => "Held";

        public string Description => "";

        public string TypeFolder => "held";

        public Stream? OpenIcon() => null;

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
        public Type SettingsType => typeof(MemoryPluginSettings);

        public object CreateDefaultSettings() => new MemoryPluginSettings();

        public ISource CreateSource(object settings)
        {
            Interlocked.Increment(ref Made);
            var source = new MemorySource();
            foreach (var pair in ((MemoryPluginSettings)settings).Files.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                source.AddFile(pair[..eq].Trim(), pair[(eq + 1)..]);
            }

            var up = false;
            source.OnInitialise = (_, _) =>
            {
                Interlocked.Increment(ref Initialised);
                up = true;
                var now = Interlocked.Increment(ref _running);
                lock (LetGo)
                {
                    MostAtOnce = Math.Max(MostAtOnce, now);
                }

                return Task.CompletedTask;
            };
            source.OnDispose = async context =>
            {
                // what a source keeps as it stops: the engine forgets a deleted source's secrets after this, not before
                context?.Secrets.Set("session", "kept as it stopped");
                Stopping = true;
                await LetGo.Task.ConfigureAwait(false);
                if (up)
                {
                    Interlocked.Decrement(ref _running);
                }
            };
            return source;
        }
    }

    /// <summary>Mounts that stop only when the test lets them, whatever token they are given.</summary>
    private sealed class HeldMountProvider : IMountProviderPlugin
    {
        public TaskCompletionSource LetGo { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started;

        /// <summary>The port in the settings of each mount started, in the order they were.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<int> Ports { get; } = new();

        public volatile bool Stopping;

        public string TypeId => "held-mount";

        public string DisplayName => "Held mount";

        public string Description => "";

        public Stream? OpenIcon() => null;

        public IReadOnlyList<OSPlatform> SupportedPlatforms { get; } = [OSPlatform.Windows, OSPlatform.Linux, OSPlatform.OSX];

        public string? UnavailableReason => null;

        public PerformanceTier Tier => PerformanceTier.Protocol;

        public bool SupportsWrites => true;

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
        public Type SettingsType => typeof(FakeMountSettings);

        public object CreateDefaultSettings(ICollectionInfo collection) => new FakeMountSettings();

        public IMount CreateMount(object settings, ICollectionInfo collection) => new HeldMount(this, (FakeMountSettings)settings, collection);

        private sealed class HeldMount(HeldMountProvider provider, FakeMountSettings settings, ICollectionInfo collection) : IMount
        {
            public IReadOnlyList<AccessPath> AccessPaths { get; private set; } = [];

            public MountOpenAction? OpenAction => null;

            public Task StartAsync(IVirtualFileSystem fs, IMountContext context, CancellationToken ct)
            {
                Interlocked.Increment(ref provider.Started);
                provider.Ports.Enqueue(settings.Port);
                AccessPaths = [new AccessPath("URL", $"held://{collection.Name}/")];
                return Task.CompletedTask;
            }

            public async Task StopAsync(CancellationToken ct)
            {
                provider.Stopping = true;
                await provider.LetGo.Task.ConfigureAwait(false);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private readonly HeldPlugin _held = new();
    private readonly HeldMountProvider _heldMounts = new();

    private async Task<AnyFsEngine> StartedAsync()
    {
        var registry = new PluginRegistry();
        registry.AddBuiltIn(new MemoryPlugin());
        registry.AddBuiltIn(new FakeMountProvider());
        registry.AddBuiltIn(_held);
        registry.AddBuiltIn(_heldMounts);
        var engine = new AnyFsEngine(_paths, registry, NullLoggerFactory.Instance);
        engine.Load();
        await engine.StartAsync(null, CancellationToken.None);
        return engine;
    }

    private SettingsStore Reload()
    {
        var store = new SettingsStore(_paths, NullLogger.Instance);
        store.Load();
        return store;
    }

    /// <summary>A source of the held kind, up; the engine's own handle on it.</summary>
    private async Task<(Guid Id, SourceHandle Handle)> HeldSourceAsync(AnyFsEngine engine, string name = "stuck")
    {
        var definition = await engine.AddSourceAsync(name, "held", enabled: true, countFilesOnStart: false, new MemoryPluginSettings());
        var handle = engine.FindSourceRunner(definition.Id)!.Handle;
        await Wait.UntilAsync(() => handle.IsUsable, what: name + " ready");
        return (definition.Id, handle);
    }

    [TestMethod]
    public async Task WhileASourceWillNotStopEverythingElseCanStillBeChanged()
    {
        var engine = await StartedAsync();
        try
        {
            var (stuck, handle) = await HeldSourceAsync(engine);
            var disabling = engine.SetSourceEnabledAsync(stuck, false);
            await Wait.UntilAsync(() => _held.Stopping, what: "the source stopping");

            // each of these waited for the stop before
            var work = await engine.AddCollectionAsync("work").WaitAsync(Soon);
            var other = await engine.AddSourceAsync("other", "memory", true, false, new MemoryPluginSettings { Files = "a.txt=A" }).WaitAsync(Soon);
            await engine.AddMembershipAsync(work.Id, other.Id, "mem/other", false).WaitAsync(Soon);
            await engine.UpdateSourceAsync(other.Id, "other", true, false, new MemoryPluginSettings { Files = "b.txt=B" }).WaitAsync(Soon);
            await engine.RetrySourceAsync(other.Id).WaitAsync(Soon);
            await engine.SetSourceEnabledAsync(other.Id, false).WaitAsync(Soon);
            await engine.SetSourceEnabledAsync(other.Id, true).WaitAsync(Soon);
            await engine.AddMountAsync(work.Id, "fake", "fake", true, false, new FakeMountSettings { Port = 31 }).WaitAsync(Soon);
            await Wait.UntilAsync(() => engine.FindSourceRunner(other.Id)!.Handle.IsUsable, what: "the other source ready");
            Assert.AreEqual(ComponentState.Stopping, handle.Status.State, "the one that will not stop says so meanwhile");
            Assert.IsFalse(Reload().Sources.Single(s => s.Id == stuck).Enabled, "what was changed of it is kept at once");

            _held.LetGo.TrySetResult();
            await disabling.WaitAsync(Soon);
            Assert.AreEqual(ComponentState.Disabled, handle.Status.State);
        }
        finally
        {
            _held.LetGo.TrySetResult();
            await engine.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task WhileAMountWillNotStopTheOtherCollectionsCanStillBeChanged()
    {
        var engine = await StartedAsync();
        try
        {
            var work = await engine.AddCollectionAsync("work");
            var mount = await engine.AddMountAsync(work.Id, "held", "held-mount", true, false, new FakeMountSettings());
            Assert.AreEqual(1, _heldMounts.Started);
            var disabling = engine.SetMountEnabledAsync(work.Id, mount.Id, false);
            await Wait.UntilAsync(() => _heldMounts.Stopping, what: "the mount stopping");

            var other = await engine.AddCollectionAsync("other").WaitAsync(Soon);
            await engine.AddMountAsync(other.Id, "fake", "fake", true, false, new FakeMountSettings { Port = 32 }).WaitAsync(Soon);
            var source = await engine.AddSourceAsync("alpha", "memory", true, false, new MemoryPluginSettings()).WaitAsync(Soon);
            await engine.AddMembershipAsync(other.Id, source.Id, "mem/alpha", false).WaitAsync(Soon);
            await Wait.UntilAsync(() => engine.Snapshot().Collections.Single(c => c.Name == "other").Colour == "Green", what: "the other collection green");

            _heldMounts.LetGo.TrySetResult();
            await disabling.WaitAsync(Soon);
            Assert.AreEqual(ComponentState.Disabled, engine.FindCollectionRunner(work.Id)!.Mounts.Single().Status.State);
        }
        finally
        {
            _heldMounts.LetGo.TrySetResult();
            await engine.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AChangeToASourceThatWillNotStopComesBackWhenItsPatienceIsOver()
    {
        var engine = await StartedAsync();
        try
        {
            engine.ChangePatience = Short;
            var (stuck, handle) = await HeldSourceAsync(engine);

            // a dialog was busy for as long as the stop took: for ever
            await engine.SetSourceEnabledAsync(stuck, false).WaitAsync(Soon);
            Assert.IsTrue(_held.Stopping);
            Assert.AreEqual(ComponentState.Stopping, handle.Status.State, "it says what it is doing");
            Assert.IsFalse(Reload().Sources.Single().Enabled, "the change is kept");

            _held.LetGo.TrySetResult();
            await engine.TurnsEndedAsync().WaitAsync(Soon);
            Assert.AreEqual(ComponentState.Disabled, handle.Status.State);
        }
        finally
        {
            _held.LetGo.TrySetResult();
            await engine.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AnEditedSourceStartsAgainOnlyWhenItsOldSelfHasStopped()
    {
        var engine = await StartedAsync();
        try
        {
            engine.ChangePatience = Short;
            var (stuck, handle) = await HeldSourceAsync(engine);

            await engine.UpdateSourceAsync(stuck, "stuck", true, false, new MemoryPluginSettings { Files = "edited.txt=E" }).WaitAsync(Soon);
            await Task.Delay(Short);
            Assert.AreEqual(1, _held.Made, "the edited one is not made while the old one still stops: both would work in one state folder");
            Assert.AreEqual(ComponentState.Stopping, handle.Status.State);
            Assert.IsFalse(handle.IsUsable);

            _held.LetGo.TrySetResult();
            await Wait.UntilAsync(() => handle.IsUsable, what: "the edited source ready");
            CollectionAssert.AreEqual(new[] { "edited.txt" }, await Handles.NamesAsync(handle.Current!, ""));
            Assert.AreEqual(2, _held.Made);
            Assert.AreEqual(1, _held.MostAtOnce, "never two of one source at a time");
        }
        finally
        {
            _held.LetGo.TrySetResult();
            await engine.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ChangesToASourceThatWillNotStopAreMadeInTheirOrderWhenItHas()
    {
        var engine = await StartedAsync();
        try
        {
            engine.ChangePatience = Short;
            var (stuck, handle) = await HeldSourceAsync(engine);

            await engine.UpdateSourceAsync(stuck, "stuck", true, false, new MemoryPluginSettings { Files = "first.txt=1" }).WaitAsync(Soon);
            await engine.SetSourceEnabledAsync(stuck, false).WaitAsync(Soon);
            await engine.UpdateSourceAsync(stuck, "stuck", false, false, new MemoryPluginSettings { Files = "second.txt=2" }).WaitAsync(Soon);
            await engine.SetSourceEnabledAsync(stuck, true).WaitAsync(Soon);
            await engine.RetrySourceAsync(stuck).WaitAsync(Soon);
            Assert.AreEqual(1, _held.Made, "nothing of it is started while the first still stops");

            _held.LetGo.TrySetResult();
            await engine.TurnsEndedAsync().WaitAsync(Soon);
            await Wait.UntilAsync(() => handle.IsUsable, what: "the source ready, as the last change left it");
            CollectionAssert.AreEqual(new[] { "second.txt" }, await Handles.NamesAsync(handle.Current!, ""));
            Assert.AreEqual(1, _held.MostAtOnce, "never two of one source at a time");
            Assert.IsTrue(_held.Made <= 3, $"the first edit's source was never started: it was replaced before its turn came ({_held.Made} made)");
        }
        finally
        {
            _held.LetGo.TrySetResult();
            await engine.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ASourceDisabledBeforeItsEditTookEffectIsNeverStarted()
    {
        var engine = await StartedAsync();
        try
        {
            engine.ChangePatience = Short;
            var (stuck, handle) = await HeldSourceAsync(engine);

            await engine.UpdateSourceAsync(stuck, "stuck", true, false, new MemoryPluginSettings { Files = "edited.txt=E" }).WaitAsync(Soon);
            await engine.SetSourceEnabledAsync(stuck, false).WaitAsync(Soon);
            _held.LetGo.TrySetResult();
            await engine.TurnsEndedAsync().WaitAsync(Soon);
            Assert.AreEqual(ComponentState.Disabled, handle.Status.State);
            Assert.AreEqual(1, _held.Made, "the edited one was disabled before its turn to start came");
        }
        finally
        {
            _held.LetGo.TrySetResult();
            await engine.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ASourceDeletedWhileItWillNotStopIsGoneAtOnceAndWhatItKeptWhenItHas()
    {
        var engine = await StartedAsync();
        try
        {
            engine.ChangePatience = Short;
            var definition = await engine.AddSourceAsync("stuck", "held", true, false, new MemoryPluginSettings());
            var stuck = definition.Id;
            await Wait.UntilAsync(() => engine.FindSourceRunner(stuck)!.Handle.IsUsable, what: "ready");
            var state = Directory.CreateDirectory(_paths.SourceState(stuck)).FullName;
            File.WriteAllText(Path.Combine(state, "kept.json"), "{}");

            await engine.DeleteSourceAsync(stuck).WaitAsync(Soon);
            Assert.IsNull(engine.FindSourceRunner(stuck));
            Assert.AreEqual(0, Reload().Sources.Count, "the definition is gone at once");
            Assert.IsTrue(Directory.Exists(state), "its folder stays while it still stops: it may write there yet");
            Assert.AreEqual("kept as it stopped", engine.Secrets.Get(stuck, "session"));

            // its id is not free meanwhile: what comes in with it gets another, and is not what the old one's end removes
            await engine.ImportSourcesAsync([SourceTransfer.Copy(definition)], (_, _) => Task.FromResult(ImportConflict.Keep)).WaitAsync(Soon);
            var imported = engine.Settings.Sources.Single();
            Assert.AreNotEqual(stuck, imported.Id);

            _held.LetGo.TrySetResult();
            await engine.TurnsEndedAsync().WaitAsync(Soon);
            Assert.IsFalse(Directory.Exists(state), "the folder goes when the source has stopped");
            Assert.IsNull(engine.Secrets.Get(stuck, "session"), "and what it kept as it stopped");
            await Wait.UntilAsync(() => engine.FindSourceRunner(imported.Id)!.Handle.IsUsable, what: "the imported source ready");
        }
        finally
        {
            _held.LetGo.TrySetResult();
            await engine.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AnEditedMountStartsAgainOnlyWhenItsOldSelfHasStopped()
    {
        var engine = await StartedAsync();
        try
        {
            engine.ChangePatience = Short;
            var work = await engine.AddCollectionAsync("work");
            var mount = await engine.AddMountAsync(work.Id, "held", "held-mount", true, false, new FakeMountSettings { Port = 1 });
            Assert.AreEqual(1, _heldMounts.Started);

            await engine.UpdateMountAsync(work.Id, mount.Id, "held", true, false, new FakeMountSettings { Port = 2 }).WaitAsync(Soon);
            await Task.Delay(Short);
            Assert.AreEqual(1, _heldMounts.Started, "the edited one is not started while the old one still stops: both would hold one port or drive");
            Assert.AreEqual(ComponentState.Stopping, engine.FindCollectionRunner(work.Id)!.Mounts.Single().Status.State);

            // and a second edit, and the mount taken away and put back, wait their turn behind it
            await engine.UpdateMountAsync(work.Id, mount.Id, "held", true, false, new FakeMountSettings { Port = 3 }).WaitAsync(Soon);
            await engine.SetMountEnabledAsync(work.Id, mount.Id, false).WaitAsync(Soon);
            await engine.SetMountEnabledAsync(work.Id, mount.Id, true).WaitAsync(Soon);
            Assert.AreEqual(1, _heldMounts.Started);

            _heldMounts.LetGo.TrySetResult();
            await engine.TurnsEndedAsync().WaitAsync(Soon);
            Assert.AreEqual(ComponentState.Ready, engine.FindCollectionRunner(work.Id)!.Mounts.Single().Status.State);
            CollectionAssert.DoesNotContain(_heldMounts.Ports.ToArray(), 2, "the first edit's mount was never started: it was replaced before its turn came");
            Assert.AreEqual(3, _heldMounts.Ports.Last(), "the mount that is up is the last edit's");
        }
        finally
        {
            _heldMounts.LetGo.TrySetResult();
            await engine.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task NothingIsStartedOnceTheEngineStops()
    {
        var engine = await StartedAsync();
        try
        {
            engine.ChangePatience = Short;
            var (stuck, _) = await HeldSourceAsync(engine);
            var work = await engine.AddCollectionAsync("work");
            await engine.AddMountAsync(work.Id, "held", "held-mount", true, false, new FakeMountSettings { Port = 1 });

            // an edited source and an edited mount, each waiting for its old self to stop, as the engine stops
            await engine.UpdateSourceAsync(stuck, "stuck", true, false, new MemoryPluginSettings { Files = "edited.txt=E" }).WaitAsync(Soon);
            await engine.UpdateMountAsync(work.Id, engine.Settings.Collections.Single().Mounts.Single().Id, "held", true, false, new FakeMountSettings { Port = 2 }).WaitAsync(Soon);
            var stopping = engine.StopAsync();
            await Task.Delay(Short);
            Assert.IsFalse(stopping.IsCompleted, "the engine's stop waits for what still stops, as it did");

            _heldMounts.LetGo.TrySetResult();
            _held.LetGo.TrySetResult();
            await stopping.WaitAsync(Soon);
            Assert.AreEqual(1, _held.Made, "the edited source was not started as the engine stopped");
            Assert.AreEqual(1, _heldMounts.Started, "nor the edited mount");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => engine.AddCollectionAsync("late"));
        }
        finally
        {
            _heldMounts.LetGo.TrySetResult();
            _held.LetGo.TrySetResult();
            await engine.DisposeAsync();
        }
    }
}
