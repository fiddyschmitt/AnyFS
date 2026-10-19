using AnyFS.Sdk;

namespace AnyFS.Sources.SharePoint;

/// <summary>A document library, or whatever a flavour serves as a library's folder (a drive, a list, a container).</summary>
/// <param name="Key">What the store needs to address it: a Graph drive id, a server-relative URL, a container id.</param>
/// <param name="Name">The folder name it shows as.</param>
/// <param name="Web">The subsite it belongs to (that <see cref="WebInfo"/>), or null for the source's own site.</param>
/// <param name="Folder">Where it shows in the source ("Foo/Documents" for a subsite's), for messages; null for its name alone.</param>
internal sealed record LibraryInfo(string Key, string Name, long? QuotaTotal, long? QuotaRemaining, WebInfo? Web = null, string? Folder = null)
{
    /// <summary>The library as messages name it.</summary>
    public string Shown => Folder ?? Name;
}

/// <summary>A subsite, or another site of the tenant, shown as a folder.</summary>
/// <param name="Key">What the store needs to address it: its server-relative URL, or its Graph site id.</param>
/// <param name="Name">The folder name it shows as: the last part of its address, which is unique among its siblings where titles need not be.</param>
internal sealed record WebInfo(string Key, string Name)
{
    /// <summary>
    /// The tenant's other sites as folders: each by the last part of its address ("Axle" for /sites/Axle), with the
    /// rest of its path after it where two end alike ("Ops (teams)" beside "Ops (sites)"), and a number past that.
    /// </summary>
    public static IReadOnlyList<WebInfo> Sites(IEnumerable<(string Key, string ServerRelativeUrl)> sites)
    {
        var found = sites.Select(s => (s.Key, Path: s.ServerRelativeUrl.Trim('/'))).Where(s => s.Path.Length > 0).DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var lastCount = found.GroupBy(s => Last(s.Path), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WebInfo>();
        foreach (var (key, path) in found.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
        {
            var last = Last(path);
            var slash = path.LastIndexOf('/');
            var name = lastCount[last] == 1 || slash < 0 ? last : $"{last} ({path[..slash]})";
            for (var n = 2; !taken.Add(name); n++)
            {
                name = $"{last} ({n})";
            }

            result.Add(new WebInfo(key, name));
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

    /// <summary>The libraries of the source's site, or of one of its subsites.</summary>
    Task<IReadOnlyList<LibraryInfo>> ListLibrariesAsync(WebInfo? web, CancellationToken ct);

    /// <summary>The subsites of the source's site or of a subsite, those the account may open; none where there are no sites (containers).</summary>
    Task<IReadOnlyList<WebInfo>> ListSubsitesAsync(WebInfo? web, CancellationToken ct);

    /// <summary>
    /// The tenant's other sites on the source's host (not subsites: site collections, such as /sites/Axle), those the
    /// account may open, found by search; the source's own site is left out. None where there are no sites; throws
    /// where there is no search to ask (a farm without a search service).
    /// </summary>
    Task<IReadOnlyList<WebInfo>> ListSitesAsync(CancellationToken ct);

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
