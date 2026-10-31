using System.Text.Json;
using AnyFS.Sdk;
using AnyFS.Sources.Common;

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
    /// <summary>Microsoft Graph's address in the cloud (where a Graph token's audience does not name it).</summary>
    public string GraphHost => Name switch
    {
        "gcc-high" => "https://graph.microsoft.us",
        "dod" => "https://dod-graph.microsoft.us",
        _ => "https://graph.microsoft.com",
    };

    public static readonly TeamsCloud Global = new("global", "https://teams.cloud.microsoft", "https://teams.microsoft.com", "Microsoft 365 (global)");

    // the US government clouds: GCC High's verified at a GCC High tenant (its authz answers at gov.teams.microsoft.us);
    // DoD's as Microsoft's endpoint list names them (dod.teams.microsoft.us, *.dod.teams.microsoft.us, *.sharepoint-mil.us,
    // login.microsoftonline.us, 29-Oct-2026), its services taken to follow GCC High's with dod for gov: unverified, for want
    // of a DoD tenant
    public static readonly TeamsCloud UsGov = new("gcc-high", "https://gov.teams.microsoft.us", "https://gov.teams.microsoft.us", "US Gov (GCC High)");
    public static readonly TeamsCloud UsGovDod = new("dod", "https://dod.teams.microsoft.us", "https://dod.teams.microsoft.us", "US Gov (DoD)");

    public static readonly IReadOnlyList<TeamsCloud> All = [Global, UsGov, UsGovDod];

    /// <summary>The cloud an organisation is in, by what the identity platform publishes for its domain: DoD, GCC High, else the global cloud (GCC "moderate" among it).</summary>
    public static TeamsCloud Of(MicrosoftRealm realm) => realm.IsDod ? UsGovDod : realm.IsUsGovernment ? UsGov : Global;

    /// <summary>A cloud by its <see cref="Name"/>; null for none.</summary>
    public static TeamsCloud? Named(string? name) => All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The cloud a source signs in to, and how it was found: the one chosen in its settings; else, for Automatic, the one
    /// the identity platform publishes for the account's domain (no sign-in needed), remembered in the source's state
    /// folder; the one remembered when that cannot be read now; else the global cloud.
    /// </summary>
    /// <param name="handler">The lookup's handler: a test's; null for the network.</param>
    internal static async Task<(TeamsCloud Cloud, string How)> ResolveAsync(TeamsSettings settings, string stateDirectory, HttpMessageHandler? handler, CancellationToken ct)
    {
        if (settings.ChosenCloud is { } chosen)
        {
            return (chosen, "as chosen in the source's settings");
        }

        var account = settings.AccountHint;
        var domain = MicrosoftRealm.DomainOf(account) ?? account;
        var file = Path.Combine(stateDirectory, "cloud.json");
        if (await MicrosoftRealm.LookupAsync(account, handler, ct).ConfigureAwait(false) is { } realm)
        {
            var found = Of(realm);
            try
            {
                Directory.CreateDirectory(stateDirectory);
                await File.WriteAllTextAsync(file, new System.Text.Json.Nodes.JsonObject { ["domain"] = domain, ["cloud"] = found.Name }.ToJsonString(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // kept for this start alone
            }

            return (found, $"found from {realm}");
        }

        try
        {
            using var kept = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
            string? S(string name) => kept.RootElement.ValueKind == JsonValueKind.Object && kept.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (string.Equals(S("domain"), domain, StringComparison.OrdinalIgnoreCase) && Named(S("cloud")) is { } last)
            {
                return (last, $"as last found for {domain}: Microsoft's sign-in details for it could not be read now");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // none remembered
        }

        return (Global, $"Microsoft's sign-in details named no organisation for {domain}, so the global cloud; choose the Cloud setting if that is wrong");
    }
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
        Hint = "In each team's channels, a folder per conversation (the opening message and its replies) with the messages as Markdown and their files and images: those among the channel's latest 2,000 messages in posts, the earlier ones in posts\\earlier, a folder per month, read back to the channel's start when that folder is opened.")]
    public bool ChannelMessages { get; set; } = true;

    [Setting("Chats", Order = 6,
        Hint = "Your 1:1, group and meeting chats, each a Markdown file: who is in it (guests, bots and other organisations' people marked), who started it and when, its pinned messages, then the conversation (its latest 2,000 messages; the earlier ones in its earlier folder, a folder per month, read back to the chat's start when that folder is opened), with its shared files and images.")]
    public bool Chats { get; set; } = true;

    [Setting("Files", Order = 7,
        Hint = "In each channel, a files folder: the channel's folder of its team's document library. In each post and chat, an attachments folder: the files its messages share and their inline images. Read from the team's SharePoint site, the sender's OneDrive and Teams' own media service. The files folders and shared files show once Teams on the web has handed out its SharePoint token or, where it gives one (GCC High's does), its Microsoft Graph token; AnyFS asks for them in the background.")]
    public bool Files { get; set; } = true;

    [Setting("Groups", Order = 8,
        Hint = "An Other groups folder: every group site you can open beyond your teams above (public groups you have not joined among them), each with its files, shown once Teams on the web has handed out its SharePoint token or its Microsoft Graph token. And two files of groups with their details: groups.md, the groups you are in (of every kind where the Teams client gives a token that can list them, as GCC High's does; else your teams'), and all-groups.md, every group there is (every group in the organisation with that token, which can run to tens of megabytes; else every group you can see).")]
    public bool Groups { get; set; } = true;

    public const string AutomaticCloud = "Automatic";

    [Setting("Calls and activity", Order = 9,
        Hint = "At the top: calls.md, your call history (when, which way, with whom, how it went, how long), and activity.md, your activity feed (mentions, replies, reactions…), as Teams keeps them.")]
    public bool CallsAndActivity { get; set; } = true;

    [Setting("Meetings", Order = 10,
        Hint = "In each meeting's chat: meeting.md (each time it was held: when, what for, who organised it, who came and for how long) and a recordings folder with its recordings, played from Teams' own media service as they are read (a recording runs to hundreds of megabytes; nothing is copied until it is opened).")]
    public bool Meetings { get; set; } = true;

    [Setting("Cloud", Advanced = true, Order = 11, Kind = SettingKind.Choice,
        Choices = [AutomaticCloud, "Microsoft 365 (global)", "US Gov (GCC High)", "US Gov (DoD)"],
        Hint = "Which Microsoft cloud the account is in: the US Government clouds have Teams hosts of their own. Automatic finds it from the account's domain, from what Microsoft publishes for every organisation (no sign-in needed), at each start, and keeps the last answer for a start that cannot look it up. Choose one to set it yourself.")]
    public string Cloud { get; set; } = AutomaticCloud;

    [Setting("Browser", Advanced = true, Order = 12, Kind = SettingKind.FilePath,
        Hint = "The Edge, Chrome, Chromium or Firefox to open for the web session's sign-in. Leave empty to find one automatically: Edge, Chrome or Chromium, and Firefox (through its WebDriver) where an organisation's policy keeps AnyFS out of those. AnyFS opens it with a profile of its own, never your everyday browser's.")]
    public string? BrowserPath { get; set; }

    [Setting("Conversations active in the last (days)", Advanced = true, Order = 13,
        Hint = "Chats and channel posts whose last message is older than this are left out, and a conversation is read back no further than this, its earlier folder too (a busy account has thousands). 0 shows everything, back to each conversation's start.")]
    public int WithinDays { get; set; }

    [Setting("Check for changes every (seconds)", Advanced = true, Order = 14,
        Hint = "How old a listing of something that changes (the teams and chats, a conversation's messages) may be before it is asked for again. 0 asks every time, and keeps no folder listings.")]
    public int RefreshSeconds { get; set; } = 60;

    [Setting(KeptListings.KeepSetting, Advanced = true, Order = 15,
        Hint = "Answer from what AnyFS last saw of each folder, so folders open at once; a listing older than 'Check for changes every' is asked for again behind the answer. With 'Count files on start', the count walks the teams, channels and chats first and keeps what it finds (below). Off: each folder is asked for as it is opened.")]
    public bool KeepListings { get; set; } = true;

    [Setting(KeptListings.WalkSetting, Advanced = true, Order = 16,
        Hint = "With folder listings kept and 'Count files on start': how long after one walk the next begins (the count's is the first). 0 walks for the count only. A walk lists the teams, channels and chats, but not each conversation's messages, which are read when the conversation is opened.")]
    public int WalkMinutes { get; set; } = 30;

    [Setting("Requests at a time", Advanced = true, Order = 17, Hint = "How many requests AnyFS has open with Teams at once.")]
    public int Concurrency { get; set; } = 8;

    internal TeamsSignIn EffectiveSignIn => TeamsSignIn.WebSession;

    /// <summary>The cloud chosen in the settings; null for Automatic (found from the account, <see cref="TeamsCloud.ResolveAsync"/>).</summary>
    internal TeamsCloud? ChosenCloud => (Cloud ?? "").Trim() switch
    {
        var c when c.Contains("DoD", StringComparison.OrdinalIgnoreCase) => TeamsCloud.UsGovDod,
        var c when c.Contains("GCC", StringComparison.OrdinalIgnoreCase) || c.Contains("Gov", StringComparison.OrdinalIgnoreCase) => TeamsCloud.UsGov,
        var c when c.Contains("global", StringComparison.OrdinalIgnoreCase) || c.Contains("365", StringComparison.OrdinalIgnoreCase) => TeamsCloud.Global,
        _ => null,
    };

    internal string? Browser => string.IsNullOrWhiteSpace(BrowserPath) ? null : BrowserPath.Trim();

    internal string AccountHint => Account.Trim();

    internal bool Keeps => KeepListings && RefreshSeconds > 0;

    internal TimeSpan Refresh => TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 0, 86_400));

    internal TimeSpan WalkEvery => TimeSpan.FromMinutes(Math.Clamp(WalkMinutes, 0, 7 * 24 * 60));

    internal TimeSpan? Within => WithinDays > 0 ? TimeSpan.FromDays(WithinDays) : null;
}
