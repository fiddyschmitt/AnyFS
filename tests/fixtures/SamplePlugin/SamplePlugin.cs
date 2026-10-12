using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.SamplePlugin;

public sealed class SampleSettings
{
    [Setting("Greeting", Hint = "Shown in hello.txt", Required = true)]
    public string Greeting { get; set; } = "Hello from the sample plugin";

    [Setting("Files", Hint = "How many numbered files to show")]
    public int Files { get; set; } = 3;

    [Setting("API token", Secret = true)]
    public string? Token { get; set; }
}

/// <summary>
/// The 60-line plugin from docs/plugin-authoring.md: a read-only source with a synthetic tree. It is also
/// what CI loads into the trimmed single-file build to prove third-party DLLs still work (spec 14, 14.1),
/// touching System.Text.Json and Regex from the guaranteed set on the way.
/// </summary>
public sealed class SamplePlugin : ISourcePlugin
{
    public string TypeId => "sample";

    public string DisplayName => "Sample";

    public string Description => "A tiny read-only source: hello.txt and a few numbered files.";

    public string TypeFolder => "sample";

    public Stream? OpenIcon() => null;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(SampleSettings);

    public object CreateDefaultSettings() => new SampleSettings();

    public ISource CreateSource(object settings) => new SampleSource((SampleSettings)settings);
}

public sealed class SampleSource(SampleSettings settings) : SourceBase
{
    private static readonly Regex Numbered = new(@"^file(\d+)\.txt$", RegexOptions.Compiled);
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    protected override Task OnInitialiseAsync(CancellationToken ct)
    {
        Context.ReportProgress("Building the tree", 0.5);
        _files["hello.txt"] = Encoding.UTF8.GetBytes(settings.Greeting + "\n");
        _files["settings.json"] = JsonSerializer.SerializeToUtf8Bytes(new { settings.Greeting, settings.Files, HasToken = settings.Token is not null });
        for (var i = 1; i <= settings.Files; i++)
        {
            _files[$"file{i}.txt"] = Encoding.UTF8.GetBytes($"file {i}\n");
        }

        Counters.Set(_files.Count, 0, EnumerationState.Complete);
        Logger.LogInformation("sample source ready with {Count} files ({Numbered} numbered)", _files.Count, _files.Keys.Count(k => Numbered.IsMatch(k)));
        return Task.CompletedTask;
    }

    public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (path.Length != 0)
        {
            throw _files.ContainsKey(path) ? VfsException.NotADirectory(path) : VfsException.NotFound(path);
        }

        foreach (var (name, bytes) in _files.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            yield return VfsEntry.File(name, bytes.Length, new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero));
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

        if (path.Length == 0)
        {
            throw VfsException.IsADirectory(path);
        }

        return _files.TryGetValue(path, out var bytes)
            ? Task.FromResult<IVfsFile>(new BytesVfsFile(bytes))
            : throw VfsException.NotFound(path);
    }
}
