namespace AnyFS.Sources.SharePoint;

/// <summary>
/// The web session's sign-in to a SharePoint Online site (spec 7.3.2): the site opened in a browser of AnyFS's own, one
/// profile per site host (<see cref="SharePointPlugin.ProfileDir"/>), and the person counted as signed in once the site's
/// FedAuth cookie is set with a page of the site open. A site that opens through a Microsoft Defender for Cloud Apps session
/// proxy cannot be signed in to this way.
/// </summary>
internal sealed class SharePointSignInTarget(Uri site, string? profileDir = null) : WebSignInTarget(site, profileDir ?? SharePointPlugin.ProfileDir(site.Host))
{
    public override string LogPrefix => "sharepoint";

    public override string WebDriverVariable => "ANYFS_SHAREPOINT_WEBDRIVER";

    public override string OtherWayIn => ", or use an app sign-in (Sign in with)";

    public override ValueTask<SignInCheck> CheckAsync(IReadOnlyList<Uri> pages, IReadOnlyList<SessionCookie> cookies, CancellationToken ct)
    {
        if (cookies.Any(c => c.Name == "FedAuth") && pages.Any(p => p.Host.Equals(Page.Host, StringComparison.OrdinalIgnoreCase)))
        {
            return ValueTask.FromResult(SignInCheck.SignedIn);
        }

        if (pages.FirstOrDefault(IsSessionProxy) is { } proxied)
        {
            return ValueTask.FromResult(SignInCheck.Failed($"{Page.Host} opens through a session proxy ({proxied.Host}, Microsoft Defender for Cloud Apps), which the web session cannot go through; an app sign-in can"));
        }

        return ValueTask.FromResult(SignInCheck.NotYet);
    }

    /// <summary>A Microsoft Defender for Cloud Apps session proxy serves the site under its own suffix (contoso.sharepoint.com.mcas.ms).</summary>
    private bool IsSessionProxy(Uri page)
        => page.Host.StartsWith(Page.Host + ".", StringComparison.OrdinalIgnoreCase) && page.Host.Contains(".mcas", StringComparison.OrdinalIgnoreCase);
}
