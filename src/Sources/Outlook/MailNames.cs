using System.Buffers;
using System.Globalization;

namespace AnyFS.Sources.Outlook;

/// <summary>
/// The names items show as (spec 7.7): a message is the folder "yyyy-MM-dd HHmmss - Sender - Subject" (local time), an
/// appointment "yyyy-MM-dd HHmm - Subject", a contact the file "Name.vcf". A character no Windows file name takes becomes
/// an underscore, a name never ends in a dot or a space, and a long one is cut (a folder's at 120 characters, a file's at
/// 150 with its extension kept), leaving room in a path for the " (2)" a name shared by two gets.
/// </summary>
internal static class MailNames
{
    public const int MaxFolderName = 120;
    public const int MaxFileName = 150;

    /// <summary>The folder in an item's folder that holds its attachments.</summary>
    public const string AttachmentsFolder = "attachments";

    /// <summary>An appointment's files.</summary>
    public const string EventMarkdown = "event.md";
    public const string EventCalendar = "event.ics";

    public const string NoSubject = "(no subject)";
    public const string NoSender = "(no sender)";

    // what no Windows file name holds: the control characters and \ / : * ? " < > |
    private static readonly SearchValues<char> Invalid = SearchValues.Create(
        "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F\"<>|:*?\\/");

    /// <summary>"2026-10-24 105729 - Adele Vance - Quarterly figures": a message's folder, before any number that tells two apart.</summary>
    public static string MessageFolder(DateTimeOffset? date, string? sender, string? subject)
    {
        var when = date is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) : "undated";
        var who = Squash(sender) ?? NoSender;
        var what = Squash(subject) ?? NoSubject;
        return Folder($"{when} - {who} - {what}");
    }

    /// <summary>"2026-10-27 1400 - Design review": an appointment's folder.</summary>
    public static string AppointmentFolder(DateTimeOffset? start, string? subject)
    {
        var when = start is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture) : "undated";
        return Folder($"{when} - {Squash(subject) ?? NoSubject}");
    }

    /// <summary>"2026-10-24 105729 - Subject": a task's or note's folder.</summary>
    public static string ItemFolder(DateTimeOffset? date, string? subject)
    {
        var when = date is { } d ? d.ToLocalTime().ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) : "undated";
        return Folder($"{when} - {Squash(subject) ?? NoSubject}");
    }

    /// <summary>"Adele Vance.vcf": a contact's file.</summary>
    public static string ContactFile(string? name) => FileName((Squash(name) ?? "(no name)") + ".vcf");

    /// <summary>The year and month folders an item is filed under ("2026", "10"), in local time; "undated" alone when it has no date.</summary>
    public static (string Year, string? Month) Period(DateTimeOffset? date)
    {
        if (date is not { } d)
        {
            return ("undated", null);
        }

        var local = d.ToLocalTime();
        return (local.Year.ToString("0000", CultureInfo.InvariantCulture), local.Month.ToString("00", CultureInfo.InvariantCulture));
    }

    /// <summary>A folder name made safe and cut to <see cref="MaxFolderName"/>.</summary>
    public static string Folder(string text)
    {
        text = Clean(Replace(text));
        if (text.Length > MaxFolderName)
        {
            text = Clean(text[..MaxFolderName]);
        }

        return text.Length == 0 ? "_" : text;
    }

    /// <summary>The name a file shows as: safe for a path and at most <see cref="MaxFileName"/> characters, the extension kept.</summary>
    public static string FileName(string name)
    {
        var text = Replace(name).Trim();
        if (text.Length > MaxFileName)
        {
            var extension = Extension(text);
            if (extension.Length > MaxFileName / 2)
            {
                extension = "";
            }

            var stem = text[..(text.Length - extension.Length)];
            text = stem[..Math.Min(stem.Length, MaxFileName - extension.Length)].Trim() + extension;
        }

        text = text.TrimEnd('.', ' ').Trim();
        return text.Length == 0 ? "_" : text;
    }

    /// <summary>A folder name told apart from another of the same name: "name (2)".</summary>
    public static string NumberedFolder(string folder, int number) => $"{folder} ({number})";

    /// <summary>A file's name told apart from another of the same name: "report (2).pdf".</summary>
    public static string NumberedFile(string name, int number)
    {
        var extension = Extension(name);
        return $"{name[..(name.Length - extension.Length)]} ({number}){extension}";
    }

    /// <summary>The characters a Windows file name cannot hold (and the control characters) as underscores.</summary>
    public static string Replace(string text)
    {
        if (text.AsSpan().IndexOfAny(Invalid) < 0)
        {
            return text;
        }

        return string.Create(text.Length, text, static (span, s) =>
        {
            for (var i = 0; i < s.Length; i++)
            {
                span[i] = Invalid.Contains(s[i]) ? '_' : s[i];
            }
        });
    }

    /// <summary>Without spaces and dots at either end (a Windows name cannot end in them).</summary>
    public static string Clean(string text) => text.Trim().TrimEnd('.', ' ').Trim();

    /// <summary>A name's extension with its dot, or "" (a name that is only an extension, ".bashrc", has none).</summary>
    public static string Extension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 || name.AsSpan(dot).ContainsAny(' ', '\t') ? "" : name[dot..];
    }

    /// <summary>Runs of whitespace (line breaks in a subject) as one space; null for nothing.</summary>
    private static string? Squash(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts);
    }
}
