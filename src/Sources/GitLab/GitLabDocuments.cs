using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnyFS.Sources.GitLab;

/// <summary>Someone GitLab names: their name and handle.</summary>
internal sealed record GlPerson(string Name, string Username)
{
    public static GlPerson? Parse(JsonElement e)
        => e.ValueKind == JsonValueKind.Object && e.Str("username") is { } username ? new GlPerson(e.Str("name") ?? username, username) : null;

    public override string ToString() => $"{Name} (@{Username})";
}

/// <summary>An issue or a merge request: what its folder and Markdown show.</summary>
internal sealed record GlTicket(
    long Iid,
    string Title,
    string Description,
    string State,
    GlPerson? Author,
    IReadOnlyList<GlPerson> Assignees,
    IReadOnlyList<GlPerson> Reviewers,
    IReadOnlyList<string> Labels,
    string? Milestone,
    DateTimeOffset? Created,
    DateTimeOffset? Updated,
    DateTimeOffset? Closed,
    DateTimeOffset? Merged,
    string? SourceBranch,
    string? TargetBranch,
    string WebUrl)
{
    public static GlTicket Parse(JsonElement e) => new(
        e.Long("iid"),
        e.Str("title") ?? "",
        e.Str("description") ?? "",
        e.Str("state") ?? "",
        GlPerson.Parse(e.Obj("author")),
        e.Arr("assignees").Select(GlPerson.Parse).OfType<GlPerson>().ToList(),
        e.Arr("reviewers").Select(GlPerson.Parse).OfType<GlPerson>().ToList(),
        e.Arr("labels").Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!).ToList(),
        e.Obj("milestone").Str("title"),
        e.Date("created_at"),
        e.Date("updated_at"),
        e.Date("closed_at"),
        e.Date("merged_at"),
        e.Str("source_branch"),
        e.Str("target_branch"),
        e.Str("web_url") ?? "");
}

/// <summary>A comment on an issue or merge request; GitLab's own notes ("changed the description") are system notes.</summary>
internal sealed record GlNote(long Id, string Body, GlPerson? Author, DateTimeOffset? Created, bool System)
{
    public static GlNote Parse(JsonElement e) => new(e.Long("id"), e.Str("body") ?? "", GlPerson.Parse(e.Obj("author")), e.Date("created_at"), e.Bool("system"));
}

/// <summary>One file's change in a merge request.</summary>
internal sealed record GlDiff(string OldPath, string NewPath, string AMode, string BMode, string Diff, bool NewFile, bool Renamed, bool Deleted, bool TooLarge)
{
    public static GlDiff Parse(JsonElement e) => new(
        e.Str("old_path") ?? "", e.Str("new_path") ?? "", e.Str("a_mode") ?? "", e.Str("b_mode") ?? "", e.Str("diff") ?? "",
        e.Bool("new_file"), e.Bool("renamed_file"), e.Bool("deleted_file"), e.Bool("too_large") || e.Bool("collapsed"));
}

/// <summary>A file attached to an issue's, merge request's or wiki page's text, as GitLab links it: <c>/uploads/&lt;secret&gt;/&lt;name&gt;</c>.</summary>
internal sealed record GlUpload(string Secret, string FileName)
{
    /// <summary>The name as a file shows it (the link's is URL-encoded).</summary>
    public string Name => Uri.UnescapeDataString(FileName);
}

/// <summary>
/// The files the source makes from GitLab's data (spec 7.8), as Outlook makes a message's (7.7): a title, a block of
/// <c>**Label:** value</c> lines, a rule, then the text as GitLab keeps it, its attachments' links pointing at the folder's
/// <c>attachments</c> beside it. UTF-8 without a byte order mark.
/// </summary>
internal static partial class GitLabDocuments
{
    /// <summary>
    /// Bumped when what the files made here hold, or what the tree's folders are named, changes: the sizes in the listings
    /// a run before saved would no longer be the files', so they are not read back.
    /// </summary>
    public const int Format = 1;

