using System.Diagnostics.CodeAnalysis;
using AnyFS.Sdk;

namespace AnyFS.Sources.Mirror;

public sealed class MirrorSettings
{
    [Setting("Paths", Kind = SettingKind.Multiline, Required = true, Order = 0,
        Hint = "One per line. Windows: \\\\server\\share, C:\\, D:\\Photos. Linux and macOS: any folder, e.g. /mnt/nas (mount shares with the OS first).",
        Placeholder = "\\\\aus-files-01\\shared")]
    public string Paths { get; set; } = "";

    [Setting("Check paths every (seconds)", Advanced = true, Order = 10, Hint = "How often unreachable paths are retried and reachable ones re-checked.")]
    public int CheckIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Mirror (type <c>mirror</c>, folder <c>files</c>, spec 7.1): a list of UNC paths, drive roots and folders,
/// each becoming a folder chain inside the source: \\h\s\x is h\s\x, C:\a is c\a, /mnt/nas is mnt\nas.
/// </summary>
public sealed class MirrorPlugin : ISourcePlugin, IValidatable
{
    public string TypeId => "mirror";

    public string DisplayName => "Mirror";

    public string Description => "Mirrors local folders, drives and (on Windows) UNC shares, each under its own path.";

    public string TypeFolder => "files";

    public Stream? OpenIcon() => null;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(MirrorSettings);

    public object CreateDefaultSettings() => new MirrorSettings();

    public ISource CreateSource(object settings) => new MirrorSource((MirrorSettings)settings);

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (MirrorSettings)settings;
        var roots = new List<MirrorRoot>();
        var any = false;
        foreach (var line in MirrorRoot.Lines(s.Paths))
        {
            any = true;
            if (!MirrorRoot.TryParse(line, OperatingSystem.IsWindows(), out var root, out var error))
            {
                yield return new ValidationMessage($"{line}: {error}", nameof(MirrorSettings.Paths));
                continue;
            }

            var clash = roots.FirstOrDefault(r => r.ChainPath.Equals(root.ChainPath, StringComparison.OrdinalIgnoreCase));
            if (clash is not null)
            {
                yield return new ValidationMessage($"{line} and {clash.Original} would both appear at '{root.ChainPath}'", nameof(MirrorSettings.Paths));
                continue;
            }

            roots.Add(root);
        }

        if (!any)
        {
            yield return new ValidationMessage("Add at least one path.", nameof(MirrorSettings.Paths));
        }

        if (s.CheckIntervalSeconds < 5)
        {
            yield return new ValidationMessage("The check interval must be at least 5 seconds.", nameof(MirrorSettings.CheckIntervalSeconds));
        }
    }
}

/// <summary>One configured path and the folder chain it appears under.</summary>
public sealed record MirrorRoot(string Original, string FullPath, string[] Chain)
{
    public string ChainPath => string.Join('/', Chain);

    public static IEnumerable<string> Lines(string paths)
        => paths.Split('\n').Select(l => l.Trim().TrimEnd('\r')).Where(l => l.Length > 0 && !l.StartsWith('#'));

    /// <summary>Parses one line. <paramref name="windows"/> says which forms this OS can open.</summary>
    public static bool TryParse(string line, bool windows, out MirrorRoot root, out string error)
    {
        root = null!;
        error = "";
        var text = line.Trim();
        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            text = @"\\" + text[8..];
        }
        else if (text.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal))
        {
            var parts = text[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                error = "a UNC path needs a server and a share (\\\\server\\share)";
                return false;
            }

            if (!windows)
            {
                error = "UNC paths need Windows; mount the share with the OS and use its mount point";
                return false;
            }

            root = new MirrorRoot(line, @"\\" + string.Join('\\', parts), parts);
            return true;
        }

        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':' && (text.Length == 2 || text[2] is '\\' or '/'))
        {
            if (!windows)
            {
                error = "drive letters are a Windows thing; use a folder path like /mnt/data";
                return false;
            }

            var letter = char.ToLowerInvariant(text[0]).ToString();
            var rest = text.Length > 3 ? text[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries) : [];
            var chain = new[] { letter }.Concat(rest).ToArray();
            var full = rest.Length == 0 ? letter.ToUpperInvariant() + @":\" : letter.ToUpperInvariant() + @":\" + string.Join('\\', rest);
            root = new MirrorRoot(line, full, chain);
            return true;
        }

        if (text.StartsWith('/'))
        {
            var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(p => p is "." or ".."))
            {
                error = "'.' and '..' are not allowed";
                return false;
            }

            root = new MirrorRoot(line, windows ? text : "/" + string.Join('/', parts), parts);
            return true;
        }

        error = windows ? "use an absolute path: \\\\server\\share, C:\\ or D:\\Photos" : "use an absolute path starting with /";
        return false;
    }
}
