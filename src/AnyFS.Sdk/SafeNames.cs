using System.Buffers;
using System.Globalization;
using System.Text;

namespace AnyFS.Sdk;

/// <summary>
/// Names a client takes for what an upstream calls something (spec 6.2): a character Windows refuses in a name becomes an
/// underscore, a name it refuses whole (a trailing dot or space, a device name) gets one more, an overlong name is cut on
/// a character boundary, and names that would then be one in either case are told apart with a number (<c>notes (2).pdf</c>).
/// The built-in sources name their folders and files with these; a plugin that shows things by their titles can too.
/// </summary>
public static class SafeNames
{
    /// <summary>The longest name a file or folder gets unless told otherwise (Windows' limit is 255; a numbered twin needs a few more).</summary>
    public const int MaxName = 240;

    private static readonly SearchValues<char> Refused = SearchValues.Create("\\/:*?\"<>|");

    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A name Windows takes: refused characters (and control characters) become underscores, a refused ending or device name
    /// gets one more, an overlong name is cut; <paramref name="trim"/> takes the white space off either end first (a title's).
    /// </summary>
    public static string Safe(string name, int maxLength = MaxName, bool trim = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        var safe = Replace(name);
        if (trim)
        {
            safe = safe.Trim();
        }

        if (safe.Length > maxLength)
        {
            safe = Cut(safe, maxLength);
        }

        if (safe.Length == 0 || safe is "." or "..")
        {
            return safe.Length == 0 ? "_" : safe.Replace('.', '_');
        }

        if (safe.EndsWith('.') || safe.EndsWith(' '))
        {
            safe += "_";
        }

        var dot = safe.IndexOf('.');
        return Devices.Contains(dot < 0 ? safe : safe[..dot]) ? "_" + safe : safe;
    }

    /// <summary>The characters a Windows name cannot hold, and the control characters, as underscores; the same string where there are none.</summary>
    public static string Replace(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var bad = -1;
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] < ' ' || Refused.Contains(name[i]))
            {
                bad = i;
                break;
            }
        }

        if (bad < 0)
        {
            return name;
        }

        return string.Create(name.Length, name, static (span, s) =>
        {
            for (var i = 0; i < s.Length; i++)
            {
                span[i] = s[i] < ' ' || Refused.Contains(s[i]) ? '_' : s[i];
            }
        });
    }

    /// <summary>
    /// Display names for a folder's entries, in their order: each made <paramref name="safe"/> (<see cref="Safe(string, int, bool)"/>
    /// by default), and those that would then be one name in either case <paramref name="numbered"/> after the first
    /// (<see cref="Numbered"/> by default). Names that need no change keep theirs ahead of sanitised ones; names in
    /// <paramref name="reserved"/> (a file the source puts beside them) are taken already.
    /// </summary>
    public static IReadOnlyList<string> Unique(IReadOnlyList<string> raw, IEnumerable<string>? reserved = null, Func<string, string>? safe = null, Func<string, int, string>? numbered = null)
    {
        ArgumentNullException.ThrowIfNull(raw);
        safe ??= name => Safe(name);
        numbered ??= Numbered;
        var names = new string[raw.Count];
        var taken = new HashSet<string>(reserved ?? [], StringComparer.OrdinalIgnoreCase);
        var order = Enumerable.Range(0, raw.Count).OrderBy(i => safe(raw[i]) == raw[i] ? 0 : 1).ThenBy(i => i).ToList();
        foreach (var i in order)
        {
            var made = safe(raw[i]);
            var name = made;
            for (var n = 2; !taken.Add(name); n++)
            {
                name = numbered(made, n);
            }

            names[i] = name;
        }

        return names;
    }

    /// <summary><c>notes (2).pdf</c> from <c>notes.pdf</c>; <c>Makefile (2)</c> from <c>Makefile</c> (an extension is at most nine characters after its dot).</summary>
    public static string Numbered(string name, int n)
    {
        ArgumentNullException.ThrowIfNull(name);
        var dot = name.LastIndexOf('.');
        return dot > 0 && name.Length - dot <= 10 ? $"{name[..dot]} ({n}){name[dot..]}" : $"{name} ({n})";
    }

    /// <summary>A title for a name: one line, its runs of white space one space each, and past <paramref name="maxLength"/> cut on a character boundary with an ellipsis.</summary>
    public static string Title(string? title, int maxLength)
    {
        var one = OneLine(title);
        return one.Length <= maxLength ? one : Cut(one, maxLength).TrimEnd() + "…";
    }

    /// <summary>A text on one line, its runs of white space one space each.</summary>
    public static string OneLine(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The first <paramref name="length"/> characters or fewer, never half a surrogate pair or combining sequence.</summary>
    public static string Cut(string text, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
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
