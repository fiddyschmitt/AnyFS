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

    /// <summary>
    /// Accepts the forms people paste: with or without a scheme, with a trailing slash, with a page or a
    /// library path after the site (<c>/sites/Team/Shared%20Documents/Forms/AllItems.aspx</c> is cut back
    /// to <c>/sites/Team</c>). Anything that is not <c>/sites/x</c>, <c>/teams/x</c> or the root is kept
    /// as given, for tenants with other managed paths.
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
        string path;
        if (segments.Count >= 2 && (segments[0].Equals("sites", StringComparison.OrdinalIgnoreCase) || segments[0].Equals("teams", StringComparison.OrdinalIgnoreCase)))
        {
            path = "/" + segments[0] + "/" + segments[1];
        }
        else
        {
            // the root site or another managed path, possibly with a page after it: a library view
            // (<library>/Forms/<view>.aspx) or a site page (SitePages/<page>.aspx) is cut off
            if (segments.Count > 0 && segments[^1].EndsWith(".aspx", StringComparison.OrdinalIgnoreCase))
            {
                segments.RemoveAt(segments.Count - 1);
                if (segments.Count >= 2 && segments[^1].Equals("Forms", StringComparison.OrdinalIgnoreCase))
                {
                    segments.RemoveRange(segments.Count - 2, 2);
                }
                else if (segments.Count >= 1 && (segments[^1].Equals("SitePages", StringComparison.OrdinalIgnoreCase) || segments[^1].Equals("Pages", StringComparison.OrdinalIgnoreCase)))
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }

            path = segments.Count == 0 ? "" : "/" + string.Join('/', segments);
        }

        var host = uri.IsDefaultPort ? uri.Host.ToLowerInvariant() : $"{uri.Host.ToLowerInvariant()}:{uri.Port}";
        address = new SiteAddress(host, path, uri.Scheme.ToLowerInvariant());
        return true;
    }

    /// <summary>Percent-encodes a path's segments, leaving the slashes.</summary>
    public static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}

/// <summary>
/// How a source path maps onto a site: the first segment names a document library (a Graph drive), the
/// rest is the path inside it. With a library pinned in the settings, the whole source path is inside it.
/// </summary>
public static class SharePointPaths
{
    /// <summary>Splits a normalised source path into the library name and the path inside the library ("" for its root).</summary>
    public static (string Library, string ItemPath) Split(string path, string? pinnedLibrary)
    {
        if (!string.IsNullOrEmpty(pinnedLibrary))
        {
            return (pinnedLibrary, path);
        }

        var slash = path.IndexOf('/');
        return slash < 0 ? (path, "") : (path[..slash], path[(slash + 1)..]);
    }

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
