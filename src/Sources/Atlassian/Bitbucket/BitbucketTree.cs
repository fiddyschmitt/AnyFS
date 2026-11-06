using AnyFS.Sdk;

namespace AnyFS.Sources.Atlassian.Bitbucket;

/// <summary>
/// One thing in a Bitbucket source's tree (spec 7.11): a folder (the server, a project, a repository, its files at a
/// commit, its branches and tags, its pull requests) or a file. A path is found from the root a folder at a time; a folder
/// finds a child by its name, a pull request folder by the number its name starts with.
/// </summary>
internal abstract class BbNode(string name)
{
    public string Name { get; } = name;

    public abstract VfsEntry Entry { get; }
}

internal abstract class BbFolder(string name, DateTimeOffset? modified = null, DateTimeOffset? created = null) : BbNode(name)
{
    public DateTimeOffset? Modified { get; } = modified;

    public DateTimeOffset? Created { get; } = created;

    public override VfsEntry Entry => VfsEntry.Directory(Name, Modified, Created);

    public abstract Task<IReadOnlyList<BbNode>> ListAsync(BitbucketSource source, CancellationToken ct);

    /// <summary>The child named <paramref name="name"/> (in either case), or null: by default from the listing.</summary>
    public virtual async Task<BbNode?> FindAsync(BitbucketSource source, string name, CancellationToken ct)
        => (await ListAsync(source, ct).ConfigureAwait(false)).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

internal abstract class BbFile(string name, long length, DateTimeOffset? modified, DateTimeOffset? created = null) : BbNode(name)
{
    public long Length { get; } = length;

    public override VfsEntry Entry => VfsEntry.File(Name, Length, modified, created ?? modified);

    public abstract Task<IVfsFile> OpenAsync(BitbucketSource source, CancellationToken ct);
}

/// <summary>A file the source makes itself and holds in memory (a pull request as Markdown, its diff).</summary>
internal sealed class BbText(string name, byte[] content, DateTimeOffset? modified, DateTimeOffset? created = null) : BbFile(name, content.Length, modified, created)
{
    public override Task<IVfsFile> OpenAsync(BitbucketSource source, CancellationToken ct) => Task.FromResult<IVfsFile>(new MemoryFile(content, "a file made from Bitbucket's data"));
}

/// <summary>A folder with nothing in it: a submodule, whose repository is another's.</summary>
internal sealed class BbEmptyFolder(string name, DateTimeOffset? modified) : BbFolder(name, modified, modified)
{
    public override Task<IReadOnlyList<BbNode>> ListAsync(BitbucketSource source, CancellationToken ct) => Task.FromResult<IReadOnlyList<BbNode>>([]);
}

/// <summary>The whole server: a folder per project the account can see, <c>Name (KEY)</c>.</summary>
internal sealed class BitbucketServerRoot() : BbFolder("")
{
    public override async Task<IReadOnlyList<BbNode>> ListAsync(BitbucketSource source, CancellationToken ct)
    {
        var projects = await source.ProjectsAsync(ct).ConfigureAwait(false);
        var names = AtlassianNames.Unique(projects.Select(ProjectFolderName).ToList());
        return projects.Select((p, i) => (BbNode)new BbProjectFolder(p, names[i])).ToList();
    }

    /// <summary>A project's folder: <c>Alpha project (ABC)</c>.</summary>
    public static string ProjectFolderName(BbProject p) => $"{AtlassianNames.Title(p.Name)} ({p.Key})";
}

/// <summary>A project: a folder per repository, by name.</summary>
internal sealed class BbProjectFolder(BbProject project, string name) : BbFolder(name)
{
    public BbProject Project { get; } = project;

    public override async Task<IReadOnlyList<BbNode>> ListAsync(BitbucketSource source, CancellationToken ct)
    {
        var repositories = await source.RepositoriesAsync(Project.Key, ct).ConfigureAwait(false);
        var names = AtlassianNames.Unique(repositories.Select(r => r.Name).ToList());
        return repositories.Select((r, i) => (BbNode)new BbRepositoryFolder(r, names[i])).ToList();
    }
}

/// <summary>
/// A repository: <c>files</c> (its default branch), <c>branches</c> and <c>tags</c> (each the repository as it stands
/// there), <c>pull requests</c>; a repository with no commits yet has no <c>files</c>.
/// </summary>
internal sealed class BbRepositoryFolder(BbRepository repository, string name) : BbFolder(name)
{
    public const string FilesFolder = "files";
    public const string BranchesFolder = "branches";
    public const string TagsFolder = "tags";
    public const string PullRequestsFolder = "pull requests";

    public BbRepository Repository { get; } = repository;

    public override async Task<IReadOnlyList<BbNode>> ListAsync(BitbucketSource source, CancellationToken ct)
    {
        var children = new List<BbNode>();
        if (await source.DefaultBranchAsync(Repository, ct).ConfigureAwait(false) is { } head)
        {
            children.Add(new BbFilesFolder(Repository, head));
        }

        if (source.Settings.BranchesAndTags)
        {
            children.Add(new BbRefsFolder(Repository, isTags: false, "", BranchesFolder));
            children.Add(new BbRefsFolder(Repository, isTags: true, "", TagsFolder));
        }

        if (source.Settings.PullRequests)
        {
            children.Add(new BbPullRequestsFolder(Repository));
        }

        return children;
    }
}

/// <summary>The repository as it stands on its default branch, at the head it had when the folder was listed.</summary>
internal sealed class BbFilesFolder(BbRepository repository, BbRef head) : BbFolder(BbRepositoryFolder.FilesFolder)
{
    public BbRepository Repository { get; } = repository;

