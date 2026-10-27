using System.Globalization;
using System.Text;

namespace AnyFS.Sources.GitLab;

/// <summary>
/// The names GitLab's things show as (spec 7.8). A repository's files keep their own names wherever Windows takes them;
/// a character Windows refuses in a name becomes an underscore, a name it refuses whole (a trailing dot or space, a device
/// name) gets one more, and two names that differ only in case (git keeps <c>README</c> and <c>readme</c> apart, AnyFS's
/// paths do not) are told apart with a number. Issues and merge requests are named by their number and title, cut to a
/// length that leaves room for the path above them.
/// </summary>
internal static class GitLabNames
{
    /// <summary>The longest title kept in an issue's or merge request's folder name.</summary>
    public const int MaxTitle = 80;

    /// <summary>The longest name a file or folder gets (Windows' limit is 255; a numbered twin needs a few more).</summary>
    public const int MaxName = 240;

    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A name Windows takes: refused characters become underscores, a refused ending or device name gets one more, an overlong name is cut.</summary>
    public static string Safe(string name)
    {
        var text = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            text.Append(c < ' ' || "\\/:*?\"<>|".Contains(c) ? '_' : c);
        }

        var safe = text.ToString();
        if (safe.Length > MaxName)
        {
            safe = Cut(safe, MaxName);
        }

        if (safe.Length == 0 || safe is "." or "..")
        {
            return safe.Length == 0 ? "_" : safe.Replace('.', '_');
        }

        if (safe.EndsWith('.') || safe.EndsWith(' '))
        {
            safe += "_";
        }

        var stem = safe.Split('.')[0];
        return Devices.Contains(stem) ? "_" + safe : safe;
    }

    /// <summary>
    /// Display names for a folder's entries, in their order: each made <see cref="Safe"/>, and those that would then be one
    /// name in either case numbered after the first (<c>readme (2)</c>, before the extension). Raw names that need no
    /// change keep theirs.
    /// </summary>
    public static IReadOnlyList<string> Unique(IReadOnlyList<string> raw)
    {
        var names = new string[raw.Count];
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // names that need no change go first, so a clean name never yields to a sanitised one
        var order = Enumerable.Range(0, raw.Count).OrderBy(i => Safe(raw[i]) == raw[i] ? 0 : 1).ThenBy(i => i).ToList();
        foreach (var i in order)
        {
            var safe = Safe(raw[i]);
            var name = safe;
            for (var n = 2; !taken.Add(name); n++)
            {
                name = Numbered(safe, n);
            }

            names[i] = name;
        }

        return names;
    }

    /// <summary><c>readme (2).md</c> from <c>readme.md</c>; <c>Makefile (2)</c> from <c>Makefile</c>.</summary>
    public static string Numbered(string name, int n)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && name.Length - dot <= 10 ? $"{name[..dot]} ({n}){name[dot..]}" : $"{name} ({n})";
    }

    /// <summary>An issue's folder: <c>#12 Install fails on Debian</c>.</summary>
    public static string Issue(long iid, string title) => Safe($"#{iid} {Title(title)}");

    /// <summary>A merge request's folder: <c>!7 Docs_ say more</c>.</summary>
    public static string MergeRequest(long iid, string title) => Safe($"!{iid} {Title(title)}");

    /// <summary>A title for a folder name: one line, its spaces collapsed, cut on a character boundary.</summary>
    public static string Title(string title)
    {
        var one = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return one.Length <= MaxTitle ? one : Cut(one, MaxTitle).TrimEnd() + "…";
    }

    /// <summary>The first <paramref name="length"/> characters or fewer, never half a surrogate pair or combining sequence.</summary>
    private static string Cut(string text, int length)
    {
        var info = new StringInfo(text);
        var builder = new StringBuilder(length);
        for (var i = 0; i < info.LengthInTextElements; i++)
        {
            var element = info.SubstringByTextElements(i, 1);
            if (builder.Length + element.Length > length)
            {
                break;
            }

            builder.Append(element);
        }

        return builder.ToString();
    }
}
