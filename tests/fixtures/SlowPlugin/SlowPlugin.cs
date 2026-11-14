using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.SlowPlugin;

public sealed class SlowSettings
{
    [Setting("Delay (seconds)", Hint = "How long a slow call waits on the source before it is answered: every read of a slow file, every listing of a slow folder, every lookup of a slow-to-find file, every open of a slow-to-open file")]
    public int DelaySeconds { get; set; } = 10;

    [Setting("A slow file's size (KiB)", Hint = "Small enough that a client reads it in one request (Windows' Get-FileHash reads 4 KiB at a time)")]
    public int SlowKiB { get; set; } = 4;

    [Setting("Long delay (seconds)", Hint = "How long every read of a file of long/ waits: longer than a client waits for a server that says nothing")]
    public int LongDelaySeconds { get; set; } = 90;
}

/// <summary>
/// The lab's slow source: a source call as slow as a cold fetch from a remote store, on demand. Each kind of call has
/// files or folders of its own that are slow in that one way, many of them, so a test takes one no client has in its
/// cache; everything else about them answers at once:
/// <list type="bullet">
/// <item>slow.bin and slow/: every read waits <see cref="SlowSettings.DelaySeconds"/>;</item>
/// <item>long/: every read waits <see cref="SlowSettings.LongDelaySeconds"/>, longer than a client waits for a server
/// that says nothing;</item>
/// <item>slowdirs/: every listing of one of its folders waits (five small files each);</item>
/// <item>slowstat/: every lookup of one of its files waits (an open asks more than once);</item>
/// <item>slowopen/: every open of one of its files waits;</item>
/// <item>quick.bin and quick/ answer at once.</item>
/// </list>
/// The E2E tests make one slow call and meanwhile list and read the quick ones through the same mount: those must come
/// back promptly. Read-only; every byte is <see cref="SlowPlugin.ByteAt"/> of its offset, for the tests to check.
/// </summary>
public sealed class SlowPlugin : ISourcePlugin
{
    public string TypeId => "lab-slow";

    public string DisplayName => "Lab slow source";

    public string Description => "Files and folders whose reads, listings, lookups or opens wait on the source, beside files that answer at once (for the E2E tests).";

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

    /// <summary>How many files long/ holds (g00000.bin and on), and slowstat/ (t...) and slowopen/ (o...); and how many folders slowdirs/ (d00000 and on).</summary>
    public const int LongFiles = 200;

    public const int SlowToFindFiles = 500;

    public const int SlowToOpenFiles = 500;

    public const int SlowFolders = 200;

    /// <summary>quick.bin's size: one read for any client (macOS's holds back its read-ahead while another read is unanswered).</summary>
    public const int QuickBytes = 16 << 10;

    /// <summary>The small files' size: quick/'s and a slow folder's.</summary>
    private const int SmallBytes = 4;
    private static readonly DateTimeOffset Time = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Folders = ["quick", "slow", "long", "slowdirs", "slowstat", "slowopen"];
    private byte[] _slow = [];
    private readonly byte[] _quick = SlowPlugin.Content(QuickBytes);

    /// <summary>What a path is here; the slow ones are slow in one way each.</summary>
    private enum Kind
    {
        None,
        Root,
        Folder,
        SlowFolder,
        QuickFile,
        SmallFile,
        SlowFile,
        LongFile,
        SlowToFindFile,
        SlowToOpenFile,
    }

    private TimeSpan Delay => TimeSpan.FromSeconds(settings.DelaySeconds);

    protected override Task OnInitialiseAsync(CancellationToken ct)
    {
        _slow = SlowPlugin.Content(Math.Max(1, settings.SlowKiB) << 10);
        Counters.Set(2 + QuickFiles + SlowFiles + LongFiles + SlowToFindFiles + SlowToOpenFiles + (SlowFolders * QuickFiles), Folders.Length + SlowFolders, EnumerationState.Complete);
        Logger.LogInformation("lab-slow source: slow files of {KiB} KiB; a slow call answers after {Delay} s, a read in long/ after {Long} s", settings.SlowKiB, settings.DelaySeconds, settings.LongDelaySeconds);
        return Task.CompletedTask;
    }

    /// <summary>Answered from the path alone, not from a listing of the folder above, so only what is meant to be slow is.</summary>
    public override async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var kind = KindOf(path);
        if (kind == Kind.SlowToFindFile)
        {
            await Task.Delay(Delay, ct).ConfigureAwait(false);
        }

