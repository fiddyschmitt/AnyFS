using AnyFS.Sdk;

namespace AnyFS.Sources.Outlook;

/// <summary>What the first field names: an Outlook data file, a mailbox on Exchange Online, an Exchange server, or nothing yet.</summary>
internal enum OutlookKind
{
    None,
    DataFile,
    Mailbox,
    Server,
}

public sealed class OutlookSettings
{
    public const string WebSession = "Web session (Outlook on the web, no app registration)";

    public const string CloudAutomatic = "Automatic";
    public const string CloudGlobal = "Global";
    public const string CloudUsGovernment = "US Government (GCC High)";

    [Setting("Mailbox or file", Required = true, Primary = true, Order = 0, Placeholder = @"jane@contoso.com, or C:\Users\me\Documents\Outlook Files\archive.pst",
        Hint = "A mailbox's address (Exchange Online, signed in through Outlook on the web in a browser window of AnyFS's own), or an Outlook data file: a .pst (an archive, an exported mailbox) or a .ost (Outlook's offline copy of a mailbox, read while Outlook has it open). An Exchange server's address will be taken here in a later release.")]
    public string Path { get; set; } = "";

    [Setting("Sign in with", Order = 1, Group = "Sign-in", Kind = SettingKind.Choice, Choices = [WebSession],
        Hint = "Web session: AnyFS opens Outlook on the web in a browser profile of its own, you sign in there once, and AnyFS reads the mailbox the way that page does, with no app registration and nothing for an administrator to approve. The sign-in is kept; a window opens again only when it lapses.")]
    public string SignInMethod { get; set; } = WebSession;

    [Setting("Cloud", Order = 2, Group = "Sign-in", Kind = SettingKind.Choice, Choices = [CloudAutomatic, CloudGlobal, CloudUsGovernment],
        Hint = "Which Microsoft cloud the mailbox is in: looked up from the address (Microsoft's public realm lookup) unless said here. US Government (GCC High) mailboxes are at outlook.office365.us.")]
    public string Cloud { get; set; } = CloudAutomatic;

    [Setting("Browser", Advanced = true, Order = 10, Kind = SettingKind.FilePath,
        Hint = "The Edge, Chrome or Chromium to open for the web session. Leave empty to find one automatically. AnyFS opens it with a profile of its own, never your everyday browser's.")]
    public string? BrowserPath { get; set; }

    [Setting("Show hidden items", Advanced = true, Order = 11,
        Hint = "Outlook's own hidden folders and items (views, rules, settings, its people caches), which Outlook never shows.")]
    public bool ShowHidden { get; set; }

    [Setting("Look for changes every (minutes)", Advanced = true, Order = 12,
        Hint = "A mailbox: how often each folder is asked for what changed (new mail shows up within this). A file: how often it is read again when another program writes to it (Outlook's OST). 0 reads at the start only.")]
    public int RefreshMinutes { get; set; } = 5;

    [Setting("Calendar: months back", Advanced = true, Order = 13,
        Hint = "How far back a mailbox's calendars are listed, counted from the start of this month; recurring appointments appear as their occurrences. (An Outlook data file lists every appointment it holds.)")]
    public int CalendarMonthsBack { get; set; } = 24;

    [Setting("Calendar: months ahead", Advanced = true, Order = 14, Hint = "How far ahead a mailbox's calendars are listed.")]
    public int CalendarMonthsAhead { get; set; } = 12;

    /// <summary>The first field trimmed, or null when there is none.</summary>
    internal string? File => string.IsNullOrWhiteSpace(Path) ? null : Path.Trim().Trim('"');

    /// <summary>Whether the path names an Outlook data file by its extension.</summary>
    internal bool IsDataFile => File is { } f && (f.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ost", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the field holds an email address: a mailbox on its server.</summary>
    internal bool IsMailboxAddress => File is { } f && !f.Contains('\\') && !f.Contains('/') && !f.Contains(' ') && f.IndexOf('@') is > 0 and var at && f.IndexOf('.', at) > at + 1;

    /// <summary>Whether the field holds a server's address (an Exchange server's EWS or OWA URL).</summary>
    internal bool IsServerUrl => File is { } f && (f.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || f.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    internal OutlookKind Kind => File is null ? OutlookKind.None : IsDataFile ? OutlookKind.DataFile : IsMailboxAddress ? OutlookKind.Mailbox : IsServerUrl ? OutlookKind.Server : OutlookKind.None;

    /// <summary>The mailbox's address in lower case, for a mailbox source.</summary>
    internal string? Address => Kind == OutlookKind.Mailbox ? File!.ToLowerInvariant() : null;

    internal string? Browser => string.IsNullOrWhiteSpace(BrowserPath) ? null : BrowserPath.Trim();
}

