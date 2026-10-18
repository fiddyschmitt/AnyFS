using System.Runtime.CompilerServices;
using System.Text.Json;
using AnyFS.Sdk;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// Drives through Microsoft Graph, items by path under each drive's root. SharePoint Online uses it with a
/// site's document libraries as the drives (<see cref="ConnectAsync"/>); SharePoint Embedded with the
/// containers of a container type (<see cref="ForContainersAsync"/>), whose ids are drive ids too.
/// </summary>
internal sealed class GraphDocumentStore : IDocumentStore
{
    private const string ChildrenSelect = "$select=id,name,size,file,folder,fileSystemInfo,lastModifiedDateTime,createdDateTime,eTag,cTag&$top=500";

    private readonly GraphClient _graph;
    private readonly Func<CancellationToken, Task<IReadOnlyList<LibraryInfo>>> _listLibraries;

    private GraphDocumentStore(GraphClient graph, string description, string libraryNoun, string siteId, Func<CancellationToken, Task<IReadOnlyList<LibraryInfo>>> listLibraries)
    {
        _graph = graph;
        Description = description;
        LibraryNoun = libraryNoun;
        SiteId = siteId;
        _listLibraries = listLibraries;
    }

    public string Description { get; }

    public string LibraryNoun { get; }

    /// <summary>The site's Graph id (for tests); empty for containers.</summary>
    public string SiteId { get; }

    /// <summary>Resolves the site; null when Graph has no such site for this account.</summary>
    public static async Task<GraphDocumentStore?> ConnectAsync(GraphClient graph, SiteAddress site, CancellationToken ct)
    {
        using var doc = await graph.TryGetJsonAsync($"{site.GraphPath}?$select=id,displayName,webUrl", site.Url, ct).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        var id = doc.RootElement.GetProperty("id").GetString() ?? throw new VfsException(VfsError.IoError, "Graph returned a site without an id");
        var name = doc.RootElement.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? site.Url : site.Url;
        var description = $"{name} ({site.Url})";
        return new GraphDocumentStore(graph, description, "document library", id, ct2 => ListDrivesAsync(graph, id, description, ct2));
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
        await store.ListLibrariesAsync(ct).ConfigureAwait(false);
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

    public Task<IReadOnlyList<LibraryInfo>> ListLibrariesAsync(CancellationToken ct) => _listLibraries(ct);

    private static async Task<IReadOnlyList<LibraryInfo>> ListDrivesAsync(GraphClient graph, string siteId, string description, CancellationToken ct)
    {
        using var doc = await graph.GetJsonAsync($"sites/{siteId}/drives?$select=id,name,driveType,quota", description, ct).ConfigureAwait(false);
        var libraries = new List<LibraryInfo>();
        foreach (var element in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var drive = GraphClient.ParseDrive(element);
            if (drive.Name.Length > 0)
            {
                libraries.Add(new LibraryInfo(drive.Id, drive.Name, drive.QuotaTotal, drive.QuotaRemaining));
            }
        }

        return libraries;
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
        var url = $"{SharePointPaths.ItemAddress(library.Key, itemPath)}/children?{ChildrenSelect}";
        while (url is not null)
        {
            using var doc = await _graph.GetJsonAsync(url, Display(library, itemPath), ct).ConfigureAwait(false);
            foreach (var element in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                yield return GraphClient.ParseItem(element);
            }

            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
    }

    public async Task<ItemRef?> GetItemAsync(LibraryInfo library, string itemPath, CancellationToken ct)
    {
        // no $select: the download URL is an annotation that only comes with the whole item
        using var doc = await _graph.TryGetJsonAsync(SharePointPaths.ItemAddress(library.Key, itemPath), Display(library, itemPath), ct).ConfigureAwait(false);
        return doc is null ? null : GraphClient.ParseItem(doc.RootElement).Item;
    }

    public async Task<HttpResponseMessage> OpenContentAsync(LibraryInfo library, string itemPath, ItemRef item, long offset, CancellationToken ct)
    {
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
            using var root = await _graph.GetJsonAsync($"drives/{library.Key}/root?$select=id", library.Name, ct).ConfigureAwait(false);
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

    private static string Display(LibraryInfo library, string itemPath) => itemPath.Length == 0 ? library.Name : library.Name + "/" + itemPath;
}

/// <summary>JSON string literals without a serializer (nothing for the trimmer to analyse).</summary>
internal static class Json
{
    public static string Quote(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";
}
