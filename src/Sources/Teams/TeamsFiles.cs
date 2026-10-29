using System.Net;
using System.Text.RegularExpressions;

namespace AnyFS.Sources.Teams;

/// <summary>
/// Where Teams' files are (spec 7.9): a shared file's site and its path within the site's library, the tenant's SharePoint
/// host from its OneDrive one, a message's inline images, an image's type from its bytes, and attachment names. Pure, so
/// what decides where a token may be sent is tested on its own.
/// </summary>
internal static partial class TeamsFiles
{
    /// <summary>
    /// A SharePoint or OneDrive file's (or folder's) site and its path within the site's library:
    /// <c>https://t.sharepoint.com/sites/x/Shared Documents/General/a.txt</c> is the site <c>https://t.sharepoint.com/sites/x</c>
    /// and <c>General/a.txt</c>. The site is the one given where it holds the address, else the address's first two segments
    /// (<c>/sites/x</c>, <c>/teams/x</c>, <c>/personal/x</c>); the library is the segment after it. Null for an address on no
    /// SharePoint host, so the SharePoint token is never sent anywhere else.
    /// </summary>
    public static (string SiteUrl, string Path)? Locate(string address, string? siteUrl)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var file) || file.Scheme != Uri.UriSchemeHttps || !TeamsServices.IsSharePointHost(file.Host))
        {
            return null;
        }

        var segments = Segments(file);
        var depth = 2;
        if (siteUrl is not null && Uri.TryCreate(siteUrl, UriKind.Absolute, out var site) && site.Host.Equals(file.Host, StringComparison.OrdinalIgnoreCase))
        {
            var siteSegments = Segments(site);
            if (siteSegments.Count < segments.Count && siteSegments.SequenceEqual(segments.Take(siteSegments.Count), StringComparer.OrdinalIgnoreCase))
            {
                depth = siteSegments.Count;
            }
        }

        // the site, its library, and something in it
        if (segments.Count < depth + 2)
        {
            return null;
        }

        var siteBase = $"https://{file.Host}" + string.Concat(segments.Take(depth).Select(s => "/" + Uri.EscapeDataString(s)));
        return (siteBase, string.Join('/', segments.Skip(depth + 1)));
    }

    private static List<string> Segments(Uri uri) => uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();

    /// <summary>A tenant's SharePoint host from its OneDrive host: <c>t-my.sharepoint.com</c> is <c>t.sharepoint.com</c>.</summary>
    public static string RootHost(string host)
    {
        var dot = host.IndexOf('.', StringComparison.Ordinal);
        return dot > 3 && host[..dot].EndsWith("-my", StringComparison.OrdinalIgnoreCase) ? host[..(dot - 3)] + host[dot..] : host;
    }

    /// <summary>
    /// Whether an address is Teams' media service, the only place the skype token is sent: the regional host the authz call
    /// named, or the media service's own hosts in any cloud (<c>*.asyncgw.teams.microsoft.com</c>, <c>*.asm.skype.com</c>,
    /// <c>*.ams.gov.teams.microsoft.us</c>).
    /// </summary>
    public static bool IsMediaService(string address, string? ams)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host;
        return (ams is not null && Uri.TryCreate(ams, UriKind.Absolute, out var known) && host.Equals(known.Host, StringComparison.OrdinalIgnoreCase))
            || host.EndsWith(".asyncgw.teams.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".asm.skype.com", StringComparison.OrdinalIgnoreCase)
            || (host.Contains(".ams.", StringComparison.OrdinalIgnoreCase) && host.EndsWith(".teams.microsoft.us", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The addresses of a message's inline images: its AMSImage <c>img</c> elements' sources, in order, each once.</summary>
    public static IReadOnlyList<string> ImagesIn(string? html)
    {
        if (string.IsNullOrEmpty(html) || !html.Contains("AMSImage", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var images = new List<string>();
        foreach (Match tag in ImgTag().Matches(html))
        {
            if (!tag.Value.Contains("schema.skype.com/AMSImage", StringComparison.OrdinalIgnoreCase) || SrcAttribute().Match(tag.Value) is not { Success: true } src)
            {
                continue;
            }

            var value = WebUtility.HtmlDecode(src.Groups["d"].Success ? src.Groups["d"].Value : src.Groups["s"].Value);
            if (value.Length > 0 && !images.Contains(value, StringComparer.Ordinal))
            {
                images.Add(value);
            }
        }

        return images;
    }

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImgTag();

    [GeneratedRegex(@"\bsrc\s*=\s*(?:""(?<d>[^""]*)""|'(?<s>[^']*)')", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SrcAttribute();

    /// <summary>An image's file extension: from its first bytes (PNG, JPEG, GIF, WebP, BMP), else its content type, else <c>.bin</c>.</summary>
    public static string ExtensionOf(ReadOnlySpan<byte> bytes, string? contentType)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']))
        {
            return ".png";
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return ".jpg";
        }

        if (bytes.StartsWith("GIF8"u8))
        {
            return ".gif";
        }

        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return ".webp";
        }

        if (bytes.StartsWith("BM"u8))
        {
            return ".bmp";
        }

        return contentType?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/heic" => ".heic",
            _ => ".bin",
        };
    }

    /// <summary>Attachment names in order: each made safe, twins numbered before the extension (<c>report (2).docx</c>).</summary>
    public static IReadOnlyList<string> Unique(IReadOnlyList<string> raw)
    {
        var names = new string[raw.Count];
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < raw.Count; i++)
        {
            var safe = TeamsNames.Safe(raw[i]);
            var stem = Path.GetFileNameWithoutExtension(safe);
            var extension = Path.GetExtension(safe);
            var name = safe;
            for (var n = 2; !taken.Add(name); n++)
            {
                name = $"{stem} ({n}){extension}";
            }

            names[i] = name;
        }

        return names;
    }
}
