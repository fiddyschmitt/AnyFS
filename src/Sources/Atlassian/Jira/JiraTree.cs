using AnyFS.Sdk;

namespace AnyFS.Sources.Atlassian.Jira;

/// <summary>
/// One thing in a Jira source's tree (spec 7.10): a folder (the server, a project, a filter's issues, an issue, its
/// attachments) or a file. A path is found from the root a folder at a time; a folder finds a child by its name, an issue
/// folder by the key its name starts with (no project listed for it).
/// </summary>
internal abstract class JiNode(string name)
{
    public string Name { get; } = name;

    public abstract VfsEntry Entry { get; }
}

internal abstract class JiFolder(string name, DateTimeOffset? modified = null, DateTimeOffset? created = null) : JiNode(name)
{
    public DateTimeOffset? Modified { get; } = modified;

    public DateTimeOffset? Created { get; } = created;

    public override VfsEntry Entry => VfsEntry.Directory(Name, Modified, Created);

    public abstract Task<IReadOnlyList<JiNode>> ListAsync(JiraSource source, CancellationToken ct);

    /// <summary>The child named <paramref name="name"/> (in either case), or null: by default from the listing.</summary>
    public virtual async Task<JiNode?> FindAsync(JiraSource source, string name, CancellationToken ct)
        => (await ListAsync(source, ct).ConfigureAwait(false)).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

internal abstract class JiFile(string name, long length, DateTimeOffset? modified, DateTimeOffset? created = null) : JiNode(name)
{
    public long Length { get; } = length;

    public override VfsEntry Entry => VfsEntry.File(Name, Length, modified, created ?? modified);

    public abstract Task<IVfsFile> OpenAsync(JiraSource source, CancellationToken ct);
}

/// <summary>A file the source makes itself and holds in memory (an issue as Markdown).</summary>
internal sealed class JiText(string name, byte[] content, DateTimeOffset? modified, DateTimeOffset? created = null) : JiFile(name, content.Length, modified, created)
{
    public override Task<IVfsFile> OpenAsync(JiraSource source, CancellationToken ct) => Task.FromResult<IVfsFile>(new MemoryFile(content, "a file made from Jira's data"));
}

/// <summary>The whole server: a folder per project the account can browse, <c>Name (KEY)</c>.</summary>
internal sealed class JiraServerRoot() : JiFolder("")
{
    public override async Task<IReadOnlyList<JiNode>> ListAsync(JiraSource source, CancellationToken ct)
    {
        var projects = await source.ProjectsAsync(ct).ConfigureAwait(false);
        var names = AtlassianNames.Unique(projects.Select(p => ProjectFolderName(p)).ToList());
        return projects.Select((p, i) => (JiNode)new JiraProjectFolder(p, names[i])).ToList();
    }

    /// <summary>A project's folder: <c>Alpha project (ABC)</c>.</summary>
    public static string ProjectFolderName(JiraProject p) => $"{AtlassianNames.Title(p.Name)} ({p.Key})";
}

/// <summary>
/// A folder of issues: a project's (its <c>project.md</c> first), or a filter's, a board's or a query's (the source's
/// root then). Each issue a folder <c>KEY-123 Summary</c>.
/// </summary>
internal abstract class JiraIssuesFolder(string name, DateTimeOffset? modified = null) : JiFolder(name, modified)
{
    /// <summary>The JQL condition that finds the folder's issues (no ordering), the "open only" setting's included.</summary>
    public abstract string Condition(JiraSource source);

    /// <summary>The files a folder of issues holds besides them (a project's project.md).</summary>
    protected virtual Task<IReadOnlyList<JiNode>> ExtrasAsync(JiraSource source, CancellationToken ct) => Task.FromResult<IReadOnlyList<JiNode>>([]);

    public override async Task<IReadOnlyList<JiNode>> ListAsync(JiraSource source, CancellationToken ct)
    {
        var extras = await ExtrasAsync(source, ct).ConfigureAwait(false);
        var issues = await source.IssueHeadsAsync(Condition(source), ct).ConfigureAwait(false);
        var children = new List<JiNode>(extras.Count + issues.Count);
        children.AddRange(extras);
        children.AddRange(issues.Select(h => (JiNode)new JiraIssueStub(h)));
        return children;
    }