        return EntryOf(kind, VfsPath.SplitLast(path).Name);
    }

    public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        switch (KindOf(path))
        {
            case Kind.Root:
                yield return VfsEntry.File("slow.bin", _slow.Length, Time);
                yield return VfsEntry.File("quick.bin", _quick.Length, Time);
                foreach (var folder in Folders)
                {
                    yield return VfsEntry.Directory(folder, Time);
                }

                break;
            case Kind.SlowFolder:
                await Task.Delay(Delay, ct).ConfigureAwait(false);
                for (var i = 1; i <= QuickFiles; i++)
                {
                    yield return VfsEntry.File($"l{i:D2}.txt", SmallBytes, Time);
                }

                break;
            case Kind.Folder:
                var (count, first, name, isFolder, length) = path switch
                {
                    "quick" => (QuickFiles, 1, (Func<int, string>)(i => $"q{i:D2}.txt"), false, SmallBytes),
                    "slow" => (SlowFiles, 0, i => $"s{i:D5}.bin", false, _slow.Length),
                    "long" => (LongFiles, 0, i => $"g{i:D5}.bin", false, _slow.Length),
                    "slowstat" => (SlowToFindFiles, 0, i => $"t{i:D5}.bin", false, _slow.Length),
                    "slowopen" => (SlowToOpenFiles, 0, i => $"o{i:D5}.bin", false, _slow.Length),
                    _ => (SlowFolders, 0, i => $"d{i:D5}", true, 0),
                };
                for (var i = first; i < first + count; i++)
                {
                    yield return isFolder ? VfsEntry.Directory(name(i), Time) : VfsEntry.File(name(i), length, Time);
                }

                break;
            case Kind.None:
                throw VfsException.NotFound(path);
            default:
                throw VfsException.NotADirectory(path);
        }
    }

    public override async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (mode.IsWrite())
        {
            throw VfsException.NotSupported("writing");
        }

        switch (KindOf(path))
        {
            case Kind.QuickFile:
                return new BytesVfsFile(_quick);
            case Kind.SmallFile:
                return new BytesVfsFile(SlowPlugin.Content(SmallBytes));
            case Kind.SlowFile:
                return new SlowFile(_slow, Delay);
            case Kind.LongFile:
                return new SlowFile(_slow, TimeSpan.FromSeconds(settings.LongDelaySeconds));
            case Kind.SlowToFindFile:
                return new BytesVfsFile(_slow);
            case Kind.SlowToOpenFile:
                await Task.Delay(Delay, ct).ConfigureAwait(false);
                return new BytesVfsFile(_slow);
            case Kind.None:
                throw VfsException.NotFound(path);
            default:
                throw VfsException.IsADirectory(path);
        }
    }

    private VfsEntry? EntryOf(Kind kind, string name) => kind switch
    {
        Kind.None => null,
        Kind.Root => VfsEntry.Directory(""),
        Kind.Folder or Kind.SlowFolder => VfsEntry.Directory(name, Time),
        Kind.QuickFile => VfsEntry.File(name, _quick.Length, Time),
        Kind.SmallFile => VfsEntry.File(name, SmallBytes, Time),
        _ => VfsEntry.File(name, _slow.Length, Time),
    };

    private static Kind KindOf(string path)
    {
        if (path.Length == 0)
        {
            return Kind.Root;
        }

        if (Array.IndexOf(Folders, path) >= 0)
        {
            return Kind.Folder;
        }

        return path switch
        {
            "slow.bin" => Kind.SlowFile,
            "quick.bin" => Kind.QuickFile,
            _ when IsNumbered(path, "quick/q", ".txt", QuickFiles, 1, 2) => Kind.SmallFile,
            _ when IsNumbered(path, "slow/s", ".bin", SlowFiles - 1, 0) => Kind.SlowFile,
            _ when IsNumbered(path, "long/g", ".bin", LongFiles - 1, 0) => Kind.LongFile,
            _ when IsNumbered(path, "slowstat/t", ".bin", SlowToFindFiles - 1, 0) => Kind.SlowToFindFile,
            _ when IsNumbered(path, "slowopen/o", ".bin", SlowToOpenFiles - 1, 0) => Kind.SlowToOpenFile,
            _ when IsNumbered(path, "slowdirs/d", "", SlowFolders - 1, 0) => Kind.SlowFolder,
            _ when path.StartsWith("slowdirs/d", StringComparison.Ordinal) && path.IndexOf('/', "slowdirs/".Length) is > 0 and var slash
                && IsNumbered(path[..slash], "slowdirs/d", "", SlowFolders - 1, 0) && IsNumbered(path[(slash + 1)..], "l", ".txt", QuickFiles, 1, 2) => Kind.SmallFile,
            _ => Kind.None,
        };
    }

    /// <summary>Whether a path is a prefix, a number of <paramref name="width"/> digits in a range, and a suffix: the names the listings give.</summary>
    private static bool IsNumbered(string path, string prefix, string suffix, int last, int first, int width = 5)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal) || path.Length != prefix.Length + width + suffix.Length)
        {
            return false;
        }

        var digits = path.AsSpan(prefix.Length, path.Length - prefix.Length - suffix.Length);
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(digits, out var n) && n >= first && n <= last;
    }

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
