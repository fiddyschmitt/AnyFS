using AnyFS.Core.Model;
using AnyFS.Core.Runtime;
using AnyFS.Core.Settings;
using AnyFS.Core.Status;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Core.Tests;

[TestClass]
public class SourceRunnerTests
{
    private string _root = "";
    private SettingsPaths _paths = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "anyfs-tests", "runner-" + Guid.NewGuid().ToString("N"));
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

    private SourceRunner Runner(MemoryPluginSettings settings, bool enabled = true, bool countOnStart = false, IPromptHandler? prompts = null, ISourcePlugin? plugin = null)
    {
        var definition = new SourceDefinition { Name = "src", Type = "memory", Enabled = enabled, CountFilesOnStart = countOnStart };
        var handle = new SourceHandle(definition.Id, definition.Name, definition.Type);
        return new SourceRunner(definition, plugin ?? new MemoryPlugin(), settings, null, handle, _paths, null, prompts ?? NoPromptHandler.Instance, NullLogger.Instance)
        {
            RetryDelay = _ => TimeSpan.FromMilliseconds(20),
            CountThrottle = TimeSpan.Zero,
        };
    }

    [TestMethod]
    public async Task StartsAndBecomesReady()
    {
        await using var runner = Runner(new MemoryPluginSettings());
        runner.Start();
        await Wait.UntilAsync(() => runner.Handle.Status.State == ComponentState.Ready, what: "ready");
        Assert.IsNotNull(runner.Handle.Current);
        Assert.IsTrue(runner.Handle.IsUsable);
        Assert.AreEqual("hello", await Handles.ReadTextAsync(runner.Handle.Current!, "hello.txt"));
        var source = (MemorySource)runner.Handle.Current!;
        Assert.AreEqual("src", source.LastContext!.SourceName);
        Assert.IsTrue(source.LastContext.CacheDirectory.StartsWith(_paths.Cache, StringComparison.Ordinal));

        await runner.StopAsync();
        Assert.IsTrue(source.Disposed);
        Assert.IsNull(runner.Handle.Current);
        Assert.AreEqual(ComponentState.Disabled, runner.Handle.Status.State);
    }

    [TestMethod]
    public async Task RetriesWithBackoffUntilItWorks()
    {
        await using var runner = Runner(new MemoryPluginSettings { FailTimes = 2 });
        var seenFailed = false;
        runner.Handle.Changed += h => seenFailed |= h.Status.State == ComponentState.Failed && h.Status.Text.Contains("retrying", StringComparison.Ordinal);
        runner.Start();
        await Wait.UntilAsync(() => runner.Handle.Status.State == ComponentState.Ready, what: "ready after retries");
        Assert.IsTrue(seenFailed, "a Failed status with the retry delay was shown in between");
    }

    [TestMethod]
    public void RetryScheduleIsTenThirtySixtyThreeHundred()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), RetryPolicy.Delay(0));
        Assert.AreEqual(TimeSpan.FromSeconds(30), RetryPolicy.Delay(1));
        Assert.AreEqual(TimeSpan.FromMinutes(1), RetryPolicy.Delay(2));
        Assert.AreEqual(TimeSpan.FromMinutes(5), RetryPolicy.Delay(3));
        Assert.AreEqual(TimeSpan.FromMinutes(5), RetryPolicy.Delay(50));
        Assert.AreEqual("10 s", RetryPolicy.Describe(RetryPolicy.Delay(0)));
        Assert.AreEqual("5 min", RetryPolicy.Describe(RetryPolicy.Delay(3)));
    }

    [TestMethod]
    public async Task PermanentFailuresAreNotRetried()
    {
        await using var runner = Runner(new MemoryPluginSettings { Permanent = true });
        runner.Start();
        await Wait.UntilAsync(() => runner.Handle.Status.State == ComponentState.Failed, what: "failed");
        await Task.Delay(100);
        Assert.AreEqual(ComponentState.Failed, runner.Handle.Status.State);
        StringAssert.Contains(runner.Handle.Status.Text, "settings are wrong");
        Assert.IsFalse(runner.Handle.Status.Text.Contains("retrying", StringComparison.Ordinal));
        Assert.IsNull(runner.Handle.Current);

        await runner.RetryNowAsync();
        await Wait.UntilAsync(() => runner.Handle.Status.State == ComponentState.Failed, what: "failed again on demand");
    }

    [TestMethod]
    public async Task HeadlessWithoutAConsoleFailsPromptingSourcesCleanly()
    {
        await using var runner = Runner(new MemoryPluginSettings { Prompt = true });
        runner.Start();
        await Wait.UntilAsync(() => runner.Handle.Status.State == ComponentState.Failed, what: "failed");
        Assert.AreEqual("Needs input: Smart card PIN", runner.Handle.Status.Text);
    }

    [TestMethod]
    public async Task PromptsReachTheHandlerAndAnswersReachTheSource()
    {
        var prompts = new ConsolePromptHandler(new StringReader("1234\n"), new StringWriter());
        await using var runner = Runner(new MemoryPluginSettings { Prompt = true }, prompts: prompts);
        runner.Start();
        await Wait.UntilAsync(() => runner.Handle.Status.State == ComponentState.Ready, what: "ready");
        Assert.AreEqual("1234", await Handles.ReadTextAsync(runner.Handle.Current!, "pin.txt"));
    }

    [TestMethod]
    public async Task DisabledAndUnknownTypes()
    {
        await using var disabled = Runner(new MemoryPluginSettings(), enabled: false);
        disabled.Start();
        Assert.AreEqual(ComponentState.Disabled, disabled.Handle.Status.State);
        Assert.IsFalse(disabled.IsRunning);

        var definition = new SourceDefinition { Name = "x", Type = "nope" };
        await using var unknown = new SourceRunner(definition, null, null, null, new SourceHandle(definition.Id, "x", "nope"), _paths, null, NoPromptHandler.Instance, NullLogger.Instance);
        unknown.Start();
        Assert.AreEqual(ComponentState.Failed, unknown.Handle.Status.State);
        StringAssert.Contains(unknown.Handle.Status.Text, "Unknown source type 'nope'");

        await using var broken = new SourceRunner(definition, new MemoryPlugin(), null, "Settings could not be read: boom", new SourceHandle(definition.Id, "x", "memory"), _paths, null, NoPromptHandler.Instance, NullLogger.Instance);
        broken.Start();
        Assert.AreEqual(ComponentState.Failed, broken.Handle.Status.State);
        StringAssert.Contains(broken.Handle.Status.Text, "boom");
    }

    [TestMethod]
    public async Task HealthReportsAfterStartChangeTheStatus()
    {
        await using var runner = Runner(new MemoryPluginSettings());
        runner.Start();
        await Wait.UntilAsync(() => runner.Handle.Status.State == ComponentState.Ready, what: "ready");
        var context = ((MemorySource)runner.Handle.Current!).LastContext!;
        context.ReportHealth(ComponentHealth.Degraded, "one root is down");
        Assert.AreEqual(ComponentState.Degraded, runner.Handle.Status.State);
        Assert.AreEqual("one root is down", runner.Handle.Status.Text);
        Assert.IsTrue(runner.Handle.IsUsable);
        context.ReportHealth(ComponentHealth.Ready);
        Assert.AreEqual(ComponentState.Ready, runner.Handle.Status.State);
        context.ReportHealth(ComponentHealth.Failed, "all gone");
        Assert.AreEqual(ComponentState.Failed, runner.Handle.Status.State);
        Assert.IsFalse(runner.Handle.IsUsable);
    }

    [TestMethod]
    public async Task ProgressDuringInitialisationShowsOnTheStartingStatus()
    {
        var gate = new TaskCompletionSource();
        var plugin = new GatedPlugin(gate);
        await using var runner = Runner(new MemoryPluginSettings(), plugin: plugin);
        runner.Start();
        await Wait.UntilAsync(() => runner.Handle.Status.Text == "Logging in", what: "progress text");
        Assert.AreEqual(ComponentState.Starting, runner.Handle.Status.State);
        Assert.AreEqual(0.3, runner.Handle.Status.Progress);
        gate.SetResult();
        await Wait.UntilAsync(() => runner.Handle.Status.State == ComponentState.Degraded, what: "degraded as reported during init");
        Assert.AreEqual("reported during init", runner.Handle.Status.Text);
    }

    [TestMethod]
    public async Task CountOnStartWalksTheTree()
    {
        await using var runner = Runner(new MemoryPluginSettings { Files = "a.txt=1;b/c.txt=2;b/d/e.txt=3" }, countOnStart: true);
        runner.Start();
        await Wait.UntilAsync(() => runner.Handle.Current?.Counters.State == EnumerationState.Complete, what: "count complete");
        Assert.AreEqual((3L, true), runner.Handle.FileCount());
        Assert.AreEqual(2, runner.Handle.Current!.Counters.DirectoriesKnown);
        await Wait.UntilAsync(() => runner.Handle.Status.State == ComponentState.Ready, what: "ready after counting");
    }

    private sealed class GatedPlugin(TaskCompletionSource gate) : ISourcePlugin
    {
        public string TypeId => "gated";

        public string DisplayName => "Gated";

        public string Description => "";

        public string TypeFolder => "gated";

        public Stream? OpenIcon() => null;

        public Type SettingsType => typeof(MemoryPluginSettings);

        public object CreateDefaultSettings() => new MemoryPluginSettings();

        public ISource CreateSource(object settings) => new MemorySource
        {
            OnInitialise = async (ctx, ct) =>
            {
                ctx.ReportProgress("Logging in", 0.3);
                await gate.Task.WaitAsync(ct);
                ctx.ReportHealth(ComponentHealth.Degraded, "reported during init");
            },
        };
    }
}

