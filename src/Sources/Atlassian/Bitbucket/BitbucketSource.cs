using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Atlassian.Bitbucket;

/// <summary>
/// A Bitbucket Data Center server, project or repository (type <c>bitbucket</c>, spec 7.11), read-only: a folder per
/// project, a folder per repository with its files at the default branch, each branch and tag as the repository stands
/// there, and its pull requests as Markdown with their diffs. Names are compared in either case, as Windows does. Folder
/// listings are kept (the SDK's <see cref="KeptListings"/>), and a change shows when a walk comes to it: a branch or tag
/// whose commit has not moved is kept as it was, with every folder below it; a repository folder at a commit is listed once
/// and kept for the run, since a commit never changes. A folder whose listing failed stays so until the next walk.
/// </summary>
public sealed class BitbucketSource : SourceBase, ICountable
{
    // entries a browse or a list call carries at most a page: Bitbucket's own cap
    private const int PageSize = 1000;

    private readonly BitbucketSettings _settings;
    private readonly AtlassianStandIn? _standIn;
    private readonly CancellationTokenSource _stopping = new();
    private AtlassianSession? _session;
    private AtlassianApi? _api;
    private RequestCache? _cache;
    private KeptListings? _kept;
    private SpoolCache? _spool;
    private BbFolder? _root;
    private RepositoriesCounted? _counted;
    private bool _degraded;

    public BitbucketSource(BitbucketSettings settings)
        : this(settings, standIn: null)
    {
    }

    internal BitbucketSource(BitbucketSettings settings, AtlassianStandIn? standIn)
    {
        _settings = settings;
        _standIn = standIn;
    }

    public override SourceCapabilities Capabilities => new(CanWrite: false, CaseInsensitive: true);

    internal BitbucketSettings Settings => _settings;

    internal AtlassianApi Api => _api ?? throw VfsException.Unavailable("Bitbucket is not signed in to yet");

    internal AtlassianSession Session => _session ?? throw new InvalidOperationException("the source has not started");

    /// <summary>What the address names, once started.</summary>
    internal BitbucketScope Scope { get; private set; } = BitbucketScope.Server;

    private RequestCache Cache => _cache ?? throw VfsException.Unavailable("Bitbucket is not signed in to yet");

    private KeptListings Kept => _kept ?? throw VfsException.Unavailable("Bitbucket is not signed in to yet");

    private SpoolCache Spool => _spool ?? throw VfsException.Unavailable("Bitbucket is not signed in to yet");

    private TimeSpan Refresh => _settings.Refresh > TimeSpan.Zero ? _settings.Refresh : TimeSpan.FromSeconds(2);

    /// <summary>The installation's address (its base, with a slash at the end), for the documents' links.</summary>
    internal Uri Installation => Session.Address.Base;

