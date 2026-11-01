using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnyFS.Sources.Atlassian.Jira;

/// <summary>
/// The files a Jira source makes (spec 7.10): an issue's <c>issue.md</c> — its key and summary, a block of
/// <c>**Label:** value</c> lines (every field with a value, the custom ones under their own names), a rule, the
/// description, then the comments, oldest first — and a project's <c>project.md</c>. The text fields come as Jira renders
/// them (HTML, its wiki markup turned out) and are converted to Markdown, their links to the issue's attachments pointing
/// at the <c>attachments</c> folder beside the file.
/// </summary>
internal static partial class JiraDocuments
{
    /// <summary>
    /// Bumped when what the files hold, or how the tree's folders are named, changes: the sizes in the listings a run
    /// before saved would no longer be the files', so they are not read back.
    /// </summary>
    public const int Format = 1;

    public const string IssueFile = "issue.md";
    public const string ProjectFile = "project.md";
    public const string AttachmentsFolder = "attachments";

    // fields shown in the block in this order, by their ids; the rest of the system fields are left out (they change
    // without the issue's update time moving, as the last time it was viewed, or say little, as the aggregates)
    private static readonly HashSet<string> Shown = new(StringComparer.Ordinal)
    {
        "summary", "issuetype", "status", "resolution", "priority", "assignee", "reporter", "creator", "created", "updated",
        "resolutiondate", "duedate", "labels", "components", "versions", "fixVersions", "parent", "subtasks", "issuelinks",
        "security", "environment", "timetracking", "description", "comment", "attachment", "project",
    };

    private static readonly HashSet<string> LeftOutCustom = new(StringComparer.OrdinalIgnoreCase) { "Rank", "Development", "Last Viewed" };

    /// <summary>The issue's folder name: <c>ABC-123 Install fails on Debian</c>.</summary>
    public static string FolderName(string key, string summary) => AtlassianNames.Safe($"{key} {AtlassianNames.Title(summary)}".Trim());

    /// <summary>The issue's attachments' names in its <c>attachments</c> folder, by attachment id: a name taken twice numbered by age.</summary>
    public static IReadOnlyDictionary<string, string> AttachmentNames(JiraIssue issue)
    {
        var names = AtlassianNames.Unique(issue.Attachments.Select(a => a.FileName).ToList());
        return issue.Attachments.Select((a, i) => (a.Id, Name: names[i])).ToDictionary(x => x.Id, x => x.Name, StringComparer.Ordinal);
    }