[TestClass]
public class PromptTests
{
    [TestMethod]
    public async Task PromptsQueueOneAtATime()
    {
        var first = new TaskCompletionSource<PromptResult>();
        var inner = new BlockingPrompts(first.Task);
        var handler = new SerialisingPromptHandler(inner);
        var request = new PromptRequest(PromptKind.Text, "t", "m");
        var a = handler.PromptAsync("a", request, CancellationToken.None);
        await Wait.UntilAsync(() => inner.Started == 1);
        var b = handler.PromptAsync("b", request, CancellationToken.None);
        await Task.Delay(50);
        Assert.AreEqual(1, inner.Started, "the second prompt waits for the first");
        Assert.AreEqual(1, handler.Waiting);
        first.SetResult(PromptResult.FromText("one"));
        Assert.AreEqual("one", (await a).Text);
        await Wait.UntilAsync(() => inner.Started == 2);
        Assert.AreEqual("default", (await b).Text);
    }

    [TestMethod]
    public async Task ConsoleHandlerKinds()
    {
        var output = new StringWriter();
        var handler = new ConsolePromptHandler(new StringReader("yes\n2\nhunter2\n\n"), output);
        Assert.AreEqual(true, (await handler.PromptAsync("s", new PromptRequest(PromptKind.YesNo, "Trust?", "fingerprint"), CancellationToken.None)).Yes);
        Assert.AreEqual(1, (await handler.PromptAsync("s", new PromptRequest(PromptKind.Choice, "Pick", "", ["a", "b"]), CancellationToken.None)).ChoiceIndex);
        Assert.AreEqual("hunter2", (await handler.PromptAsync("s", new PromptRequest(PromptKind.Password, "PIN", ""), CancellationToken.None)).Text);
        Assert.AreEqual("dflt", (await handler.PromptAsync("s", new PromptRequest(PromptKind.Text, "Name", "", DefaultValue: "dflt"), CancellationToken.None)).Text);
        Assert.IsFalse((await handler.PromptAsync("s", new PromptRequest(PromptKind.Text, "EOF", ""), CancellationToken.None)).Answered);
        StringAssert.Contains(output.ToString(), "[s] Trust?");
        StringAssert.Contains(output.ToString(), "fingerprint");
        StringAssert.Contains(output.ToString(), "  2. b");
    }

