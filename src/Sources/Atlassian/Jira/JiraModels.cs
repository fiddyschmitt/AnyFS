using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnyFS.Sources.Atlassian.Jira;

/// <summary>What a Jira source's address names (spec 7.10).</summary>
internal enum JiraScopeKind
{
    /// <summary>The whole server: every project the account can browse.</summary>
    Server,

    /// <summary>One project's issues.</summary>
    Project,

    /// <summary>A saved filter's issues.</summary>
    Filter,

    /// <summary>A board's issues (its filter's).</summary>
    Board,

    /// <summary>The issues a JQL query finds.</summary>
    Jql,
}

/// <summary>A Jira source's scope: what kind, and its key, id or query.</summary>
internal sealed partial record JiraScope(JiraScopeKind Kind, string Value)
{
    public static readonly JiraScope Server = new(JiraScopeKind.Server, "");

    /// <summary>
    /// The scope the rest of an address names (below the installation): a project (<c>projects/ABC</c>, <c>browse/ABC</c>,
    /// an issue's <c>browse/ABC-123</c>, a project's settings), a saved filter (<c>issues/?filter=10200</c>), a board
    /// (<c>secure/RapidBoard.jspa?rapidView=12</c>), a query (<c>issues/?jql=…</c>), else the whole server for nothing
    /// below it. Null, with why, for an address that names nothing a source can show.
    /// </summary>
    public static JiraScope? From(AtlassianAddress address, out string? problem)
    {
        problem = null;
        var rest = address.Rest;
        var query = address.Query;
        string? Seg(int i) => i < rest.Count ? rest[i] : null;

        if (query.TryGetValue("jql", out var jql) && !string.IsNullOrWhiteSpace(jql))
        {
            return new JiraScope(JiraScopeKind.Jql, jql.Trim());
        }

        string? ownFilter = null;
        if (query.TryGetValue("filter", out var filter) && filter.Length > 0)
        {
            if (long.TryParse(filter, out var id) && id > 0)
            {
                return new JiraScope(JiraScopeKind.Filter, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            // one of Jira's own (allopenissues, myopenissues...), which has no address of its own: a project's issues page
            // names the project with it, and that is the scope; alone, nothing a source shows
            ownFilter = $"The filter {filter} is one of Jira's own (my open issues, reported by me...), which has no address of its own: save it as a filter, or give its query (issues/?jql=...).";
        }

        if (Seg(0) is { } first && first.Equals("secure", StringComparison.OrdinalIgnoreCase) && Seg(1) is { } page && page.Equals("RapidBoard.jspa", StringComparison.OrdinalIgnoreCase)
            && query.TryGetValue("rapidView", out var board) && long.TryParse(board, out var boardId) && boardId > 0)
        {
            return new JiraScope(JiraScopeKind.Board, boardId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (Seg(0) is { } p && (p.Equals("projects", StringComparison.OrdinalIgnoreCase) || p.Equals("browse", StringComparison.OrdinalIgnoreCase)) && Seg(1) is { } key)
        {
            if (IssueKey().Match(key) is { Success: true } issue)
            {
                return new JiraScope(JiraScopeKind.Project, issue.Groups["project"].Value.ToUpperInvariant());
            }

            if (ProjectKey().IsMatch(key))
            {
                return new JiraScope(JiraScopeKind.Project, key.ToUpperInvariant());
            }
        }

        if (rest.Count >= 4 && rest[0].Equals("plugins", StringComparison.OrdinalIgnoreCase) && rest[2].Equals("project-config", StringComparison.OrdinalIgnoreCase) && ProjectKey().IsMatch(rest[3]))
        {
            return new JiraScope(JiraScopeKind.Project, rest[3].ToUpperInvariant());
        }

        if (ownFilter is not null)
        {
            problem = ownFilter;
            return null;
        }

        if (rest.Count == 0 || rest is ["secure", "Dashboard.jspa"] || rest is ["issues"] || rest.Count == 1 && rest[0].Equals("default.jsp", StringComparison.OrdinalIgnoreCase))
        {
            return Server;
        }

        problem = "The address names nothing a Jira source shows: give the server's address, a project's (…/projects/ABC), a saved filter's (…/issues/?filter=10200) or a board's.";
        return null;
    }

    /// <summary>A project's key, as Jira's default pattern and the usual custom ones make them.</summary>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    public static partial Regex ProjectKey();

    /// <summary>An issue's key: its project's key, a dash, its number.</summary>
    [GeneratedRegex(@"^(?<project>[A-Za-z][A-Za-z0-9_]*)-(?<number>\d+)$", RegexOptions.CultureInvariant)]
    public static partial Regex IssueKey();

    /// <summary>The issue key an issue folder's name starts with (<c>ABC-123 Summary</c>), or null.</summary>
    public static string? KeyOfFolder(string name)
    {
        var space = name.IndexOf(' ');
        var head = space < 0 ? name : name[..space];
        return IssueKey().IsMatch(head) ? head.ToUpperInvariant() : null;
    }

    /// <summary>The project key a project folder's name ends with (<c>Alpha project (ABC)</c>), or null.</summary>
    public static string? KeyOfProjectFolder(string name)
    {
        if (!name.EndsWith(')'))
        {
            return null;
        }

        var open = name.LastIndexOf('(');
        var key = open < 0 ? "" : name[(open + 1)..^1];
        return ProjectKey().IsMatch(key) ? key.ToUpperInvariant() : null;
    }

    /// <summary>A JQL query as a condition to add to: its ordering cut off (a filter's <c>ORDER BY</c>), in brackets.</summary>
    public static string Condition(string jql)
    {
        var order = OrderBy().Match(jql);
        var where = (order.Success ? jql[..order.Index] : jql).Trim();
        return where.Length == 0 ? "" : $"({where})";
    }

    [GeneratedRegex(@"\border\s+by\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OrderBy();

    /// <summary>JQL's quoting of a value: in double quotes, its quotes and backslashes escaped.</summary>
    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/// <summary>A project, as Jira's project list and project call give it.</summary>
internal sealed record JiraProject(string Id, string Key, string Name, string? TypeKey, AtlassianUser? Lead, string? Description, string? Url)
{
    public static JiraProject Parse(JsonElement e) => new(
        e.Str("id") ?? "",
        e.Str("key") ?? "",
        e.Str("name") ?? e.Str("key") ?? "",
        e.Str("projectTypeKey"),
        AtlassianUser.Person(e.Obj("lead")),
        e.Str("description"),
        e.Str("url"));
}

/// <summary>An issue as a folder listing needs it: its key and summary (the folder's name) and its times.</summary>
internal sealed record JiraIssueHead(string Id, string Key, string Summary, DateTimeOffset? Created, DateTimeOffset? Updated)
{
    public static JiraIssueHead Parse(JsonElement e)
    {
        var f = e.Obj("fields");
        return new JiraIssueHead(e.Str("id") ?? "", e.Str("key") ?? "", f.Str("summary") ?? "", f.Date("created"), f.Date("updated"));
    }
}

/// <summary>A file attached to an issue.</summary>
internal sealed record JiraAttachment(string Id, string FileName, long Size, string? MimeType, DateTimeOffset? Created, AtlassianUser? Author, string? Content)
{
    public static JiraAttachment Parse(JsonElement e) => new(
        e.Str("id") ?? "",
        e.Str("filename") ?? "attachment",
        e.Long("size"),
        e.Str("mimeType"),
        e.Date("created"),
        AtlassianUser.Person(e.Obj("author")),
        e.Str("content"));
}

/// <summary>A comment on an issue: its author, times, the text as written (wiki markup) and as Jira renders it (HTML).</summary>
internal sealed record JiraComment(string Id, AtlassianUser? Author, DateTimeOffset? Created, DateTimeOffset? Updated, AtlassianUser? UpdateAuthor, string Body, string? Html, string? Visibility)
{
    public static JiraComment Parse(JsonElement e, string? html) => new(
        e.Str("id") ?? "",
        AtlassianUser.Person(e.Obj("author")),
        e.Date("created"),
        e.Date("updated"),
        AtlassianUser.Person(e.Obj("updateAuthor")),
        e.Str("body") ?? "",
        html ?? e.Str("renderedBody"),
        e.Obj("visibility") is { ValueKind: JsonValueKind.Object } v ? $"{v.Str("type")} {v.Str("value")}" : null);
}

/// <summary>
/// An issue as a search or the issue call gives it with every field, Jira's rendering of its text fields
/// (<c>renderedFields</c>) and the fields' names (<c>names</c>): what its folder and its issue.md show. The JSON is
/// kept (cloned, so it outlives the response) for the document to read.
/// </summary>
internal sealed class JiraIssue
{
    private JiraIssue(JsonElement issue, IReadOnlyDictionary<string, string> names)
    {
        Raw = issue;
        Fields = issue.Obj("fields");
        Rendered = issue.Obj("renderedFields");
        Names = names;
        Id = issue.Str("id") ?? "";
        Key = issue.Str("key") ?? "";
        Summary = Fields.Str("summary") ?? "";
        Created = Fields.Date("created");
        Updated = Fields.Date("updated");
        Attachments = Fields.Arr("attachment").Select(JiraAttachment.Parse).OrderBy(a => a.Created ?? DateTimeOffset.MinValue).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
    }

    public JsonElement Raw { get; }

    public JsonElement Fields { get; }

    public JsonElement Rendered { get; }

    public IReadOnlyDictionary<string, string> Names { get; }

    public string Id { get; }

    public string Key { get; }

    public string Summary { get; }

    public DateTimeOffset? Created { get; }

    public DateTimeOffset? Updated { get; }

    /// <summary>The issue's attachments, oldest first (the order a name taken twice is numbered in).</summary>
    public IReadOnlyList<JiraAttachment> Attachments { get; }

    /// <summary>The project's key, from the issue's.</summary>
    public string ProjectKey => Key.Contains('-') ? Key[..Key.LastIndexOf('-')] : Key;

    /// <summary>The issue's number in its project (sorting).</summary>
    public long Number => Key.LastIndexOf('-') is var dash and > 0 && long.TryParse(Key[(dash + 1)..], out var n) ? n : 0;

    /// <summary>The comments the response carried, and how many the issue has (more than it carried when Jira caps them).</summary>
    public (IReadOnlyList<JsonElement> Comments, long Total) CarriedComments
    {
        get
        {
            var comment = Fields.Obj("comment");
            var comments = comment.Arr("comments").ToList();
            var total = comment.Has("total") ? comment.Long("total") : comments.Count;
            return (comments, Math.Max(total, comments.Count));
        }
    }

    /// <summary>An issue from a search's <c>issues</c> element or the issue call's answer, with the names the response gave (the issue's own, else the search's).</summary>
    public static JiraIssue Parse(JsonElement issue, IReadOnlyDictionary<string, string>? names = null)
    {
        var clone = issue.Clone();
        var own = clone.Obj("names");
        IReadOnlyDictionary<string, string> fieldNames = own.ValueKind == JsonValueKind.Object
            ? own.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : names ?? new Dictionary<string, string>(StringComparer.Ordinal);
        return new JiraIssue(clone, fieldNames);
    }

    /// <summary>A search response's <c>names</c>: each field's name by its id.</summary>
    public static IReadOnlyDictionary<string, string> NamesOf(JsonElement response)
        => response.Obj("names") is { ValueKind: JsonValueKind.Object } names
            ? names.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
}
