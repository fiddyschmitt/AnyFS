using System.Buffers;
using System.Text;
using AnyFS.Sdk;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// What SharePoint accepts as a file or folder name, checked before a request goes out so a client gets a
/// clear error at once (the server would answer 400 invalidRequest a round trip later). The rules are the
/// documented ones for SharePoint Online and SharePoint Server: no <c>" * : &lt; &gt; ? / \ |</c>, no
/// leading or trailing space, no trailing dot, no name that is only dots, none of the reserved names,
/// no <c>~$</c> prefix (Office lock files), no <c>_vti_</c>, and a path of at most 400 characters.
/// </summary>
public static class SharePointNames
{
    public const int MaxPathLength = 400;

    private static readonly SearchValues<char> Invalid = SearchValues.Create("\"*:<>?/\\|");

    /// <summary>True for a character SharePoint refuses in a name.</summary>
    internal static bool IsInvalidChar(char c) => Invalid.Contains(c);

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lock", "CON", "PRN", "AUX", "NUL", "desktop.ini",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Why <paramref name="name"/> is not a valid SharePoint name, or null when it is.</summary>
    public static string? Problem(string name)
    {
        if (name.Length == 0)
        {
            return "the name is empty";
        }

        var bad = name.AsSpan().IndexOfAny(Invalid);
        if (bad >= 0)
        {
            return $"SharePoint does not allow '{name[bad]}' in a name";
        }

        if (name[0] == ' ' || name[^1] == ' ')
        {
            return "SharePoint does not allow a name to start or end with a space";
        }

        if (name[^1] == '.')
        {
            return "SharePoint does not allow a name to end with a dot";
        }

        if (name.AsSpan().IndexOfAnyExcept('.') < 0)
        {
            return "SharePoint does not allow a name made of dots";
        }

        if (name.StartsWith("~$", StringComparison.Ordinal))
        {
            return "SharePoint does not allow names starting with ~$ (Office lock files)";
        }

        if (name.Contains("_vti_", StringComparison.OrdinalIgnoreCase))
        {
            return "SharePoint does not allow _vti_ in a name";
        }

        var stem = name.IndexOf('.') is var dot && dot > 0 ? name[..dot] : name;
        if (Reserved.Contains(name) || Reserved.Contains(stem))
        {
            return $"'{name}' is a name SharePoint reserves";
        }

        return null;
    }

    /// <summary>
    /// A title as a folder's name, for what shows by its title (a library, a container): a title may hold what a name
    /// cannot ("Health / Safety"), so each of <c>" * : &lt; &gt; ? / \ |</c> shows as its full-width look-alike
    /// (Health ／ Safety), control characters are left out, and at the end, where Windows drops them, spaces are left off
    /// and a dot shows as its look-alike. A title that is already a name stays as it is; empty when nothing is left.
    /// </summary>
    public static string FolderName(string title)
    {
        var name = new StringBuilder(title.Length);
        foreach (var c in title)
        {
            if (!char.IsControl(c))
            {
                name.Append(c switch
                {
                    '"' => '＂',
                    '*' => '＊',
                    ':' => '：',
                    '<' => '＜',
                    '>' => '＞',
                    '?' => '？',
                    '/' => '／',
                    '\\' => '＼',
                    '|' => '｜',
                    _ => c,
                });
            }
        }

        while (name.Length > 0 && name[^1] == ' ')
        {
            name.Length--;
        }

        for (var i = name.Length - 1; i >= 0 && name[i] == '.'; i--)
        {
            name[i] = '．';
        }

        return name.ToString();
    }

    /// <summary>Throws <see cref="VfsError.InvalidPath"/> when the last segment of <paramref name="path"/> or the whole path is not acceptable.</summary>
    public static void Require(string path)
    {
        if (path.Length > MaxPathLength)
        {
            throw VfsException.InvalidPath(path, $"SharePoint paths are at most {MaxPathLength} characters");
        }

        var (_, name) = VfsPath.SplitLast(path);
        if (Problem(name) is { } problem)
        {
            throw VfsException.InvalidPath(path, problem);
        }
    }
}
