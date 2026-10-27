using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.GitLab;

/// <summary>
/// A GitLab server, group or project (type <c>gitlab</c>, spec 7.8), read-only: groups and projects as GitLab names them,
/// each project's repository on its default branch (<c>files</c>), every branch and tag, and the project's other folders.
/// Names are compared in either case, as Windows does.
/// </summary>
public sealed partial class GitLabSource : SourceBase, ICountable
{
    private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(5);

    private readonly GitLabSettings _settings;
    private readonly GitLabStandIn? _standIn;
    private readonly CancellationTokenSource _stopping = new();
    private GitLabSession? _session;
    private GitLabApi? _api;
    private Listings? _listings;
    private TreeStore? _trees;
    private SpoolCache? _spool;
    private GlFolder? _root;
    private Task _saving = Task.CompletedTask;
    private volatile bool _graphQlRefused;
    private bool _degraded;

    public GitLabSource(GitLabSettings settings)
        : this(settings, standIn: null)
    {
    }

    internal GitLabSource(GitLabSettings settings, GitLabStandIn? standIn)
    {
        _settings = settings;
        _standIn = standIn;
    }

    public override SourceCapabilities Capabilities => new(CanWrite: false, CaseInsensitive: true);

    internal GitLabSettings Settings => _settings;

    internal GitLabApi Api => _api ?? throw VfsException.Unavailable("GitLab is not signed in to yet");

    /// <summary>What the address names, once started.</summary>
    internal ScopeKind Scope { get; private set; }

    /// <summary>The session (for tests).</summary>
    internal GitLabSession Session => _session ?? throw new InvalidOperationException("the source has not started");

    internal Listings Cache => _listings ?? throw VfsException.Unavailable("GitLab is not signed in to yet");

    internal SpoolCache Spool => _spool ?? throw VfsException.Unavailable("GitLab is not signed in to yet");

    internal TimeSpan Refresh => _settings.Refresh;

    protected override async Task OnInitialiseAsync(CancellationToken ct)
    {
        _session = new GitLabSession(_settings, Logger, _standIn, Context.Secrets);
        await _session.SignInAsync(interactive: true, message => Context.ReportProgress(message), ct).ConfigureAwait(false);
        _api = new GitLabApi(_session, _settings.Concurrency, Logger);
        _listings = new Listings(_stopping.Token);
        _trees = new TreeStore(Path.Combine(Context.StateDirectory, TreeStore.FileName), Logger);
        _spool = new SpoolCache(Path.Combine(Context.CacheDirectory, "spool"), logger: Logger);

        var path = _session.Address.Path;
        Context.ReportProgress(path.Length == 0 ? $"Opening {_session.Address.Server.Host}" : $"Opening {path}");
        try
        {
            if (path.Length == 0)
            {
                Scope = ScopeKind.Server;
                _root = new ServerRoot();
            }
            else if (await _api.FindAsync($"projects/{GitLabAddress.Id(path)}", ct).ConfigureAwait(false) is { } project)
            {
                using (project)
                {
                    Scope = ScopeKind.Project;
                    _root = new ProjectFolder(GlProject.Parse(project.RootElement), "");
                }
            }
            else if (await _api.FindAsync($"groups/{GitLabAddress.Id(path)}?with_projects=false", ct).ConfigureAwait(false) is { } group)
            {
                using (group)
                {
                    Scope = ScopeKind.Group;
                    _root = new GroupFolder(GlGroup.Parse(group.RootElement), "");
                }
            }
            else
            {
                throw new SourceFailedException($"GitLab ({_session.Address.Server.Host}) has no group or project {path} that {_session.User?.Username ?? "this account"} can see.", permanent: true);
            }
        }
        catch (VfsException ex)
        {
            throw new SourceFailedException($"Opening {(path.Length == 0 ? _session.Address.Server.Host : path)}: {ex.Message}", permanent: ex.Error == VfsError.AccessDenied, ex);
        }

        Logger.LogInformation("gitlab: serving {Scope} {Path} on {Server} as {User} ({Trees:N0} repository folders kept)",
            Scope.ToString().ToLowerInvariant(), path, _session.Address.Server, _session.User?.Username, _trees.Count);
        _saving = SaveAsync(_stopping.Token);
    }

    // --- the VFS -------------------------------------------------------------------------------------------------------

    public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        if (node is not GlFolder folder)
        {
            throw VfsException.NotADirectory(path);
        }

