using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.GitLab;

/// <summary>
/// A GitLab server, group or project (type <c>gitlab</c>, spec 7.8), read-only: groups and projects as GitLab names them,
/// each project's repository on its default branch (<c>files</c>), every branch and tag, and the project's other folders.
/// Names are compared in either case, as Windows does. Folder listings are kept (the SDK's <see cref="KeptListings"/>),
/// and a change shows when a walk comes to it: the walks go through the folders opened, and "Count files on start" walks the tree
/// into them, each repository whole at once: a project's default branch, and each of its branches and tags at its commit.
/// </summary>
public sealed partial class GitLabSource : SourceBase, ICountable
{
    // the tile follows the requests made of the server, which the client tells of as they go, not the calls made of the
    // source: one of those may ask nothing, and may hand back a file whose bytes are asked for only later
    protected override bool HealthFollowsCalls => false;

    // how many folders' last listings are remembered at most (ResolveAsync); past that they start again
    private const int MostFoldersRemembered = 200_000;

    // how many folders' folders are remembered for the walks at most (ResolveAsync); past that they start again, and a walk
    // lists once more the few it is in
    private const int MostWalkedRemembered = 50_000;

    // a page of GitLab's listings at its largest
    private const int ProjectsPage = 100;

    // how many ranges of project ids for each that lists them at once (EveryProjectAsync): on the lab's 3,505 projects 1, 2, 4 and
    // 8 took the same time within its noise, and the more of them, the less a cluster of ids holds up the rest
    private const int RangesPerWalker = 4;

    // what each folder last listed of folders, found by name: on the way down to a project for every call (_lastListed),
    // anywhere else for the walks alone (_walked)
    private readonly ConcurrentDictionary<string, FoldersListed> _lastListed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FoldersListed> _walked = new(StringComparer.OrdinalIgnoreCase);
    private readonly GitLabSettings _settings;
    private readonly GitLabStandIn? _standIn;
    private readonly CancellationTokenSource _stopping = new();
    private GitLabSession? _session;
    private GitLabApi? _api;
    private RequestCache? _listings;
    private KeptListings? _kept;
    private SpoolCache? _spool;
    private GlFolder? _root;
    private ProjectsCounted? _counted;
    // GraphQL does not give the files' sizes on this server (an old one, or one that refuses it): told by its first size
    // query, and for the run. Once one has been answered, a query that fails is that query's alone
    private volatile bool _graphQlRefused;
    private volatile bool _graphQlAnswered;

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

    internal RequestCache Cache => _listings ?? throw VfsException.Unavailable("GitLab is not signed in to yet");

    internal SpoolCache Spool => _spool ?? throw VfsException.Unavailable("GitLab is not signed in to yet");

    private KeptListings Kept => _kept ?? throw VfsException.Unavailable("GitLab is not signed in to yet");

    internal TimeSpan Refresh => _settings.Refresh;

    protected override async Task OnInitialiseAsync(CancellationToken ct)
    {
        _session = new GitLabSession(_settings, Logger, _standIn, Context.Secrets, Context.UpstreamWatcher);
        await _session.SignInAsync(interactive: Context.Attended, message => Context.ReportProgress(message), ct).ConfigureAwait(false);
        _api = new GitLabApi(_session, Requests, Logger);
        _api.TellHealth(MarkHealthy, MarkDegraded);
        _api.Held += (wait, why) => Volatile.Read(ref _counted)?.Waiting(wait, why);
        _listings = new RequestCache(_stopping.Token, _standIn?.AnswersKept ?? RequestCache.DefaultMaxBytes);
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
                    // nothing lists the root again, so what this says of it is asked for afresh as it ages
                    _root = new ProjectFolder(GlProject.Parse(project.RootElement) with { Whole = false }, "");
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

        // with no refresh interval nothing is kept: every listing asks, through the caches below
        _kept = new KeptListings(ListFolderAsync, Counters, Logger, new KeptListingsOptions
        {
            Keep = _settings.Keeps,
            Unsaved = why => HoldDegraded("listings", why),
            UnkeptFor = TimeSpan.Zero,
            WalkEvery = _settings.WalkEvery,

            // a server's or a group's walk goes through "Projects at a time" projects at once, each a part of its own with
            // "Requests at a time per project" of its folders listed together; one project's walk is one part with the same
            Parallelism = RequestsPerProject,
            MaxInFlight = Scope == ScopeKind.Project ? RequestsPerProject : Requests,
            PartsBelow = Scope == ScopeKind.Project ? null : HoldsProjectsAsync,
            PartsAtOnce = ProjectsAtATime,
            ListTree = ListTreeAsync,
            StampOf = StampAsync,

            // a folder that failed answers so until the next walk asks it again, as the other sources' do: it was asked
            // again, with its tries and their waits, at every visit and every pass of a search
            KeepFailures = true,
            Name = "gitlab",

            // the listings last run saw open at once at the next start: for the same server, account and scope, the same
            // settings that shape the tree, the same files (a file's size is a fact about how it is written), and what a
            // repository's entries keep in the same form (they are what a file is found and read by)
            SaveTo = Path.Combine(Context.StateDirectory, "listings.json.gz"),
            SaveKey = $"{_session.Address.Server}|{_session.User?.Username}|{path}|{_settings.Shape}|md{GitLabDocuments.Format}|keys{GitLabKeys.Format}",
        });

        // a sign-in renewed: what it refused may be answered now
        _session.Renewed += _kept.Cache.ForgetFailures;
        _kept.StartWalksOfHeld();
        Logger.LogInformation("gitlab: serving {Scope} {Path} on {Server} as {User}",
            Scope.ToString().ToLowerInvariant(), path, _session.Address.Server, _session.User?.Username);
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

        using var opening = _ticketsAhead.ForOneThing();
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

        foreach (var child in await ListedAsync(folder, VfsPath.Normalize(path), ct).ConfigureAwait(false))
        {
            yield return child.Entry;
        }
    }