    /// <summary>The issue's <c>issue.md</c>.</summary>
    /// <param name="comments">The comments to end with, oldest first; null leaves them out (the Comments setting off).</param>
    /// <param name="attachments">The attachments' names in the folder beside the file, by id; null where it is not shown, and the links point at Jira.</param>
    public static byte[] Issue(JiraIssue issue, IReadOnlyList<JiraComment>? comments, IReadOnlyDictionary<string, string>? attachments, Uri installation)
    {
        var f = issue.Fields;
        var sb = new StringBuilder();
        sb.Append("# ").Append(issue.Key).Append(' ').Append(AtlassianNames.OneLine(issue.Summary)).Append("\n\n");
        AtlassianMarkdown.Line(sb, "Type", f.Obj("issuetype").Str("name"));
        AtlassianMarkdown.Line(sb, "Status", f.Obj("status").Str("name"));
        AtlassianMarkdown.Line(sb, "Resolution", f.Obj("resolution").Str("name") ?? (f.Has("resolution") ? null : "Unresolved"));
        AtlassianMarkdown.Line(sb, "Priority", f.Obj("priority").Str("name"));
        AtlassianMarkdown.Line(sb, "Assignee", AtlassianUser.Person(f.Obj("assignee"))?.ToString() ?? "Unassigned");
        AtlassianMarkdown.Line(sb, "Reporter", AtlassianUser.Person(f.Obj("reporter"))?.ToString());
        if (AtlassianUser.Person(f.Obj("creator")) is { } creator && creator != AtlassianUser.Person(f.Obj("reporter")))
        {
            AtlassianMarkdown.Line(sb, "Creator", creator.ToString());
        }

        AtlassianMarkdown.Line(sb, "Created", AtlassianMarkdown.Date(f.Date("created")));
        AtlassianMarkdown.Line(sb, "Updated", AtlassianMarkdown.Date(f.Date("updated")));
        AtlassianMarkdown.Line(sb, "Resolved", AtlassianMarkdown.Date(f.Date("resolutiondate")));
        AtlassianMarkdown.Line(sb, "Due", f.Str("duedate") is { } due && DateTimeOffset.TryParse(due, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var day) ? AtlassianMarkdown.Day(day) : null);
        AtlassianMarkdown.Line(sb, "Labels", Join(f.Arr("labels").Select(l => l.ValueKind == JsonValueKind.String ? l.GetString() : null)));
        AtlassianMarkdown.Line(sb, "Components", Join(f.Arr("components").Select(c => c.Str("name"))));
        AtlassianMarkdown.Line(sb, "Affects versions", Join(f.Arr("versions").Select(v => v.Str("name"))));
        AtlassianMarkdown.Line(sb, "Fix versions", Join(f.Arr("fixVersions").Select(v => v.Str("name"))));
        AtlassianMarkdown.Line(sb, "Parent", Linked(f.Obj("parent")));
        AtlassianMarkdown.Line(sb, "Sub-tasks", Join(f.Arr("subtasks").Select(Linked), "; "));
        AtlassianMarkdown.Line(sb, "Links", Join(f.Arr("issuelinks").Select(Link), "; "));
        AtlassianMarkdown.Line(sb, "Security level", f.Obj("security").Str("name"));
        AtlassianMarkdown.Line(sb, "Time tracking", TimeTracking(f.Obj("timetracking")));

        // the custom fields with a value, under their own names, short ones as lines and text ones as sections below
        var sections = new List<(string Name, string Markdown)>();
        foreach (var field in f.EnumerateObject().OrderBy(p => issue.Names.GetValueOrDefault(p.Name, p.Name), StringComparer.OrdinalIgnoreCase))
        {
            if (Shown.Contains(field.Name) || !field.Name.StartsWith("customfield_", StringComparison.Ordinal) || field.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            var name = issue.Names.GetValueOrDefault(field.Name, field.Name);
            if (LeftOutCustom.Contains(name) || name.StartsWith("[CHART]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (issue.Rendered.Str(field.Name) is { Length: > 0 } html && IsText(html))
            {
                var text = AtlassianMarkdown.FromHtml(html, installation, url => LinkAttachment(url, attachments));
                if (text.Length > 0)
                {
                    sections.Add((name, text));
                }

                continue;
            }

            AtlassianMarkdown.Line(sb, name, Value(field.Value));
        }

        AtlassianMarkdown.Line(sb, "Web", new Uri(installation, "browse/" + issue.Key).AbsoluteUri);
        sb.Append("\n---\n\n");

        var description = Text(issue, "description", installation, attachments);
        if (description.Length > 0)
        {
            sb.Append(description).Append("\n\n");
        }

        var environment = Text(issue, "environment", installation, attachments);
        if (environment.Length > 0)
        {
            sb.Append("## Environment\n\n").Append(environment).Append("\n\n");
        }

        foreach (var (name, markdown) in sections)
        {
            sb.Append("## ").Append(AtlassianNames.OneLine(name)).Append("\n\n").Append(markdown).Append("\n\n");
        }

        if (comments is not null && comments.Count > 0)
        {
            sb.Append("## Comments\n");
            foreach (var comment in comments)
            {
                sb.Append("\n### ").Append(comment.Author?.ToString() ?? "Anonymous").Append(", ").Append(AtlassianMarkdown.Date(comment.Created) ?? "");
                if (comment.Updated is { } edited && comment.Created is { } made && edited - made > TimeSpan.FromMinutes(1))
                {
                    sb.Append(" (edited ").Append(AtlassianMarkdown.Date(edited));
                    if (comment.UpdateAuthor is { } editor && editor != comment.Author)
                    {
                        sb.Append(" by ").Append(editor);
                    }

                    sb.Append(')');
                }

                if (comment.Visibility is { } visible)
                {
                    sb.Append(" (visible to ").Append(visible).Append(')');
                }

                sb.Append("\n\n");
                var body = comment.Html is { } html
                    ? AtlassianMarkdown.FromHtml(html, installation, url => LinkAttachment(url, attachments))
                    : AtlassianMarkdown.Tidy(comment.Body);
                if (body.Length > 0)
                {
                    sb.Append(body).Append('\n');
                }
            }
        }

        return AtlassianMarkdown.Bytes(sb);
    }

    /// <summary>A project's <c>project.md</c>: its name and key, lead, type, description, components and versions, its address.</summary>
    public static byte[] Project(JsonElement project, Uri installation)
    {
        var p = JiraProject.Parse(project);
        var sb = new StringBuilder();
        sb.Append("# ").Append(AtlassianNames.OneLine(p.Name)).Append(" (").Append(p.Key).Append(")\n\n");
        AtlassianMarkdown.Line(sb, "Key", p.Key);
        AtlassianMarkdown.Line(sb, "Type", p.TypeKey);
        AtlassianMarkdown.Line(sb, "Lead", p.Lead?.ToString());
        AtlassianMarkdown.Line(sb, "Category", project.Obj("projectCategory").Str("name"));
        AtlassianMarkdown.Line(sb, "Web", new Uri(installation, "projects/" + Uri.EscapeDataString(p.Key)).AbsoluteUri);
        AtlassianMarkdown.Line(sb, "URL", p.Url);
        sb.Append("\n---\n\n");
        if (!string.IsNullOrWhiteSpace(p.Description))
        {
            // a project's description is HTML where Jira lets admins write it so, plain text otherwise
            sb.Append(p.Description.Contains('<') ? AtlassianMarkdown.FromHtml(p.Description, installation) : AtlassianMarkdown.Tidy(p.Description)).Append("\n\n");
        }

        var components = project.Arr("components").ToList();
        if (components.Count > 0)
        {
            sb.Append("## Components\n\n");
            foreach (var c in components.OrderBy(c => c.Str("name"), StringComparer.OrdinalIgnoreCase))
            {
                sb.Append("- ").Append(AtlassianNames.OneLine(c.Str("name")));
                if (AtlassianUser.Person(c.Obj("lead")) is { } lead)
                {
                    sb.Append(" (lead ").Append(lead).Append(')');
                }

                if (c.Str("description") is { Length: > 0 } description)
                {
                    sb.Append(": ").Append(AtlassianNames.OneLine(description));
                }

                sb.Append('\n');
            }

            sb.Append('\n');
        }

        var versions = project.Arr("versions").ToList();
        if (versions.Count > 0)
        {
            sb.Append("## Versions\n\n");
            foreach (var v in versions)
            {
                sb.Append("- ").Append(AtlassianNames.OneLine(v.Str("name")));
                var state = v.Bool("archived") ? "archived" : v.Bool("released") ? "released" : "unreleased";
                sb.Append(" (").Append(state);
                if (v.Str("releaseDate") is { Length: > 0 } released)
                {
                    sb.Append(' ').Append(released);
                }

                sb.Append(')');
                if (v.Str("description") is { Length: > 0 } description)
                {
                    sb.Append(": ").Append(AtlassianNames.OneLine(description));
                }

                sb.Append('\n');
            }
        }

        return AtlassianMarkdown.Bytes(sb);
    }

    /// <summary>A text field as Markdown: Jira's rendering of it where the response has one, else the text as written.</summary>
    private static string Text(JiraIssue issue, string field, Uri installation, IReadOnlyDictionary<string, string>? attachments)
        => issue.Rendered.Str(field) is { Length: > 0 } html
            ? AtlassianMarkdown.FromHtml(html, installation, url => LinkAttachment(url, attachments))
            : AtlassianMarkdown.Tidy(issue.Fields.Str(field) ?? "");

    /// <summary>
    /// An attachment's address in Jira (<c>…/secure/attachment/10001/screen.png</c>, or its thumbnail's) as the path to its
    /// file in the folder beside the document; null for any other address, which stays as it is.
    /// </summary>
    internal static string? LinkAttachment(Uri url, IReadOnlyDictionary<string, string>? attachments)
    {
        if (attachments is null)
        {
            return null;
        }

        var match = AttachmentPath().Match(url.AbsolutePath);
        return match.Success && attachments.TryGetValue(match.Groups["id"].Value, out var name) ? AtlassianMarkdown.RelativeLink(AttachmentsFolder, name) : null;
    }

    [GeneratedRegex(@"/secure/(?:attachment|thumbnail)/(?<id>\d+)/", RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentPath();

    /// <summary>Whether a rendered custom field is text worth a section (a paragraph, a list, a table), not a value.</summary>
    private static bool IsText(string html) => html.Contains("<p", StringComparison.OrdinalIgnoreCase) || html.Contains("<ul", StringComparison.OrdinalIgnoreCase)
        || html.Contains("<ol", StringComparison.OrdinalIgnoreCase) || html.Contains("<table", StringComparison.OrdinalIgnoreCase) || html.Contains("<pre", StringComparison.OrdinalIgnoreCase);

    /// <summary>Another issue as a field names it (a parent, a sub-task, a link's end): <c>ABC-12 Its summary</c>.</summary>
    private static string? Linked(JsonElement other)
        => other.Str("key") is { } key ? $"{key} {AtlassianNames.OneLine(other.Obj("fields").Str("summary"))}".Trim() : null;

    /// <summary>An issue link as this issue's end reads it: <c>blocks ABC-12 Its summary</c>.</summary>
    private static string? Link(JsonElement link)
    {
        var type = link.Obj("type");
        if (link.Has("outwardIssue"))
        {
            return $"{type.Str("outward") ?? type.Str("name")} {Linked(link.Obj("outwardIssue"))}";
        }

        return link.Has("inwardIssue") ? $"{type.Str("inward") ?? type.Str("name")} {Linked(link.Obj("inwardIssue"))}" : null;
    }

    private static string? TimeTracking(JsonElement t)
    {
        var parts = new List<string>();
        if (t.Str("originalEstimate") is { Length: > 0 } estimate)
        {
            parts.Add(estimate + " estimated");
        }

        if (t.Str("timeSpent") is { Length: > 0 } spent)
        {
            parts.Add(spent + " logged");
        }

        if (t.Str("remainingEstimate") is { Length: > 0 } remaining)
        {
            parts.Add(remaining + " remaining");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>
    /// A custom field's value as text: a person by name, an option by its value (a cascading one's child after it), a
    /// version, group or component by name, an issue by key, a time as the documents write it, a list joined; null for
    /// nothing worth a line.
    /// </summary>
    internal static string? Value(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String:
                var s = v.GetString() ?? "";
                if (SprintText().Match(s) is { Success: true } sprint)
                {
                    return sprint.Groups["name"].Value;
                }

                if (DateTimeText().IsMatch(s) && Json.ParseDate(s) is { } time)
                {
                    return AtlassianMarkdown.Date(time);
                }

                return s.Length == 0 || s is "{}" ? null : s;
            case JsonValueKind.Number:
                return v.GetRawText();
            case JsonValueKind.True:
                return "yes";
            case JsonValueKind.False:
                return "no";
            case JsonValueKind.Array:
                return Join(v.EnumerateArray().Select(Value), ", ");
            case JsonValueKind.Object:
                if (v.Has("displayName"))
                {
                    return AtlassianUser.Person(v)?.ToString();
                }

                if (v.Str("value") is { } option)
                {
                    return v.Obj("child").Str("value") is { } child ? $"{option} - {child}" : option;
                }

                return v.Str("name") ?? v.Str("key") ?? v.Str("title");
            default:
                return null;
        }
    }

    // Jira Software's sprint field as REST v2 writes it: com.atlassian.greenhopper.service.sprint.Sprint@1a2b[id=1,...,name=Sprint 1,...]
    [GeneratedRegex(@"^com\.atlassian\.greenhopper\.service\.sprint\.Sprint@.*?[\[,]name=(?<name>[^,\]]*)", RegexOptions.CultureInvariant)]
    private static partial Regex SprintText();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}", RegexOptions.CultureInvariant)]
    private static partial Regex DateTimeText();

    private static string? Join(IEnumerable<string?> values, string separator = ", ")
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
        return list.Count == 0 ? null : string.Join(separator, list);
    }
}
