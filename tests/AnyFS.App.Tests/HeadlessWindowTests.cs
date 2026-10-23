using AnyFS.App.Gui;
using AnyFS.App.Gui.Views;
using AnyFS.App.Tests;
using AnyFS.Core.Plugins;
using AnyFS.Sdk;
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

    /// <summary>
    /// The window answering while a source lists a big server at its start, as 3DX lists 200,000 documents: pages of records
    /// kept, eight at a time, a progress report each. ANYFS_TEST_UI_LOAD=1 runs it; it prints how long the window took to
    /// answer (each a hop onto its thread from outside), before and during the listing.
    /// </summary>
    [TestMethod]
    public async Task TheWindowAnswersWhileASourceListsAtItsStart()
    {
        if (Environment.GetEnvironmentVariable("ANYFS_TEST_UI_LOAD") is not ("1" or "true"))
        {
            Assert.Inconclusive("measures the window under a listing's load: ANYFS_TEST_UI_LOAD=1");
        }

        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "window-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Session.Dispatch(async () =>
            {
                var registry = new PluginRegistry();
                registry.AddBuiltIn(new MemoryPlugin());
                registry.AddBuiltIn(new FakeMountProvider());
                registry.AddBuiltIn(new ListingPlugin());
                var host = GuiHost.Create(new GuiOptions(Path.Combine(root, "settings")), registry);
                host.Start();
                var window = new MainWindow(host);
                window.Show();
                await Task.Delay(500);

                var idle = await AnswerTimesAsync(TimeSpan.FromSeconds(3));
                var collection = host.Engine.Settings.Collections.Single();
                var source = await host.Engine.AddSourceAsync("big", "listing", true, false, new MemoryPluginSettings());
                await host.Engine.AddMembershipAsync(collection.Id, source.Id, "big", false);
                var busy = await AnswerTimesAsync(TimeSpan.FromSeconds(15));
                Console.WriteLine($"idle: {Describe(idle)}");
                Console.WriteLine($"while listing: {Describe(busy)}");
                Console.WriteLine($"gen0 {GC.CollectionCount(0)}, gen1 {GC.CollectionCount(1)}, gen2 {GC.CollectionCount(2)}, heap {GC.GetTotalMemory(false) >> 20} MiB");

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

    /// <summary>How long the window's thread took to run a hop onto it, every 20 ms for <paramref name="period"/>, from a thread of its own.</summary>
    private static Task<List<double>> AnswerTimesAsync(TimeSpan period) => Task.Run(async () =>
    {
        var times = new List<double>();
        var until = DateTime.UtcNow + period;
        while (DateTime.UtcNow < until)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Input);
            times.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await Task.Delay(20);
        }

        return times;
    });

    private static string Describe(List<double> times)
    {
        var sorted = times.OrderBy(t => t).ToList();
        return $"{sorted.Count} hops, median {sorted[sorted.Count / 2]:F1} ms, 95th {sorted[(int)(sorted.Count * 0.95)]:F1} ms, worst {sorted[^1]:F1} ms, over 100 ms {sorted.Count(t => t > 100)}";
    }

    /// <summary>A source whose start lists like 3DX's: pages of records, eight at a time, kept, a progress report each.</summary>
    private sealed class ListingPlugin : ISourcePlugin
    {
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
                var kept = new System.Collections.Concurrent.ConcurrentBag<List<(string Id, string Name, string Title, string[] Files, DateTimeOffset Modified)>>();
                var done = 0;
                const int pages = 2000;
                await Parallel.ForEachAsync(Enumerable.Range(0, pages), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (page, token) =>
                {
                    await Task.Delay(40, token);
                    var json = new System.Text.StringBuilder("[");
                    for (var i = 0; i < 100; i++)
                    {
                        json.Append($"{{\"id\":\"{Guid.NewGuid():N}\",\"name\":\"DOC-{page}-{i}\",\"title\":\"A document title of some length {page} {i}\",\"files\":[\"a.pdf\",\"b.docx\"],\"modified\":\"2026-10-23T10:00:00Z\"}},");
                    }

                    json.Length--;
                    json.Append(']');
                    using var parsed = System.Text.Json.JsonDocument.Parse(json.ToString());
                    kept.Add(parsed.RootElement.EnumerateArray().Select(e => (e.GetProperty("id").GetString()!, e.GetProperty("name").GetString()!, e.GetProperty("title").GetString()!,
                        e.GetProperty("files").EnumerateArray().Select(f => f.GetString()!).ToArray(), e.GetProperty("modified").GetDateTimeOffset())).ToList());
                    var now = Interlocked.Increment(ref done);
                    ctx.ReportProgress($"Listing the documents: page {now:N0} of {pages:N0}", (double)now / pages);
                });
            },
        };
    }

    [TestMethod]
    public async Task AWindowThatIsBusyForMoreThanASecondSaysSoInTheLog()
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
                await Task.Delay(1500);

                // the window's thread held up: the watchdog's probe waits behind it
                Thread.Sleep(MainWindow.StallWorthLogging + TimeSpan.FromMilliseconds(800));
                await Task.Delay(1500);

                await host.ShutdownAsync();
                window.Close();
                return true;
            }, CancellationToken.None);

            var log = await File.ReadAllTextAsync(Path.Combine(root, "anyfs.log"));
            StringAssert.Contains(log, "window: its thread was busy for");
            StringAssert.Contains(log, "the garbage collector held every thread for");
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
