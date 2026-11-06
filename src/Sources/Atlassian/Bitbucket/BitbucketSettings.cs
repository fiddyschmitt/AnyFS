using AnyFS.Sdk;

namespace AnyFS.Sources.Atlassian.Bitbucket;

public sealed class BitbucketSettings : IAtlassianSettings
{
    [Setting("Address", Required = true, Primary = true, Order = 0, Placeholder = "https://bitbucket.example.com/projects/ABC/repos/docs",
        Hint = "A Bitbucket server (Data Center), a project on it or a repository: its address as the browser shows it. A server shows every project you can see; a project its repositories; a repository only itself. A page's address pasted here is cut back to what it belongs to; a clone address (…/scm/abc/docs.git) names its repository.")]
    public string Address { get; set; } = "";

    [Setting("Sign in with", Order = 1, Group = "Sign-in", Kind = SettingKind.Choice,
        Choices = [AtlassianSignInChoices.Token, AtlassianSignInChoices.WebSession, AtlassianSignInChoices.Password],
        Hint = AtlassianSignInChoices.Hint)]
    public string SignInMethod { get; set; } = AtlassianSignInChoices.Token;

    [Setting("Personal access token", Secret = true, Order = 2, Group = "Sign-in", Kind = SettingKind.Password, Hint = "An HTTP access token made under Manage account, HTTP access tokens, with Repository read permission.")]
    public string? AccessToken { get; set; }

    [Setting("Username", Order = 3, Group = "Sign-in", Placeholder = "the name you sign in to Bitbucket with")]
    public string? Username { get; set; }

    [Setting("Password", Secret = true, Order = 4, Group = "Sign-in")]
    public string? Password { get; set; }

    [Setting("Branches and tags", Order = 5,
        Hint = "In each repository, a folder of every branch and one of every tag, each the repository as it stands there, beside files (the default branch).")]
    public bool BranchesAndTags { get; set; } = true;

    [Setting("Pull requests", Order = 6,
        Hint = "In each repository, a folder per pull request: its description and comments as Markdown, and its changes as a diff.")]
    public bool PullRequests { get; set; } = true;

    [Setting("Browser", Advanced = true, Order = 7, Kind = SettingKind.FilePath,
        Hint = "The Edge, Chrome, Chromium or Firefox to open for the web session's sign-in. Leave empty to find one automatically. AnyFS opens it with a profile of its own, never your everyday browser's; where a policy keeps AnyFS from driving it, the sign-in goes through Selenium WebDriver (your default browser, then Firefox and the others).")]
    public string? BrowserPath { get; set; }

    [Setting("Trust any certificate", Advanced = true, Order = 8,
        Hint = "Accept the server's HTTPS certificate whatever it is (a server with a self-signed one). Off for a real server.")]
    public bool TrustAnyCertificate { get; set; }

    [Setting("Open pull requests only", Advanced = true, Order = 9, Hint = "Leave out pull requests that were merged or declined (a busy repository has thousands).")]
    public bool OpenOnly { get; set; }

    [Setting("Use Bitbucket's answers for (seconds)", Advanced = true, Order = 10,
        Hint = "How long an answer from Bitbucket is used again before it is asked for again, as a file is opened or a folder not kept yet is listed. It checks no kept folder for changes: the walks do that (below). 0 asks every time, and keeps no folder listings.")]
    public int RefreshSeconds { get; set; } = 60;

    [Setting(KeptListings.KeepSetting, Advanced = true, Order = 11,
        Hint = "Answer from what AnyFS last saw of each folder, so folders open at once and going through them again asks nothing of Bitbucket; a change shows after the next walk (below). A folder that could not be listed shows empty until then. With 'Count files on start', the count walks the folders first and keeps what it finds. Off: each folder is asked for as it is opened.")]
    public bool KeepListings { get; set; } = true;

    [Setting(KeptListings.WalkSetting, Advanced = true, Order = 12,
        Hint = "With folder listings kept: how long after one walk the next begins. The walks are what checks kept folders for changes, so this is how long a change can take to show. 0 walks once, at the start. Without 'Count files on start' a walk goes only through the folders that have been opened; with it, through every repository. A branch or tag whose commit has not moved is kept as it was, with every folder below it, so a walk over a quiet repository asks a request for its branches, one for its tags and one for its pull requests.")]
    public int WalkMinutes { get; set; } = 30;

    [Setting("Repositories at a time", Advanced = true, Order = 13,
        Hint = "How many repositories a walk goes through at once, each one's folders listed together. A server with thousands of repositories and the capacity to answer counts faster with more. However many are set, a server that asks AnyFS to wait (429) is waited for.")]
    public int RepositoriesAtATime { get; set; } = 8;

    [Setting("Requests at a time per repository", Advanced = true, Order = 14,
        Hint = "How many of one repository's folders a walk lists at once, and how many requests one repository's own listing and opens have open with the server at once. Repositories at a time times this is what AnyFS has open with the server at most.")]
    public int RequestsPerRepository { get; set; } = 8;

    internal AtlassianSignIn EffectiveSignIn => AtlassianSignInChoices.From(SignInMethod) ?? AtlassianSignIn.Token;

    AtlassianSignIn IAtlassianSettings.EffectiveSignIn => EffectiveSignIn;

    /// <summary>The address as given, tidied (<see cref="AtlassianAddress.Tidy"/>), or null while it is no http(s) address.</summary>
    internal Uri? Url => AtlassianAddress.Tidy(Address);

    Uri? IAtlassianSettings.Url => Url;

    string? IAtlassianSettings.Token => string.IsNullOrWhiteSpace(AccessToken) ? null : AccessToken.Trim();

    string? IAtlassianSettings.User => EffectiveSignIn == AtlassianSignIn.Password && !string.IsNullOrWhiteSpace(Username) ? Username.Trim() : null;

    string? IAtlassianSettings.Secret => Password;

    string? IAtlassianSettings.Browser => string.IsNullOrWhiteSpace(BrowserPath) ? null : BrowserPath.Trim();

    /// <summary>What the API client has open with the server at most: the two settings' product.</summary>
    int IAtlassianSettings.Concurrency => Requests;

    internal int Requests => Math.Clamp(RepositoriesAtATime, 1, 64) * Math.Clamp(RequestsPerRepository, 1, 64);

    internal TimeSpan Refresh => TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 0, 86_400));

    /// <summary>Whether folder listings are kept: asked for, and a refresh interval to keep them by.</summary>
    internal bool Keeps => KeepListings && Refresh > TimeSpan.Zero;

    /// <summary>The settings that shape the tree, as one string: listings saved under another are not read back.</summary>
    internal string Shape => $"{BranchesAndTags}|{PullRequests}|{OpenOnly}";

    internal TimeSpan WalkEvery => TimeSpan.FromMinutes(Math.Clamp(WalkMinutes, 0, 7 * 24 * 60));
}
