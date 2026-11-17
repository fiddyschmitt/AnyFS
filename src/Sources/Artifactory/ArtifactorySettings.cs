using AnyFS.Sdk;

namespace AnyFS.Sources.Artifactory;

/// <summary>How an Artifactory source signs in (spec 7.13).</summary>
internal enum ArtifactorySignIn
{
    /// <summary>An identity token, an access token or an API key, sent with every request.</summary>
    Token,

    /// <summary>A browser of AnyFS's own, on the server's own sign-in page (whatever single sign-on it has); AnyFS keeps the session.</summary>
    WebSession,

    /// <summary>A user name and password, sent with every request (Basic).</summary>
    Password,

    /// <summary>Nothing: what the server lets anyone read.</summary>
    Anonymous,
}

public sealed class ArtifactorySettings
{
    public const string TokenChoice = "Access token";
    public const string WebSessionChoice = "Web session, in a browser";
    public const string PasswordChoice = "User name and password";
    public const string AnonymousChoice = "No sign-in";

    [Setting("Address", Required = true, Primary = true, Order = 0, Placeholder = "https://artifactory.example.com/artifactory/libs-release-local",
        Hint = "An Artifactory server, a repository on it or a folder in a repository: its address as the browser shows it. A server shows every repository you can see; a repository only itself; a folder what is below it. A page's address pasted here (the tree's, a file's) is cut back to what it names.")]
    public string Address { get; set; } = "";

    [Setting("Sign in with", Order = 1, Group = "Sign-in", Kind = SettingKind.Choice, Choices = [TokenChoice, WebSessionChoice, PasswordChoice, AnonymousChoice],
        Hint = "Access token: an identity token (Artifactory: your profile, Generate an Identity Token), an access token, or an API key. Web session: AnyFS opens the server's own sign-in page in a browser of its own for you to sign in, single sign-on and all, and keeps that session; it renews it without a window while the server lets it, and shows the window again only when it does not. User name and password: sent with every request, where the server takes them (one that signs people in through single sign-on alone does not). No sign-in: what the server lets anyone read.")]
    public string SignInMethod { get; set; } = TokenChoice;

    [Setting("Access token", Secret = true, Order = 2, Group = "Sign-in", Kind = SettingKind.Password, Hint = "An identity token, an access token or an API key.")]
    public string? AccessToken { get; set; }

    [Setting("Username", Order = 3, Group = "Sign-in", Placeholder = "the name you sign in to Artifactory with")]
    public string? Username { get; set; }

    [Setting("Password", Secret = true, Order = 4, Group = "Sign-in")]
    public string? Password { get; set; }

    [Setting("Local repositories", Order = 5, Hint = "With a server's address: the repositories that hold their own files (local and federated ones, build information, release bundles).")]
    public bool LocalRepositories { get; set; } = true;

    [Setting("Remote repositories", Order = 6,
        Hint = "With a server's address: the repositories that stand in for another server (a public registry, say), each showing what Artifactory has fetched from it so far. Nothing AnyFS reads makes Artifactory fetch more.")]
    public bool RemoteRepositories { get; set; } = true;

    [Setting("Virtual repositories", Order = 7,
        Hint = "With a server's address: the repositories that show several others as one. Off by default: their files are those of the local and remote repositories behind them, shown again.")]
    public bool VirtualRepositories { get; set; }

    [Setting("Browser", Advanced = true, Order = 8, Kind = SettingKind.FilePath,
        Hint = "The Edge, Chrome, Chromium or Firefox to open for the web session's sign-in. Leave empty to find one automatically. AnyFS opens it with a profile of its own, never your everyday browser's; where a policy keeps AnyFS from driving it, the sign-in goes through Selenium WebDriver (your default browser, then Firefox and the others).")]
    public string? BrowserPath { get; set; }

    [Setting("Trust any certificate", Advanced = true, Order = 9,
        Hint = "Accept the server's HTTPS certificate whatever it is (a server with a self-signed one). Off for a real server.")]
    public bool TrustAnyCertificate { get; set; }

    [Setting("Use Artifactory's answers for (seconds)", Advanced = true, Order = 10,
        Hint = "How long an answer from Artifactory (the repositories there are) is used again before it is asked for again, as a folder not kept yet is listed. It checks no kept folder for changes: the walks do that (below). 0 asks every time, and keeps no folder listings.")]
    public int RefreshSeconds { get; set; } = 60;

