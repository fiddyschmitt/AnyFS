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

/// <summary>How a mailbox is signed in to.</summary>
internal enum OutlookSignIn
{
    /// <summary>Outlook on the web in a browser of AnyFS's own: Exchange Online.</summary>
    WebSession,

    /// <summary>As the person is signed in to Windows: an Exchange server.</summary>
    WindowsCurrent,

    /// <summary>A user name and password the server knows: an Exchange server.</summary>
    Windows,
}

public sealed class OutlookSettings
{
    public const string WebSession = "Web session (Outlook on the web, no app registration)";
    public const string WindowsCurrent = WindowsAccount.AsYourself;
    public const string Windows = WindowsAccount.WithPassword;

    public const string CloudAutomatic = "Automatic";
    public const string CloudGlobal = "Global";
    public const string CloudUsGovernment = "US Government (GCC High)";

    [Setting("Mailbox, server or file", Required = true, Primary = true, Order = 0, Placeholder = @"jane@contoso.com, https://mail.contoso.com, or C:\Users\me\Documents\Outlook Files\archive.pst",
        Hint = "A mailbox's address (Exchange Online, signed in through Outlook on the web in a browser window of AnyFS's own); an Exchange server's address (its Outlook on the web or EWS address, signed in with a Windows account: your own mailbox there); or an Outlook data file, a .pst (an archive, an exported mailbox) or a .ost (Outlook's offline copy of a mailbox, read while Outlook has it open).")]
    public string Path { get; set; } = "";

    [Setting("Sign in with", Order = 1, Group = "Sign-in", Kind = SettingKind.Choice, Choices = [WebSession, WindowsCurrent, Windows],
        Hint = "A mailbox on Exchange Online: the web session. AnyFS opens Outlook on the web in a browser profile of its own, you sign in there once, and AnyFS reads the mailbox the way that page does, with no app registration and nothing for an administrator to approve; a window opens again only when the sign-in lapses. An Exchange server: a Windows account, as you are signed in to Windows (nothing to type) or with a user name and password.")]
    public string SignInMethod { get; set; } = WebSession;

    [Setting("Cloud", Order = 2, Group = "Sign-in", Kind = SettingKind.Choice, Choices = [CloudAutomatic, CloudGlobal, CloudUsGovernment],
        Hint = "Which Microsoft cloud the mailbox is in: looked up from the address (Microsoft's public realm lookup) unless said here. US Government (GCC High) mailboxes are at outlook.office365.us.")]
    public string Cloud { get; set; } = CloudAutomatic;

    [Setting("Username", Order = 3, Group = "Sign-in", Placeholder = @"CONTOSO\name, or name@contoso.com",
        Hint = "The Windows account whose mailbox to read, for an Exchange server.")]
    public string? Username { get; set; }

    [Setting("Password", Secret = true, Order = 4, Group = "Sign-in")]
    public string? Password { get; set; }

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

    [Setting("Trust any certificate", Advanced = true, Order = 15,
        Hint = "Accept an Exchange server's HTTPS certificate whatever it is (a lab with a self-signed one). Off for a real server.")]
    public bool TrustAnyCertificate { get; set; }

    /// <summary>The first field trimmed, or null when there is none.</summary>
    internal string? File => string.IsNullOrWhiteSpace(Path) ? null : Path.Trim().Trim('"');

    /// <summary>Whether the path names an Outlook data file by its extension.</summary>
    internal bool IsDataFile => File is { } f && (f.EndsWith(".pst", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ost", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the field holds an email address: a mailbox on its server.</summary>
    internal bool IsMailboxAddress => File is { } f && !f.Contains('\\') && !f.Contains('/') && !f.Contains(' ') && f.IndexOf('@') is > 0 and var at && f.IndexOf('.', at) > at + 1;

    /// <summary>Whether the field holds a server's address (an Exchange server's Outlook on the web or EWS address).</summary>
    internal bool IsServerUrl => ServerUrl is not null;

    internal OutlookKind Kind => File is null ? OutlookKind.None : IsDataFile ? OutlookKind.DataFile : IsMailboxAddress ? OutlookKind.Mailbox : IsServerUrl ? OutlookKind.Server : OutlookKind.None;

    /// <summary>The mailbox's address in lower case, for a mailbox source.</summary>
    internal string? Address => Kind == OutlookKind.Mailbox ? File!.ToLowerInvariant() : null;

    /// <summary>The server's address as given, when it is an http(s) one with a host.</summary>
    internal Uri? ServerUrl => File is { } f && Uri.TryCreate(f, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" && !string.IsNullOrEmpty(url.Host) ? url : null;

    /// <summary>The server's EWS endpoint: the address as given when it names Exchange.asmx, else /EWS/Exchange.asmx on its host.</summary>
    internal Uri? EwsUrl
    {
        get
        {
            if (ServerUrl is not { } url)
            {
                return null;
            }

            return url.AbsolutePath.Contains("/ews/exchange.asmx", StringComparison.OrdinalIgnoreCase) ? url : new Uri(url, "/EWS/Exchange.asmx");
        }
    }

    /// <summary>How the sign-in goes: the web session for a mailbox on Exchange Online; for a server a Windows account, as yourself unless a user name and password are chosen.</summary>
    internal OutlookSignIn EffectiveSignIn => Kind switch
    {
        OutlookKind.Server => string.Equals(SignInMethod?.Trim(), Windows, StringComparison.OrdinalIgnoreCase) ? OutlookSignIn.Windows : OutlookSignIn.WindowsCurrent,
        _ => OutlookSignIn.WebSession,
    };

    internal bool IsWindowsCurrent => EffectiveSignIn == OutlookSignIn.WindowsCurrent;

    internal string? Browser => string.IsNullOrWhiteSpace(BrowserPath) ? null : BrowserPath.Trim();
}