    [TestMethod]
    public async Task NoPromptHandlerFailsPermanently()
    {
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => NoPromptHandler.Instance.PromptAsync("s", new PromptRequest(PromptKind.Text, "PIN", ""), CancellationToken.None));
        Assert.IsTrue(ex.Permanent);
        Assert.AreEqual("Needs input: PIN", ex.Message);
    }

    private sealed class BlockingPrompts(Task<PromptResult> first) : IPromptHandler
    {
        public int Started;

        public async Task<PromptResult> PromptAsync(string sourceName, PromptRequest request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref Started);
            return n == 1 ? await first : PromptResult.FromText("default");
        }
    }
}

[TestClass]
public class SharedResourcesTests
{
    [TestMethod]
    public async Task SameKeySharesOneInstanceAndDisposesWithTheLastLease()
    {
        var shared = new SharedResources();
        var created = 0;
        Task<Disposable> Factory(CancellationToken ct)
        {
            created++;
            return Task.FromResult(new Disposable());
        }

        var a = await shared.AcquireAsync("k", Factory, CancellationToken.None);
        var b = await shared.AcquireAsync("k", Factory, CancellationToken.None);
        var other = await shared.AcquireAsync("other", Factory, CancellationToken.None);
        Assert.AreSame(a.Value, b.Value);
        Assert.AreNotSame(a.Value, other.Value);
        Assert.AreEqual(2, created);
        Assert.AreEqual(2, shared.Count);

        await a.DisposeAsync();
        await a.DisposeAsync();
        Assert.IsFalse(b.Value.Disposed, "one lease is still out");
        await b.DisposeAsync();
        Assert.IsTrue(b.Value.Disposed);
        Assert.AreEqual(1, shared.Count);

        var c = await shared.AcquireAsync("k", Factory, CancellationToken.None);
        Assert.AreEqual(3, created, "after the last lease went, the key is created afresh");
        await c.DisposeAsync();
        await other.DisposeAsync();
        Assert.AreEqual(0, shared.Count);
    }