    [Setting(KeptListings.KeepSetting, Advanced = true, Order = 11,
        Hint = "Answer from what AnyFS last saw of each folder, so folders open at once and going through them again asks nothing of Artifactory; a change shows after the next walk (below). With 'Count files on start', the count walks the folders first and keeps what it finds. Off: each folder is asked for as it is opened.")]
    public bool KeepListings { get; set; } = true;

    [Setting(KeptListings.WalkSetting, Advanced = true, Order = 12,
        Hint = "With folder listings kept: how long after one walk the next begins. The walks are what checks kept folders for changes, so this is how long a new or changed file can take to show. 0 walks once, at the start. Without 'Count files on start' a walk goes only through the folders that have been opened; with it, through every repository. A walk asks each repository what was added or changed since the last one, and lists again only the folders that holds; a repository nothing happened in costs one request. A file deleted shows as gone after the walk that lists everything again, once a day.")]
    public int WalkMinutes { get; set; } = 30;

    [Setting("Repositories at a time", Advanced = true, Order = 13,
        Hint = "How many repositories a walk goes through at once. A server with the capacity to answer counts faster with more. However many are set, a server that asks AnyFS to wait (429) is waited for, and a search or an open while the walk runs goes first.")]
    public int RepositoriesAtATime { get; set; } = 4;

    [Setting("Requests at a time per repository", Advanced = true, Order = 14,
        Hint = "How many requests AnyFS has open with the server at once for each repository a walk is in, and for a source of one repository in all; times the repositories at a time, what it has open with the server at most, which any browsing meanwhile shares.")]
    public int RequestsPerRepository { get; set; } = 4;

    internal ArtifactorySignIn EffectiveSignIn => SignInFrom(SignInMethod) ?? ArtifactorySignIn.Token;

    internal static ArtifactorySignIn? SignInFrom(string? choice) => (choice ?? "").Trim() switch
    {
        var c when c.Length == 0 || c.StartsWith("Access token", StringComparison.OrdinalIgnoreCase) || c.Equals("Token", StringComparison.OrdinalIgnoreCase) => ArtifactorySignIn.Token,
        var c when c.StartsWith("Web session", StringComparison.OrdinalIgnoreCase) => ArtifactorySignIn.WebSession,
        var c when c.StartsWith("User name", StringComparison.OrdinalIgnoreCase) || c.StartsWith("Username", StringComparison.OrdinalIgnoreCase) || c.Equals("Password", StringComparison.OrdinalIgnoreCase) => ArtifactorySignIn.Password,
        var c when c.StartsWith("No sign-in", StringComparison.OrdinalIgnoreCase) || c.Equals("Anonymous", StringComparison.OrdinalIgnoreCase) || c.Equals("None", StringComparison.OrdinalIgnoreCase) => ArtifactorySignIn.Anonymous,
        _ => null,
    };

    /// <summary>What the sign-in goes as, for messages.</summary>
    internal string Who => EffectiveSignIn switch
    {
        ArtifactorySignIn.Password => string.IsNullOrWhiteSpace(Username) ? "the user name and password" : Username.Trim(),
        ArtifactorySignIn.WebSession => "the web session",
        ArtifactorySignIn.Anonymous => "anyone",
        _ => "the access token",
    };

    internal string? Token => string.IsNullOrWhiteSpace(AccessToken) ? null : AccessToken.Trim();

    internal string? User => string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();

    internal string? Browser => string.IsNullOrWhiteSpace(BrowserPath) ? null : BrowserPath.Trim();

    /// <summary>The address as given, tidied (<see cref="ArtifactoryAddress.Tidy"/>), or null while it is no http(s) address.</summary>
    internal Uri? Url => ArtifactoryAddress.Tidy(Address);

    internal int Repositories => Math.Clamp(RepositoriesAtATime, 1, 64);

    internal int PerRepository => Math.Clamp(RequestsPerRepository, 1, 64);

    /// <summary>What the API client has open with the server at most: the two settings' product.</summary>
    internal int Requests => Repositories * PerRepository;

    internal TimeSpan Refresh => TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 0, 86_400));

    /// <summary>Whether folder listings are kept: asked for, and a refresh interval to keep them by.</summary>
    internal bool Keeps => KeepListings && Refresh > TimeSpan.Zero;

    /// <summary>The settings that shape the tree, as one string: listings saved under another are not read back.</summary>
    internal string Shape => $"{LocalRepositories}|{RemoteRepositories}|{VirtualRepositories}";

    internal TimeSpan WalkEvery => TimeSpan.FromMinutes(Math.Clamp(WalkMinutes, 0, 7 * 24 * 60));
}
