using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.GitLab;

/// <summary>
/// A GitLab server, group or project (type <c>gitlab</c>, spec 7.8), read-only: groups and projects as GitLab names them,
/// each project's repository on its default branch (<c>files</c>), every branch and tag, and the project's other folders.
/// Names are compared in either case, as Windows does. Folder listings are kept (the SDK's <see cref="KeptListings"/>),
/// each asked for again behind the answer once older than the refresh interval, and "Count files on start" walks the tree
/// into them, each project's default branch whole at once.
/// </summary>
public sealed partial class GitLabSource : SourceBase, ICountable
{
    private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(5);

    // GraphQL size queries (a hundred files each) at once for one folder or one whole tree
    private const int SizeQueriesAtOnce = 4;

    private readonly GitLabSettings _settings;
    private readonly GitLabStandIn? _standIn;
    private readonly CancellationTokenSource _stopping = new();
    private GitLabSession? _session;
    private GitLabApi? _api;
    private Listings? _listings;
    private TreeStore? _trees;
    private KeptListings? _kept;
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

    private TreeStore Trees => _trees ?? throw VfsException.Unavailable("GitLab is not signed in to yet");

    private KeptListings Kept => _kept ?? throw VfsException.Unavailable("GitLab is not signed in to yet");

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

        // with no refresh interval nothing is kept: every listing asks, through the caches below; the walks take half the
        // requests, so whoever browses meanwhile has the other half
        var requests = Math.Clamp(_settings.Concurrency, 1, 64);
        var walking = Math.Max(1, requests / 2);
        _kept = new KeptListings(ListFolderAsync, Counters, Logger, new KeptListingsOptions
        {
            Keep = _settings.Keeps,
            UnkeptFor = TimeSpan.Zero,
            RefreshAfter = _settings.Refresh,
            WalkEvery = _settings.WalkEvery,
            Parallelism = walking,
            MaxInFlight = walking,
            LeftOut = LeftOutAsync,
            ListTree = ListTreeAsync,
            StampOf = StampAsync,
            Name = "gitlab",

            // the listings last run saw open at once at the next start: for the same server, account and scope, the same
            // settings that shape the tree, and the same files (a file's size is a fact about how it is written)
            SaveTo = Path.Combine(Context.StateDirectory, "listings.json.gz"),
            SaveKey = $"{_session.Address.Server}|{_session.User?.Username}|{path}|{_settings.Shape}|md{GitLabDocuments.Format}",
        });

