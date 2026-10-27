using AnyFS.Sdk;

namespace AnyFS.Sources.GitLab;

/// <summary>A project's issues or merge requests: a folder per one, named by its number and title (<c>#12 …</c>, <c>!7 …</c>).</summary>
internal sealed class TicketsFolder(GlProject project, bool mergeRequests, string name) : GlFolder(name, project.LastActivity, project.Created)
{
    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var tickets = await source.TicketsAsync(project, mergeRequests, ct).ConfigureAwait(false);
        var names = GitLabNames.Unique(tickets.Select(t => mergeRequests ? GitLabNames.MergeRequest(t.Iid, t.Title) : GitLabNames.Issue(t.Iid, t.Title)).ToList());
        return tickets.Select((t, i) => (GlNode)new TicketFolder(project, t, mergeRequests, names[i])).ToList();
    }
}

/// <summary>
/// One issue or merge request: <c>issue.md</c> or <c>merge_request.md</c> (what GitLab says of it, its description),
/// <c>comments.md</c>, a merge request's <c>changes.diff</c>, and <c>attachments</c>: the files its text and comments link
/// to, which the Markdown links point at.
/// </summary>
internal sealed class TicketFolder(GlProject project, GlTicket ticket, bool mergeRequest, string name) : GlFolder(name, ticket.Updated, ticket.Created)
{
    public const string Attachments = "attachments";

    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var notes = await source.NotesAsync(project, ticket, mergeRequest, ct).ConfigureAwait(false);
        var uploads = GitLabDocuments.Uploads(notes.Where(n => !n.System).Select(n => n.Body).Prepend(ticket.Description));
        var names = GitLabNames.Unique(uploads.Select(u => u.Name).ToList());
        var byUpload = uploads.Select((u, i) => (u, names[i])).ToDictionary(p => p.u, p => p.Item2);
        var heading = $"{(mergeRequest ? "!" : "#")}{ticket.Iid} {ticket.Title}";
        var children = new List<GlNode>
        {
            new TextFile(mergeRequest ? "merge_request.md" : "issue.md", GitLabDocuments.Ticket(ticket, mergeRequest, byUpload), ticket.Updated, ticket.Created),
            new TextFile("comments.md", GitLabDocuments.Comments($"Comments on {heading}", notes, byUpload), notes.Count == 0 ? ticket.Created : notes.Max(n => n.Created) ?? ticket.Updated, ticket.Created),
        };
        if (mergeRequest)
        {
            var diffs = await source.DiffsAsync(project, ticket, ct).ConfigureAwait(false);
            children.Add(new TextFile("changes.diff", GitLabDocuments.Patch(diffs), ticket.Updated, ticket.Created));
        }

        if (uploads.Count > 0)
        {
            children.Add(new AttachmentsFolder(project, uploads.Select(u => (u, byUpload[u])).ToList(), Attachments, ticket.Updated));
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
