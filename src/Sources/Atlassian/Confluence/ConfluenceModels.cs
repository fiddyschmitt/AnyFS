using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Atlassian.Confluence;

/// <summary>What a Confluence source's address names (spec 7.10).</summary>
internal enum ConfluenceScopeKind
{
    /// <summary>The whole server: every space the account can read.</summary>
    Server,

    /// <summary>One space's pages (and blog posts).</summary>
    Space,

    /// <summary>A page and the pages below it.</summary>
    Page,
}

/// <summary>
/// A Confluence source's scope: what kind, its space, and for a page its id, or its title (and a blog post's day) where the
/// address names it so, which the server is asked for at the start.
/// </summary>
internal sealed partial record ConfluenceScope(ConfluenceScopeKind Kind, string Space = "", string? PageId = null, string? Title = null, DateOnly? PostingDay = null)
{
    public static readonly ConfluenceScope Server = new(ConfluenceScopeKind.Server);

    /// <summary>
    /// The scope the rest of an address names (below the installation): a space (<c>display/KEY</c>, <c>spaces/KEY/…</c>,
    /// <c>spaces/viewspace.action?key=KEY</c>), a page (<c>pages/viewpage.action?pageId=123</c>,
    /// <c>display/KEY/Page+Title</c>, <c>spaces/KEY/pages/123/…</c>, a tiny link <c>x/AbCd</c>), a blog post
    /// (<c>display/KEY/2026/10/30/Title</c>), else the whole server for nothing below it. Null, with why, for an address
    /// that names nothing a source can show.
    /// </summary>
    public static ConfluenceScope? From(AtlassianAddress address, out string? problem)
    {
        problem = null;
        var rest = address.Rest;
        var query = address.Query;
        string? Seg(int i) => i < rest.Count ? rest[i] : null;
        bool Is(int i, string value) => Seg(i) is { } s && s.Equals(value, StringComparison.OrdinalIgnoreCase);

        if (query.TryGetValue("pageId", out var pageId) && long.TryParse(pageId, out var id) && id > 0)
        {
            return new ConfluenceScope(ConfluenceScopeKind.Page, PageId: id.ToString(CultureInfo.InvariantCulture));
        }

        if (Is(0, "pages") && query.TryGetValue("spaceKey", out var titled) && query.TryGetValue("title", out var title) && title.Length > 0)
        {
            return new ConfluenceScope(ConfluenceScopeKind.Page, titled, Title: title);
        }

        if (Is(0, "x") && Seg(1) is { } tiny && TinyId(tiny) is { } tinyId)
        {
            return new ConfluenceScope(ConfluenceScopeKind.Page, PageId: tinyId.ToString(CultureInfo.InvariantCulture));
        }

        if (Is(0, "display") && Seg(1) is { } displayed)
        {
            if (rest.Count == 2)
            {
                return new ConfluenceScope(ConfluenceScopeKind.Space, displayed);
            }

            // a blog post: display/KEY/yyyy/MM/dd/Title
            if (rest.Count >= 6 && TryDay(rest[2], rest[3], rest[4], out var posted))
            {
                return new ConfluenceScope(ConfluenceScopeKind.Page, displayed, Title: rest[5], PostingDay: posted);
            }

            return new ConfluenceScope(ConfluenceScopeKind.Page, displayed, Title: rest[2]);
        }

        if (Is(0, "spaces"))
        {
            if (Seg(1) is { } action && action.EndsWith(".action", StringComparison.OrdinalIgnoreCase))
            {
                if (query.TryGetValue("key", out var key) && key.Length > 0)
                {
                    return new ConfluenceScope(ConfluenceScopeKind.Space, key);
                }
            }
            else if (Seg(1) is { } space)
            {
                if (Is(2, "pages") && Seg(3) is { } number && long.TryParse(number, out var page) && page > 0)
                {
                    return new ConfluenceScope(ConfluenceScopeKind.Page, space, PageId: page.ToString(CultureInfo.InvariantCulture));
                }

                // a blog post by its id: spaces/KEY/blog/yyyy/MM/dd/ID/Title, or spaces/KEY/blog/ID
                if (Is(2, "blog") && (rest.Count >= 7 && TryDay(rest[3], rest[4], rest[5], out _) ? Seg(6) : Seg(3)) is { } post
                    && long.TryParse(post, out var postId) && postId > 0)
                {
                    return new ConfluenceScope(ConfluenceScopeKind.Page, space, PageId: postId.ToString(CultureInfo.InvariantCulture));
                }

                return new ConfluenceScope(ConfluenceScopeKind.Space, space);
            }
        }

        if ((Is(0, "collector") || Is(0, "spacedirectory")) && query.TryGetValue("key", out var collected) && collected.Length > 0)
        {
            return new ConfluenceScope(ConfluenceScopeKind.Space, collected);
        }

        if (rest.Count == 0 || rest.Count == 1 && rest[0].ToLowerInvariant() is "index.action" or "dashboard.action" or "spacedirectory" or "welcome.action")
        {
            return Server;
        }

        problem = "The address names nothing a Confluence source shows: give the server's address, a space's (…/display/KEY) or a page's.";
        return null;
    }

