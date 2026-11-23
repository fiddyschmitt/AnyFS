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
    /// Whether what it last listed is good for finding the folders in it (<see cref="GitLabSource.ResolveAsync"/>): the way down to a
    /// project, whose folders ask for what they hold themselves. Not an issue's, say: its folders are made from what was listed.
    /// </summary>
    public virtual bool FoundFromLastListing => false;

    public abstract Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct);

    /// <summary>
    /// The child named <paramref name="name"/> (in either case), or null: from the folder's listing, made at
    /// <paramref name="path"/> for the source to remember, unless the folder can find the one without making them all
    /// (a project's issues, by the number).
    /// </summary>
    public virtual Task<GlNode?> FindAsync(GitLabSource source, string path, string name, CancellationToken ct)
        => source.FoundByListingAsync(this, path, name, ct);
}

internal abstract class GlFile(string name, long length, DateTimeOffset? modified, DateTimeOffset? created = null, string? etag = null) : GlNode(name)
{
    public long Length { get; } = length;

    public override VfsEntry Entry => VfsEntry.File(Name, Length, modified, created ?? modified, etag: etag);

    public abstract Task<IVfsFile> OpenAsync(GitLabSource source, CancellationToken ct);
}

/// <summary>A folder with nothing in it: a submodule (git keeps only the commit it points at), its entry saying so (<see cref="GitLabKeys"/>).</summary>
internal sealed class EmptyFolder(string name, DateTimeOffset? modified = null, string? key = null) : GlFolder(name, modified)
{
    public override VfsEntry Entry => key is null ? base.Entry : base.Entry with { Key = key };

    public override Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct) => Task.FromResult<IReadOnlyList<GlNode>>([]);
}

/// <summary>A file the source makes itself and holds in memory (an issue as Markdown, a pipeline's summary).</summary>
internal sealed class TextFile(string name, byte[] content, DateTimeOffset? modified, DateTimeOffset? created = null) : GlFile(name, content.Length, modified, created)
{
    public override Task<IVfsFile> OpenAsync(GitLabSource source, CancellationToken ct) => Task.FromResult<IVfsFile>(new MemoryFile(content, "a file made from GitLab's data"));
}

/// <summary>
/// The whole server (spec 7.8): every group and project the account can see, or only those it is a member of
/// (<see cref="GitLabSettings.ShowsEveryProject"/>). A group lists as GitLab lists it (the subgroups and projects the
/// account can see); a namespace that is no group listed, someone's own or one the account reaches only through a project
/// it was invited to, shows just the way to the projects in it.
/// </summary>
internal sealed class ServerRoot() : GlFolder("")
{
    public override bool FoundFromLastListing => true;

    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
        => NamespaceFolder.Children(await source.ServerListingAsync(ct).ConfigureAwait(false), "");
}

/// <summary>A namespace on the way to projects the server's listing names, itself no group the listing names.</summary>
internal sealed class NamespaceFolder(string fullPath, string name) : GlFolder(name)
{
    public override bool FoundFromLastListing => true;

    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
        => Children(await source.ServerListingAsync(ct).ConfigureAwait(false), fullPath);

    /// <summary>What is directly below <paramref name="prefix"/> ("" for the root): the groups, the namespaces on the way to deeper ones, the projects in it.</summary>
    public static IReadOnlyList<GlNode> Children(ServerListing listing, string prefix)
    {
        var below = prefix.Length == 0 ? "" : prefix + "/";
        var segments = new SortedDictionary<string, (GlGroup? Group, GlProject? Project)>(StringComparer.Ordinal);
        foreach (var group in listing.TopGroups)
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

        foreach (var project in listing.Projects)
        {
            if (listing.InTopGroup(project.FullPath) || !project.FullPath.StartsWith(below, StringComparison.Ordinal))
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
                : (GlNode)new NamespaceFolder(below + segment, names[i]);
        }).ToList();
    }
}

/// <summary>What the whole server shows: the groups (the top-most of each line) and the projects.</summary>
internal sealed class ServerListing
{
    private readonly HashSet<string> _topPaths;

