using AnyFS.Sdk;

namespace AnyFS.Sources.Atlassian;

/// <summary>How a Jira or Confluence source signs in (spec 7.10).</summary>
internal enum AtlassianSignIn
{
    /// <summary>A personal access token (Jira 8.14+, Confluence 7.9+), sent as a bearer token.</summary>
    Token,

    /// <summary>A browser of AnyFS's own, on the server's own sign-in page (whatever single sign-on it has); AnyFS keeps the session.</summary>
    WebSession,

    /// <summary>A user name and password: the product's own login form, else Basic authentication where the server allows it.</summary>
    Password,
}

/// <summary>What the sign-in, the session and the REST client need from a Jira or Confluence source's settings.</summary>
internal interface IAtlassianSettings
{
    AtlassianSignIn EffectiveSignIn { get; }

    /// <summary>The address as given, tidied (<see cref="AtlassianAddress.Tidy"/>), or null while it is no http(s) address.</summary>
    Uri? Url { get; }

    string? Token { get; }

    string? User { get; }

    string? Secret { get; }

    string? Browser { get; }

    bool TrustAnyCertificate { get; }

    int Concurrency { get; }
}

/// <summary>The "Sign in with" choices, as both products' settings show them.</summary>
internal static class AtlassianSignInChoices
{
    public const string Token = "Personal access token";
    public const string WebSession = "Web session, in a browser";
    public const string Password = "User name and password";

    public const string Hint = "Personal access token: one you make in your profile (Profile, Personal Access Tokens; in Bitbucket, Manage account, HTTP access tokens), sent with every request; the server's own way for programs. Web session: AnyFS opens the server's own sign-in page in a browser of its own for you to sign in, single sign-on and all, and keeps that session; it renews it without a window while the server lets it, and shows the window again only when it does not. User name and password: the server's own login form with your account's name and password (an Active Directory account where the server signs people in through the directory).";

    public static AtlassianSignIn? From(string? choice) => (choice ?? "").Trim() switch
    {
        var c when c.Length == 0 || c.StartsWith("Personal access token", StringComparison.OrdinalIgnoreCase) || c.StartsWith("Token", StringComparison.OrdinalIgnoreCase) || c.StartsWith("Access token", StringComparison.OrdinalIgnoreCase) => AtlassianSignIn.Token,
        var c when c.StartsWith("Web session", StringComparison.OrdinalIgnoreCase) => AtlassianSignIn.WebSession,
        var c when c.StartsWith("User name", StringComparison.OrdinalIgnoreCase) || c.StartsWith("Username", StringComparison.OrdinalIgnoreCase) || c.StartsWith("Password", StringComparison.OrdinalIgnoreCase) => AtlassianSignIn.Password,
        _ => null,
    };

    /// <summary>The problems with the sign-in fields, as the plugins' Validate yields them.</summary>
    public static IEnumerable<ValidationMessage> Problems(string? choice, IAtlassianSettings s, string choiceProperty, string tokenProperty, string userProperty, string browserProperty)
    {
        if (From(choice) is null)
        {
            yield return new ValidationMessage($"Sign in with one of: {Token}, {WebSession}, {Password}.", choiceProperty);
        }

        switch (s.EffectiveSignIn)
        {
            case AtlassianSignIn.Token when s.Token is null:
                yield return new ValidationMessage("Give a personal access token: make one in your profile on the server (Personal Access Tokens).", tokenProperty);
                break;
            case AtlassianSignIn.Password when string.IsNullOrWhiteSpace(s.User):
                yield return new ValidationMessage("Give the user name to sign in as.", userProperty);
                break;
            case AtlassianSignIn.WebSession when s.Browser is { } browser && !File.Exists(browser):
                yield return new ValidationMessage("No browser at that path; leave it empty to find Edge or Chrome automatically.", browserProperty, IsError: false);
                break;
        }
    }
}
