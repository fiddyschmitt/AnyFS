using System.Text.Json;
using System.Text.Json.Nodes;
using AnyFS.Core.Model;
using AnyFS.Core.Plugins;
using AnyFS.Core.Settings;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Core.Tests;

[TestClass]
public class SettingsStoreTests
{
    private string _root = "";
    private SettingsPaths _paths = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "settings-" + Guid.NewGuid().ToString("N"));
        _paths = SettingsPaths.Resolve(Path.Combine(_root, "settings"));
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

    [TestMethod]
    public void SettingsDirOverrideMakesSiblings()
    {
        Assert.AreEqual(Path.Combine(_root, "settings"), _paths.Settings);
        Assert.AreEqual(Path.Combine(_root, "state"), _paths.State);
        Assert.AreEqual(Path.Combine(_root, "cache"), _paths.Cache);
        Assert.AreEqual(Path.Combine(_root, "plugins"), _paths.Plugins);
        Assert.AreEqual(Path.Combine(_root, "anyfs.log"), _paths.LogFile);
        Assert.IsFalse(_paths.Portable);
        Assert.AreEqual(Path.Combine(_root, "settings", "sources.json"), _paths.SourcesFile);
    }

    [TestMethod]
    public void PortableWhenTheExeFolderIsWritable()
    {
        Directory.CreateDirectory(_root);
        var portable = SettingsPaths.Resolve(null, _root);
        Assert.IsTrue(portable.Portable);
        Assert.AreEqual(Path.Combine(_root, "settings"), portable.Settings);
        Assert.AreEqual(Path.Combine(_root, "anyfs.log"), portable.LogFile);

        var notPortable = SettingsPaths.Resolve(null, Path.Combine(_root, "does-not-exist"));
        Assert.IsFalse(notPortable.Portable);
        StringAssert.EndsWith(notPortable.Settings, "settings");
    }

    [TestMethod]
    public void RoundTripKeepsPathReadOnlyAndPluginSettings()
    {
        var store = new SettingsStore(_paths, NullLogger.Instance);
        store.Load();
        Assert.IsTrue(store.FirstStart);
        var source = new SourceDefinition
        {
            Name = "aus-files-01",
            Type = "mirror",
            CountFilesOnStart = true,
            Settings = new JsonObject { ["paths"] = "\\\\aus-files-01\\shared", ["checkIntervalSeconds"] = 30 },
        };
        store.Sources.Add(source);
        var collection = new CollectionDefinition { Name = "ben" };
        collection.Sources.Add(new SourceMembership { Id = source.Id, Path = "files", ReadOnly = true });
        collection.Mounts.Add(new MountDefinition { Name = "HTTP", Provider = "http", ReadOnly = true, Settings = new JsonObject { ["port"] = 11001 } });
        store.Collections.Add(collection);
        store.App.LogLevel = "debug";
        store.SaveSources();
        store.SaveCollections();
        store.SaveApp();

        var reloaded = new SettingsStore(_paths, NullLogger.Instance);
        reloaded.Load();
        Assert.IsFalse(reloaded.FirstStart);
        Assert.AreEqual("debug", reloaded.App.LogLevel);
        var s = reloaded.Sources.Single();
        Assert.AreEqual(source.Id, s.Id);
        Assert.AreEqual("mirror", s.Type);
        Assert.IsTrue(s.CountFilesOnStart);
        Assert.AreEqual("\\\\aus-files-01\\shared", s.Settings!["paths"]!.GetValue<string>());
        Assert.AreEqual(30, s.Settings["checkIntervalSeconds"]!.GetValue<int>());
        var c = reloaded.Collections.Single();
        Assert.AreEqual("ben", c.Name);
        Assert.AreEqual("files", c.Sources.Single().Path);
        Assert.IsTrue(c.Sources.Single().ReadOnly);
        Assert.IsTrue(c.Mounts.Single().ReadOnly);
        Assert.AreEqual(11001, c.Mounts.Single().Settings!["port"]!.GetValue<int>());

        var json = File.ReadAllText(_paths.CollectionsFile);
        StringAssert.Contains(json, "\"version\": 1");
        StringAssert.Contains(json, "\"readOnly\": true");
        Assert.IsTrue(json.IndexOf("\"id\"", StringComparison.Ordinal) < json.IndexOf("\"name\"", StringComparison.Ordinal), "stable property order");
        Assert.AreEqual(0, Directory.GetFiles(_paths.Settings, "*.tmp-*").Length, "no temp files left behind");
    }

    [TestMethod]
    public void FirstStartCreatesCollectionOneWithTheNativeMount()
    {
        var registry = new PluginRegistry();
        var store = new SettingsStore(_paths, NullLogger.Instance);
        store.Load();
        Assert.IsTrue(store.EnsureDefaultCollection(registry));
        var collection = store.Collections.Single();
        Assert.AreEqual("Collection 1", collection.Name);
        Assert.IsTrue(collection.Enabled);
        Assert.AreEqual(0, collection.Sources.Count);
        var mount = collection.Mounts.Single();
        Assert.AreEqual(SettingsStore.NativeProviderForThisOs(), mount.Provider);
        Assert.AreEqual(SettingsStore.DefaultMountName(mount.Provider), mount.Name);
        Assert.IsTrue(File.Exists(_paths.CollectionsFile));
        Assert.IsTrue(File.Exists(_paths.SourcesFile));
        Assert.IsTrue(File.Exists(_paths.AppSettingsFile));

        var again = new SettingsStore(_paths, NullLogger.Instance);
        again.Load();
        Assert.IsFalse(again.EnsureDefaultCollection(registry), "only on a first start");
    }

    [TestMethod]
    public void CorruptFilesAreQuarantinedNotFatal()
    {
        Directory.CreateDirectory(_paths.Settings);
        File.WriteAllText(_paths.SourcesFile, "{ not json");
        var store = new SettingsStore(_paths, NullLogger.Instance);
        store.Load();
        Assert.AreEqual(0, store.Sources.Count);
        Assert.IsFalse(File.Exists(_paths.SourcesFile));
        Assert.AreEqual(1, Directory.GetFiles(_paths.Settings, "sources.json.bad-*").Length);
    }

    [TestMethod]
    public void NewerVersionsAndUnknownMembershipsAreKept()
    {
        Directory.CreateDirectory(_paths.Settings);
        var unknown = Guid.NewGuid();
        File.WriteAllText(_paths.SourcesFile, """{ "version": 99, "sources": [] }""");
        File.WriteAllText(_paths.CollectionsFile, $$"""{ "version": 1, "collections": [ { "name": "ben", "sources": [ { "id": "{{unknown}}", "path": " /Files\\x/ ", "readOnly": false } ], "mounts": [ { "provider": "http" } ] } ] }""");
        var store = new SettingsStore(_paths, NullLogger.Instance);
        store.Load();
        var c = store.Collections.Single();
        Assert.AreNotEqual(Guid.Empty, c.Id, "missing ids are generated");
        Assert.AreEqual(unknown, c.Sources.Single().Id, "a membership of an unknown source is kept, not deleted");
        Assert.AreEqual("Files/x", c.Sources.Single().Path, "paths are normalised");
        Assert.AreEqual("HTTP", c.Mounts.Single().Name, "a missing mount name defaults to the provider's");
    }

    [TestMethod]
    public void DefaultSourcePathIsTypeFolderSlashName()
    {
        Assert.AreEqual("files/aus-files-01", SettingsStore.DefaultSourcePath(new FakePlugin(), "aus-files-01"));
    }

    [TestMethod]
    public void AtomicWriteReplacesTheWholeFile()
    {
        var path = Path.Combine(_root, "x.json");
        JsonFiles.WriteAtomic(path, new AppSettings { LogLevel = "a-much-longer-value" }, CoreJsonContext.Default.AppSettings);
        JsonFiles.WriteAtomic(path, new AppSettings { LogLevel = "b" }, CoreJsonContext.Default.AppSettings);
        var read = JsonFiles.Read(path, CoreJsonContext.Default.AppSettings);
        Assert.AreEqual("b", read!.LogLevel);
        Assert.IsNull(JsonFiles.Read(Path.Combine(_root, "missing.json"), CoreJsonContext.Default.AppSettings));
        File.WriteAllText(path, "");
        Assert.IsNull(JsonFiles.Read(path, CoreJsonContext.Default.AppSettings), "an empty file reads as missing");
    }

    private sealed class FakePlugin : ISourcePlugin
    {
        public string TypeId => "fake";

        public string DisplayName => "Fake";

        public string Description => "";

        public string TypeFolder => "files";

        public Stream? OpenIcon() => null;

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings() => new();

        public ISource CreateSource(object settings) => throw new NotSupportedException();
    }
}

