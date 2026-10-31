using AnyFS.Sdk;

namespace AnyFS.Sources.GitLab;

/// <summary>
/// One thing in a GitLab source's tree (spec 7.8): a folder (a group, a project, a repository folder at a commit...) or a
/// file. A path is found from the root, a listing at a time: each folder lists its children through the source's caches,
/// and a child is found by its name in either case.
/// </summary>
internal abstract class GlNode(string name)
{
    public string Name { get; } = name;

    public abstract VfsEntry Entry { get; }
}

internal abstract class GlFolder(string name, DateTimeOffset? modified = null, DateTimeOffset? created = null) : GlNode(name)
{
    public DateTimeOffset? Modified { get; } = modified;

    public DateTimeOffset? Created { get; } = created;

    public override VfsEntry Entry => VfsEntry.Directory(Name, Modified, Created);

    /// <summary>
    /// Whether a walk of the whole tree ("Count files on start") goes into it: not a whole repository at another branch or
    /// tag (a folder a request each, and a project has many refs); those are listed when opened. An issue's notes and a
    /// pipeline's jobs are walked: a project whose activity has not moved is kept whole by its stamp
    /// (<see cref="GitLabSource.StampAsync"/>), so they cost their requests once.
    /// </summary>
    public virtual bool Walked => true;

    public abstract Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct);
}

internal abstract class GlFile(string name, long length, DateTimeOffset? modified, DateTimeOffset? created = null, string? etag = null) : GlNode(name)
{
    public long Length { get; } = length;

    public override VfsEntry Entry => VfsEntry.File(Name, Length, modified, created ?? modified, etag: etag);

    public abstract Task<IVfsFile> OpenAsync(GitLabSource source, CancellationToken ct);
}

/// <summary>A folder with nothing in it: a submodule (git keeps only the commit it points at), an empty repository's files.</summary>
internal sealed class EmptyFolder(string name, DateTimeOffset? modified = null) : GlFolder(name, modified)
{
    public override Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct) => Task.FromResult<IReadOnlyList<GlNode>>([]);
}

/// <summary>A file the source makes itself and holds in memory (an issue as Markdown, a job's log).</summary>
internal sealed class TextFile(string name, byte[] content, DateTimeOffset? modified, DateTimeOffset? created = null) : GlFile(name, content.Length, modified, created)
{
    public override Task<IVfsFile> OpenAsync(GitLabSource source, CancellationToken ct) => Task.FromResult<IVfsFile>(new MemoryFile(content, "a file made from GitLab's data"));
}

/// <summary>
/// The whole server (spec 7.8): the groups the account is a member of and the namespaces of the projects it is a member of,
/// never every public project of a big instance. A member group lists as GitLab lists it (all its subgroups and projects);
/// a namespace the account reaches only through a project it was invited to shows just the way to that project.
/// </summary>
internal sealed class ServerRoot() : GlFolder("")
{
    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
        => MembershipFolder.Children(await source.MembershipsAsync(ct).ConfigureAwait(false), "");
}

/// <summary>A namespace on the way to projects the account is a member of, though not of the namespace itself.</summary>
internal sealed class MembershipFolder(string fullPath, string name) : GlFolder(name)
{
    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
        => Children(await source.MembershipsAsync(ct).ConfigureAwait(false), fullPath);

