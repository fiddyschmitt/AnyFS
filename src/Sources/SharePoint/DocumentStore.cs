using AnyFS.Sdk;

namespace AnyFS.Sources.SharePoint;

/// <summary>A document library, or whatever a flavour serves as a library's folder (a drive, a list, a container).</summary>
/// <param name="Key">What the store needs to address it: a Graph drive id, a server-relative URL, a container id.</param>
/// <param name="Name">The folder name it shows as.</param>
/// <param name="Web">The subsite it belongs to (that <see cref="WebInfo"/>), or null for the source's own site.</param>
/// <param name="Folder">Where it shows in the source ("Foo/Documents" for a subsite's), for messages; null for its name alone.</param>
/// <param name="Pages">The site's pages (Site Pages): each .aspx page shows as a read-only Markdown file (<see cref="SharePointPages"/>).</param>
internal sealed record LibraryInfo(string Key, string Name, long? QuotaTotal, long? QuotaRemaining, WebInfo? Web = null, string? Folder = null, bool Pages = false)
{
    /// <summary>The library as messages name it.</summary>
    public string Shown => Folder ?? Name;
}

/// <summary>A subsite, another site of the tenant, or one of the sites a source was given, shown as a folder.</summary>
/// <param name="Key">What the store needs to address it: its URL, or its Graph site id.</param>
/// <param name="Name">The folder name it shows as: the last part of its address, which is unique among its siblings where titles need not be.</param>
/// <param name="Url">Its address, where the store knows it.</param>
/// <param name="SiteCollection">Whether it is a site collection's own site (the root, /sites/x) rather than a subsite; null where the store does not say.</param>
internal sealed record WebInfo(string Key, string Name, string? Url = null, bool? SiteCollection = null)
{
    /// <summary>The root site of its host (a tenant's or a farm's root site), which shows the other sites in a Sites folder.</summary>
    public bool IsRoot => Url is not null && Uri.TryCreate(Url, UriKind.Absolute, out var url) && url.AbsolutePath.Trim('/').Length == 0;

    /// <summary>
    /// The sites a source was given, as its top-level folders: each by the last part of its address, a root site by its
    /// host; where two would share a name, the rest of the path, or the host, follows in brackets ("Ops (sites)", "Ops
    /// (intranet)"), and a number past that. In the order given.
    /// </summary>
    public static IReadOnlyList<WebInfo> Listed(IEnumerable<(string Key, Uri Url)> sites) => Listed(sites.Select(s => new WebInfo(s.Key, "", s.Url.AbsoluteUri)));

