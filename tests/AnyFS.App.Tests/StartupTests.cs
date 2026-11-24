using AnyFS.App.Cli;
using AnyFS.App.Gui;
using AnyFS.App.Gui.Views;
using AnyFS.Core.Plugins;
using AnyFS.Core.Settings;
using AnyFS.Sdk.Testing;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;

namespace AnyFS.App.Tests;

/// <summary>How AnyFS starts: one at a time on a settings folder, and what keeps it from starting is said (the review of 21-Nov-2026, R7f and R7g).</summary>
[TestClass]
public class StartupTests
{
    private static HeadlessUnitTestSession Session => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(StartupTests).Assembly);

    private static PluginRegistry Registry()
    {
        var registry = new PluginRegistry();
        registry.AddBuiltIn(new MemoryPlugin());
        registry.AddBuiltIn(new FakeMountProvider());
        return registry;
    }

    [TestMethod]
    public async Task ASecondAnyFsOnTheSameSettingsIsRefusedAndTheFirstGoesOn()
    {
        // Each copy keeps the settings, the secrets and the stats in memory and writes each file whole: with two, the one
        // that wrote last won, and a session one had just renewed was gone at the other's next save
        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "startup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new GuiOptions(Path.Combine(root, "settings"));
            var first = GuiHost.Create(options, Registry());
            first.Start();

            var refused = Assert.ThrowsExactly<SettingsInUseException>(() => GuiHost.Create(options, Registry()));
            StringAssert.Contains(refused.Message, first.Paths.Settings);
            Assert.AreEqual(3, await Commands.ServeAsync(options.SettingsDir, null, null, CancellationToken.None), "anyfs serve says so by its exit code");

            // the first is as it was: it takes a change and keeps it
            var added = await first.Engine.AddCollectionAsync("work");
            await first.ShutdownAsync();

            // and the folder is free once it has stopped
            var next = GuiHost.Create(options, Registry());
            Assert.IsTrue(next.Engine.Settings.Collections.Any(c => c.Id == added.Id));
            await next.ShutdownAsync();
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [TestMethod]
    public async Task WhatKeepsAnyFsFromStartingIsSaidInAWindow()
    {
        await Session.Dispatch(() =>
        {
            var inUse = new StartupFailureWindow(new SettingsInUseException("AnyFS is already running with these settings."), null);
            inUse.Show();
            Assert.AreEqual("AnyFS is already running with these settings.", inUse.Message);
            Assert.AreEqual(inUse.Message, inUse.GetVisualDescendants().OfType<SelectableTextBlock>().Single().Text, "the window says it");
            Assert.AreEqual(3, inUse.ExitCode);
            inUse.Close();

            var options = new GuiOptions(Path.Combine(Path.GetTempPath(), "anyfs-tests", "nowhere", "settings"));
            var unread = new StartupFailureWindow(new IOException("sources.json is being used by another process."), options);
            StringAssert.Contains(unread.Message, "AnyFS could not start");
            StringAssert.Contains(unread.Message, "being used by another process");
            StringAssert.Contains(unread.Message, "anyfs.log", "where the log is");
            Assert.AreEqual(1, unread.ExitCode);
            return true;
        }, CancellationToken.None);
    }
}
