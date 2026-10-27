using System.Globalization;
using System.Text.Json.Nodes;

namespace AnyFS.Sources.Tests;

/// <summary>The fake's issues, merge requests, their comments and changes, and the files uploaded to a project (spike E's shapes).</summary>
internal sealed partial class FakeGitLab
{
    /// <summary>A comment; GitLab's own ("assigned to @x") are system notes.</summary>
    public sealed record Note(string Body, User Author, DateTimeOffset Created, bool System = false);

    /// <summary>One file's change in a merge request, as GitLab's /diffs gives it.</summary>
    public sealed record Diff(string OldPath, string NewPath, string Text, bool NewFile = false, bool Deleted = false, bool Renamed = false, bool TooLarge = false);

    /// <summary>An issue or a merge request.</summary>
    public sealed class Ticket(long iid, string title, string description, User author, DateTimeOffset created)
    {
        public long Iid { get; } = iid;

        public string Title { get; set; } = title;

        public string Description { get; set; } = description;

        public User Author { get; } = author;

        public string State { get; set; } = "opened";

        public List<string> Labels { get; } = [];

        public string? Milestone { get; set; }

        public List<User> Assignees { get; } = [];

        public DateTimeOffset Created { get; } = created;

        public DateTimeOffset Updated { get; set; } = created;

        public DateTimeOffset? Closed { get; set; }

        public string? SourceBranch { get; set; }

        public string? TargetBranch { get; set; }

        public List<Note> Notes { get; } = [];

        public List<Diff> Diffs { get; } = [];
    }

    public sealed partial class Project
    {
        public List<Ticket> IssueList { get; } = [];

        public List<Ticket> MergeRequestList { get; } = [];

        /// <summary>The files uploaded to the project (issue, merge request and release attachments) by "secret/name".</summary>
        public Dictionary<string, byte[]> Uploads { get; } = new(StringComparer.Ordinal);
    }

    private Response? Tickets(Project project, Request r, string[] rest)
    {
        switch (rest)
        {
            case ["issues"]:
                return project.Issues ? Paged(r, Filter(project.IssueList, r).Select(t => TicketJson(project, t, false))) : Response.Message(403, "403 Forbidden");
            case ["merge_requests"]:
                return project.MergeRequests ? Paged(r, Filter(project.MergeRequestList, r).Select(t => TicketJson(project, t, true))) : Response.Message(403, "403 Forbidden");
            case [var kind and ("issues" or "merge_requests"), var iid, "notes"]:
                return Find(project, kind, iid) is { } ticket
                    ? Paged(r, ticket.Notes.Select((n, i) => (JsonNode)new JsonObject
                    {
                        ["id"] = 1000 + i,
                        ["body"] = n.Body,
                        ["author"] = UserJson(n.Author),
                        ["created_at"] = n.Created.ToString("O", CultureInfo.InvariantCulture),
                        ["system"] = n.System,
                    }))
                    : Response.NotFound("404 Not Found");
            case ["merge_requests", var iid, "diffs"]:
                return Find(project, "merge_requests", iid) is { } mr
                    ? Paged(r, mr.Diffs.Select(d => (JsonNode)new JsonObject
                    {
                        ["old_path"] = d.OldPath,
                        ["new_path"] = d.NewPath,
                        ["a_mode"] = d.NewFile ? "0" : "100644",
                        ["b_mode"] = d.Deleted ? "0" : "100644",
                        ["diff"] = d.Text,
                        ["new_file"] = d.NewFile,
                        ["renamed_file"] = d.Renamed,
                        ["deleted_file"] = d.Deleted,
                        ["too_large"] = d.TooLarge,
                    }))
                    : Response.NotFound("404 Not Found");
            case ["uploads", var secret, var file]:
                return project.Uploads.TryGetValue($"{secret}/{file}", out var bytes)
                    ? new Response(200, bytes, "application/octet-stream", new() { ["Content-Length"] = bytes.Length.ToString(CultureInfo.InvariantCulture) })
                    : Response.NotFound("404 Not found");
            default:
                return null;
        }
    }

    private static IEnumerable<Ticket> Filter(List<Ticket> tickets, Request r)
    {
        var state = r.Q("state") ?? "opened";
        var after = r.Q("updated_after") is { } text ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
        return tickets.Where(t => (state == "all" || t.State == state) && (after is null || t.Updated > after))
            .OrderBy(t => r.Q("order_by") == "updated_at" ? t.Updated : t.Created).ThenBy(t => t.Iid);
    }

    private static Ticket? Find(Project project, string kind, string iid)
        => (kind == "issues" ? project.IssueList : project.MergeRequestList).FirstOrDefault(t => t.Iid.ToString(CultureInfo.InvariantCulture) == iid);

    private JsonNode TicketJson(Project p, Ticket t, bool mr) => new JsonObject
    {
        ["id"] = 5000 + t.Iid,
        ["iid"] = t.Iid,
        ["title"] = t.Title,
        ["description"] = t.Description,
        ["state"] = t.State,
        ["author"] = UserJson(t.Author),
        ["assignees"] = new JsonArray(t.Assignees.Select(a => (JsonNode)UserJson(a)).ToArray()),
        ["reviewers"] = new JsonArray(),
        ["labels"] = new JsonArray(t.Labels.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
        ["milestone"] = t.Milestone is null ? null : new JsonObject { ["title"] = t.Milestone },
        ["created_at"] = t.Created.ToString("O", CultureInfo.InvariantCulture),
        ["updated_at"] = t.Updated.ToString("O", CultureInfo.InvariantCulture),
        ["closed_at"] = t.Closed?.ToString("O", CultureInfo.InvariantCulture),
        ["merged_at"] = null,
        ["source_branch"] = t.SourceBranch,
        ["target_branch"] = t.TargetBranch,
        ["web_url"] = $"{ExternalUrl}/{p.FullPath}/-/{(mr ? "merge_requests" : "issues")}/{t.Iid}",
    };
}