    protected override async Task OnInitialiseAsync(CancellationToken ct)
    {
        _session = new AtlassianSession(AtlassianProduct.Bitbucket, _settings, Logger, _standIn, Context.Secrets, Context.UpstreamWatcher);
        await _session.SignInAsync(interactive: true, message => Context.ReportProgress(message), ct).ConfigureAwait(false);
        _api = new AtlassianApi(_session, _settings.Requests, Logger);
        _cache = new RequestCache(_stopping.Token);
        _spool = new SpoolCache(Path.Combine(Context.CacheDirectory, "spool"), logger: Logger);

        Scope = BitbucketScope.From(_session.Address, out var problem) ?? throw new SourceFailedException(problem!, permanent: true);
        Context.ReportProgress(Scope.Kind == BitbucketScopeKind.Server ? $"Opening {_session.Address.Base.Host}" : $"Opening the {Scope.Kind.ToString().ToLowerInvariant()} {Scope.Value}");
        try
        {
            _root = await RootAsync(Scope, ct).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            throw new SourceFailedException($"Opening the {Scope.Kind.ToString().ToLowerInvariant()} {Scope.Value}: {ex.Message}", permanent: ex.Error is VfsError.AccessDenied or VfsError.NotFound, ex);
        }

        // a server's or a project's walk goes through "Repositories at a time" repositories at once, each a part of its own with
        // "Requests at a time per repository" of its folders listed together; one repository's walk is one part with the same
        var perRepository = Math.Clamp(_settings.RequestsPerRepository, 1, 64);
        _kept = new KeptListings(ListFolderAsync, Counters, Logger, new KeptListingsOptions
        {
            Keep = _settings.Keeps,
            UnkeptFor = TimeSpan.Zero,
            WalkEvery = _settings.WalkEvery,
            Parallelism = perRepository,
            MaxInFlight = Scope.Kind == BitbucketScopeKind.Repository ? perRepository : _settings.Requests,
            PartsBelow = Scope.Kind == BitbucketScopeKind.Repository ? null : HoldsRepositoriesAsync,
            PartsAtOnce = Math.Clamp(_settings.RepositoriesAtATime, 1, 64),
            StampOf = StampAsync,
            KeepFailures = true,
            Name = "bitbucket",

            // the listings last run saw open at once at the next start: for the same installation, account and scope, the
            // same settings that shape the tree, and the same documents (a file's size is a fact about how it is written)
            SaveTo = Path.Combine(Context.StateDirectory, "listings.json.gz"),
            SaveKey = $"{_session.Address.Server}|{_session.User?.Name}|{Scope.Kind}:{Scope.Value}|{_settings.Shape}|md{BitbucketDocuments.Format}",
        });

        _session.Renewed += _kept.Cache.ForgetFailures;
        _kept.StartWalksOfHeld();
        Logger.LogInformation("bitbucket: serving {Scope} {Value} on {Server} as {User}", Scope.Kind.ToString().ToLowerInvariant(), Scope.Value, _session.Address.Server, _session.User?.Name);
    }

    /// <summary>The folder the scope is: the server's projects, a project's repositories, or a repository.</summary>
    private async Task<BbFolder> RootAsync(BitbucketScope scope, CancellationToken ct)
    {
        switch (scope.Kind)
        {
            case BitbucketScopeKind.Project:
                return new BbProjectFolder(await ProjectAsync(scope.ProjectKey, ct).ConfigureAwait(false), "");
            case BitbucketScopeKind.Repository:
                return new BbRepositoryFolder(await RepositoryAsync(scope.ProjectKey, scope.Slug, ct).ConfigureAwait(false), "");
            default:
                return new BitbucketServerRoot();
        }
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
            return (_root ?? throw VfsException.Unavailable("Bitbucket is not signed in to yet")).Entry;
        }

