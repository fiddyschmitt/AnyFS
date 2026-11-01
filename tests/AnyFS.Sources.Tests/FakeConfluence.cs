using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A stand-in for a Confluence Data Center server (spec 7.10): spaces (global and personal, current and archived), pages
/// in a tree and blog posts with their storage format, versions and labels, attachments, footer and inline comments with
/// replies, read restrictions; answered as Confluence's REST API answers: the space list and space call, a space's
/// top-level pages, the content call and list, children (pages, attachments, comments), the cursor-paged scan, content
/// search and search by the CQL the source writes, users by key, and the attachment download (with Range). Offset pages
/// carry <c>_links.next</c> relative to the installation, as Confluence's do.
/// </summary>
internal sealed partial class FakeConfluence(string origin = "https://confluence.fake.test", string root = "/confluence")
    : FakeAtlassian(origin, root, "confluence", "10.2.19")
{
    private readonly object _gate = new();
    private long _nextId = 100_000;

    public List<Space> Spaces { get; } = [];

    public List<Content> Contents { get; } = [];

    public List<Attachment> Attachments { get; } = [];

    public List<Comment> Comments { get; } = [];

    /// <summary>The most a request with bodies gives (Confluence caps those lower), and the most without.</summary>
    public int LimitWithBodies { get; set; } = 50;

    public int Limit { get; set; } = 200;

    /// <summary>Whether the scan is there (Confluence 7.18+).</summary>
    public bool Scan { get; set; } = true;

    protected override string WhoPath => "rest/api/user/current";

    protected override JsonNode Who(Account? user)
        => user is null ? new JsonObject { ["type"] = "anonymous", ["displayName"] = "Anonymous" } : Person(user.Name);

    // --- the model -------------------------------------------------------------------------------------------------------

    public sealed class Space(string key, string name, string type = "global", string status = "current")
    {
        public string Key { get; } = key;

        public string Name { get; } = name;

        public string Type { get; } = type;

        public string Status { get; set; } = status;

        public Content? Home { get; set; }

        public HashSet<string>? VisibleTo { get; set; }
    }

    public sealed class Content(string id, string type, string title, Space space, Content? parent)
    {
        public string Id { get; } = id;

        public string Type { get; } = type;

        public string Title { get; set; } = title;

        public Space Space { get; } = space;

        public Content? Parent { get; set; } = parent;

        public int Version { get; set; } = 1;

        public string Storage { get; set; } = "";

        public DateTimeOffset Created { get; set; } = new(2026, 10, 2, 10, 0, 0, TimeSpan.FromHours(10));

        public DateTimeOffset Changed { get; set; } = new(2026, 10, 2, 10, 0, 0, TimeSpan.FromHours(10));

        public string CreatedBy { get; set; } = "tester";

        public string ChangedBy { get; set; } = "tester";

        public List<string> Labels { get; } = [];

        /// <summary>The accounts that may read it (and the pages below it); null for everyone who reads the space.</summary>
        public HashSet<string>? RestrictedTo { get; set; }
    }

    public sealed class Attachment(string id, Content container, string fileName, byte[] bytes)
    {
        public string Id { get; } = id;

        public Content Container { get; } = container;

        public string FileName { get; } = fileName;

        public byte[] Bytes { get; set; } = bytes;

        public int Version { get; set; } = 1;

        public DateTimeOffset Changed { get; set; } = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

        public string MediaType { get; set; } = "application/octet-stream";
    }

    public sealed class Comment(string id, Content container, Comment? parent, string author, string storage, DateTimeOffset created)
    {
        public string Id { get; } = id;

        public Content Container { get; } = container;

        public Comment? Parent { get; } = parent;

        public string Author { get; } = author;

        public string Storage { get; } = storage;

        public DateTimeOffset Created { get; } = created;

        public bool Inline { get; set; }

        public string? Selection { get; set; }
    }

    private string NextId() => (++_nextId).ToString(CultureInfo.InvariantCulture);

    public Space AddSpace(string key, string name, string type = "global", string status = "current")
    {
        lock (_gate)
        {
            var space = new Space(key, name, type, status);
            space.Home = new Content(NextId(), "page", name + " Home", space, null) { Storage = "<p>Welcome to " + name + ".</p>" };
            Contents.Add(space.Home);
            Spaces.Add(space);
            return space;
        }
    }

    public Content AddPage(Space space, Content? parent, string title, string storage, string type = "page")
    {
        lock (_gate)
        {
            var page = new Content(NextId(), type, title, space, parent) { Storage = storage };
            Contents.Add(page);
            return page;
        }
    }

    public Attachment AddAttachment(Content page, string name, byte[] bytes)
    {
        lock (_gate)
        {
            var attachment = new Attachment("att" + NextId(), page, name, bytes);
            Attachments.Add(attachment);
            return attachment;
        }
    }

    public Comment AddComment(Content page, string author, string storage, DateTimeOffset when, Comment? parent = null)
    {
        lock (_gate)
        {
            var comment = new Comment(NextId(), page, parent, author, storage, when);
            Comments.Add(comment);
            return comment;
        }
    }

    public Content PageTitled(string title) => Contents.Single(c => c.Title == title);

    // --- who may read what -----------------------------------------------------------------------------------------------

    private static bool CanRead(Account? user, Space space) => space.VisibleTo is null || user is not null && space.VisibleTo.Contains(user.Name);

    private static bool CanRead(Account? user, Content content)
    {
        for (var c = content; c is not null; c = c.Parent)
        {
            if (c.RestrictedTo is not null && (user is null || !c.RestrictedTo.Contains(user.Name)))
            {
                return false;
            }
        }

        return CanRead(user, content.Space);
    }

    // --- answering -------------------------------------------------------------------------------------------------------

    protected override Response AnswerProduct(Request request, string path, Account? user)
    {
        var s = path.Split('/');
        if (s is ["download", "attachments", var pageId, var file])
        {
            if (user is null)
            {
                return Response.Redirect(Server + "/login.action");
            }

            var attachment = Attachments.FirstOrDefault(a => a.Container.Id == pageId && a.FileName == file && CanRead(user, a.Container));
            return attachment is null ? Response.NotFound() : Bytes(request, attachment.Bytes, attachment.MediaType);
        }

        if (Unauthenticated(user) is { } refused)
        {
            return refused;
        }

        lock (_gate)
        {
            var expand = (request.Q("expand") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            switch (s)
            {
                case ["rest", "api", "user"]:
                    var who = Accounts.FirstOrDefault(a => a.Key == request.Q("key") || a.Name == request.Q("username"));
                    return who is null ? Response.NotFound() : Response.Json(Person(who.Name));

                case ["rest", "api", "space"]:
                    var spaces = Spaces.Where(x => CanRead(user, x) && (request.Q("type") is not { } t || x.Type == t) && x.Status == (request.Q("status") ?? "current"));
                    return Page(request, "rest/api/space", spaces.Select(x => (JsonNode)SpaceJson(x, expand)).ToList(), Limit);

                case ["rest", "api", "space", var key]:
                    return Spaces.FirstOrDefault(x => x.Key == key && CanRead(user, x)) is { } space ? Response.Json(SpaceJson(space, expand)) : Response.NotFound($"No space with key : {key}");

                case ["rest", "api", "space", var key, "content", "page"]:
                    var top = Contents.Where(c => c.Space.Key == key && c.Type == "page" && c.Parent is null && CanRead(user, c));
                    return Page(request, path, top.Select(c => (JsonNode)ContentJson(c, expand)).ToList(), Cap(expand));

                case ["rest", "api", "content", "scan"]:
                    return ScanPage(request, user, expand);

                case ["rest", "api", "content", "search"]:
                    return CqlContent(request, user, expand);

                case ["rest", "api", "content"]:
                    var listed = Contents.Where(c => CanRead(user, c) && c.Type == (request.Q("type") ?? "page")
                        && (request.Q("spaceKey") is not { } sk || c.Space.Key == sk) && (request.Q("title") is not { } title || c.Title == title)
                        && (request.Q("postingDay") is not { } day || c.Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == day));
                    return Page(request, path, listed.Select(c => (JsonNode)ContentJson(c, expand)).ToList(), Cap(expand));

                case ["rest", "api", "content", var id]:
                    return Contents.FirstOrDefault(c => c.Id == id && CanRead(user, c)) is { } content ? Response.Json(ContentJson(content, expand)) : Response.NotFound($"No content found with id : {id}");

                case ["rest", "api", "content", var id, "child", var kind]:
                    var parent = Contents.FirstOrDefault(c => c.Id == id && CanRead(user, c));
                    if (parent is null)
                    {
                        return Response.NotFound($"No content found with id : {id}");
                    }

                    return kind switch
                    {
                        "page" => Page(request, path, Contents.Where(c => c.Parent == parent && c.Type == "page" && CanRead(user, c)).Select(c => (JsonNode)ContentJson(c, expand)).ToList(), Cap(expand)),
                        "attachment" => Page(request, path, Attachments.Where(a => a.Container == parent).Select(a => (JsonNode)AttachmentJson(a, expand)).ToList(), Limit),
                        "comment" => Page(request, path, Comments.Where(c => c.Container == parent).OrderBy(c => c.Created).Select(c => (JsonNode)CommentJson(c, expand)).ToList(), Cap(expand)),
                        _ => Response.NotFound(),
                    };

                case ["rest", "api", "search"]:
                    return Search(request, user);

                default:
                    return Response.NotFound();
            }
        }
    }

    private int Cap(HashSet<string> expand) => expand.Any(e => e.StartsWith("body", StringComparison.Ordinal)) ? LimitWithBodies : Limit;

    /// <summary>One offset page of a list, <c>_links.next</c> relative to the installation (without its context path) while there are more.</summary>
    private Response Page(Request request, string path, List<JsonNode> all, int cap)
    {
        var start = request.QInt("start", 0);
        var limit = Math.Min(request.QInt("limit", 25), cap);
        var page = all.Skip(start).Take(limit).ToList();
        var links = new JsonObject { ["base"] = Server, ["context"] = Root };
        if (start + page.Count < all.Count)
        {
            var query = request.Query.Where(q => q.Key is not ("start" or "limit")).Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}");
            links["next"] = $"/{path}?{string.Join('&', query.Append($"limit={limit}").Append($"start={start + page.Count}"))}";
        }

        return Response.Json(new JsonObject { ["results"] = new JsonArray(page.Select(p => p.DeepClone()).ToArray()), ["start"] = start, ["limit"] = limit, ["size"] = page.Count, ["_links"] = links });
    }

    /// <summary>The scan: a space's pages a cursor at a time (the cursor is the next page's offset here).</summary>
    private Response ScanPage(Request request, Account? user, HashSet<string> expand)
    {
        if (!Scan)
        {
            return Response.NotFound();
        }

        var all = Contents.Where(c => c.Type == (request.Q("type") ?? "page") && c.Space.Key == request.Q("spaceKey") && CanRead(user, c)).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        var from = int.TryParse(request.Q("cursor"), out var cursor) ? cursor : 0;
        var limit = Math.Min(request.QInt("limit", 25), Cap(expand));
        var page = all.Skip(from).Take(limit).ToList();
        var links = new JsonObject { ["base"] = Server };
        if (from + page.Count < all.Count)
        {
            links["next"] = $"/rest/api/content/scan?spaceKey={request.Q("spaceKey")}&type={request.Q("type") ?? "page"}&limit={limit}&expand={Uri.EscapeDataString(request.Q("expand") ?? "")}&cursor={from + page.Count}";
        }

        return Response.Json(new JsonObject { ["results"] = new JsonArray(page.Select(c => (JsonNode)ContentJson(c, expand)).ToArray()), ["limit"] = limit, ["size"] = page.Count, ["_links"] = links });
    }

    [GeneratedRegex(@"space\s*=\s*""?(?<key>[^""\s)]+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex CqlSpace();

    [GeneratedRegex(@"type\s*=\s*(?<type>\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex CqlType();

    [GeneratedRegex(@"\(id\s*=\s*(?<id>\d+)\s+or\s+ancestor\s*=\s*\k<id>\)", RegexOptions.IgnoreCase)]
    private static partial Regex CqlUnder();

    [GeneratedRegex(@"(?<field>lastmodified|created)\s*>=\s*now\(""-(?<n>\d+)m""\)", RegexOptions.IgnoreCase)]
    private static partial Regex CqlSince();

    private static DateTimeOffset CreatedOf(object item) => item switch
    {
        Content c => c.Created,
        Attachment a => a.Changed,
        Comment c => c.Created,
        _ => DateTimeOffset.MinValue,
    };

    /// <summary>What a CQL query the source writes finds: a space's content (of a type), or a page and what is below it.</summary>
    private List<(string Type, object Item, DateTimeOffset When)> Cql(string cql, Account? user)
    {
        var found = new List<(string Type, object Item, DateTimeOffset When)>();
        found.AddRange(Contents.Where(c => CanRead(user, c)).Select(c => (c.Type, (object)c, c.Changed)));
        found.AddRange(Attachments.Where(a => CanRead(user, a.Container)).Select(a => ("attachment", (object)a, a.Changed)));
        found.AddRange(Comments.Where(c => CanRead(user, c.Container)).Select(c => ("comment", (object)c, c.Created)));
        Content? ContainerOf(object item) => item switch { Content c => c, Attachment a => a.Container, Comment c => c.Container, _ => null };

        if (CqlSpace().Match(cql) is { Success: true } space)
        {
            found = found.Where(f => ContainerOf(f.Item)!.Space.Key == space.Groups["key"].Value).ToList();
        }

        if (CqlType().Match(cql) is { Success: true } type)
        {
            found = found.Where(f => f.Type == type.Groups["type"].Value).ToList();
        }

        foreach (Match since in CqlSince().Matches(cql))
        {
            var after = DateTimeOffset.UtcNow.AddMinutes(-long.Parse(since.Groups["n"].Value, CultureInfo.InvariantCulture));
            var made = since.Groups["field"].Value.Equals("created", StringComparison.OrdinalIgnoreCase);
            found = found.Where(f => (made ? CreatedOf(f.Item) : f.When) >= after).ToList();
        }

        if (CqlUnder().Match(cql) is { Success: true } under)
        {
            var id = under.Groups["id"].Value;
            found = found.Where(f => f.Item is Content c && (c.Id == id || Ancestors(c).Any(a => a.Id == id))).ToList();
        }

        return cql.Contains("order by lastmodified desc", StringComparison.OrdinalIgnoreCase) ? found.OrderByDescending(f => f.When).ToList() : found;
    }

    private Response CqlContent(Request request, Account? user, HashSet<string> expand)
    {
        var found = Cql(request.Q("cql") ?? "", user);
        var items = found.Select(f => f.Item switch
        {
            Content c => (JsonNode)ContentJson(c, expand),
            Attachment a => AttachmentJson(a, expand),
            Comment c => CommentJson(c, expand),
            _ => throw new InvalidOperationException(),
        }).ToList();
        return Page(request, "rest/api/content/search", items, Cap(expand));
    }

    private Response Search(Request request, Account? user)
    {
        var found = Cql(request.Q("cql") ?? "", user);
        var limit = request.QInt("limit", 25);
        var results = found.Take(limit).Select(f => (JsonNode)new JsonObject
        {
            ["content"] = new JsonObject { ["id"] = f.Item switch { Content c => c.Id, Attachment a => a.Id, Comment c => c.Id, _ => "" }, ["type"] = f.Type },
            ["lastModified"] = Time(f.When),
        }).ToArray();
        return Response.Json(new JsonObject { ["results"] = new JsonArray(results), ["start"] = 0, ["limit"] = limit, ["size"] = results.Length, ["totalSize"] = found.Count });
    }

    // --- JSON as Confluence writes it -----------------------------------------------------------------------------------

    public static string Time(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    private JsonObject Person(string name)
    {
        var account = Accounts.FirstOrDefault(a => a.Name == name);
        return new JsonObject { ["type"] = "known", ["username"] = name, ["userKey"] = account?.Key ?? name, ["displayName"] = account?.Display ?? name };
    }

    private static IEnumerable<Content> Ancestors(Content c)
    {
        var chain = new List<Content>();
        for (var p = c.Parent; p is not null; p = p.Parent)
        {
            chain.Insert(0, p);
        }

        return chain;
    }

    private JsonObject SpaceJson(Space space, HashSet<string> expand)
    {
        var json = new JsonObject { ["key"] = space.Key, ["name"] = space.Name, ["type"] = space.Type, ["status"] = space.Status };
        if (expand.Contains("homepage") && space.Home is { } home)
        {
            json["homepage"] = new JsonObject { ["id"] = home.Id, ["title"] = home.Title };
        }

        return json;
    }

    private JsonObject ContentJson(Content c, HashSet<string> expand)
    {
        var json = new JsonObject
        {
            ["id"] = c.Id,
            ["type"] = c.Type,
            ["status"] = "current",
            ["title"] = c.Title,
            ["_links"] = new JsonObject
            {
                ["webui"] = c.Type == "blogpost" ? $"/display/{c.Space.Key}/{c.Created:yyyy/MM/dd}/{c.Title.Replace(' ', '+')}" : $"/display/{c.Space.Key}/{c.Title.Replace(' ', '+')}",
                ["tinyui"] = "/x/" + c.Id,
            },
        };
        if (expand.Contains("version"))
        {
            json["version"] = new JsonObject { ["number"] = c.Version, ["when"] = Time(c.Changed), ["by"] = Person(c.ChangedBy) };
        }

        if (expand.Contains("history"))
        {
            json["history"] = new JsonObject { ["createdDate"] = Time(c.Created), ["createdBy"] = Person(c.CreatedBy) };
        }

        if (expand.Contains("space"))
        {
            json["space"] = new JsonObject { ["key"] = c.Space.Key, ["name"] = c.Space.Name };
        }

        if (expand.Contains("ancestors"))
        {
            json["ancestors"] = new JsonArray(Ancestors(c).Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["title"] = a.Title }).ToArray());
        }

        if (expand.Contains("body.storage"))
        {
            json["body"] = new JsonObject { ["storage"] = new JsonObject { ["value"] = c.Storage, ["representation"] = "storage" } };
        }

        if (expand.Contains("metadata.labels"))
        {
            json["metadata"] = new JsonObject { ["labels"] = new JsonObject { ["results"] = new JsonArray(c.Labels.Select(l => (JsonNode)new JsonObject { ["name"] = l }).ToArray()) } };
        }

        return json;
    }

    private JsonObject AttachmentJson(Attachment a, HashSet<string> expand)
    {
        var json = new JsonObject
        {
            ["id"] = a.Id,
            ["type"] = "attachment",
            ["title"] = a.FileName,
            ["version"] = new JsonObject { ["number"] = a.Version, ["when"] = Time(a.Changed) },
            ["extensions"] = new JsonObject { ["mediaType"] = a.MediaType, ["fileSize"] = a.Bytes.Length },
            ["_links"] = new JsonObject { ["download"] = $"/download/attachments/{a.Container.Id}/{Uri.EscapeDataString(a.FileName)}?version={a.Version}&api=v2" },
        };
        if (expand.Contains("container"))
        {
            json["container"] = new JsonObject { ["id"] = a.Container.Id, ["type"] = a.Container.Type };
        }

        return json;
    }

    private JsonObject CommentJson(Comment c, HashSet<string> expand)
    {
        var ancestors = new List<Comment>();
        for (var p = c.Parent; p is not null; p = p.Parent)
        {
            ancestors.Insert(0, p);
        }

        return new JsonObject
        {
            ["id"] = c.Id,
            ["type"] = "comment",
            ["title"] = "Re: " + c.Container.Title,
            ["body"] = new JsonObject { ["storage"] = new JsonObject { ["value"] = c.Storage, ["representation"] = "storage" } },
            ["version"] = new JsonObject { ["number"] = 1, ["when"] = Time(c.Created), ["by"] = Person(c.Author) },
            ["history"] = new JsonObject { ["createdDate"] = Time(c.Created), ["createdBy"] = Person(c.Author) },
            ["ancestors"] = new JsonArray(ancestors.Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["type"] = "comment" }).ToArray()),
            ["container"] = new JsonObject { ["id"] = c.Container.Id, ["type"] = c.Container.Type },
            ["extensions"] = new JsonObject
            {
                ["location"] = c.Inline ? "inline" : "footer",
                ["inlineProperties"] = c.Inline ? new JsonObject { ["originalSelection"] = c.Selection, ["markerRef"] = "m-" + c.Id } : null,
            },
        };
    }
}
