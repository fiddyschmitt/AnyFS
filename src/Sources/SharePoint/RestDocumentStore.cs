using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// A site's document libraries through SharePoint's REST API, for SharePoint Server and the web session: libraries
/// are the site's lists of the document-library kind, items are files and folders under each library's root folder,
/// all addressed by server-relative URL. A subsite's, another site's, and each of several sites a source was given are
/// reached through that site's own API, by its address (they may be on other servers of a Windows sign-in).
/// </summary>
internal sealed class RestDocumentStore : IDocumentStore
{
    private const string FileSelect = "Name,ServerRelativeUrl,Length,TimeCreated,TimeLastModified,UniqueId,ETag";
    private const string FolderSelect = "Name,ServerRelativeUrl,TimeCreated,TimeLastModified,UniqueId,ItemCount";

    // the client that holds the connections and the sign-in, disposed last; and the source's own site's, which is that
    // client or one for the address the site was found at (a source of several sites has none of its own)
    private readonly SharePointRestClient _owner;
    private readonly SharePointRestClient _rest;
    private readonly ConcurrentDictionary<string, SharePointRestClient> _subsites = new(StringComparer.OrdinalIgnoreCase);
    private readonly PageCache _pages = new();

    // the Markdown of the pages in sites' own folders, by address and version: rendering one is a page load on the server
    private readonly PageCache _sitePages = new();

    // the sites (by key, "" for the source's own) whose own folder the account may not list: one it may only read
    private readonly ConcurrentDictionary<string, bool> _ownFolderRefused = new(StringComparer.OrdinalIgnoreCase);

    // a SharePoint without modern pages has no CanvasContent1 to ask for; found out once, on the first page listing
    private volatile bool _noCanvas;

    private RestDocumentStore(SharePointRestClient owner, SharePointRestClient rest, string description)
    {
        _owner = owner;
        _rest = rest;
        Description = description;
    }

    public string Description { get; }

    public string LibraryNoun => "document library";

    /// <summary>The client for the source's own site (for tests, which make their subsite and page through it).</summary>
    internal SharePointRestClient Rest => _rest;

    /// <summary>Finds the site the address is in (<see cref="FindAsync"/>) and names it.</summary>
    public static async Task<RestDocumentStore> ConnectAsync(SharePointRestClient rest, SiteAddress site, CancellationToken ct)
    {
        var (client, title) = await FindAsync(rest, site, ct).ConfigureAwait(false);
        return new RestDocumentStore(rest, client, $"{title} ({Address(client)})");
    }

    /// <summary>A store for a source of several sites, over this client's connections and sign-in: the source finds them (<see cref="FindSiteAsync"/>).</summary>
    public static RestDocumentStore ForSites(SharePointRestClient rest) => new(rest, rest, "the sites given");

    /// <summary>
    /// One of several sites, found as <see cref="ConnectAsync"/> finds a site but through a probe (<see cref="SharePointRestClient.Probe"/>):
    /// a server that is down leaves its site out without holding the source up or showing it degraded. Its calls later go
    /// through a client of its own (<see cref="Client"/>).
    /// </summary>
    public async Task<WebInfo> FindSiteAsync(SiteAddress site, CancellationToken ct)
    {
        using var probe = _owner.Probe();
        var (found, _) = await FindAsync(probe, site, ct).ConfigureAwait(false);
        var key = Address(found);
        try
        {
            return new WebInfo(key, WebInfo.NameOf(new Uri(key)), key, await IsSiteCollectionAsync(found, key, ct).ConfigureAwait(false));
        }
        finally
        {
            if (found != probe)
            {
                found.Dispose();
            }
        }
    }

