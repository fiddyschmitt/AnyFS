using AnyFS.Sdk;

namespace AnyFS.Sources.Atlassian.Confluence;

public sealed class ConfluenceSettings : IAtlassianSettings
{
    [Setting("Address", Required = true, Primary = true, Order = 0, Placeholder = "https://confluence.example.com/display/KEY",
        Hint = "A Confluence server, a space on it or a page: its address as the browser shows it. A server shows every space you can read; a space its pages; a page itself and the pages below it.")]
    public string Address { get; set; } = "";

    [Setting("Sign in with", Order = 1, Group = "Sign-in", Kind = SettingKind.Choice,
        Choices = [AtlassianSignInChoices.Token, AtlassianSignInChoices.WebSession, AtlassianSignInChoices.Password],
        Hint = AtlassianSignInChoices.Hint)]
    public string SignInMethod { get; set; } = AtlassianSignInChoices.Token;

    [Setting("Personal access token", Secret = true, Order = 2, Group = "Sign-in", Kind = SettingKind.Password)]
    public string? AccessToken { get; set; }

    [Setting("Username", Order = 3, Group = "Sign-in", Placeholder = "the name you sign in to Confluence with")]
    public string? Username { get; set; }

    [Setting("Password", Secret = true, Order = 4, Group = "Sign-in")]
    public string? Password { get; set; }

    [Setting("Comments", Order = 5, Hint = "Each page's comments, oldest first, at the end of its Markdown (an inline comment with the text it is on).")]
    public bool Comments { get; set; } = true;

    [Setting("Attachments", Order = 6, Hint = "Each page's attached files, in the page's folder beside its Markdown; the page's images and links point at them.")]
    public bool Attachments { get; set; } = true;

    [Setting("Blog posts", Order = 7, Hint = "In each space with blog posts, a Blog folder of them, each named by the day it was posted.")]
    public bool BlogPosts { get; set; } = true;

    [Setting("Personal spaces", Order = 8, Hint = "A Personal spaces folder of everyone's personal spaces you can read (a big site has thousands).")]
    public bool PersonalSpaces { get; set; }

    [Setting("Archived spaces", Order = 9, Hint = "An Archived spaces folder of the archived spaces you can read.")]
    public bool ArchivedSpaces { get; set; }

    [Setting("Browser", Advanced = true, Order = 10, Kind = SettingKind.FilePath,
        Hint = "The Edge, Chrome or Chromium to open for the web session's sign-in. Leave empty to find one automatically. AnyFS opens it with a profile of its own, never your everyday browser's; where a policy keeps AnyFS from driving it, the sign-in goes through Selenium WebDriver (your default browser, then Firefox and the others).")]
    public string? BrowserPath { get; set; }

    [Setting("Trust any certificate", Advanced = true, Order = 11,
        Hint = "Accept the server's HTTPS certificate whatever it is (a server with a self-signed one). Off for a real server.")]
    public bool TrustAnyCertificate { get; set; }

    [Setting("Check for changes every (seconds)", Advanced = true, Order = 12,
        Hint = "How old a folder's listing may be before it is asked for again. 0 asks every time, and keeps no folder listings.")]
    public int RefreshSeconds { get; set; } = 60;

    [Setting(KeptListings.KeepSetting, Advanced = true, Order = 13,
        Hint = "Answer from what AnyFS last saw of each folder, so folders open at once; a listing older than 'Check for changes every' is asked for again behind the answer, so a change shows the next time the folder is opened. With 'Count files on start', the count walks the folders first and keeps what it finds (below). A folder that could not be listed shows empty until the next walk. Off: each folder is asked for as it is opened.")]
    public bool KeepListings { get; set; } = true;

    [Setting(KeptListings.WalkSetting, Advanced = true, Order = 14,
        Hint = "With folder listings kept and 'Count files on start': how long after one walk the next begins (the count's is the first). 0 walks for the count only. A walk reads each space whole, its pages, attachments and comments in bulk; a space whose newest change and content count have not moved since the last walk is kept as it was, so a walk over a quiet server asks a request per space; once a day everything is read again. The walk takes half the requests, so browsing meanwhile has the rest.")]
    public int WalkMinutes { get; set; } = 30;

    [Setting("Requests at a time", Advanced = true, Order = 15, Hint = "How many requests AnyFS has open with the server at once.")]
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
    internal string Shape => $"{Comments}|{Attachments}|{BlogPosts}|{PersonalSpaces}|{ArchivedSpaces}";

    internal TimeSpan WalkEvery => TimeSpan.FromMinutes(Math.Clamp(WalkMinutes, 0, 7 * 24 * 60));

    /// <summary>
    /// How long a folder whose listing failed answers so at once: until the next walk lists it again, whatever the failure;
    /// where no walk comes ("Count files on start" off, or walks turned off), as long as the walks' usual interval.
    /// </summary>
    internal TimeSpan FailureKeptFor => WalkEvery > TimeSpan.Zero ? WalkEvery : TimeSpan.FromMinutes(30);
}