        Logger.LogInformation("gitlab: serving {Scope} {Path} on {Server} as {User} ({Trees:N0} repository folders kept)",
            Scope.ToString().ToLowerInvariant(), path, _session.Address.Server, _session.User?.Username, _trees.Count);
        _saving = SaveAsync(_stopping.Token);
    }

    // --- the VFS -------------------------------------------------------------------------------------------------------

    public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var entry in await Kept.Cache.GetAsync(VfsPath.Normalize(path), ct).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    /// <summary>A file or folder from its folder's listing (kept, or asked for), the root as it is.</summary>
    public override async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        var normalized = VfsPath.Normalize(path);
        if (normalized.Length == 0)
        {
            return (_root ?? throw VfsException.Unavailable("GitLab is not signed in to yet")).Entry;
        }

        return await Kept.Cache.StatAsync(normalized, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false);
    }

    /// <summary>A folder's listing from GitLab, through the caches below (what <see cref="KeptListings"/> keeps).</summary>
    private async IAsyncEnumerable<VfsEntry> ListFolderAsync(string path, [EnumeratorCancellation] CancellationToken ct)
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
        => Trees.GetAsync(TreeKey(project, commit, path), async c =>
        {
            var query = $"projects/{project.Id}/repository/tree?ref={Uri.EscapeDataString(commit)}&per_page=100&pagination=keyset"
                + (path.Length == 0 ? "" : "&path=" + Uri.EscapeDataString(path));
            var entries = new List<GlTreeEntry>();
            await foreach (var e in Api.PagesAsync(query, c).ConfigureAwait(false))
            {
                entries.Add(GlTreeEntry.Parse(e));
            }

            var sizes = await SizesAsync(project, commit, entries.Where(e => e.IsBlob).Select(e => RepoPath(path, e.Name)).ToList(), c).ConfigureAwait(false);
            return entries.Select(e => ItemOf(e, RepoPath(path, e.Name), sizes)).ToList();
        }, ct);

    private static string TreeKey(GlProject project, string commit, string path) => $"{project.Id}@{commit}:{path}";

    private static string RepoPath(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    /// <summary>An entry of a repository folder as kept: a file with its size, by its path in the repository.</summary>
    private static TreeItem ItemOf(GlTreeEntry e, string path, IReadOnlyDictionary<string, (long Size, bool Lfs)> sizes) => e.Type switch
    {
        "tree" => new TreeItem(e.Name, 't', e.Sha, 0, false),
        "commit" => new TreeItem(e.Name, 'c', e.Sha, 0, false),
        _ => new TreeItem(e.Name, e.Mode == "120000" ? 'l' : 'b', e.Sha, sizes[path].Size, sizes[path].Lfs),
    };

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
    /// The files' sizes, by their paths in the repository: from GraphQL a hundred at a time, a few queries at once (an LFS
    /// file's is its object's, <c>rawSize</c>), or where GraphQL is refused, one HEAD request per file (X-Gitlab-Size).
    /// </summary>
    private async Task<Dictionary<string, (long Size, bool Lfs)>> SizesAsync(GlProject project, string commit, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var sizes = new ConcurrentDictionary<string, (long Size, bool Lfs)>(StringComparer.Ordinal);
        if (!_graphQlRefused && paths.Count > 0)
        {
            try
            {
                await Parallel.ForEachAsync(paths.Chunk(100), new ParallelOptions { MaxDegreeOfParallelism = SizeQueriesAtOnce, CancellationToken = ct }, async (batch, c) =>
                {
                    var variables = new JsonObject
                    {
                        ["project"] = project.FullPath,
                        ["ref"] = commit,
                        ["paths"] = new JsonArray(batch.Select(p => (JsonNode)p).ToArray()),
                    };
                    using var doc = await Api.GraphQlAsync(BlobSizes, variables, c).ConfigureAwait(false);
                    foreach (var node in doc.RootElement.Obj("data").Obj("project").Obj("repository").Obj("blobs").Arr("nodes"))
                    {
                        var lfs = node.Str("lfsOid") is { Length: > 0 };
                        sizes[node.Str("path") ?? ""] = (lfs ? node.Long("rawSize") : node.Long("size"), lfs);
                    }
                }).ConfigureAwait(false);
            }
            catch (VfsException ex) when (ex.Error is VfsError.IoError or VfsError.NotFound or VfsError.AccessDenied)
            {
                _graphQlRefused = true;
                Logger.LogInformation("gitlab: GraphQL does not give the files' sizes ({Error}); asking for each file's instead", ex.Message);
            }
        }

        foreach (var path in paths.Where(p => !sizes.ContainsKey(p)))
        {
            var headers = await Api.HeadAsync($"projects/{project.Id}/repository/files/{Uri.EscapeDataString(path)}?ref={Uri.EscapeDataString(commit)}", ct).ConfigureAwait(false);
            sizes[path] = (headers.TryGetValue("X-Gitlab-Size", out var size) && long.TryParse(size, out var n) ? n : 0, false);
        }

        return new Dictionary<string, (long Size, bool Lfs)>(sizes, StringComparer.Ordinal);
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

    // --- counting and walking ------------------------------------------------------------------------------------------

    /// <summary>
    /// "Count files on start": the first walk of the tree (<see cref="KeptListings"/>), which lists every folder it goes into
    /// and keeps the listings, so they open at once afterwards: each project's default branch whole, a hundred entries a
    /// request (<see cref="ListTreeAsync"/>); with listings kept, a walk again every "Walk every folder every" minutes. It
    /// leaves out the folders that cost requests of their own (<see cref="GlFolder.Walked"/>), so the count ends as a lower
    /// bound ("+").
    /// </summary>
    public Task CountFilesAsync(CancellationToken ct) => Kept.CountAsync(ct);

    /// <summary>The listings as kept, for tests.</summary>
    internal ListingCache Listings => Kept.Cache;

    /// <summary>
    /// A folder's stamp for the walks (<see cref="KeptListingsOptions.StampOf"/>): a project's folder has its last activity
    /// as GitLab tells it (moved by a push, an issue, a comment, a pipeline, with an hour's grain) and its default branch's
    /// head, so a project nothing happened in is kept whole; an issue's or merge request's folder its last update, a
    /// pipeline's its last update, so a changed project costs only what changed in it; any other folder none, being a
    /// request or two to list. The project's parent listing and its head are the calls the stamps share, each kept a
    /// refresh interval.
    /// </summary>
    internal async ValueTask<string?> StampAsync(string path, string? held, CancellationToken ct)
    {
        switch (await ResolveAsync(path, ct).ConfigureAwait(false))
        {
            case ProjectFolder project:
                var head = project.Project.Repository ? await HeadAsync(project.Project, ct).ConfigureAwait(false) : null;
                return $"{project.Project.LastActivity?.UtcTicks ?? 0}|{head?.Commit}";
            case TicketFolder ticket:
                return (ticket.Ticket.Updated?.UtcTicks ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case PipelineFolder pipeline:
                return $"{pipeline.Pipeline.Updated?.UtcTicks ?? 0}|{pipeline.Pipeline.Status}";
            default:
                return null;
        }
    }

    /// <summary>For a walk: of the folders in a folder, those a walk leaves out (<see cref="GlFolder.Walked"/>).</summary>
    private async ValueTask<IReadOnlySet<string>?> LeftOutAsync(string path, CancellationToken ct)
    {
        if (await ResolveAsync(path, ct).ConfigureAwait(false) is not GlFolder folder)
        {
            return null;
        }

        var left = (await Guard(() => folder.ListAsync(this, ct)).ConfigureAwait(false))
            .OfType<GlFolder>().Where(f => !f.Walked).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        return left.Count == 0 ? null : left;
    }

    /// <summary>
    /// For a walk: a project's <c>files</c>, its default branch, whole, every folder's listing at the head commit by path,
    /// from the repository kept whole first (<see cref="FillTreeAsync"/>); null for any other folder, which the walk lists
    /// alone.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>?> ListTreeAsync(string path, CancellationToken ct)
    {
        if (await ResolveAsync(path, ct).ConfigureAwait(false) is not FilesFolder files)
        {
            return null;
        }

        var listings = new Dictionary<string, IReadOnlyList<VfsEntry>>(StringComparer.Ordinal);
        if (await Guard(() => HeadAsync(files.Project, ct)).ConfigureAwait(false) is not { } head)
        {
            listings[path] = [];
            return listings;
        }

        await Guard(() => FillTreeAsync(files.Project, head.Commit, ct)).ConfigureAwait(false);
        var pending = new Queue<(GlFolder Folder, string Path)>();
        pending.Enqueue((files.At(head), path));
        while (pending.TryDequeue(out var next))
        {
            // from what is kept now: no request
            var children = await next.Folder.ListAsync(this, ct).ConfigureAwait(false);
            listings[next.Path] = children.Select(c => c.Entry).ToList();
            foreach (var folder in children.OfType<GlFolder>())
            {
                pending.Enqueue((folder, VfsPath.Join(next.Path, folder.Name)));
            }
        }

        return listings;
    }

    /// <summary>
    /// Keeps every folder of the repository at a commit (<see cref="TreeStore"/>) from its recursive tree, a hundred entries
    /// a request, and its files' sizes a hundred at a time, where folder by folder costs a request or two a folder; nothing
    /// is asked when the commit's folders are all kept already.
    /// </summary>
    /// <returns>How many folders it kept.</returns>
    private async Task<int> FillTreeAsync(GlProject project, string commit, CancellationToken ct)
    {
        if (TreeKept(project, commit))
        {
            return 0;
        }

        var folders = new Dictionary<string, List<GlTreeEntry>>(StringComparer.Ordinal) { [""] = [] };
        List<GlTreeEntry> In(string folder) => folders.TryGetValue(folder, out var entries) ? entries : folders[folder] = [];
        var query = $"projects/{project.Id}/repository/tree?ref={Uri.EscapeDataString(commit)}&recursive=true&per_page=100&pagination=keyset";
        await foreach (var e in Api.PagesAsync(query, ct).ConfigureAwait(false))
        {
            var entry = GlTreeEntry.Parse(e);
            var at = entry.Path.Length > 0 ? entry.Path : entry.Name;
            var slash = at.LastIndexOf('/');
            In(slash < 0 ? "" : at[..slash]).Add(entry);
            if (entry.IsTree)
            {
                In(at);
            }
        }

        var blobs = folders.SelectMany(f => f.Value.Where(e => e.IsBlob).Select(e => RepoPath(f.Key, e.Name))).ToList();
        var sizes = await SizesAsync(project, commit, blobs, ct).ConfigureAwait(false);
        Trees.Keep(folders.Select(f => (TreeKey(project, commit, f.Key), f.Value.Select(e => ItemOf(e, RepoPath(f.Key, e.Name), sizes)).ToList())));
        Logger.LogDebug("gitlab: kept {Project} at {Commit} whole: {Folders:N0} folders, {Files:N0} files", project.FullPath, commit, folders.Count, blobs.Count);
        return folders.Count;
    }

    /// <summary>Whether every folder of the repository at the commit is kept, the root down.</summary>
    private bool TreeKept(GlProject project, string commit)
    {
        var pending = new Stack<string>();
        pending.Push("");
        while (pending.TryPop(out var folder))
        {
            if (!Trees.TryGet(TreeKey(project, commit, folder), out var items))
            {
                return false;
            }

            foreach (var item in items.Where(i => i.K == 't'))
            {
                pending.Push(RepoPath(folder, item.N));
            }
        }

        return true;
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

        // the walks stop before what they ask through goes, and what they kept is saved
        if (_kept is not null)
        {
            await _kept.DisposeAsync().ConfigureAwait(false);
        }

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
