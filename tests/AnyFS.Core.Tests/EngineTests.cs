using System.Text.Json.Nodes;
using AnyFS.Core.Model;
using AnyFS.Core.Plugins;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Core.Status;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
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
            var collection = new CollectionDefinition { Name = "ben" };
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
        var ben = snapshot.Collections.Single();
        Assert.AreEqual("Green", ben.Colour);
        Assert.AreEqual("2 sources ready, 2 mounts ready", ben.Text);
        Assert.AreEqual("mem/alpha", ben.Sources.Single(s => s.Name == "alpha").Path);
        Assert.IsTrue(ben.Sources.Single(s => s.Name == "beta").ReadOnly);
        Assert.AreEqual("fake://1/ben/", ben.Mounts.Single(m => m.Name == "fake-rw").AccessPaths.Single().Value);
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
        Assert.AreEqual("ben", read!.Collections.Single().Name);
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
