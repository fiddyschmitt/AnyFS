using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnyFS.Sources.Tests;

/// <summary>
/// What changed in a library since a token, as the two services say it (asked of the sandbox tenant on 24-Nov-2026; the
/// review of 21-Nov-2026, P7).
/// <para>
/// Graph's <c>drives/{id}/root/delta</c>: <c>token=latest</c> gives a link and no items; with a token, the root, then each
/// item that changed with the folders above it, each with its parent's path (<c>parentReference.path</c>, not encoded);
/// a renamed or moved item as it is now, and nothing of where it was nor of what a folder holds; a deleted one as its id,
/// <c>deleted</c> and its parent's id, every one of what a deleted folder held too; another link at each call, with
/// nothing in the drive changed between them; 410 for a token it no longer knows.
/// </para>
/// <para>
/// SharePoint's <c>web/lists(guid)/getchanges</c>: each change as its kind (1 added, 2 changed, 3 deleted, 4 renamed or
/// moved), the item's number and unique id, and a token of its own; <c>ServerRelativeUrl</c> there and empty; a folder
/// moved or deleted with all it holds is one change; a token from before the log began answers the whole log; one that is
/// no token is a 500. Where an item is now is asked by its number (<c>GetItems</c> with a CAML query). The list's
/// <c>CurrentChangeToken</c> is the token for now, and another each time it is read.
/// </para>
/// A token here names a picture of the library as it was when the token was given, and what changed is what differs
/// from it now: the services keep a log, and answer the same of it.
/// </summary>
internal sealed partial class FakeSharePoint
{
    private static int _nextItemNumber;

    private readonly Dictionary<long, (Library Library, Dictionary<Guid, Pictured> Items, int Removed)> _pictures = [];

    // what was removed, in order: an item made and removed between two tokens is in neither picture, and the services
    // tell of it as removed all the same (both did, asked on 24-Nov-2026)
    private readonly List<(Library Library, Guid Id, Pictured Was)> _removed = [];
    private readonly Dictionary<Library, (long Picture, long Print)> _lastPicture = [];
    private long _nextPicture = 1000;
    private long _deltaLinks;

    /// <summary>Whether a list tells its change token (<c>CurrentChangeToken</c>): off, as a server that knows no such property.</summary>
    public bool ChangeTokens { get; set; } = true;

    /// <summary>Whether a delta says which folder an item is in (<c>parentReference.path</c>), as the tenant did when asked; Microsoft's page says a delta does not.</summary>
    public bool DeltaPaths { get; set; } = true;

    /// <summary>How many changes the change log gives in one answer (SharePoint's own number).</summary>
    public int ChangeLogLimit { get; set; } = 1000;

    /// <summary>Makes every token given so far one the services no longer know, as a change log trimmed past them.</summary>
    public void ForgetChangeTokens()
    {
        lock (_gate)
        {
            _pictures.Clear();
            _lastPicture.Clear();
        }
    }

    /// <summary>Removes the file or folder at a path of a library, with all it holds, as someone else would.</summary>
    public void Remove(Library library, string path)
    {
        lock (_gate)
        {
            var item = At(library, path);
            Removing(item);
            item.Parent!.Children.Remove(item);
            item.Parent.Touch();
        }
    }

    /// <summary>Notes an item about to be removed, and all it holds.</summary>
    private void Removing(Item item)
    {
        if (item.Library is not { } library || item.Parent is null)
        {
            return;
        }

        foreach (var gone in item is FolderItem folder ? Everything(folder) : [item])
        {
            _removed.Add((library, gone.UniqueId, new Pictured(gone.Parent!.UniqueId, gone.Name, gone.Version, (gone as FileItem)?.ContentVersion ?? 0, gone is FolderItem, NumberOf(gone))));
        }
    }

    /// <summary>Moves the file or folder at a path of a library to another path of it (another folder, another name, or both), with all it holds, as someone else would.</summary>
    public void Move(Library library, string from, string to)
    {
        lock (_gate)
        {
            var item = At(library, from);
            var slash = to.LastIndexOf('/');
            var target = slash < 0 ? library.Root : (FolderItem)At(library, to[..slash]);
            item.Parent!.Children.Remove(item);
            item.Parent.Touch();
            item.Parent = target;
            item.Name = to[(slash + 1)..];
            target.Children.Add(item);
            target.Touch();
            item.Touch();
        }
    }

