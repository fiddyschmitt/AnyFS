using System.Net;
using System.Text.RegularExpressions;

namespace AnyFS.Sources.GitLab;

/// <summary>
/// GitLab's sign-in page as the Windows sign-ins read it (spike B): its CSRF token (the page's <c>csrf-token</c> meta tag,
/// which every form posts as <c>authenticity_token</c>) and the ways in it offers: the Kerberos button
/// (<c>/users/auth/kerberos</c>), an LDAP server's form (<c>/users/auth/ldapmain/callback</c>, one per server) and
/// GitLab's own password form (<c>/users/sign_in</c>).
/// </summary>
internal sealed partial record GitLabSignInPage(string? Csrf, bool Kerberos, IReadOnlyList<string> LdapForms, bool PasswordForm)
{
    [GeneratedRegex(@"<meta\s+name=""csrf-token""\s+content=""(?<token>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CsrfMeta();

    [GeneratedRegex(@"<form\b[^>]*\baction=""(?<action>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FormAction();

    public static GitLabSignInPage Parse(string html)
    {
        var csrf = CsrfMeta().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups["token"].Value) : null;
        var actions = FormAction().Matches(html).Select(a => WebUtility.HtmlDecode(a.Groups["action"].Value)).Select(PathOf).ToList();
        return new GitLabSignInPage(
            csrf,
            actions.Any(a => a.EndsWith("/users/auth/kerberos", StringComparison.Ordinal)),
            actions.Where(a => a.Contains("/users/auth/ldap", StringComparison.Ordinal) && a.EndsWith("/callback", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList(),
            actions.Any(a => a.EndsWith("/users/sign_in", StringComparison.Ordinal)));
    }

    /// <summary>A form's action's path (an action may be absolute, with the server's configured address).</summary>
    private static string PathOf(string action) => Uri.TryCreate(action, UriKind.Absolute, out var url) ? url.AbsolutePath : action.Split('?')[0];

    /// <summary>The ways in, for messages: "Kerberos, LDAP and a password".</summary>
    public string Offers()
    {
        var ways = new List<string>();
        if (Kerberos)
        {
            ways.Add("Kerberos");
        }

        if (LdapForms.Count > 0)
        {
            ways.Add("LDAP");
        }

        if (PasswordForm)
        {
            ways.Add("a GitLab password");
        }

        return ways.Count == 0 ? "no sign-in AnyFS knows (its own page only: a web session)" : string.Join(", ", ways);
    }
}
