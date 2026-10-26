using System.Text.Json;

namespace AnyFS.App.Tests;

/// <summary>What the app asks of the .NET runtime: anyfs.runtimeconfig.json, copied beside the tests with anyfs.dll.</summary>
[TestClass]
public class RuntimeConfigTests
{
    /// <summary>
    /// The garbage collector takes memory to be short from 97% in use, not 90%: from 95% (the default's 90 + 5) every thread
    /// that runs out of room while a background collection is under way waits for all of it, the window's among them, which
    /// held the window up for 0.35 to 0.5 s every few seconds while sources listed at the start (26-Oct-2026).
    /// </summary>
    [TestMethod]
    public void TheCollectorTakesMemoryToBeShortFrom97PercentInUse()
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "anyfs.runtimeconfig.json")));
        var properties = config.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");
        Assert.AreEqual(97, properties.GetProperty("System.GC.HighMemoryPercent").GetInt32());
    }
}