    /// <summary>How many times a folder's children were all made only to find one of them (<see cref="FoundByListingAsync"/>), for tests: a walk does it for none.</summary>
    internal long ListingsToFind => Interlocked.Read(ref _listingsToFind);

    private long _listingsToFind;

    /// <summary>One of a folder's children by its name, in either case, from the folder's listing made for it: how a folder that cannot find one alone does (<see cref="GlFolder.FindAsync"/>).</summary>
    internal async Task<GlNode?> FoundByListingAsync(GlFolder folder, string path, string name, CancellationToken ct)
    {
        Interlocked.Increment(ref _listingsToFind);
        return Child(await ListedAsync(folder, path, ct).ConfigureAwait(false), name);
    }

    /// <summary>A folder's children, through the caches below, remembered as what the folder last listed.</summary>
    private async Task<IReadOnlyList<GlNode>> ListedAsync(GlFolder folder, string path, CancellationToken ct)
    {
        var children = await Guard(() => folder.ListAsync(this, ct)).ConfigureAwait(false);
        if (folder is FilesFolder or TreeFolder)
        {
            // inside a repository the listings kept are what is gone on from (KeptChild)
            return children;
        }

        // on the way down to a project, for every call; anywhere else for the walks, whoever listed it: a walk that waited
        // for a listing a person had asked for goes on from that one
        var (remembered, most) = _settings.Keeps && folder.FoundFromLastListing ? (_lastListed, MostFoldersRemembered) : (_walked, MostWalkedRemembered);
        if (FoldersListed.Of(children) is not { } folders)
        {
            remembered.TryRemove(path, out _);
        }
        else
        {
            if (remembered.Count >= most)
            {
                remembered.Clear();
            }

            remembered[path] = folders;
        }

        return children;
    }

    /// <summary>
    /// The folders a folder last listed, found by name (<see cref="ResolveAsync"/>): one of a group's thousands of
    /// projects without going through them all, at every folder below it a walk comes to. The files are not held: an
    /// issue's is its whole text.
    /// </summary>
    private sealed class FoldersListed
    {
        private readonly GlFolder[] _folders;
        private Dictionary<string, GlFolder>? _byName;

        private FoldersListed(GlFolder[] folders) => _folders = folders;

        /// <summary>Null where the folder lists no folder.</summary>
        public static FoldersListed? Of(IReadOnlyList<GlNode> children)
        {
            var folders = children.OfType<GlFolder>().ToArray();
            return folders.Length == 0 ? null : new FoldersListed(folders);
        }

        public GlNode? Find(string name)
        {
            if (_folders.Length <= 8)
            {
                return Child(_folders, name);
            }

            // made by the first to look, and by two at once the same
            var byName = _byName;
            if (byName is null)
            {
                byName = new Dictionary<string, GlFolder>(_folders.Length, StringComparer.OrdinalIgnoreCase);
                foreach (var folder in _folders)
                {
                    byName.TryAdd(folder.Name, folder);
                }

                _byName = byName;
            }

            // a name as it was listed is found at once; another (an issue's with the title it had) as a listing finds it
            return byName.TryGetValue(name, out var found) ? found : Child(_folders, name);
        }
    }

    public override async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        if (mode.IsWrite())
        {
            throw VfsException.NotSupported("writing to GitLab");
        }

