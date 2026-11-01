using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A stand-in for a Jira Data Center server (spec 7.10): projects (each visible to everyone or to some accounts), issues
/// with their fields, rendered text, comments and attachments, saved filters and boards; answered as Jira's REST API v2
/// answers: the project list and project call, search (GET and POST) over the JQL the source writes (a project, a key
/// range, an id after another, "not done", a filter's query in brackets, keys in a list; ordered by id, by update or by
/// key), the issue call with <c>renderedFields</c> and <c>names</c>, the comment call, filters, a board's configuration and
/// the attachment download (with Range).
/// </summary>
internal sealed partial class FakeJira(string origin = "https://jira.fake.test", string root = "/jira") : FakeAtlassian(origin, root, "jira", "11.3.12")
{
    private readonly object _gate = new();
    private long _nextIssueId = 10_000;
    private long _nextId = 20_000;

    public List<Project> Projects { get; } = [];

    public List<Issue> Issues { get; } = [];

    public Dictionary<long, (string Name, string Jql)> Filters { get; } = [];

    public Dictionary<long, long> Boards { get; } = [];

    /// <summary>How many comments a search carries for an issue (Jira's own cap where it has one); the comment call has them all.</summary>
    public int CommentsCarried { get; set; } = int.MaxValue;

    /// <summary>The JQL of every search, oldest first.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Searches { get; } = new();

    /// <summary>The most results one search gives (jira.search.views.default.max).</summary>
    public int MaxResults { get; set; } = 1000;

    /// <summary>Whether a search with a key that does not exist in a comparison fails, as Jira's validation does when asked to validate.</summary>
    public bool StrictKeys { get; set; } = true;

    protected override string WhoPath => "rest/api/2/myself";

    protected override JsonNode Who(Account? user) => new JsonObject { ["name"] = user?.Name, ["key"] = user?.Key, ["displayName"] = user?.Display, ["emailAddress"] = user?.Name + "@fake.test" };

    // --- the model -------------------------------------------------------------------------------------------------------

    public sealed class Project(string id, string key, string name)
    {
        public string Id { get; } = id;

        public string Key { get; } = key;

        public string Name { get; set; } = name;

        public string TypeKey { get; set; } = "software";

        public string? Lead { get; set; }

        public string? Description { get; set; }

        public List<(string Name, string? Lead, string? Description)> Components { get; } = [];

        public List<(string Name, bool Released, string? Date)> Versions { get; } = [];

        /// <summary>The accounts that may browse it; null for everyone.</summary>
        public HashSet<string>? VisibleTo { get; set; }
    }

    public sealed class Issue(long id, string key, Project project, string summary)
    {
        public long Id { get; } = id;

        public string Key { get; set; } = key;

        public Project Project { get; set; } = project;

        public string Summary { get; set; } = summary;

        public string Type { get; set; } = "Task";

        public string Status { get; set; } = "To Do";

        public string StatusCategory { get; set; } = "new";

        public string? Resolution { get; set; }

        public string Priority { get; set; } = "Medium";

        public string? Assignee { get; set; }

        public string Reporter { get; set; } = "tester";

        public DateTimeOffset Created { get; set; } = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Updated { get; set; } = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

        public string? Description { get; set; }

        public string? DescriptionHtml { get; set; }

        public List<string> Labels { get; } = [];

        public List<string> Components { get; } = [];

        public List<string> FixVersions { get; } = [];

        public Issue? Parent { get; set; }

        public List<(string Type, string Outward, string Inward, Issue Other, bool OutwardFromHere)> Links { get; } = [];

        public List<Comment> Comments { get; } = [];

        public List<Attachment> Attachments { get; } = [];

        /// <summary>Custom fields: id → (name, value as Jira gives it, rendered HTML or null).</summary>
        public Dictionary<string, (string Name, JsonNode? Value, string? Html)> Custom { get; } = [];

