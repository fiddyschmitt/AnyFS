using System.Net;
using AnyFS.Sdk;

namespace AnyFS.Sources.Common;

/// <summary>
/// The two Windows sign-ins a source on the person's own network offers (spec 7.3, 7.6): "Windows account, as yourself", which
/// goes as the person is signed in to Windows (a smartcard or Windows Hello sign-in too) with nothing to type, and "Windows
/// account", a user name and password the server knows. NTLM or Kerberos either way, by the handler's credentials.
/// </summary>
public static class WindowsAccount
{
    /// <summary>The choice's name in a source's "Sign in with".</summary>
    public const string AsYourself = "Windows account, as yourself";

    /// <summary>The choice's name in a source's "Sign in with".</summary>
    public const string WithPassword = "Windows account";

    /// <summary>Whether the sign-in goes as the account AnyFS runs as, not a user name and password: chosen outright, or no user name given.</summary>
    public static bool UsesCurrentLogon(bool asYourself, string? username) => asYourself || string.IsNullOrWhiteSpace(username);

    /// <summary>The account a Windows sign-in goes as, for messages: "CONTOSO\name (your Windows sign-in)", or the user name given.</summary>
    public static string Name(bool asYourself, string? username)
        => UsesCurrentLogon(asYourself, username) ? $@"{Environment.UserDomainName}\{Environment.UserName} (your Windows sign-in)" : username!.Trim();

    /// <summary>
    /// The credentials for the sign-in: DOMAIN\name is split, name@domain is passed as a user principal name, none (or
    /// as yourself) means the account AnyFS runs as.
    /// </summary>
    public static ICredentials Credentials(bool asYourself, string? username, string? password)
    {
        if (UsesCurrentLogon(asYourself, username))
        {
            return CredentialCache.DefaultNetworkCredentials;
        }

        var user = username!.Trim();
        var slash = user.IndexOf('\\');
        return slash > 0
            ? new NetworkCredential(user[(slash + 1)..], password ?? "", user[..slash])
            : new NetworkCredential(user, password ?? "");
    }

    /// <summary>
    /// What is wrong with a Windows sign-in's settings, for a plugin's Validate: signing in as yourself off Windows, a
    /// password without a user name, a user name with spaces. The user name and password are not looked at for as yourself
    /// (the form hides them, and one kept from an earlier choice is no error).
    /// </summary>
    /// <param name="signInProperty">The settings property that holds the choice.</param>
    /// <param name="usernameProperty">The settings property that holds the user name.</param>
    public static IEnumerable<ValidationMessage> Problems(bool asYourself, string? username, string? password, string signInProperty, string usernameProperty)
    {
        if (asYourself)
        {
            if (!OperatingSystem.IsWindows())
            {
                yield return new ValidationMessage($"Signing in as yourself works on Windows only: choose {WithPassword} and give a user name and password.", signInProperty);
            }
        }
        else if (string.IsNullOrWhiteSpace(username))
        {
            if (!string.IsNullOrEmpty(password))
            {
                yield return new ValidationMessage("A password needs a user name.", usernameProperty);
            }

            if (!OperatingSystem.IsWindows())
            {
                yield return new ValidationMessage("Give a user name and password: signing in as the current account works on Windows only.", usernameProperty);
            }
        }
        else if (username.Trim().Any(char.IsWhiteSpace))
        {
            yield return new ValidationMessage(@"The user name is DOMAIN\name or name@domain, with no spaces.", usernameProperty);
        }
    }
}