    [GeneratedRegex(@"(?<![\w/])/uploads/(?<secret>[0-9a-f]{32})/(?<name>[^)\s""'\]>]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectUpload();

    /// <summary>The uploads a text links to (<c>/uploads/…</c>, as GitLab writes an issue's, a merge request's and a comment's), each once.</summary>
    public static IReadOnlyList<GlUpload> Uploads(IEnumerable<string> texts)
        => texts.SelectMany(t => ProjectUpload().Matches(t).Select(m => new GlUpload(m.Groups["secret"].Value, m.Groups["name"].Value)))
            .DistinctBy(u => (u.Secret, u.FileName)).ToList();

    /// <summary>A text with its upload links pointing at <paramref name="folder"/> (relative), each by the name its file shows there.</summary>
    public static string LinkUploads(string text, string folder, IReadOnlyDictionary<GlUpload, string> names)
        => ProjectUpload().Replace(text, m =>
        {
            var upload = new GlUpload(m.Groups["secret"].Value, m.Groups["name"].Value);
            return names.TryGetValue(upload, out var name) ? $"{folder}/{Uri.EscapeDataString(name)}" : m.Value;
        });

    /// <summary>An issue's or merge request's own file: its title, what GitLab says of it, its description.</summary>
    public static byte[] Ticket(GlTicket t, bool mergeRequest, IReadOnlyDictionary<GlUpload, string> attachments)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(mergeRequest ? "!" : "#").Append(t.Iid).Append(' ').Append(OneLine(t.Title)).Append("\n\n");
        Line(sb, "State", State(t, mergeRequest));
        Line(sb, "Author", t.Author?.ToString());
        if (mergeRequest)
        {
            Line(sb, "Branches", t.SourceBranch is null ? null : $"{t.SourceBranch} into {t.TargetBranch}");
        }

        Line(sb, "Assignees", Join(t.Assignees));
        Line(sb, "Reviewers", Join(t.Reviewers));
        Line(sb, "Labels", t.Labels.Count == 0 ? null : string.Join(", ", t.Labels));
        Line(sb, "Milestone", t.Milestone);
        Line(sb, "Created", Date(t.Created));
        Line(sb, "Updated", Date(t.Updated));
        Line(sb, "Web", t.WebUrl);
        sb.Append("\n---\n\n");
        sb.Append(LinkUploads(t.Description.ReplaceLineEndings("\n").Trim(), "attachments", attachments));
        sb.Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>The comments, oldest first, each under its author and time; GitLab's own notes left out.</summary>
    public static byte[] Comments(string heading, IReadOnlyList<GlNote> notes, IReadOnlyDictionary<GlUpload, string> attachments)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(OneLine(heading)).Append("\n\n");
        var shown = notes.Where(n => !n.System).ToList();
        if (shown.Count == 0)
        {
            sb.Append("No comments.\n");
        }

        foreach (var note in shown)
        {
            sb.Append("## ").Append(note.Author?.ToString() ?? "someone").Append(", ").Append(Date(note.Created) ?? "").Append("\n\n");
            sb.Append(LinkUploads(note.Body.ReplaceLineEndings("\n").Trim(), "attachments", attachments)).Append("\n\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString().TrimEnd('\n') + "\n");
    }

    /// <summary>A merge request's changes as one unified patch, as <c>git diff</c> writes it (GitLab gives each file's hunks).</summary>
    public static byte[] Patch(IReadOnlyList<GlDiff> diffs)
    {
        var sb = new StringBuilder();
        foreach (var d in diffs)
        {
            sb.Append("diff --git a/").Append(d.OldPath).Append(" b/").Append(d.NewPath).Append('\n');
            if (d.NewFile)
            {
                sb.Append("new file mode ").Append(d.BMode).Append('\n');
            }
            else if (d.Deleted)
            {
                sb.Append("deleted file mode ").Append(d.AMode).Append('\n');
            }
            else if (d.AMode != d.BMode && d.AMode.Length > 0 && d.BMode.Length > 0)
            {
                sb.Append("old mode ").Append(d.AMode).Append("\nnew mode ").Append(d.BMode).Append('\n');
            }

            if (d.Renamed)
            {
                sb.Append("rename from ").Append(d.OldPath).Append("\nrename to ").Append(d.NewPath).Append('\n');
            }

            if (d.TooLarge)
            {
                sb.Append("# GitLab does not show this file's changes (too large, or a binary file)\n");
                continue;
            }

            if (d.Diff.Length == 0)
            {
                continue;
            }

            sb.Append("--- ").Append(d.NewFile ? "/dev/null" : "a/" + d.OldPath).Append('\n');
            sb.Append("+++ ").Append(d.Deleted ? "/dev/null" : "b/" + d.NewPath).Append('\n');
            sb.Append(d.Diff.ReplaceLineEndings("\n"));
            if (!d.Diff.EndsWith('\n'))
            {
                sb.Append('\n');
            }
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string State(GlTicket t, bool mergeRequest) => t.State switch
    {
        "opened" => mergeRequest ? "open" : "open",
        "closed" => t.Closed is { } closed ? $"closed {Date(closed)}" : "closed",
        "merged" => t.Merged is { } merged ? $"merged {Date(merged)}" : "merged",
        var other => other,
    };

    internal static void Line(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.Append("**").Append(label).Append(":** ").Append(value.ReplaceLineEndings(" ").Trim()).Append("  \n");
        }
    }

    private static string? Join(IReadOnlyList<GlPerson> people) => people.Count == 0 ? null : string.Join(", ", people);

    internal static string OneLine(string text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static string? Date(DateTimeOffset? time) => time?.ToLocalTime().ToString("ddd d MMM yyyy HH:mm (UTCzzz)", CultureInfo.InvariantCulture);
}
