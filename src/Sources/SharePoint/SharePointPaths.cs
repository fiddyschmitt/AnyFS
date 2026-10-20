using AnyFS.Sdk;

namespace AnyFS.Sources.SharePoint;

/// <summary>A site's address split the way Microsoft Graph wants it: host and server-relative path.</summary>
/// <param name="Host">contoso.sharepoint.com</param>
/// <param name="ServerRelativePath">"/sites/Team", or "" for the root site</param>
/// <param name="Scheme">https, or http for an on-premises lab.</param>
public sealed record SiteAddress(string Host, string ServerRelativePath, string Scheme = "https")
{
    public string Url => $"{Scheme}://{Host}{ServerRelativePath}";

    /// <summary>The Graph address of the site: <c>/sites/{host}:{path}</c>, or <c>/sites/{host}</c> for the root site.</summary>
    public string GraphPath => ServerRelativePath.Length == 0 ? $"sites/{Host}" : $"sites/{Host}:{EscapePath(ServerRelativePath)}";

    /// <summary>The root site of its host (the address has no path).</summary>
    public bool IsRoot => ServerRelativePath.Length == 0;

    /// <summary>
    /// Where SharePoint Online keeps a site collection: its root, or a name under a managed path (/sites/x, /teams/x, and
    /// the older /portals/x and /personal/x); anything else is a subsite there. SharePoint Server may have other managed
    /// paths, so its stores ask the server instead.
    /// </summary>
    public bool IsSiteCollectionAddress
    {
        get
        {
            var segments = ServerRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length == 0 || (segments.Length == 2 && SiteCollectionPaths.Contains(segments[0]));
        }
    }

    private static readonly HashSet<string> SiteCollectionPaths = new(StringComparer.OrdinalIgnoreCase) { "sites", "teams", "portals", "personal" };

    /// <summary>What can follow a site in an address and is never a subsite: its lists, its well-known libraries, a library's views.</summary>
    private static readonly HashSet<string> NotSites = new(StringComparer.OrdinalIgnoreCase) { "Lists", "Shared Documents", "SitePages", "Pages", "SiteAssets", "Style Library", "FormServerTemplates", "Forms" };

    /// <summary>
    /// Accepts the forms people paste: with or without a scheme, with a trailing slash, with a page or a
    /// library path after the site (<c>/sites/Team/Shared%20Documents/Forms/AllItems.aspx</c> is cut back
    /// to <c>/sites/Team</c>), and a subsite's address as it is (<c>/sites/Team/Plans</c>). What is cut is
    /// a page (<c>.aspx</c>), SharePoint's own paths (<c>_layouts</c>, <c>_api</c>), a list, a well-known
    /// library (<see cref="NotSites"/>) or a library before its <c>Forms</c>, and everything after it; a library
    /// of another name, or a folder in one, cannot be told from a subsite without asking, so connecting steps
    /// back from it to the site that answers (<see cref="Candidates"/>). Paths other than <c>/sites/x</c> and
    /// <c>/teams/x</c> are kept as well, for other managed paths.
    /// </summary>
    public static bool TryParse(string? text, out SiteAddress address, out string reason)
    {
        address = null!;
        reason = "";
        var t = (text ?? "").Trim();
        if (t.Length == 0)
        {
            reason = "Give the site's address, like https://contoso.sharepoint.com/sites/Team.";
            return false;
        }

        if (!t.Contains("://", StringComparison.Ordinal))
        {
            t = "https://" + t;
        }

        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            reason = "The site's address is a URL, like https://contoso.sharepoint.com/sites/Team.";
            return false;
        }

        if (uri.Host.Length == 0 || uri.Host.Contains(' '))
        {
            reason = "The site's address needs a host name.";
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        var floor = Floor(segments);
        var cut = segments.FindIndex(floor, s => s.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) || s.StartsWith('_') || NotSites.Contains(s));
        if (cut >= 0)
        {
            // a library's view (<library>/Forms/<view>.aspx) goes with its library
            if (segments[cut].Equals("Forms", StringComparison.OrdinalIgnoreCase) && cut > floor)
            {
                cut--;
            }

            segments.RemoveRange(cut, segments.Count - cut);
        }

        var path = segments.Count == 0 ? "" : "/" + string.Join('/', segments);
        var host = uri.IsDefaultPort ? uri.Host.ToLowerInvariant() : $"{uri.Host.ToLowerInvariant()}:{uri.Port}";
        address = new SiteAddress(host, path, uri.Scheme.ToLowerInvariant());
        return true;
    }

    /// <summary>
    /// The address, then each shorter one down to the site it must at least be in: <c>/sites/x</c> (or <c>/teams/x</c>),
    /// else the first name below the root, else the root. Connecting tries them in turn, since SharePoint answers a
    /// library's or a folder's address with 404 rather than with the site it is in.
    /// </summary>
    public IEnumerable<SiteAddress> Candidates()
    {
        var segments = ServerRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var floor = Math.Max(Floor(segments), Math.Min(1, segments.Length));
        for (var n = segments.Length; n >= floor; n--)
        {
            yield return this with { ServerRelativePath = n == 0 ? "" : "/" + string.Join('/', segments[..n]) };
        }
    }

    /// <summary>How many of an address's names are the site at least: two under a managed path (/sites/x), else none.</summary>
    private static int Floor(IReadOnlyList<string> segments)
        => segments.Count >= 2 && (segments[0].Equals("sites", StringComparison.OrdinalIgnoreCase) || segments[0].Equals("teams", StringComparison.OrdinalIgnoreCase)) ? 2 : 0;

    /// <summary>Percent-encodes a path's segments, leaving the slashes.</summary>
    public static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}

/// <summary>
/// Paths inside a library (a Graph drive). Which library, and which subsite, a source path is in is the source's to
/// work out (<see cref="SharePointSource"/>): its sites' libraries and subsites are folders, the rest is inside a library.
/// </summary>
public static class SharePointPaths
{
    /// <summary>The Graph address of an item in a drive: <c>drives/{id}/root</c> or <c>drives/{id}/root:/{escaped path}:</c>.</summary>
    public static string ItemAddress(string driveId, string itemPath)
        => itemPath.Length == 0 ? $"drives/{driveId}/root" : $"drives/{driveId}/root:/{SiteAddress.EscapePath(itemPath)}:";

    /// <summary>Throws <see cref="VfsError.InvalidPath"/> for a name SharePoint would refuse; the empty path (a root) is fine.</summary>
    public static void RequireValid(string path)
    {
        if (path.Length > 0)
        {
            SharePointNames.Require(path);
        }
    }
}
