using AnyFS.Sdk;

namespace AnyFS.Sources.Teams;

/// <summary>How a Teams source signs in (spec 7.9). Web session is the only way: the Teams web client mints no Graph token (spike 18), so there is no token or app sign-in to offer.</summary>
internal enum TeamsSignIn
{
    /// <summary>A browser of AnyFS's own on the Teams web client; AnyFS keeps the session and reads the tokens the client mints for its own services.</summary>
    WebSession,
}

/// <summary>
/// A Microsoft cloud: the Teams web host the browser opens, and the API host the <c>authz</c> discovery call goes to
/// (spike 18). The services' regional URLs come back from <c>authz</c> at runtime, not set here.
/// </summary>
internal sealed record TeamsCloud(string Name, string TeamsHost, string ApiHost, string Display)
{
    public static readonly TeamsCloud Global = new("global", "https://teams.cloud.microsoft", "https://teams.microsoft.com", "Microsoft 365 (global)");

    // the US government clouds (the user's work may be one): the hosts differ; the sign-in follows the account
    public static readonly TeamsCloud UsGov = new("gcc-high", "https://gov.teams.microsoft.us", "https://gov.teams.microsoft.us", "US Gov (GCC High)");
    public static readonly TeamsCloud UsGovDod = new("dod", "https://dod.teams.microsoft.us", "https://dod.teams.microsoft.us", "US Gov (DoD)");

    public static readonly IReadOnlyList<TeamsCloud> All = [Global, UsGov, UsGovDod];
}

public sealed class TeamsSettings
{
    public const string WebSession = "Web session, in a browser";

    [Setting("Account", Required = true, Primary = true, Order = 0, Placeholder = "you@example.com",
        Hint = "The work or school account whose Teams to show: your email. AnyFS opens the Teams sign-in in a browser of its own for you to sign in (single sign-on and all), and shows that account's teams, channels and chats, read-only.")]
    public string Account { get; set; } = "";

    [Setting("Sign in with", Order = 1, Group = "Sign-in", Kind = SettingKind.Choice, Choices = [WebSession],
        Hint = "Web session: AnyFS opens Teams on the web in a browser of its own for you to sign in, and keeps that session; it renews it without a window while the sign-in holds, and shows the window again only when it has lapsed. Teams on the web has no app-registration or token sign-in to offer, so this is the only way.")]
    public string SignInMethod { get; set; } = WebSession;

    [Setting("Channel messages", Order = 5,
        Hint = "In each team's channels, a folder per conversation (the opening message and its replies) with the messages as Markdown and their files and images.")]
    public bool ChannelMessages { get; set; } = true;

    [Setting("Chats", Order = 6,
        Hint = "Your 1:1, group and meeting chats, each a Markdown file of the whole conversation with its shared files and images.")]
    public bool Chats { get; set; } = true;

    [Setting("Files", Order = 7,
        Hint = "In each channel, a files folder: the channel's folder of its team's document library. In each post and chat, an attachments folder: the files its messages share and their inline images. Read from the team's SharePoint site, the sender's OneDrive and Teams' own media service.")]
    public bool Files { get; set; } = true;

    [Setting("Cloud", Advanced = true, Order = 10, Kind = SettingKind.Choice,
        Choices = ["Microsoft 365 (global)", "US Gov (GCC High)", "US Gov (DoD)"],
        Hint = "Which Microsoft cloud the account is in. Global for almost everyone; the US Government clouds use their own Teams hosts.")]
    public string Cloud { get; set; } = "Microsoft 365 (global)";

    [Setting("Browser", Advanced = true, Order = 11, Kind = SettingKind.FilePath,
        Hint = "The Edge, Chrome, Chromium or Firefox to open for the web session's sign-in. Leave empty to find one automatically: Edge, Chrome or Chromium, and Firefox (through its WebDriver) where an organisation's policy keeps AnyFS out of those. AnyFS opens it with a profile of its own, never your everyday browser's.")]
    public string? BrowserPath { get; set; }

    [Setting("Conversations active in the last (days)", Advanced = true, Order = 12,
        Hint = "Chats and channel posts whose last message is older than this are left out, and a conversation is read back no further than this (a busy account has thousands). 0 shows everything.")]
    public int WithinDays { get; set; }

    [Setting("Check for changes every (seconds)", Advanced = true, Order = 13,
        Hint = "How old a listing of something that changes (the teams and chats, a conversation's messages) may be before it is asked for again. 0 asks every time, and keeps no folder listings.")]
    public int RefreshSeconds { get; set; } = 60;

    [Setting(KeptListings.KeepSetting, Advanced = true, Order = 14,
        Hint = "Answer from what AnyFS last saw of each folder, so folders open at once; a listing older than 'Check for changes every' is asked for again behind the answer. With 'Count files on start', the count walks the teams, channels and chats first and keeps what it finds (below). Off: each folder is asked for as it is opened.")]
    public bool KeepListings { get; set; } = true;

    [Setting(KeptListings.WalkSetting, Advanced = true, Order = 15,
        Hint = "With folder listings kept and 'Count files on start': how long after one walk the next begins (the count's is the first). 0 walks for the count only. A walk lists the teams, channels and chats, but not each conversation's messages, which are read when the conversation is opened.")]
    public int WalkMinutes { get; set; } = 30;

    [Setting("Requests at a time", Advanced = true, Order = 16, Hint = "How many requests AnyFS has open with Teams at once.")]
    public int Concurrency { get; set; } = 8;

    internal TeamsSignIn EffectiveSignIn => TeamsSignIn.WebSession;

    internal TeamsCloud TheCloud => Cloud.Trim() switch
    {
        var c when c.Contains("DoD", StringComparison.OrdinalIgnoreCase) => TeamsCloud.UsGovDod,
        var c when c.Contains("GCC", StringComparison.OrdinalIgnoreCase) || c.Contains("Gov", StringComparison.OrdinalIgnoreCase) => TeamsCloud.UsGov,
        _ => TeamsCloud.Global,
    };

    internal string? Browser => string.IsNullOrWhiteSpace(BrowserPath) ? null : BrowserPath.Trim();

    internal string AccountHint => Account.Trim();

    internal bool Keeps => KeepListings && RefreshSeconds > 0;

    internal TimeSpan Refresh => TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 0, 86_400));

    internal TimeSpan WalkEvery => TimeSpan.FromMinutes(Math.Clamp(WalkMinutes, 0, 7 * 24 * 60));

    internal TimeSpan? Within => WithinDays > 0 ? TimeSpan.FromDays(WithinDays) : null;
}