[TestClass]
public class PluginSettingsBinderTests
{
    public enum Mode
    {
        Fast,
        Safe,
    }

    public sealed class Poco
    {
        [Setting("Host", Required = true, Order = 1)]
        public string Host { get; set; } = "default-host";

        [Setting("Port", Order = 2)]
        public int Port { get; set; } = 22;

        [Setting("Password", Secret = true, Order = 3)]
        public string? Password { get; set; }

        [Setting("Mode", Order = 0)]
        public Mode Mode { get; set; } = Mode.Fast;

        public bool NoAttribute { get; set; }
    }

    [TestMethod]
    public void DescribeOrdersByOrderAndDefaultsTheLabel()
    {
        var props = PluginSettingsBinder.Describe(typeof(Poco));
        // Order first, then declaration order among equals (Mode is declared before NoAttribute)
        CollectionAssert.AreEqual(new[] { "Mode", "NoAttribute", "Host", "Port", "Password" }, props.Select(p => p.Name).ToArray());
        Assert.AreEqual("NoAttribute", props[1].Attribute.Label, "a property without the attribute is labelled by its name");
        Assert.IsTrue(props.Single(p => p.Name == "Password").IsSecret);
        Assert.AreEqual("noAttribute", props[1].JsonName);
    }

    [TestMethod]
    public void MaterialiseUsesDefaultsForMissingPropertiesAndFillsSecrets()
    {
        var json = JsonNode.Parse("""{ "port": 2222, "mode": "Safe", "unknown": 1 }""")!.AsObject();
        var poco = (Poco)PluginSettingsBinder.Materialise(typeof(Poco), json, name => name == "Password" ? "hunter2" : null);
        Assert.AreEqual("default-host", poco.Host);
        Assert.AreEqual(2222, poco.Port);
        Assert.AreEqual(Mode.Safe, poco.Mode);
        Assert.AreEqual("hunter2", poco.Password);

        var empty = (Poco)PluginSettingsBinder.Materialise(typeof(Poco), null);
        Assert.AreEqual(22, empty.Port);
        Assert.IsNull(empty.Password);
    }