    /// <summary>The same for sites the store found (each with its address), keeping what else it knows of them.</summary>
    public static IReadOnlyList<WebInfo> Listed(IEnumerable<WebInfo> sites)
    {
        var found = sites.Select(s => (Site: s, Url: new Uri(s.Url!), Path: PathOf(new Uri(s.Url!)))).ToList();
        var lastCount = found.GroupBy(s => Last(s.Url, s.Path), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WebInfo>();
        foreach (var (site, url, path) in found)
        {
            var last = Last(url, path);
            var slash = path.LastIndexOf('/');
            var name = lastCount[last] == 1 ? last
                : slash > 0 && found.Count(s => string.Equals(Last(s.Url, s.Path), last, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)) == 1 ? $"{last} ({path[..slash]})"
                : $"{last} ({url.Host})";
            for (var n = 2; !taken.Add(name); n++)
            {
                name = $"{last} ({n})";
            }

            result.Add(site with { Name = name, Url = url.AbsoluteUri.TrimEnd('/') });
        }

        return result;
    }

    /// <summary>Whether two of these are the same site: the same key, or the same address.</summary>
    public bool IsSameSite(WebInfo other)
        => string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase)
            || (Url is not null && other.Url is not null && string.Equals(Url.TrimEnd('/'), other.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A site that joins those already shown (one left out at first that answers later), named as <see cref="Listed"/>
    /// names: as its address ends, else with the rest of its path after it (its server where a site shown has the same
    /// path), else a number; never a name shown already, so no folder already shown is renamed.
    /// </summary>
    public static WebInfo Joining(WebInfo site, IReadOnlyCollection<WebInfo> shown)
    {
        var url = new Uri(site.Url!);
        var path = PathOf(url);
        var last = Last(url, path);
        var slash = path.LastIndexOf('/');
        var samePath = shown.Any(w => w.Url is not null && string.Equals(PathOf(new Uri(w.Url)), path, StringComparison.OrdinalIgnoreCase));
        bool Free(string name) => !shown.Any(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        foreach (var name in new[] { last, slash > 0 && !samePath ? $"{last} ({path[..slash]})" : $"{last} ({url.Host})", $"{last} ({url.Host})" })
        {
            if (Free(name))
            {
                return site with { Name = name };
            }
        }

        for (var n = 2; ; n++)
        {
            if (Free($"{last} ({n})"))
            {
                return site with { Name = $"{last} ({n})" };
            }
        }
    }

    /// <summary>The name a site's address gives it alone: its last part, or a root site's host.</summary>
    public static string NameOf(Uri url) => Last(url, PathOf(url));

    private static string PathOf(Uri url) => Uri.UnescapeDataString(url.AbsolutePath).Trim('/');

    private static string Last(Uri url, string path) => path.Length == 0 ? url.Host : path[(path.LastIndexOf('/') + 1)..];

    /// <summary>
    /// The tenant's other sites as folders: each by the last part of its address ("Axle" for /sites/Axle), with the
    /// rest of its path after it where two end alike ("Ops (teams)" beside "Ops (sites)"), and a number past that.
    /// </summary>
    public static IReadOnlyList<WebInfo> Sites(IEnumerable<(string Key, string ServerRelativeUrl, string? Url)> sites)
    {
        var found = sites.Select(s => (s.Key, Path: s.ServerRelativeUrl.Trim('/'), s.Url)).Where(s => s.Path.Length > 0).DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var lastCount = found.GroupBy(s => Last(s.Path), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WebInfo>();
        foreach (var (key, path, url) in found.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
        {
            var last = Last(path);
            var slash = path.LastIndexOf('/');
            var name = lastCount[last] == 1 || slash < 0 ? last : $"{last} ({path[..slash]})";
            for (var n = 2; !taken.Add(name); n++)
            {
                name = $"{last} ({n})";
            }

            result.Add(new WebInfo(key, name, url));
        }

        return result;

        static string Last(string path) => path[(path.LastIndexOf('/') + 1)..];
    }
}

/// <summary>
/// What a listing knows about an item beyond its <see cref="VfsEntry"/>: the ids the next calls need. The
/// source treats it as opaque, except that <see cref="Id"/> and <see cref="Version"/> key the spool cache.
/// </summary>
/// <param name="Id">A stable id (Graph item id, SharePoint UniqueId).</param>
/// <param name="Version">Changes when the content changes (cTag, ETag).</param>
/// <param name="DownloadUrl">A pre-authenticated URL for the content, when the flavour has one.</param>
internal sealed record ItemRef(string Id, string? Version, string? DownloadUrl, long Size, bool IsFolder);

/// <summary>
/// The operations <see cref="SharePointSource"/> needs from a flavour, on paths inside a library. Every
/// method maps failures to <see cref="VfsException"/> and may throw <see cref="SignInRequiredException"/>
/// when the sign-in has lapsed. Item paths are '/'-separated, relative to the library, "" for its root.
/// </summary>
internal interface IDocumentStore : IDisposable
{
    /// <summary>What the source is serving, for log lines: the site's name or URL.</summary>
    string Description { get; }

    /// <summary>What a top-level folder is called in messages: "document library", or "container".</summary>
    string LibraryNoun { get; }

    /// <summary>
    /// One of the sites a source of several was given, by its address: the site the address is in (stepping back from
    /// a library's or a folder's address), with its key, its address and the last part of its address as its name (the
    /// source keeps the names apart). Throws what SharePoint answered for an address that is no site the account may
    /// open: NotFound, AccessDenied, Unavailable.
    /// </summary>
    Task<WebInfo> FindSiteAsync(SiteAddress site, CancellationToken ct);

    /// <summary>The libraries of the source's site, or of one of its subsites.</summary>
    Task<IReadOnlyList<LibraryInfo>> ListLibrariesAsync(WebInfo? web, CancellationToken ct);

    /// <summary>The subsites of the source's site or of a subsite, those the account may open; none where there are no sites (containers).</summary>
    Task<IReadOnlyList<WebInfo>> ListSubsitesAsync(WebInfo? web, CancellationToken ct);

    /// <summary>
    /// The tenant's other sites on a root site's host (not subsites: site collections, such as /sites/Axle), those the
    /// account may open, found by search; that root site is left out. The root site is the source's own (null), or one of
    /// the sites it was given. None where there are no sites; throws where there is no search to ask (a farm without a
    /// search service).
    /// </summary>
    Task<IReadOnlyList<WebInfo>> ListSitesAsync(WebInfo? web, CancellationToken ct);

    /// <summary>The children of a folder; throws NotFound for a missing folder. (The source checks for a file first.)</summary>
    IAsyncEnumerable<(VfsEntry Entry, ItemRef Item)> ListChildrenAsync(LibraryInfo library, string itemPath, CancellationToken ct);

    /// <summary>The item at a path, or null when there is none.</summary>
    Task<ItemRef?> GetItemAsync(LibraryInfo library, string itemPath, CancellationToken ct);

    /// <summary>
    /// The file's content from <paramref name="offset"/>, headers read and the body left to stream: 200 or 206,
    /// or 416 past the end. The store deals with expired download URLs itself.
    /// </summary>
    Task<HttpResponseMessage> OpenContentAsync(LibraryInfo library, string itemPath, ItemRef item, long offset, CancellationToken ct);

    /// <summary>Creates or replaces the file from a local file; the item as it is afterwards.</summary>
    Task<ItemRef> UploadAsync(LibraryInfo library, string itemPath, string localPath, long length, CancellationToken ct);

    /// <summary>Creates a folder; AlreadyExists when there is one; NotFound when the parent is missing.</summary>
    Task<ItemRef> CreateFolderAsync(LibraryInfo library, string itemPath, CancellationToken ct);

    Task DeleteAsync(LibraryInfo library, string itemPath, bool isFolder, CancellationToken ct);

    /// <summary>Renames or moves within the library; the target must not exist.</summary>
    Task<ItemRef> MoveAsync(LibraryInfo library, string fromPath, string toPath, bool isFolder, CancellationToken ct);

    Task SetTimesAsync(LibraryInfo library, string itemPath, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct);
}
