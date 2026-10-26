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

    /// <summary>A Microsoft sign-in as a registered app in the browser, then Microsoft Graph: Exchange Online.</summary>
    AppBrowser,

    /// <summary>The same with a device code.</summary>
    AppDeviceCode,

    /// <summary>As the person is signed in to Windows: an Exchange server.</summary>
    WindowsCurrent,

    /// <summary>A user name and password the server knows: an Exchange server.</summary>
    Windows,
}

public sealed class OutlookSettings
{
    public const string WebSession = "Web session (Outlook on the web, no app registration)";
    public const string AppBrowser = EntraSignIn.WithBrowser;
    public const string AppDeviceCode = EntraSignIn.WithDeviceCode;
    public const string WindowsCurrent = WindowsAccount.AsYourself;
    public const string Windows = WindowsAccount.WithPassword;

    [Setting("Mailbox, server or file", Required = true, Primary = true, Order = 0, Placeholder = @"jane@contoso.com, https://mail.contoso.com, or C:\Users\me\Documents\Outlook Files\archive.pst",
        Hint = "A mailbox's address (Exchange Online, signed in through Outlook on the web in a browser window of AnyFS's own), or Outlook on the web's own address for Exchange Online (https://outlook.office365.us for GCC High, https://outlook.office.com): the mailbox you sign in to there; an Exchange server's address (its Outlook on the web or EWS address, signed in with a Windows account: your own mailbox there); or an Outlook data file, a .pst (an archive, an exported mailbox) or a .ost (Outlook's offline copy of a mailbox, read while Outlook has it open).")]
    public string Path { get; set; } = "";

    [Setting("Sign in with", Order = 1, Group = "Sign-in", Kind = SettingKind.Choice, Choices = [WebSession, AppBrowser, AppDeviceCode, WindowsCurrent, Windows],
        Hint = "A mailbox on Exchange Online: the web session, or an app sign-in. The web session: AnyFS opens Outlook on the web in a browser profile of its own, you sign in there once, and AnyFS reads the mailbox the way that page does, with no app registration and nothing for an administrator to approve; a window opens again only when the sign-in lapses. App sign-in: AnyFS signs in as a registered app, in the browser or with a device code, and reads the mailbox through Microsoft Graph; an administrator must first approve AnyFS's app (or your organisation's own, under Advanced) for reading mail, calendars and contacts. An Exchange server: a Windows account, as you are signed in to Windows (nothing to type) or with a user name and password.")]
    public string SignInMethod { get; set; } = WebSession;

    [Setting("Cloud", Order = 2, Group = "Sign-in", Kind = SettingKind.Choice, Choices = [MicrosoftCloud.AutomaticName, MicrosoftCloud.GlobalName, MicrosoftCloud.UsGovernmentName],
        Hint = "Which Microsoft cloud the mailbox is in: looked up from the address (Microsoft's public realm lookup) unless said here. US Government (GCC High) mailboxes are at outlook.office365.us.")]
    public string Cloud { get; set; } = MicrosoftCloud.AutomaticName;

    [Setting("Username", Order = 3, Group = "Sign-in", Placeholder = @"CONTOSO\name, or name@contoso.com",
        Hint = "The Windows account whose mailbox to read, for an Exchange server.")]
    public string? Username { get; set; }

    [Setting("Password", Secret = true, Order = 4, Group = "Sign-in")]
    public string? Password { get; set; }

    [Setting("Tenant", Advanced = true, Order = 8, Group = "Sign-in",
        Hint = "For an app sign-in: organizations (any work or school account), or your tenant's domain or id when the sign-in needs steering to it. In the US Government cloud, organizations means the mailbox's own domain.")]
    public string Tenant { get; set; } = "organizations";

