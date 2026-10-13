using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using AnyFS.Sdk;

namespace AnyFS.Sdk.Testing;

/// <summary>Settings for <see cref="MemoryPlugin"/>: "path=text;path2=text2" plus failure knobs for runner tests.</summary>
public sealed class MemoryPluginSettings
{
    [Setting("Files", Kind = SettingKind.Multiline)]
    public string Files { get; set; } = "hello.txt=hello";

    [Setting("Fail times")]
    public int FailTimes { get; set; }

    [Setting("Permanent failure")]
    public bool Permanent { get; set; }

    [Setting("Needs a prompt")]
    public bool Prompt { get; set; }

    [Setting("Can write")]
    public bool CanWrite { get; set; } = true;

    [Setting("Secret", Secret = true)]
    public string? Secret { get; set; }
}

/// <summary>A source plugin over <see cref="MemorySource"/>, so engine tests need no real upstream.</summary>
public sealed class MemoryPlugin : ISourcePlugin
{
    public static int Failures;

    public string TypeId => "memory";

    public string DisplayName => "Memory";

    public string Description => "in-memory test source";

    public string TypeFolder => "mem";

    public Stream? OpenIcon() => null;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(MemoryPluginSettings);

    public object CreateDefaultSettings() => new MemoryPluginSettings();

    public ISource CreateSource(object settings)
    {
        var s = (MemoryPluginSettings)settings;
        var source = new MemorySource(s.CanWrite);
        foreach (var pair in s.Files.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            source.AddFile(pair[..eq].Trim(), pair[(eq + 1)..]);
        }

        source.OnInitialise = async (ctx, ct) =>
        {
            if (s.Prompt)
            {
                var answer = await ctx.PromptAsync(new PromptRequest(PromptKind.Password, "Smart card PIN", "Enter the PIN"), ct);
                if (!answer.Answered)
                {
                    throw new SourceFailedException("no PIN given", permanent: true);
                }

                source.AddFile("pin.txt", answer.Text ?? "");
            }

            if (s.Permanent)
            {
                throw new SourceFailedException("the settings are wrong", permanent: true);
            }

            if (s.FailTimes > 0)
            {
                // the same settings object comes back on every attempt, so this counts down across retries
                s.FailTimes--;
                Interlocked.Increment(ref Failures);
                throw new IOException("upstream down");
            }

            if (s.Secret is not null)
            {
                source.AddFile("secret.txt", s.Secret);
            }
        };
        return source;
    }
}

public sealed class FakeMountSettings
{
    [Setting("Fail to start")]
    public bool FailStart { get; set; }

    [Setting("Port")]
    public int Port { get; set; } = 1;
}

/// <summary>A mount provider that records what it was given and shares a "listener" per port.</summary>
public sealed class FakeMountProvider : IMountProviderPlugin
{
    public static readonly List<FakeMount> Mounts = [];

    public string TypeId => "fake";

    public string DisplayName => "Fake";

    public string Description => "records calls";

    public Stream? OpenIcon() => null;

    public IReadOnlyList<OSPlatform> SupportedPlatforms { get; } = [OSPlatform.Windows, OSPlatform.Linux, OSPlatform.OSX];

    public string? UnavailableReason { get; set; }

    public PerformanceTier Tier => PerformanceTier.Protocol;

    public bool SupportsWrites => true;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(FakeMountSettings);

    public object CreateDefaultSettings(ICollectionInfo collection) => new FakeMountSettings();

    public IMount CreateMount(object settings, ICollectionInfo collection)
    {
        var mount = new FakeMount((FakeMountSettings)settings, collection);
        lock (Mounts)
        {
            Mounts.Add(mount);
        }

        return mount;
    }
}

public sealed class FakeMount(FakeMountSettings settings, ICollectionInfo collection) : IMount
{
    public IVirtualFileSystem? Fs { get; private set; }

    public IMountContext? Context { get; private set; }

    public ISharedLease<FakeListener>? Lease { get; private set; }

    public ICollectionInfo Collection => collection;

    public bool Stopped { get; private set; }

    public bool Disposed { get; private set; }

    public IReadOnlyList<AccessPath> AccessPaths { get; private set; } = [];

    public MountOpenAction? OpenAction => AccessPaths.Count > 0 ? new MountOpenAction.Browser(AccessPaths[0].Value) : null;

    public async Task StartAsync(IVirtualFileSystem fs, IMountContext context, CancellationToken ct)
    {
        if (settings.FailStart)
        {
            throw new InvalidOperationException("the fake mount was told to fail");
        }

        Fs = fs;
        Context = context;
        Lease = await context.AcquireSharedAsync("fake:" + settings.Port, _ => Task.FromResult(new FakeListener(settings.Port).Started()), ct);
        AccessPaths = [new AccessPath("URL", $"fake://{settings.Port}/{collection.Name}/")];
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Stopped = true;
        if (Lease is not null)
        {
            await Lease.DisposeAsync();
            Lease = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class FakeListener(int port) : IAsyncDisposable
{
    public static int Alive;

    private int _disposed;

    public int Port => port;

    public FakeListener Started()
    {
        Interlocked.Increment(ref Alive);
        return this;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Interlocked.Decrement(ref Alive);
        }

        return ValueTask.CompletedTask;
    }
}

