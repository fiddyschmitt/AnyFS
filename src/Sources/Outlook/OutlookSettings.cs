using AnyFS.Sdk;

namespace AnyFS.Sources.Outlook;

public sealed class OutlookSettings
{
    [Setting("Mailbox or file", Required = true, Primary = true, Order = 0, Kind = SettingKind.FilePath, Placeholder = @"C:\Users\me\Documents\Outlook Files\archive.pst",
        Hint = "An Outlook data file: a .pst (an archive, an exported mailbox) or a .ost (the offline copy of a mailbox, read while Outlook has it open). A mailbox address or an Exchange server will be taken here in a later release.")]
    public string Path { get; set; } = "";

    [Setting("Show hidden items", Advanced = true, Order = 10,
        Hint = "Outlook's own hidden folders and the hidden items in each folder (views, rules, settings), which Outlook never shows.")]
    public bool ShowHidden { get; set; }

    [Setting("Look for changes every (minutes)", Advanced = true, Order = 11,
        Hint = "How often the file is read again for folders and items that changed, for a file another program writes to (Outlook's OST). 0 reads it at the start only.")]
    public int RefreshMinutes { get; set; } = 5;

    /// <summary>The path trimmed, or null when there is none.</summary>
    internal string? File => string.IsNullOrWhiteSpace(Path) ? null : Path.Trim().Trim('"');

    /// <summary>Whether the path names an Outlook data file by its extension.</summary>
    internal bool IsDataFile => File is { } f && (f.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ost", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the field holds an email address or a server rather than a file: what a later release serves.</summary>
    internal bool LooksLikeAMailbox => File is { } f && (f.Contains('@') || f.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || f.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
