using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnyFS.Sources.Tests;

/// <summary>
/// Microsoft Graph as the tenant answers it (the calls GraphDocumentStore and GraphClient make, and those the live tests
/// make): sites by address (sites/{host}:{path}) and by id, their drives and lists (Site Pages only as a system list),
/// subsites, the tenant's sites by search; drive items by path, children a page at a time (an absolute nextLink),
/// pre-authenticated download addresses that honour a Range, uploads in one PUT up to 4 MiB or in an upload session's
/// pieces, folders made, items renamed, moved, timed and deleted; SharePoint Embedded's containers, listed by container
/// type only, for their direct members.
/// </summary>
internal sealed partial class FakeSharePoint
{
    /// <summary>Where the tenant's download and upload addresses start.</summary>
    private const string GraphTransfers = "/_fakegraph/";

    /// <summary>Graph's limit for a one-request upload.</summary>
    private const int GraphSimpleUploadLimit = 4 * 1024 * 1024;

    /// <summary>An upload session's pieces must be multiples of this, but for the last.</summary>
    private const int GraphChunkUnit = 320 * 1024;

    private readonly List<Library> _containers = [];
    private readonly Dictionary<string, Account> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cachedTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (FileItem File, DateTimeOffset Until)> _downloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GraphUpload> _graphUploads = new(StringComparer.Ordinal);

    /// <summary>Bearer tokens made so far: an account's first, and one for each time the client asked for a new one.</summary>
    public int TokensIssued { get; private set; }

    /// <summary>Of those, the ones the client asked to be new (after a 401), not the one it already had.</summary>
    public int TokensRefreshed { get; private set; }

