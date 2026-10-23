using AnyFS.App.Gui;
using AnyFS.App.Gui.Views;
using AnyFS.App.Tests;
using AnyFS.Core.Plugins;
using AnyFS.Sdk.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace AnyFS.App.Tests;

/// <summary>Builds the real window on Avalonia's headless platform (no display needed; CI runs this on Linux too).</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

[TestClass]
public class HeadlessWindowTests
{
    /// <summary>One session per test assembly; Avalonia tears it down when the process ends.</summary>
    private static HeadlessUnitTestSession Session => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessWindowTests).Assembly);

    [TestMethod]
    public async Task TheWindowOpensWithThreeTabsAndCollectionOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "window-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Session.Dispatch(async () =>
            {
                var registry = new PluginRegistry();
                registry.AddBuiltIn(new MemoryPlugin());
                registry.AddBuiltIn(new FakeMountProvider());
                var host = GuiHost.Create(new GuiOptions(Path.Combine(root, "settings")), registry);
                host.Start();
                var window = new MainWindow(host);
                window.Show();
                await Task.Delay(500);
                window.ViewModel.Refresh();

                var tabs = window.FindControl<TabControl>("Tabs");
                Assert.IsNotNull(tabs);
                Assert.AreEqual(3, tabs.ItemCount);
                var headers = tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString()).ToList();
                CollectionAssert.AreEqual(new[] { "Collections", "All sources", "Stats" }, headers);

                var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "CollectionsList");
                Assert.AreEqual(1, list.ItemCount);
                Assert.AreEqual("Collection 1", window.ViewModel.Collections.Collections.Single().Name);
                Assert.IsNotNull(list.ContainerFromIndex(0), "the tile was realised");
                StringAssert.StartsWith(window.Title, "AnyFS");

                await host.ShutdownAsync();
                window.Close();
                return true;
            }, CancellationToken.None);
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
    public async Task ASourcesIconShowsItsTypeAndTheOpenButtonsNoTooltip()
    {
        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "window-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Session.Dispatch(async () =>
            {
                var registry = new PluginRegistry();
                registry.AddBuiltIn(new MemoryPlugin());
                registry.AddBuiltIn(new FakeMountProvider());
                var host = GuiHost.Create(new GuiOptions(Path.Combine(root, "settings")), registry);
                host.Start();
                await Task.Delay(300);
                var collection = host.Engine.Settings.Collections.Single();
                var source = await host.Engine.AddSourceAsync("alpha", "memory", true, false, new MemoryPluginSettings { Files = "a.txt=A" });
                await host.Engine.AddMembershipAsync(collection.Id, source.Id, "alpha", false);
                var window = new MainWindow(host);
                window.Show();
                await Task.Delay(500);
                window.ViewModel.Refresh();
                await Task.Delay(300);

                var badges = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("badge") && b.DataContext is AnyFS.App.Gui.ViewModels.SourceTileViewModel).ToList();
                Assert.IsTrue(badges.Count > 0, "a source tile was realised");
                Assert.IsTrue(badges.All(b => (ToolTip.GetTip(b) as string) == "Memory"), "the source's icon names its type");

                var openButtons = window.GetVisualDescendants().OfType<OpenButton>().SelectMany(o => o.GetVisualDescendants().OfType<Button>()).ToList();
                Assert.IsTrue(openButtons.Count > 0, "Open containing folder buttons were realised");
                Assert.IsTrue(openButtons.All(b => ToolTip.GetTip(b) is null), "no tooltip on Open containing folder");

                await host.ShutdownAsync();
                window.Close();
                return true;
            }, CancellationToken.None);
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
}