    public BbRef Head { get; } = head;

    public override Task<IReadOnlyList<BbNode>> ListAsync(BitbucketSource source, CancellationToken ct)
        => new BbTreeFolder(Repository, Head.LatestCommit, "", Name, null).ListAsync(source, ct);
}

/// <summary>
/// The branches or the tags, by name: a <c>/</c> in a name makes a folder (<c>feature/x</c> is <c>feature\x</c>), and each
/// ref's folder is the repository at its commit.
/// </summary>
internal sealed class BbRefsFolder(BbRepository repository, bool isTags, string prefix, string name) : BbFolder(name)
{
    public override async Task<IReadOnlyList<BbNode>> ListAsync(BitbucketSource source, CancellationToken ct)
    {
        var refs = isTags ? await source.TagsAsync(repository, ct).ConfigureAwait(false) : await source.BranchesAsync(repository, ct).ConfigureAwait(false);
        var level = new SortedDictionary<string, BbRef?>(StringComparer.Ordinal);
        foreach (var r in refs)
        {
            if (!r.DisplayId.StartsWith(prefix, StringComparison.Ordinal) || r.DisplayId.Length == prefix.Length)
            {
                continue;
            }

            var rest = r.DisplayId[prefix.Length..];
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
        var names = AtlassianNames.Unique(raw);
        var children = new List<BbNode>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            if (level[raw[i]] is { } r)
            {
                // the ref's folder is the repository at its commit, dated by it
                var commit = await source.CommitAsync(repository, r.LatestCommit, ct).ConfigureAwait(false);
                children.Add(new BbTreeFolder(repository, r.LatestCommit, "", names[i], commit?.CommitTime));
            }
            else
            {
                children.Add(new BbRefsFolder(repository, isTags, prefix + raw[i] + "/", names[i]));
            }
        }

        return children;
    }
}

/// <summary>A folder of the repository at a commit: never changes, so listed once and kept for the run.</summary>
internal sealed class BbTreeFolder(BbRepository repository, string commit, string path, string name, DateTimeOffset? time) : BbFolder(name, time, time)
{
    public BbRepository Repository { get; } = repository;

    public string Commit { get; } = commit;

    /// <summary>The folder's path in the repository; empty for its root.</summary>
    public string RepoPath { get; } = path;

    public override async Task<IReadOnlyList<BbNode>> ListAsync(BitbucketSource source, CancellationToken ct)
    {
        var entries = await source.TreeAsync(Repository, Commit, RepoPath, ct).ConfigureAwait(false);
        var time = Modified ?? (await source.CommitAsync(Repository, Commit, ct).ConfigureAwait(false))?.CommitTime;
        var ordered = entries.OrderBy(e => e.IsDirectory || e.IsSubmodule ? 0 : 1).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
        var names = AtlassianNames.Unique(ordered.Select(e => e.Name).ToList());
        var children = new List<BbNode>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            var full = RepoPath.Length == 0 ? entry.Name : RepoPath + "/" + entry.Name;
            children.Add(entry.IsDirectory ? new BbTreeFolder(Repository, Commit, full, names[i], time)
                : entry.IsSubmodule ? new BbEmptyFolder(names[i], time)
                : new BbBlobFile(Repository, Commit, full, entry.Size, names[i], time));
        }

        return children;
    }
}

/// <summary>A file of the repository at a commit: its bytes as git keeps them, streamed as it is opened.</summary>
internal sealed class BbBlobFile(BbRepository repository, string commit, string path, long size, string name, DateTimeOffset? time) : BbFile(name, size, time, time)
{
    public override Task<IVfsFile> OpenAsync(BitbucketSource source, CancellationToken ct) => source.OpenBlobAsync(repository, commit, path, Length, ct);
}

/// <summary>The repository's pull requests (open ones, or all), newest first: each a folder <c>#12 Title</c>.</summary>
internal sealed class BbPullRequestsFolder(BbRepository repository) : BbFolder(BbRepositoryFolder.PullRequestsFolder)
{
    public BbRepository Repository { get; } = repository;

    public override async Task<IReadOnlyList<BbNode>> ListAsync(BitbucketSource source, CancellationToken ct)
    {
        var requests = await source.PullRequestsAsync(Repository, ct).ConfigureAwait(false);
        return requests.Select(pr => (BbNode)new BbPullRequestFolder(Repository, pr)).ToList();
    }

    /// <summary>A pull request by the number its folder's name starts with, read alone: a name a kept listing still gives after the title changed opens it as it is now.</summary>
    public override async Task<BbNode?> FindAsync(BitbucketSource source, string name, CancellationToken ct)
    {
        if (BitbucketScope.NumberOfFolder(name) is not { } number)
        {
            return null;
        }

        var pr = await source.PullRequestAsync(Repository, number, ct).ConfigureAwait(false);
        return pr is null ? null : new BbPullRequestFolder(Repository, pr);
    }
}

/// <summary>A pull request: its <c>pull-request.md</c> (fields, description and comments) and its <c>changes.diff</c>.</summary>
internal sealed class BbPullRequestFolder(BbRepository repository, BbPullRequest request) : BbFolder(BitbucketDocuments.FolderName(request.Id, request.Title), request.Updated, request.Created)
{
    public BbPullRequest Request { get; } = request;

    public override Task<IReadOnlyList<BbNode>> ListAsync(BitbucketSource source, CancellationToken ct) => source.PullRequestChildrenAsync(repository, Request, ct);
}