    [TestMethod]
    public void SerialiseStripsSecretsAndHandsThemToTheSink()
    {
        var poco = new Poco { Host = "h", Password = "s3cret", Mode = Mode.Safe };
        var secrets = new Dictionary<string, string?>();
        var json = PluginSettingsBinder.Serialise(typeof(Poco), poco, (k, v) => secrets[k] = v);
        Assert.IsFalse(json.ContainsKey("password"));
        Assert.IsFalse(json.ToJsonString().Contains("s3cret", StringComparison.Ordinal));
        Assert.AreEqual("h", json["host"]!.GetValue<string>());
        Assert.AreEqual("Safe", json["mode"]!.GetValue<string>());
        Assert.AreEqual("s3cret", secrets["Password"]);
        Assert.IsFalse(PluginSettingsBinder.Scrubbed(typeof(Poco), poco).ToJsonString().Contains("s3cret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BadJsonThrows()
    {
        var json = JsonNode.Parse("""{ "port": "not a number" }""")!.AsObject();
        Assert.ThrowsExactly<JsonException>(() => PluginSettingsBinder.Materialise(typeof(Poco), json));
    }
}

[TestClass]
public class SecretStoreTests
{
    private string _root = "";

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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

    [TestMethod]
    public void RoundTripsAcrossInstancesWithoutPlaintextOnDisk()
    {
        var file = Path.Combine(_root, "secrets.json");
        var keys = Path.Combine(_root, "keys");
        var owner = Guid.NewGuid();
        var store = new SecretStore(file, keys, NullLogger.Instance);
        store.Set(owner, "Password", "hunter2");
        store.Set(owner, "Token", "tok");
        Assert.AreEqual("hunter2", store.Get(owner, "Password"));
        Assert.IsNull(store.Get(owner, "Nope"));
        Assert.IsNull(store.Get(Guid.NewGuid(), "Password"));

        var text = File.ReadAllText(file);
        Assert.IsFalse(text.Contains("hunter2", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains(owner.ToString("N") + "/Password", StringComparison.Ordinal));

        var again = new SecretStore(file, keys, NullLogger.Instance);
        Assert.AreEqual("hunter2", again.Get(owner, "Password"));
        Assert.AreEqual(2, again.Count);

        var scoped = again.For(owner);
        Assert.AreEqual("tok", scoped.Get("Token"));
        scoped.Set("Token", null);
        Assert.IsNull(again.Get(owner, "Token"));
        again.RemoveAll(owner);
        Assert.AreEqual(0, again.Count);
        Assert.AreEqual(0, new SecretStore(file, keys, NullLogger.Instance).Count);
    }
}