    /// <summary>What is directly below <paramref name="prefix"/> ("" for the root): member groups, the namespaces on the way to deeper ones, the projects in it.</summary>
    public static IReadOnlyList<GlNode> Children(Memberships memberships, string prefix)
    {
        var below = prefix.Length == 0 ? "" : prefix + "/";
        var segments = new SortedDictionary<string, (GlGroup? Group, GlProject? Project)>(StringComparer.Ordinal);
        foreach (var group in memberships.TopGroups)
        {
            if (group.FullPath.StartsWith(below, StringComparison.Ordinal) && group.FullPath.Length > below.Length)
            {
                var rest = group.FullPath[below.Length..];
                var segment = rest.Split('/')[0];
                if (!rest.Contains('/'))
                {
                    segments[segment] = (group, null);
                }
                else if (!segments.ContainsKey(segment))
                {
                    segments[segment] = (null, null);
                }
            }
        }

        foreach (var project in memberships.Projects)
        {
            if (memberships.InMemberGroup(project.FullPath) || !project.FullPath.StartsWith(below, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = project.FullPath[below.Length..];
            var segment = rest.Split('/')[0];
            if (!rest.Contains('/'))
            {
                segments[segment] = (null, project);
            }
            else if (!segments.ContainsKey(segment))
            {
                segments[segment] = (null, null);
            }
        }

        var raw = segments.Keys.ToList();
        var names = GitLabNames.Unique(raw);
        return raw.Select((segment, i) =>
        {
            var (group, project) = segments[segment];
            return group is not null ? new GroupFolder(group, names[i])
                : project is not null ? new ProjectFolder(project, names[i])
                : (GlNode)new MembershipFolder(below + segment, names[i]);
        }).ToList();
    }
}

/// <summary>What the account is a member of on the whole server: the groups (the top-most of each line) and the projects.</summary>
internal sealed class Memberships(IReadOnlyList<GlGroup> groups, IReadOnlyList<GlProject> projects)
{
    /// <summary>The member groups that no other member group holds: each lists all that is under it itself.</summary>
    public IReadOnlyList<GlGroup> TopGroups { get; } = groups.Where(g => !groups.Any(o => o != g && g.FullPath.StartsWith(o.FullPath + "/", StringComparison.Ordinal))).ToList();

    public IReadOnlyList<GlProject> Projects { get; } = projects;

    /// <summary>Whether a project's path is under a member group, which lists it itself.</summary>
    public bool InMemberGroup(string projectPath) => TopGroups.Any(g => projectPath.StartsWith(g.FullPath + "/", StringComparison.Ordinal));
}

/// <summary>A group: its subgroups and projects, as GitLab lists them to the account.</summary>
internal sealed class GroupFolder(GlGroup group, string name) : GlFolder(name, null, group.Created)
{
    public GlGroup Group { get; } = group;

    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var (subgroups, projects) = await source.GroupContentsAsync(Group, ct).ConfigureAwait(false);

        // a group's own wiki (GitLab Premium) first, where it has one; a subgroup or project named wiki is numbered
        var wiki = source.Settings.Wiki && await source.GroupHasWikiAsync(Group, ct).ConfigureAwait(false);
        var raw = (wiki ? ["wiki"] : Array.Empty<string>()).Concat(subgroups.Select(g => g.Path)).Concat(projects.Select(p => p.Path)).ToList();
        var names = GitLabNames.Unique(raw);
        var children = new List<GlNode>(raw.Count);
        var at = 0;
        if (wiki)
        {
            children.Add(new WikiFolder(WikiOwner.Of(Group), "", names[at++]));
        }

        foreach (var subgroup in subgroups)
        {
            children.Add(new GroupFolder(subgroup, names[at++]));
        }

        foreach (var project in projects)
        {
            children.Add(new ProjectFolder(project, names[at++]));
        }

        return children;
    }
}

/// <summary>
/// A project: <c>files</c> (the default branch), <c>branches</c> and <c>tags</c> (each ref as the repository stands
/// there), and the folders of the project's other features where they are on and the settings show them.
/// </summary>
internal sealed class ProjectFolder(GlProject project, string name) : GlFolder(name, project.LastActivity, project.Created)
{
    public GlProject Project { get; } = project;

    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var project = await source.ProjectAsync(Project.Id, ct).ConfigureAwait(false);
        var settings = source.Settings;
        var sections = new List<GlNode>();
        if (project.Repository)
        {
            sections.Add(new FilesFolder(project));
            if (settings.BranchesAndTags)
            {
                sections.Add(new RefsFolder(project, RefKind.Branch, "", "branches", project.LastActivity));
                sections.Add(new RefsFolder(project, RefKind.Tag, "", "tags", project.LastActivity));
            }
        }

        // GitLab turns every project's wiki on; only one with pages shows
        if (project.Wiki && settings.Wiki && await source.HasWikiPagesAsync(WikiOwner.Of(project), ct).ConfigureAwait(false))
        {
            sections.Add(new WikiFolder(WikiOwner.Of(project), "", "wiki"));
        }

        if (settings.IssuesAndMergeRequests)
        {
            if (project.Issues)
            {
                sections.Add(new TicketsFolder(project, mergeRequests: false, "issues"));
            }

            if (project.MergeRequests && project.Repository)
            {
                sections.Add(new TicketsFolder(project, mergeRequests: true, "merge requests"));
            }
        }

