using System.Collections.Concurrent;
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
/// all addressed by server-relative URL. A subsite's are reached through that subsite's own API.
/// </summary>
internal sealed class RestDocumentStore : IDocumentStore
{
    private const string FileSelect = "Name,ServerRelativeUrl,Length,TimeCreated,TimeLastModified,UniqueId,ETag";
    private const string FolderSelect = "Name,ServerRelativeUrl,TimeCreated,TimeLastModified,UniqueId,ItemCount";

    private readonly SharePointRestClient _rest;
    private readonly ConcurrentDictionary<string, SharePointRestClient> _subsites = new(StringComparer.OrdinalIgnoreCase);

    private RestDocumentStore(SharePointRestClient rest, string description)
    {
        _rest = rest;
        Description = description;
    }

    public string Description { get; }

    public string LibraryNoun => "document library";

    /// <summary>The client for the source's own site (for tests, which make their subsite through it).</summary>
    internal SharePointRestClient Rest => _rest;

    /// <summary>Checks the site answers and names it.</summary>
    public static async Task<RestDocumentStore> ConnectAsync(SharePointRestClient rest, CancellationToken ct)
    {
        using var doc = await rest.GetJsonAsync("web?$select=Title,Url,ServerRelativeUrl", rest.SiteUrl.ToString(), ct).ConfigureAwait(false);
        var title = doc.RootElement.TryGetProperty("Title", out var t) ? t.GetString() : null;
        var url = doc.RootElement.TryGetProperty("Url", out var u) ? u.GetString() : null;
        return new RestDocumentStore(rest, $"{title ?? url ?? rest.SiteUrl.ToString()} ({url ?? rest.SiteUrl.ToString()})");
    }

    public void Dispose()
    {
        foreach (var subsite in _subsites.Values)
        {
            subsite.Dispose();
        }

        _rest.Dispose();
    }

    public async Task<IReadOnlyList<LibraryInfo>> ListLibrariesAsync(WebInfo? web, CancellationToken ct)
    {
        // BaseTemplate 101: document libraries; the hidden ones are SharePoint's own (Form Templates and the like)
        using var doc = await Client(web).GetJsonAsync("web/lists?$filter=BaseTemplate%20eq%20101%20and%20Hidden%20eq%20false&$select=Title,RootFolder/ServerRelativeUrl&$expand=RootFolder", web?.Key ?? Description, ct).ConfigureAwait(false);
        var libraries = new List<LibraryInfo>();
        foreach (var list in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var title = list.TryGetProperty("Title", out var t) ? t.GetString() : null;
            var root = list.TryGetProperty("RootFolder", out var rf) && rf.TryGetProperty("ServerRelativeUrl", out var sru) ? sru.GetString() : null;
            if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(root))
            {
                libraries.Add(new LibraryInfo(root.TrimEnd('/'), title, null, null, web));
            }
        }

        return libraries;
    }

    /// <summary>The subsites the account may open, as SharePoint filters them for the current user (-1: of every template and configuration).</summary>
    public async Task<IReadOnlyList<WebInfo>> ListSubsitesAsync(WebInfo? web, CancellationToken ct)
    {
        using var doc = await Client(web).GetJsonAsync("web/getsubwebsfilteredforcurrentuser(nwebtemplatefilter=-1,nconfigurationfilter=-1)", web?.Key ?? Description, ct).ConfigureAwait(false);
        var subsites = new List<WebInfo>();
        foreach (var element in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var url = element.TryGetProperty("ServerRelativeUrl", out var u) ? u.GetString()?.TrimEnd('/') : null;
            if (!string.IsNullOrEmpty(url))
            {
                subsites.Add(new WebInfo(url, url[(url.LastIndexOf('/') + 1)..]));
            }
        }

        return subsites;
    }

    public async IAsyncEnumerable<(VfsEntry Entry, ItemRef Item)> ListChildrenAsync(LibraryInfo library, string itemPath, [EnumeratorCancellation] CancellationToken ct)
    {
        var folder = Url(library, itemPath);
        var api = $"{SharePointRestClient.FolderApi(folder)}?$expand=Folders,Files&$select=Exists,{Prefix("Folders", FolderSelect)},{Prefix("Files", FileSelect)}";
        using var doc = await Client(library).GetJsonAsync(api, Display(library, itemPath), ct).ConfigureAwait(false);
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
                yield return ParseFile(element);
            }
        }
    }

    public async Task<ItemRef?> GetItemAsync(LibraryInfo library, string itemPath, CancellationToken ct)
    {
        var url = Url(library, itemPath);
        using (var file = await Client(library).TryGetJsonAsync($"{SharePointRestClient.FileApi(url)}?$select={FileSelect},Exists", Display(library, itemPath), ct).ConfigureAwait(false))
        {
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

    public Task<HttpResponseMessage> OpenContentAsync(LibraryInfo library, string itemPath, ItemRef item, long offset, CancellationToken ct)
        => Client(library).GetContentAsync(Url(library, itemPath), offset, Display(library, itemPath), ct);

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

    /// <summary>The client for the source's own site, or for a subsite (made the first time it is needed).</summary>
    private SharePointRestClient Client(WebInfo? web) => web is null ? _rest : _subsites.GetOrAdd(web.Key, _rest.ForSite);

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
