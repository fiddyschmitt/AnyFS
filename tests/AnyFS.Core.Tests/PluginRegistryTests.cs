using AnyFS.Core.Plugins;
using AnyFS.Core.Settings;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Core.Tests;

[TestClass]
public class PluginRegistryTests
{
    private static string SamplePluginDll => Path.Combine(AppContext.BaseDirectory, "AnyFS.SamplePlugin.dll");

    [TestMethod]
    public void BuiltInsRegisterAndDuplicatesAreRecorded()
    {
        var registry = new PluginRegistry();
        registry.AddBuiltIn(new FakeSourcePlugin("one"));
        registry.AddBuiltIn(new FakeSourcePlugin("ONE"));
        registry.AddBuiltIn(new FakeMountProvider("http"));
        Assert.AreEqual(1, registry.SourcePlugins.Count);
        Assert.IsNotNull(registry.FindSource("One"));
        Assert.IsNotNull(registry.FindMountProvider("HTTP"));
        Assert.IsNull(registry.FindMountProvider("smb"));
        var duplicate = registry.Plugins.Single(p => p.Error is not null);
        StringAssert.Contains(duplicate.Error, "already registered");
        Assert.AreEqual(3, registry.Plugins.Count);
    }

    [TestMethod]
    public void LoadsTheSamplePluginFromAPluginsFolder()
    {
        Assert.IsTrue(File.Exists(SamplePluginDll), $"expected the sample plugin beside the tests at {SamplePluginDll}");
        var dir = Path.Combine(Path.GetTempPath(), "anyfs-tests", "plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // read+write rather than File.Copy: the DLL may already be mapped by another test's load context
            File.WriteAllBytes(Path.Combine(dir, "AnyFS.SamplePlugin.dll"), File.ReadAllBytes(SamplePluginDll));
            File.WriteAllText(Path.Combine(dir, "garbage.dll"), "this is not a .NET assembly");
            var registry = new PluginRegistry();
            Assert.AreEqual(1, registry.LoadDirectory(dir, NullLogger.Instance));

            var plugin = registry.FindSource("sample");
            Assert.IsNotNull(plugin);
            Assert.AreEqual("Sample", plugin.DisplayName);
            Assert.AreEqual("sample", plugin.TypeFolder);
            var sampleInfo = registry.Plugins.Single(p => p.TypeId == "sample");
            Assert.AreEqual("source", sampleInfo.Kind);
            StringAssert.EndsWith(sampleInfo.Origin, "AnyFS.SamplePlugin.dll");
            Assert.IsNull(sampleInfo.Error);

            var garbage = registry.Plugins.Single(p => p.Origin.EndsWith("garbage.dll", StringComparison.Ordinal));
            Assert.IsNotNull(garbage.Error);

            // what the settings layer does with a third-party POCO: reflection and JSON through the binder
            var props = PluginSettingsBinder.Describe(plugin.SettingsType);
            CollectionAssert.AreEquivalent(new[] { "Greeting", "Files", "Token" }, props.Select(p => p.Name).ToArray());
            Assert.IsTrue(props.Single(p => p.Name == "Token").IsSecret);
            var settings = PluginSettingsBinder.Materialise(plugin.SettingsType, System.Text.Json.Nodes.JsonNode.Parse("""{ "greeting": "hi", "files": 2 }""")!.AsObject(), _ => "tok");
            var json = PluginSettingsBinder.Serialise(plugin.SettingsType, settings);
            Assert.AreEqual("hi", json["greeting"]!.GetValue<string>());
            Assert.AreEqual(2, json["files"]!.GetValue<int>());
            Assert.IsFalse(json.ContainsKey("token"));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the loaded DLL stays mapped (non-collectible load context) and Windows keeps it locked
            }
        }
    }

    [TestMethod]
    public async Task TheSamplePluginServesItsTree()
    {
        var registry = new PluginRegistry();
        registry.LoadFile(SamplePluginDll, NullLogger.Instance);
        var plugin = registry.FindSource("sample")!;
        var settings = PluginSettingsBinder.Materialise(plugin.SettingsType, null);
        var source = plugin.CreateSource(settings);
        await source.InitialiseAsync(new Sdk.Testing.TestSourceContext("sample"), CancellationToken.None);
        var names = await Handles.NamesAsync(source, "");
        CollectionAssert.Contains(names, "hello.txt");
        CollectionAssert.Contains(names, "file3.txt");
        StringAssert.StartsWith(await Handles.ReadTextAsync(source, "hello.txt"), "Hello from the sample plugin");
        Assert.AreEqual(Sdk.EnumerationState.Complete, source.Counters.State);
        await source.DisposeAsync();
    }

    [TestMethod]
    public void MissingDirectoryLoadsNothing()
    {
        var registry = new PluginRegistry();
        Assert.AreEqual(0, registry.LoadDirectory(Path.Combine(Path.GetTempPath(), "anyfs-tests", "no-such-dir"), NullLogger.Instance));
    }

    [TestMethod]
    public void SdkCompatibility()
    {
        var host = SdkInfo.SdkVersion;
        Assert.IsTrue(PluginRegistry.IsSdkCompatible(host, out _));
        Assert.IsTrue(PluginRegistry.IsSdkCompatible(new Version(host.Major, host.Minor, host.Build + 5), out _), "a newer patch is fine");
        Assert.IsFalse(PluginRegistry.IsSdkCompatible(new Version(host.Major + 1, 0, 0), out var reason));
        StringAssert.Contains(reason, "built against");
        Assert.IsFalse(PluginRegistry.IsSdkCompatible(null, out reason));
        StringAssert.Contains(reason, "does not reference");
        if (host.Major == 0)
        {
            Assert.IsFalse(PluginRegistry.IsSdkCompatible(new Version(0, host.Minor + 1, 0), out _), "before 1.0 a newer minor is a breaking change");
            if (host.Minor > 0)
            {
                Assert.IsTrue(PluginRegistry.IsSdkCompatible(new Version(0, host.Minor - 1, 0), out _));
            }
        }
    }

    internal sealed class FakeSourcePlugin(string typeId) : ISourcePlugin
    {
        public string TypeId => typeId;

        public string DisplayName => typeId;

        public string Description => "";

        public string TypeFolder => "fake";

        public Stream? OpenIcon() => null;

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings() => new();

        public ISource CreateSource(object settings) => throw new NotSupportedException();
    }

    internal sealed class FakeMountProvider(string typeId) : IMountProviderPlugin
    {
        public string TypeId => typeId;

        public string DisplayName => typeId;

        public string Description => "";

        public Stream? OpenIcon() => null;

        public IReadOnlyList<System.Runtime.InteropServices.OSPlatform> SupportedPlatforms { get; } = [System.Runtime.InteropServices.OSPlatform.Windows, System.Runtime.InteropServices.OSPlatform.Linux, System.Runtime.InteropServices.OSPlatform.OSX];

        public string? UnavailableReason => null;

        public PerformanceTier Tier => PerformanceTier.Protocol;

        public bool SupportsWrites => true;

        public Type SettingsType => typeof(object);

        public object CreateDefaultSettings(ICollectionInfo collection) => new();

        public IMount CreateMount(object settings, ICollectionInfo collection) => throw new NotSupportedException();
    }
}