    /// <summary>Whether a site is its site collection's own (the site collection's address is the site's), rather than a subsite; null when SharePoint does not say.</summary>
    private static async Task<bool?> IsSiteCollectionAsync(SharePointRestClient client, string address, CancellationToken ct)
    {
        try
        {
            using var doc = await client.GetJsonAsync("site?$select=Url", address, ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("Url", out var url) && url.GetString() is { } collection
                ? string.Equals(collection.TrimEnd('/'), address, StringComparison.OrdinalIgnoreCase)
                : null;
        }
        catch (VfsException)
        {
            return null;
        }
    }

    /// <summary>
    /// The site an address is in, with its title: the address itself, else each shorter one (<see cref="SiteAddress.Candidates"/>),
    /// since SharePoint answers a library's or a folder's address with 404; the address's own answer when none is a site.
    /// </summary>
    private static async Task<(SharePointRestClient Client, string Title)> FindAsync(SharePointRestClient rest, SiteAddress site, CancellationToken ct)
    {
        VfsException? first = null;
        foreach (var candidate in site.Candidates())
        {
            var url = new Uri(candidate.Url.TrimEnd('/') + "/");
            var client = url == rest.SiteUrl ? rest : rest.ForSite(url);
            try
            {
                using var doc = await client.GetJsonAsync("web?$select=Title,Url", client.SiteUrl.ToString(), ct).ConfigureAwait(false);
                var title = doc.RootElement.TryGetProperty("Title", out var t) ? t.GetString() : null;
                return (client, string.IsNullOrEmpty(title) ? Address(client) : title);
            }
            catch (VfsException ex) when (ex.Error == VfsError.NotFound)
            {
                first ??= ex;
                if (client != rest)
                {
                    client.Dispose();
                }
            }
            catch
            {
                if (client != rest)
                {
                    client.Dispose();
                }

                throw;
            }
        }

        throw first ?? VfsException.NotFound(site.Url);
    }

    /// <summary>A site's address as it is keyed and shown: the client's, without the trailing slash.</summary>
    private static string Address(SharePointRestClient client) => client.SiteUrl.AbsoluteUri.TrimEnd('/');

    public void Dispose()
    {
        foreach (var client in _subsites.Values.Append(_rest).Where(c => c != _owner).Distinct())
        {
            client.Dispose();
        }

        _owner.Dispose();
    }

    public async Task<IReadOnlyList<LibraryInfo>> ListLibrariesAsync(WebInfo? web, CancellationToken ct)
    {
        // BaseTemplate 101: document libraries, 119: the site's pages; the hidden ones are SharePoint's own
        using var doc = await Client(web).GetJsonAsync($"web/lists?$filter=(BaseTemplate%20eq%20101%20or%20BaseTemplate%20eq%20{SharePointPages.PageLibraryTemplate})%20and%20Hidden%20eq%20false&$select=Title,BaseTemplate,RootFolder/ServerRelativeUrl&$expand=RootFolder", web?.Key ?? Description, ct).ConfigureAwait(false);
        var libraries = new List<LibraryInfo>();
        foreach (var list in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var title = list.TryGetProperty("Title", out var t) ? t.GetString() : null;
            var root = list.TryGetProperty("RootFolder", out var rf) && rf.TryGetProperty("ServerRelativeUrl", out var sru) ? sru.GetString() : null;
            var pages = list.TryGetProperty("BaseTemplate", out var bt) && bt.TryGetInt32(out var template) && template == SharePointPages.PageLibraryTemplate;
            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(root))
            {
                libraries.Add(new LibraryInfo(root.TrimEnd('/'), title, null, null, web, Pages: pages));
            }
        }

        return libraries;
    }

