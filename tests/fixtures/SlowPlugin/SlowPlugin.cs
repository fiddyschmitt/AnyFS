using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.SlowPlugin;

public sealed class SlowSettings
{
    [Setting("Delay (seconds)", Hint = "How long every read of a slow file waits on the source before it is answered")]
    public int DelaySeconds { get; set; } = 10;

    [Setting("A slow file's size (KiB)", Hint = "Small enough that a client reads it in one request (Windows' Get-FileHash reads 4 KiB at a time)")]
    public int SlowKiB { get; set; } = 4;
}

/// <summary>
/// The lab's slow source: a source call as slow as a cold fetch from a remote store, on demand. Its slow files, slow.bin
/// and the many in slow/ (each read once, so no client answers one from its cache), answer every read after
/// <see cref="SlowSettings.DelaySeconds"/>; quick.bin and quick/ answer at once. The E2E tests read a slow file and
/// meanwhile list and read the quick ones through the same mount: those must come back promptly. Read-only; every byte is
/// <see cref="ByteAt"/> of its offset, for the tests to check.
/// </summary>
public sealed class SlowPlugin : ISourcePlugin
{
    public string TypeId => "lab-slow";

    public string DisplayName => "Lab slow source";

    public string Description => "Files whose every read waits on the source, beside files that answer at once (for the E2E tests).";

    public string TypeFolder => "lab-slow";

    public Stream? OpenIcon() => null;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(SlowSettings);

    public object CreateDefaultSettings() => new SlowSettings();

    public ISource CreateSource(object settings) => new SlowSource((SlowSettings)settings);

    /// <summary>The byte at <paramref name="offset"/> of every file here.</summary>
    public static byte ByteAt(long offset) => (byte)((offset * 31) ^ (offset >> 9));

    /// <summary>A file's whole content: its length's bytes of <see cref="ByteAt"/>.</summary>
    public static byte[] Content(int length) => [.. Enumerable.Range(0, length).Select(i => ByteAt(i))];
}

public sealed class SlowSource(SlowSettings settings) : SourceBase
{
    public const int QuickFiles = 5;

    /// <summary>How many slow files slow/ holds: s00000.bin and on, more than any run reads.</summary>
    public const int SlowFiles = 2000;

    /// <summary>quick.bin's size: one read for any client (macOS's holds back its read-ahead while another read is unanswered).</summary>
    public const int QuickBytes = 16 << 10;
    private static readonly DateTimeOffset Time = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    private byte[] _slow = [];
    private readonly byte[] _quick = SlowPlugin.Content(QuickBytes);

    protected override Task OnInitialiseAsync(CancellationToken ct)
    {
        _slow = SlowPlugin.Content(Math.Max(1, settings.SlowKiB) << 10);
        Counters.Set(2 + QuickFiles + SlowFiles, 2, EnumerationState.Complete);
        Logger.LogInformation("lab-slow source: slow files of {KiB} KiB answer each read after {Delay} s", settings.SlowKiB, settings.DelaySeconds);
        return Task.CompletedTask;
    }

    public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (path.Length == 0)
        {
            yield return VfsEntry.File("slow.bin", _slow.Length, Time);
            yield return VfsEntry.File("quick.bin", _quick.Length, Time);
            yield return VfsEntry.Directory("quick", Time);
            yield return VfsEntry.Directory("slow", Time);
        }
        else if (path == "quick")
        {
            for (var i = 1; i <= QuickFiles; i++)
            {
                yield return VfsEntry.File($"q{i:D2}.txt", 4, Time);
            }
        }
        else if (path == "slow")
        {
            for (var i = 0; i < SlowFiles; i++)
            {
                yield return VfsEntry.File($"s{i:D5}.bin", _slow.Length, Time);
            }
        }
        else
        {
            throw IsFile(path) ? VfsException.NotADirectory(path) : VfsException.NotFound(path);
        }

        await Task.CompletedTask;
    }

    public override Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (mode.IsWrite())
        {
            throw VfsException.NotSupported("writing");
        }

        return path switch
        {
            "quick.bin" => Task.FromResult<IVfsFile>(new BytesVfsFile(_quick)),
            _ when IsNumbered(path, "quick/q", ".txt", QuickFiles, 1) => Task.FromResult<IVfsFile>(new BytesVfsFile(SlowPlugin.Content(4))),
            _ when IsFile(path) => Task.FromResult<IVfsFile>(new SlowFile(_slow, TimeSpan.FromSeconds(settings.DelaySeconds))),
            "" or "quick" or "slow" => throw VfsException.IsADirectory(path),
            _ => throw VfsException.NotFound(path),
        };
    }

    private static bool IsFile(string path)
        => path is "slow.bin" or "quick.bin" || IsNumbered(path, "quick/q", ".txt", QuickFiles, 1) || IsNumbered(path, "slow/s", ".bin", SlowFiles - 1, 0);

    private static bool IsNumbered(string path, string prefix, string suffix, int last, int first)
        => path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal)
            && int.TryParse(path[prefix.Length..^suffix.Length], out var n) && n >= first && n <= last;

    /// <summary>A slow file: every read with data to give waits first, as a cold fetch does (a read at the end answers at once); cancelling it cancels the wait.</summary>
    private sealed class SlowFile(byte[] bytes, TimeSpan delay) : IVfsFile
    {
        private readonly BytesVfsFile _inner = new(bytes);

        public long Length => bytes.Length;

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            if (offset < bytes.Length && buffer.Length > 0)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            return await _inner.ReadAsync(offset, buffer, ct).ConfigureAwait(false);
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => throw VfsException.NotSupported("writing");

        public ValueTask SetLengthAsync(long length, CancellationToken ct) => throw VfsException.NotSupported("writing");

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
