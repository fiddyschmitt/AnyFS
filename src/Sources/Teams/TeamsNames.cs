using System.Globalization;
using System.Text;

namespace AnyFS.Sources.Teams;

/// <summary>
/// The names Teams' things show as (spec 7.9): a team, channel, chat or thread folder made safe for Windows (a refused
/// character becomes an underscore, a refused ending or device name gets one more, an overlong name is cut), and two that
/// would be one name in either case told apart with a number. The same rules as the other sources' name makers.
/// </summary>
internal static class TeamsNames
{
    public const int MaxName = 200;

    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Safe(string name)
    {
        var text = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            text.Append(c < ' ' || "\\/:*?\"<>|".Contains(c) ? '_' : c);
        }

        var safe = Cut(text.ToString(), MaxName);
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

    /// <summary>Display names for a set of things in order: each made <see cref="Safe"/>, twins in case numbered after the first.</summary>
    public static IReadOnlyList<string> Unique(IReadOnlyList<string> raw)
    {
        var names = new string[raw.Count];
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = Enumerable.Range(0, raw.Count).OrderBy(i => Safe(raw[i]) == raw[i] ? 0 : 1).ThenBy(i => i).ToList();
        foreach (var i in order)
        {
            var safe = Safe(raw[i]);
            var name = safe;
            for (var n = 2; !taken.Add(name); n++)
            {
                name = $"{safe} ({n})";
            }

            names[i] = name;
        }

        return names;
    }

    private static string Cut(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }

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