    public ServerListing(IReadOnlyList<GlGroup> groups, IReadOnlyList<GlProject> projects)
    {
        var paths = groups.Select(g => g.FullPath).ToHashSet(StringComparer.Ordinal);
        TopGroups = groups.Where(g => !Above(g.FullPath).Any(paths.Contains)).ToList();
        _topPaths = TopGroups.Select(g => g.FullPath).ToHashSet(StringComparer.Ordinal);
        Projects = projects;
    }

    /// <summary>The groups that no other group listed holds: each lists all that is under it itself.</summary>
    public IReadOnlyList<GlGroup> TopGroups { get; }

    public IReadOnlyList<GlProject> Projects { get; }

    /// <summary>Whether a project's path is under a top-most group, which lists it itself.</summary>
    public bool InTopGroup(string projectPath) => Above(projectPath).Any(_topPaths.Contains);

    /// <summary>The namespaces a path is under, the nearest first.</summary>
    private static IEnumerable<string> Above(string path)
    {
        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
        {
            yield return path[..slash];
        }
    }
}

/// <summary>A group: its subgroups and projects, as GitLab lists them to the account.</summary>
internal sealed class GroupFolder(GlGroup group, string name) : GlFolder(name, null, group.Created)
{
    public GlGroup Group { get; } = group;

    public override bool FoundFromLastListing => true;

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

    public override bool FoundFromLastListing => true;

    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var project = await source.ProjectAsync(Project, ct).ConfigureAwait(false);
        var settings = source.Settings;

        // what the sections ask of the server, asked together: a walk comes to thousands of projects, and three answers waited
        // for in turn cost three times the wait of one (5-Nov-2026). GitLab turns every project's wiki on; only one with pages
        // shows. Most projects have no pipelines or releases: those folders show only where there are some.
        var wiki = project.Wiki && settings.Wiki ? source.HasWikiPagesAsync(WikiOwner.Of(project), ct) : Task.FromResult(false);
        var pipelines = settings.Pipelines && project.Pipelines && project.Repository
            ? source.HasAnyAsync(async () => (await source.PipelinesAsync(project, ct).ConfigureAwait(false)).Count)
            : Task.FromResult(false);
        var releases = settings.Releases && project.Releases && project.Repository
            ? source.HasAnyAsync(async () => (await source.ReleasesAsync(project, ct).ConfigureAwait(false)).Count)
            : Task.FromResult(false);
        await Task.WhenAll(wiki, pipelines, releases).ConfigureAwait(false);

        var sections = new List<GlNode>();
        if (project.RepositoryReadable)
        {
            sections.Add(new FilesFolder(project));
            if (settings.BranchesAndTags)
            {
                sections.Add(new RefsFolder(project, RefKind.Branch, "", "branches", project.LastActivity));
                sections.Add(new RefsFolder(project, RefKind.Tag, "", "tags", project.LastActivity));
            }
        }

        if (wiki.Result)
        {
            sections.Add(new WikiFolder(WikiOwner.Of(project), "", "wiki"));
        }

        if (settings.IssuesAndMergeRequests)
        {
            if (project.Issues)
            {
                sections.Add(new TicketsFolder(project, mergeRequests: false, "issues"));
            }

            if (project.MergeRequests && project.RepositoryReadable)
            {
                sections.Add(new TicketsFolder(project, mergeRequests: true, "merge requests"));
            }
        }

        if (pipelines.Result)
        {
            sections.Add(new PipelinesFolder(project, "pipelines"));
        }

        if (releases.Result)
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
        // a ref's folder is the whole repository at its commit: walked whole as files is (ListTree), nothing asked where the
        // commit is kept already (a branch at the default branch's head costs nothing); until 6-Nov-2026 left out of the walk
        // and listed folder by folder when opened, so a search's first pass over the tree asked the server for every one
        return raw.Select((segment, i) => level[segment] is { } r
            ? (GlNode)new TreeFolder(project, r.Commit, "", names[i], r.Committed)
            : new RefsFolder(project, kind, prefix + segment + "/", names[i], Modified)).ToList();
    }
}