    /// <summary>Makes everything in a library as old as that: last changed so long ago.</summary>
    public void Age(Library library, TimeSpan by)
    {
        lock (_gate)
        {
            foreach (var item in Everything(library.Root))
            {
                item.Created -= by;
                item.Modified -= by;
            }
        }
    }

    private static Item At(Library library, string path)
    {
        Item item = library.Root;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            item = (item as FolderItem)?.Child(part) ?? throw new InvalidOperationException($"the fake's '{library.Title}' has no '{path}'");
        }

        return item;
    }

    /// <summary>An item as it was when a token was given.</summary>
    private sealed record Pictured(Guid Parent, string Name, int Version, int Content, bool Folder, int Number);

    private enum Changed
    {
        Added = 1,
        Updated = 2,
        Deleted = 3,
        Renamed = 4,
    }

    /// <summary>A list item's number, by its item: given when first asked for, and its for good.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Item, object> Numbers = [];

    private static int NumberOf(Item item) => (int)Numbers.GetValue(item, _ => Interlocked.Increment(ref _nextItemNumber));

    private static string GraphIdOf(Guid id) => "01" + Convert.ToHexString(id.ToByteArray());

    private static Dictionary<Guid, Item> ItemsOf(Library library) => Everything(library.Root).Where(i => i.Parent is not null).ToDictionary(i => i.UniqueId);

    /// <summary>A picture of the library as it is now, and its number: a token. The last one again where nothing has changed since it was taken.</summary>
    private long Picture(Library library)
    {
        long print = 17;
        foreach (var item in Everything(library.Root))
        {
            if (item.Parent is not null)
            {
                print = unchecked((print * 31) + HashCode.Combine(item.UniqueId, item.Parent.UniqueId, item.Name, item.Version, (item as FileItem)?.ContentVersion ?? 0));
            }
        }

        if (_lastPicture.TryGetValue(library, out var last) && last.Print == print && _pictures.ContainsKey(last.Picture))
        {
            return last.Picture;
        }

        var id = ++_nextPicture;
        _pictures[id] = (library, ItemsOf(library).ToDictionary(p => p.Key, p => new Pictured(p.Value.Parent!.UniqueId, p.Value.Name, p.Value.Version, (p.Value as FileItem)?.ContentVersion ?? 0, p.Value is FolderItem, NumberOf(p.Value))), _removed.Count);
        _lastPicture[library] = (id, print);

        // the last few hundred are enough for any test
        foreach (var old in _pictures.Keys.Where(k => k < id - 500).ToList())
        {
            _pictures.Remove(old);
        }

        return id;
    }

    /// <summary>What differs from the picture a token names; null for a token not known (any more).</summary>
    private List<(Changed Kind, Guid Id, Item? Item, Pictured? Was)>? ChangesSince(Library library, long picture)
    {
        if (!_pictures.TryGetValue(picture, out var then) || !ReferenceEquals(then.Library, library))
        {
            return null;
        }

        var now = ItemsOf(library);
        var changes = new List<(Changed, Guid, Item?, Pictured?)>();
        foreach (var (id, item) in now)
        {
            if (!then.Items.TryGetValue(id, out var was))
            {
                changes.Add((Changed.Added, id, item, null));
            }
            else if (was.Name != item.Name || was.Parent != item.Parent!.UniqueId)
            {
                changes.Add((Changed.Renamed, id, item, was));
            }
            else if (was.Version != item.Version || was.Content != ((item as FileItem)?.ContentVersion ?? 0))
            {
                changes.Add((Changed.Updated, id, item, was));
            }
        }

        foreach (var (id, was) in then.Items)
        {
            if (!now.ContainsKey(id))
            {
                changes.Add((Changed.Deleted, id, null, was));
            }
        }

        // and what was made and removed since
        foreach (var (where, id, was) in _removed.Skip(then.Removed))
        {
            if (ReferenceEquals(where, library) && !then.Items.ContainsKey(id) && !now.ContainsKey(id))
            {
                changes.Add((Changed.Deleted, id, null, was));
            }
        }

        return changes;
    }

    // --- SharePoint's change log ----------------------------------------------------------------------------------------

    private string ChangeTokenOf(Library library) => $"1;3;{library.Id};{DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)};{Picture(library).ToString(CultureInfo.InvariantCulture)}";

    /// <summary><c>web/lists(guid'…')/getchanges</c> and <c>…/GetItems</c>.</summary>
    private Response ListApi(Request request, Web web, string listId, List<Call> calls)
    {
        var library = web.Libraries.FirstOrDefault(l => Guid.TryParse(listId, out var id) && l.Id == id);
        if (library is null)
        {
            return Response.Json(404, RestError("-2130575322, Microsoft.SharePoint.SPException", "List does not exist."));
        }

        var name = calls.Count == 0 ? "" : calls[0].Name.ToLowerInvariant();
        if (request.Method == "GET" && name.Length == 0)
        {
            return Response.Json(200, new JsonObject { ["Id"] = library.Id.ToString(), ["ItemCount"] = library.ItemCount });
        }

        if (request.Method != "POST")
        {
            return NotUnderstood("lists/" + name);
        }

        var body = JsonNode.Parse(request.Body)?["query"];
        switch (name)
        {
            case "getchanges":
                return ChangeLog(library, body?["ChangeTokenStart"]?["StringValue"]?.GetValue<string>());
            case "getitems":
                return ItemsByNumber(library, body?["ViewXml"]?.GetValue<string>() ?? "");
            default:
                return NotUnderstood("lists/" + name);
        }
    }

    private Response ChangeLog(Library library, string? start)
    {
        // a token is a picture's number; a change's own token is that and how many changes of the answer came before it
        var parts = start?.Split(';');
        var at = parts is { Length: 5 } ? parts[4].Split(':') : [];
        var skip = 0;
        if (at.Length is not (1 or 2) || !long.TryParse(at[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var picture) || (at.Length == 2 && !int.TryParse(at[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out skip)))
        {
            return Response.Json(500, RestError("-1, System.FormatException", "Input string was not in a correct format."));
        }

        // a token from before the log began (or one forgotten) answers the log from its start: everything there is, as added
        var changes = ChangesSince(library, picture) ?? ItemsOf(library).Select(p => (Changed.Added, p.Key, (Item?)p.Value, (Pictured?)null)).ToList();
        var gone = changes.Where(c => c.Kind == Changed.Deleted).Select(c => c.Id).ToHashSet();

        // a folder deleted with all it holds is one change, and so is one moved; a folder is not changed by what is put into it
        var told = changes.Where(c => !((c.Kind == Changed.Deleted && gone.Contains(c.Was!.Parent)) || (c.Kind == Changed.Updated && c.Item is FolderItem))).ToList();
        var value = new JsonArray();
        var number = skip;
        foreach (var (kind, id, item, was) in told.Skip(skip).Take(ChangeLogLimit))
        {
            var token = $"1;3;{library.Id};{DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)};{picture.ToString(CultureInfo.InvariantCulture)}:{(++number).ToString(CultureInfo.InvariantCulture)}";
            value.Add(new JsonObject
            {
                ["ChangeToken"] = new JsonObject { ["StringValue"] = token },
                ["ChangeType"] = (int)kind,
                ["SiteId"] = library.Web?.Collection.Id.ToString(),
                ["Time"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                ["Editor"] = "",
                ["EditorEmailHint"] = null,
                ["ItemId"] = item is not null ? NumberOf(item) : was!.Number,
                ["ListId"] = library.Id.ToString(),
                ["ServerRelativeUrl"] = "",
                ["SharedByUser"] = null,
                ["SharedWithUsers"] = null,
                ["UniqueId"] = id.ToString(),
                ["WebId"] = library.Web?.Id.ToString(),
            });
        }

        return Response.Json(200, new JsonObject { ["value"] = value });
    }

    /// <summary>The items a CAML query names by their numbers (<c>&lt;In&gt;&lt;FieldRef Name='ID'/&gt;…</c>), each with where it is.</summary>
    private static Response ItemsByNumber(Library library, string view)
    {
        var wanted = Regex.Matches(view, @"<Value Type='Counter'>(\d+)</Value>").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();
        var value = new JsonArray();
        foreach (var item in ItemsOf(library).Values.Where(i => wanted.Contains(NumberOf(i))).OrderBy(NumberOf))
        {
            var number = NumberOf(item);
            value.Add(new JsonObject
            {
                ["Id"] = number,
                ["ID"] = number,
                ["FileRef"] = item.ServerRelativeUrl,
                ["FileDirRef"] = item.Parent!.ServerRelativeUrl,
                ["FSObjType"] = item is FolderItem ? 1 : 0,
                ["UniqueId"] = item.UniqueId.ToString(),
            });
        }

        return Response.Json(200, new JsonObject { ["value"] = value });
    }

    // --- Graph's delta ----------------------------------------------------------------------------------------------------

    /// <summary><c>drives/{id}/root/delta</c>.</summary>
    private Response Delta(Request request, Library library)
    {
        var select = request.Query.TryGetValue("$select", out var s) ? s : null;
        // a link's token is a picture's number and the link's own: the tenant gave another link at each call, though
        // nothing in the drive had changed (it counts what happens anywhere in the site)
        string Link(long picture) => $"https://{request.Host}/v1.0/drives/{library.DriveId}/root/delta?token={picture.ToString(CultureInfo.InvariantCulture)}.{(++_deltaLinks).ToString(CultureInfo.InvariantCulture)}{(select is null ? "" : "&$select=" + select)}";
        var token = request.Query.TryGetValue("token", out var t) ? t : null;
        if (token == "latest")
        {
            return GraphJson(200, new JsonObject { ["value"] = new JsonArray(), ["@odata.deltaLink"] = Link(Picture(library)) });
        }

        List<(Changed Kind, Guid Id, Item? Item, Pictured? Was)> changes;
        long picture = 0;
        if (token is null)
        {
            // from no token: everything there is
            changes = ItemsOf(library).Select(p => (Changed.Added, p.Key, (Item?)p.Value, (Pictured?)null)).ToList();
        }
        else if (!long.TryParse(token.Split('.')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out picture) || ChangesSince(library, picture) is not { } since)
        {
            return GraphJson(410, GraphError("resyncRequired", "Resync required. Replace any local items with the server's version (including deletes) if you're sure that the service was up to date with your local changes when you last sync'd. Upload any local changes that the server doesn't know about."))
                .With("Location", $"https://{request.Host}/v1.0/drives/{library.DriveId}/root/delta");
        }
        else
        {
            changes = since;
        }

        // the root, then each item that changed after the folders above it, each once; a deleted one as its id and its parent's
        var items = new List<JsonNode>();
        var said = new HashSet<Guid>();
        void Say(Item item)
        {
            if (item.Parent is null || !said.Add(item.UniqueId))
            {
                return;
            }

            Say(item.Parent);
            var json = ItemJson(request, item, select: null, downloadUrl: false);
            if (DeltaPaths)
            {
                json["parentReference"]!["path"] = $"/drives/{library.DriveId}/root:{(item.Parent.RelativePath.Length == 0 ? "" : "/" + item.Parent.RelativePath)}";
            }

            items.Add(select is null ? json : Select(select, json));
        }

        if (changes.Count > 0)
        {
            var root = ItemJson(request, library.Root, select: null, downloadUrl: false);
            root["root"] = new JsonObject();
            items.Add(select is null ? root : Select(select, root));
        }

        foreach (var (_, _, item, _) in changes.Where(c => c.Item is not null))
        {
            Say(item!);
        }

        foreach (var (_, id, _, was) in changes.Where(c => c.Kind == Changed.Deleted))
        {
            var json = new JsonObject
            {
                ["id"] = GraphIdOf(id),
                ["parentReference"] = new JsonObject { ["driveId"] = library.DriveId, ["driveType"] = "documentLibrary", ["id"] = GraphIdOf(was!.Parent) },
                ["deleted"] = new JsonObject { ["state"] = "deleted" },
                [was.Folder ? "folder" : "file"] = new JsonObject(),
                ["size"] = 0,
            };
            items.Add(select is null ? json : Select(select, json));
        }

        // a page at a time, the link for the next time on the last
        var skip = request.Query.TryGetValue("$skiptoken", out var st) && int.TryParse(st, CultureInfo.InvariantCulture, out var k) ? k : 0;
        var page = items.Skip(skip).Take(GraphPageSize).ToList();
        var answer = new JsonObject { ["value"] = new JsonArray(page.Select(n => n.DeepClone()).ToArray()) };
        if (skip + GraphPageSize < items.Count)
        {
            var query = string.Join('&', request.RawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.StartsWith("%24skiptoken=", StringComparison.OrdinalIgnoreCase) && !p.StartsWith("$skiptoken=", StringComparison.OrdinalIgnoreCase)));
            answer["@odata.nextLink"] = $"https://{request.Host}/v1.0/drives/{library.DriveId}/root/delta?{(query.Length == 0 ? "" : query + "&")}$skiptoken={skip + GraphPageSize}";
        }
        else
        {
            answer["@odata.deltaLink"] = Link(changes.Count == 0 && token is not null ? picture : Picture(library));
        }

        return GraphJson(200, answer);
    }
}
