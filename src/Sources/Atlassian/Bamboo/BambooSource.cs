using System.Runtime.CompilerServices;
using System.Text.Json;
using AngleSharp.Html.Parser;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Atlassian.Bamboo;

/// <summary>
/// A Bamboo Data Center server, project or plan (type <c>bamboo</c>, spec 7.12), read-only: a folder per project, a
/// folder per plan with its newest builds, each build as Markdown with a folder per job holding its log and the
/// artifacts it kept, and a plan's branches as plans of their own. Names are compared in either case, as Windows does.
/// Folder listings are kept (the SDK's <see cref="KeptListings"/>), and a change shows when a walk comes to it: a build
/// that has finished is kept as it was, with every folder below it, since nothing about it changes any more; a plan's
/// folder is listed again at each walk (one request, its newest builds). A folder whose listing failed stays so until the
/// next walk.
/// </summary>
public sealed class BambooSource : SourceBase, ICountable
{
    // the most entries one page of a Bamboo list is asked for
    private const int PageSize = 1000;

    // the most of an artifact's page that is read for the files it links
    private const int PageBytes = 4 << 20;

    private readonly BambooSettings _settings;
    private readonly AtlassianStandIn? _standIn;
    private readonly CancellationTokenSource _stopping = new();
    private AtlassianSession? _session;
    private AtlassianApi? _api;
    private RequestCache? _cache;
    private KeptListings? _kept;
    private SpoolCache? _spool;
    private BmFolder? _root;
    private PlansCounted? _counted;

    public BambooSource(BambooSettings settings)
        : this(settings, standIn: null)
    {
    }

    internal BambooSource(BambooSettings settings, AtlassianStandIn? standIn)
    {
        _settings = settings;
        _standIn = standIn;
    }

    public override SourceCapabilities Capabilities => new(CanWrite: false, CaseInsensitive: true);

    internal BambooSettings Settings => _settings;

    internal AtlassianApi Api => _api ?? throw VfsException.Unavailable("Bamboo is not signed in to yet");

    internal AtlassianSession Session => _session ?? throw new InvalidOperationException("the source has not started");

    /// <summary>What the address names, once started.</summary>
    internal BambooScope Scope { get; private set; } = BambooScope.Server;

    private RequestCache Cache => _cache ?? throw VfsException.Unavailable("Bamboo is not signed in to yet");

    private KeptListings Kept => _kept ?? throw VfsException.Unavailable("Bamboo is not signed in to yet");

    private SpoolCache Spool => _spool ?? throw VfsException.Unavailable("Bamboo is not signed in to yet");

    private TimeSpan Refresh => _settings.Refresh > TimeSpan.Zero ? _settings.Refresh : TimeSpan.FromSeconds(2);

    /// <summary>The installation's address (its base, with a slash at the end), for the documents' links and the files' addresses.</summary>
    internal Uri Installation => Session.Address.Base;

    protected override async Task OnInitialiseAsync(CancellationToken ct)
    {
        _session = new AtlassianSession(AtlassianProduct.Bamboo, _settings, Logger, _standIn, Context.Secrets, Context.UpstreamWatcher);
        await _session.SignInAsync(interactive: true, message => Context.ReportProgress(message), ct).ConfigureAwait(false);
        _api = new AtlassianApi(_session, _settings.Requests, Logger);
        _cache = new RequestCache(_stopping.Token);
        _spool = new SpoolCache(Path.Combine(Context.CacheDirectory, "spool"), logger: Logger);

        Scope = BambooScope.From(_session.Address, out var problem) ?? throw new SourceFailedException(problem!, permanent: true);
        Context.ReportProgress(Scope.Kind == BambooScopeKind.Server ? $"Opening {_session.Address.Base.Host}" : $"Opening the {Scope.Kind.ToString().ToLowerInvariant()} {Scope.Value}");
        try
        {
            _root = await RootAsync(Scope, ct).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            throw new SourceFailedException($"Opening the {Scope.Kind.ToString().ToLowerInvariant()} {Scope.Value}: {ex.Message}", permanent: ex.Error is VfsError.AccessDenied or VfsError.NotFound, ex);
        }

        // a server's or a project's walk goes through "Plans at a time" plans at once, each a part of its own with "Requests
        // at a time per plan" of its folders listed together; one plan's walk is one part with the same
        var perPlan = Math.Clamp(_settings.RequestsPerPlan, 1, 64);
        _kept = new KeptListings(ListFolderAsync, Counters, Logger, new KeptListingsOptions
        {
            Keep = _settings.Keeps,
            UnkeptFor = TimeSpan.Zero,
            WalkEvery = _settings.WalkEvery,
            Parallelism = perPlan,
            MaxInFlight = Scope.Kind == BambooScopeKind.Plan ? perPlan : _settings.Requests,
            PartsBelow = Scope.Kind == BambooScopeKind.Plan ? null : HoldsPlansAsync,
            PartsAtOnce = Math.Clamp(_settings.PlansAtATime, 1, 64),
            StampOf = StampAsync,
            KeepFailures = true,
            Name = "bamboo",

            // the listings last run saw open at once at the next start: for the same installation, account and scope, the
            // same settings that shape the tree, and the same documents (a file's size is a fact about how it is written)
            SaveTo = Path.Combine(Context.StateDirectory, "listings.json.gz"),
            SaveKey = $"{_session.Address.Server}|{_session.User?.Name}|{Scope.Kind}:{Scope.Value}|{_settings.Shape}|md{BambooDocuments.Format}",
        });

        _session.Renewed += _kept.Cache.ForgetFailures;
        _kept.StartWalksOfHeld();
        Logger.LogInformation("bamboo: serving {Scope} {Value} on {Server} as {User}", Scope.Kind.ToString().ToLowerInvariant(), Scope.Value, _session.Address.Server, _session.User?.Name);
    }

