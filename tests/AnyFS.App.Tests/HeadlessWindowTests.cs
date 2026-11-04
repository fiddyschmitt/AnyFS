using AnyFS.App.Gui;
using AnyFS.App.Gui.ViewModels;
using AnyFS.App.Gui.Views;
using AnyFS.App.Tests;
using AnyFS.Core.Operations;
using AnyFS.Core.Plugins;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
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
    public async Task TheWindowOpensWithFourTabsAndCollectionOne()
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
                Assert.AreEqual(4, tabs.ItemCount);
                var headers = tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString()).ToList();
                CollectionAssert.AreEqual(new[] { "Collections", "All sources", "Operations", "Stats" }, headers);

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

    /// <summary>The Operations tab in the real window: its rows, their colours, and the XAML's bindings to the view model (spec 11.5).</summary>
    [TestMethod]
    public async Task TheOperationsTabShowsWhatTheSourcesAreAsked()
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
                var collection = host.Engine.Settings.Collections.Single();
                var definition = await host.Engine.AddSourceAsync("alpha", "memory", true, false, new MemoryPluginSettings { Files = "a.txt=A" });
                await host.Engine.AddMembershipAsync(collection.Id, definition.Id, "alpha", false);
                var handle = host.Engine.FindSourceRunner(definition.Id)!.Handle;
                await Wait.UntilAsync(() => handle.IsUsable, what: "alpha ready");

                // kept up to date only once it is the tab shown
                var tab = window.ViewModel.OperationsTab;
                Assert.IsFalse(tab.IsActive);
                Assert.AreEqual(0, tab.Rows.Count);
                var tabs = window.FindControl<TabControl>("Tabs")!;
                tabs.SelectedIndex = 2;
                Assert.IsTrue(tab.IsActive);
                Assert.AreEqual("Operations", ((TabItem)tabs.SelectedItem!).Header);
                await Task.Delay(100);
                window.UpdateLayout();

                // what a real call leaves behind: a read through the source's handle
                Assert.AreEqual("A", await ReadAllAsync(handle.Current!, "a.txt"));
                tab.Refresh();
                CollectionAssert.Contains(tab.Rows.Select(r => r.Operation).ToList(), "Read file");

                // a row of each kind, as the list draws it
                var running = host.Engine.Operations.Begin(definition.Id, "alpha", OperationKind.List, "running");
                var failed = host.Engine.Operations.Begin(definition.Id, "alpha", OperationKind.Delete, "failed");
                failed.Fail(new IOException("boom"));
                var ok = host.Engine.Operations.Begin(definition.Id, "alpha", OperationKind.Stat, "ok");
                ok.Complete();
                tab.Refresh();
                await Task.Delay(100);
                window.UpdateLayout();
                var rows = window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "OperationRows");
                Assert.AreEqual(tab.Rows.Count, rows.ItemCount);
                CollectionAssert.AreEqual(new[] { "ok", "failed", "running" }, tab.Rows.Take(3).Select(r => r.Path).ToList(), "newest at the top");

                var greenRow = RowBackground(rows, 0);
                Assert.IsTrue(greenRow.G > greenRow.R && greenRow.A > 0, $"a success is washed green: {greenRow}");
                var redRow = RowBackground(rows, 1);
                Assert.IsTrue(redRow.R > redRow.G && redRow.A > 0, $"a failure is washed red: {redRow}");
                Assert.AreEqual(0, RowBackground(rows, 2).A, "a running row has none");
                var texts = rows.ContainerFromIndex(1)!.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                CollectionAssert.Contains(texts, "alpha");
                CollectionAssert.Contains(texts, "Delete");
                CollectionAssert.Contains(texts, "failed");
                CollectionAssert.Contains(texts, "boom");

                // every header sorts, through its binding and parameter
                var headers = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("colhead")).ToList();
                Assert.AreEqual(7, headers.Count);
                foreach (var (header, title) in headers.Zip(new[] { "Started", "Source", "Operation", "Path", "Duration (s)", "Detail", "Result" }))
                {
                    StringAssert.StartsWith(header.Content?.ToString(), title);
                    Assert.IsTrue(header.Command!.CanExecute(header.CommandParameter));
                    header.Command.Execute(header.CommandParameter);
                    await Task.Delay(10);
                    window.UpdateLayout();
                    Assert.AreEqual(Enum.Parse<OperationColumn>((string)header.CommandParameter!), tab.SortColumn, title);
                    StringAssert.Contains(header.Content?.ToString(), tab.SortDescending ? "▼" : "▲", title);
                }

                tab.SortCommand.Execute("Started");
                Assert.IsTrue(tab.SortDescending, "Started begins with the latest");

                // the filters, as the controls set them
                var longer = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "LongerThan");
                longer.Text = "1000";
                Assert.AreEqual("1000", tab.LongerThanText);
                Assert.AreEqual(0, tab.Rows.Count);
                longer.Text = "";
                Assert.IsTrue(tab.Rows.Count > 0);

                var failures = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "Failures only");
                failures.IsChecked = true;
                Assert.IsTrue(tab.Rows.All(r => r.Outcome == OperationOutcome.Failed));
                failures.IsChecked = false;

                var filters = window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "SourceFilters");
                await Task.Delay(100);
                window.UpdateLayout();
                var alpha = filters.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content as string == "alpha");
                Assert.IsTrue(alpha.IsChecked);
                alpha.IsChecked = false;
                Assert.AreEqual(0, tab.Rows.Count, "unticked, the source's rows go");
                alpha.IsChecked = true;
                Assert.IsTrue(tab.Rows.Count > 0);

                running.Complete();
                tabs.SelectedIndex = 3;
                Assert.IsFalse(tab.IsActive, "another tab shown, the rows are left alone");

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

    private static async Task<string> ReadAllAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var buffer = new byte[file.Length];
        var n = await file.ReadFullyAsync(0, buffer, CancellationToken.None);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, n);
    }

    private static Color RowBackground(ItemsControl rows, int index)
    {
        var row = rows.ContainerFromIndex(index);
        Assert.IsNotNull(row, $"row {index} was realised");
        var border = row.GetVisualDescendants().OfType<Border>().Prepend(row as Border).OfType<Border>().First(b => b.Classes.Contains("oprow"));
        return ((ISolidColorBrush)border.Background!).Color;
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

    /// <summary>
    /// A collection with a mount from the last run is renamed in the real window: the window picks the change up by itself, and
    /// the Open containing folder buttons of its tile, its source's and its mount's point at the mount's new path (the user, 4-Nov-2026).
    /// </summary>
    [TestMethod]
    public async Task RenamingACollectionFromTheLastRunMovesItsOpenButtonsInTheWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "window-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Session.Dispatch(async () =>
            {
                var registry = new PluginRegistry();
                registry.AddBuiltIn(new MemoryPlugin());
                registry.AddBuiltIn(new FakeMountProvider());

                // the last run: a collection, a source and a mount, saved
                var earlier = GuiHost.Create(new GuiOptions(Path.Combine(root, "settings")), registry);
                earlier.Start();
                var work = await earlier.Engine.AddCollectionAsync("work");
                var source = await earlier.Engine.AddSourceAsync("alpha", "memory", true, false, new MemoryPluginSettings { Files = "a.txt=A" });
                await earlier.Engine.AddMembershipAsync(work.Id, source.Id, "mem", false);
                await earlier.Engine.AddMountAsync(work.Id, "Fake", "fake", true, false, new FakeMountSettings { Port = 11 });
                await earlier.ShutdownAsync();

                var host = GuiHost.Create(new GuiOptions(Path.Combine(root, "settings")), registry);
                host.Start();
                var window = new MainWindow(host);
                window.Show();
                await Wait.UntilAsync(() => host.Engine.FindCollectionRunner(work.Id)?.Mounts.Single().Mount is not null, 15000, "the mount up");
                window.ViewModel.Refresh();
                window.ViewModel.Collections.SelectedCollection = window.ViewModel.Collections.Collections.Single(c => c.Id == work.Id);
                await Task.Delay(300);
                window.UpdateLayout();
                Assert.AreEqual("fake://11/work/", Opens(window, work.Id));

                await host.Engine.RenameCollectionAsync(work.Id, "workplace");

                // the window's own timer picks the rename up: no Refresh here
                await Wait.UntilAsync(() => Opens(window, work.Id) == "fake://11/workplace/", what: "the window to show the new path");
                window.UpdateLayout();

                // Collection 1's tile is there too, with nothing to open (no WinFsp in this registry): its button is the disabled one
                var buttons = window.GetVisualDescendants().OfType<OpenButton>().Where(o => o.DataContext is TileViewModel { OpenTargets.Count: > 0 }).ToList();
                Assert.AreEqual(3, buttons.Count, "the collection's, the source's and the mount's buttons were realised");
                foreach (var button in buttons)
                {
                    var tile = (TileViewModel)button.DataContext!;
                    Assert.IsTrue(tile.HasOneOpenTarget, tile.Name);
                    Assert.AreEqual("fake://11/workplace/", ((MountOpenAction.Browser)tile.OpenTargets.Single().Action).Url, tile.Name);
                    var visible = button.GetVisualDescendants().OfType<Button>().Single(b => b.IsVisible);
                    Assert.IsTrue(visible.IsEnabled && ReferenceEquals(visible.Command, tile.OpenSingleCommand), $"{tile.Name}: one enabled button, the tile's own command");
                }

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

        static string? Opens(MainWindow window, Guid collectionId)
            => (window.ViewModel.Collections.Collections.SingleOrDefault(c => c.Id == collectionId)?.OpenTargets.SingleOrDefault()?.Action as MountOpenAction.Browser)?.Url;
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