    /// <summary>How long a download address works (Graph's are pre-authenticated, and expire).</summary>
    public TimeSpan DownloadUrlLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How many children a listing's page holds at most (Graph's own cap is 200; smaller to see the paging).</summary>
    public int GraphPageSize { get; set; } = 200;

    /// <summary>The SharePoint Embedded containers.</summary>
    public IReadOnlyList<Library> Containers => _containers;

    /// <summary>
    /// A bearer token for <paramref name="account"/>, as the sign-in's cache gives it: the one it has, whether or not
    /// Graph still takes it, unless <paramref name="fresh"/> (the client asks for a new one after a 401).
    /// </summary>
    public string IssueToken(string account, bool fresh)
    {
        lock (_gate)
        {
            var known = _accounts.TryGetValue(account, out var a) ? a : throw new InvalidOperationException($"no account {account} here");
            if (!fresh && _cachedTokens.TryGetValue(account, out var cached))
            {
                return cached;
            }

            var token = "fake-token-" + NewSecret(16);
            _tokens[token] = known;
            _cachedTokens[account] = token;
            TokensIssued++;
            if (fresh)
            {
                TokensRefreshed++;
            }

            return token;
        }
    }

    /// <summary>Every bearer token stops working, as when they expire early: 401 until the client asks for a new one.</summary>
    public void ExpireTokens()
    {
        lock (_gate)
        {
            _tokens.Clear();
        }
    }

    /// <summary>Every download address given stops working: 401, until the client asks for the item again.</summary>
    public void ExpireDownloadUrls()
    {
        lock (_gate)
        {
            _downloads.Clear();
        }
    }

    /// <summary>A container of <paramref name="type"/> for <paramref name="members"/>.</summary>
    public Library AddContainer(string name, Guid type, params string[] members)
    {
        lock (_gate)
        {
            var container = new Library(this, web: null, name, name, 101) { ContainerTypeId = type };
            foreach (var member in members)
            {
                container.Members.Add(member);
            }

            _containers.Add(container);
            return container;
        }
    }

    // --- Graph's addresses ------------------------------------------------------------------------------------------

    private Response Graph(Request request)
    {
        var auth = request.Header("Authorization");
        if (auth is null || !auth.StartsWith("Bearer ", StringComparison.Ordinal) || !_tokens.TryGetValue(auth[7..], out var account))
        {
            return GraphJson(401, GraphError("InvalidAuthenticationToken", "Access token has expired or is not yet valid."))
                .With("WWW-Authenticate", "Bearer realm=\"\", authorization_uri=\"https://login.microsoftonline.com/common/oauth2/authorize\", client_id=\"00000003-0000-0000-c000-000000000000\"");
        }

        request.Account = account;
        if (!request.RawPath.StartsWith("/v1.0/", StringComparison.Ordinal))
        {
            return GraphJson(400, GraphError("BadRequest", "Invalid version."));
        }

        try
        {
            return GraphRoute(request, request.RawPath[6..]);
        }
        catch (GraphFault fault)
        {
            return fault.Response;
        }
    }

    private Response GraphRoute(Request request, string path)
    {
        if (path == "sites")
        {
            return Search(request.Account!, request);
        }

        if (path.StartsWith("sites/", StringComparison.Ordinal))
        {
            return GraphSite(request, path[6..]);
        }

        if (path.StartsWith("storage/fileStorage/containers", StringComparison.Ordinal))
        {
            return GraphContainers(request, path["storage/fileStorage/containers".Length..].TrimStart('/'));
        }

        if (path.StartsWith("drives/", StringComparison.Ordinal))
        {
            return GraphDrive(request, path[7..]);
        }

        return GraphJson(400, GraphError("invalidRequest", $"Resource not found for the segment '{path.Split('/')[0]}'."));
    }

    // --- sites ------------------------------------------------------------------------------------------------------

    private Response GraphSite(Request request, string rest)
    {
        var slash = rest.IndexOf('/');
        var key = Uri.UnescapeDataString(slash < 0 ? rest : rest[..slash]);
        var after = slash < 0 ? "" : rest[(slash + 1)..];

        Web? web;
        if (key.Contains(','))
        {
            // by id: host,collection,web
            web = Webs.FirstOrDefault(w => w.GraphId.Equals(key, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // by address: host, or host:path (after the colon to the end, slashes and all)
            var colon = key.IndexOf(':');
            var host = colon < 0 ? key : key[..colon];
            var sitePath = colon < 0 ? "" : key[(colon + 1)..];
            if (colon >= 0 && slash >= 0)
            {
                // the site's path runs on past the first slash: the whole of the rest is the address
                sitePath = Uri.UnescapeDataString(rest[(colon + 1)..]);
                after = "";
            }

            web = host.Equals(Host, StringComparison.OrdinalIgnoreCase) ? WebAt(sitePath) : null;
        }

        if (web is null)
        {
            // a library's or a folder's address is no site: 404, as SharePoint answers
            return GraphJson(404, GraphError("itemNotFound", "Requested site could not be found"));
        }

        if (RoleOf(web, request.Account) < Role.Read)
        {
            return GraphJson(403, GraphError("accessDenied", "Access denied"));
        }

        var parts = after.Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length == 0 ? "" : parts[0])
        {
            case "":
                return GraphJson(200, Select(request, SiteJson(web)));
            case "drives":
                return GraphPaged(request, web.Libraries.Where(l => l.BaseTemplate == 101 && !l.Hidden && l.InGraphDrives).Select(l => (JsonNode)Select(request, DriveJson(l))).ToList());
            case "lists":
                var system = request.Query.TryGetValue("$select", out var select) && Regex.IsMatch(select, @"(^|,)system(,|$)");
                var withDrive = request.Query.TryGetValue("$expand", out var expand) && expand.StartsWith("drive", StringComparison.Ordinal);
                return GraphPaged(request, web.Libraries.Where(l => l.BaseTemplate is 101 or 119 && !l.Hidden && (system || l.InGraphDrives))
                    .Select(l => (JsonNode)ListJson(l, withDrive)).ToList());
            case "sites":
                return GraphPaged(request, web.Subwebs.Where(w => RoleOf(w, request.Account) >= Role.Read).Select(w => (JsonNode)Select(request, SiteJson(w))).ToList());
            case "pages":
                return GraphPages(request, web, parts.Skip(1).ToList());
            default:
                return GraphJson(400, GraphError("invalidRequest", $"Resource not found for the segment '{parts[0]}'."));
        }
    }

    /// <summary>The tenant's sites search finds for the account: every web it may open (the client keeps the site collections).</summary>
    private Response Search(Account account, Request request)
    {
        if (!request.Query.ContainsKey("search"))
        {
            return GraphJson(400, GraphError("invalidRequest", "Listing all sites needs a search."));
        }

        if (!SearchService)
        {
            return GraphJson(503, GraphError("serviceNotAvailable", "Search is not available."));
        }

        var found = Webs.Where(w => w.Collection.Searchable && RoleOf(w, account) >= Role.Read).Select(w => (JsonNode)Select(request, SiteJson(w))).ToList();
        return GraphPaged(request, found);
    }

    private JsonObject SiteJson(Web web)
    {
        var json = new JsonObject
        {
            ["id"] = web.GraphId,
            ["name"] = web.Path.Length == 0 ? "" : web.Leaf,
            ["displayName"] = web.Title,
            ["webUrl"] = web.Url,
            ["createdDateTime"] = GraphTime(DateTimeOffset.UtcNow.AddDays(-30)),
        };
        if (web.IsSiteCollection)
        {
            json["siteCollection"] = new JsonObject { ["hostname"] = Host };
        }

        return json;
    }

    private JsonObject DriveJson(Library library)
    {
        var used = library.Used;
        return new JsonObject
        {
            ["id"] = library.DriveId,
            ["name"] = library.Title,
            ["driveType"] = library.Web is null ? "other" : "documentLibrary",
            ["quota"] = new JsonObject { ["total"] = library.QuotaTotal, ["used"] = used, ["remaining"] = library.QuotaTotal - used, ["deleted"] = 0, ["state"] = "normal" },
        };
    }

    private static JsonObject ListJson(Library library, bool withDrive)
    {
        var json = new JsonObject
        {
            ["id"] = library.Id.ToString(),
            ["displayName"] = library.Title,
            ["lastModifiedDateTime"] = GraphTime(library.LastItemModified),
            ["list"] = new JsonObject { ["template"] = library.Pages ? "webPageLibrary" : "documentLibrary", ["hidden"] = library.Hidden },
        };
        if (!library.InGraphDrives)
        {
            json["system"] = new JsonObject();
        }

        if (withDrive)
        {
            json["drive"] = new JsonObject { ["id"] = library.DriveId };
        }

        return json;
    }

    // --- pages: the API the live tests make their page with ---------------------------------------------------------

    private Response GraphPages(Request request, Web web, List<string> parts)
    {
        var library = web.Libraries.FirstOrDefault(l => l.Pages) ?? throw new GraphFault(GraphJson(404, GraphError("itemNotFound", "The site has no pages.")));
        if (parts.Count == 0 && request.Method == "GET")
        {
            return GraphJson(200, new JsonObject
            {
                ["value"] = new JsonArray(library.Root.Children.OfType<FileItem>().Select(p => (JsonNode)new JsonObject { ["id"] = p.UniqueId.ToString(), ["name"] = p.Name, ["title"] = p.Title }).ToArray()),
            });
        }

        if (parts.Count == 0 && request.Method == "POST")
        {
            var body = JsonNode.Parse(request.Body)!;
            var name = body["name"]?.GetValue<string>() ?? "Untitled.aspx";
            var texts = body["canvasLayout"]?["horizontalSections"]?.AsArray()
                .SelectMany(s => s?["columns"]?.AsArray() ?? [])
                .SelectMany(c => c?["webparts"]?.AsArray() ?? [])
                .Select(w => w?["innerHtml"]?.GetValue<string>() ?? "")
                .ToList() ?? [];
            var page = library.Root.AddFile(name, System.Text.Encoding.UTF8.GetBytes("<%@ Page %>"));
            page.Title = body["title"]?.GetValue<string>();
            page.CanvasContent1 = CanvasHtml(texts);
            return GraphJson(201, new JsonObject { ["id"] = page.UniqueId.ToString(), ["name"] = page.Name, ["title"] = page.Title });
        }

        if (parts.Count == 3 && parts[1] == "microsoft.graph.sitePage" && parts[2] == "publish")
        {
            return Response.Empty(204);
        }

        return GraphJson(400, GraphError("invalidRequest", "Not a pages request this fake knows."));
    }

    // --- SharePoint Embedded's containers ---------------------------------------------------------------------------

    private Response GraphContainers(Request request, string rest)
    {
        if (rest.Length == 0)
        {
            // listed by container type only (the filter is mandatory), the account's direct memberships, the active ones
            if (!request.Query.TryGetValue("$filter", out var filter) || Regex.Match(filter, @"containerTypeId\s+eq\s+([0-9a-fA-F-]{36})") is not { Success: true } match)
            {
                return GraphJson(400, GraphError("invalidRequest", "The filter on containerTypeId is required."));
            }

            var type = Guid.Parse(match.Groups[1].Value);
            var account = request.Account!;
            var listed = account.HasOneDrive
                ? _containers.Where(c => c.ContainerTypeId == type && c.Members.Contains(account.Name)).Select(c => (JsonNode)ContainerJson(c)).ToList()
                : [];
            return GraphPaged(request, listed);
        }

        var id = Uri.UnescapeDataString(rest);
        var container = _containers.FirstOrDefault(c => c.DriveId == id);
        if (container is null || !container.Members.Contains(request.Account!.Name))
        {
            return GraphJson(404, GraphError("itemNotFound", "The requested container was not found."));
        }

        return GraphJson(200, ContainerJson(container));
    }

    private static JsonObject ContainerJson(Library container) => new()
    {
        ["id"] = container.DriveId,
        ["displayName"] = container.Title,
        ["containerTypeId"] = container.ContainerTypeId.ToString(),
        ["status"] = container.Status,
        ["createdDateTime"] = GraphTime(container.Root.Created),
    };

    // --- drives and their items -------------------------------------------------------------------------------------

    private Library? FindDrive(string id)
        => _containers.FirstOrDefault(c => c.DriveId == id) ?? Webs.SelectMany(w => w.Libraries).FirstOrDefault(l => l.DriveId == id);

    private Response GraphDrive(Request request, string rest)
    {
        var slash = rest.IndexOf('/');
        var driveId = Uri.UnescapeDataString(slash < 0 ? rest : rest[..slash]);
        var library = FindDrive(driveId);
        if (library is null)
        {
            return GraphJson(404, GraphError("itemNotFound", "The drive could not be found."));
        }

        var role = library.Web is { } web ? RoleOf(web, request.Account) : library.Members.Contains(request.Account!.Name) ? Role.Edit : Role.None;
        if (role < Role.Read)
        {
            return GraphJson(403, GraphError("accessDenied", "Access denied"));
        }

        // root, root:/a/b:, items/{id}; then /children, /content, /createUploadSession, or nothing
        var after = slash < 0 ? "" : rest[(slash + 1)..];
        Item? item;
        string? missingPath = null;
        string suffix;
        if (after.StartsWith("root:", StringComparison.Ordinal))
        {
            var close = after.IndexOf(':', 5);
            var itemPath = Uri.UnescapeDataString(close < 0 ? after[5..] : after[5..close]).Trim('/');
            suffix = close < 0 ? "" : after[(close + 1)..];
            item = Locate(library.Root, itemPath);
            missingPath = item is null ? itemPath : null;
        }
        else if (after.StartsWith("root", StringComparison.Ordinal))
        {
            item = library.Root;
            suffix = after[4..];
        }
        else if (after.StartsWith("items/", StringComparison.Ordinal))
        {
            var end = after.IndexOf('/', 6);
            var itemId = end < 0 ? after[6..] : after[6..end];
            item = Everything(library.Root).FirstOrDefault(i => i.GraphId == itemId);
            suffix = end < 0 ? "" : after[end..];
        }
        else if (after == "list" && request.Method == "GET")
        {
            // the drive's list: what the source reads a library's stamp from
            return GraphJson(200, Select(request, ListJson(library, false)));
        }
        else
        {
            return GraphJson(400, GraphError("invalidRequest", "Not a drive request this fake knows."));
        }

        suffix = suffix.TrimStart('/');
        var write = request.Method is "PUT" or "POST" or "PATCH" or "DELETE";
        if (write && role < Role.Edit)
        {
            return GraphJson(403, GraphError("accessDenied", "Access denied"));
        }

        switch (suffix, request.Method)
        {
            case ("", "GET"):
                return item is null ? ItemNotFound() : GraphJson(200, ItemJson(request, item, select: request.Query.TryGetValue("$select", out var s) ? s : null));
            case ("children", "GET"):
                return item is FolderItem folder ? GraphPaged(request, folder.Children.Select(c => (JsonNode)ItemJson(request, c, request.Query.TryGetValue("$select", out var cs) ? cs : null)).ToList(), $"drives/{library.DriveId}/items/{folder.GraphId}/children") : ItemNotFound();
            case ("children", "POST"):
                return item is FolderItem parent ? CreateFolder(request, parent) : ItemNotFound();
            case ("content", "PUT"):
                return PutContent(request, library, item, missingPath);
            case ("createUploadSession", "POST"):
                return CreateUploadSession(library, item, missingPath);
            case ("", "PATCH"):
                return item is null ? ItemNotFound() : Patch(request, library, item);
            case ("", "DELETE"):
                if (item is null || item.Parent is null)
                {
                    return ItemNotFound();
                }

                item.Parent.Children.Remove(item);
                item.Parent.Touch();
                return Response.Empty(204);
            default:
                return GraphJson(400, GraphError("invalidRequest", $"{request.Method} {suffix} is not a request this fake knows."));
        }
    }

    private static Item? Locate(FolderItem root, string path)
    {
        Item? item = root;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            item = (item as FolderItem)?.Child(part);
            if (item is null)
            {
                return null;
            }
        }

        return item;
    }

    private static IEnumerable<Item> Everything(FolderItem folder)
    {
        yield return folder;
        foreach (var child in folder.Children)
        {
            if (child is FolderItem sub)
            {
                foreach (var item in Everything(sub))
                {
                    yield return item;
                }
            }
            else
            {
                yield return child;
            }
        }
    }

    private Response CreateFolder(Request request, FolderItem parent)
    {
        var body = JsonNode.Parse(request.Body)!;
        var name = body["name"]!.GetValue<string>();
        if (!ValidName(name))
        {
            return GraphJson(400, GraphError("invalidRequest", $"The provided name contains invalid character '{name.FirstOrDefault(c => "\"*:<>?/\\|".Contains(c))}'"));
        }

        if (parent.Child(name) is not null)
        {
            if (body["@microsoft.graph.conflictBehavior"]?.GetValue<string>() is "fail" or null)
            {
                return GraphJson(409, GraphError("nameAlreadyExists", "An item with the same name already exists under the parent"));
            }
        }

        return GraphJson(201, ItemJson(request, parent.AddFolder(name), select: null, downloadUrl: false));
    }

    private Response PutContent(Request request, Library library, Item? item, string? missingPath)
    {
        if (request.Body.Length > GraphSimpleUploadLimit)
        {
            return GraphJson(413, GraphError("requestEntityTooLarge", "The request is larger than 4 MB: use an upload session."));
        }

        if (item is FolderItem)
        {
            return GraphJson(409, GraphError("nameAlreadyExists", "A folder has that name."));
        }

        if (item is FileItem existing)
        {
            if (existing.Locked)
            {
                return GraphJson(423, GraphError("resourceLocked", "The resource you are attempting to access is locked"));
            }

            existing.Replace(request.Body);
            return GraphJson(200, ItemJson(request, existing, select: null));
        }

        var (parent, name) = ParentOf(library, missingPath!);
        if (parent is null)
        {
            return ItemNotFound();
        }

        if (!ValidName(name))
        {
            return GraphJson(400, GraphError("invalidRequest", "The provided name contains invalid characters."));
        }

        return GraphJson(201, ItemJson(request, parent.AddFile(name, request.Body), select: null));
    }

    private (FolderItem? Parent, string Name) ParentOf(Library library, string path)
    {
        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? library.Root : Locate(library.Root, path[..slash]) as FolderItem;
        return (parent, slash < 0 ? path : path[(slash + 1)..]);
    }

    private Response CreateUploadSession(Library library, Item? item, string? missingPath)
    {
        if (item is FolderItem)
        {
            return GraphJson(409, GraphError("nameAlreadyExists", "A folder has that name."));
        }

        FolderItem? parent;
        string name;
        if (item is FileItem existing)
        {
            (parent, name) = (existing.Parent, existing.Name);
        }
        else
        {
            (parent, name) = ParentOf(library, missingPath!);
        }

        if (parent is null)
        {
            return ItemNotFound();
        }

        var id = NewSecret(16);
        _graphUploads[id] = new GraphUpload(parent, name);
        return GraphJson(200, new JsonObject
        {
            ["uploadUrl"] = $"{Origin}{GraphTransfers}upload/{id}",
            ["expirationDateTime"] = GraphTime(DateTimeOffset.UtcNow.AddDays(3)),
            ["nextExpectedRanges"] = new JsonArray("0-"),
        });
    }

    private Response Patch(Request request, Library library, Item item)
    {
        var body = JsonNode.Parse(request.Body)!;
        if (body["fileSystemInfo"] is JsonObject times)
        {
            // Graph sets the times for anyone who may write, as fileSystemInfo
            if (times["createdDateTime"]?.GetValue<string>() is { } created)
            {
                item.Created = DateTimeOffset.Parse(created, CultureInfo.InvariantCulture).ToUniversalTime();
            }

            if (times["lastModifiedDateTime"]?.GetValue<string>() is { } modified)
            {
                item.Modified = DateTimeOffset.Parse(modified, CultureInfo.InvariantCulture).ToUniversalTime();
            }

            item.Version++;
        }

        if (body["name"]?.GetValue<string>() is { } name)
        {
            var target = item.Parent!;
            if (body["parentReference"]?["id"]?.GetValue<string>() is { } parentId)
            {
                target = Everything(library.Root).OfType<FolderItem>().FirstOrDefault(f => f.GraphId == parentId) ?? throw new GraphFault(ItemNotFound());
            }

            if (!ValidName(name))
            {
                return GraphJson(400, GraphError("invalidRequest", "The provided name contains invalid characters."));
            }

            if (target.Child(name) is { } there && there != item)
            {
                return GraphJson(409, GraphError("nameAlreadyExists", "Name already exists"));
            }

            item.Parent!.Children.Remove(item);
            item.Parent.Touch();
            item.Name = name;
            item.Parent = target;
            target.Children.Add(item);
            target.Touch();
            item.Version++;
        }

        return GraphJson(200, ItemJson(request, item, select: null, downloadUrl: false));
    }

    // --- download and upload addresses ------------------------------------------------------------------------------

    private Response GraphTransfer(Request request)
    {
        var rest = request.RawPath[GraphTransfers.Length..];
        if (rest.StartsWith("download/", StringComparison.Ordinal))
        {
            // pre-authenticated: no bearer, the address itself is the permission, until it expires
            if (!_downloads.TryGetValue(rest[9..], out var download) || download.Until < DateTimeOffset.UtcNow)
            {
                return GraphJson(401, GraphError("unauthenticated", "The download address has expired."));
            }

            return Bytes(request, download.File.Content);
        }

        if (rest.StartsWith("upload/", StringComparison.Ordinal))
        {
            var id = rest[7..];
            if (!_graphUploads.TryGetValue(id, out var upload))
            {
                return GraphJson(404, GraphError("itemNotFound", "The upload session was not found."));
            }

            if (request.Method == "DELETE")
            {
                _graphUploads.Remove(id);
                return Response.Empty(204);
            }

            return UploadPiece(request, id, upload);
        }

        return Response.Empty(404);
    }

    private Response UploadPiece(Request request, string id, GraphUpload upload)
    {
        var range = Regex.Match(request.Header("Content-Range") ?? "", @"^bytes (\d+)-(\d+)/(\d+)$");
        if (!range.Success)
        {
            return GraphJson(400, GraphError("invalidRequest", "The Content-Range header is missing or malformed."));
        }

        var from = long.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
        var to = long.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
        var total = long.Parse(range.Groups[3].Value, CultureInfo.InvariantCulture);
        if (to - from + 1 != request.Body.Length)
        {
            return GraphJson(400, GraphError("invalidRequest", "The Content-Range header length does not match the provided number of bytes."));
        }

        var last = to + 1 == total;
        if (!last && request.Body.Length % GraphChunkUnit != 0)
        {
            return GraphJson(400, GraphError("invalidRequest", "An upload session's pieces must be a multiple of 320 KiB."));
        }

        if (from == upload.Data.Length)
        {
            upload.Data.Write(request.Body);
        }
        else if (from + request.Body.Length != upload.Data.Length)
        {
            // a piece sent again after a failure is taken as it was; one out of order is refused
            return GraphJson(416, GraphError("invalidRange", "The uploaded fragment overlaps with data that has already been received."));
        }

        if (!last)
        {
            return GraphJson(202, new JsonObject
            {
                ["expirationDateTime"] = GraphTime(DateTimeOffset.UtcNow.AddDays(3)),
                ["nextExpectedRanges"] = new JsonArray($"{upload.Data.Length}-"),
            });
        }

        _graphUploads.Remove(id);
        var existing = upload.Parent.File(upload.Name);
        var file = upload.Parent.AddFile(upload.Name, upload.Data.ToArray());
        return GraphJson(existing is null ? 201 : 200, ItemJson(request, file, select: null, downloadUrl: false));
    }

    /// <summary>A file's bytes from a Range (206; 416 past the end), or whole.</summary>
    private Response Bytes(Request request, byte[] bytes)
    {
        var range = request.Header("Range");
        if (range is not null && HonoursRanges && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = range[6..].Split('-');
            var from = long.Parse(spec[0], CultureInfo.InvariantCulture);
            var to = spec.Length > 1 && spec[1].Length > 0 ? Math.Min(long.Parse(spec[1], CultureInfo.InvariantCulture), bytes.LongLength - 1) : bytes.LongLength - 1;
            if (from >= bytes.LongLength)
            {
                return Response.Empty(416).With("Content-Range", $"bytes */{bytes.LongLength}");
            }

            return Response.Bytes(206, bytes[(int)from..(int)(to + 1)]).With("Content-Range", $"bytes {from}-{to}/{bytes.LongLength}");
        }

        return Response.Bytes(200, bytes);
    }

    // --- JSON as Graph gives it -------------------------------------------------------------------------------------

    private static string GraphTime(DateTimeOffset when) => when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>A driveItem; a file's download address only when nothing was selected (it is an annotation that comes with the whole item).</summary>
    private JsonObject ItemJson(Request request, Item item, string? select, bool downloadUrl = true)
    {
        var json = new JsonObject
        {
            ["id"] = item.GraphId,
            ["name"] = item.Parent is null ? "root" : item.Name,
            ["eTag"] = item.GraphETag,
            ["createdDateTime"] = GraphTime(item.Created),
            ["lastModifiedDateTime"] = GraphTime(item.Modified),
            ["webUrl"] = item.Library?.Web is null ? $"{Origin}/contentstorage/{item.Library?.DriveId}/{Uri.EscapeDataString(item.RelativePath)}" : Origin + string.Join('/', item.ServerRelativeUrl.Split('/').Select(Uri.EscapeDataString)),
            ["fileSystemInfo"] = new JsonObject { ["createdDateTime"] = GraphTime(item.Created), ["lastModifiedDateTime"] = GraphTime(item.Modified) },
            ["parentReference"] = new JsonObject { ["driveId"] = item.Library?.DriveId, ["driveType"] = "documentLibrary", ["id"] = item.Parent?.GraphId },
        };
        switch (item)
        {
            case FolderItem folder:
                json["size"] = folder.Size;
                json["folder"] = new JsonObject { ["childCount"] = folder.Children.Count };
                break;
            case FileItem file:
                json["size"] = file.Content.LongLength;
                json["cTag"] = file.GraphCTag;
                json["file"] = new JsonObject { ["mimeType"] = "application/octet-stream" };
                if (select is null && downloadUrl)
                {
                    var token = NewSecret(24);
                    _downloads[token] = (file, DateTimeOffset.UtcNow + DownloadUrlLifetime);
                    json["@microsoft.graph.downloadUrl"] = $"{Origin}{GraphTransfers}download/{token}";
                }

                if (request.Query.TryGetValue("$expand", out var expand) && expand.StartsWith("listItem", StringComparison.Ordinal))
                {
                    var fields = new JsonObject { ["Title"] = file.Title, ["WikiField"] = file.WikiField };
                    if (ModernPages)
                    {
                        fields["CanvasContent1"] = file.CanvasContent1;
                    }

                    json["listItem"] = new JsonObject { ["id"] = PageId(file).ToString(CultureInfo.InvariantCulture), ["fields"] = fields };
                }

                break;
        }

        return select is null ? json : Select(select, json);
    }

    /// <summary>Only the properties $select names (with the listItem and annotations it brings).</summary>
    private static JsonObject Select(Request request, JsonObject json) => request.Query.TryGetValue("$select", out var select) ? Select(select, json) : json;

    private static JsonObject Select(string select, JsonObject json)
    {
        var wanted = select.Split(',', StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var kept = new JsonObject();
        foreach (var (name, value) in json.ToList())
        {
            if (wanted.Contains(name) || name == "listItem" || name.StartsWith('@'))
            {
                json.Remove(name);
                kept[name] = value;
            }
        }

        return kept;
    }

    /// <summary>A collection a page at a time: <see cref="GraphPageSize"/> (or $top, if fewer) each, the next page's absolute address in @odata.nextLink.</summary>
    private Response GraphPaged(Request request, List<JsonNode> all, string? pathForNext = null)
    {
        var size = request.Query.TryGetValue("$top", out var top) && int.TryParse(top, CultureInfo.InvariantCulture, out var t) ? Math.Min(t, GraphPageSize) : GraphPageSize;
        var skip = request.Query.TryGetValue("$skiptoken", out var token) && int.TryParse(token, CultureInfo.InvariantCulture, out var k) ? k : 0;
        var page = all.Skip(skip).Take(size).ToList();
        var json = new JsonObject { ["value"] = new JsonArray(page.Select(n => n.DeepClone()).ToArray()) };
        if (skip + size < all.Count)
        {
            var query = string.Join('&', request.RawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith("%24skiptoken=", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("$skiptoken=", StringComparison.OrdinalIgnoreCase)));
            var path = pathForNext ?? request.RawPath["/v1.0/".Length..];
            json["@odata.nextLink"] = $"https://{request.Host}/v1.0/{path}?{(query.Length == 0 ? "" : query + "&")}$skiptoken={skip + size}";
        }

        return GraphJson(200, json);
    }

    private static Response GraphJson(int status, JsonNode json) => Response.Json(status, json, "application/json; odata.metadata=minimal; odata.streaming=true; IEEE754Compatible=false; charset=utf-8");

    private static Response ItemNotFound() => GraphJson(404, GraphError("itemNotFound", "The resource could not be found."));

    /// <summary>Graph's error body.</summary>
    public static JsonObject GraphError(string code, string message) => new()
    {
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
            ["innerError"] = new JsonObject { ["date"] = GraphTime(DateTimeOffset.UtcNow), ["request-id"] = Guid.NewGuid().ToString(), ["client-request-id"] = Guid.NewGuid().ToString() },
        },
    };

    private sealed class GraphFault(Response response) : Exception
    {
        public Response Response { get; } = response;
    }

    private sealed class GraphUpload(FolderItem parent, string name)
    {
        public FolderItem Parent { get; } = parent;

        public string Name { get; } = name;

        public MemoryStream Data { get; } = new();
    }
}
