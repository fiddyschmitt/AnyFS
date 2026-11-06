using System.Globalization;
using AnyFS.Sdk;

namespace AnyFS.Sources.Atlassian;

/// <summary>
/// The names the Atlassian products' things show as (spec 7.10 to 7.12), by the SDK's <see cref="SafeNames"/>: a title is
/// trimmed and cut to a length that leaves room for the path above it; a folder's name may carry a key in parentheses or a
/// number after a hash, which these read back.
/// </summary>
internal static class AtlassianNames
{
    /// <summary>The longest title kept in an issue's folder name, or a page's.</summary>
    public const int MaxTitle = 100;

    /// <summary>A name Windows takes: refused characters become underscores, a refused ending or device name gets one more, an overlong name is cut.</summary>
    public static string Safe(string name) => SafeNames.Safe(name, trim: true);

    /// <summary>Display names for a folder's entries, in their order, each made <see cref="Safe"/> and twins in case numbered; names in <paramref name="reserved"/> are taken already.</summary>
    public static IReadOnlyList<string> Unique(IReadOnlyList<string> raw, IEnumerable<string>? reserved = null) => SafeNames.Unique(raw, reserved, Safe);

    /// <summary><c>notes (2).pdf</c> from <c>notes.pdf</c>; <c>Makefile (2)</c> from <c>Makefile</c>.</summary>
    public static string Numbered(string name, int n) => SafeNames.Numbered(name, n);

    /// <summary>The key a folder's name ends with in parentheses (<c>Alpha (ABC)</c>, <c>Build (PROJ-PLAN)</c>), or null.</summary>
    public static string? KeyInParentheses(string name)
    {
        if (!name.EndsWith(')'))
        {
            return null;
        }

        var open = name.LastIndexOf('(');
        var key = open < 0 ? "" : name[(open + 1)..^1].Trim();
        return key.Length == 0 || key.Contains(' ') ? null : key;
    }

    /// <summary>The number a folder's name starts with after a hash (<c>#12 Title</c>), or null.</summary>
    public static long? NumberAfterHash(string name)
    {
        var space = name.IndexOf(' ');
        var head = space < 0 ? name : name[..space];
        return head.Length > 1 && head[0] == '#' && long.TryParse(head[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    /// <summary>A title for a name: one line, its spaces collapsed, cut on a character boundary with an ellipsis.</summary>
    public static string Title(string? title) => SafeNames.Title(title, MaxTitle);

    /// <summary>A text on one line, its runs of white space one space each.</summary>
    public static string OneLine(string? text) => SafeNames.OneLine(text);
}
