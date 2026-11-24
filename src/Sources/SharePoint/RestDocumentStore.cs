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

    // each site's lists as they stand, one call a site, kept a moment so a walk's libraries (stamped together) share it
    private readonly Dictionary<string, ListStates> _listStamps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ListStampsKept = TimeSpan.FromSeconds(3);

    // the sites (by key) whose SharePoint tells no change token of a list: found out once each, and their libraries then
    // go by their stamps alone
    private readonly ConcurrentDictionary<string, bool> _noChangeTokens = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One answer of a site's lists, and the libraries a walk has gone by it for.</summary>
    private sealed class ListStates(Task<Dictionary<string, ListState>> lists)
    {
        public long At { get; } = Stopwatch.GetTimestamp();

        public Task<Dictionary<string, ListState>> Lists { get; } = lists;

        public HashSet<string> GoneBy { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Usable => Stopwatch.GetElapsedTime(At) <= ListStampsKept && !Lists.IsFaulted && !Lists.IsCanceled;
    }

    /// <summary>A list as <c>web/lists</c> tells it: its stamp (its last item change and its count), its id, and its change log's token for now where the server tells one.</summary>
    private sealed record ListState(string Stamp, string Id, string? ChangeToken);

    /// <summary>A change log is kept for a time (sixty days, as SharePoint Server is set up; the service does not say). A token older than this is not asked of it: one from before the log begins is answered with what the log has, and no word that more happened.</summary>
    private static readonly TimeSpan ChangeLogTrusted = TimeSpan.FromDays(30);

    /// <summary>
    /// A list's stamp names its last item change to the second, so a change in that same second, after the stamp was
    /// read, leaves it as it was. The stamp is believed to say "nothing changed" only once the token it is held with was
    /// taken this long after that change (well past the second, and past any difference between the clocks of the
    /// servers that write the two); until then the change log itself is asked.
    /// </summary>
    internal static readonly TimeSpan StampBelievedAfter = TimeSpan.FromMinutes(1);

    // how many changes the log gives at once at the least: fewer in an answer, and that was all of them
    private const int ChangesAtOnce = 1000;

    // how many items are asked where they are in one query: as many as a query's In takes (asked of a library of 5,151
    // items, above the view threshold, on 24-Nov-2026: 500 numbers answered in 0.3 s)
    private const int PlacedAtOnce = 500;

    /// <summary>
    /// A library's stamp: its list's <c>LastItemModifiedDate</c> (which moves when any item in it is added, changed or
    /// removed) and <c>ItemCount</c>, from one <c>web/lists</c> call for its site shared by every library of the site for
    /// a moment; null for a library the call does not name.
    /// </summary>
    public async Task<string?> LibraryStampAsync(LibraryInfo library, CancellationToken ct)
        => (await ListStateAsync(library, goesBy: true, held: null, ct).ConfigureAwait(false))?.Stamp;

    /// <summary>
    /// A library's list as its site's lists say it now: from the answer the site's other libraries were just given,
    /// where there is one (a walk stamps a site's libraries together, and one call covers them all). A new answer is
    /// asked for a library that has gone by that one before (<paramref name="goesBy"/>: it is the next walk asking), and
    /// for one whose token held (<paramref name="held"/>) is that answer's own, which cannot say what happened after it.
    /// </summary>
    private async Task<ListState?> ListStateAsync(LibraryInfo library, bool goesBy, string? held, CancellationToken ct)
    {
        var site = library.Web?.Key ?? "";
        var mine = library.Key.TrimEnd('/');
        ListStates? kept;
        lock (_listStamps)
        {
            if (!_listStamps.TryGetValue(site, out kept) || !kept.Usable || kept.GoneBy.Contains(mine))
            {
                kept = null;
            }
        }

        if (kept is not null)
        {
            var state = (await kept.Lists.WaitAsync(ct).ConfigureAwait(false)).GetValueOrDefault(mine);
            if (held is null || state?.ChangeToken != held)
            {
                if (goesBy)
                {
                    lock (_listStamps)
                    {
                        kept.GoneBy.Add(mine);
                    }
                }

                return state;
            }
        }

        lock (_listStamps)
        {
            // another of the site's libraries may have asked meanwhile: its answer is as new
            if (!_listStamps.TryGetValue(site, out var latest) || ReferenceEquals(latest, kept) || !latest.Usable || latest.GoneBy.Contains(mine))
            {
                latest = new ListStates(ListStatesAsync(library.Web, ct));
                _listStamps[site] = latest;
            }

            if (goesBy)
            {
                latest.GoneBy.Add(mine);
            }

            kept = latest;
        }

        return (await kept.Lists.WaitAsync(ct).ConfigureAwait(false)).GetValueOrDefault(mine);
    }

    private async Task<Dictionary<string, ListState>> ListStatesAsync(WebInfo? web, CancellationToken ct)
    {
        const string Select = "web/lists?$select=Id,LastItemModifiedDate,ItemCount,{0}RootFolder/ServerRelativeUrl&$expand=RootFolder";
        var site = web?.Key ?? "";
        var tokens = !_noChangeTokens.ContainsKey(site);
        JsonDocument doc;
        try
        {
            doc = await Client(web).GetJsonAsync(string.Format(CultureInfo.InvariantCulture, Select, tokens ? "CurrentChangeToken," : ""), web?.Key ?? Description, ct).ConfigureAwait(false);
        }
        catch (VfsException ex) when (tokens && ex.Error is not (VfsError.Unavailable or VfsError.AccessDenied or VfsError.NotFound))
        {
            // a SharePoint that knows no such property of a list (it says so, as for any name it does not know), or
            // that will not tell it: asked without it, and where that is answered the site's libraries go by their
            // stamps from here on, as they did
            doc = await Client(web).GetJsonAsync(string.Format(CultureInfo.InvariantCulture, Select, ""), web?.Key ?? Description, ct).ConfigureAwait(false);
            _noChangeTokens[site] = true;
        }

        using (doc)
        {
            var lists = new Dictionary<string, ListState>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var root = list.TryGetProperty("RootFolder", out var rf) && rf.TryGetProperty("ServerRelativeUrl", out var sru) ? sru.GetString() : null;
                var modified = list.TryGetProperty("LastItemModifiedDate", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                var count = list.TryGetProperty("ItemCount", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetRawText() : "";
                var id = list.TryGetProperty("Id", out var i) ? i.GetString() : null;
                var token = list.TryGetProperty("CurrentChangeToken", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("StringValue", out var sv) ? sv.GetString() : null;
                if (!string.IsNullOrEmpty(root) && modified is not null)
                {
                    lists[root.TrimEnd('/')] = new ListState($"{modified}|{count}", id ?? "", string.IsNullOrEmpty(id) ? null : token);
                }
            }

            return lists;
        }
    }

    /// <summary>
    /// What changed in the library since a token, by SharePoint's change log (asked of a tenant on 24-Nov-2026). The
    /// token is the library's stamp and the log's own token for then, as one answer gave them together. With none,
    /// those for now. Where the stamp is still the one in the token, and can be believed
    /// (<see cref="StampBelievedAfter"/>), nothing has changed and the log is not asked: one call a site covers all its
    /// libraries. Else the log says each change by its kind and the item's number and id, and nothing of where: so the
    /// items that still exist are asked for by their numbers, which gives the folder each is in, and one renamed, moved
    /// or deleted may be held somewhere else, where the source looks for it by its id. A token older than the log is
    /// trusted to be kept, a log that would not answer, or one that names no ids, answers that everything is to be
    /// listed. Null where the server tells no token.
    /// </summary>
    public async Task<LibraryChanges?> ChangesAsync(LibraryInfo library, string? since, CancellationToken ct)
    {
        // the token held: the stamp, and the log's own token
        var space = since?.IndexOf(' ', StringComparison.Ordinal) ?? -1;
        var start = since is not null && space > 0 && space < since.Length - 1 ? since[(space + 1)..] : null;
        if (await ListStateAsync(library, goesBy: since is not null, held: start, ct).ConfigureAwait(false) is not { ChangeToken: { Length: > 0 } current } list)
        {
            return null;
        }

        LibraryChanges Everything(string why) => new($"{list.Stamp} {current}", true, [], why);
        if (since is null)
        {
            return Everything("it has no token yet");
        }

        if (start is null || TakenAt(start) is not { } taken)
        {
            return Everything("its token is of another kind");
        }

        var same = since.AsSpan(0, space).SequenceEqual(list.Stamp);
        if (same && Believed(list.Stamp, taken))
        {
            // the list says nothing in it was added, changed or removed, and the log is not asked. The stamp and the
            // token for now came in one answer, so the one may take the other's place when that one is old
            return new LibraryChanges($"{list.Stamp} {current}", false, []);
        }

        if (DateTime.UtcNow.Ticks - taken > ChangeLogTrusted.Ticks)
        {
            return Everything($"its token is more than {ChangeLogTrusted.TotalDays:N0} days old, and the change log may not go back so far");
        }

        var client = Client(library);
        var changed = new Dictionary<string, (int Number, bool Elsewhere, bool Gone)>(StringComparer.Ordinal);
        string? last = null;
        while (true)
        {
            var count = 0;
            var from = last ?? start;
            var query = "{\"query\":{\"Item\":true,\"Add\":true,\"Update\":true,\"DeleteObject\":true,\"Rename\":true,\"Move\":true,\"Restore\":true,\"SystemUpdate\":true,\"ChangeTokenStart\":{\"StringValue\":" + Json.Quote(from) + "}}}";
            JsonDocument answer;
            try
            {
                answer = await client.PostJsonAsync($"web/lists(guid'{list.Id}')/getchanges", query, Display(library, ""), ct).ConfigureAwait(false);
            }
            catch (VfsException ex) when (ex.Error is not VfsError.Unavailable)
            {
                return Everything($"SharePoint would not say what changed: {ex.Message}");
            }

            using (answer)
            {
                if (!answer.RootElement.TryGetProperty("value", out var told) || told.ValueKind != JsonValueKind.Array)
                {
                    return Everything("the change log answered with something other than changes");
                }

                foreach (var change in told.EnumerateArray())
                {
                    count++;
                    var kind = change.TryGetProperty("ChangeType", out var k) && k.TryGetInt32(out var type) ? type : 0;
                    var id = change.TryGetProperty("UniqueId", out var u) && u.ValueKind == JsonValueKind.String ? Id(u.GetString()) : null;
                    if (id is null || !change.TryGetProperty("ItemId", out var n) || !n.TryGetInt32(out var number))
                    {
                        return Everything("the change log does not name the items that changed");
                    }

                    if (change.TryGetProperty("ChangeToken", out var ct2) && ct2.ValueKind == JsonValueKind.Object && ct2.TryGetProperty("StringValue", out var tv) && tv.GetString() is { Length: > 0 } token)
                    {
                        last = token;
                    }

                    // 3 deleted, 5 moved away: gone, unless a later change brings it back; 4 renamed or moved (within the
                    // list: the log has one word for both), and those two, may be held somewhere else
                    var was = changed.GetValueOrDefault(id);
                    changed[id] = (number, was.Elsewhere || kind is 3 or 4 or 5, kind is 3 or 5);
                }
            }

            // a full answer: more may follow, from the last change told (one that tells no further is the end)
            if (count < ChangesAtOnce || last is null || last == from)
            {
                break;
            }
        }

        // where each is now: the items of those numbers, which the list is asked for some at a time
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefix = library.Key.TrimEnd('/');
        foreach (var some in changed.Where(c => !c.Value.Gone).Select(c => c.Value.Number).Distinct().Chunk(PlacedAtOnce))
        {
            var view = "<View Scope='RecursiveAll'><Query><Where><In><FieldRef Name='ID'/><Values>"
                + string.Concat(some.Select(number => $"<Value Type='Counter'>{number.ToString(CultureInfo.InvariantCulture)}</Value>"))
                + $"</Values></In></Where></Query><RowLimit>{some.Length.ToString(CultureInfo.InvariantCulture)}</RowLimit></View>";
            JsonDocument placed;
            try
            {
                placed = await client.PostJsonAsync($"web/lists(guid'{list.Id}')/GetItems?$select=Id,FileRef,FileDirRef,FSObjType,UniqueId", "{\"query\":{\"ViewXml\":" + Json.Quote(view) + "}}", Display(library, ""), ct).ConfigureAwait(false);
            }
            catch (VfsException ex) when (ex.Error is not VfsError.Unavailable)
            {
                return Everything($"SharePoint would not say where the changed items are: {ex.Message}");
            }

            using (placed)
            {
                if (!placed.RootElement.TryGetProperty("value", out var found) || found.ValueKind != JsonValueKind.Array)
                {
                    return Everything("SharePoint answered where the changed items are with something other than items");
                }

                foreach (var item in found.EnumerateArray())
                {
                    var id = item.TryGetProperty("UniqueId", out var u) && u.ValueKind == JsonValueKind.String ? Id(u.GetString()) : null;
                    var folder = item.TryGetProperty("FileDirRef", out var d) ? d.GetString() : null;
                    if (id is not null && folder is not null && folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (folder.Length == prefix.Length || folder[prefix.Length] == '/'))
                    {
                        folders[id] = folder[prefix.Length..].Trim('/');
                    }
                }
            }
        }

        // one the list did not give back is gone since, or not this account's to see: wherever it is held, that is listed
        var items = changed.Select(c => folders.TryGetValue(c.Key, out var folder) ? new LibraryChange(c.Key, folder, c.Value.Elsewhere) : new LibraryChange(c.Key, null, true)).ToList();

        // From now on: the stamp and the token the one answer gave together, before the log was asked. What changed
        // between the two is told again the next time, and listed twice; nothing is left out. With nothing changed and
        // a stamp that cannot be believed yet (the same one, and the token for now still too close to its change), the
        // token given stands and the log is asked again at the next walk; once the one for now can be believed, or the
        // stamp is another, it takes the other's place, and the stamp alone answers from then on
        return items.Count == 0 && same && !(TakenAt(current) is { } now && Believed(list.Stamp, now))
            ? new LibraryChanges(since, false, [])
            : new LibraryChanges($"{list.Stamp} {current}", false, items, MoveOn: true);
    }

    /// <summary>When a change token was given, in ticks: the fourth part of "1;3;{list};{ticks};{number}". Null for what is no such token.</summary>
    private static long? TakenAt(string token)
    {
        var parts = token.Split(';');
        return parts.Length == 5 && long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ? ticks : null;
    }

    /// <summary>Whether a token taken then came long enough after the last change a stamp names (<see cref="StampBelievedAfter"/>).</summary>
    private static bool Believed(string stamp, long taken)
    {
        var bar = stamp.IndexOf('|', StringComparison.Ordinal);
        return bar > 0
            && DateTime.TryParse(stamp.AsSpan(0, bar), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var changed)
            && taken - changed.Ticks >= StampBelievedAfter.Ticks;
    }

    /// <summary>An item's unique id as entries and changes both carry it: its GUID in one form.</summary>
    private static string? Id(string? unique) => string.IsNullOrEmpty(unique) ? null : Guid.TryParse(unique, out var guid) ? guid.ToString("D") : unique.ToLowerInvariant();

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
        _pages.Remember(item.Id ?? version, version, markdown);
        return (VfsEntry.File(SharePointPages.Shown(entry.Name), markdown.Length, entry.Modified, entry.Created, VfsAttributes.ReadOnly, version) with { Key = entry.Key }, item with { Version = version, Size = markdown.Length });
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

    public async Task<ItemRef> UploadAsync(LibraryInfo library, string itemPath, string localPath, long length, bool onlyIfNew, CancellationToken ct)
    {
        var (parent, name) = VfsPath.SplitLast(itemPath);
        var folder = Url(library, parent);
        var path = Display(library, itemPath);
        var add = $"{SharePointRestClient.FolderApi(folder)}/Files/AddUsingPath(decodedurl='{SharePointRestClient.Encode(name)}',overwrite={(onlyIfNew ? "false" : "true")})?$select={FileSelect}";
        if (length <= SharePointRestClient.SimpleUploadLimit)
        {
            using var created = await Client(library).PostBinaryAsync(add, () => OpenLocal(localPath), length, path, ct).ConfigureAwait(false);
            return ParseFile(created.RootElement).Item;
        }

        // The pieces go against a file that is there. One that is keeps what it holds until the last piece lands
        // (StartUpload leaves the current content as it is), so an empty one is made only where there is none: made over
        // a file that was there, an upload that failed part way left that file empty (review of 21-Nov-2026).
        var file = SharePointRestClient.FileApi(Url(library, itemPath));
        var made = false;
        using (var there = await Client(library).TryGetJsonAsync($"{file}?$select=Exists", path, ct).ConfigureAwait(false))
        {
            var none = there is null || there.RootElement.TryGetProperty("Exists", out var exists) && exists.ValueKind == JsonValueKind.False;
            if (!none && onlyIfNew)
            {
                throw VfsException.AlreadyExists(path);
            }

            if (none)
            {
                var addNew = $"{SharePointRestClient.FolderApi(folder)}/Files/AddUsingPath(decodedurl='{SharePointRestClient.Encode(name)}',overwrite=false)";
                using var empty = await Client(library).PostBinaryAsync(addNew, () => Stream.Null, 0, path, ct).ConfigureAwait(false);
                made = true;
            }
        }

        var uploadId = Guid.NewGuid();
        long offset = 0;
        try
        {
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
        }
        catch
        {
            await GiveUpUploadAsync(library, file, uploadId, made, path).ConfigureAwait(false);
            throw;
        }

        throw new VfsException(VfsError.IoError, $"'{path}': the upload ended without the file");
    }

    /// <summary>
    /// After an upload in pieces that failed: the pieces the server holds are let go, and the empty file made for them is
    /// deleted, so no file of nothing stands where the upload was to land. As far as the server lets it, in a time of its
    /// own (the upload's may be what ended it), and without a word where it does not: the upload's own failure is what is told.
    /// </summary>
    private async Task GiveUpUploadAsync(LibraryInfo library, string file, Guid uploadId, bool made, string path)
    {
        using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var cancelled = await Client(library).PostJsonAsync($"{file}/CancelUpload(uploadId=guid'{uploadId}')", null, path, brief.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is VfsException or HttpRequestException or IOException or OperationCanceledException or SignInRequiredException or ObjectDisposedException)
        {
        }

        if (!made)
        {
            return;
        }

        try
        {
            await Client(library).DeleteAsync(file, path, brief.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is VfsException or HttpRequestException or IOException or OperationCanceledException or SignInRequiredException or ObjectDisposedException)
        {
        }
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

    public async Task<int> CountChildrenAsync(LibraryInfo library, string itemPath, CancellationToken ct)
    {
        var path = Display(library, itemPath);
        var api = SharePointRestClient.FolderApi(Url(library, itemPath));
        using var folder = await Client(library).TryGetJsonAsync($"{api}?$select=ItemCount,Exists", path, ct).ConfigureAwait(false);
        if (folder is null || folder.RootElement.TryGetProperty("Exists", out var exists) && exists.ValueKind == JsonValueKind.False)
        {
            throw VfsException.NotFound(path);
        }

        if (folder.RootElement.TryGetProperty("ItemCount", out var count) && TryLong(count, out var items))
        {
            return (int)Math.Min(items, int.MaxValue);
        }

        // a server that does not say: what the folder lists
        var listed = 0;
        await foreach (var _ in ListChildrenAsync(library, itemPath, ct).ConfigureAwait(false))
        {
            listed++;
        }

        return listed;
    }

    public async Task<ItemRef> MoveAsync(LibraryInfo library, string fromPath, string toPath, bool isFolder, bool replace, CancellationToken ct)
    {
        var from = Url(library, fromPath);
        var to = Url(library, toPath);

        // moveOperations is SharePoint's MoveOperations: 1 is Overwrite
        var api = isFolder
            ? $"{SharePointRestClient.FolderApi(from)}/MoveToUsingPath(DecodedUrl='{SharePointRestClient.Encode(to)}')"
            : $"{SharePointRestClient.FileApi(from)}/MoveToUsingPath(DecodedUrl='{SharePointRestClient.Encode(to)}',moveOperations={(replace ? 1 : 0)})";
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
        var id = file.TryGetProperty("UniqueId", out var u) ? Id(u.GetString()) ?? "" : "";
        var etag = file.TryGetProperty("ETag", out var e) ? e.GetString() : null;

        // with the item's id, which is how the change log names what changed (ChangesAsync)
        return (new VfsEntry(name, false, length, created, modified, VfsAttributes.None, etag) { Key = id.Length > 0 ? id : null }, new ItemRef(id, etag, null, length, false));
    }

    internal static (VfsEntry Entry, ItemRef Item) ParseFolder(JsonElement folder)
    {
        var name = folder.GetProperty("Name").GetString() ?? "";
        var created = ReadTime(folder, "TimeCreated");
        var modified = ReadTime(folder, "TimeLastModified");
        var id = folder.TryGetProperty("UniqueId", out var u) ? Id(u.GetString()) ?? "" : "";
        return (VfsEntry.Directory(name, modified, created) with { Key = id.Length > 0 ? id : null }, new ItemRef(id, modified?.ToString("O"), null, 0, true));
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
