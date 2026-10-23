using AnyFS.Core.Model;
using AnyFS.Core.Plugins;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Core.Status;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Core.Tests;

/// <summary>Changing things while the engine runs (what the window does): applied live and persisted.</summary>
[TestClass]
public class EngineMutationTests
{
    private string _root = "";
    private SettingsPaths _paths = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "mutate-" + Guid.NewGuid().ToString("N"));
        _paths = SettingsPaths.Resolve(Path.Combine(_root, "settings"));
        // an empty collections.json means no "Collection 1" on first start, so Single() below means ours
        Directory.CreateDirectory(_paths.Settings);
        File.WriteAllText(_paths.CollectionsFile, """{ "version": 1, "collections": [] }""");
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

    private async Task<AnyFsEngine> StartedAsync()
    {
        var engine = new AnyFsEngine(_paths, Registry(), NullLoggerFactory.Instance);
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

    private static FakeMount LiveMount(string collection)
    {
        lock (FakeMountProvider.Mounts)
        {
            return FakeMountProvider.Mounts.Last(m => m.Collection.Name == collection && m.Fs is not null && !m.Stopped);
        }
    }

    [TestMethod]
    public async Task AddingACollectionASourceAMembershipAndAMountGoesLiveAndToDisk()
    {
        await using var engine = await StartedAsync();
        var changes = 0;
        engine.Changed += () => Interlocked.Increment(ref changes);

        var work = await engine.AddCollectionAsync("work");
        var alpha = await engine.AddSourceAsync("alpha", "memory", enabled: true, countFilesOnStart: false, new MemoryPluginSettings { Files = "a.txt=A", Secret = "s3cret" });
        await engine.AddMembershipAsync(work.Id, alpha.Id, "mem/alpha", readOnly: false);
        var mount = await engine.AddMountAsync(work.Id, "fake", "fake", enabled: true, readOnly: false, new FakeMountSettings { Port = 7 });
        await Wait.UntilAsync(() => engine.Snapshot().Collections.Single(c => c.Name == "work").Colour == "Green", what: "work green");

        var snapshot = engine.Snapshot().Collections.Single(c => c.Name == "work");
        Assert.AreEqual("mem/alpha", snapshot.Sources.Single().Path);
        Assert.AreEqual("fake://7/work/", snapshot.Mounts.Single().AccessPaths.Single().Value);
        var live = LiveMount("work");
        CollectionAssert.AreEqual(new[] { "mem/" }, await Handles.NamesAsync(live.Fs!, ""));
        Assert.AreEqual("A", await Handles.ReadTextAsync(live.Fs!, "mem/alpha/a.txt"));
        Assert.AreEqual("s3cret", await Handles.ReadTextAsync(live.Fs!, "mem/alpha/secret.txt"), "the secret reached the plugin");
        Assert.IsTrue(changes > 0, "Changed fired");

        var sourcesJson = await File.ReadAllTextAsync(_paths.SourcesFile);
        Assert.IsFalse(sourcesJson.Contains("s3cret", StringComparison.Ordinal), "secrets never enter sources.json");
        Assert.AreEqual("s3cret", engine.Secrets.Get(alpha.Id, "Secret"));

        var store = Reload();
        Assert.AreEqual("alpha", store.Sources.Single().Name);
        var collection = store.Collections.Single(c => c.Name == "work");
        Assert.AreEqual("mem/alpha", collection.Sources.Single().Path);
        Assert.AreEqual(mount.Id, collection.Mounts.Single().Id);
        Assert.AreEqual(7, collection.Mounts.Single().Settings!["port"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task ThePluginTidiesTheSettingsBeforeTheyAreStored()
    {
        await using var engine = await StartedAsync();
        var alpha = await engine.AddSourceAsync("alpha", "memory", true, false, new MemoryPluginSettings { Files = " a.txt=A ; ;b.txt=B" });
        Assert.AreEqual("a.txt=A;b.txt=B", Reload().Sources.Single().Settings!["files"]!.GetValue<string>(), "when added");

        await engine.UpdateSourceAsync(alpha.Id, "alpha", true, false, new MemoryPluginSettings { Files = "c.txt=C ;" });
        Assert.AreEqual("c.txt=C", Reload().Sources.Single().Settings!["files"]!.GetValue<string>(), "and when edited");
    }

    [TestMethod]
    public async Task EditingASourceRestartsItOnlyWhenItsSettingsChanged()
    {
        await using var engine = await StartedAsync();
        var work = await engine.AddCollectionAsync("work");
        var alpha = await engine.AddSourceAsync("alpha", "memory", true, false, new MemoryPluginSettings { Files = "a.txt=A" });
        await engine.AddMembershipAsync(work.Id, alpha.Id, "", false);
        var runner = engine.FindSourceRunner(alpha.Id)!;
        await Wait.UntilAsync(() => runner.Handle.IsUsable, what: "alpha ready");
        var instance = runner.Handle.Current;

        await engine.UpdateSourceAsync(alpha.Id, "alpha renamed", true, false, new MemoryPluginSettings { Files = "a.txt=A" });
        Assert.AreSame(instance, engine.FindSourceRunner(alpha.Id)!.Handle.Current, "a rename alone doesn't restart");
        Assert.AreEqual("alpha renamed", Reload().Sources.Single().Name);
        Assert.AreEqual("alpha renamed", engine.Snapshot().Collections.Single(c => c.Name == "work").Sources.Single().Name);

        await engine.UpdateSourceAsync(alpha.Id, "alpha renamed", true, false, new MemoryPluginSettings { Files = "b.txt=B" });
        var replaced = engine.FindSourceRunner(alpha.Id)!;
        await Wait.UntilAsync(() => replaced.Handle.IsUsable && !ReferenceEquals(replaced.Handle.Current, instance), what: "restarted with new settings");
        CollectionAssert.AreEqual(new[] { "b.txt" }, await Handles.NamesAsync(engine.Collections.Single(c => c.Name == "work").Vfs, ""));
        Assert.AreEqual("b.txt=B", Reload().Sources.Single().Settings!["files"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task DisablingASourceRemovesItFromTheTreeAndEnablingBringsItBack()
    {
        await using var engine = await StartedAsync();
        var work = await engine.AddCollectionAsync("work");
        var alpha = await engine.AddSourceAsync("alpha", "memory", true, false, new MemoryPluginSettings { Files = "a.txt=A" });
        await engine.AddMembershipAsync(work.Id, alpha.Id, "mem/alpha", false);
        var vfs = engine.Collections.Single(c => c.Name == "work").Vfs;
        await Wait.UntilAsync(() => engine.FindSourceRunner(alpha.Id)!.Handle.IsUsable, what: "ready");

        await engine.SetSourceEnabledAsync(alpha.Id, false);
        CollectionAssert.AreEqual(Array.Empty<string>(), await Handles.NamesAsync(vfs, ""), "a disabled source is absent from the tree");
        Assert.IsFalse(engine.FindSourceRunner(alpha.Id)!.IsRunning);
        Assert.IsFalse(Reload().Sources.Single().Enabled);

        await engine.SetSourceEnabledAsync(alpha.Id, true);
        await Wait.UntilAsync(() => engine.FindSourceRunner(alpha.Id)!.Handle.IsUsable, what: "ready again");
        CollectionAssert.AreEqual(new[] { "mem/" }, await Handles.NamesAsync(vfs, ""));
        Assert.IsTrue(Reload().Sources.Single().Enabled);
    }

    [TestMethod]
    public async Task MembershipEditsChangeThePathAndReadOnlyLive()
    {
        await using var engine = await StartedAsync();
        var work = await engine.AddCollectionAsync("work");
        var alpha = await engine.AddSourceAsync("alpha", "memory", true, false, new MemoryPluginSettings { Files = "a.txt=A" });
        await engine.AddMembershipAsync(work.Id, alpha.Id, "mem/alpha", false);
        var vfs = engine.Collections.Single(c => c.Name == "work").Vfs;
        await Wait.UntilAsync(() => engine.FindSourceRunner(alpha.Id)!.Handle.IsUsable, what: "ready");
        Assert.IsTrue(vfs.CanWrite("mem/alpha/new.txt"));

        await engine.UpdateMembershipAsync(work.Id, alpha.Id, "files", readOnly: true);
        CollectionAssert.AreEqual(new[] { "files/" }, await Handles.NamesAsync(vfs, ""));
        Assert.IsFalse(vfs.CanWrite("files/new.txt"));
        var membership = Reload().Collections.Single().Sources.Single();
        Assert.AreEqual("files", membership.Path);
        Assert.IsTrue(membership.ReadOnly);

        await engine.RemoveMembershipAsync(work.Id, alpha.Id);
        CollectionAssert.AreEqual(Array.Empty<string>(), await Handles.NamesAsync(vfs, ""));
        Assert.AreEqual(0, Reload().Collections.Single().Sources.Count);
        Assert.IsTrue(engine.FindSourceRunner(alpha.Id)!.IsRunning, "without a collection filter every enabled source keeps running");
    }

    [TestMethod]
    public async Task DeletingASourceRemovesItsMembershipsSecretsAndState()
    {
        await using var engine = await StartedAsync();
        var work = await engine.AddCollectionAsync("work");
        var bob = await engine.AddCollectionAsync("bob");
        var alpha = await engine.AddSourceAsync("alpha", "memory", true, false, new MemoryPluginSettings { Files = "a.txt=A", Secret = "s3cret" });
        await engine.AddMembershipAsync(work.Id, alpha.Id, "mem/alpha", false);
        await engine.AddMembershipAsync(bob.Id, alpha.Id, "", true);
        await Wait.UntilAsync(() => engine.FindSourceRunner(alpha.Id)!.Handle.IsUsable, what: "ready");
        var source = (MemorySource)engine.FindSourceRunner(alpha.Id)!.Handle.Current!;
        Directory.CreateDirectory(_paths.SourceState(alpha.Id));

        await engine.DeleteSourceAsync(alpha.Id);
        Assert.IsTrue(source.Disposed);
        Assert.IsNull(engine.FindSourceRunner(alpha.Id));
        Assert.IsNull(engine.Secrets.Get(alpha.Id, "Secret"));
        Assert.IsFalse(Directory.Exists(_paths.SourceState(alpha.Id)));
        var store = Reload();
        Assert.AreEqual(0, store.Sources.Count);
        Assert.IsTrue(store.Collections.All(c => c.Sources.Count == 0));
        Assert.AreEqual(0, engine.Snapshot().Collections.Sum(c => c.Sources.Count));
    }

    [TestMethod]
    public async Task ASourceDeletedWhileItStartsGoesWhileAnotherProgramHoldsItsSecretsAMoment()
    {
        // the user removed a 3DX source (and its definition) while it listed its documents: stopping, it kept its session
        // (a secret), and something scanning the secrets file just written (an antivirus) still held it when the engine
        // removed the source's secrets; Windows refused the rename and Remove source said UnauthorizedAccess_IODenied_NoPathName
        var registry = Registry();
        var plugin = new ListingPlugin(_paths.SecretsFile);
        registry.AddBuiltIn(plugin);
        await using var engine = new AnyFsEngine(_paths, registry, NullLoggerFactory.Instance);
        engine.Load();
        await engine.StartAsync(null, CancellationToken.None);
        var work = await engine.AddCollectionAsync("work");
        var source = await engine.AddSourceAsync("3dx", "listing", true, false, new MemoryPluginSettings());
        await engine.AddMembershipAsync(work.Id, source.Id, "3dx", false);
        await Wait.UntilAsync(() => plugin.Listing, what: "the source listing its documents");

        await engine.RemoveMembershipAsync(work.Id, source.Id);
        await engine.DeleteSourceAsync(source.Id);
        Assert.IsTrue(plugin.HeldTheFile, "the secrets file was held as the source was removed");
        Assert.IsNull(engine.Secrets.Get(source.Id, "session"));
        Assert.IsNull(engine.FindSourceRunner(source.Id));
        Assert.AreEqual(0, Reload().Sources.Count);
    }

    /// <summary>A source that lists until stopped, keeps a session secret when it stops, and then has its secrets file held for a moment, as an antivirus scanning it does.</summary>
    private sealed class ListingPlugin(string secretsFile) : ISourcePlugin
    {
        public volatile bool Listing;
        public volatile bool HeldTheFile;

        public string TypeId => "listing";

        public string DisplayName => "Listing";

        public string Description => "";

        public string TypeFolder => "listing";

        public Stream? OpenIcon() => null;

        public Type SettingsType => typeof(MemoryPluginSettings);

        public object CreateDefaultSettings() => new MemoryPluginSettings();

        public ISource CreateSource(object settings) => new MemorySource
        {
            OnInitialise = async (ctx, ct) =>
            {
                ctx.ReportProgress("Listing the documents", 0.1);
                Listing = true;
                await Task.Delay(Timeout.Infinite, ct);
            },
            OnDispose = context =>
            {
                context?.Secrets.Set("session", "cookies");
                var held = new FileStream(secretsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                HeldTheFile = true;
                _ = Task.Delay(150).ContinueWith(_ => held.Dispose(), TaskScheduler.Default);
                return Task.CompletedTask;
            },
        };
    }

    [TestMethod]
    public async Task RenamingACollectionRestartsItsMountsUnderTheNewName()
    {
        await using var engine = await StartedAsync();
        var work = await engine.AddCollectionAsync("work");
        await engine.AddMountAsync(work.Id, "fake", "fake", true, false, new FakeMountSettings { Port = 3 });
        var before = LiveMount("work");
        Assert.AreEqual("fake://3/work/", before.AccessPaths.Single().Value);

        await engine.RenameCollectionAsync(work.Id, "workplace");
        Assert.IsTrue(before.Stopped, "the old mount instance was stopped");
        var after = LiveMount("workplace");
        Assert.AreNotSame(before, after);
        Assert.AreEqual("fake://3/workplace/", after.AccessPaths.Single().Value);
        Assert.AreEqual("workplace", after.Fs!.CollectionName);
        Assert.AreEqual("workplace", Reload().Collections.Single().Name);
        Assert.AreEqual("workplace", engine.Snapshot().Collections.Single().Name);
    }

    [TestMethod]
    public async Task DisablingACollectionStopsItsMountsAndEnablingStartsThem()
    {
        await using var engine = await StartedAsync();
        var work = await engine.AddCollectionAsync("work");
        await engine.AddMountAsync(work.Id, "fake", "fake", true, false, new FakeMountSettings { Port = 4 });
        var first = LiveMount("work");

        await engine.SetCollectionEnabledAsync(work.Id, false);
        Assert.IsTrue(first.Stopped);
        Assert.AreEqual(0, FakeListener.Alive, "the shared listener went with the last mount");
        var snapshot = engine.Snapshot().Collections.Single();
        Assert.AreEqual("Grey", snapshot.Colour);
        Assert.IsFalse(Reload().Collections.Single().Enabled);

        await engine.SetCollectionEnabledAsync(work.Id, true);
        var second = LiveMount("work");
        Assert.AreNotSame(first, second);
        Assert.AreEqual(1, FakeListener.Alive);
        Assert.AreEqual("Green", engine.Snapshot().Collections.Single().Colour);
    }

    [TestMethod]
    public async Task DeletingACollectionStopsItsMountsAndKeepsItsSources()
    {
        await using var engine = await StartedAsync();
        var work = await engine.AddCollectionAsync("work");
        var alpha = await engine.AddSourceAsync("alpha", "memory", true, false, new MemoryPluginSettings());
        await engine.AddMembershipAsync(work.Id, alpha.Id, "", false);
        await engine.AddMountAsync(work.Id, "fake", "fake", true, false, new FakeMountSettings { Port = 5 });
        var mount = LiveMount("work");

        await engine.DeleteCollectionAsync(work.Id);
        Assert.IsTrue(mount.Stopped && mount.Disposed);
        Assert.IsNull(engine.FindCollectionRunner(work.Id));
        var store = Reload();
        Assert.IsFalse(store.Collections.Any(c => c.Name == "work"));
        Assert.AreEqual("alpha", store.Sources.Single().Name, "sources are global and survive the collection");
        Assert.IsTrue(engine.FindSourceRunner(alpha.Id)!.IsRunning);
    }

    [TestMethod]
    public async Task EditingAMountRestartsItAndDeletingItReleasesTheListener()
    {
        await using var engine = await StartedAsync();
        var work = await engine.AddCollectionAsync("work");
        var mount = await engine.AddMountAsync(work.Id, "fake", "fake", true, false, new FakeMountSettings { Port = 6 });
        var first = LiveMount("work");

        await engine.UpdateMountAsync(work.Id, mount.Id, "renamed", true, false, new FakeMountSettings { Port = 6 });
        Assert.IsFalse(first.Stopped, "a name-only edit doesn't restart");
        Assert.AreEqual("renamed", Reload().Collections.Single().Mounts.Single().Name);

        await engine.UpdateMountAsync(work.Id, mount.Id, "renamed", true, true, new FakeMountSettings { Port = 8 });
        Assert.IsTrue(first.Stopped);
        var second = LiveMount("work");
        Assert.IsTrue(second.Fs!.ReadOnly);
        Assert.AreEqual("fake://8/work/", second.AccessPaths.Single().Value);
        Assert.AreEqual(8, Reload().Collections.Single().Mounts.Single().Settings!["port"]!.GetValue<int>());
        Assert.IsTrue(Reload().Collections.Single().Mounts.Single().ReadOnly);

        await engine.SetMountEnabledAsync(work.Id, mount.Id, false);
        Assert.IsTrue(second.Stopped);
        Assert.AreEqual(nameof(ComponentState.Disabled), engine.Snapshot().Collections.Single().Mounts.Single().State);
        await engine.SetMountEnabledAsync(work.Id, mount.Id, true);
        Assert.AreEqual(nameof(ComponentState.Ready), engine.Snapshot().Collections.Single().Mounts.Single().State);

        await engine.DeleteMountAsync(work.Id, mount.Id);
        Assert.AreEqual(0, FakeListener.Alive);
        Assert.AreEqual(0, engine.Snapshot().Collections.Single().Mounts.Count);
        Assert.AreEqual(0, Reload().Collections.Single().Mounts.Count);
    }

    [TestMethod]
    public async Task RetryStartsAFailedSourceAgainAtOnce()
    {
        await using var engine = await StartedAsync();
        var work = await engine.AddCollectionAsync("work");
        var flaky = await engine.AddSourceAsync("flaky", "memory", true, false, new MemoryPluginSettings { FailTimes = 1 });
        await engine.AddMembershipAsync(work.Id, flaky.Id, "", false);
        var runner = engine.FindSourceRunner(flaky.Id)!;
        await Wait.UntilAsync(() => runner.Handle.Status.State == ComponentState.Failed, what: "first failure");
        StringAssert.Contains(runner.Handle.Status.Text, "retrying in 10 s");

        await engine.RetrySourceAsync(flaky.Id);
        await Wait.UntilAsync(() => engine.FindSourceRunner(flaky.Id)!.Handle.IsUsable, what: "ready after retry");
    }

    [TestMethod]
    public async Task ValidationRejectsBadNamesPathsAndTypes()
    {
        await using var engine = await StartedAsync();
        var work = await engine.AddCollectionAsync("work");
        var alpha = await engine.AddSourceAsync("alpha", "memory", true, false, new MemoryPluginSettings());

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => engine.AddCollectionAsync(" "));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => engine.AddCollectionAsync("WORK"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => engine.AddCollectionAsync("a/b"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => engine.AddSourceAsync("Alpha", "memory", true, false, new MemoryPluginSettings()));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => engine.AddSourceAsync("beta", "no-such-type", true, false, new MemoryPluginSettings()));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => engine.AddMembershipAsync(work.Id, alpha.Id, "../x", false));
        await engine.AddMembershipAsync(work.Id, alpha.Id, "mem/alpha", false);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => engine.AddMembershipAsync(work.Id, alpha.Id, "again", false));
        await engine.AddMountAsync(work.Id, "fake", "fake", true, false, new FakeMountSettings { Port = 9 });
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => engine.AddMountAsync(work.Id, "FAKE", "fake", true, false, new FakeMountSettings { Port = 10 }));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => engine.AddMountAsync(work.Id, "other", "no-such-provider", true, false, new FakeMountSettings()));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => engine.DeleteSourceAsync(Guid.NewGuid()));

        var store = Reload();
        Assert.AreEqual(1, store.Collections.Count);
        Assert.AreEqual(1, store.Sources.Count);
    }

    [TestMethod]
    public async Task MutationsBeforeStartOnlyTouchTheFiles()
    {
        await using var engine = new AnyFsEngine(_paths, Registry(), NullLoggerFactory.Instance);
        engine.Load();
        var work = await engine.AddCollectionAsync("work");
        var alpha = await engine.AddSourceAsync("alpha", "memory", true, false, new MemoryPluginSettings());
        await engine.AddMembershipAsync(work.Id, alpha.Id, "mem/alpha", false);
        Assert.AreEqual(0, engine.Sources.Count);
        Assert.AreEqual(0, engine.Collections.Count);

        await engine.StartAsync(null, CancellationToken.None);
        await Wait.UntilAsync(() => engine.Snapshot().Collections.Single(c => c.Name == "work").Colour == "Green", what: "green");
    }
}
