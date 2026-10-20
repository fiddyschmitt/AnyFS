using System.Runtime.CompilerServices;
using System.Text.Json;
using AnyFS.Sdk;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// Drives through Microsoft Graph, items by path under each drive's root. SharePoint Online uses it with a
/// site's document libraries as the drives, and its subsites' (<see cref="ConnectAsync"/>); SharePoint Embedded
/// with the containers of a container type (<see cref="ForContainersAsync"/>), whose ids are drive ids too.
/// </summary>
internal sealed class GraphDocumentStore : IDocumentStore
{
    private const string ChildrenSelect = "$select=id,name,size,file,folder,fileSystemInfo,lastModifiedDateTime,createdDateTime,eTag,cTag&$top=500";

    /// <summary>The same for the site's pages, with each page's content fields (<see cref="SharePointPages"/>).</summary>
    private const string PageSelect = "$select=id,name,size,file,folder,fileSystemInfo,lastModifiedDateTime,createdDateTime,eTag,cTag,webUrl&$expand=listItem($select=id;$expand=fields($select=" + SharePointPages.Fields + "))";

    private readonly GraphClient _graph;
    private readonly PageCache _pages = new();
    private readonly Func<CancellationToken, Task<IReadOnlyList<LibraryInfo>>> _listLibraries;
    private readonly SiteAddress? _site;

    private GraphDocumentStore(GraphClient graph, string description, string libraryNoun, string siteId, Func<CancellationToken, Task<IReadOnlyList<LibraryInfo>>> listLibraries, SiteAddress? site = null)
    {
        _graph = graph;
        Description = description;
        LibraryNoun = libraryNoun;
        SiteId = siteId;
        _listLibraries = listLibraries;
        _site = site;
    }

    public string Description { get; }

    public string LibraryNoun { get; }

    /// <summary>The site's Graph id (for tests); empty for containers.</summary>
    public string SiteId { get; }

    /// <summary>The Graph client (for tests, which make their page through it).</summary>
    internal GraphClient Graph => _graph;