    [TestMethod]
    public async Task ConcurrentAcquirersShareTheCreation()
    {
        var shared = new SharedResources();
        var gate = new TaskCompletionSource();
        var created = 0;
        async Task<Disposable> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref created);
            await gate.Task;
            return new Disposable();
        }

        var tasks = Enumerable.Range(0, 5).Select(_ => shared.AcquireAsync("k", Factory, CancellationToken.None)).ToList();
        await Task.Delay(30);
        Assert.AreEqual(1, created);
        gate.SetResult();
        var leases = await Task.WhenAll(tasks);
        Assert.IsTrue(leases.All(l => ReferenceEquals(l.Value, leases[0].Value)));
        foreach (var lease in leases)
        {
            await lease.DisposeAsync();
        }

        Assert.IsTrue(leases[0].Value.Disposed);
    }

    [TestMethod]
    public async Task AFailedFactoryIsRetriedNextTime()
    {
        var shared = new SharedResources();
        var attempts = 0;
        Task<Disposable> Factory(CancellationToken ct)
        {
            attempts++;
            return attempts == 1 ? Task.FromException<Disposable>(new InvalidOperationException("port busy")) : Task.FromResult(new Disposable());
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => shared.AcquireAsync("k", Factory, CancellationToken.None));
        Assert.AreEqual(0, shared.Count);
        var lease = await shared.AcquireAsync("k", Factory, CancellationToken.None);
        Assert.AreEqual(2, attempts);
        await lease.DisposeAsync();
    }

    private sealed class Disposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
