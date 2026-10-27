using System.Net;
using AnyFS.Sdk;

namespace AnyFS.Sources.GitLab;

/// <summary>How a GitLab source signs in (spec 7.8).</summary>
internal enum GitLabSignIn
{
    /// <summary>A personal, project or group access token, sent as GitLab's API asks (PRIVATE-TOKEN).</summary>
    AccessToken,

    /// <summary>A browser of AnyFS's own, on the server's own sign-in page (whatever single sign-on it has); AnyFS keeps the session.</summary>
    WebSession,

    /// <summary>GitLab's Kerberos sign-in with the person's Windows sign-in: no password, no browser.</summary>
    WindowsCurrent,

    /// <summary>A Windows account's name and password: GitLab's Kerberos sign-in, else its LDAP sign-in, else its own.</summary>
    Windows,
}

public sealed class GitLabSettings
{
    public const string AccessTokenChoice = "Access token";
    public const string WebSession = "Web session, in a browser";
    public const string WindowsCurrent = WindowsAccount.AsYourself;
    public const string Windows = WindowsAccount.WithPassword;

    [Setting("Address", Required = true, Primary = true, Order = 0, Placeholder = "https://gitlab.example.com/group/project",
        Hint = "A GitLab server, a group on it or a project: its address as the browser shows it. A server shows the groups and projects you are a member of; a group its subgroups and projects; a project only itself. A page's address pasted here is cut back to its project's.")]
    public string Address { get; set; } = "";

    [Setting("Sign in with", Order = 1, Group = "Sign-in", Kind = SettingKind.Choice, Choices = [AccessTokenChoice],
        Hint = "Access token: a personal, project or group access token with the read_api and read_repository scopes (GitLab: Preferences, Access tokens).")]
    public string SignInMethod { get; set; } = AccessTokenChoice;

    [Setting("Access token", Secret = true, Order = 2, Group = "Sign-in", Kind = SettingKind.Password, Placeholder = "glpat-...")]
    public string? AccessToken { get; set; }

    [Setting("Username", Order = 3, Group = "Sign-in", Placeholder = @"name@domain, or CONTOSO\name",
        Hint = "The account to sign in as. For Kerberos from a computer outside the domain, name@domain finds the domain's servers more surely than DOMAIN\\name.")]
    public string? Username { get; set; }

    [Setting("Password", Secret = true, Order = 4, Group = "Sign-in")]
    public string? Password { get; set; }

    [Setting("Branches and tags", Order = 5,
        Hint = "In each project, a folder of every branch and one of every tag, each the repository as it stands there, beside files (the default branch).")]
    public bool BranchesAndTags { get; set; } = true;

    [Setting("Wiki", Order = 6,
        Hint = "In each project with a wiki (and each group with one, GitLab Premium), its pages as files of their own markup (Markdown, AsciiDoc...), the page's address on the first line, and the files they embed, where the sign-in is a browser session or a Windows account (GitLab serves those to no token).")]
    public bool Wiki { get; set; } = true;

    [Setting("Browser", Advanced = true, Order = 10, Kind = SettingKind.FilePath,
        Hint = "The Edge, Chrome or Chromium to open for the web session's sign-in. Leave empty to find one automatically. AnyFS opens it with a profile of its own, never your everyday browser's; where a policy keeps AnyFS from driving it, the sign-in goes through Selenium WebDriver (your default browser, then Firefox and the others).")]
    public string? BrowserPath { get; set; }

    [Setting("Trust any certificate", Advanced = true, Order = 11,
        Hint = "Accept the server's HTTPS certificate whatever it is (a server with a self-signed one). Off for a real server.")]
    public bool TrustAnyCertificate { get; set; }

    [Setting("Check for changes every (seconds)", Advanced = true, Order = 14,
        Hint = "How old a listing of something that changes (branches, issues, pipelines, the wiki) may be before it is asked for again. The repository as it stands at a commit never changes, and is kept.")]
    public int RefreshSeconds { get; set; } = 60;

    [Setting("Requests at a time", Advanced = true, Order = 15, Hint = "How many requests AnyFS has open with the server at once.")]
    public int Concurrency { get; set; } = 8;

    internal GitLabSignIn EffectiveSignIn => SignInFrom(SignInMethod) ?? GitLabSignIn.AccessToken;

    internal static GitLabSignIn? SignInFrom(string? choice) => (choice ?? "").Trim() switch
    {
        var c when c.Length == 0 || c.StartsWith("Access token", StringComparison.OrdinalIgnoreCase) || c.Equals("Token", StringComparison.OrdinalIgnoreCase) => GitLabSignIn.AccessToken,
        var c when c.StartsWith("Web session", StringComparison.OrdinalIgnoreCase) => GitLabSignIn.WebSession,
        var c when c.Equals(WindowsCurrent, StringComparison.OrdinalIgnoreCase) => GitLabSignIn.WindowsCurrent,
        var c when c.Equals(Windows, StringComparison.OrdinalIgnoreCase) => GitLabSignIn.Windows,
        _ => null,
    };

    internal bool IsWindowsCurrent => EffectiveSignIn == GitLabSignIn.WindowsCurrent;

    /// <summary>The account the sign-in goes as, for messages.</summary>
    internal string Who => EffectiveSignIn switch
    {
        GitLabSignIn.Windows => WindowsAccount.Name(asYourself: false, Username),
        GitLabSignIn.WindowsCurrent => WindowsAccount.Name(asYourself: true, null),
        GitLabSignIn.WebSession => "the web session",
        _ => "the access token",
    };

    /// <summary>The Windows credentials for the Kerberos sign-in: the user name and password, or as yourself.</summary>
    internal ICredentials Credentials() => WindowsAccount.Credentials(IsWindowsCurrent, EffectiveSignIn == GitLabSignIn.Windows ? Username : null, Password);

    /// <summary>The user name and password for a sign-in form (LDAP's, GitLab's own): the name without a domain; null when there is none to give.</summary>
    internal NetworkCredential? FormCredentials()
    {
        if (EffectiveSignIn != GitLabSignIn.Windows || string.IsNullOrWhiteSpace(Username))
        {
            return null;
        }

        var user = Username.Trim();
        var slash = user.IndexOf('\\');
        if (slash >= 0)
        {
            user = user[(slash + 1)..];
        }
        else if (user.IndexOf('@') is var at and > 0)
        {
            user = user[..at];
        }

        return new NetworkCredential(user, Password ?? "");
    }

    internal string? Browser => string.IsNullOrWhiteSpace(BrowserPath) ? null : BrowserPath.Trim();

    internal string? Token => string.IsNullOrWhiteSpace(AccessToken) ? null : AccessToken.Trim();

    internal TimeSpan Refresh => TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 0, 86_400));

    /// <summary>The address as given, tidied (<see cref="GitLabAddress.Tidy"/>), or null while it is no http(s) address.</summary>
    internal Uri? Url => GitLabAddress.Tidy(Address);
}