    /// <summary>
    /// An issue by the key its folder's name starts with, read alone (or with the issues around it), not by listing them all. The
    /// key alone says which: a listing kept until the next walk still gives the name the issue had before its summary changed,
    /// and a client opens what it was shown. One moved away answers with its new key, and is not here.
    /// </summary>
    public override async Task<JiNode?> FindAsync(JiraSource source, string name, CancellationToken ct)
    {
        if (JiraScope.KeyOfFolder(name) is not { } key)
        {
            return (await ExtrasAsync(source, ct).ConfigureAwait(false)).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        var issue = await source.IssueAsync(key, ct).ConfigureAwait(false);
        return issue is not null && issue.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ? new JiraIssueFolder(issue) : null;
    }
}

/// <summary>A project: its <c>project.md</c> and its issues.</summary>
internal sealed class JiraProjectFolder(JiraProject project, string name) : JiraIssuesFolder(name)
{
    public JiraProject Project { get; } = project;

    public override string Condition(JiraSource source) => source.Narrowed($"project = {JiraScope.Quote(Project.Key)}");

    protected override async Task<IReadOnlyList<JiNode>> ExtrasAsync(JiraSource source, CancellationToken ct)
        => [await source.ProjectFileAsync(Project.Key, ct).ConfigureAwait(false)];
}

/// <summary>A filter's, a board's or a query's issues, as the source's root.</summary>
internal sealed class JiraQueryRoot(string jql) : JiraIssuesFolder("")
{
    public string Jql { get; } = jql;

    public override string Condition(JiraSource source) => source.Narrowed(JiraScope.Condition(Jql));
}

/// <summary>An issue as a listing of its folder names it: its key, summary and times; read whole only when opened.</summary>
internal sealed class JiraIssueStub(JiraIssueHead head) : JiFolder(JiraDocuments.FolderName(head.Key, head.Summary), head.Updated, head.Created)
{
    public JiraIssueHead Head { get; } = head;

    public override async Task<IReadOnlyList<JiNode>> ListAsync(JiraSource source, CancellationToken ct)
    {
        var issue = await source.IssueAsync(Head.Key, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(Head.Key);
        return await new JiraIssueFolder(issue).ListAsync(source, ct).ConfigureAwait(false);
    }
}

/// <summary>An issue: its <c>issue.md</c> and, where it has attachments and they are shown, its <c>attachments</c> folder.</summary>
internal sealed class JiraIssueFolder(JiraIssue issue) : JiFolder(JiraDocuments.FolderName(issue.Key, issue.Summary), issue.Updated, issue.Created)
{
    public JiraIssue Issue { get; } = issue;

    public override async Task<IReadOnlyList<JiNode>> ListAsync(JiraSource source, CancellationToken ct)
        => await source.IssueChildrenAsync(Issue, ct).ConfigureAwait(false);
}

/// <summary>An issue's attached files, each under the name it was attached with (a name twice numbered by age).</summary>
internal sealed class JiraAttachmentsFolder(JiraIssue issue) : JiFolder(JiraDocuments.AttachmentsFolder, issue.Updated, issue.Created)
{
    public JiraIssue Issue { get; } = issue;

    public override Task<IReadOnlyList<JiNode>> ListAsync(JiraSource source, CancellationToken ct)
    {
        var names = JiraDocuments.AttachmentNames(Issue);
        return Task.FromResult<IReadOnlyList<JiNode>>(Issue.Attachments.Select(a => (JiNode)new JiraAttachmentFile(a, names[a.Id])).ToList());
    }
}

/// <summary>An attached file, read from Jira as it is opened (spooled), its size from the attachment's own record.</summary>
internal sealed class JiraAttachmentFile(JiraAttachment attachment, string name) : JiFile(name, attachment.Size, attachment.Created)
{
    public JiraAttachment Attachment { get; } = attachment;

    public override Task<IVfsFile> OpenAsync(JiraSource source, CancellationToken ct) => source.OpenAttachmentAsync(Attachment, ct);
}