    /// <summary>Resolves the site the address is in (<see cref="FindAsync"/>); null when Graph has no such site for this account.</summary>
    public static async Task<GraphDocumentStore?> ConnectAsync(GraphClient graph, SiteAddress site, CancellationToken ct)
    {
        if (await FindAsync(graph, site, ct).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        var description = $"{found.Title} ({found.Site.Url})";
        return new GraphDocumentStore(graph, description, "document library", found.Id, ct2 => ListDrivesAsync(graph, found.Id, null, description, ct2), found.Site);
    }

    /// <summary>A store for a source of several sites: the source finds them (<see cref="FindSiteAsync"/>).</summary>
    public static GraphDocumentStore ForSites(GraphClient graph)
        => new(graph, "the sites given", "document library", "", _ => Task.FromResult<IReadOnlyList<LibraryInfo>>([]));

    /// <summary>One of several sites, found as <see cref="ConnectAsync"/> finds a site.</summary>
    public async Task<WebInfo> FindSiteAsync(SiteAddress site, CancellationToken ct)
    {
        var found = await FindAsync(_graph, site, ct).ConfigureAwait(false)
            ?? throw new VfsException(VfsError.NotFound, $"{site.Url} was not found, or the account has no access to it");
        return new WebInfo(found.Id, WebInfo.NameOf(new Uri(found.Site.Url)), found.Site.Url, found.SiteCollection);
    }

    /// <summary>
    /// The site an address is in, with its id and title: the address itself, else each shorter one
    /// (<see cref="SiteAddress.Candidates"/>), since Graph has no site at a library's or a folder's address. Null when none
    /// answers. A site collection's own site carries Graph's siteCollection facet; one at a site collection's address in
    /// SharePoint Online (<see cref="SiteAddress.IsSiteCollectionAddress"/>) is taken as one too.
    /// </summary>
    private static async Task<(string Id, string Title, SiteAddress Site, bool SiteCollection)?> FindAsync(GraphClient graph, SiteAddress site, CancellationToken ct)
    {
        foreach (var candidate in site.Candidates())
        {
            using var doc = await graph.TryGetJsonAsync($"{candidate.GraphPath}?$select=id,displayName,webUrl,siteCollection", candidate.Url, ct).ConfigureAwait(false);
            if (doc is null)
            {
                continue;
            }

            var id = doc.RootElement.GetProperty("id").GetString() ?? throw new VfsException(VfsError.IoError, "Graph returned a site without an id");
            var title = doc.RootElement.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
            var collection = doc.RootElement.TryGetProperty("siteCollection", out var facet) && facet.ValueKind == JsonValueKind.Object || candidate.IsSiteCollectionAddress;
            return (id, string.IsNullOrEmpty(title) ? candidate.Url : title, candidate, collection);
        }

        return null;
    }

    /// <summary>
    /// The containers of a container type that the signed-in user is a direct member of, each a drive; or one
    /// container by id (<paramref name="containerId"/>), which works for an account Graph lists nothing for.
    /// Lists once now, so that a container type the app is not registered on fails here with Graph's reason.
    /// </summary>
    public static async Task<GraphDocumentStore> ForContainersAsync(GraphClient graph, Guid containerTypeId, string? containerId, CancellationToken ct)
    {
        var description = $"SharePoint Embedded containers of type {containerTypeId}";
        Func<CancellationToken, Task<IReadOnlyList<LibraryInfo>>> list = containerId is null
            ? ct2 => ListContainersAsync(graph, containerTypeId, ct2)
            : ct2 => GetContainerAsync(graph, containerTypeId, containerId, ct2);
        var store = new GraphDocumentStore(graph, description, "container", "", list);
        await store.ListLibrariesAsync(null, ct).ConfigureAwait(false);
        return store;
    }

    /// <summary>
    /// One container by its id, named by that id (the setting it was pinned with). Graph answers this for any
    /// member, where the listing (<see cref="ListContainersAsync"/>) shows nothing to an account without a
    /// OneDrive or whose membership comes through a group (18-Oct-2026: the sandbox administrator).
    /// </summary>
    internal static async Task<IReadOnlyList<LibraryInfo>> GetContainerAsync(GraphClient graph, Guid containerTypeId, string containerId, CancellationToken ct)
    {
        var what = $"container {containerId}";
        using var doc = await graph.TryGetJsonAsync($"storage/fileStorage/containers/{Uri.EscapeDataString(containerId)}", what, ct).ConfigureAwait(false)
            ?? throw VfsException.NotFound(what);
        if (doc.RootElement.TryGetProperty("containerTypeId", out var type) && Guid.TryParse(type.GetString(), out var actual) && actual != containerTypeId)
        {
            throw new VfsException(VfsError.InvalidPath, $"{what} belongs to container type {actual}, not {containerTypeId}");
        }

        var container = ParseContainer(doc.RootElement) ?? throw VfsException.Unavailable($"{what} is not active");
        return [container with { Name = containerId }];
    }

    /// <summary>True for what looks like a container's id (a drive id: "b!" and 60-odd characters) rather than a display name.</summary>
    internal static bool LooksLikeContainerId(string value) => value.StartsWith("b!", StringComparison.Ordinal) && value.Length > 20 && !value.Any(char.IsWhiteSpace);

    public void Dispose() => _graph.Dispose();

    public Task<IReadOnlyList<LibraryInfo>> ListLibrariesAsync(WebInfo? web, CancellationToken ct)
        => web is null ? _listLibraries(ct) : ListDrivesAsync(_graph, web.Key, web, web.Name, ct);

    /// <summary>None: Graph reaches the files in lists and drives only, not those in a site's own folder.</summary>
    public Task<IReadOnlyList<SitePage>> ListSitePagesAsync(WebInfo? web, CancellationToken ct) => Task.FromResult<IReadOnlyList<SitePage>>([]);

    /// <summary>A site's subsites, each named by the last part of its address; containers have none.</summary>
    public async Task<IReadOnlyList<WebInfo>> ListSubsitesAsync(WebInfo? web, CancellationToken ct)
    {
        if (web is null && SiteId.Length == 0)
        {
            return [];   // containers, or the several sites' store, which has no site of its own
        }

        var url = $"sites/{web?.Key ?? SiteId}/sites?$select=id,webUrl";
        var subsites = new List<WebInfo>();
        while (url is not null)
        {
            using var doc = await _graph.GetJsonAsync(url, web?.Name ?? Description, ct).ConfigureAwait(false);
            foreach (var element in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var id = element.TryGetProperty("id", out var i) ? i.GetString() : null;
                var address = element.TryGetProperty("webUrl", out var w) && Uri.TryCreate(w.GetString(), UriKind.Absolute, out var parsed) ? parsed : null;
                var name = address is null ? "" : Uri.UnescapeDataString(address.AbsolutePath.TrimEnd('/').Split('/')[^1]);
                if (!string.IsNullOrEmpty(id) && name.Length > 0)
                {
                    subsites.Add(new WebInfo(id, name, address!.AbsoluteUri.TrimEnd('/')));
                }
            }

            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }

        return subsites;
    }

    /// <summary>
    /// Graph's site search across the tenant (what the account may open), kept to site collections on the root site's
    /// host: SharePoint Online's are the root and /sites/x or /teams/x, so a deeper address is a subsite and is left out.
    /// </summary>
    public async Task<IReadOnlyList<WebInfo>> ListSitesAsync(WebInfo? web, CancellationToken ct)
    {
        var root = web?.Url is { } address && SiteAddress.TryParse(address, out var parsed, out _) ? parsed : _site;
        if (root is null)
        {
            return [];
        }

        var url = "sites?search=*&$select=id,webUrl";
        var found = new List<(string, string, string?)>();
        while (url is not null)
        {
            using var doc = await _graph.GetJsonAsync(url, Description, ct).ConfigureAwait(false);
            foreach (var element in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var id = element.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (!string.IsNullOrEmpty(id) && element.TryGetProperty("webUrl", out var w) && Uri.TryCreate(w.GetString(), UriKind.Absolute, out var site)
                    && string.Equals(site.Authority, root.Host, StringComparison.OrdinalIgnoreCase))
                {
                    var path = Uri.UnescapeDataString(site.AbsolutePath).TrimEnd('/');
                    if (path.Count(c => c == '/') == 2 && !string.Equals(path, root.ServerRelativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add((id, path, site.AbsoluteUri.TrimEnd('/')));
                    }
                }
            }

            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }

        return WebInfo.Sites(found);
    }

    private static async Task<IReadOnlyList<LibraryInfo>> ListDrivesAsync(GraphClient graph, string siteId, WebInfo? web, string description, CancellationToken ct)
    {
        var drivesCall = graph.GetJsonAsync($"sites/{siteId}/drives?$select=id,name,driveType,quota", description, ct);
        var pagesCall = PagesLibraryAsync(graph, siteId, web, description, ct);
        await Task.WhenAll(drivesCall, pagesCall).ConfigureAwait(false);
        using var doc = await drivesCall.ConfigureAwait(false);
        var libraries = new List<LibraryInfo>();
        foreach (var element in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var drive = GraphClient.ParseDrive(element);
            if (drive.Name.Length > 0)
            {
                libraries.Add(new LibraryInfo(drive.Id, drive.Name, drive.QuotaTotal, drive.QuotaRemaining, web));
            }
        }

        if (await pagesCall.ConfigureAwait(false) is { } pages && !libraries.Any(l => l.Key == pages.Key))
        {
            libraries.Add(pages);
        }

        return libraries;
    }

    /// <summary>
    /// The site's pages as a library: Graph keeps Site Pages out of a site's drives (it is a system list), so its drive is
    /// found through the site's lists, selected with their system facet. Null where there is none, or the account may
    /// not list the lists (its libraries still show).
    /// </summary>
    private static async Task<LibraryInfo?> PagesLibraryAsync(GraphClient graph, string siteId, WebInfo? web, string description, CancellationToken ct)
    {
        var url = $"sites/{siteId}/lists?$select=id,displayName,list,system&$expand=drive($select=id)";
        try
        {
            while (url is not null)
            {
                using var doc = await graph.GetJsonAsync(url, description, ct).ConfigureAwait(false);
                foreach (var list in doc.RootElement.GetProperty("value").EnumerateArray())
                {
                    if (list.TryGetProperty("list", out var facet) && facet.TryGetProperty("template", out var template) && template.GetString() == SharePointPages.GraphTemplate
                        && !(facet.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)
                        && list.TryGetProperty("drive", out var drive) && drive.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } driveId)
                    {
                        var name = list.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
                        return new LibraryInfo(driveId, string.IsNullOrWhiteSpace(name) ? "Site Pages" : name, null, null, web, Pages: true);
                    }
                }

                url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            }
        }
        catch (VfsException ex) when (ex.Error is VfsError.AccessDenied or VfsError.NotFound)
        {
        }

        return null;
    }

    /// <summary>
    /// Graph lists containers only by container type (the filter is mandatory), only the active ones matter,
    /// and only the user's direct memberships come back. A container's display name is free text, so the
    /// folder name is made from it (<see cref="FolderName"/>) and made unique.
    /// </summary>
    internal static async Task<IReadOnlyList<LibraryInfo>> ListContainersAsync(GraphClient graph, Guid containerTypeId, CancellationToken ct)
    {
        var what = $"containers of type {containerTypeId}";
        var url = $"storage/fileStorage/containers?$filter=containerTypeId eq {containerTypeId}";
        var containers = new List<LibraryInfo>();
        while (url is not null)
        {
            using var doc = await graph.GetJsonAsync(url, what, ct).ConfigureAwait(false);
            foreach (var element in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (ParseContainer(element) is { } container)
                {
                    containers.Add(container);
                }
            }

            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }

        return UniqueNames(containers);
    }

    /// <summary>A container as a library, or null for one that is not active yet.</summary>
    internal static LibraryInfo? ParseContainer(JsonElement element)
    {
        var id = element.GetProperty("id").GetString() ?? throw new VfsException(VfsError.IoError, "Graph returned a container without an id");
        if (element.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && !string.Equals(status.GetString(), "active", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var displayName = element.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : null;
        return new LibraryInfo(id, FolderName(displayName, id), null, null);
    }

    /// <summary>The folder a container shows as: its display name with what SharePoint forbids in a name replaced, or a name from its id.</summary>
    internal static string FolderName(string? displayName, string id)
    {
        var name = new string((displayName ?? "").Trim().Select(c => c < ' ' || SharePointNames.IsInvalidChar(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0 || SharePointNames.Problem(name) is not null)
        {
            var tail = id.StartsWith("b!", StringComparison.Ordinal) ? id[2..] : id;
            name = "Container " + tail[..Math.Min(8, tail.Length)];
        }

        return name;
    }

    /// <summary>Two containers with the same display name get " (2)", " (3)" after the first, in id order so the names are stable.</summary>
    internal static IReadOnlyList<LibraryInfo> UniqueNames(IReadOnlyList<LibraryInfo> containers)
    {
        var result = new List<LibraryInfo>(containers.Count);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in containers.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            var name = container.Name;
            for (var n = 2; !taken.Add(name); n++)
            {
                name = $"{container.Name} ({n})";
            }

            result.Add(name == container.Name ? container : container with { Name = name });
        }

        return result;
    }

    public async IAsyncEnumerable<(VfsEntry Entry, ItemRef Item)> ListChildrenAsync(LibraryInfo library, string itemPath, [EnumeratorCancellation] CancellationToken ct)
    {
        var url = $"{SharePointPaths.ItemAddress(library.Key, itemPath)}/children?{(library.Pages ? PageSelect : ChildrenSelect)}";
        while (url is not null)
        {
            using var doc = await _graph.GetJsonAsync(url, Display(library, itemPath), ct).ConfigureAwait(false);
            foreach (var element in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (!library.Pages || element.TryGetProperty("folder", out _))
                {
                    yield return GraphClient.ParseItem(element);
                }
                else if (SharePointPages.IsPage(element.GetProperty("name").GetString() ?? ""))
                {
                    yield return ParsePage(element);
                }
            }

            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
    }

    /// <summary>A page as its Markdown file; the Markdown kept for the read that follows.</summary>
    private (VfsEntry Entry, ItemRef Item) ParsePage(JsonElement element)
    {
        var (entry, item) = GraphClient.ParseItem(element);
        var fields = element.TryGetProperty("listItem", out var listItem) && listItem.TryGetProperty("fields", out var f) ? f : default;
        var pageUrl = element.TryGetProperty("webUrl", out var w) && Uri.TryCreate(w.GetString(), UriKind.Absolute, out var u) ? u : null;
        var markdown = SharePointPages.Markdown(fields, pageUrl);
        var version = SharePointPages.Version(markdown);
        _pages.Remember(item.Id, version, markdown);
        return (VfsEntry.File(SharePointPages.Shown(entry.Name), markdown.Length, entry.Modified, entry.Created, VfsAttributes.ReadOnly, version), item with { Version = version, DownloadUrl = null, Size = markdown.Length });
    }

    public async Task<ItemRef?> GetItemAsync(LibraryInfo library, string itemPath, CancellationToken ct)
    {
        // in the site's pages a file is a page, by its Markdown name; anything else there is a folder or nothing
        if (library.Pages && SharePointPages.Upstream(itemPath) is { } page)
        {
            using var found = await _graph.TryGetJsonAsync($"{SharePointPaths.ItemAddress(library.Key, page)}?{PageSelect}", Display(library, itemPath), ct).ConfigureAwait(false);
            if (found is not null && !found.RootElement.TryGetProperty("folder", out _))
            {
                return ParsePage(found.RootElement).Item;
            }
        }

        // no $select: the download URL is an annotation that only comes with the whole item
        using var doc = await _graph.TryGetJsonAsync(SharePointPaths.ItemAddress(library.Key, itemPath), Display(library, itemPath), ct).ConfigureAwait(false);
        var item = doc is null ? null : GraphClient.ParseItem(doc.RootElement).Item;
        return library.Pages && item is { IsFolder: false } ? null : item;
    }

    public async Task<HttpResponseMessage> OpenContentAsync(LibraryInfo library, string itemPath, ItemRef item, long offset, CancellationToken ct)
    {
        if (library.Pages)
        {
            // a page is its Markdown: from the listing that sized it, else made again
            var markdown = _pages.Find(item.Id, item.Version);
            if (markdown is null)
            {
                var again = await GetItemAsync(library, itemPath, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(Display(library, itemPath));
                markdown = _pages.Find(again.Id, again.Version) ?? throw VfsException.NotFound(Display(library, itemPath));
            }

            return SharePointPages.Response(markdown, offset);
        }

        var path = Display(library, itemPath);
        var url = item.DownloadUrl ?? await FreshDownloadUrlAsync(library, itemPath, ct).ConfigureAwait(false);
        try
        {
            return await _graph.GetContentAsync(url, offset, path, ct).ConfigureAwait(false);
        }
        catch (VfsException ex) when (ex.Error is VfsError.AccessDenied or VfsError.NotFound or VfsError.Unavailable)
        {
            // the pre-authenticated URL expires after a while: a fresh one, once
            url = await FreshDownloadUrlAsync(library, itemPath, ct).ConfigureAwait(false);
            return await _graph.GetContentAsync(url, offset, path, ct).ConfigureAwait(false);
        }
    }

    public async Task<ItemRef> UploadAsync(LibraryInfo library, string itemPath, string localPath, long length, CancellationToken ct)
    {
        var address = SharePointPaths.ItemAddress(library.Key, itemPath);
        var path = Display(library, itemPath);
        JsonDocument result;
        if (length <= GraphClient.SimpleUploadLimit)
        {
            result = await _graph.PutContentAsync(address, () => OpenLocal(localPath), length, path, ct).ConfigureAwait(false);
        }
        else
        {
            await using var content = OpenLocal(localPath);
            result = await _graph.UploadAsync(address, content, length, path, ct).ConfigureAwait(false);
        }

        using (result)
        {
            return GraphClient.ParseItem(result.RootElement).Item;
        }
    }

    public async Task<ItemRef> CreateFolderAsync(LibraryInfo library, string itemPath, CancellationToken ct)
    {
        var (parent, name) = VfsPath.SplitLast(itemPath);
        var json = "{\"name\":" + Json.Quote(name) + ",\"folder\":{},\"@microsoft.graph.conflictBehavior\":\"fail\"}";
        try
        {
            using var created = await _graph.SendJsonAsync(HttpMethod.Post, $"{SharePointPaths.ItemAddress(library.Key, parent)}/children", json, Display(library, itemPath), ct).ConfigureAwait(false);
            return GraphClient.ParseItem(created.RootElement).Item;
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            throw VfsException.NotFound(Display(library, parent));
        }
    }

    public Task DeleteAsync(LibraryInfo library, string itemPath, bool isFolder, CancellationToken ct)
        => _graph.DeleteAsync(SharePointPaths.ItemAddress(library.Key, itemPath), Display(library, itemPath), ct);

    public async Task<ItemRef> MoveAsync(LibraryInfo library, string fromPath, string toPath, bool isFolder, CancellationToken ct)
    {
        var (toParent, toName) = VfsPath.SplitLast(toPath);
        string parentId;
        if (toParent.Length == 0)
        {
            using var root = await _graph.GetJsonAsync($"drives/{library.Key}/root?$select=id", library.Shown, ct).ConfigureAwait(false);
            parentId = root.RootElement.GetProperty("id").GetString()!;
        }
        else
        {
            var parentItem = await GetItemAsync(library, toParent, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(Display(library, toParent));
            parentId = parentItem.Id;
        }

        var json = "{\"name\":" + Json.Quote(toName) + ",\"parentReference\":{\"id\":" + Json.Quote(parentId) + "}}";
        using var moved = await _graph.SendJsonAsync(HttpMethod.Patch, SharePointPaths.ItemAddress(library.Key, fromPath), json, Display(library, fromPath), ct).ConfigureAwait(false);
        return GraphClient.ParseItem(moved.RootElement).Item;
    }

    public async Task SetTimesAsync(LibraryInfo library, string itemPath, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct)
    {
        var parts = new List<string>();
        if (created is { } c)
        {
            parts.Add("\"createdDateTime\":" + Json.Quote(c.UtcDateTime.ToString("O")));
        }

        if (modified is { } m)
        {
            parts.Add("\"lastModifiedDateTime\":" + Json.Quote(m.UtcDateTime.ToString("O")));
        }

        if (parts.Count == 0)
        {
            return;
        }

        var json = "{\"fileSystemInfo\":{" + string.Join(',', parts) + "}}";
        using var _ = await _graph.SendJsonAsync(HttpMethod.Patch, SharePointPaths.ItemAddress(library.Key, itemPath), json, Display(library, itemPath), ct).ConfigureAwait(false);
    }

    private async Task<string> FreshDownloadUrlAsync(LibraryInfo library, string itemPath, CancellationToken ct)
    {
        var item = await GetItemAsync(library, itemPath, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(Display(library, itemPath));
        return item.DownloadUrl ?? throw new VfsException(VfsError.IoError, $"'{Display(library, itemPath)}' has no download URL (is it a file?)");
    }

    private static FileStream OpenLocal(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);

    private static string Display(LibraryInfo library, string itemPath) => itemPath.Length == 0 ? library.Shown : library.Shown + "/" + itemPath;
}

/// <summary>JSON string literals without a serializer (nothing for the trimmer to analyse).</summary>
internal static class Json
{
    public static string Quote(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";
}
