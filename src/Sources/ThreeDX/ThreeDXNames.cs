using System.Text;

namespace AnyFS.Sources.ThreeDX;

/// <summary>
/// The names documents and files show as (spec 7.6), as Mount3DX names them, so a path someone keeps from it still leads to
/// the same file: a document is the folder "Name Rev A (Title)", the title left out when it is the name; a file keeps its
/// title. A character no Windows file name takes becomes an underscore, a name never ends in a dot or a space, and a long
/// one is cut (a document's at 120 characters, a file's at 150 with its extension kept), leaving room in a path for the
/// " (2)" a name shared by two gets.
/// </summary>
internal static class ThreeDXNames
{
    public const int MaxDocumentName = 120;
    public const int MaxFileName = 150;

    /// <summary>The Internet shortcut in a document's folder that opens the document in 3DX.</summary>
    public const string LinkFile = "_link.url";

    // what no Windows file name holds: the control characters and \ / : * ? " < > |
    private static readonly System.Buffers.SearchValues<char> Invalid = System.Buffers.SearchValues.Create(
        "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F\"<>|:*?\\/");

    /// <summary>The document's attributes as 3DX gives them, made when it is read.</summary>
    public const string MetadataFile = "_metadata.json";

    /// <summary>"Name Rev A (Title)": the folder a document shows as, before any number that tells two apart.</summary>
    public static string DocumentFolder(string name, string revision, string title)
    {
        var text = $"{name} Rev {revision}";
        if (!string.Equals(name, title, StringComparison.OrdinalIgnoreCase))
        {
            text += $" ({title})";
        }

        text = Clean(Replace(text));
        if (text.Length > MaxDocumentName)
        {
            text = Clean(text[..MaxDocumentName]);
        }

        return text.Length == 0 ? "_" : text;
    }

    /// <summary>The name a file shows as: its title, safe for a path and at most <see cref="MaxFileName"/> characters, the extension kept.</summary>
    public static string FileName(string title)
    {
        var text = Replace(title);
        if (text.Length > MaxFileName)
        {
            var extension = Extension(text);
            if (extension.Length > MaxFileName / 2)
            {
                // a "extension" that would take most of the room is no extension (a name that merely holds a dot)
                extension = "";
            }

            var stem = text[..(text.Length - extension.Length)];
            text = stem[..Math.Min(stem.Length, MaxFileName - extension.Length)].Trim() + extension;
        }

        text = text.TrimEnd('.', ' ').Trim();
        return text.Length == 0 ? "_" : text;
    }

    /// <summary>A document's folder name told apart from another of the same name: "Name Rev A (Title) (2) (Document)".</summary>
    public static string NumberedFolder(string folder, int number, string type) => $"{folder} ({number}) ({Clean(Replace(type))})";

    /// <summary>A file's name told apart from another of the same name in its folder: "report (2).pdf".</summary>
    public static string NumberedFile(string name, int number)
    {
        var extension = Extension(name);
        return $"{name[..(name.Length - extension.Length)]} ({number}){extension}";
    }

    /// <summary>The characters a Windows file name cannot hold (and the control characters) as underscores; the same string when there are none.</summary>
    public static string Replace(string text)
    {
        if (text.AsSpan().IndexOfAny(Invalid) < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c < ' ' || c is '"' or '<' or '>' or '|' or ':' or '*' or '?' or '\\' or '/' ? '_' : c);
        }

        return builder.ToString();
    }

    /// <summary>Without the dots and spaces a name may not end in, nor the spaces it starts with.</summary>
    private static string Clean(string text) => text.TrimEnd('.').Trim().TrimEnd('.').Trim();

    /// <summary>".pdf" from "report.pdf"; "" when there is no dot after the first character.</summary>
    private static string Extension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? "" : name[dot..];
    }
}