/// <summary>
/// A folder of the repository at a commit: never changes, so its listing is kept as long as the commit is where the folder
/// stands. Its folders first, then the rest, each in the order of their names, however GitLab sent them (a folder listed
/// alone, or the whole tree at once), so twins in case are numbered the same either way. Each entry carries what finding
/// it again takes (<see cref="GitLabKeys"/>), so a kept listing is enough to go on from (<see cref="FromKept"/>).
/// </summary>
internal sealed class TreeFolder(GlProject project, string commit, string path, string name, DateTimeOffset? time) : GlFolder(name, time, time)
{
    public GlProject Project => project;

    public string Commit => commit;

    /// <summary>The folder's path in the repository; empty for its root (<c>files</c>, or a branch's or tag's folder).</summary>
    public string RepoPath => path;

    /// <summary>A folder in the repository says git's name for it where the name shown is another; its root, a ref's folder, nothing.</summary>
    public override VfsEntry Entry => path.Length == 0 ? base.Entry : base.Entry with { Key = GitLabKeys.Of(path[(path.LastIndexOf('/') + 1)..], Name) };

    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
        => Children(project, commit, path, await source.TreeAsync(project, commit, path, ct).ConfigureAwait(false), Modified);

    /// <summary>The folder at <paramref name="path"/>'s children, from its entries as GitLab lists them, each with the time given.</summary>
    public static List<GlNode> Children(GlProject project, string commit, string path, IReadOnlyList<TreeItem> listed, DateTimeOffset? time)
    {
        var items = listed.OrderBy(i => i.K == 't' ? 0 : 1).ThenBy(i => i.N, StringComparer.Ordinal).ToList();
        var names = GitLabNames.Unique(items.Select(i => i.N).ToList());
        var children = new List<GlNode>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var full = path.Length == 0 ? item.N : path + "/" + item.N;
            children.Add(item.K switch
            {
                't' => new TreeFolder(project, commit, full, names[i], time),
                'c' => new EmptyFolder(names[i], time, GitLabKeys.Of(item.N, names[i], submodule: true)),
                _ => new BlobFile(project, commit, full, item, names[i], time),
            });
        }

        return children;
    }

    /// <summary>
    /// What an entry of a kept listing of the repository folder at <paramref name="path"/> names, made from the entry alone:
    /// a folder, a submodule, or a file with its blob id (the entry's ETag), size and LFS mark. Null for a file entry with no
    /// blob id, which only GitLab's listing can say.
    /// </summary>
    public static GlNode? FromKept(GlProject project, string commit, string path, VfsEntry entry)
    {
        var (gitName, lfs, submodule) = GitLabKeys.Read(entry);
        var full = path.Length == 0 ? gitName : path + "/" + gitName;
        if (entry.IsDirectory)
        {
            return submodule ? new EmptyFolder(entry.Name, entry.Modified, entry.Key) : new TreeFolder(project, commit, full, entry.Name, entry.Modified);
        }

        return entry.ETag is { Length: > 0 } blob
            ? new BlobFile(project, commit, full, new TreeItem(gitName, 'b', blob, entry.Length, lfs), entry.Name, entry.Modified)
            : null;
    }
}

/// <summary>A file of the repository: its bytes as git keeps them, or for a file in Git LFS the object's.</summary>
internal sealed class BlobFile(GlProject project, string commit, string path, TreeItem item, string name, DateTimeOffset? time)
    : GlFile(name, item.L, time, time, item.S)
{
    /// <summary>With git's name where it shows another, and its LFS mark (<see cref="GitLabKeys"/>).</summary>
    public override VfsEntry Entry => base.Entry with { Key = GitLabKeys.Of(item.N, Name, lfs: item.F) };

    public override Task<IVfsFile> OpenAsync(GitLabSource source, CancellationToken ct) => source.OpenBlobAsync(project, commit, path, item, ct);
}