    [Setting("Client ID", Advanced = true, Order = 9, Group = "Sign-in",
        Hint = "For an app sign-in: the app registration AnyFS signs in as. Leave the default unless your organisation registered its own for AnyFS, with the delegated Microsoft Graph permissions Mail.Read, Calendars.Read and Contacts.Read; in the US Government cloud it must have, since AnyFS's own exists only in the global cloud.")]
    public string ClientId { get; set; } = EntraSignIn.DefaultClientId;

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
        Hint = "How far back a mailbox's calendars are listed, counted from the start of this month; recurring appointments appear as their occurrences. (An Outlook data file lists every appointment it holds, a recurring one as each of its occurrences.)")]
    public int CalendarMonthsBack { get; set; } = 24;

    [Setting("Calendar: months ahead", Advanced = true, Order = 14,
        Hint = "How far ahead a mailbox's calendars are listed. In an Outlook data file, how far ahead a recurring appointment without an end date is listed.")]
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

    internal OutlookKind Kind => File is null ? OutlookKind.None : IsDataFile ? OutlookKind.DataFile : IsMailboxAddress || WebCloud is not null ? OutlookKind.Mailbox : IsServerUrl ? OutlookKind.Server : OutlookKind.None;

    /// <summary>The mailbox's address in lower case, for a mailbox source given by its address; null for one given by Outlook on the web's (<see cref="WebCloud"/>).</summary>
    internal string? Address => Kind == OutlookKind.Mailbox && WebCloud is null ? File!.ToLowerInvariant() : null;

    /// <summary>
    /// The cloud whose own Outlook on the web the field's address is (https://outlook.office365.us, GCC High's; the
    /// global cloud's outlook.office.com and the rest): a mailbox on Exchange Online, the one signed in to there through the
    /// web session, never an Exchange server, which a Windows account could not sign in to (it asks for a bearer token).
    /// </summary>
    internal MicrosoftCloud? WebCloud => MicrosoftCloud.FromOutlookHost(Url?.Host);

    /// <summary>The host of Outlook on the web's address, in lower case, for a mailbox given that way; null otherwise.</summary>
    internal string? WebHost => WebCloud is null ? null : Url!.Host.ToLowerInvariant();

    /// <summary>The server's address as given, when it is an http(s) one with a host that is not Microsoft 365's own Outlook on the web.</summary>
    internal Uri? ServerUrl => WebCloud is null ? Url : null;

    private Uri? Url => File is { } f && Uri.TryCreate(f, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" && !string.IsNullOrEmpty(url.Host) ? url : null;

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

    /// <summary>
    /// How the sign-in goes: for a mailbox on Exchange Online the web session, or an app sign-in when one is chosen; for a
    /// server a Windows account, as yourself unless a user name and password are chosen.
    /// </summary>
    internal OutlookSignIn EffectiveSignIn => Kind switch
    {
        OutlookKind.Server => Chose(Windows) ? OutlookSignIn.Windows : OutlookSignIn.WindowsCurrent,
        _ when Chose(AppBrowser) => OutlookSignIn.AppBrowser,
        _ when Chose(AppDeviceCode) => OutlookSignIn.AppDeviceCode,
        _ => OutlookSignIn.WebSession,
    };

    /// <summary>An app sign-in and Microsoft Graph: a mailbox, with one of the app sign-ins chosen.</summary>
    internal bool IsAppSignIn => EffectiveSignIn is OutlookSignIn.AppBrowser or OutlookSignIn.AppDeviceCode;

    /// <summary>The Microsoft sign-in flavour for <see cref="EntraSignIn"/>: a device code when chosen, else the browser.</summary>
    internal SignInMethod Method => EffectiveSignIn == OutlookSignIn.AppDeviceCode ? Microsoft365.SignInMethod.DeviceCode : Microsoft365.SignInMethod.Browser;

    internal string EffectiveClientId => string.IsNullOrWhiteSpace(ClientId) ? EntraSignIn.DefaultClientId : ClientId.Trim();

    /// <summary>The mailbox's domain: contoso.com for jane@contoso.com.</summary>
    internal string? Domain => Address is { } address ? address[(address.LastIndexOf('@') + 1)..] : null;

    /// <summary>The cloud as far as the settings say: the one chosen, else the tenant's or the address's initial domain's; null when the realm lookup must tell.</summary>
    internal MicrosoftCloud? KnownCloud => WebCloud ?? MicrosoftCloud.FromName(Cloud) ?? MicrosoftCloud.FromTenant(Tenant) ?? MicrosoftCloud.FromTenant(Domain);

    /// <summary>
    /// The tenant an app sign-in goes to in <paramref name="cloud"/>: the one given, organizations by default, except
    /// outside the global cloud, where organizations becomes the mailbox's domain: an organisation's own registration
    /// there is usually for its own tenant only, which the organizations endpoint refuses (AADSTS50194).
    /// </summary>
    internal string TenantIn(MicrosoftCloud cloud)
    {
        var tenant = string.IsNullOrWhiteSpace(Tenant) ? "organizations" : Tenant.Trim();
        return !cloud.IsGlobal && string.Equals(tenant, "organizations", StringComparison.OrdinalIgnoreCase) && Domain is { } domain ? domain : tenant;
    }

    private bool Chose(string choice) => string.Equals(SignInMethod?.Trim(), choice, StringComparison.OrdinalIgnoreCase);

    internal bool IsWindowsCurrent => EffectiveSignIn == OutlookSignIn.WindowsCurrent;

    internal string? Browser => string.IsNullOrWhiteSpace(BrowserPath) ? null : BrowserPath.Trim();
}
