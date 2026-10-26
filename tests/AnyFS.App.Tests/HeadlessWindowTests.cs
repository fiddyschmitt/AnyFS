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

    /// <summary>
    /// The window while three SharePoint sources, added one after another, start and count their files (the user,
    /// 23-Oct-2026, after adding three SharePoint sources: "window: its thread was busy for 18059 ms"). ANYFS_TEST_UI_SHAREPOINT
    /// names a SharePoint Server site (the lab farm's http://192.168.0.92/sites/anyfs) and ANYFS_TEST_SPS_USER and
    /// _PASSWORD its account; it prints how long the window took to answer.
    /// </summary>
    [TestMethod]
    public async Task TheWindowAnswersWhileSharePointSourcesStart()
    {
        var site = Environment.GetEnvironmentVariable("ANYFS_TEST_UI_SHAREPOINT");
        var user = Environment.GetEnvironmentVariable("ANYFS_TEST_SPS_USER");
        var password = Environment.GetEnvironmentVariable("ANYFS_TEST_SPS_PASSWORD");
        if (string.IsNullOrEmpty(site) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        {
            Assert.Inconclusive("measures the window as SharePoint sources start: ANYFS_TEST_UI_SHAREPOINT, ANYFS_TEST_SPS_USER, ANYFS_TEST_SPS_PASSWORD");
        }

        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "window-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Session.Dispatch(async () =>
            {
                var host = GuiHost.Create(new GuiOptions(Path.Combine(root, "settings")), BuiltIns.CreateRegistry());
                host.Start();
                var window = new MainWindow(host);
                window.Show();
                await Task.Delay(1000);

                var idle = await AnswerTimesAsync(TimeSpan.FromSeconds(3));
                var collection = host.Engine.Settings.Collections.Single();
                var adding = AnswerTimesAsync(TimeSpan.FromSeconds(60));
                for (var i = 1; i <= 3; i++)
                {
                    var settings = new AnyFS.Sources.SharePoint.SharePointSettings { SiteUrl = site!, SignInMethod = AnyFS.Sources.SharePoint.SharePointSettings.Windows, Username = user, Password = password };
                    var source = await host.Engine.AddSourceAsync($"sp{i}", "sharepoint", true, true, settings);
                    await host.Engine.AddMembershipAsync(collection.Id, source.Id, $"sp{i}", false);
                    await Task.Delay(2000);
                }

                var busy = await adding;
                Console.WriteLine($"idle: {Describe(idle)}");
                Console.WriteLine($"adding and starting three SharePoint sources: {Describe(busy)}");
                await host.ShutdownAsync();
                window.Close();
                return true;
            }, CancellationToken.None);

            foreach (var line in (await File.ReadAllLinesAsync(Path.Combine(root, "anyfs.log"))).Where(l => l.Contains("window: its thread was busy", StringComparison.Ordinal)))
            {
                Console.WriteLine(line);
            }
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
    /// The window as sources are added while others keep their sign-in in the secret store (as SharePoint's app sign-in
    /// keeps its token cache) and something holds secrets.json for a moment after each write (an antivirus). The user's
    /// window was "busy for 18059 ms" after adding three SharePoint sources (23-Oct-2026). ANYFS_TEST_UI_LOAD=1; it
    /// prints how long the window took to answer.
    /// </summary>
    [TestMethod]
    public async Task TheWindowAnswersWhileSourcesKeepTheirSignInsAndTheSecretsFileIsHeld()
    {
        if (Environment.GetEnvironmentVariable("ANYFS_TEST_UI_LOAD") is not ("1" or "true"))
        {
            Assert.Inconclusive("measures the window under secret store load: ANYFS_TEST_UI_LOAD=1");
        }

        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "window-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Session.Dispatch(async () =>
            {
                var registry = new PluginRegistry();
                registry.AddBuiltIn(new MemoryPlugin());
                registry.AddBuiltIn(new FakeMountProvider());
                registry.AddBuiltIn(new KeepingPlugin());
                var host = GuiHost.Create(new GuiOptions(Path.Combine(root, "settings")), registry);
                host.Start();
                var window = new MainWindow(host);
                window.Show();
                await Task.Delay(500);

                var collection = host.Engine.Settings.Collections.Single();
                var first = await host.Engine.AddSourceAsync("keeping", "keeping", true, false, new MemoryPluginSettings { Secret = "s3cret" });
                await host.Engine.AddMembershipAsync(collection.Id, first.Id, "keeping", false);
                await Task.Delay(1000);

                // an antivirus scanning secrets.json each time it is written: held 200 ms
                var secrets = host.Engine.Paths.SecretsFile;
                using var scanner = new FileSystemWatcher(Path.GetDirectoryName(secrets)!, Path.GetFileName(secrets)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
                void Hold(object? _, FileSystemEventArgs e)
                {
                    try
                    {
                        var held = new FileStream(secrets, FileMode.Open, FileAccess.Read, FileShare.Read);
                        _ = Task.Delay(200).ContinueWith(_ => held.Dispose(), TaskScheduler.Default);
                    }
                    catch (IOException)
                    {
                    }
                }

                scanner.Changed += Hold;
                scanner.Renamed += (s, e) => Hold(s, e);
                scanner.EnableRaisingEvents = true;

                var measuring = AnswerTimesAsync(TimeSpan.FromSeconds(15));
                for (var i = 1; i <= 3; i++)
                {
                    var source = await host.Engine.AddSourceAsync($"added{i}", "keeping", true, false, new MemoryPluginSettings { Secret = $"s3cret{i}" });
                    await host.Engine.AddMembershipAsync(collection.Id, source.Id, $"added{i}", false);
                    await Task.Delay(1500);
                }

                var times = await measuring;
                scanner.EnableRaisingEvents = false;
                Console.WriteLine($"adding three sources while others keep their sign-ins: {Describe(times)}");
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

    /// <summary>A source that, once started, keeps a sign-in's cache in the secret store every 100 ms, as SharePoint's app sign-in keeps its token cache.</summary>
    private sealed class KeepingPlugin : ISourcePlugin
    {
        public string TypeId => "keeping";

        public string DisplayName => "Keeping";

        public string Description => "";

        public string TypeFolder => "keeping";

        public Stream? OpenIcon() => null;

        public Type SettingsType => typeof(MemoryPluginSettings);

        public object CreateDefaultSettings() => new MemoryPluginSettings();

        public ISource CreateSource(object settings) => new MemorySource
        {
            OnInitialise = (ctx, ct) =>
            {
                _ = Task.Run(async () =>
                {
                    for (var i = 0; i < 300 && !ct.IsCancellationRequested; i++)
                    {
                        try
                        {
                            ctx.Secrets.Set("token-cache", new string((char)('a' + (i % 26)), 20_000));
                        }
                        catch (IOException)
                        {
                        }

                        await Task.Delay(100, CancellationToken.None);
                    }
                }, CancellationToken.None);
                return Task.CompletedTask;
            },
        };
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

    /// <summary>
    /// The window held up for more than a quarter second says so in the log, with how much of the machine's memory is in use
    /// (a background collection holds up every thread that allocates once memory is short, 26-Oct-2026); held up again at
    /// once, it does not say so again.
    /// </summary>
    [TestMethod]
    public async Task AWindowThatIsHeldUpSaysSoInTheLog()
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

                // the window's thread held up: the watchdog's probe waits behind it; then held up again
                Thread.Sleep(MainWindow.StallWorthLogging + TimeSpan.FromMilliseconds(800));
                await Task.Delay(1500);
                Thread.Sleep(MainWindow.StallWorthLogging + TimeSpan.FromMilliseconds(800));
                await Task.Delay(1500);

                await host.ShutdownAsync();
                window.Close();
                return true;
            }, CancellationToken.None);

            var log = await File.ReadAllTextAsync(Path.Combine(root, "anyfs.log"));
            StringAssert.Contains(log, "window: its thread was busy for");
            StringAssert.Contains(log, "the garbage collector held every thread for");
            StringAssert.Contains(log, "% in use, which the collector takes to be high from ");
            Assert.AreEqual(1, log.Split('\n').Count(l => l.Contains("window: its thread was busy for", StringComparison.Ordinal)), "once in ten seconds: " + log);
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

    [TestMethod]
    public async Task ATypeWithAnIconShowsItInPlaceOfTheLetter()
    {
        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "window-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Session.Dispatch(async () =>
            {
                var registry = new PluginRegistry();
                registry.AddBuiltIn(new MemoryPlugin());
                registry.AddBuiltIn(new IconPlugin());
                registry.AddBuiltIn(new FakeMountProvider());
                var host = GuiHost.Create(new GuiOptions(Path.Combine(root, "settings")), registry);
                PluginIcons.Registry = host.Engine.Registry;
                host.Start();
                await Task.Delay(300);
                var collection = host.Engine.Settings.Collections.Single();
                foreach (var (name, type) in new[] { ("lettered", "memory"), ("pictured", "iconic") })
                {
                    var source = await host.Engine.AddSourceAsync(name, type, true, false, new MemoryPluginSettings());
                    await host.Engine.AddMembershipAsync(collection.Id, source.Id, name, false);
                }

                var window = new MainWindow(host);
                window.Show();
                await Task.Delay(500);
                window.ViewModel.Refresh();
                await Task.Delay(300);

                Border Badge(string name) => window.GetVisualDescendants().OfType<Border>()
                    .Single(b => b.Classes.Contains("badge") && b.DataContext is AnyFS.App.Gui.ViewModels.SourceTileViewModel tile && tile.Name == name);
                var pictured = Badge("pictured");
                Assert.IsTrue(pictured.Classes.Contains("icon"));
                Assert.IsNotNull(pictured.GetVisualDescendants().OfType<Image>().Single().Source, "the type's icon");
                Assert.IsFalse(pictured.GetVisualDescendants().OfType<TextBlock>().Single().IsVisible, "no letter behind it");

                var lettered = Badge("lettered");
                Assert.IsFalse(lettered.Classes.Contains("icon"));
                Assert.IsNull(lettered.GetVisualDescendants().OfType<Image>().Single().Source);
                Assert.IsTrue(lettered.GetVisualDescendants().OfType<TextBlock>().Single().IsVisible, "the letter on its square");

                await host.ShutdownAsync();
                window.Close();
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            PluginIcons.Registry = null;
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A Memory source whose type has an icon (a one-pixel PNG).</summary>
    private sealed class IconPlugin : ISourcePlugin
    {
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        private readonly MemoryPlugin _inner = new();

        public string TypeId => "iconic";

        public string DisplayName => "Iconic";

        public string Description => "";

        public string TypeFolder => "iconic";

        public Stream? OpenIcon() => new MemoryStream(Png);

        public Type SettingsType => typeof(MemoryPluginSettings);

        public object CreateDefaultSettings() => new MemoryPluginSettings();

        public ISource CreateSource(object settings) => _inner.CreateSource(settings);
    }
}
