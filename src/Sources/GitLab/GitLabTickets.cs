using System.Globalization;
using AnyFS.Sdk;

namespace AnyFS.Sources.GitLab;

/// <summary>A project's issues or merge requests: a folder per one, named by its number and title (<c>#12 …</c>, <c>!7 …</c>).</summary>
internal sealed class TicketsFolder(GlProject project, bool mergeRequests, string name) : GlFolder(name, project.LastActivity, project.Created)
{
    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var tickets = await source.TicketsAsync(project, mergeRequests, ct).ConfigureAwait(false);
        var names = GitLabNames.Unique(tickets.Select(NameOf).ToList());
        return tickets.Select((t, i) => (GlNode)new TicketFolder(project, t, mergeRequests, names[i])).ToList();
    }

    /// <summary>
    /// One issue's or merge request's folder by the number its name starts with, nothing else named or made. Finding one
    /// listed them all: each was named again and a folder made for each, so a search that read every issue's file of a
    /// project of N issues named N issues N times (the review of 21-Nov-2026, P5: 4 GiB allocated to read 2,000 issues'
    /// files, 15 for 4,000). Its name is the one a listing gives it: the number a name starts with keeps the names apart,
    /// so none is ever numbered as another's twin in case.
    /// </summary>
    public override async Task<GlNode?> FindAsync(GitLabSource source, string path, string name, CancellationToken ct)
    {
        if (NumberIn(name) is not { } number || await source.TicketAsync(project, mergeRequests, number, ct).ConfigureAwait(false) is not { } ticket)
        {
            return null;
        }

        // by the name it has, or by its number alone where its title has changed since the name was listed
        var own = GitLabNames.Unique([NameOf(ticket)])[0];
        return own.Equals(name, StringComparison.OrdinalIgnoreCase) || TicketFolder.SameTicket(own, name) ? new TicketFolder(project, ticket, mergeRequests, own) : null;
    }

    private string NameOf(GlTicket ticket) => mergeRequests ? GitLabNames.MergeRequest(ticket.Iid, ticket.Title) : GitLabNames.Issue(ticket.Iid, ticket.Title);

    /// <summary>The number a folder's name starts with (<c>#12 …</c>, <c>!7 …</c>), or null where it starts with none.</summary>
    private long? NumberIn(string name)
    {
        if (name.Length < 2 || name[0] != (mergeRequests ? '!' : '#'))
        {
            return null;
        }

        var end = name.IndexOf(' ', StringComparison.Ordinal);
        return long.TryParse(end < 0 ? name.AsSpan(1) : name.AsSpan(1, end - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }
}

/// <summary>
/// One issue or merge request: <c>issue.md</c> or <c>merge_request.md</c> (what GitLab says of it, its description, then
/// its comments, as Jira's <c>issue.md</c> ends with its own), a merge request's <c>changes.diff</c>, and
/// <c>attachments</c>: the files its text and comments link to, which the Markdown links point at.
/// </summary>
internal sealed class TicketFolder(GlProject project, GlTicket ticket, bool mergeRequest, string name) : GlFolder(name, ticket.Updated, ticket.Created)
{
    public const string Attachments = "attachments";

    public GlTicket Ticket { get; } = ticket;

    /// <summary>Whether two folder names are one ticket's: the number says, as a listing kept until the next walk still gives the title it had.</summary>
    public static bool SameTicket(string name, string other)
        => name.IndexOf(' ', StringComparison.Ordinal) is var end and > 1 && other.StartsWith(name[..(end + 1)], StringComparison.Ordinal);

    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        // the folder holds the ticket without its description, as its list does: with it here, for as long as this takes
        var whole = await source.WholeTicketAsync(project, mergeRequest, Ticket, ct).ConfigureAwait(false);
        var notes = await source.NotesAsync(project, Ticket, mergeRequest, ct).ConfigureAwait(false);
        var uploads = GitLabDocuments.Uploads(notes.Where(n => !n.System).Select(n => n.Body).Prepend(whole.Description));
        var names = GitLabNames.Unique(uploads.Select(u => u.Name).ToList());
        var byUpload = uploads.Select((u, i) => (u, names[i])).ToDictionary(p => p.u, p => p.Item2);
        var children = new List<GlNode>
        {
            new TextFile(mergeRequest ? "merge_request.md" : "issue.md", GitLabDocuments.Ticket(whole, mergeRequest, notes, byUpload), Ticket.Updated, Ticket.Created),
        };
        if (mergeRequest)
        {
            var diffs = await source.DiffsAsync(project, Ticket, ct).ConfigureAwait(false);
            children.Add(new TextFile("changes.diff", GitLabDocuments.Patch(diffs), Ticket.Updated, Ticket.Created));
        }

        if (uploads.Count > 0)
        {
            children.Add(new AttachmentsFolder(project, uploads.Select(u => (u, byUpload[u])).ToList(), Attachments, Ticket.Updated));
        }

        return children;
    }
}

/// <summary>The files an issue's, merge request's or release's text links to, by the names they were uploaded with.</summary>
internal sealed class AttachmentsFolder(GlProject project, IReadOnlyList<(GlUpload Upload, string Name)> uploads, string name, DateTimeOffset? modified) : GlFolder(name, modified)
{
    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var files = new List<GlNode>(uploads.Count);
        foreach (var (upload, shown) in uploads)
        {
            if ((await source.UploadSizeAsync(project, upload, ct).ConfigureAwait(false)).Value is { } length)
            {
                files.Add(new UploadFile(project, upload, shown, length, Modified));
            }
        }

        return files;
    }
}

/// <summary>A file attached to an issue, a merge request or a release.</summary>
internal sealed class UploadFile(GlProject project, GlUpload upload, string name, long length, DateTimeOffset? modified) : GlFile(name, length, modified, modified, upload.Secret)
{
    public override Task<IVfsFile> OpenAsync(GitLabSource source, CancellationToken ct) => source.OpenUploadAsync(project, upload, Length, ct);
}