        return await Kept.Cache.StatAsync(normalized, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false);
    }

    /// <summary>A folder's listing from Bitbucket, through the caches below (what <see cref="KeptListings"/> keeps).</summary>
    private async IAsyncEnumerable<VfsEntry> ListFolderAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        if (node is not BbFolder folder)
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
            throw VfsException.NotSupported("writing to Bitbucket");
        }

        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        return node is BbFile file ? await Guard(() => file.OpenAsync(this, ct)).ConfigureAwait(false) : throw VfsException.IsADirectory(path);
    }

    /// <summary>The node at <paramref name="path"/>, found a folder at a time from the root (a pull request by its number, not by listing them all).</summary>
    private async Task<BbNode> ResolveAsync(string path, CancellationToken ct)
    {
        BbNode node = _root ?? throw VfsException.Unavailable("Bitbucket is not signed in to yet");
        var normalized = VfsPath.Normalize(path);
        if (normalized.Length == 0)
        {
            return node;
        }

        foreach (var segment in VfsPath.Split(normalized))
        {
            if (node is not BbFolder folder)
            {
                throw VfsException.NotADirectory(path);
            }

            node = await Guard(() => folder.FindAsync(this, segment, ct)).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
        }

        return node;
    }

    /// <summary>Runs a request to Bitbucket, marking the source Degraded while Bitbucket cannot be reached and Ready again once it answers.</summary>
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

    /// <summary>Every element of a paged listing, page after page (<c>start</c> and <c>limit</c>, <c>isLastPage</c> and <c>nextPageStart</c>).</summary>
    internal async IAsyncEnumerable<JsonElement> PagesAsync(string relative, [EnumeratorCancellation] CancellationToken ct)
    {
        var start = 0L;
        while (true)
        {
            var separator = relative.Contains('?') ? "&" : "?";
            using var doc = await Api.GetAsync($"{relative}{separator}start={start}&limit={PageSize}", ct).ConfigureAwait(false);
            var values = doc.RootElement.Arr("values").ToList();
            foreach (var value in values)
            {
                yield return value.Clone();
            }

            if (values.Count == 0 || doc.RootElement.Bool("isLastPage") || !doc.RootElement.Has("nextPageStart"))
            {
                yield break;
            }

            var next = doc.RootElement.Long("nextPageStart");
            if (next <= start)
            {
                yield break;
            }

            start = next;
        }
    }

    /// <summary>Every project the account can see, by name (people's own projects are not listed; a person's is a scope of its own).</summary>
    internal Task<List<BbProject>> ProjectsAsync(CancellationToken ct)
        => Cache.GetAsync("projects", Refresh, async c =>
        {
            var projects = new List<BbProject>();
            await foreach (var e in PagesAsync("projects", c).ConfigureAwait(false))
            {
                projects.Add(BbProject.Parse(e));
            }

            projects = projects.Where(p => p.Key.Length > 0).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
            Logger.LogInformation("bitbucket: {User} can see {Count:N0} projects", Session.User?.Name, projects.Count);
            return projects;
        }, ct);

    internal async Task<BbProject> ProjectAsync(string key, CancellationToken ct)
    {
        var box = await Cache.GetAsync($"project:{key}", Refresh, async c =>
        {
            using var doc = await Api.FindAsync(BitbucketScope.ProjectPath(key), c).ConfigureAwait(false)
                ?? throw new VfsException(VfsError.NotFound, $"Bitbucket has no project {key} that {Session.User?.Name} can see");
            return new Box<BbProject>(BbProject.Parse(doc.RootElement));
        }, ct).ConfigureAwait(false);
        return box.Value;
    }

    /// <summary>A project's repositories, by name, those that are not available (being forked, deleted) left out.</summary>
    internal Task<List<BbRepository>> RepositoriesAsync(string projectKey, CancellationToken ct)
        => Cache.GetAsync($"repos:{projectKey}", Refresh, async c =>
        {
            var repositories = new List<BbRepository>();
            await foreach (var e in PagesAsync($"{BitbucketScope.ProjectPath(projectKey)}/repos", c).ConfigureAwait(false))
            {
                repositories.Add(BbRepository.Parse(e));
            }

            return repositories.Where(r => r.Slug.Length > 0 && r.State.Equals("AVAILABLE", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Slug, StringComparer.Ordinal).ToList();
        }, ct);

    internal async Task<BbRepository> RepositoryAsync(string projectKey, string slug, CancellationToken ct)
    {
        var box = await Cache.GetAsync($"repo:{projectKey}/{slug}", Refresh, async c =>
        {
            using var doc = await Api.FindAsync(BitbucketScope.RepositoryPath(projectKey, slug), c).ConfigureAwait(false)
                ?? throw new VfsException(VfsError.NotFound, $"Bitbucket has no repository {projectKey}/{slug} that {Session.User?.Name} can see");
            return new Box<BbRepository>(BbRepository.Parse(doc.RootElement));
        }, ct).ConfigureAwait(false);
        return box.Value;
    }

    /// <summary>A repository's branches, by name, the default marked; none for a repository with no commits yet.</summary>
    internal Task<List<BbRef>> BranchesAsync(BbRepository repository, CancellationToken ct)
        => Cache.GetAsync($"branches:{repository.Path}", Refresh, async c =>
        {
            var refs = new List<BbRef>();
            await foreach (var e in PagesAsync($"{repository.Path}/branches?orderBy=ALPHABETICAL", c).ConfigureAwait(false))
            {
                refs.Add(BbRef.Parse(e));
            }

            return refs.Where(r => r.DisplayId.Length > 0 && r.LatestCommit.Length > 0).ToList();
        }, ct);

    /// <summary>A repository's tags, by name.</summary>
    internal Task<List<BbRef>> TagsAsync(BbRepository repository, CancellationToken ct)
        => Cache.GetAsync($"tags:{repository.Path}", Refresh, async c =>
        {
            var refs = new List<BbRef>();
            await foreach (var e in PagesAsync($"{repository.Path}/tags?orderBy=ALPHABETICAL", c).ConfigureAwait(false))
            {
                refs.Add(BbRef.Parse(e));
            }

            return refs.Where(r => r.DisplayId.Length > 0 && r.LatestCommit.Length > 0).ToList();
        }, ct);

    /// <summary>The default branch, from the branches (one request, which the branches folder shares); null for a repository with no commits yet.</summary>
    internal async Task<BbRef?> DefaultBranchAsync(BbRepository repository, CancellationToken ct)
    {
        var branches = await BranchesAsync(repository, ct).ConfigureAwait(false);
        return branches.FirstOrDefault(b => b.IsDefault) ?? branches.FirstOrDefault(b => b.DisplayId is "main" or "master");
    }

    /// <summary>A commit (its time, for the folders at it), kept for the run: a commit never changes. Null where Bitbucket has none by that id.</summary>
    internal async Task<BbCommit?> CommitAsync(BbRepository repository, string id, CancellationToken ct)
    {
        var box = await Cache.GetAsync($"commit:{repository.Path}@{id}", null, async c =>
        {
            using var doc = await Api.FindAsync($"{repository.Path}/commits/{Uri.EscapeDataString(id)}", c).ConfigureAwait(false);
            return new Box<BbCommit?>(doc is null ? null : BbCommit.Parse(doc.RootElement));
        }, ct).ConfigureAwait(false);
        return box.Value;
    }

    /// <summary>A repository folder's entries at a commit (the browse call, a page of a thousand at a time), kept for the run.</summary>
    internal Task<List<BbTreeEntry>> TreeAsync(BbRepository repository, string commit, string path, CancellationToken ct)
        => Cache.GetAsync($"tree:{repository.Path}@{commit}:{path}", null, async c =>
        {
            var entries = new List<BbTreeEntry>();
            var start = 0L;
            while (true)
            {
                using var doc = await Api.GetAsync($"{repository.Path}/browse{RepoPath(path)}?at={Uri.EscapeDataString(commit)}&start={start}&limit={PageSize}", c).ConfigureAwait(false);
                var children = doc.RootElement.Obj("children");
                if (children.ValueKind != JsonValueKind.Object)
                {
                    throw new VfsException(VfsError.NotADirectory, $"{path} is a file in {repository.Name}, not a folder");
                }

                var page = children.Arr("values").Select(BbTreeEntry.Parse).Where(e => e.Name.Length > 0).ToList();
                entries.AddRange(page);
                if (page.Count == 0 || children.Bool("isLastPage") || !children.Has("nextPageStart"))
                {
                    break;
                }

                var next = children.Long("nextPageStart");
                if (next <= start)
                {
                    break;
                }

                start = next;
            }

            return entries;
        }, ct);

    /// <summary>A path in the repository, each segment escaped, with a slash before it; empty for the root.</summary>
    private static string RepoPath(string path) => path.Length == 0 ? "" : "/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>A file's bytes at a commit, spooled from Bitbucket as they arrive and kept by commit and path (what a commit holds never changes).</summary>
    internal Task<IVfsFile> OpenBlobAsync(BbRepository repository, string commit, string path, long length, CancellationToken ct)
    {
        if (length == 0)
        {
            return Task.FromResult<IVfsFile>(new MemoryFile([]));
        }

        var url = Api.Rest($"{repository.Path}/raw{RepoPath(path)}?at={Uri.EscapeDataString(commit)}");
        return Spool.OpenAsync($"bitbucket:{Installation.Authority}{Installation.AbsolutePath}:{repository.Path}@{commit}:{path}", length,
            async c => await ResponseStream.OpenAsync(await Api.OpenAsync(url, "a file", c).ConfigureAwait(false), c).ConfigureAwait(false), ct);
    }

    /// <summary>A repository's pull requests, newest first (open ones alone with "Open pull requests only").</summary>
    internal Task<List<BbPullRequest>> PullRequestsAsync(BbRepository repository, CancellationToken ct)
        => Cache.GetAsync($"prs:{repository.Path}", Refresh, async c =>
        {
            var requests = new List<BbPullRequest>();
            await foreach (var e in PagesAsync($"{repository.Path}/pull-requests?state={(_settings.OpenOnly ? "OPEN" : "ALL")}&order=NEWEST", c).ConfigureAwait(false))
            {
                requests.Add(BbPullRequest.Parse(e));
            }

            return requests.Where(pr => pr.Id > 0).ToList();
        }, ct);

    /// <summary>One pull request, as the list last gave it, else asked for alone; null where there is none (or it is closed and only open ones show).</summary>
    internal async Task<BbPullRequest?> PullRequestAsync(BbRepository repository, long id, CancellationToken ct)
    {
        if (Cache.Peek<List<BbPullRequest>>($"prs:{repository.Path}") is { } listed && listed.FirstOrDefault(pr => pr.Id == id) is { } known)
        {
            return known;
        }

        var box = await Cache.GetAsync($"pr:{repository.Path}#{id}", Refresh, async c =>
        {
            using var doc = await Api.FindAsync($"{repository.Path}/pull-requests/{id}", c).ConfigureAwait(false);
            var pr = doc is null ? null : BbPullRequest.Parse(doc.RootElement);
            return new Box<BbPullRequest?>(pr is not null && _settings.OpenOnly && !pr.Open ? null : pr);
        }, ct).ConfigureAwait(false);
        return box.Value;
    }

    /// <summary>A pull request folder's children: its <c>pull-request.md</c> and, where it has changes, its <c>changes.diff</c>.</summary>
    internal async Task<IReadOnlyList<BbNode>> PullRequestChildrenAsync(BbRepository repository, BbPullRequest pr, CancellationToken ct)
    {
        var activities = await ActivitiesAsync(repository, pr, ct).ConfigureAwait(false);
        var children = new List<BbNode> { new BbText(BitbucketDocuments.PullRequestFile, BitbucketDocuments.PullRequest(repository, pr, activities, Installation), pr.Updated, pr.Created) };
        var diff = await DiffAsync(repository, pr, ct).ConfigureAwait(false);
        if (diff.Length > 0)
        {
            children.Add(new BbText(BitbucketDocuments.DiffFile, diff, pr.Updated, pr.Created));
        }

        return children;
    }

    /// <summary>What happened to a pull request, as its version stands (kept for the run by version: a new comment moves the version).</summary>
    private Task<List<BbActivity>> ActivitiesAsync(BbRepository repository, BbPullRequest pr, CancellationToken ct)
        => Cache.GetAsync($"activities:{repository.Path}#{pr.Id}@{pr.Version}|{pr.Updated?.UtcTicks}", null, async c =>
        {
            var activities = new List<BbActivity>();
            await foreach (var e in PagesAsync($"{repository.Path}/pull-requests/{pr.Id}/activities", c).ConfigureAwait(false))
            {
                activities.Add(BbActivity.Parse(e));
            }

            return activities;
        }, ct);

    /// <summary>A pull request's diff as Bitbucket gives it (text), kept for the run by version; empty where it has none.</summary>
    private Task<byte[]> DiffAsync(BbRepository repository, BbPullRequest pr, CancellationToken ct)
        => Cache.GetAsync($"diff:{repository.Path}#{pr.Id}@{pr.Version}|{pr.FromCommit}", null, async c =>
        {
            using var response = await Api.OpenAsync(Api.Rest($"{repository.Path}/pull-requests/{pr.Id}.diff"), "a pull request's diff", c).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(c).ConfigureAwait(false);
            return Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"));
        }, ct);

    // --- counting and walking ------------------------------------------------------------------------------------------

    /// <summary>
    /// "Count files on start": the first walk of the tree (<see cref="KeptListings"/>), which lists every folder and keeps
    /// the listings, so they open at once afterwards; with listings kept, a walk again every "Walk every folder every"
    /// minutes. A server's or a project's count says how far it has got through its repositories.
    /// </summary>
    public async Task CountFilesAsync(CancellationToken ct)
    {
        if (Scope.Kind != BitbucketScopeKind.Repository)
        {
            Context.ReportProgress("Counting files: listing the repositories");
            Volatile.Write(ref _counted, new RepositoriesCounted(Context, await RepositoriesToCountAsync(ct).ConfigureAwait(false)));
        }

        try
        {
            await Kept.CountAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _counted, null)?.Over();
        }
    }

    /// <summary>How many repositories a count goes through: the project's, or every project's (a request a project, which the walk needs anyway). Null where that failed.</summary>
    private async Task<long?> RepositoriesToCountAsync(CancellationToken ct)
    {
        try
        {
            if (_root is BbProjectFolder { Project: var project })
            {
                return (await RepositoriesAsync(project.Key, ct).ConfigureAwait(false)).Count;
            }

            long total = 0;
            foreach (var p in await ProjectsAsync(ct).ConfigureAwait(false))
            {
                total += (await RepositoriesAsync(p.Key, ct).ConfigureAwait(false)).Count;
            }

            return total;
        }
        catch (VfsException ex)
        {
            Logger.LogDebug("bitbucket: how many repositories there are to count is not known: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>How far a count has got through the repositories: each said once as the walk comes to it, one at a time, so what is said keeps the order of the count.</summary>
    private sealed class RepositoriesCounted(ISourceContext context, long? total)
    {
        private readonly HashSet<string> _reached = new(StringComparer.Ordinal);
        private bool _over;

        public void Reached(BbRepository repository)
        {
            lock (_reached)
            {
                if (_over || !_reached.Add(repository.Path))
                {
                    return;
                }

                long count = _reached.Count;
                if (total is > 0 and var known)
                {
                    context.ReportProgress($"Counting files: {Math.Min(count, known):N0} of {known:N0} repositories", (double)Math.Min(count - 1, known - 1) / known);
                }
                else
                {
                    context.ReportProgress($"Counting files: {count:N0} repositories so far");
                }
            }
        }

        public void Over()
        {
            lock (_reached)
            {
                _over = true;
            }
        }
    }

    /// <summary>The listings as kept, for tests.</summary>
    internal ListingCache Listings => Kept.Cache;

    /// <summary>One walk now (a test's: the walks after the count come by the clock), stamps kept unless <paramref name="full"/>.</summary>
    internal Task<bool> WalkAsync(SourceCounters counters, bool full, CancellationToken ct) => Kept.WalkAsync(counters, full, ct);

    /// <summary>For a walk: whether a folder's folders are each walked as a part of their own: the server's projects and a project's repositories.</summary>
    private async ValueTask<bool> HoldsRepositoriesAsync(string path, CancellationToken ct)
        => await ResolveAsync(path, ct).ConfigureAwait(false) is BitbucketServerRoot or BbProjectFolder;

    /// <summary>
    /// A folder's stamp for the walks (<see cref="KeptListingsOptions.StampOf"/>): a repository's <c>files</c>, and each branch's
    /// or tag's folder, has its commit, so one whose commit has not moved is kept whole; the pull requests folder has the newest
    /// request's time, and a pull request folder its version. Any other folder none: a project's repositories, a repository's
    /// branches and tags are a request to list each.
    /// </summary>
    internal async ValueTask<string?> StampAsync(string path, string? held, CancellationToken ct)
    {
        switch (await ResolveAsync(path, ct).ConfigureAwait(false))
        {
            case BbRepositoryFolder repository:
                Volatile.Read(ref _counted)?.Reached(repository.Repository);
                return null;
            case BbFilesFolder files:
                return files.Head.LatestCommit;
            case BbTreeFolder { RepoPath.Length: 0 } tree:
                return tree.Commit;
            case BbPullRequestsFolder requests:
                var newest = (await PullRequestsAsync(requests.Repository, ct).ConfigureAwait(false)).FirstOrDefault();
                return newest is null ? "none" : $"{newest.Id}|{newest.Version}|{newest.Updated?.UtcTicks ?? 0}";
            case BbPullRequestFolder request:
                return $"{request.Request.Version}|{request.Request.Updated?.UtcTicks ?? 0}";
            default:
                return null;
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

    public override async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_kept is not null)
        {
            await _kept.DisposeAsync().ConfigureAwait(false);
        }

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
