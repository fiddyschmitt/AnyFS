using AnyFS.Sdk;

namespace AnyFS.Sources.Atlassian.Jira;

public sealed class JiraSettings : IAtlassianSettings
{
    [Setting("Address", Required = true, Primary = true, Order = 0, Placeholder = "https://jira.example.com/projects/ABC",
        Hint = "A Jira server, a project on it, a saved filter or a board: its address as the browser shows it. A server shows every project you can browse; a project its issues; a filter or a board the issues it finds. An issue's address is cut back to its project's.")]
    public string Address { get; set; } = "";

    [Setting("Sign in with", Order = 1, Group = "Sign-in", Kind = SettingKind.Choice,
        Choices = [AtlassianSignInChoices.Token, AtlassianSignInChoices.WebSession, AtlassianSignInChoices.Password],
        Hint = AtlassianSignInChoices.Hint)]
    public string SignInMethod { get; set; } = AtlassianSignInChoices.Token;

    [Setting("Personal access token", Secret = true, Order = 2, Group = "Sign-in", Kind = SettingKind.Password)]
    public string? AccessToken { get; set; }

    [Setting("Username", Order = 3, Group = "Sign-in", Placeholder = "the name you sign in to Jira with")]
    public string? Username { get; set; }

    [Setting("Password", Secret = true, Order = 4, Group = "Sign-in")]
    public string? Password { get; set; }

    [Setting("Comments", Order = 5, Hint = "Each issue's comments, oldest first, at the end of its issue.md.")]
    public bool Comments { get; set; } = true;

    [Setting("Attachments", Order = 6, Hint = "Each issue's attached files, in an attachments folder beside its issue.md; the description's images and links point at them.")]
    public bool Attachments { get; set; } = true;

    [Setting("Browser", Advanced = true, Order = 7, Kind = SettingKind.FilePath,
        Hint = "The Edge, Chrome or Chromium to open for the web session's sign-in. Leave empty to find one automatically. AnyFS opens it with a profile of its own, never your everyday browser's; where a policy keeps AnyFS from driving it, the sign-in goes through Selenium WebDriver (your default browser, then Firefox and the others).")]
    public string? BrowserPath { get; set; }

    [Setting("Trust any certificate", Advanced = true, Order = 8,
        Hint = "Accept the server's HTTPS certificate whatever it is (a server with a self-signed one). Off for a real server.")]
    public bool TrustAnyCertificate { get; set; }

    [Setting("Open issues only", Advanced = true, Order = 9, Hint = "Leave out issues whose status is done (a busy project has thousands).")]
    public bool OpenOnly { get; set; }

    [Setting("Use Jira's answers for (seconds)", Advanced = true, Order = 10,
        Hint = "How long an answer from Jira is used again before it is asked for again, as a file is opened or a folder not kept yet is listed. It checks no kept folder for changes: the walks do that (below). 0 asks every time, and keeps no folder listings.")]
    public int RefreshSeconds { get; set; } = 60;

    [Setting(KeptListings.KeepSetting, Advanced = true, Order = 11,
        Hint = "Answer from what AnyFS last saw of each folder, so folders open at once and going through them again asks nothing of Jira; a change shows after the next walk (below). A folder that could not be listed shows empty until then. With 'Count files on start', the count walks the folders first and keeps what it finds. Off: each folder is asked for as it is opened.")]
    public bool KeepListings { get; set; } = true;

    [Setting(KeptListings.WalkSetting, Advanced = true, Order = 12,
        Hint = "With folder listings kept: how long after one walk the next begins. The walks are what checks kept folders for changes, so this is how long a change can take to show. 0 walks once, at the start. Without 'Count files on start' a walk goes only through the folders that have been opened; with it, through every project. A project whose newest change and issue count have not moved since the last walk is kept as it was, and one that moved reads only the issues changed since, so a walk over a quiet server asks a request per project; once a day everything is read again. The walk takes half the requests, so browsing meanwhile has the rest.")]
    public int WalkMinutes { get; set; } = 30;

    [Setting("Requests at a time", Advanced = true, Order = 13, Hint = "How many requests AnyFS has open with the server at once.")]
    public int Concurrency { get; set; } = 8;

    internal AtlassianSignIn EffectiveSignIn => AtlassianSignInChoices.From(SignInMethod) ?? AtlassianSignIn.Token;

    AtlassianSignIn IAtlassianSettings.EffectiveSignIn => EffectiveSignIn;

    /// <summary>The address as given, tidied (<see cref="AtlassianAddress.Tidy"/>), or null while it is no http(s) address.</summary>
    internal Uri? Url => AtlassianAddress.Tidy(Address);

    Uri? IAtlassianSettings.Url => Url;

    string? IAtlassianSettings.Token => string.IsNullOrWhiteSpace(AccessToken) ? null : AccessToken.Trim();

    string? IAtlassianSettings.User => EffectiveSignIn == AtlassianSignIn.Password && !string.IsNullOrWhiteSpace(Username) ? Username.Trim() : null;

    string? IAtlassianSettings.Secret => Password;

    string? IAtlassianSettings.Browser => string.IsNullOrWhiteSpace(BrowserPath) ? null : BrowserPath.Trim();

    internal TimeSpan Refresh => TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 0, 86_400));

    /// <summary>Whether folder listings are kept: asked for, and a refresh interval to keep them by.</summary>
    internal bool Keeps => KeepListings && Refresh > TimeSpan.Zero;

    /// <summary>The settings that shape the tree, as one string: listings saved under another are not read back.</summary>
    internal string Shape => $"{Comments}|{Attachments}|{OpenOnly}";

    internal TimeSpan WalkEvery => TimeSpan.FromMinutes(Math.Clamp(WalkMinutes, 0, 7 * 24 * 60));
}