    // each site's lists' stamps, one call a site, kept a moment so a walk's libraries (stamped together) share it
    private readonly ConcurrentDictionary<string, (long At, Task<Dictionary<string, string>> Stamps)> _listStamps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ListStampsKept = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A library's stamp: its list's <c>LastItemModifiedDate</c> (which moves when any item in it is added, changed or
    /// removed) and <c>ItemCount</c>, from one <c>web/lists</c> call for its site shared by every library of the site for
    /// a moment; null for a library the call does not name.
    /// </summary>
    public async Task<string?> LibraryStampAsync(LibraryInfo library, CancellationToken ct)
    {
        var key = library.Web?.Key ?? "";
        var now = Stopwatch.GetTimestamp();
        if (!_listStamps.TryGetValue(key, out var kept) || Stopwatch.GetElapsedTime(kept.At) > ListStampsKept || kept.Stamps.IsFaulted)
        {
            kept = (now, ListStampsAsync(library.Web, ct));
            _listStamps[key] = kept;
        }

        return (await kept.Stamps.WaitAsync(ct).ConfigureAwait(false)).GetValueOrDefault(library.Key.TrimEnd('/'));
    }

    private async Task<Dictionary<string, string>> ListStampsAsync(WebInfo? web, CancellationToken ct)
    {
        using var doc = await Client(web).GetJsonAsync("web/lists?$select=LastItemModifiedDate,ItemCount,RootFolder/ServerRelativeUrl&$expand=RootFolder", web?.Key ?? Description, ct).ConfigureAwait(false);
        var stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var root = list.TryGetProperty("RootFolder", out var rf) && rf.TryGetProperty("ServerRelativeUrl", out var sru) ? sru.GetString() : null;
            var modified = list.TryGetProperty("LastItemModifiedDate", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var count = list.TryGetProperty("ItemCount", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetRawText() : "";
            if (!string.IsNullOrEmpty(root) && modified is not null)
            {
                stamps[root.TrimEnd('/')] = $"{modified}|{count}";
            }
        }

        return stamps;
    }

    /// <summary>The subsites the account may open, as SharePoint filters them for the current user (-1: of every template and configuration), each by its address.</summary>
    public async Task<IReadOnlyList<WebInfo>> ListSubsitesAsync(WebInfo? web, CancellationToken ct)
    {
        var client = Client(web);
        using var doc = await client.GetJsonAsync("web/getsubwebsfilteredforcurrentuser(nwebtemplatefilter=-1,nconfigurationfilter=-1)", web?.Key ?? Description, ct).ConfigureAwait(false);
        var subsites = new List<WebInfo>();
        foreach (var element in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var url = element.TryGetProperty("ServerRelativeUrl", out var u) ? u.GetString()?.TrimEnd('/') : null;
            if (!string.IsNullOrEmpty(url))
            {
                var address = client.AddressOf(url);
                subsites.Add(new WebInfo(address, url[(url.LastIndexOf('/') + 1)..], address));
            }
        }

        return subsites;
    }

    /// <summary>
    /// The site collections SharePoint's search finds for the account (contentclass STS_Site: what it may open, no
    /// subsites), 500 at a time, on the site's server only, the site itself left out; asked through that site.
    /// </summary>
    public async Task<IReadOnlyList<WebInfo>> ListSitesAsync(WebInfo? web, CancellationToken ct)
    {
        const int Page = 500;
        var client = Client(web);
        var own = client.SiteUrl.AbsolutePath.TrimEnd('/');
        var found = new List<(string, string, string?)>();
        for (var start = 0; start < 100 * Page; start += Page)
        {
            using var doc = await client.GetJsonAsync($"search/query?querytext='contentclass:STS_Site'&selectproperties='Path'&trimduplicates=false&rowlimit={Page}&startrow={start}", web?.Key ?? Description, ct).ConfigureAwait(false);
            if (!(doc.RootElement.TryGetProperty("PrimaryQueryResult", out var primary) && primary.ValueKind == JsonValueKind.Object
                && primary.TryGetProperty("RelevantResults", out var results) && results.ValueKind == JsonValueKind.Object
                && results.TryGetProperty("Table", out var table) && table.ValueKind == JsonValueKind.Object
                && table.TryGetProperty("Rows", out var rows) && rows.ValueKind == JsonValueKind.Array))
            {
                // search answered, but without its results (PrimaryQueryResult null): an error, so the sites known before stay
                throw new VfsException(VfsError.IoError, $"{web?.Url ?? Description}: SharePoint's search answered without results");
            }

            foreach (var row in rows.EnumerateArray())
            {
                var cells = row.TryGetProperty("Cells", out var all) && all.ValueKind == JsonValueKind.Array ? all.EnumerateArray().ToList() : [];
                var path = cells.Where(c => c.TryGetProperty("Key", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == "Path")
                    .Select(c => c.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                    .FirstOrDefault();
                if (Uri.TryCreate(path, UriKind.Absolute, out var site) && string.Equals(site.Authority, client.SiteUrl.Authority, StringComparison.OrdinalIgnoreCase))
                {
                    var serverRelative = Uri.UnescapeDataString(site.AbsolutePath).TrimEnd('/');
                    if (!string.Equals(serverRelative, own, StringComparison.OrdinalIgnoreCase))
                    {
                        var address = client.AddressOf(serverRelative);
                        found.Add((address, serverRelative, address));
                    }
                }
            }

            var total = results.TryGetProperty("TotalRows", out var t) && t.TryGetInt32(out var n) ? n : 0;
            if (rows.GetArrayLength() < Page || start + Page >= total)
            {
                break;
            }
        }

        return WebInfo.Sites(found);
    }

    /// <summary>
    /// The .aspx files in the site's own folder (its root folder, not a list's), each rendered by SharePoint and made
    /// Markdown (<see cref="SharePointPages.MarkdownFromPage"/>), again only when the file changed. One that forwards the
    /// browser elsewhere (a modern site's default.aspx) is left out. An account that may only read the site may not list
    /// that folder (SharePoint's Browse Directories), though it may open the pages in it: for such a site, remembered after
    /// the first refusal, the landing page is looked for by its usual name, default.aspx.
    /// </summary>
    public async Task<IReadOnlyList<SitePage>> ListSitePagesAsync(WebInfo? web, CancellationToken ct)
    {
        var client = Client(web);
        var key = web?.Key ?? "";
        if (_ownFolderRefused.ContainsKey(key))
        {
            return await LandingPageAsync(client, ct).ConfigureAwait(false);
        }

        JsonDocument doc;
        try
        {
            doc = await client.GetJsonAsync($"web/RootFolder/Files?$select={FileSelect}", web?.Key ?? Description, ct).ConfigureAwait(false);
        }
        catch (VfsException ex) when (SharePointRestClient.IsDenial(ex))
        {
            _ownFolderRefused[key] = true;
            return await LandingPageAsync(client, ct).ConfigureAwait(false);
        }

        using var _ = doc;
        var pages = new List<SitePage>();
        foreach (var file in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var (entry, item) = ParseFile(file);
            if (!SharePointPages.IsPage(entry.Name) || !file.TryGetProperty("ServerRelativeUrl", out var u) || u.GetString() is not { } serverRelative)
            {
                continue;
            }

            var address = new Uri(client.AddressOf(serverRelative));
            var markdown = _sitePages.Find(address.AbsoluteUri, item.Version);
            if (markdown is null)
            {
                if (await client.GetPageAsync(address, ct).ConfigureAwait(false) is not { } html)
                {
                    continue;
                }

                markdown = SharePointPages.MarkdownFromPage(html, address);
                _sitePages.Remember(address.AbsoluteUri, item.Version ?? "", markdown);
            }

            pages.Add(new SitePage(SharePointPages.Shown(entry.Name), markdown, entry.Created, entry.Modified));
        }

        return pages;
    }

    /// <summary>A site's default.aspx, for an account that may not list the site's own folder: none where it is not there or forwards elsewhere.</summary>
    private static async Task<IReadOnlyList<SitePage>> LandingPageAsync(SharePointRestClient client, CancellationToken ct)
    {
        const string Name = "default.aspx";
        var address = new Uri(client.SiteUrl, Name);
        try
        {
            return await client.GetPageAsync(address, ct).ConfigureAwait(false) is { } html
                ? [new SitePage(SharePointPages.Shown(Name), SharePointPages.MarkdownFromPage(html, address), null, null)]
                : [];
        }
        catch (VfsException ex) when (ex.Error is VfsError.NotFound || SharePointRestClient.IsDenial(ex))
        {
            return [];
        }
    }

    public async IAsyncEnumerable<(VfsEntry Entry, ItemRef Item)> ListChildrenAsync(LibraryInfo library, string itemPath, [EnumeratorCancellation] CancellationToken ct)
    {
        var folder = Url(library, itemPath);
        using var doc = library.Pages
            ? await PageFolderAsync(library, itemPath, ct).ConfigureAwait(false)
            : await Client(library).GetJsonAsync($"{SharePointRestClient.FolderApi(folder)}?$expand=Folders,Files&$select=Exists,{Prefix("Folders", FolderSelect)},{Prefix("Files", FileSelect)}", Display(library, itemPath), ct).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("Exists", out var exists) && exists.ValueKind == JsonValueKind.False)
        {
            throw VfsException.NotFound(Display(library, itemPath));
        }

        if (doc.RootElement.TryGetProperty("Folders", out var folders))
        {
            foreach (var element in folders.EnumerateArray())
            {
                var name = element.GetProperty("Name").GetString() ?? "";
                if (itemPath.Length == 0 && name.Equals("Forms", StringComparison.OrdinalIgnoreCase))
                {
                    continue;   // the library's own forms folder
                }

                yield return ParseFolder(element);
            }
        }

        if (doc.RootElement.TryGetProperty("Files", out var files))
        {
            foreach (var element in files.EnumerateArray())
            {
                if (!library.Pages)
                {
                    yield return ParseFile(element);
                }
                else if (SharePointPages.IsPage(element.GetProperty("Name").GetString() ?? ""))
                {
                    yield return ParsePage(library, element);
                }
            }
        }
    }

    /// <summary>A folder of the site's pages with each page's content fields, for the Markdown (<see cref="SharePointPages"/>).</summary>
    private async Task<JsonDocument> PageFolderAsync(LibraryInfo library, string itemPath, CancellationToken ct)
    {
        var folder = SharePointRestClient.FolderApi(Url(library, itemPath));
        try
        {
            return await Client(library).GetJsonAsync($"{folder}?$expand=Folders,Files,Files/ListItemAllFields&$select=Exists,{Prefix("Folders", FolderSelect)},{Prefix("Files", FileSelect)},{Prefix("Files/ListItemAllFields", PageFields)}", Display(library, itemPath), ct).ConfigureAwait(false);
        }
        catch (VfsException ex) when (!_noCanvas && ex.Message.Contains("CanvasContent1", StringComparison.Ordinal))
        {
            _noCanvas = true;
            return await PageFolderAsync(library, itemPath, ct).ConfigureAwait(false);
        }
    }

    private string PageFields => _noCanvas ? SharePointPages.WikiFields : SharePointPages.Fields;

    /// <summary>A page as its Markdown file; the Markdown kept for the read that follows.</summary>
    private (VfsEntry Entry, ItemRef Item) ParsePage(LibraryInfo library, JsonElement file)
    {
        var (entry, item) = ParseFile(file);
        var fields = file.TryGetProperty("ListItemAllFields", out var f) ? f : default;
        var serverRelative = file.TryGetProperty("ServerRelativeUrl", out var u) ? u.GetString() : null;
        var pageUrl = serverRelative is null ? null : new Uri(Client(library).SiteUrl, SiteAddress.EscapePath(serverRelative));
        var markdown = SharePointPages.Markdown(fields, pageUrl);
        var version = SharePointPages.Version(markdown);
        _pages.Remember(item.Id, version, markdown);
        return (VfsEntry.File(SharePointPages.Shown(entry.Name), markdown.Length, entry.Modified, entry.Created, VfsAttributes.ReadOnly, version), item with { Version = version, Size = markdown.Length });
    }

    public async Task<ItemRef?> GetItemAsync(LibraryInfo library, string itemPath, CancellationToken ct)
    {
        // in the site's pages a file is a page, by its Markdown name; anything else there is a folder or nothing
        if (library.Pages && SharePointPages.Upstream(itemPath) is { } page)
        {
            using var found = await Client(library).TryGetJsonAsync($"{SharePointRestClient.FileApi(Url(library, page))}?$expand=ListItemAllFields&$select={FileSelect},Exists,{Prefix("ListItemAllFields", PageFields)}", Display(library, itemPath), ct).ConfigureAwait(false);
            if (found is not null && !(found.RootElement.TryGetProperty("Exists", out var pe) && pe.ValueKind == JsonValueKind.False))
            {
                return ParsePage(library, found.RootElement).Item;
            }
        }

        var url = Url(library, itemPath);
        if (!library.Pages)
        {
            using var file = await Client(library).TryGetJsonAsync($"{SharePointRestClient.FileApi(url)}?$select={FileSelect},Exists", Display(library, itemPath), ct).ConfigureAwait(false);
            if (file is not null && !(file.RootElement.TryGetProperty("Exists", out var fe) && fe.ValueKind == JsonValueKind.False))
            {
                return ParseFile(file.RootElement).Item;
            }
        }

        using var folder = await Client(library).TryGetJsonAsync($"{SharePointRestClient.FolderApi(url)}?$select={FolderSelect},Exists", Display(library, itemPath), ct).ConfigureAwait(false);
        if (folder is null || folder.RootElement.TryGetProperty("Exists", out var de) && de.ValueKind == JsonValueKind.False)
        {
            return null;
        }

        return ParseFolder(folder.RootElement).Item;
    }

    public async Task<HttpResponseMessage> OpenContentAsync(LibraryInfo library, string itemPath, ItemRef item, long offset, CancellationToken ct)
    {
        if (!library.Pages)
        {
            return await Client(library).GetContentAsync(Url(library, itemPath), offset, Display(library, itemPath), ct).ConfigureAwait(false);
        }

        // a page is its Markdown: from the listing that sized it, else made again
        var markdown = _pages.Find(item.Id, item.Version);
        if (markdown is null)
        {
            var again = await GetItemAsync(library, itemPath, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(Display(library, itemPath));
            markdown = _pages.Find(again.Id, again.Version) ?? throw VfsException.NotFound(Display(library, itemPath));
        }

        return SharePointPages.Response(markdown, offset);
    }

    public async Task<ItemRef> UploadAsync(LibraryInfo library, string itemPath, string localPath, long length, CancellationToken ct)
    {
        var (parent, name) = VfsPath.SplitLast(itemPath);
        var folder = Url(library, parent);
        var path = Display(library, itemPath);
        var add = $"{SharePointRestClient.FolderApi(folder)}/Files/AddUsingPath(decodedurl='{SharePointRestClient.Encode(name)}',overwrite=true)?$select={FileSelect}";
        if (length <= SharePointRestClient.SimpleUploadLimit)
        {
            using var created = await Client(library).PostBinaryAsync(add, () => OpenLocal(localPath), length, path, ct).ConfigureAwait(false);
            return ParseFile(created.RootElement).Item;
        }

        // an empty file first, then the pieces against it
        using (var empty = await Client(library).PostBinaryAsync(add, () => Stream.Null, 0, path, ct).ConfigureAwait(false))
        {
        }

        var file = SharePointRestClient.FileApi(Url(library, itemPath));
        var uploadId = Guid.NewGuid();
        long offset = 0;
        while (offset < length)
        {
            var chunk = Math.Min(SharePointRestClient.UploadChunkSize, length - offset);
            var last = offset + chunk >= length;
            var api = offset == 0
                ? $"{file}/StartUpload(uploadId=guid'{uploadId}')"
                : last
                    ? $"{file}/FinishUpload(uploadId=guid'{uploadId}',fileOffset={offset})?$select={FileSelect}"
                    : $"{file}/ContinueUpload(uploadId=guid'{uploadId}',fileOffset={offset})";
            var from = offset;
            using var result = await Client(library).PostBinaryAsync(api, () => new SliceStream(OpenLocal(localPath), from, chunk), chunk, path, ct).ConfigureAwait(false);
            offset += chunk;
            if (last)
            {
                return ParseFile(result.RootElement).Item;
            }
        }

        throw new VfsException(VfsError.IoError, $"'{path}': the upload ended without the file");
    }

    public async Task<ItemRef> CreateFolderAsync(LibraryInfo library, string itemPath, CancellationToken ct)
    {
        var url = Url(library, itemPath);
        var (parent, _) = VfsPath.SplitLast(itemPath);
        if (parent.Length > 0 && await GetItemAsync(library, parent, ct).ConfigureAwait(false) is null)
        {
            throw VfsException.NotFound(Display(library, parent));
        }

        using var created = await Client(library).PostJsonAsync($"web/Folders/AddUsingPath(decodedurl='{SharePointRestClient.Encode(url)}')?$select={FolderSelect}", null, Display(library, itemPath), ct).ConfigureAwait(false);
        return ParseFolder(created.RootElement).Item;
    }

    public Task DeleteAsync(LibraryInfo library, string itemPath, bool isFolder, CancellationToken ct)
    {
        var url = Url(library, itemPath);
        return Client(library).DeleteAsync(isFolder ? SharePointRestClient.FolderApi(url) : SharePointRestClient.FileApi(url), Display(library, itemPath), ct);
    }

    public async Task<ItemRef> MoveAsync(LibraryInfo library, string fromPath, string toPath, bool isFolder, CancellationToken ct)
    {
        var from = Url(library, fromPath);
        var to = Url(library, toPath);
        var api = isFolder
            ? $"{SharePointRestClient.FolderApi(from)}/MoveToUsingPath(DecodedUrl='{SharePointRestClient.Encode(to)}')"
            : $"{SharePointRestClient.FileApi(from)}/MoveToUsingPath(DecodedUrl='{SharePointRestClient.Encode(to)}',moveOperations=0)";
        using (var _ = await Client(library).PostJsonAsync(api, null, Display(library, fromPath), ct).ConfigureAwait(false))
        {
        }

        return await GetItemAsync(library, toPath, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(Display(library, toPath));
    }

    /// <summary>
    /// Sets Created and Modified as given, through the client object model's <c>UpdateOverwriteVersion</c>: no
    /// new version, no changed editor. REST cannot: a MERGE ignores those fields, and the form API
    /// (<c>ValidateUpdateListItem</c>) takes the site's regional format at minute precision. SharePoint honours
    /// the values only for a site collection administrator; for anyone else it keeps them as they were, without
    /// a word, as it does for its own clients (18-Oct-2026, Subscription Edition).
    /// </summary>
    public async Task SetTimesAsync(LibraryInfo library, string itemPath, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct)
    {
        var fields = new List<(string Name, DateTimeOffset When)>();
        if (created is { } c)
        {
            fields.Add(("Created", c));
        }

        if (modified is { } m)
        {
            fields.Add(("Modified", m));
        }

        if (fields.Count == 0)
        {
            return;
        }

        var item = await GetItemAsync(library, itemPath, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(Display(library, itemPath));
        var getter = item.IsFolder ? "GetFolderByServerRelativeUrl" : "GetFileByServerRelativeUrl";
        var actions = new StringBuilder("<ObjectPath Id=\"4\" ObjectPathId=\"1\" /><ObjectPath Id=\"5\" ObjectPathId=\"2\" /><ObjectPath Id=\"6\" ObjectPathId=\"3\" />");
        var id = 7;
        foreach (var (name, when) in fields)
        {
            actions.Append("<Method Name=\"SetFieldValue\" Id=\"").Append(id++).Append("\" ObjectPathId=\"3\"><Parameters><Parameter Type=\"String\">").Append(name)
                .Append("</Parameter><Parameter Type=\"DateTime\">").Append(when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append("</Parameter></Parameters></Method>");
        }

        actions.Append("<Method Name=\"UpdateOverwriteVersion\" Id=\"").Append(id).Append("\" ObjectPathId=\"3\" />");
        var xml = "<Request xmlns=\"http://schemas.microsoft.com/sharepoint/clientquery/2009\" SchemaVersion=\"15.0.0.0\" LibraryVersion=\"16.0.0.0\" ApplicationName=\"AnyFS\"><Actions>" + actions
            + "</Actions><ObjectPaths><StaticProperty Id=\"0\" TypeId=\"{3747adcd-a3c3-41b9-bfab-4a64dd2f1e0a}\" Name=\"Current\" /><Property Id=\"1\" ParentId=\"0\" Name=\"Web\" /><Method Id=\"2\" ParentId=\"1\" Name=\"" + getter
            + "\"><Parameters><Parameter Type=\"String\">" + SecurityElement.Escape(Url(library, itemPath)) + "</Parameter></Parameters></Method><Property Id=\"3\" ParentId=\"2\" Name=\"ListItemAllFields\" /></ObjectPaths></Request>";
        await Client(library).PostCsomAsync(xml, Display(library, itemPath), ct).ConfigureAwait(false);
    }

    // --- parsing -------------------------------------------------------------------------------------------

    internal static (VfsEntry Entry, ItemRef Item) ParseFile(JsonElement file)
    {
        var name = file.GetProperty("Name").GetString() ?? "";
        var length = file.TryGetProperty("Length", out var l) && TryLong(l, out var len) ? len : 0;
        var created = ReadTime(file, "TimeCreated");
        var modified = ReadTime(file, "TimeLastModified");
        var id = file.TryGetProperty("UniqueId", out var u) ? u.GetString() ?? "" : "";
        var etag = file.TryGetProperty("ETag", out var e) ? e.GetString() : null;
        return (new VfsEntry(name, false, length, created, modified, VfsAttributes.None, etag), new ItemRef(id, etag, null, length, false));
    }

    internal static (VfsEntry Entry, ItemRef Item) ParseFolder(JsonElement folder)
    {
        var name = folder.GetProperty("Name").GetString() ?? "";
        var created = ReadTime(folder, "TimeCreated");
        var modified = ReadTime(folder, "TimeLastModified");
        var id = folder.TryGetProperty("UniqueId", out var u) ? u.GetString() ?? "" : "";
        return (VfsEntry.Directory(name, modified, created), new ItemRef(id, modified?.ToString("O"), null, 0, true));
    }

    private static bool TryLong(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number ? element.TryGetInt64(out value) : element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out value);
    }

    private static DateTimeOffset? ReadTime(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var time) ? time : null;

    private static string Prefix(string collection, string select) => string.Join(',', select.Split(',').Select(s => $"{collection}/{s}"));

    private static string Url(LibraryInfo library, string itemPath) => itemPath.Length == 0 ? library.Key : library.Key + "/" + itemPath;

    private static string Display(LibraryInfo library, string itemPath) => itemPath.Length == 0 ? library.Shown : library.Shown + "/" + itemPath;

    /// <summary>The client for the source's own site, or for another one by its address (made the first time it is needed).</summary>
    private SharePointRestClient Client(WebInfo? web) => web is null ? _rest : _subsites.GetOrAdd(web.Key, key => _owner.ForSite(new Uri(key + "/")));

    private SharePointRestClient Client(LibraryInfo library) => Client(library.Web);

    private static FileStream OpenLocal(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);

    /// <summary>A window onto a stream: <paramref name="length"/> bytes from <paramref name="offset"/>, read-only, owns the stream.</summary>
    private sealed class SliceStream(Stream inner, long offset, long length) : Stream
    {
        private long _position;
        private bool _positioned;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int o, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int o, int count)
        {
            Position0();
            var n = inner.Read(buffer, o, (int)Math.Min(count, length - _position));
            _position += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Position0();
            var want = (int)Math.Min(buffer.Length, length - _position);
            if (want <= 0)
            {
                return 0;
            }

            var n = await inner.ReadAsync(buffer[..want], cancellationToken).ConfigureAwait(false);
            _position += n;
            return n;
        }

        private void Position0()
        {
            if (!_positioned)
            {
                inner.Seek(offset, SeekOrigin.Begin);
                _positioned = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