        foreach (var child in await Guard(() => folder.ListAsync(this, ct)).ConfigureAwait(false))
        {
            yield return child.Entry;
        }
    }

    public override async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        try
        {
            return (await ResolveAsync(path, ct).ConfigureAwait(false)).Entry;
        }
        catch (VfsException ex) when (ex.Error is VfsError.NotFound or VfsError.NotADirectory)
        {
            return null;
        }
    }

    public override async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        if (mode.IsWrite())
        {
            throw VfsException.NotSupported("writing to GitLab");
        }

        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        return node is GlFile file ? await Guard(() => file.OpenAsync(this, ct)).ConfigureAwait(false) : throw VfsException.IsADirectory(path);
    }

    /// <summary>The node at <paramref name="path"/>, found a listing at a time from the root (each through the caches).</summary>
    private async Task<GlNode> ResolveAsync(string path, CancellationToken ct)
    {
        GlNode node = _root ?? throw VfsException.Unavailable("GitLab is not signed in to yet");
        var normalized = VfsPath.Normalize(path);
        if (normalized.Length == 0)
        {
            return node;
        }

        foreach (var segment in VfsPath.Split(normalized))
        {
            if (node is not GlFolder folder)
            {
                throw VfsException.NotADirectory(path);
            }

            var children = await Guard(() => folder.ListAsync(this, ct)).ConfigureAwait(false);
            node = children.FirstOrDefault(c => c.Name.Equals(segment, StringComparison.OrdinalIgnoreCase)) ?? throw VfsException.NotFound(path);
        }

        return node;
    }

    /// <summary>Runs a request to GitLab, marking the source Degraded while GitLab cannot be reached and Ready again once it answers.</summary>
    private async Task<T> Guard<T>(Func<Task<T>> work)
    {
        try
        {
            var result = await work().ConfigureAwait(false);
            MarkHealthy();
            return result;
        }
        catch (VfsException ex) when (ex.Error == VfsError.Unavailable)
        {
            MarkDegraded(ex.Message);
            throw;
        }
    }

    // --- what the folders ask for --------------------------------------------------------------------------------------

    /// <summary>The groups and projects the account is a member of, for the whole server: asked again every ten minutes at most.</summary>
    internal Task<Memberships> MembershipsAsync(CancellationToken ct)
        => Cache.GetAsync("memberships", Refresh < TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : Refresh, async c =>
        {
            var groups = new List<GlGroup>();
            await foreach (var g in Api.PagesAsync("groups?min_access_level=10&per_page=100&order_by=id&sort=asc", c).ConfigureAwait(false))
            {
                groups.Add(GlGroup.Parse(g));
            }

            var projects = new List<GlProject>();
            await foreach (var p in Api.PagesAsync("projects?membership=true&simple=true&per_page=100&order_by=id&sort=asc&pagination=keyset", c).ConfigureAwait(false))
            {
                projects.Add(GlProject.Parse(p));
            }

            Logger.LogInformation("gitlab: {User} is a member of {Groups:N0} groups and {Projects:N0} projects", Session.User?.Username, groups.Count, projects.Count);
            return new Memberships(groups, projects);
        }, ct);

    /// <summary>A group's subgroups and its own projects (not those shared with it, which show under their own groups).</summary>
    internal Task<GroupContents> GroupContentsAsync(GlGroup group, CancellationToken ct)
        => Cache.GetAsync($"group:{group.Id}", Refresh, async c =>
        {
            var subgroups = new List<GlGroup>();
            await foreach (var g in Api.PagesAsync($"groups/{group.Id}/subgroups?per_page=100&all_available=true&order_by=path&sort=asc", c).ConfigureAwait(false))
            {
                subgroups.Add(GlGroup.Parse(g));
            }

            var projects = new List<GlProject>();
            await foreach (var p in Api.PagesAsync($"groups/{group.Id}/projects?per_page=100&include_subgroups=false&with_shared=false&order_by=path&sort=asc", c).ConfigureAwait(false))
            {
                projects.Add(GlProject.Parse(p));
            }

            return new GroupContents(subgroups, projects);
        }, ct);

    /// <summary>A project as it stands now (its default branch, its features), asked again once the refresh interval has passed.</summary>
    internal Task<GlProject> ProjectAsync(long id, CancellationToken ct)
        => Cache.GetAsync($"project:{id}", Refresh, async c =>
        {
            using var doc = await Api.GetAsync($"projects/{id}", c).ConfigureAwait(false);
            return GlProject.Parse(doc.RootElement);
        }, ct);

    /// <summary>The default branch's head (its commit and when it was made), or null for a project with no commits.</summary>
    internal async Task<GlRef?> HeadAsync(GlProject project, CancellationToken ct)
    {
        var current = await ProjectAsync(project.Id, ct).ConfigureAwait(false);
        if (current.EmptyRepo || string.IsNullOrEmpty(current.DefaultBranch))
        {
            return null;
        }

        var head = await Cache.GetAsync($"head:{project.Id}:{current.DefaultBranch}", Refresh, async c =>
        {
            using var doc = await Api.FindAsync($"projects/{project.Id}/repository/branches/{Uri.EscapeDataString(current.DefaultBranch)}", c).ConfigureAwait(false);
            return new Box<GlRef?>(doc is null ? null : GlRef.Parse(doc.RootElement));
        }, ct).ConfigureAwait(false);
        return head.Value;
    }

    /// <summary>A project's branches or tags, asked again once the refresh interval has passed.</summary>
    internal Task<List<GlRef>> RefsAsync(GlProject project, RefKind kind, CancellationToken ct)
        => Cache.GetAsync($"refs:{project.Id}:{kind}", Refresh, async c =>
        {
            var refs = new List<GlRef>();
            var what = kind == RefKind.Branch ? "branches" : "tags";
            await foreach (var r in Api.PagesAsync($"projects/{project.Id}/repository/{what}?per_page=100", c).ConfigureAwait(false))
            {
                refs.Add(GlRef.Parse(r));
            }

            return refs;
        }, ct);

    /// <summary>A repository folder at a commit: the tree's entries, each file with its size (one GraphQL query per hundred files).</summary>
    internal Task<List<TreeItem>> TreeAsync(GlProject project, string commit, string path, CancellationToken ct)
        => (_trees ?? throw VfsException.Unavailable("GitLab is not signed in to yet")).GetAsync($"{project.Id}@{commit}:{path}", async c =>
        {
            var query = $"projects/{project.Id}/repository/tree?ref={Uri.EscapeDataString(commit)}&per_page=100&pagination=keyset"
                + (path.Length == 0 ? "" : "&path=" + Uri.EscapeDataString(path));
            var entries = new List<GlTreeEntry>();
            await foreach (var e in Api.PagesAsync(query, c).ConfigureAwait(false))
            {
                entries.Add(GlTreeEntry.Parse(e));
            }

            var sizes = await SizesAsync(project, commit, path, entries.Where(e => e.Type == "blob").Select(e => e.Name).ToList(), c).ConfigureAwait(false);
            return entries.Select(e => e.Type switch
            {
                "tree" => new TreeItem(e.Name, 't', e.Sha, 0, false),
                "commit" => new TreeItem(e.Name, 'c', e.Sha, 0, false),
                _ => new TreeItem(e.Name, e.Mode == "120000" ? 'l' : 'b', e.Sha, sizes[e.Name].Size, sizes[e.Name].Lfs),
            }).ToList();
        }, ct);

    private const string BlobSizes = """
        query($project: ID!, $ref: String!, $paths: [String!]!) {
          project(fullPath: $project) {
            repository {
              blobs(ref: $ref, paths: $paths, first: 100) {
                nodes { path size rawSize lfsOid }
              }
            }
          }
        }
        """;

    /// <summary>
    /// The files' sizes, by name: from GraphQL a hundred at a time (an LFS file's is its object's, <c>rawSize</c>), or where
    /// GraphQL is refused, one HEAD request per file (X-Gitlab-Size).
    /// </summary>
    private async Task<Dictionary<string, (long Size, bool Lfs)>> SizesAsync(GlProject project, string commit, string folder, List<string> names, CancellationToken ct)
    {
        var sizes = new Dictionary<string, (long, bool)>(StringComparer.Ordinal);
        var prefix = folder.Length == 0 ? "" : folder + "/";
        if (!_graphQlRefused)
        {
            try
            {
                foreach (var batch in names.Chunk(100))
                {
                    var variables = new JsonObject
                    {
                        ["project"] = project.FullPath,
                        ["ref"] = commit,
                        ["paths"] = new JsonArray(batch.Select(n => (JsonNode)(prefix + n)).ToArray()),
                    };
                    using var doc = await Api.GraphQlAsync(BlobSizes, variables, ct).ConfigureAwait(false);
                    var nodes = doc.RootElement.Obj("data").Obj("project").Obj("repository").Obj("blobs").Arr("nodes");
                    foreach (var node in nodes)
                    {
                        var path = node.Str("path") ?? "";
                        var lfs = node.Str("lfsOid") is { Length: > 0 };
                        var name = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
                        sizes[name] = (lfs ? node.Long("rawSize") : node.Long("size"), lfs);
                    }
                }
            }
            catch (VfsException ex) when (ex.Error is VfsError.IoError or VfsError.NotFound or VfsError.AccessDenied)
            {
                _graphQlRefused = true;
                Logger.LogInformation("gitlab: GraphQL does not give the files' sizes ({Error}); asking for each file's instead", ex.Message);
            }
        }

        foreach (var name in names.Where(n => !sizes.ContainsKey(n)))
        {
            var headers = await Api.HeadAsync($"projects/{project.Id}/repository/files/{Uri.EscapeDataString(prefix + name)}?ref={Uri.EscapeDataString(commit)}", ct).ConfigureAwait(false);
            sizes[name] = (headers.TryGetValue("X-Gitlab-Size", out var size) && long.TryParse(size, out var n) ? n : 0, false);
        }

        return sizes;
    }

    /// <summary>A repository file's bytes, spooled from GitLab as they arrive and kept by its blob (or LFS object): the same file on two branches is one download.</summary>
    internal Task<IVfsFile> OpenBlobAsync(GlProject project, string commit, string path, TreeItem item, CancellationToken ct)
    {
        if (item.L == 0)
        {
            return Task.FromResult<IVfsFile>(new MemoryFile([]));
        }

        var key = $"gitlab:{Api.Address.Server.Host}:{project.Id}:{(item.F ? "lfs:" : "")}{item.S}";
        var relative = item.F
            ? $"projects/{project.Id}/repository/files/{Uri.EscapeDataString(path)}/raw?ref={Uri.EscapeDataString(commit)}&lfs=true"
            : $"projects/{project.Id}/repository/blobs/{item.S}/raw";
        return Spool.OpenAsync(key, item.L,
            async c => await ResponseStream.OpenAsync(await Api.OpenAsync(relative, c).ConfigureAwait(false), c).ConfigureAwait(false), ct);
    }

    // --- counting ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Counts the files of every project's default branch (a recursive listing of its tree, a hundred entries a request);
    /// branches, tags and the other folders are not counted, so the count ends as a lower bound ("+").
    /// </summary>
    public async Task CountFilesAsync(CancellationToken ct)
    {
        if (Counters.State == EnumerationState.Complete || _root is null)
        {
            return;
        }

        var projects = await ProjectsInScopeAsync(ct).ConfigureAwait(false);
        Counters.Set(0, 0, EnumerationState.InProgress);
        var done = 0;
        await Parallel.ForEachAsync(projects, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (project, c) =>
        {
            try
            {
                if (await HeadAsync(project, c).ConfigureAwait(false) is { } head)
                {
                    var query = $"projects/{project.Id}/repository/tree?ref={Uri.EscapeDataString(head.Commit)}&recursive=true&per_page=100&pagination=keyset";
                    await foreach (var e in Api.PagesAsync(query, c).ConfigureAwait(false))
                    {
                        if (e.Str("type") == "tree")
                        {
                            Counters.Add(0, 1);
                        }
                        else
                        {
                            Counters.Add(1, 0);
                        }
                    }
                }
            }
            catch (VfsException ex) when (ex.Error is VfsError.NotFound or VfsError.AccessDenied)
            {
                Logger.LogInformation("gitlab: {Project} was not counted: {Error}", project.FullPath, ex.Message);
            }

            var n = Interlocked.Increment(ref done);
            Context.ReportProgress($"Counting files: {n:N0} of {projects.Count:N0} projects", (double)n / Math.Max(1, projects.Count));
        }).ConfigureAwait(false);

        Counters.SetState(EnumerationState.Unknown);
    }

    /// <summary>The projects the source shows: the one project, a group's (with its subgroups'), or the account's memberships.</summary>
    private async Task<IReadOnlyList<GlProject>> ProjectsInScopeAsync(CancellationToken ct)
    {
        switch (_root)
        {
            case ProjectFolder project:
                return [project.Project];
            case GroupFolder group:
                var projects = new List<GlProject>();
                await foreach (var p in Api.PagesAsync($"groups/{group.Group.Id}/projects?per_page=100&include_subgroups=true&with_shared=false&simple=true", ct).ConfigureAwait(false))
                {
                    projects.Add(GlProject.Parse(p));
                }

                return projects;
            default:
                return (await MembershipsAsync(ct).ConfigureAwait(false)).Projects;
        }
    }

    // --- health and keeping --------------------------------------------------------------------------------------------

    private void MarkDegraded(string detail)
    {
        if (!_degraded && IsInitialised)
        {
            _degraded = true;
            Context.ReportHealth(ComponentHealth.Degraded, detail);
        }
    }

    private void MarkHealthy()
    {
        if (_degraded && IsInitialised)
        {
            _degraded = false;
            Context.ReportHealth(ComponentHealth.Ready);
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(SaveEvery, ct).ConfigureAwait(false);
                _trees?.Save();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _saving.ConfigureAwait(false);
        _trees?.Save();
        if (_spool is not null)
        {
            await _spool.DisposeAsync().ConfigureAwait(false);
        }

        _api?.Dispose();
        if (_session is not null)
        {
            _session.Save();
            _session.Dispose();
        }
        _stopping.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>A value kept in <see cref="Listings"/>, which keeps only references.</summary>
internal sealed record Box<T>(T Value);

/// <summary>A group's subgroups and its own projects.</summary>
internal sealed record GroupContents(List<GlGroup> Subgroups, List<GlProject> Projects);