        using var opening = _ticketsAhead.ForOneThing();
        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        return node is GlFile file ? await Guard(() => file.OpenAsync(this, ct)).ConfigureAwait(false) : throw VfsException.IsADirectory(path);
    }

    /// <summary>
    /// The node at <paramref name="path"/>, found a listing at a time from the root: with listings kept, on the way down to a
    /// project, from what each folder last listed where that names it, so that a folder deep in a big group does not ask for
    /// the group's thousands of projects again each time the refresh interval has passed; inside a repository, from the
    /// listings kept of its folders (<see cref="KeptChild"/>); else through the caches. A listing that took longer to read
    /// than the interval was stale as it arrived, and a walk through such a group hardly moved.
    /// <para>
    /// For a walk, any folder is found from what the folder it is in last listed (<see cref="FoldersListed"/>). A walk lists
    /// a folder and then comes to each folder in it several times (its stamp, its tree, its listing, its parts), and each
    /// time every folder on the way was listed again to find the next: an issue's folder by naming every issue of the
    /// project and making a folder for each, so a project of N issues named N issues 3 N times (the review of 21-Nov-2026,
    /// P5: 12.7 GiB allocated for 2,000 issues, 3.3 for 1,000). The folder the walk is in was listed by it, or is kept by a
    /// stamp that says nothing below it has changed, so what it last listed is what the walk is to go on from. A person's
    /// call still goes through the caches, as fresh as the refresh interval.
    /// </para>
    /// </summary>
    internal async Task<GlNode> ResolveAsync(string path, CancellationToken ct)
    {
        GlNode node = _root ?? throw VfsException.Unavailable("GitLab is not signed in to yet");
        var normalized = VfsPath.Normalize(path);
        if (normalized.Length == 0)
        {
            return node;
        }

        var at = "";
        foreach (var segment in VfsPath.Split(normalized))
        {
            if (node is not GlFolder folder)
            {
                throw VfsException.NotADirectory(path);
            }

            GlNode? known = null;
            if (_settings.Keeps)
            {
                known = _lastListed.TryGetValue(at, out var last) ? last.Find(segment) : KeptChild(folder, at, segment);
            }

            if (known is null && Walks.Current && _walked.TryGetValue(at, out var walked))
            {
                known = walked.Find(segment);
            }

            node = known ?? await folder.FindAsync(this, at, segment, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
            at = VfsPath.Join(at, segment);
            if (node is ProjectFolder project)
            {
                IsRoot(project.Project, VfsPath.Join(at, "files"));
            }
        }

        return node;
    }

    /// <summary>
    /// A repository folder's child from the listing kept of the folder, nothing asked (spec 7.8): a repository's entries carry
    /// what finding and reading them takes (<see cref="GitLabKeys"/>), and their commit is the one the repository's listings
    /// were made at, kept as its root's stamp, so a file opens as its folder shows it. Null outside a repository, where its
    /// folder's listing is not kept, or where the listing does not name the child.
    /// </summary>
    private GlNode? KeptChild(GlFolder folder, string path, string name)
    {
        (GlProject Project, string? Commit, string RepoPath)? at = folder switch
        {
            FilesFolder files => (files.Project, _kept?.Cache.StampHeld(path), ""),
            TreeFolder { RepoPath.Length: 0 } root => (root.Project, _kept?.Cache.StampHeld(path), ""),
            TreeFolder tree => (tree.Project, tree.Commit, tree.RepoPath),
            _ => null,
        };
        if (at is not { Commit: { Length: > 0 } commit } repository || commit == NoRepository || _kept?.Cache.Held(path) is not { } listing)
        {
            return null;
        }

        foreach (var entry in listing)
        {
            if (entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return TreeFolder.FromKept(repository.Project, commit, repository.RepoPath, entry);
            }
        }

        return null;
    }

    // a project's files folder's stamp where it has no repository, or nothing in it
    private const string NoRepository = "none";

    /// <summary>The one of a folder's children with the name, in either case; an issue's also by its number, with a title it had.</summary>
    private static GlNode? Child(IReadOnlyList<GlNode> children, string name)
        => children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? children.OfType<TicketFolder>().FirstOrDefault(t => TicketFolder.SameTicket(t.Name, name));

    /// <summary>
    /// "Projects at a time" (1 to 64): how many projects a server's or a group's walk goes through at once, each a part of its own
    /// (the user, 5-Nov-2026, a server of 3,500 projects counting slowly at 32 requests: "how many projects to do simultaneously").
    /// Before, the walkers took the projects off one stack, so a walker stayed with a project's folders one after another and the
    /// requests at a time went half unused.
    /// </summary>
    private int ProjectsAtATime => Math.Clamp(_settings.ProjectsAtATime, 1, 64);

    /// <summary>"Requests at a time per project" (1 to 64): folders listed at once within one project's part, and the whole of a source of one project.</summary>
    private int RequestsPerProject => Math.Clamp(_settings.RequestsPerProject, 1, 64);

    /// <summary>What the API client has open with the server at most: the projects at a time times the requests at a time per project.</summary>
    private int Requests => ProjectsAtATime * RequestsPerProject;

    /// <summary>How many range listings of the projects go at once (<see cref="ProjectsInRangesAsync"/>): as many as projects at a time.</summary>
    private int Walkers => ProjectsAtATime;

    /// <summary>Whether what a folder holds is walked as parts of their own: the server's root, a namespace on the way to projects and a group hold projects, and groups that hold more.</summary>
    private async ValueTask<bool> HoldsProjectsAsync(string folder, CancellationToken ct)
        => await ResolveAsync(folder, ct).ConfigureAwait(false) is ServerRoot or NamespaceFolder or GroupFolder;

    // --- what the folders ask for --------------------------------------------------------------------------------------

    /// <summary>
    /// What the whole server shows: every group and project the account can see, as GitLab's Explore page lists them
    /// (those archived or marked for deletion too, which GitLab leaves out only when asked to); or only what it is a member
    /// of (<see cref="GitLabSettings.ShowsEveryProject"/>). Asked again every ten minutes at most.
    /// </summary>
    internal Task<ServerListing> ServerListingAsync(CancellationToken ct)
        => Cache.GetAsync("server", Refresh < TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : Refresh, async c =>
        {
            var every = _settings.ShowsEveryProject;
            var groups = new List<GlGroup>();
            await foreach (var g in Api.PagesAsync($"groups?{(every ? "all_available=true" : "min_access_level=10")}&per_page=100&order_by=id&sort=asc", c).ConfigureAwait(false))
            {
                groups.Add(GlGroup.Parse(g));
            }

            var projects = await EveryProjectAsync(c).ConfigureAwait(false);
            Logger.LogInformation("gitlab: {User} {Sees} {Groups:N0} groups and {Projects:N0} projects", Session.User?.Username, every ? "can see" : "is a member of", groups.Count, projects.Count);
            return new ServerListing(groups, projects);
        }, ct);

    /// <summary>
    /// Every project the account can see, in the order of their ids. Each page of GitLab's listing says where the next begins,
    /// so its pages can only be asked one after another, and everything else on the server waits for this listing. Past the
    /// first page, which is all a small server needs, the rest is asked in ranges of ids side by side, as many at once as a walk
    /// may ask.
    /// </summary>
    private async Task<List<GlProject>> EveryProjectAsync(CancellationToken ct)
    {
        var query = $"{_settings.ProjectsQuery}&per_page={ProjectsPage}&order_by=id&sort=asc&pagination=keyset";
        var projects = new List<GlProject>();
        await using var pages = Api.PagesAsync(query, ct).GetAsyncEnumerator(ct);
        while (projects.Count < ProjectsPage && await pages.MoveNextAsync().ConfigureAwait(false))
        {
            projects.Add(GlProject.Parse(pages.Current));
        }

        if (projects.Count == ProjectsPage && Walkers > 1 && await ProjectsInRangesAsync(query, projects[^1].Id, ct).ConfigureAwait(false) is { } rest)
        {
            projects.AddRange(rest);
            return projects;
        }

        // a page at a time from where it got to
        while (await pages.MoveNextAsync().ConfigureAwait(false))
        {
            projects.Add(GlProject.Parse(pages.Current));
        }

        return projects;
    }

    /// <summary>
    /// The projects past an id, from up to the highest there is: that span cut in <see cref="RangesPerWalker"/> times as many
    /// ranges as walkers, so that a range of many projects does not leave the others idle, and each walked to its end. Null
    /// where the server does not keep to the ranges (<c>id_after</c>, <c>id_before</c>): no project is then counted twice.
    /// </summary>
    private async Task<List<GlProject>?> ProjectsInRangesAsync(string query, long after, CancellationToken ct)
    {
        long highest = 0;
        await foreach (var project in Api.PagesAsync($"{_settings.ProjectsQuery}&per_page=1&order_by=id&sort=desc&pagination=keyset", ct).ConfigureAwait(false))
        {
            highest = project.Long("id");
            break;
        }

        if (highest <= after)
        {
            return [];
        }

        var ranges = (int)Math.Min(Walkers * RangesPerWalker, highest - after);
        var found = new List<GlProject>[ranges];
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, ranges), new ParallelOptions { MaxDegreeOfParallelism = Walkers, CancellationToken = ct }, async (i, c) =>
            {
                var from = after + ((highest - after) * i / ranges);
                var to = after + ((highest - after) * (i + 1) / ranges);

                // the last is open at its end: a project made since the highest was asked for is in it
                var last = i == ranges - 1;
                var projects = new List<GlProject>();
                await foreach (var e in Api.PagesAsync($"{query}&id_after={from}{(last ? "" : $"&id_before={to + 1}")}", c).ConfigureAwait(false))
                {
                    var project = GlProject.Parse(e);
                    if (project.Id <= from || (!last && project.Id > to))
                    {
                        throw new RangeNotKeptException();
                    }

                    projects.Add(project);
                }

                found[i] = projects;
            }).ConfigureAwait(false);
        }
        catch (RangeNotKeptException)
        {
            Logger.LogInformation("gitlab: {Host} does not keep to a range of project ids; they are listed a page at a time", Api.Address.Server.Host);
            return null;
        }

        return found.SelectMany(f => f).ToList();
    }

    private sealed class RangeNotKeptException : Exception;

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

    /// <summary>
    /// A project's default branch and features: with listings kept, as the listing it was found in told them, where that told
    /// them all (a group's does), so a walk of thousands of projects need not ask for each. The next walk lists that folder again.
    /// </summary>
    internal Task<GlProject> ProjectAsync(GlProject listed, CancellationToken ct)
        => listed.Whole && _settings.Keeps ? Task.FromResult(listed) : ProjectAsync(listed.Id, ct);

    /// <summary>A project as it stands now (its default branch, its features), asked again once the refresh interval has passed.</summary>
    private Task<GlProject> ProjectAsync(long id, CancellationToken ct)
        => Cache.GetAsync($"project:{id}", Refresh, async c =>
        {
            using var doc = await Api.GetAsync($"projects/{id}", c).ConfigureAwait(false);
            return GlProject.Parse(doc.RootElement);
        }, ct);

    /// <summary>The default branch's head (its commit and when it was made), or null for a project with no commits.</summary>
    internal async Task<GlRef?> HeadAsync(GlProject project, CancellationToken ct)
    {
        var current = await ProjectAsync(project, ct).ConfigureAwait(false);
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

    /// <summary>
    /// A repository folder at a commit: the tree's entries, each file with its size (one GraphQL query per hundred files), asked
    /// once however many ask at once and kept the refresh interval; with listings kept, what holds it from then on is the
    /// folder's listing (<see cref="KeptChild"/>).
    /// </summary>
    internal Task<List<TreeItem>> TreeAsync(GlProject project, string commit, string path, CancellationToken ct)
        => Cache.GetAsync($"tree:{TreeKey(project, commit, path)}", Refresh, async c =>
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
    /// file's is its object's, <c>rawSize</c>); the files GraphQL did not give, one HEAD request each (X-Gitlab-Size), a
    /// project's requests at a time. A server whose first size query is refused has no GraphQL for this (an old one, or one
    /// that keeps it off) and is not asked again this run. A query that fails after one has been answered (a time limit of
    /// GitLab's own) costs its own hundred files a HEAD each and says nothing of the next: until 22-Nov-2026 it turned
    /// GraphQL off for the run, and every repository read after it cost a request a file, one after another.
    /// </summary>
    private async Task<Dictionary<string, (long Size, bool Lfs)>> SizesAsync(GlProject project, string commit, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var sizes = new ConcurrentDictionary<string, (long Size, bool Lfs)>(StringComparer.Ordinal);
        var each = new ParallelOptions { MaxDegreeOfParallelism = RequestsPerProject, CancellationToken = ct };
        if (!_graphQlRefused && paths.Count > 0)
        {
            // GraphQL size queries (a hundred files each) for one folder or one whole tree: a project's requests at a time at once
            await Parallel.ForEachAsync(paths.Chunk(100), each, async (batch, c) =>
            {
                if (_graphQlRefused)
                {
                    return;
                }

                try
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

                    _graphQlAnswered = true;
                }
                catch (VfsException ex) when (ex.Error is VfsError.IoError or VfsError.NotFound or VfsError.AccessDenied || IsTooMuch(ex))
                {
                    if (_graphQlAnswered)
                    {
                        Logger.LogInformation("gitlab: a size query for {Count:N0} files of {Project} failed ({Error}); asking for each of those files' instead", batch.Length, project.FullPath, ex.Message);
                    }
                    else if (!_graphQlRefused)
                    {
                        _graphQlRefused = true;
                        Logger.LogInformation("gitlab: GraphQL does not give the files' sizes ({Error}); asking for each file's instead", ex.Message);
                    }
                }
            }).ConfigureAwait(false);
        }

        await Parallel.ForEachAsync(paths.Where(p => !sizes.ContainsKey(p)).ToList(), each, async (path, c) =>
        {
            var headers = await Api.HeadAsync($"projects/{project.Id}/repository/files/{Uri.EscapeDataString(path)}?ref={Uri.EscapeDataString(commit)}", c).ConfigureAwait(false);
            sizes[path] = (headers.TryGetValue("X-Gitlab-Size", out var size) && long.TryParse(size, out var n) ? n : 0, false);
        }).ConfigureAwait(false);

        return new Dictionary<string, (long Size, bool Lfs)>(sizes, StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a query failed as one too heavy for the server does: refused as too many, or given up by what stands in
    /// front of GitLab (502, 503, 504). The sizes are asked a file at a time then, which a server that is really down
    /// refuses in its turn.
    /// </summary>
    private static bool IsTooMuch(VfsException ex)
        => RequestCore.StatusOf(ex) is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout;

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
    /// and keeps the listings, so they open at once afterwards: each repository whole, a hundred entries a request
    /// (<see cref="ListTreeAsync"/>), on a project's default branch and, since 6-Nov-2026, at each of its branches and tags;
    /// with listings kept, a walk again every "Walk every folder every" minutes. It goes into every folder, a branch's and
    /// a tag's among them, so nothing is left out and the count is exact unless a folder could not be listed. A server's
    /// or a group's count says how far it has got through the list of its projects; the walk comes to each in turn, and
    /// all that is below one before the next, so that is how far through the walk it is too.
    /// </summary>
    public async Task CountFilesAsync(CancellationToken ct)
    {
        if (Scope != ScopeKind.Project)
        {
            Context.ReportProgress("Counting files: listing the projects");
            Volatile.Write(ref _counted, new ProjectsCounted(Context, await ProjectsToCountAsync(ct).ConfigureAwait(false)));
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

    /// <summary>How many projects a count goes through: what the server lists, the walk's first step in any case, or what GitLab says a group has. Null where it says nothing: past 10,000, or a listing that failed, which the walk meets for itself.</summary>
    private async Task<long?> ProjectsToCountAsync(CancellationToken ct)
    {
        try
        {
            if (_root is not GroupFolder { Group: var group })
            {
                return (await ServerListingAsync(ct).ConfigureAwait(false)).Projects.Count;
            }

            long? total = null;
            await foreach (var _ in Api.PagesAsync($"groups/{group.Id}/projects?include_subgroups=true&with_shared=false&simple=true&per_page=1", ct, t => total = t).ConfigureAwait(false))
            {
                break;
            }

            return total;
        }
        catch (VfsException ex)
        {
            Logger.LogDebug("gitlab: how many projects there are to count is not known: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// How far a count has got through the projects: each said once as the walk comes to it (or someone opens it first), and
    /// how much of the list is behind it. Projects come to at once ("Projects at a time") are said one at a time, so that
    /// what is said keeps the order of the count: counted and said apart, a smaller number could be said after a larger one.
    /// While the server has asked AnyFS to wait (<see cref="GitLabApi.Held"/>), the count says so too, with the server's reason,
    /// so a tile that stands still says why.
    /// </summary>
    private sealed class ProjectsCounted(ISourceContext context, long? total)
    {
        private readonly HashSet<long> _reached = [];
        private string? _said;
        private double? _fraction;
        private DateTime _heldUntil;
        private string _why = "";

        public long? Total => total;

        public void Reached(GlProject project)
        {
            lock (_reached)
            {
                if (!_reached.Add(project.Id))
                {
                    return;
                }

                long count = _reached.Count;
                if (total is > 0 and var known)
                {
                    // the one it has come to is not behind it yet: the bar is full only as the count ends
                    _said = $"Counting files: {Math.Min(count, known):N0} of {known:N0} projects";
                    _fraction = (double)Math.Min(count - 1, known - 1) / known;
                }
                else
                {
                    _said = $"Counting files: {count:N0} projects so far";
                    _fraction = null;
                }

                Say();
            }
        }

        /// <summary>The server asked AnyFS to wait: said with how far the count has got, until the wait is over, when the count is said plainly again.</summary>
        public void Waiting(TimeSpan wait, string why)
        {
            lock (_reached)
            {
                _heldUntil = DateTime.UtcNow + wait;
                _why = why;
                if (_said is not null)
                {
                    Say();
                }
            }

            _ = Task.Delay(wait + TimeSpan.FromMilliseconds(50)).ContinueWith(_ =>
            {
                lock (_reached)
                {
                    if (!_over && _said is not null && DateTime.UtcNow >= _heldUntil)
                    {
                        Say();
                    }
                }
            }, TaskScheduler.Default);
        }

        /// <summary>The count is over: nothing more is said of it.</summary>
        public void Over()
        {
            lock (_reached)
            {
                _over = true;
            }
        }

        private bool _over;

        private void Say()
        {
            var waiting = DateTime.UtcNow < _heldUntil ? $", waiting {Math.Ceiling((_heldUntil - DateTime.UtcNow).TotalSeconds):N0} s as GitLab asks ({_why})" : "";
            context.ReportProgress(_said + waiting, _fraction);
        }
    }

    /// <summary>The listings as kept, for tests.</summary>
    internal ListingCache Listings => Kept.Cache;

    /// <summary>One walk now (a test's: the walks after the count come by the clock), stamps kept unless <paramref name="full"/>.</summary>
    internal Task<bool> WalkAsync(SourceCounters counters, bool full, CancellationToken ct) => Kept.WalkAsync(counters, full, ct);

    /// <summary>
    /// A folder's stamp for the walks (<see cref="KeptListingsOptions.StampOf"/>): a project's folder has its last activity
    /// as GitLab tells it (moved by a push, an issue, a comment, a pipeline, with an hour's grain) and its default branch's
    /// head, so a project nothing happened in is kept whole; the repository's own folders (<c>files</c>, and each branch's or
    /// tag's) their commit, so a project with an issue's comment and no push keeps its trees; an issue's or merge request's
    /// folder its last update, a pipeline's its last update, so a changed project costs only what changed in it; any other
    /// folder none, being a request or two to list. The project's parent listing and its head are the calls the stamps share,
    /// each kept a refresh interval.
    /// </summary>
    internal async ValueTask<string?> StampAsync(string path, string? held, CancellationToken ct)
    {
        switch (await ResolveAsync(path, ct).ConfigureAwait(false))
        {
            case ProjectFolder project:
                Volatile.Read(ref _counted)?.Reached(project.Project);

                // the root of a source of one project is the project as it was read at the start, and nothing lists it
                // again: its last activity is asked for as it stands (the same answer its head is found by). Taken from
                // the start's, no walk but the day's whole one saw a comment, a new issue or a pipeline
                var now = ReferenceEquals(project, _root) ? await ProjectAsync(project.Project, ct).ConfigureAwait(false) : project.Project;
                var head = now.Repository ? await HeadAsync(project.Project, ct).ConfigureAwait(false) : null;
                return $"{now.LastActivity?.UtcTicks ?? 0}|{head?.Commit}";
            case FilesFolder files:
                var filesAt = files.Project.Repository && await HeadAsync(files.Project, ct).ConfigureAwait(false) is { } at ? at.Commit : NoRepository;
                IsRoot(files.Project, path, filesAt);
                return filesAt;
            case TreeFolder { RepoPath.Length: 0 } tree:
                IsRoot(tree.Project, path, tree.Commit);
                return tree.Commit;
            case TicketFolder ticket:
                return (ticket.Ticket.Updated?.UtcTicks ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case PipelineFolder pipeline:
                return $"{pipeline.Pipeline.Updated?.UtcTicks ?? 0}|{pipeline.Pipeline.Status}";
            default:
                return null;
        }
    }


    /// <summary>
    /// For a walk: a repository whole, every folder's listing at a commit by path: a project's <c>files</c> (its default branch
    /// at its head) or a branch's or tag's folder (at its commit); null for any other folder, which the walk lists alone. The
    /// same repository at the same commit kept under another folder (a branch at the default branch's head, a tag at a
    /// branch's) is copied from there (<see cref="KeptElsewhere"/>), else read whole (<see cref="FillTreeAsync"/>), once.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>?> ListTreeAsync(string path, CancellationToken ct)
    {
        TreeFolder root;
        switch (await ResolveAsync(path, ct).ConfigureAwait(false))
        {
            case FilesFolder files:
                if (await Guard(() => HeadAsync(files.Project, ct)).ConfigureAwait(false) is not { } head)
                {
                    return new Dictionary<string, IReadOnlyList<VfsEntry>>(StringComparer.Ordinal) { [path] = [] };
                }

                root = files.At(head);
                break;
            case TreeFolder { RepoPath.Length: 0 } tree:
                root = tree;
                break;
            default:
                return null;
        }

        LetGoOfReadsKept();
        var listings = KeptElsewhere(root, path);
        if (listings is null)
        {
            var folders = await Guard(() => FillTreeAsync(root.Project, root.Commit, ct)).ConfigureAwait(false);
            if (_settings.Keeps)
            {
                _readFor[(root.Project.Id, root.Commit)] = VfsPath.Normalize(path);
            }
            listings = new Dictionary<string, IReadOnlyList<VfsEntry>>(folders.Count, StringComparer.Ordinal);
            var pending = new Queue<(string Repo, string Path)>();
            pending.Enqueue(("", path));
            while (pending.TryDequeue(out var next))
            {
                var children = TreeFolder.Children(root.Project, root.Commit, next.Repo, folders.GetValueOrDefault(next.Repo) ?? [], root.Modified);
                listings[next.Path] = children.Select(c => c.Entry).ToList();
                foreach (var child in children)
                {
                    switch (child)
                    {
                        case TreeFolder folder:
                            pending.Enqueue((folder.RepoPath, VfsPath.Join(next.Path, folder.Name)));
                            break;
                        case EmptyFolder submodule:
                            listings[VfsPath.Join(next.Path, submodule.Name)] = [];
                            break;
                    }
                }
            }
        }

        IsRoot(root.Project, path, root.Commit);
        return listings;
    }

    /// <summary>
    /// The repository at the root's commit as kept under another folder, every listing of it moved under <paramref name="path"/>:
    /// a repository's entries say nothing of where it is shown (<see cref="GitLabKeys"/>), so they are the same under any ref at
    /// that commit. Null where none is kept whole at that commit. Until 16-Nov-2026 the refs shared a copy the source held of
    /// every repository folder read in the run, in memory and bounded at a million folders.
    /// </summary>
    private Dictionary<string, IReadOnlyList<VfsEntry>>? KeptElsewhere(TreeFolder root, string path)
    {
        if (KeptRoot(root.Project.Id, root.Commit, except: VfsPath.Normalize(path)) is not { } other)
        {
            return null;
        }

        var held = Kept.Cache.Under(other);
        if (!held.ContainsKey(other))
        {
            return null;
        }

        var copied = new Dictionary<string, IReadOnlyList<VfsEntry>>(held.Count, StringComparer.Ordinal);
        foreach (var (at, entries) in held)
        {
            // whole: a folder whose listing is not kept (let go since) is read again with the rest
            foreach (var entry in entries)
            {
                if (entry.IsDirectory && !held.ContainsKey(VfsPath.Join(at, entry.Name)))
                {
                    return null;
                }
            }

            copied[path + at[other.Length..]] = entries;
        }

        return copied;
    }

    /// <summary>
    /// Notes a folder that is a repository's root (a project's <c>files</c>, a branch's or a tag's folder), with the commit
    /// it stands at where the caller knows it: one a ref at a commit it is kept at is copied from (<see cref="KeptRoot"/>).
    /// </summary>
    private void IsRoot(GlProject project, string path, string? commit = null)
    {
        if (!_settings.Keeps)
        {
            return;
        }

        // As many as the branches and tags of every project a walk has come to, a path and a commit each, were noted for
        // the run (the review of 21-Nov-2026, P6). Past a number they start again: a walk notes each root as it comes
        // to it, and a ref listed before its fellows are noted again is read from GitLab where it might have been copied
        if (Volatile.Read(ref _rootsNoted) >= MostRootsNoted)
        {
            _roots.Clear();
            Interlocked.Exchange(ref _rootsNoted, 0);
        }

        var roots = _roots.GetOrAdd(project.Id, _ => new ConcurrentDictionary<string, string?>(StringComparer.Ordinal));
        path = VfsPath.Normalize(path);
        if (commit is null)
        {
            // noted on the way down to its project: the commit it was last seen at, if any, stands
            if (roots.TryAdd(path, null))
            {
                Interlocked.Increment(ref _rootsNoted);
            }
        }
        else
        {
            var added = false;
            roots.AddOrUpdate(path, _ =>
            {
                added = true;
                return commit;
            }, (_, _) => commit);
            if (added)
            {
                Interlocked.Increment(ref _rootsNoted);
            }
        }
    }

    /// <summary>The repository roots noted at most, over every project; past that they start again (lower in a test).</summary>
    internal int MostRootsNoted { get; set; } = 200_000;

    /// <summary>How many repository roots are noted, about (for tests).</summary>
    internal int RootsNoted => Volatile.Read(ref _rootsNoted);

    private int _rootsNoted;

    /// <summary>
    /// A root of the project's repository (<see cref="IsRoot"/>) whose listings are kept at <paramref name="commit"/>,
    /// other than <paramref name="except"/>; null where there is none. The stamp held is asked only of a root last seen
    /// at that commit, or seen at none yet (a project's <c>files</c> before the walk has come to it, which an earlier
    /// run may have kept). It was asked of every root of the project, as many as its branches and tags, at each listing
    /// of one: a first count of a project of 300 read 45,000 stamps (the review of 21-Nov-2026, N21).
    /// </summary>
    private string? KeptRoot(long project, string commit, string except)
    {
        if (!_settings.Keeps || _kept is null || !_roots.TryGetValue(project, out var roots))
        {
            return null;
        }

        foreach (var (root, seenAt) in roots)
        {
            if (root == except || (seenAt is not null && seenAt != commit))
            {
                continue;
            }

            Interlocked.Increment(ref _rootStampsRead);
            if (_kept.Cache.StampHeld(root) == commit)
            {
                return root;
            }
        }

        return null;
    }

    /// <summary>How many times the stamp held of a repository's root was asked for, to find one kept at a commit (<see cref="KeptRoot"/>), for tests.</summary>
    internal long RootStampsRead => Interlocked.Read(ref _rootStampsRead);

    private long _rootStampsRead;

    // the folders that are a repository's root, by the project, each with the commit it was last seen at (null: at none
    // yet): as many as the project's refs (KeptRoot)
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, string?>> _roots = new();

    // a repository's whole tree being read, or read and not yet seen kept (FillTreeAsync), by the project and the commit
    private readonly ConcurrentDictionary<(long Project, string Commit), Lazy<Task<Dictionary<string, List<TreeItem>>>>> _filling = new();

    // the folder a repository read whole was read for, until it is seen kept there (LetGoOfReadsKept)
    private readonly ConcurrentDictionary<(long Project, string Commit), string> _readFor = new();

    /// <summary>
    /// Every folder of the repository at a commit, from its recursive tree, a hundred entries a request, and its files' sizes a
    /// hundred at a time, where folder by folder costs a request or two a folder; by the folder's path in the repository. Read
    /// once however many ask: a branch and a tag at the same commit walked together read it one time. With listings kept, a
    /// read is held until the walk has kept its listings (<see cref="LetGoOfReadsKept"/>), from when a ref at that commit
    /// copies them (<see cref="KeptElsewhere"/>): let go of at once, a ref that came to it just after the read, before the walk
    /// had kept what it read, read it all again.
    /// </summary>
    private async Task<Dictionary<string, List<TreeItem>>> FillTreeAsync(GlProject project, string commit, CancellationToken ct)
    {
        // the dictionary may make a value for each asker at once, and only the one it keeps may start the read: a task started
        // in the making ran for each, and the repository was read as many times
        var key = (project.Id, commit);
        var filling = _filling.GetOrAdd(key, _ => new Lazy<Task<Dictionary<string, List<TreeItem>>>>(() => Task.Run(() => FillTreeNowAsync(project, commit, _stopping.Token))));
        try
        {
            return await filling.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (filling.Value.IsFaulted || filling.Value.IsCanceled || (filling.Value.IsCompleted && !_settings.Keeps))
            {
                _filling.TryRemove(new KeyValuePair<(long, string), Lazy<Task<Dictionary<string, List<TreeItem>>>>>(key, filling));
            }
        }
    }

    /// <summary>Lets go of each repository read whose listings the walk has kept since (<see cref="FillTreeAsync"/>): a ref at its commit copies them from then on.</summary>
    private void LetGoOfReadsKept()
    {
        foreach (var (key, filling) in _filling)
        {
            if (filling.IsValueCreated && filling.Value.IsCompletedSuccessfully && _readFor.TryGetValue(key, out var path) && _kept?.Cache.StampHeld(path) == key.Commit)
            {
                _filling.TryRemove(new KeyValuePair<(long, string), Lazy<Task<Dictionary<string, List<TreeItem>>>>>(key, filling));
                _readFor.TryRemove(new KeyValuePair<(long, string), string>(key, path));
            }
        }
    }

    private async Task<Dictionary<string, List<TreeItem>>> FillTreeNowAsync(GlProject project, string commit, CancellationToken ct)
    {
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
        Logger.LogDebug("gitlab: read {Project} at {Commit} whole: {Folders:N0} folders, {Files:N0} files", project.FullPath, commit, folders.Count, blobs.Count);
        return folders.ToDictionary(f => f.Key, f => f.Value.Select(e => ItemOf(e, RepoPath(f.Key, e.Name), sizes)).ToList(), StringComparer.Ordinal);
    }

    // --- stopping ------------------------------------------------------------------------------------------------------

    public override async ValueTask DisposeAsync()
    {
        // the walks stop first, and what they kept is written down; then what they asked through goes. The other way round,
        // each repository a walk was reading failed as if it could not be read at once, and said so in the log at every stop
        if (_kept is not null)
        {
            await _kept.DisposeAsync().ConfigureAwait(false);
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
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

/// <summary>A group's subgroups and its own projects.</summary>
internal sealed record GroupContents(List<GlGroup> Subgroups, List<GlProject> Projects);
