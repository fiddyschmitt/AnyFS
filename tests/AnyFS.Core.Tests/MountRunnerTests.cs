using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using AnyFS.Core.Model;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Core.Stats;
using AnyFS.Core.Status;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Core.Tests;

/// <summary>
/// One mount of one collection (the review of 21-Nov-2026, R7e). A start that failed is tried again, as a source's is: a
/// mount whose port was still held as AnyFS started again stayed down until someone looked. And a stop is waited for no
/// longer than its time: no provider reads the token it is given, so a stop took as long as it took, a window closing
/// gave it the time its sources needed to keep what they keep.
/// </summary>
[TestClass]
public class MountRunnerTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);
    private string _root = "";
    private SettingsPaths _paths = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "mountrunner-" + Guid.NewGuid().ToString("N"));
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

    private sealed record Collection(string Name) : ICollectionInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    /// <summary>Mounts whose start can fail, as on a port another program still holds, and whose stop can be held, whatever token it is given.</summary>
    private sealed class TestProvider : IMountProviderPlugin
    {
        private int _portHeld;

        /// <summary>Starts yet to fail, starts asked for, and stops that ended.</summary>
        public int FailStarts;
        public int Starts;
        public int StopsEnded;

        /// <summary>A stop waits for this.</summary>
        public TaskCompletionSource? HoldStop { get; set; }

        /// <summary>The mounts share one port: a start fails while another mount holds it, and a stop lets it go as it ends.</summary>
        public bool OnePort { get; init; }

        public string TypeId => "test-mount";

        public string DisplayName => "Test mount";

        public string Description => "";

        public Stream? OpenIcon() => null;

        public IReadOnlyList<OSPlatform> SupportedPlatforms { get; } = [OSPlatform.Windows, OSPlatform.Linux, OSPlatform.OSX];

        public string? UnavailableReason => null;

        public PerformanceTier Tier => PerformanceTier.Protocol;

        public bool SupportsWrites => true;

        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
        public Type SettingsType => typeof(FakeMountSettings);

        public object CreateDefaultSettings(ICollectionInfo collection) => new FakeMountSettings();

        public IMount CreateMount(object settings, ICollectionInfo collection) => new TestMount(this);

        private sealed class TestMount(TestProvider provider) : IMount
        {
            private bool _holdsThePort;

            public IReadOnlyList<AccessPath> AccessPaths { get; private set; } = [];

            public MountOpenAction? OpenAction => null;

            public Task StartAsync(IVirtualFileSystem fs, IMountContext context, CancellationToken ct)
            {
                Interlocked.Increment(ref provider.Starts);
                if (Interlocked.Decrement(ref provider.FailStarts) >= 0)
                {
                    throw new IOException("the port is taken");
                }

                if (provider.OnePort)
                {
                    if (Interlocked.CompareExchange(ref provider._portHeld, 1, 0) != 0)
                    {
                        throw new IOException("the port is taken");
                    }

                    _holdsThePort = true;
                }

                AccessPaths = [new AccessPath("URL", "test://mount/")];
                return Task.CompletedTask;
            }

            public async Task StopAsync(CancellationToken ct)
            {
                if (provider.HoldStop is { } hold)
                {
                    await hold.Task.ConfigureAwait(false);
                }

                if (_holdsThePort)
                {
                    _holdsThePort = false;
                    Volatile.Write(ref provider._portHeld, 0);
                }

                Interlocked.Increment(ref provider.StopsEnded);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private MountRunner Runner(TestProvider provider)
    {
        var definition = new MountDefinition { Name = "test", Provider = provider.TypeId };
        var runner = new MountRunner(definition, provider, new FakeMountSettings(), null, new Collection("work"), _paths, new SharedResources(), new StatsCollector(), NullLogger.Instance);
        runner.RetryDelay = _ => TimeSpan.FromMilliseconds(20);
        return runner;
    }

    private static IVirtualFileSystem Fs() => Handles.Vfs("work").ForMount(false);

    [TestMethod]
    public async Task AMountThatFailedToStartIsTriedAgainUntilItIsUp()
    {
        var provider = new TestProvider { FailStarts = 2 };
        var runner = Runner(provider);
        await runner.StartAsync(Fs(), CancellationToken.None);
        if (runner.Status.State != ComponentState.Ready)
        {
            Assert.AreEqual(ComponentState.Failed, runner.Status.State);
            StringAssert.Contains(runner.Status.Text, "the port is taken");
            StringAssert.Contains(runner.Status.Text, "retrying in");
        }

        await Wait.UntilAsync(() => runner.Status.State == ComponentState.Ready, what: "the mount up");
        Assert.AreEqual(3, provider.Starts);
        Assert.AreEqual("test://mount/", runner.AccessPaths.Single().Value);
        await runner.StopAsync();
        Assert.AreEqual(1, provider.StopsEnded, "only the one that came up had anything to stop");
    }

    [TestMethod]
    public async Task AMountStoppedWhileItWaitsToBeTriedAgainIsTriedNoMore()
    {
        var provider = new TestProvider { FailStarts = int.MaxValue };
        var runner = Runner(provider);
        await runner.StartAsync(Fs(), CancellationToken.None);
        await Wait.UntilAsync(() => provider.Starts >= 3, what: "a few tries");

        await runner.StopAsync().WaitAsync(Soon);
        var tried = provider.Starts;
        await Task.Delay(200);
        Assert.AreEqual(tried, provider.Starts, "nothing is tried after the stop");
        Assert.AreEqual(ComponentState.Disabled, runner.Status.State, "and it no longer says it is about to");

        // and started again, it starts afresh
        Volatile.Write(ref provider.FailStarts, 0);
        await runner.StartAsync(Fs(), CancellationToken.None);
        Assert.AreEqual(ComponentState.Ready, runner.Status.State);
        await runner.StopAsync();
    }

    [TestMethod]
    public async Task AStopIsNotWaitedForPastItsTime()
    {
        var provider = new TestProvider { HoldStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var runner = Runner(provider);
        runner.StopTimeout = TimeSpan.FromMilliseconds(100);
        runner.StopGrace = TimeSpan.FromMilliseconds(100);
        await runner.StartAsync(Fs(), CancellationToken.None);
        Assert.AreEqual(ComponentState.Ready, runner.Status.State);

        await runner.StopAsync().WaitAsync(Soon);
        Assert.AreEqual(0, provider.StopsEnded, "the provider's stop goes on");
        Assert.AreEqual(ComponentState.Disabled, runner.Status.State);
        Assert.AreEqual(0, runner.AccessPaths.Count);

        provider.HoldStop.SetResult();
        await Wait.UntilAsync(() => provider.StopsEnded == 1, what: "the provider's stop ending in its own time");
    }

    [TestMethod]
    public async Task AMountStartedWhileItsOldSelfStillHoldsThePortComesUpWhenThatHasLetGo()
    {
        // the two together: an edited mount whose old self takes long to let its port go
        var provider = new TestProvider { OnePort = true, HoldStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var old = Runner(provider);
        old.StopTimeout = TimeSpan.FromMilliseconds(100);
        old.StopGrace = TimeSpan.FromMilliseconds(100);
        await old.StartAsync(Fs(), CancellationToken.None);
        Assert.AreEqual(ComponentState.Ready, old.Status.State);
        await old.StopAsync().WaitAsync(Soon);

        var replacement = Runner(provider);
        await replacement.StartAsync(Fs(), CancellationToken.None);
        Assert.AreEqual(ComponentState.Failed, replacement.Status.State, "the port is still held");
        await Wait.UntilAsync(() => provider.Starts >= 4, what: "a few tries");

        provider.HoldStop.SetResult();
        await Wait.UntilAsync(() => replacement.Status.State == ComponentState.Ready, what: "the mount up once the port is free");
        provider.HoldStop = null;
        await replacement.StopAsync();
    }
}