        // most projects have no pipelines or releases: those folders show only where there are some
        if (settings.Pipelines && project.Pipelines && project.Repository
            && await source.HasAnyAsync(async () => (await source.PipelinesAsync(project, ct).ConfigureAwait(false)).Count).ConfigureAwait(false))
        {
            sections.Add(new PipelinesFolder(project, "pipelines"));
        }

        if (settings.Releases && project.Releases && project.Repository
            && await source.HasAnyAsync(async () => (await source.ReleasesAsync(project, ct).ConfigureAwait(false)).Count).ConfigureAwait(false))
        {
            sections.Add(new ReleasesFolder(project, "releases"));
        }

        return sections;
    }
}

/// <summary>The repository as it stands on the default branch: its head is asked again once the refresh interval has passed.</summary>
internal sealed class FilesFolder(GlProject project) : GlFolder("files", project.LastActivity, project.Created)
{
    public GlProject Project { get; } = project;

    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        if (await source.HeadAsync(Project, ct).ConfigureAwait(false) is not { } head)
        {
            return [];
        }

        return await At(head).ListAsync(source, ct).ConfigureAwait(false);
    }

    /// <summary>The repository at the head given: the folder's contents then.</summary>
    public TreeFolder At(GlRef head) => new(Project, head.Commit, "", Name, head.Committed);
}

internal enum RefKind
{
    Branch,
    Tag,
}

/// <summary>
/// The branches or the tags, by name: a <c>/</c> in a name makes a folder (<c>feature/x</c> is <c>feature\x</c>), and each
/// ref's folder is the repository at its commit.
/// </summary>
internal sealed class RefsFolder(GlProject project, RefKind kind, string prefix, string name, DateTimeOffset? modified) : GlFolder(name, modified)
{
    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var refs = await source.RefsAsync(project, kind, ct).ConfigureAwait(false);
        var level = new SortedDictionary<string, GlRef?>(StringComparer.Ordinal);
        foreach (var r in refs)
        {
            if (!r.Name.StartsWith(prefix, StringComparison.Ordinal) || r.Name.Length == prefix.Length)
            {
                continue;
            }

            var rest = r.Name[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                level[rest] = r;
            }
            else
            {
                level.TryAdd(rest[..slash], null);
            }
        }

        var raw = level.Keys.ToList();
        var names = GitLabNames.Unique(raw);
        // a ref's folder is a whole repository: not walked, but listed when opened
        return raw.Select((segment, i) => level[segment] is { } r
            ? (GlNode)new TreeFolder(project, r.Commit, "", names[i], r.Committed, walked: false)
            : new RefsFolder(project, kind, prefix + segment + "/", names[i], Modified)).ToList();
    }
}

/// <summary>
/// A folder of the repository at a commit: never changes, so kept for good (<see cref="TreeStore"/>). Its folders first,
/// then the rest, each in the order of their names, however GitLab sent them (a folder listed alone, or the whole tree at
/// once), so twins in case are numbered the same either way.
/// </summary>
internal sealed class TreeFolder(GlProject project, string commit, string path, string name, DateTimeOffset? time, bool walked = true) : GlFolder(name, time, time)
{
    public override bool Walked => walked;

    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var items = (await source.TreeAsync(project, commit, path, ct).ConfigureAwait(false))
            .OrderBy(i => i.K == 't' ? 0 : 1).ThenBy(i => i.N, StringComparer.Ordinal).ToList();
        var names = GitLabNames.Unique(items.Select(i => i.N).ToList());
        var children = new List<GlNode>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var full = path.Length == 0 ? item.N : path + "/" + item.N;
            children.Add(item.K switch
            {
                't' => new TreeFolder(project, commit, full, names[i], Modified),
                'c' => new EmptyFolder(names[i], Modified),
                _ => new BlobFile(project, commit, full, item, names[i], Modified),
            });
        }

        return children;
    }
}

/// <summary>A file of the repository: its bytes as git keeps them, or for a file in Git LFS the object's.</summary>
internal sealed class BlobFile(GlProject project, string commit, string path, TreeItem item, string name, DateTimeOffset? time)
    : GlFile(name, item.L, time, time, item.S)
{
    public override Task<IVfsFile> OpenAsync(GitLabSource source, CancellationToken ct) => source.OpenBlobAsync(project, commit, path, item, ct);
}