    /// <summary>The folder the scope is: the server's projects, a project's plans, or a plan.</summary>
    private async Task<BmFolder> RootAsync(BambooScope scope, CancellationToken ct)
    {
        switch (scope.Kind)
        {
            case BambooScopeKind.Project:
                return new BmProjectFolder(await ProjectAsync(scope.ProjectKey, ct).ConfigureAwait(false), "");
            case BambooScopeKind.Plan:
                return new BmPlanFolder(await PlanAsync(scope.PlanKey, ct).ConfigureAwait(false), "");
            default:
                return new BambooServerRoot();
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
            return (_root ?? throw VfsException.Unavailable("Bamboo is not signed in to yet")).Entry;
        }

        return await Kept.Cache.StatAsync(normalized, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false);
    }

    /// <summary>A folder's listing from Bamboo, through the caches below (what <see cref="KeptListings"/> keeps).</summary>
    private async IAsyncEnumerable<VfsEntry> ListFolderAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        if (node is not BmFolder folder)
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
            throw VfsException.NotSupported("writing to Bamboo");
        }

        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        return node is BmFile file ? await Guard(() => file.OpenAsync(this, ct)).ConfigureAwait(false) : throw VfsException.IsADirectory(path);
    }

    /// <summary>The node at <paramref name="path"/>, found a folder at a time from the root (a build by its number, not by listing them all).</summary>
    private async Task<BmNode> ResolveAsync(string path, CancellationToken ct)
    {
        BmNode node = _root ?? throw VfsException.Unavailable("Bamboo is not signed in to yet");
        var normalized = VfsPath.Normalize(path);
        if (normalized.Length == 0)
        {
            return node;
        }

        foreach (var segment in VfsPath.Split(normalized))
        {
            if (node is not BmFolder folder)
            {
                throw VfsException.NotADirectory(path);
            }

            node = await Guard(() => folder.FindAsync(this, segment, ct)).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
        }

        return node;
    }

    // --- what the folders ask for --------------------------------------------------------------------------------------

    /// <summary>
    /// Every element of a Bamboo list, page after page: the list object (<paramref name="collection"/>: <c>projects</c>,
    /// <c>plans</c>, <c>results</c>) holds its <c>size</c> (how many there are in all), <c>start-index</c> and
    /// <c>max-result</c>, and the elements under <paramref name="item"/>. At most <paramref name="atMost"/> of them.
    /// </summary>
    internal async IAsyncEnumerable<JsonElement> PagesAsync(string relative, string collection, string item, int? atMost, [EnumeratorCancellation] CancellationToken ct)
    {
        var start = 0L;
        var got = 0;
        while (true)
        {
            var want = atMost is { } most ? Math.Min(PageSize, most - got) : PageSize;
            if (want <= 0)
            {
                yield break;
            }

            // the results list takes max-results, the others max-result: both are sent, a server ignoring the one it does not know
            var separator = relative.Contains('?') ? "&" : "?";
            using var doc = await Api.GetAsync($"{relative}{separator}start-index={start}&max-result={want}&max-results={want}", ct).ConfigureAwait(false);
            var list = doc.RootElement.Obj(collection);
            var values = list.Arr(item).ToList();
            foreach (var value in values)
            {
                yield return value.Clone();
                if (atMost is { } max && ++got >= max)
                {
                    yield break;
                }
            }

            var total = list.Long("size");
            if (values.Count == 0 || total > 0 && start + values.Count >= total)
            {
                yield break;
            }

            start += values.Count;
        }
    }

    /// <summary>Every project the account can see, by name.</summary>
    internal Task<List<BmProject>> ProjectsAsync(CancellationToken ct)
        => Cache.GetAsync("projects", Refresh, async c =>
        {
            var projects = new List<BmProject>();
            await foreach (var e in PagesAsync("project?expand=projects.project", "projects", "project", null, c).ConfigureAwait(false))
            {
                projects.Add(BmProject.Parse(e));
            }

            projects = projects.Where(p => p.Key.Length > 0).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
            Logger.LogInformation("bamboo: {User} can see {Count:N0} projects", Session.User?.Name, projects.Count);
            return projects;
        }, ct);

    internal async Task<BmProject> ProjectAsync(string key, CancellationToken ct)
    {
        var box = await Cache.GetAsync($"project:{key}", Refresh, async c =>
        {
            using var doc = await Api.FindAsync(BambooScope.Path("project", key), c).ConfigureAwait(false)
                ?? throw new VfsException(VfsError.NotFound, $"Bamboo has no project {key} that {Session.User?.Name} can see");
            return new Box<BmProject>(BmProject.Parse(doc.RootElement));
        }, ct).ConfigureAwait(false);
        return box.Value;
    }

    /// <summary>A project's plans, by name.</summary>
    internal Task<List<BmPlan>> PlansAsync(string projectKey, CancellationToken ct)
        => Cache.GetAsync($"plans:{projectKey}", Refresh, async c =>
        {
            var plans = new List<BmPlan>();
            await foreach (var e in PagesAsync($"{BambooScope.Path("project", projectKey)}?expand=plans.plan", "plans", "plan", null, c).ConfigureAwait(false))
            {
                plans.Add(BmPlan.Parse(e));
            }

            return plans.Where(p => p.Key.Length > 0).OrderBy(p => p.ShortName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
        }, ct);

    internal async Task<BmPlan> PlanAsync(string key, CancellationToken ct)
    {
        var box = await Cache.GetAsync($"plan:{key}", Refresh, async c =>
        {
            using var doc = await Api.FindAsync(BambooScope.Path("plan", key), c).ConfigureAwait(false)
                ?? throw new VfsException(VfsError.NotFound, $"Bamboo has no plan {key} that {Session.User?.Name} can see");
            return new Box<BmPlan>(BmPlan.Parse(doc.RootElement));
        }, ct).ConfigureAwait(false);
        return box.Value;
    }

    /// <summary>A plan's branches, by name: each a plan of its own (<c>PROJ-PLAN12</c>), its short name the branch's.</summary>
    internal Task<List<BmPlan>> BranchesAsync(BmPlan plan, CancellationToken ct)
        => Cache.GetAsync($"branches:{plan.Key}", Refresh, async c =>
        {
            var branches = new List<BmPlan>();
            await foreach (var e in PagesAsync($"{BambooScope.Path("plan", plan.Key)}/branch", "branches", "branch", null, c).ConfigureAwait(false))
            {
                branches.Add(BmPlan.Parse(e) with { Type = "chain_branch", ProjectKey = plan.ProjectKey, ProjectName = plan.ProjectName });
            }

            return branches.Where(b => b.Key.Length > 0).OrderBy(b => b.ShortName, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Key, StringComparer.Ordinal).ToList();
        }, ct);

    /// <summary>A plan's newest builds (as many as "Builds kept"), newest first.</summary>
    internal Task<List<BmResult>> ResultsAsync(BmPlan plan, CancellationToken ct)
        => Cache.GetAsync($"results:{plan.Key}", Refresh, async c =>
        {
            var results = new List<BmResult>();
            await foreach (var e in PagesAsync($"{BambooScope.Path("result", plan.Key)}?expand=results.result", "results", "result", _settings.Builds, c).ConfigureAwait(false))
            {
                results.Add(BmResult.Parse(e));
            }

            return results.Where(r => r.Number > 0 && r.Key.Length > 0).OrderByDescending(r => r.Number).ToList();
        }, ct);

    /// <summary>One build by its number, as the list last gave it, else asked for alone; null where the plan has none by that number.</summary>
    internal async Task<BmResult?> ResultAsync(BmPlan plan, long number, CancellationToken ct)
    {
        if (Cache.Peek<List<BmResult>>($"results:{plan.Key}") is { } listed && listed.FirstOrDefault(r => r.Number == number) is { } known)
        {
            return known;
        }

        var key = $"{plan.Key}-{number}";
        var box = await Cache.GetAsync($"buildof:{key}", Refresh, async c => new Box<BmBuild?>(await FetchBuildAsync(key, c).ConfigureAwait(false)), ct).ConfigureAwait(false);
        return box.Value?.Result;
    }

    /// <summary>A build whole: its jobs with their artifacts and logs, its changes and issues. Kept for the run once it is final (nothing about it changes any more).</summary>
    internal async Task<BmBuild> BuildAsync(BmPlan plan, BmResult result, CancellationToken ct)
    {
        if (Cache.Peek<Box<BmBuild?>>($"buildof:{result.Key}") is { Value: { } found } && found.Result.LifeCycle == result.LifeCycle)
        {
            return found;
        }

        var box = await Cache.GetAsync($"build:{result.Key}@{result.LifeCycle}", result.Final ? null : Refresh, async c =>
            new Box<BmBuild>(await FetchBuildAsync(result.Key, c).ConfigureAwait(false) ?? throw new VfsException(VfsError.NotFound, $"Bamboo has no build {result.Key} of {plan.ShortName} any more")), ct).ConfigureAwait(false);
        return box.Value;
    }

    private async Task<BmBuild?> FetchBuildAsync(string key, CancellationToken ct)
    {
        using var doc = await Api.FindAsync($"{BambooScope.Path("result", key)}?expand=stages.stage.results.result.artifacts,stages.stage.results.result.logFiles,changes.change.files,jiraIssues", ct).ConfigureAwait(false);
        return doc is null ? null : BmBuild.Parse(doc.RootElement);
    }

    /// <summary>The size of a file Bamboo serves (a log, an artifact's file), by HEAD; 0 where it says none. Kept for the run.</summary>
    internal async Task<long> SizeAsync(Uri url, CancellationToken ct)
    {
        var box = await Cache.GetAsync($"size:{url.AbsoluteUri}", null, async c =>
        {
            using var response = await Api.HeadAsync(url, "a file's size", c).ConfigureAwait(false);
            return new Box<long>(response.Content.Headers.ContentLength ?? 0);
        }, ct).ConfigureAwait(false);
        return box.Value;
    }

    /// <summary>
    /// The files and folders an artifact's page links, below its own address (Bamboo shows a page for a folder of files);
    /// null where the address serves a file instead. Kept for the run: a build's artifacts do not change.
    /// </summary>
    internal async Task<List<BmArtifactEntry>?> ArtifactEntriesAsync(Uri url, CancellationToken ct)
    {
        var box = await Cache.GetAsync($"index:{url.AbsoluteUri}", null, async c =>
        {
            using var response = await Api.OpenAsync(url, "an artifact", c).ConfigureAwait(false);
            if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) != true)
            {
                return new Box<List<BmArtifactEntry>?>(null);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(c).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var buffer = new char[PageBytes];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(length), c).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            return new Box<List<BmArtifactEntry>?>(Linked(new string(buffer, 0, length), response.RequestMessage?.RequestUri ?? url));
        }, ct).ConfigureAwait(false);
        return box.Value;
    }

    /// <summary>What a page links below its own address: a folder for a link ending in a slash or going deeper, a file otherwise; each once.</summary>
    internal static List<BmArtifactEntry> Linked(string html, Uri page)
    {
        var folder = page.AbsoluteUri.EndsWith('/') ? page : new Uri(page.AbsoluteUri + "/");
        var document = new HtmlParser().ParseDocument(html);
        var entries = new Dictionary<string, BmArtifactEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href")?.Trim();
            if (string.IsNullOrEmpty(href) || href.StartsWith('#') || href.StartsWith('?') || !Uri.TryCreate(folder, href, out var target))
            {
                continue;
            }

            var below = target.GetLeftPart(UriPartial.Path);
            if (!below.StartsWith(folder.AbsoluteUri, StringComparison.OrdinalIgnoreCase) || below.Length == folder.AbsoluteUri.Length)
            {
                continue;
            }

            var rest = below[folder.AbsoluteUri.Length..];
            var slash = rest.IndexOf('/');
            var first = Uri.UnescapeDataString(slash < 0 ? rest : rest[..slash]);
            var name = AtlassianNames.Safe(first);
            if (name.Length == 0 || name is "." or "..")
            {
                continue;
            }

            var directory = slash >= 0;
            var url = directory ? new Uri(folder, Uri.EscapeDataString(first) + "/") : new Uri(folder, Uri.EscapeDataString(first));
            if (!entries.TryGetValue(name, out var had) || directory && !had.IsDirectory)
            {
                entries[name] = new BmArtifactEntry(name, directory, url);
            }
        }

        return entries.Values.ToList();
    }

    /// <summary>A file Bamboo serves, spooled as it arrives and kept by its address (a build's files do not change).</summary>
    internal Task<IVfsFile> OpenWebAsync(Uri url, long length, CancellationToken ct)
    {
        if (length == 0)
        {
            return Task.FromResult<IVfsFile>(new MemoryFile([]));
        }

        return Spool.OpenAsync($"bamboo:{url.AbsoluteUri}", length, async c => await ResponseStream.OpenAsync(await Api.OpenAsync(url, "a file", c).ConfigureAwait(false), c).ConfigureAwait(false), ct);
    }

    // --- counting and walking ------------------------------------------------------------------------------------------

    /// <summary>
    /// "Count files on start": the first walk of the tree (<see cref="KeptListings"/>), which lists every folder and keeps
    /// the listings, so they open at once afterwards; with listings kept, a walk again every "Walk every folder every"
    /// minutes. A server's or a project's count says how far it has got through its plans.
    /// </summary>
    public async Task CountFilesAsync(CancellationToken ct)
    {
        if (Scope.Kind != BambooScopeKind.Plan)
        {
            Context.ReportProgress("Counting files: listing the plans");
            Volatile.Write(ref _counted, new PlansCounted(Context, await PlansToCountAsync(ct).ConfigureAwait(false)));
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

    /// <summary>How many plans a count goes through: the project's, or every project's (a request a project, which the walk needs anyway). Null where that failed.</summary>
    private async Task<long?> PlansToCountAsync(CancellationToken ct)
    {
        try
        {
            if (_root is BmProjectFolder { Project: var project })
            {
                return (await PlansAsync(project.Key, ct).ConfigureAwait(false)).Count;
            }

            long total = 0;
            foreach (var p in await ProjectsAsync(ct).ConfigureAwait(false))
            {
                total += (await PlansAsync(p.Key, ct).ConfigureAwait(false)).Count;
            }

            return total;
        }
        catch (VfsException ex)
        {
            Logger.LogDebug("bamboo: how many plans there are to count is not known: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>How far a count has got through the plans: each said once as the walk comes to it, one at a time, so what is said keeps the order of the count.</summary>
    private sealed class PlansCounted(ISourceContext context, long? total)
    {
        private readonly HashSet<string> _reached = new(StringComparer.Ordinal);
        private bool _over;

        public void Reached(BmPlan plan)
        {
            lock (_reached)
            {
                if (_over || !_reached.Add(plan.Key))
                {
                    return;
                }

                long count = _reached.Count;
                if (total is > 0 and var known)
                {
                    context.ReportProgress($"Counting files: {Math.Min(count, known):N0} of {known:N0} plans", (double)Math.Min(count - 1, known - 1) / known);
                }
                else
                {
                    context.ReportProgress($"Counting files: {count:N0} plans so far");
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

    /// <summary>For a walk: whether a folder's folders are each walked as a part of their own: the server's projects and a project's plans.</summary>
    private async ValueTask<bool> HoldsPlansAsync(string path, CancellationToken ct)
        => await ResolveAsync(path, ct).ConfigureAwait(false) is BambooServerRoot or BmProjectFolder;

    /// <summary>
    /// A folder's stamp for the walks (<see cref="KeptListingsOptions.StampOf"/>): a build's folder has its key and how far
    /// it has got, so a build that has finished is kept whole, with its jobs and artifacts; a plan's folder has none (its
    /// listing, one request, is what finds a new build), nor has any other.
    /// </summary>
    internal async ValueTask<string?> StampAsync(string path, string? held, CancellationToken ct)
    {
        switch (await ResolveAsync(path, ct).ConfigureAwait(false))
        {
            case BmPlanFolder { Plan.IsBranch: false } plan:
                Volatile.Read(ref _counted)?.Reached(plan.Plan);
                return null;
            case BmBuildFolder build:
                return $"{build.Result.Key}|{build.Result.LifeCycle}|{build.Result.State}";
            default:
                return null;
        }
    }

    // --- health and keeping --------------------------------------------------------------------------------------------

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