        public long Number => long.Parse(Key[(Key.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture);
    }

    public sealed record Comment(string Id, string Author, string Body, string Html, DateTimeOffset Created);

    public sealed record Attachment(string Id, string FileName, byte[] Bytes, DateTimeOffset Created, string Author = "tester", string MimeType = "application/octet-stream");

    public Project AddProject(string key, string name, params string[] visibleTo)
    {
        lock (_gate)
        {
            var project = new Project((++_nextId).ToString(CultureInfo.InvariantCulture), key, name) { VisibleTo = visibleTo.Length == 0 ? null : [.. visibleTo] };
            Projects.Add(project);
            return project;
        }
    }

    public Issue AddIssue(Project project, string summary, Action<Issue>? fill = null)
    {
        lock (_gate)
        {
            var number = Issues.Where(i => i.Project == project).Select(i => i.Number).DefaultIfEmpty(0).Max() + 1;
            var issue = new Issue(++_nextIssueId, $"{project.Key}-{number}", project, summary);
            fill?.Invoke(issue);
            Issues.Add(issue);
            return issue;
        }
    }

    public Comment AddComment(Issue issue, string author, string body, string html, DateTimeOffset when)
    {
        lock (_gate)
        {
            var comment = new Comment((++_nextId).ToString(CultureInfo.InvariantCulture), author, body, html, when);
            issue.Comments.Add(comment);
            return comment;
        }
    }

    public Attachment AddAttachment(Issue issue, string name, byte[] bytes, DateTimeOffset when)
    {
        lock (_gate)
        {
            var attachment = new Attachment((++_nextId).ToString(CultureInfo.InvariantCulture), name, bytes, when);
            issue.Attachments.Add(attachment);
            return attachment;
        }
    }

    public Issue IssueAt(string key) => Issues.Single(i => i.Key == key);

    // --- answering -------------------------------------------------------------------------------------------------------

    protected override Response AnswerProduct(Request request, string path, Account? user)
    {
        var s = path.Split('/');
        if (path.StartsWith("secure/attachment/", StringComparison.Ordinal) && s.Length >= 3)
        {
            if (user is null)
            {
                return Response.Redirect(Server + "/login.jsp");
            }

            var attachment = Visible(user).SelectMany(i => i.Attachments).FirstOrDefault(a => a.Id == s[2]);
            return attachment is null ? Response.NotFound() : Bytes(request, attachment.Bytes, attachment.MimeType);
        }

        if (Unauthenticated(user) is { } refused)
        {
            return refused;
        }

        lock (_gate)
        {
            return path switch
            {
                "rest/api/2/project" => Response.Json(new JsonArray(Visible(user).Select(i => i.Project).Concat(Projects.Where(p => CanBrowse(user, p))).Distinct().Select(p => (JsonNode)ProjectJson(p, full: false)).ToArray())),
                _ when s is ["rest", "api", "2", "project", var key] => Projects.FirstOrDefault(p => p.Key == key && CanBrowse(user, p)) is { } project ? Response.Json(ProjectJson(project, full: true)) : Response.NotFound($"No project could be found with key '{key}'."),
                "rest/api/2/search" => Search(request, user),
                _ when s is ["rest", "api", "2", "issue", var key] => Visible(user).FirstOrDefault(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } issue
                    ? Response.Json(IssueJson(issue, Fields(request.Q("fields")), Expands(request.Q("expand")), names: true))
                    : Response.NotFound("Issue Does Not Exist"),
                _ when s is ["rest", "api", "2", "issue", var key, "comment"] => Visible(user).FirstOrDefault(i => i.Key == key) is { } issue ? CommentPage(request, issue) : Response.NotFound("Issue Does Not Exist"),
                _ when s is ["rest", "api", "2", "filter", var id] && long.TryParse(id, out var filterId) && Filters.TryGetValue(filterId, out var filter)
                    => Response.Json(new JsonObject { ["id"] = id, ["name"] = filter.Name, ["jql"] = filter.Jql }),
                _ when s is ["rest", "agile", "1.0", "board", var id, "configuration"] && long.TryParse(id, out var boardId) && Boards.TryGetValue(boardId, out var boardFilter)
                    => Response.Json(new JsonObject { ["id"] = boardId, ["filter"] = new JsonObject { ["id"] = boardFilter.ToString(CultureInfo.InvariantCulture) } }),
                _ => Response.NotFound(),
            };
        }
    }

    private static bool CanBrowse(Account? user, Project project) => project.VisibleTo is null || user is not null && project.VisibleTo.Contains(user.Name);

    private IEnumerable<Issue> Visible(Account? user) => Issues.Where(i => CanBrowse(user, i.Project));

    private static HashSet<string>? Fields(string? fields) => fields is null ? null : [.. fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static HashSet<string> Expands(string? expand) => expand is null ? [] : [.. expand.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private Response Search(Request request, Account? user)
    {
        string jql;
        int startAt, maxResults;
        HashSet<string>? fields;
        HashSet<string> expand;
        bool validate;
        if (request.Method == "POST")
        {
            var body = request.JsonBody!;
            jql = body["jql"]?.GetValue<string>() ?? "";
            startAt = body["startAt"]?.GetValue<int>() ?? 0;
            maxResults = body["maxResults"]?.GetValue<int>() ?? 50;
            fields = body["fields"] is JsonArray f ? [.. f.Select(x => x!.GetValue<string>())] : null;
            expand = body["expand"] is JsonArray e ? [.. e.Select(x => x!.GetValue<string>())] : Expands(body["expand"]?.GetValue<string>());
            validate = body["validateQuery"] is not { } v || v.GetValueKind() != System.Text.Json.JsonValueKind.False;
        }
        else
        {
            jql = request.Q("jql") ?? "";
            startAt = request.QInt("startAt", 0);
            maxResults = request.QInt("maxResults", 50);
            fields = Fields(request.Q("fields"));
            expand = Expands(request.Q("expand"));
            validate = request.Q("validateQuery") != "false";
        }

        Searches.Enqueue(jql);
        List<Issue> found;
        try
        {
            found = Query(jql, Visible(user).ToList(), validate);
        }
        catch (FormatException ex)
        {
            return Response.Json(new JsonObject { ["errorMessages"] = new JsonArray((JsonNode)ex.Message), ["errors"] = new JsonObject() }, 400);
        }

        var page = found.Skip(startAt).Take(Math.Min(maxResults, MaxResults)).ToList();
        var response = new JsonObject
        {
            ["startAt"] = startAt,
            ["maxResults"] = Math.Min(maxResults, MaxResults),
            ["total"] = found.Count,
            ["issues"] = new JsonArray(page.Select(i => (JsonNode)IssueJson(i, fields, expand, names: false)).ToArray()),
        };
        if (expand.Contains("names"))
        {
            response["names"] = Names(page);
        }

        return Response.Json(response);
    }

    /// <summary>The issues a JQL query finds, in its order: the conditions the source writes, joined by AND.</summary>
    private List<Issue> Query(string jql, List<Issue> issues, bool validate)
    {
        var order = OrderBy().Match(jql);
        var where = (order.Success ? jql[..order.Index] : jql).Trim();
        var matched = issues.Where(i => Matches(where, i, validate, issues)).ToList();
        var by = order.Success ? order.Groups["by"].Value.Trim().ToLowerInvariant() : "key asc";
        return by switch
        {
            "id asc" => matched.OrderBy(i => i.Id).ToList(),
            "updated desc" => matched.OrderByDescending(i => i.Updated).ThenByDescending(i => i.Id).ToList(),
            "key asc" => matched.OrderBy(i => i.Project.Key, StringComparer.Ordinal).ThenBy(i => i.Number).ToList(),
            _ => throw new FormatException($"The JQL ordering '{by}' is not one the fake knows"),
        };
    }

    private bool Matches(string condition, Issue issue, bool validate, List<Issue> all)
    {
        foreach (var raw in SplitAnd(condition))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            if (part.StartsWith('(') && part.EndsWith(')'))
            {
                if (!Matches(part[1..^1], issue, validate, all))
                {
                    return false;
                }

                continue;
            }

            Match m;
            if ((m = ProjectIs().Match(part)).Success)
            {
                if (!issue.Project.Key.Equals(m.Groups["key"].Value, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else if ((m = UpdatedWithin().Match(part)).Success)
            {
                if (issue.Updated < DateTimeOffset.UtcNow.AddMinutes(-long.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)))
                {
                    return false;
                }
            }
            else if (part.Equals("statusCategory != Done", StringComparison.OrdinalIgnoreCase))
            {
                if (issue.StatusCategory == "done")
                {
                    return false;
                }
            }
            else if ((m = IdAfter().Match(part)).Success)
            {
                if (issue.Id <= long.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture))
                {
                    return false;
                }
            }
            else if ((m = KeyCompare().Match(part)).Success)
            {
                var key = m.Groups["key"].Value;
                if (validate && StrictKeys && !all.Any(i => i.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new FormatException($"An issue with key '{key}' does not exist for field 'issuekey'.");
                }

                var project = key[..key.LastIndexOf('-')];
                var number = long.Parse(key[(key.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture);
                var ok = issue.Project.Key.Equals(project, StringComparison.OrdinalIgnoreCase) && (m.Groups["op"].Value == ">=" ? issue.Number >= number : issue.Number <= number);
                if (!ok)
                {
                    return false;
                }
            }
            else if ((m = KeyIn().Match(part)).Success)
            {
                var keys = m.Groups["keys"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (!keys.Contains(issue.Key, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else
            {
                throw new FormatException($"The JQL '{part}' is not one the fake knows");
            }
        }

        return true;
    }

    /// <summary>A condition's parts joined by AND at the top level (not inside brackets or quotes).</summary>
    private static IEnumerable<string> SplitAnd(string condition)
    {
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < condition.Length; i++)
        {
            var c = condition[i];
            if (c == '"' && (i == 0 || condition[i - 1] != '\\'))
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '(')
            {
                depth++;
            }
            else if (!quoted && c == ')')
            {
                depth--;
            }
            else if (!quoted && depth == 0 && string.Compare(condition, i, " AND ", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
            {
                yield return condition[start..i];
                start = i + 5;
                i += 4;
            }
        }

        yield return condition[start..];
    }

    [GeneratedRegex(@"^project\s*=\s*""?(?<key>[A-Za-z][A-Za-z0-9_]*)""?$", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectIs();

    [GeneratedRegex(@"^id\s*>\s*(?<id>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex IdAfter();

    [GeneratedRegex(@"^updated\s*>=\s*-(?<n>\d+)m$", RegexOptions.IgnoreCase)]
    private static partial Regex UpdatedWithin();

    [GeneratedRegex(@"^issuekey\s*(?<op>>=|<=)\s*(?<key>[A-Za-z][A-Za-z0-9_]*-\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyCompare();

    [GeneratedRegex(@"^(issue)?key\s+in\s*\((?<keys>[^)]*)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyIn();

    [GeneratedRegex(@"\border\s+by\s+(?<by>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OrderBy();

    private Response CommentPage(Request request, Issue issue)
    {
        var start = request.QInt("startAt", 0);
        var max = request.QInt("maxResults", 50);
        var rendered = Expands(request.Q("expand")).Contains("renderedBody");
        var page = issue.Comments.OrderBy(c => c.Created).Skip(start).Take(max).Select(c => (JsonNode)CommentJson(c, rendered)).ToArray();
        return Response.Json(new JsonObject { ["startAt"] = start, ["maxResults"] = max, ["total"] = issue.Comments.Count, ["comments"] = new JsonArray(page) });
    }

    // --- JSON as Jira writes it ----------------------------------------------------------------------------------------

    public static string Time(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + (t.Offset < TimeSpan.Zero ? "-" : "+") + t.Offset.ToString("hhmm", CultureInfo.InvariantCulture);

    private JsonObject Person(string? name)
    {
        if (name is null)
        {
            return null!;
        }

        var account = Accounts.FirstOrDefault(a => a.Name == name);
        return new JsonObject { ["name"] = name, ["key"] = account?.Key ?? name, ["displayName"] = account?.Display ?? name, ["active"] = true };
    }

    private JsonObject ProjectJson(Project p, bool full)
    {
        var json = new JsonObject
        {
            ["self"] = $"{Server}/rest/api/2/project/{p.Id}",
            ["id"] = p.Id,
            ["key"] = p.Key,
            ["name"] = p.Name,
            ["projectTypeKey"] = p.TypeKey,
            ["lead"] = p.Lead is null ? null : Person(p.Lead),
        };
        if (full)
        {
            json["description"] = p.Description ?? "";
            json["components"] = new JsonArray(p.Components.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["description"] = c.Description, ["lead"] = c.Lead is null ? null : Person(c.Lead) }).ToArray());
            json["versions"] = new JsonArray(p.Versions.Select(v => (JsonNode)new JsonObject { ["name"] = v.Name, ["released"] = v.Released, ["archived"] = false, ["releaseDate"] = v.Date }).ToArray());
        }

        return json;
    }

    private static JsonObject Names(IEnumerable<Issue> issues)
    {
        var names = new JsonObject
        {
            ["summary"] = "Summary", ["issuetype"] = "Issue Type", ["status"] = "Status", ["description"] = "Description", ["comment"] = "Comment",
            ["attachment"] = "Attachment", ["created"] = "Created", ["updated"] = "Updated", ["customfield_10000"] = "Rank",
        };
        foreach (var (id, custom) in issues.SelectMany(i => i.Custom))
        {
            names[id] = custom.Name;
        }

        return names;
    }

    private JsonObject CommentJson(Comment c, bool rendered)
    {
        var json = new JsonObject
        {
            ["id"] = c.Id,
            ["author"] = Person(c.Author),
            ["updateAuthor"] = Person(c.Author),
            ["body"] = c.Body,
            ["created"] = Time(c.Created),
            ["updated"] = Time(c.Created),
        };
        if (rendered)
        {
            json["renderedBody"] = c.Html;
        }

        return json;
    }

    /// <summary>An issue as search and the issue call give it: the asked fields (all for none or <c>*all</c>), the rendered ones where asked.</summary>
    private JsonObject IssueJson(Issue i, HashSet<string>? fields, HashSet<string> expand, bool names)
    {
        bool Want(string f) => fields is null || fields.Contains("*all") || fields.Contains(f);
        var carried = i.Comments.OrderBy(c => c.Created).Take(CommentsCarried).ToList();
        var all = new JsonObject
        {
            ["summary"] = i.Summary,
            ["issuetype"] = new JsonObject { ["name"] = i.Type, ["subtask"] = i.Parent is not null },
            ["status"] = new JsonObject { ["name"] = i.Status, ["statusCategory"] = new JsonObject { ["key"] = i.StatusCategory } },
            ["resolution"] = i.Resolution is null ? null : new JsonObject { ["name"] = i.Resolution },
            ["priority"] = new JsonObject { ["name"] = i.Priority },
            ["assignee"] = Person(i.Assignee),
            ["reporter"] = Person(i.Reporter),
            ["creator"] = Person(i.Reporter),
            ["created"] = Time(i.Created),
            ["updated"] = Time(i.Updated),
            ["lastViewed"] = Time(DateTimeOffset.UtcNow),
            ["labels"] = new JsonArray(i.Labels.Select(l => (JsonNode)l).ToArray()),
            ["components"] = new JsonArray(i.Components.Select(c => (JsonNode)new JsonObject { ["name"] = c }).ToArray()),
            ["fixVersions"] = new JsonArray(i.FixVersions.Select(v => (JsonNode)new JsonObject { ["name"] = v }).ToArray()),
            ["versions"] = new JsonArray(),
            ["parent"] = i.Parent is null ? null : new JsonObject { ["key"] = i.Parent.Key, ["fields"] = new JsonObject { ["summary"] = i.Parent.Summary } },
            ["subtasks"] = new JsonArray(Issues.Where(x => x.Parent == i).Select(x => (JsonNode)new JsonObject { ["key"] = x.Key, ["fields"] = new JsonObject { ["summary"] = x.Summary } }).ToArray()),
            ["issuelinks"] = new JsonArray(i.Links.Select(l => (JsonNode)new JsonObject
            {
                ["type"] = new JsonObject { ["name"] = l.Type, ["outward"] = l.Outward, ["inward"] = l.Inward },
                [l.OutwardFromHere ? "outwardIssue" : "inwardIssue"] = new JsonObject { ["key"] = l.Other.Key, ["fields"] = new JsonObject { ["summary"] = l.Other.Summary } },
            }).ToArray()),
            ["description"] = i.Description,
            ["environment"] = null,
            ["comment"] = new JsonObject { ["comments"] = new JsonArray(carried.Select(c => (JsonNode)CommentJson(c, false)).ToArray()), ["maxResults"] = carried.Count, ["total"] = i.Comments.Count, ["startAt"] = 0 },
            ["attachment"] = new JsonArray(i.Attachments.Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a.Id,
                ["filename"] = a.FileName,
                ["size"] = a.Bytes.Length,
                ["mimeType"] = a.MimeType,
                ["created"] = Time(a.Created),
                ["author"] = Person(a.Author),
                ["content"] = $"{Server}/secure/attachment/{a.Id}/{Uri.EscapeDataString(a.FileName)}",
            }).ToArray()),
            ["customfield_10000"] = "0|i0001:",
        };
        foreach (var (id, custom) in i.Custom)
        {
            all[id] = custom.Value?.DeepClone();
        }

        var chosen = new JsonObject();
        foreach (var (name, value) in all)
        {
            if (Want(name))
            {
                chosen[name] = value?.DeepClone();
            }
        }

        var json = new JsonObject { ["id"] = i.Id.ToString(CultureInfo.InvariantCulture), ["key"] = i.Key, ["self"] = $"{Server}/rest/api/2/issue/{i.Id}", ["fields"] = chosen };
        if (expand.Contains("renderedFields"))
        {
            var rendered = new JsonObject
            {
                ["description"] = i.DescriptionHtml,
                ["comment"] = new JsonObject { ["comments"] = new JsonArray(carried.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["body"] = c.Html }).ToArray()) },
            };
            foreach (var (id, custom) in i.Custom.Where(c => c.Value.Html is not null))
            {
                rendered[id] = custom.Html;
            }

            json["renderedFields"] = rendered;
        }

        if (names && expand.Contains("names"))
        {
            json["names"] = Names([i]);
        }

        return json;
    }
}
