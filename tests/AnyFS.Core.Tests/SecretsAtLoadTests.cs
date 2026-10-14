using AnyFS.Core.Plugins;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Sdk.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Core.Tests;

/// <summary>
/// Spec 9.3: a secret written into sources.json or collections.json by hand or by a script (the E2E lab's
/// settings) moves to the secret store on the next load and leaves the file, the way Import does.
/// </summary>
[TestClass]
public class SecretsAtLoadTests
{
    private string _root = "";
    private SettingsPaths _paths = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "secrets-" + Guid.NewGuid().ToString("N"));
        _paths = SettingsPaths.Resolve(Path.Combine(_root, "settings"));
        Directory.CreateDirectory(_paths.Settings);
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

    [TestMethod]
    public async Task SecretsInTheJsonFilesMoveToTheSecretStoreOnLoad()
    {
        var sourceId = Guid.NewGuid();
        var mountId = Guid.NewGuid();
        File.WriteAllText(_paths.SourcesFile, $$"""
            { "version": 1, "sources": [
              { "id": "{{sourceId}}", "name": "mem", "type": "memory", "enabled": true,
                "settings": { "canWrite": true, "secret": "s3cret-value" } } ] }
            """);
        File.WriteAllText(_paths.CollectionsFile, $$"""
            { "version": 1, "collections": [
              { "id": "{{Guid.NewGuid()}}", "name": "work", "enabled": true,
                "sources": [ { "id": "{{sourceId}}", "path": "", "readOnly": false } ],
                "mounts": [ { "id": "{{mountId}}", "name": "Fake", "provider": "fake", "enabled": true, "readOnly": false,
                              "settings": { "port": 7, "token": "t0ken-value" } } ] } ] }
            """);

        await using (var engine = new AnyFsEngine(_paths, Registry(), NullLoggerFactory.Instance))
        {
            engine.Load();
            Assert.AreEqual("s3cret-value", engine.Secrets.Get(sourceId, "Secret"), "the source's secret is in the store");
            Assert.AreEqual("t0ken-value", engine.Secrets.Get(mountId, "Token"), "the mount's secret is in the store");
            var sources = File.ReadAllText(_paths.SourcesFile);
            var collections = File.ReadAllText(_paths.CollectionsFile);
            Assert.IsFalse(sources.Contains("s3cret-value"), "sources.json was rewritten without the secret: " + sources);
            StringAssert.Contains(sources, "\"canWrite\": true", "the other settings stay");
            Assert.IsFalse(collections.Contains("t0ken-value"), "collections.json was rewritten without the secret: " + collections);
            StringAssert.Contains(collections, "\"port\": 7");

            // the plugin receives the value from the store, as if the window had typed it in
            await engine.StartAsync(null, CancellationToken.None);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && engine.Snapshot().Collections.Single().Sources.Single().State != "Ready")
            {
                await Task.Delay(50);
            }

            var vfs = engine.Collections.Single().Vfs;
            var entry = await vfs.StatAsync("secret.txt", CancellationToken.None);
            Assert.IsNotNull(entry, "MemoryPlugin adds secret.txt when it was given a secret");
            Assert.AreEqual("s3cret-value".Length, entry.Length);
        }

        // a second load finds nothing to move and leaves the files alone
        var before = (File.GetLastWriteTimeUtc(_paths.SourcesFile), File.GetLastWriteTimeUtc(_paths.CollectionsFile));
        await using var again = new AnyFsEngine(_paths, Registry(), NullLoggerFactory.Instance);
        again.Load();
        Assert.AreEqual(before, (File.GetLastWriteTimeUtc(_paths.SourcesFile), File.GetLastWriteTimeUtc(_paths.CollectionsFile)));
        Assert.AreEqual("s3cret-value", again.Secrets.Get(sourceId, "Secret"));
    }
}