    /// <summary>Three address segments as a blog post's posting day (<c>2026/10/30</c>).</summary>
    private static bool TryDay(string year, string month, string day, out DateOnly posted)
    {
        posted = default;
        if (int.TryParse(year, out var y) && int.TryParse(month, out var m) && int.TryParse(day, out var d)
            && y is > 1990 and < 3000 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m))
        {
            posted = new DateOnly(y, m, d);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The page id a tiny link names (<c>x/AbCd</c>): the id's bytes, low first and trailing zeros dropped, in base 64
    /// with <c>-</c> for <c>/</c> and <c>_</c> for <c>+</c> (Confluence's TinyUrl). Null for anything else.
    /// </summary>
    public static long? TinyId(string tiny)
    {
        if (tiny.Length is 0 or > 11 || !TinyChars().IsMatch(tiny))
        {
            return null;
        }

        var base64 = tiny.Replace('-', '/').Replace('_', '+').PadRight(11, 'A') + "=";
        try
        {
            var bytes = Convert.FromBase64String(base64);
            long id = 0;
            for (var i = bytes.Length - 1; i >= 0; i--)
            {
                id = (id << 8) | bytes[i];
            }

            return id > 0 ? id : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9\-_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TinyChars();

    /// <summary>CQL's quoting of a value: in double quotes, its quotes and backslashes escaped.</summary>
    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/// <summary>A space, as the space list gives it.</summary>
internal sealed record ConfluenceSpace(string Key, string Name, string Type, string Status, string? HomepageId)
{
    public bool Personal => Type.Equals("personal", StringComparison.OrdinalIgnoreCase);

    public bool Archived => Status.Equals("archived", StringComparison.OrdinalIgnoreCase);

    public static ConfluenceSpace Parse(JsonElement e) => new(
        e.Str("key") ?? "",
        e.Str("name") ?? e.Str("key") ?? "",
        e.Str("type") ?? "global",
        e.Str("status") ?? "current",
        e.Obj("homepage").Str("id") ?? (e.Obj("_expandable").Str("homepage") is { } link ? link[(link.LastIndexOf('/') + 1)..] : null));
}

/// <summary>
/// A page or blog post as the content calls give it with its body (the storage format), version, history, labels and
/// ancestors: what its folder and its Markdown show.
/// </summary>
internal sealed record ConfluenceContent(
    string Id,
    string Type,
    string Title,
    string SpaceKey,
    int Version,
    DateTimeOffset? Changed,
    AtlassianUser? ChangedBy,
    DateTimeOffset? Created,
    AtlassianUser? CreatedBy,
    IReadOnlyList<string> Labels,
    string? ParentId,
    string? Storage,
    string? WebUi,
    string? TinyUi)
{
    public bool IsBlogPost => Type.Equals("blogpost", StringComparison.OrdinalIgnoreCase);

    /// <summary>A blog post's day, as Confluence names it in the post's address: its creation's date where it was made.</summary>
    public DateOnly? PostingDay => Created is { } created ? DateOnly.FromDateTime(created.DateTime) : null;

    public static ConfluenceContent Parse(JsonElement e)
    {
        var version = e.Obj("version");
        var history = e.Obj("history");
        var ancestors = e.Arr("ancestors").ToList();
        return new ConfluenceContent(
            e.Str("id") ?? "",
            e.Str("type") ?? "page",
            e.Str("title") ?? "",
            e.Obj("space").Str("key") ?? "",
            (int)version.Long("number"),
            version.Date("when"),
            AtlassianUser.Person(version.Obj("by")),
            history.Date("createdDate"),
            AtlassianUser.Person(history.Obj("createdBy")),
            e.Obj("metadata").Obj("labels").Arr("results").Select(l => l.Str("name")).OfType<string>().ToList(),
            ancestors.Count == 0 ? null : ancestors[^1].Str("id"),
            e.Obj("body").Obj("storage").Str("value"),
            e.Obj("_links").Str("webui"),
            e.Obj("_links").Str("tinyui"));
    }
}

/// <summary>A file attached to a page or blog post.</summary>
internal sealed record ConfluenceAttachment(string Id, string FileName, long Size, string? MediaType, int Version, DateTimeOffset? Changed, string? Download, string? ContainerId)
{
    public static ConfluenceAttachment Parse(JsonElement e, string? containerId = null) => new(
        e.Str("id") ?? "",
        e.Str("title") ?? "attachment",
        e.Obj("extensions").Long("fileSize"),
        e.Obj("extensions").Str("mediaType") ?? e.Obj("metadata").Str("mediaType"),
        (int)e.Obj("version").Long("number"),
        e.Obj("version").Date("when"),
        e.Obj("_links").Str("download"),
        containerId ?? e.Obj("container").Str("id"));
}

/// <summary>A comment on a page or blog post: footer or inline, its author, time and text (storage format), what it answers.</summary>
internal sealed record ConfluenceComment(string Id, string? ContainerId, string? ParentId, AtlassianUser? Author, DateTimeOffset? Created, string Storage, bool Inline, string? Selection)
{
    public static ConfluenceComment Parse(JsonElement e, string? containerId = null)
    {
        var ancestors = e.Arr("ancestors").ToList();
        var extensions = e.Obj("extensions");
        return new ConfluenceComment(
            e.Str("id") ?? "",
            containerId ?? e.Obj("container").Str("id"),
            ancestors.Count == 0 ? null : ancestors[^1].Str("id"),
            AtlassianUser.Person(e.Obj("history").Obj("createdBy")) ?? AtlassianUser.Person(e.Obj("version").Obj("by")),
            e.Obj("history").Date("createdDate") ?? e.Obj("version").Date("when"),
            e.Obj("body").Obj("storage").Str("value") ?? "",
            string.Equals(extensions.Str("location"), "inline", StringComparison.OrdinalIgnoreCase),
            extensions.Obj("inlineProperties").Str("originalSelection"));
    }
}
