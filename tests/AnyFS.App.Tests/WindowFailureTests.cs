using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AnyFS.App.Gui;
using AnyFS.App.Gui.Services;
using AnyFS.App.Gui.Views;
using AnyFS.Core.Model;
using AnyFS.Core.Plugins;
using AnyFS.Core.Settings;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.App.Tests;

/// <summary>
/// What the window, and the mounts with it, are kept through: its close while sources start, an exception on its thread,
/// a tick that fails, a file the shell will not open.
/// </summary>
[TestClass]
public class WindowFailureTests
{
    private static HeadlessUnitTestSession Session => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(WindowFailureTests).Assembly);

    /// <summary>
    /// The window closed while a source is still starting (the engine's start waits for the first source of a collection
    /// before its mounts). The cancellation ended the run before it had reached the part that stops things, and the
    /// shutdown, its run over, stopped nothing either: no source disposed, no stats flushed.
    /// </summary>
    [TestMethod]
    public async Task AWindowClosedWhileASourceStartsStopsItAllTheSame()
    {
        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "host-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = SettingsPaths.Resolve(Path.Combine(root, "settings"));
            var store = new SettingsStore(paths, NullLogger.Instance);
            store.Load();
            var source = new SourceDefinition { Name = "signing", Type = "waiting" };
            store.Sources.Add(source);
            var collection = new CollectionDefinition { Name = "c" };
            collection.Sources.Add(new SourceMembership { Id = source.Id, Path = "signing" });
            store.Collections.Add(collection);
            store.SaveSources();
            store.SaveCollections();

            var waiting = new WaitingPlugin();
            var registry = new PluginRegistry();
            registry.AddBuiltIn(waiting);
            registry.AddBuiltIn(new FakeMountProvider());
            var host = GuiHost.Create(new GuiOptions(Path.Combine(root, "settings")), registry);
            host.Start();
            await waiting.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await host.ShutdownAsync();
            Assert.IsTrue(waiting.Source!.Disposed, "the source that was starting is disposed");
            Assert.IsTrue(File.Exists(paths.StatsFile), "the stats are flushed");
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
    /// Something posted to the window's thread throws and nothing catches it. Unhandled, that ends the window's loop, and in
    /// the app the process and every mount with it. Handled as the app handles it, it is an error in the log and the window
    /// goes on.
    /// </summary>
    [TestMethod]
    public async Task AnExceptionOnTheWindowsThreadIsLoggedAndTheWindowGoesOn()
    {
        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "host-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Session.Dispatch(async () =>
            {
                var registry = new PluginRegistry();
                registry.AddBuiltIn(new MemoryPlugin());
                registry.AddBuiltIn(new FakeMountProvider());
                var host = GuiHost.Create(new GuiOptions(Path.Combine(root, "settings")), registry);

                // as the app does where it starts its window, which these tests do in its place
                Dispatcher.UIThread.UnhandledException += host.OnWindowThreadException;
                try
                {
                    host.Start();
                    var window = new MainWindow(host);
                    window.Show();
                    await Task.Delay(300);

                    var after = false;
                    Dispatcher.UIThread.Post(() => throw new InvalidOperationException("thrown on the window's thread"));
                    Dispatcher.UIThread.Post(() => after = true);
                    await Task.Delay(300);
                    Assert.IsTrue(after, "what was posted after it ran");

                    // a method that throws after an await with nothing awaiting it (an event's handler) comes the same way
                    ThrowAfterAnAwait();
                    await Task.Delay(300);
                    Assert.IsTrue(window.IsVisible, "the window is still there");

                    await host.ShutdownAsync();
                    window.Close();
                }
                finally
                {
                    Dispatcher.UIThread.UnhandledException -= host.OnWindowThreadException;
                }

                return true;
            }, CancellationToken.None);

            var errors = (await File.ReadAllLinesAsync(Path.Combine(root, "anyfs.log"))).Where(l => l.Contains(" ERROR AnyFS ", StringComparison.Ordinal)).ToList();
            Assert.AreEqual(2, errors.Count, "an error in the log for each");
            var log = await File.ReadAllTextAsync(Path.Combine(root, "anyfs.log"));
            StringAssert.Contains(log, "InvalidOperationException: thrown on the window's thread");
            StringAssert.Contains(log, "InvalidOperationException: thrown after an await");
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

        static async void ThrowAfterAnAwait()
        {
            await Task.Yield();
            throw new InvalidOperationException("thrown after an await");
        }
    }

    /// <summary>
    /// The window's quarter-second tick reads what plugins supply, a mount's access paths among it. A tick that throws is
    /// not thrown on by the dispatcher: it keeps the exception, and the timer is never started again, so the window showed
    /// what it showed then for as long as it stayed open. Kept through it, the window picks up what changes afterwards, and
    /// the failure is in the log once.
    /// </summary>
    [TestMethod]
    public async Task ATickThatFailsDoesNotStopTheWindowRefreshing()
    {
        var root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "host-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Session.Dispatch(async () =>
            {
                var provider = new FailingPathsProvider();
                var registry = new PluginRegistry();
                registry.AddBuiltIn(new MemoryPlugin());
                registry.AddBuiltIn(new FakeMountProvider());
                registry.AddBuiltIn(provider);
                var host = GuiHost.Create(new GuiOptions(Path.Combine(root, "settings")), registry);
                host.Start();
                var work = await host.Engine.AddCollectionAsync("work");
                await host.Engine.AddMountAsync(work.Id, "Failing", provider.TypeId, true, false, new FakeMountSettings());
                var window = new MainWindow(host);
                window.Show();
                await Wait.UntilAsync(() => Shown(window, work.Id) == "work", what: "the window to show the collection");

                // until the window's own ticks have met it twice: the second is the one that never came
                provider.FailsWith = new InvalidOperationException("the paths are not to be had");
                await Wait.UntilAsync(() => provider.FailedOnTheWindowsThread >= 2, 15000, "the window's next tick after one that failed");
                provider.FailsWith = null;

                // the window's own timer picks the rename up: no Refresh here
                await host.Engine.RenameCollectionAsync(work.Id, "workplace");
                await Wait.UntilAsync(() => Shown(window, work.Id) == "workplace", what: "the window to show the new name");

                await host.ShutdownAsync();
                window.Close();
                return true;
            }, CancellationToken.None);

            var lines = await File.ReadAllLinesAsync(Path.Combine(root, "anyfs.log"));
            Assert.AreEqual(1, lines.Count(l => l.Contains("window: its refresh failed", StringComparison.Ordinal)), "said once, however many ticks it lasted");
            Assert.AreEqual(1, lines.Count(l => l.Contains("window: it refreshes again", StringComparison.Ordinal)), "and once that it is over");
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

        static string? Shown(MainWindow window, Guid collectionId)
            => window.ViewModel.Collections.Collections.SingleOrDefault(c => c.Id == collectionId)?.Name;
    }

    /// <summary>
    /// "Open log" when the shell cannot open the file: a warning, as for a mount's folder that cannot be opened. The call
    /// was outside the catch its sibling has.
    /// </summary>
    [TestMethod]
    public void AFileTheShellCannotOpenIsAWarningNotAnException()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("only Windows' shell refuses a file that is not there itself; elsewhere a program is run to open it, which says so in its own way");
        }

        var log = new KeptLog();
        var missing = Path.Combine(Path.GetTempPath(), "anyfs-tests", "nothing-" + Guid.NewGuid().ToString("N"), "anyfs.log");
        new ShellOpener(log).OpenFile(missing);
        var line = log.Lines.Single();
        Assert.AreEqual(LogLevel.Warning, line.Level);
        StringAssert.Contains(line.Text, missing);
        Assert.IsInstanceOfType<System.ComponentModel.Win32Exception>(line.Exception);
    }

    /// <summary>
    /// "Open containing folder" ran the provider's command (the device-less <c>net use</c> for SMB) and waited for it on
    /// the window's thread, up to 15 s, twice where a connection is dropped first: the window stood still for as long
    /// (the review of 21-Nov-2026, P10). The open goes on elsewhere and the click returns.
    /// </summary>
    [TestMethod]
    public async Task OpeningAMountsFolderDoesNotHoldTheWindowForTheCommandBeforeIt()
    {
        var log = new KeptLog();
        var shown = new List<string>();
        var opener = new ShellOpener(log, (title, _) =>
        {
            shown.Add(title);
            return Task.CompletedTask;
        });

        // a command that takes three seconds, and opens nothing after it
        var slow = OperatingSystem.IsWindows()
            ? new MountOpenAction.Command("cmd.exe", ["/c", "ping -n 4 127.0.0.1 > nul"], null)
            : new MountOpenAction.Command("sleep", ["3"], null);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        opener.Open(slow, null);
        var returned = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        Assert.IsFalse(opener.Opening.IsCompleted, "the command is still running");
        Assert.IsTrue(returned < TimeSpan.FromSeconds(2), $"the click returned after {returned.TotalMilliseconds:F0} ms");
        await opener.Opening.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.IsTrue(System.Diagnostics.Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(2), "and the command was waited for, elsewhere");
        Assert.AreEqual(0, log.Lines.Count, string.Join("; ", log.Lines.Select(l => l.Text)));

        // a command that is not there is said in the log, by whoever ran it; text is shown at once, on the caller's thread
        opener.Open(new MountOpenAction.Command("anyfs-no-such-command-" + Guid.NewGuid().ToString("N"), [], null), null);
        await opener.Opening.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.AreEqual(LogLevel.Warning, log.Lines.Single().Level);
        opener.Open(new MountOpenAction.ShowText("Mount it", "sudo mount ..."), null);
        CollectionAssert.AreEqual(new[] { "Mount it" }, shown);
    }

    /// <summary>A source that never gets past its start, as one waiting for a sign-in.</summary>
    private sealed class WaitingPlugin : ISourcePlugin
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MemorySource? Source { get; private set; }

        public string TypeId => "waiting";

        public string DisplayName => "Waiting";

        public string Description => "";

        public string TypeFolder => "waiting";

        public Stream? OpenIcon() => null;

        public Type SettingsType => typeof(MemoryPluginSettings);

        public object CreateDefaultSettings() => new MemoryPluginSettings();

        public ISource CreateSource(object settings) => Source = new MemorySource
        {
            OnInitialise = async (_, ct) =>
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            },
        };
    }

    /// <summary>A mount provider whose mounts cannot say where they are while <see cref="FailsWith"/> is set, as a provider's own code may fail at any time.</summary>
    private sealed class FailingPathsProvider : IMountProviderPlugin
    {
        private Exception? _failsWith;
        private int _failedOnTheWindowsThread;

        /// <summary>What reading a mount's access paths throws; null to have them.</summary>
        public Exception? FailsWith
        {
            get => Volatile.Read(ref _failsWith);
            set => Volatile.Write(ref _failsWith, value);
        }

        /// <summary>How many times the window's thread asked for the paths and <see cref="FailsWith"/> was thrown (the engine's status tick asks too, on its own thread).</summary>
        public int FailedOnTheWindowsThread => Volatile.Read(ref _failedOnTheWindowsThread);

        public string TypeId => "failing-paths";

        public string DisplayName => "Failing paths";

        public string Description => "";

        public Stream? OpenIcon() => null;

        public IReadOnlyList<OSPlatform> SupportedPlatforms { get; } = [OSPlatform.Windows, OSPlatform.Linux, OSPlatform.OSX];

        public string? UnavailableReason => null;

        public PerformanceTier Tier => PerformanceTier.Protocol;

        public bool SupportsWrites => true;

        public Type SettingsType => typeof(FakeMountSettings);

        public object CreateDefaultSettings(ICollectionInfo collection) => new FakeMountSettings();

        public IMount CreateMount(object settings, ICollectionInfo collection) => new Mount(this, collection);

        private sealed class Mount(FailingPathsProvider provider, ICollectionInfo collection) : IMount
        {
            public IReadOnlyList<AccessPath> AccessPaths
            {
                get
                {
                    if (provider.FailsWith is { } failure)
                    {
                        if (Dispatcher.UIThread.CheckAccess())
                        {
                            Interlocked.Increment(ref provider._failedOnTheWindowsThread);
                        }

                        throw failure;
                    }

                    return [new AccessPath("URL", $"failing://{collection.Name}/")];
                }
            }

            public MountOpenAction? OpenAction => null;

            public Task StartAsync(IVirtualFileSystem fs, IMountContext context, CancellationToken ct) => Task.CompletedTask;

            public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Keeps what is logged for a test to read.</summary>
    private sealed class KeptLog : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Text, Exception? Exception)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
