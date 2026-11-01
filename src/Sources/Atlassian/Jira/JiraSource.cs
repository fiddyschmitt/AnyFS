using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Atlassian.Jira;

/// <summary>
/// A Jira server, project, saved filter, board or query (type <c>jira</c>, spec 7.10), read-only: a folder per project, a
/// folder per issue with its <c>issue.md</c> (fields, description and comments as Markdown) and its attachments. Names are
/// compared in either case, as Windows does. Folder listings are kept (the SDK's <see cref="KeptListings"/>), each asked
/// for again behind the answer once older than the refresh interval; "Count files on start" walks the tree into them, a
/// project's issues a hundred a request; a project whose newest change and issue count have not moved is kept as it was.
/// </summary>
public sealed class JiraSource : SourceBase, ICountable
{
    // whole issues a search request carries (rendered text and comments make a hundred a few megabytes at most)
    private const int IssuesPerRequest = 100;

    // issue heads (key, summary, times) a request carries: Jira's own cap, jira.search.views.default.max
    private const int HeadsPerRequest = 1000;

    // issues read whole and kept for the opens and listings that follow (each a few kilobytes as JSON)
    private const int IssuesKept = 2_000;

    // an issue read alone brings the hundred numbered around it, the way a client goes through a project's folders
    private const int IssuesAround = 100;

    private static readonly string[] AllFields = ["*all"];
    private static readonly string[] HeadFields = ["summary", "created", "updated"];

    private readonly JiraSettings _settings;
    private readonly AtlassianStandIn? _standIn;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, (JiraIssue Issue, long At)> _issues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _reading = new(StringComparer.OrdinalIgnoreCase);
    private AtlassianSession? _session;
    private AtlassianApi? _api;
    private RequestCache? _cache;
    private KeptListings? _kept;
    private SpoolCache? _spool;
    private JiFolder? _root;
    private bool _degraded;

    public JiraSource(JiraSettings settings)
        : this(settings, standIn: null)
    {
    }

    internal JiraSource(JiraSettings settings, AtlassianStandIn? standIn)
    {
        _settings = settings;
        _standIn = standIn;
    }

    public override SourceCapabilities Capabilities => new(CanWrite: false, CaseInsensitive: true);

    internal JiraSettings Settings => _settings;

    internal AtlassianApi Api => _api ?? throw VfsException.Unavailable("Jira is not signed in to yet");

    internal AtlassianSession Session => _session ?? throw new InvalidOperationException("the source has not started");

    /// <summary>What the address names, once started.</summary>
    internal JiraScope Scope { get; private set; } = JiraScope.Server;

    private RequestCache Cache => _cache ?? throw VfsException.Unavailable("Jira is not signed in to yet");

    private KeptListings Kept => _kept ?? throw VfsException.Unavailable("Jira is not signed in to yet");

    private SpoolCache Spool => _spool ?? throw VfsException.Unavailable("Jira is not signed in to yet");

    private TimeSpan Refresh => _settings.Refresh > TimeSpan.Zero ? _settings.Refresh : TimeSpan.FromSeconds(2);

    /// <summary>The installation's address (its base, with a slash at the end), for the documents' links.</summary>
    internal Uri Installation => Session.Address.Base;

    protected override async Task OnInitialiseAsync(CancellationToken ct)
    {
        _session = new AtlassianSession(AtlassianProduct.Jira, _settings, Logger, _standIn, Context.Secrets);
        await _session.SignInAsync(interactive: true, message => Context.ReportProgress(message), ct).ConfigureAwait(false);
        _api = new AtlassianApi(_session, _settings.Concurrency, Logger);
        _cache = new RequestCache(_stopping.Token);
        _spool = new SpoolCache(Path.Combine(Context.CacheDirectory, "spool"), logger: Logger);

        Scope = JiraScope.From(_session.Address, out var problem) ?? throw new SourceFailedException(problem!, permanent: true);
        Context.ReportProgress(Scope.Kind == JiraScopeKind.Server ? $"Opening {_session.Address.Base.Host}" : $"Opening the {Scope.Kind.ToString().ToLowerInvariant()} {Scope.Value}");
        try
        {
            _root = await RootAsync(Scope, ct).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            throw new SourceFailedException($"Opening the {Scope.Kind.ToString().ToLowerInvariant()} {Scope.Value}: {ex.Message}", permanent: ex.Error is VfsError.AccessDenied or VfsError.NotFound, ex);
        }

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
            ListTree = ListTreeAsync,
            StampOf = StampAsync,
            FailureKeptFor = ex => ex.Error != VfsError.Unavailable && _settings.WalkEvery > TimeSpan.Zero ? _settings.WalkEvery : _settings.Refresh,
            Name = "jira",

            // the listings last run saw open at once at the next start: for the same installation, account and scope, the
            // same settings that shape the tree, and the same documents (a file's size is a fact about how it is written)
            SaveTo = Path.Combine(Context.StateDirectory, "listings.json.gz"),
            SaveKey = $"{_session.Address.Server}|{_session.User?.Name}|{Scope.Kind}:{Scope.Value}|{_settings.Shape}|md{JiraDocuments.Format}",
        });

        Logger.LogInformation("jira: serving {Scope} {Value} on {Server} as {User}", Scope.Kind.ToString().ToLowerInvariant(), Scope.Value, _session.Address.Server, _session.User?.Name);
    }

    /// <summary>The folder the scope is: the server's projects, a project, or the issues a filter, board or query finds.</summary>
    private async Task<JiFolder> RootAsync(JiraScope scope, CancellationToken ct)
    {
        switch (scope.Kind)
        {
            case JiraScopeKind.Project:
                using (var project = await Api.FindAsync($"project/{Uri.EscapeDataString(scope.Value)}", ct).ConfigureAwait(false)
                    ?? throw new VfsException(VfsError.NotFound, $"Jira has no project {scope.Value} that {Session.User?.Name} can browse"))
                {
                    return new JiraProjectFolder(JiraProject.Parse(project.RootElement), "");
                }

            case JiraScopeKind.Filter:
                return new JiraQueryRoot(await FilterJqlAsync(scope.Value, ct).ConfigureAwait(false));

            case JiraScopeKind.Board:
                using (var board = await Api.GetAsync(new Uri(Installation, $"rest/agile/1.0/board/{scope.Value}/configuration"), $"the board {scope.Value}", ct).ConfigureAwait(false))
                {
                    var filter = board.RootElement.Obj("filter").Str("id") ?? throw new VfsException(VfsError.NotFound, $"the board {scope.Value} names no filter");
                    return new JiraQueryRoot(await FilterJqlAsync(filter, ct).ConfigureAwait(false));
                }

            case JiraScopeKind.Jql:
                // asked once, so a query Jira cannot run fails here with Jira's own words
                using (await SearchPageAsync(scope.Value, [], 0, null, validate: true, ct).ConfigureAwait(false))
                {
                    return new JiraQueryRoot(scope.Value);
                }

            default:
                return new JiraServerRoot();
        }
    }

    private async Task<string> FilterJqlAsync(string id, CancellationToken ct)
    {
        using var filter = await Api.GetAsync($"filter/{id}", ct).ConfigureAwait(false);
        return filter.RootElement.Str("jql") ?? throw new VfsException(VfsError.NotFound, $"the filter {id} has no query");
    }

    /// <summary>A condition narrowed by the settings: done issues left out with "Open issues only".</summary>
    internal string Narrowed(string condition)
        => !_settings.OpenOnly ? condition : condition.Length == 0 ? "statusCategory != Done" : $"{condition} AND statusCategory != Done";

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
            return (_root ?? throw VfsException.Unavailable("Jira is not signed in to yet")).Entry;
        }

        return await Kept.Cache.StatAsync(normalized, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false);
    }

    /// <summary>A folder's listing from Jira, through the caches below (what <see cref="KeptListings"/> keeps).</summary>
    private async IAsyncEnumerable<VfsEntry> ListFolderAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        if (node is not JiFolder folder)
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
            throw VfsException.NotSupported("writing to Jira");
        }

        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        return node is JiFile file ? await Guard(() => file.OpenAsync(this, ct)).ConfigureAwait(false) : throw VfsException.IsADirectory(path);
    }

    /// <summary>The node at <paramref name="path"/>, found a folder at a time from the root (an issue by its key, not by listing its project).</summary>
    private async Task<JiNode> ResolveAsync(string path, CancellationToken ct)
    {
        JiNode node = _root ?? throw VfsException.Unavailable("Jira is not signed in to yet");
        var normalized = VfsPath.Normalize(path);
        if (normalized.Length == 0)
        {
            return node;
        }

        foreach (var segment in VfsPath.Split(normalized))
        {
            if (node is not JiFolder folder)
            {
                throw VfsException.NotADirectory(path);
            }

            node = await Guard(() => folder.FindAsync(this, segment, ct)).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
        }

        return node;
    }

    /// <summary>Runs a request to Jira, marking the source Degraded while Jira cannot be reached and Ready again once it answers.</summary>
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

    /// <summary>Every project the account can browse, by name (archived ones left out, as Jira lists them).</summary>
    internal Task<List<JiraProject>> ProjectsAsync(CancellationToken ct)
        => Cache.GetAsync("projects", Refresh, async c =>
        {
            using var doc = await Api.GetAsync("project", c).ConfigureAwait(false);
            var projects = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(JiraProject.Parse).Where(p => p.Key.Length > 0).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.Ordinal).ToList()
                : [];
            Logger.LogInformation("jira: {User} can browse {Count:N0} projects", Session.User?.Name, projects.Count);
            return projects;
        }, ct);

    /// <summary>A project's <c>project.md</c> (its components and versions: the project call, once a refresh interval).</summary>
    internal async Task<JiNode> ProjectFileAsync(string key, CancellationToken ct)
    {
        var (bytes, modified) = await Cache.GetAsync($"project:{key}", Refresh, async c =>
        {
            using var doc = await Api.GetAsync($"project/{Uri.EscapeDataString(key)}", c).ConfigureAwait(false);
            return new Tuple<byte[], DateTimeOffset?>(JiraDocuments.Project(doc.RootElement, Installation), null);
        }, ct).ConfigureAwait(false);
        return new JiText(JiraDocuments.ProjectFile, bytes, modified);
    }

    /// <summary>The keys, summaries and times of the issues a condition finds, by number: a thousand a request, by id.</summary>
    internal Task<List<JiraIssueHead>> IssueHeadsAsync(string condition, CancellationToken ct)
        => Cache.GetAsync($"heads:{condition}", Refresh, async c =>
        {
            var heads = new List<JiraIssueHead>();
            await foreach (var (issue, _) in SearchAsync(condition, HeadFields, full: false, HeadsPerRequest, c).ConfigureAwait(false))
            {
                heads.Add(JiraIssueHead.Parse(issue));
            }

            return heads;
        }, ct);

    /// <summary>
    /// An issue whole (every field, Jira's rendering of its text, the fields' names), kept a refresh interval: read with
    /// the issues numbered around it in one search, as a client goes on through a project's folders; read alone where
    /// that search does not have it (moved to another project, or the server will not take the range). Null for an issue
    /// that is not there (or not to this account).
    /// </summary>
    internal async Task<JiraIssue?> IssueAsync(string key, CancellationToken ct)
    {
        if (Fresh(key) is { } kept)
        {
            return kept;
        }

        if (JiraScope.IssueKey().Match(key) is { Success: true } match && long.TryParse(match.Groups["number"].Value, out var number))
        {
            var project = match.Groups["project"].Value.ToUpperInvariant();
            var first = (number - 1) / IssuesAround * IssuesAround + 1;
            var block = $"{project}:{first}";
            // one read of a block however many ask for issues in it at once
            var reading = _reading.GetOrAdd(block, _ => new Lazy<Task>(() => ReadAroundAsync(project, first, first + IssuesAround - 1))).Value;
            try
            {
                await reading.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (VfsException ex) when (ex.Error is not VfsError.Unavailable)
            {
                Logger.LogDebug("jira: the issues around {Key} could not be read together ({Error}); reading it alone", key, ex.Message);
            }

            if (Fresh(key) is { } around)
            {
                return around;
            }
        }

        return await Cache.GetAsync($"issue:{key}", Refresh, async c =>
        {
            using var doc = await Api.FindAsync($"issue/{Uri.EscapeDataString(key)}?expand=renderedFields,names&fields=*all", c).ConfigureAwait(false);
            var issue = doc is null ? null : JiraIssue.Parse(doc.RootElement);
            if (issue is not null)
            {
                Remember(issue);
            }

            return new Box<JiraIssue?>(issue);
        }, ct).ConfigureAwait(false) is { Value: { } read } ? read : null;
    }

    /// <summary>The issues numbered <paramref name="first"/> to <paramref name="last"/> in a project, read whole in one search and kept.</summary>
    private async Task ReadAroundAsync(string project, long first, long last)
    {
        await Task.Yield();
        try
        {
            var jql = $"project = {JiraScope.Quote(project)} AND issuekey >= {project}-{first} AND issuekey <= {project}-{last}";
            await foreach (var (issue, names) in SearchAsync(jql, AllFields, full: true, IssuesPerRequest, _stopping.Token, validate: false).ConfigureAwait(false))
            {
                Remember(JiraIssue.Parse(issue, names));
            }
        }
        finally
        {
            _reading.TryRemove($"{project}:{first}", out _);
        }
    }

    private JiraIssue? Fresh(string key)
        => _issues.TryGetValue(key, out var kept) && Stopwatch.GetElapsedTime(kept.At) < Refresh ? kept.Issue : null;

    private void Remember(JiraIssue issue)
    {
        _issues[issue.Key] = (issue, Stopwatch.GetTimestamp());
        if (_issues.Count > IssuesKept)
        {
            foreach (var old in _issues.OrderBy(i => i.Value.At).Take(IssuesKept / 4).ToList())
            {
                _issues.TryRemove(old);
            }
        }
    }

    /// <summary>An issue folder's children: its <c>issue.md</c> and, where it has attachments and they are shown, its <c>attachments</c>.</summary>
    internal async Task<IReadOnlyList<JiNode>> IssueChildrenAsync(JiraIssue issue, CancellationToken ct)
    {
        var comments = _settings.Comments ? await CommentsAsync(issue, ct).ConfigureAwait(false) : null;
        var showAttachments = _settings.Attachments && issue.Attachments.Count > 0;
        var names = showAttachments ? JiraDocuments.AttachmentNames(issue) : null;
        var markdown = JiraDocuments.Issue(issue, comments, names, Installation);
        var children = new List<JiNode> { new JiText(JiraDocuments.IssueFile, markdown, issue.Updated, issue.Created) };
        if (showAttachments)
        {
            children.Add(new JiraAttachmentsFolder(issue));
        }

        return children;
    }

    /// <summary>
    /// An issue's comments, oldest first: those its response carried, with Jira's rendering of each; where Jira carried
    /// fewer than the issue has, all of them from the comment call, a page at a time.
    /// </summary>
    private async Task<IReadOnlyList<JiraComment>> CommentsAsync(JiraIssue issue, CancellationToken ct)
    {
        var (carried, total) = issue.CarriedComments;
        if (total <= carried.Count)
        {
            var rendered = issue.Rendered.Obj("comment").Arr("comments").Where(c => c.Str("id") is not null).ToDictionary(c => c.Str("id")!, c => c.Str("body"), StringComparer.Ordinal);
            return carried.Select(c => JiraComment.Parse(c, rendered.GetValueOrDefault(c.Str("id") ?? ""))).OrderBy(c => c.Created ?? DateTimeOffset.MinValue).ToList();
        }

        return await Cache.GetAsync($"comments:{issue.Key}@{issue.Updated?.UtcTicks}", Refresh, async c =>
        {
            var all = new List<JiraComment>();
            for (var start = 0; ; start += 1000)
            {
                using var page = await Api.GetAsync($"issue/{Uri.EscapeDataString(issue.Key)}/comment?startAt={start}&maxResults=1000&orderBy=created&expand=renderedBody", c).ConfigureAwait(false);
                var comments = page.RootElement.Arr("comments").ToList();
                all.AddRange(comments.Select(e => JiraComment.Parse(e, null)));
                if (comments.Count == 0 || start + comments.Count >= page.RootElement.Long("total"))
                {
                    break;
                }
            }

            return all.OrderBy(x => x.Created ?? DateTimeOffset.MinValue).ToList();
        }, ct).ConfigureAwait(false);
    }

    /// <summary>An attachment's bytes, spooled from Jira as they arrive and kept by its id (an attachment never changes).</summary>
    internal Task<IVfsFile> OpenAttachmentAsync(JiraAttachment attachment, CancellationToken ct)
    {
        if (attachment.Size == 0)
        {
            return Task.FromResult<IVfsFile>(new MemoryFile([]));
        }

        // the address at the source's own installation: Jira writes its configured base URL into the attachment's
        var url = Session.Address.Page($"secure/attachment/{attachment.Id}/{Uri.EscapeDataString(attachment.FileName)}");
        return Spool.OpenAsync($"jira:{Installation.Authority}{Installation.AbsolutePath}:attachment:{attachment.Id}", attachment.Size,
            async c => await ResponseStream.OpenAsync(await Api.OpenAsync(url, "an attachment", c).ConfigureAwait(false), c).ConfigureAwait(false), ct);
    }

    // --- searching -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every issue a condition finds, a page at a time, ordered by id and paged by it (<c>id &gt; the last</c>): a search
    /// past Jira's result window (10,000 where it is set) fails with startAt, never so. Each issue's element lives while
    /// the caller handles it (its page's document goes when the next issue is asked for): a caller that keeps one clones
    /// it, as <see cref="JiraIssue.Parse"/> does.
    /// </summary>
    internal async IAsyncEnumerable<(JsonElement Issue, IReadOnlyDictionary<string, string> Names)> SearchAsync(
        string condition, string[] fields, bool full, int pageSize, [EnumeratorCancellation] CancellationToken ct, bool validate = true)
    {
        string? last = null;
        while (true)
        {
            var where = string.Join(" AND ", new[] { condition, last is null ? "" : $"id > {last}" }.Where(s => s.Length > 0));
            using var doc = await SearchPageAsync((where.Length == 0 ? "" : where + " ") + "ORDER BY id ASC", fields, pageSize, full ? ["renderedFields", "names"] : null, validate, ct).ConfigureAwait(false);
            var names = full ? JiraIssue.NamesOf(doc.RootElement) : new Dictionary<string, string>(StringComparer.Ordinal);
            var issues = doc.RootElement.Arr("issues").ToList();
            foreach (var issue in issues)
            {
                yield return (issue, names);
            }

            var total = doc.RootElement.Long("total");
            if (issues.Count == 0 || issues.Count >= total)
            {
                yield break;
            }

            last = issues[^1].Str("id");
        }
    }

    /// <summary>One search request (POSTed: a query of a hundred keys does not fit an address).</summary>
    private Task<JsonDocument> SearchPageAsync(string jql, string[] fields, int maxResults, string[]? expand, bool validate, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["jql"] = jql,
            ["startAt"] = 0,
            ["maxResults"] = maxResults,
            ["fields"] = new JsonArray(fields.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            ["validateQuery"] = validate,
        };
        if (expand is not null)
        {
            body["expand"] = new JsonArray(expand.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray());
        }

        return Api.PostAsync("search", body.ToJsonString(), ct);
    }

    // --- counting and walking ------------------------------------------------------------------------------------------

    /// <summary>
    /// "Count files on start": the first walk of the tree (<see cref="KeptListings"/>), which lists every folder and keeps
    /// the listings, so they open at once afterwards: each project's issues whole, a hundred a request
    /// (<see cref="ListTreeAsync"/>); with listings kept, a walk again every "Walk every folder every" minutes.
    /// </summary>
    public Task CountFilesAsync(CancellationToken ct) => Kept.CountAsync(ct);

    /// <summary>The listings as kept, for tests.</summary>
    internal ListingCache Listings => Kept.Cache;

    /// <summary>One walk now (a test's: the walks after the count come by the clock), stamps kept unless <paramref name="full"/>.</summary>
    internal Task<bool> WalkAsync(SourceCounters counters, bool full, CancellationToken ct) => Kept.WalkAsync(counters, full, ct);

    /// <summary>
    /// A folder's stamp for the walks (<see cref="KeptListingsOptions.StampOf"/>): a folder of issues (a project, the
    /// scope's root) has its newest update and its issue count, one search with one result, so a project nothing changed
    /// in (no issue edited, commented, added, moved or deleted) is kept whole; an issue's folder its update time. Any other
    /// folder none (the server's projects are a request to list).
    /// </summary>
    internal async ValueTask<string?> StampAsync(string path, string? held, CancellationToken ct)
    {
        var segments = VfsPath.Split(VfsPath.Normalize(path)).ToList();
        var issuesDepth = Scope.Kind == JiraScopeKind.Server ? 1 : 0;
        if (segments.Count == issuesDepth)
        {
            if (await ResolveAsync(path, ct).ConfigureAwait(false) is not JiraIssuesFolder folder)
            {
                return null;
            }

            using var doc = await SearchPageAsync((folder.Condition(this) is { Length: > 0 } c ? c + " " : "") + "ORDER BY updated DESC", ["updated"], 1, null, validate: true, ct).ConfigureAwait(false);
            var newest = doc.RootElement.Arr("issues").FirstOrDefault().Obj("fields").Date("updated");
            return $"{newest?.UtcTicks ?? 0}|{doc.RootElement.Long("total")}";
        }

        if (segments.Count == issuesDepth + 1 && JiraScope.KeyOfFolder(segments[^1]) is { } key)
        {
            using var doc = await Api.FindAsync($"issue/{Uri.EscapeDataString(key)}?fields=updated", ct).ConfigureAwait(false);
            return doc is null ? "gone" : (doc.RootElement.Obj("fields").Date("updated")?.UtcTicks ?? 0).ToString(CultureInfo.InvariantCulture);
        }

        return null;
    }

    /// <summary>
    /// For a walk: a folder of issues (a project, the scope's root) and everything below it. Read whole (every issue with
    /// every field, a hundred a search) the first time, and again once a day (<see cref="ReadWholeEvery"/>); between, only
    /// the issues changed since the last read (<c>updated &gt;= -Nm</c>, Jira's relative time, which needs no time zone),
    /// the others' listings kept as they were, unless the issue count says one went or came unseen. Null for any other
    /// folder, which the walk lists alone.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>?> ListTreeAsync(string path, CancellationToken ct)
    {
        if (await ResolveAsync(path, ct).ConfigureAwait(false) is not JiraIssuesFolder folder)
        {
            return null;
        }

        return await Guard(async () =>
        {
            if (_treeReads.TryGetValue(path, out var read) && DateTimeOffset.UtcNow - read.Whole < ReadWholeEvery
                && await ChangedTreeAsync(path, folder, read, ct).ConfigureAwait(false) is { } changed)
            {
                return changed;
            }

            return await WholeTreeAsync(path, folder, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>When a folder of issues was last read whole, and last read at all (whole, or the changes since): where the next read's window starts.</summary>
    private sealed record TreeRead(DateTimeOffset Whole, DateTimeOffset At);

    // by path, for the run (a restart reads a changed folder whole once)
    private readonly ConcurrentDictionary<string, TreeRead> _treeReads = new(StringComparer.Ordinal);

    private static readonly TimeSpan ReadWholeEvery = TimeSpan.FromDays(1);

    /// <summary>A folder of issues read whole: its listing, and every issue folder's and attachments folder's below it.</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>> WholeTreeAsync(string path, JiraIssuesFolder folder, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var listings = new Dictionary<string, IReadOnlyList<VfsEntry>>(StringComparer.Ordinal);
        var entries = new List<VfsEntry>();
        if (folder is JiraProjectFolder project)
        {
            entries.Add((await ProjectFileAsync(project.Project.Key, ct).ConfigureAwait(false)).Entry);
        }

        var count = 0;
        await foreach (var (json, names) in SearchAsync(folder.Condition(this), AllFields, full: true, IssuesPerRequest, ct).ConfigureAwait(false))
        {
            entries.Add(await ListIssueAsync(path, JiraIssue.Parse(json, names), listings, ct).ConfigureAwait(false));
            count++;
        }

        listings[path] = entries;
        _treeReads[path] = new TreeRead(started, started);
        Logger.LogDebug("jira: read {Folder} whole: {Count:N0} issues", path.Length == 0 ? "the scope" : path, count);
        return listings;
    }

    /// <summary>An issue's folder and the listings below it, into <paramref name="listings"/>; its entry in its parent's listing.</summary>
    private async Task<VfsEntry> ListIssueAsync(string parent, JiraIssue issue, Dictionary<string, IReadOnlyList<VfsEntry>> listings, CancellationToken ct)
    {
        var issueFolder = new JiraIssueFolder(issue);
        var issuePath = VfsPath.Join(parent, issueFolder.Name);
        var children = await IssueChildrenAsync(issue, ct).ConfigureAwait(false);
        listings[issuePath] = children.Select(c => c.Entry).ToList();
        if (children.OfType<JiraAttachmentsFolder>().FirstOrDefault() is { } attachments)
        {
            listings[VfsPath.Join(issuePath, attachments.Name)] = (await attachments.ListAsync(this, ct).ConfigureAwait(false)).Select(c => c.Entry).ToList();
        }

        return issueFolder.Entry;
    }

    /// <summary>
    /// A folder of issues from what is held of it and the issues changed since its last read (<paramref name="read"/>):
    /// null where that cannot be trusted (nothing held, a last read too long ago, an issue count that does not add up),
    /// and the folder is read whole. The window starts from the last read, not from the newest held issue: the folder's
    /// own listing is listed afresh behind answers between walks, which moves the newest held issue on while the issue
    /// folders below it keep the listings of the last read.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>?> ChangedTreeAsync(string path, JiraIssuesFolder folder, TreeRead read, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var held = Kept.Cache.Under(path);
        if (!held.TryGetValue(path, out var top))
        {
            return null;
        }

        var issues = top.Where(e => e.IsDirectory && JiraScope.KeyOfFolder(e.Name) is not null).ToList();

        // a length of time since the last read, so the two clocks need not agree; a couple of minutes more, as Jira's
        // relative times count in whole minutes and an issue may have changed as the last read went by it
        var minutes = (long)Math.Ceiling((started - read.At).TotalMinutes) + 2;
        if (minutes > TimeSpan.FromDays(7).TotalMinutes)
        {
            return null;
        }

        var condition = folder.Condition(this);
        var since = (condition.Length == 0 ? "" : condition + " AND ") + $"updated >= -{minutes}m";
        var changed = new List<JiraIssue>();
        await foreach (var (json, names) in SearchAsync(since, AllFields, full: true, IssuesPerRequest, ct).ConfigureAwait(false))
        {
            changed.Add(JiraIssue.Parse(json, names));
        }

        // an issue deleted or moved out leaves no update to find: the count tells
        using (var count = await SearchPageAsync((condition.Length == 0 ? "" : condition + " ") + "ORDER BY id ASC", ["updated"], 0, null, validate: true, ct).ConfigureAwait(false))
        {
            var keys = issues.Select(e => JiraScope.KeyOfFolder(e.Name)!).Concat(changed.Select(i => i.Key)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (count.RootElement.Long("total") != keys)
            {
                Logger.LogDebug("jira: {Folder} has {Total:N0} issues, not the {Held:N0} held and changed: reading it whole", path, count.RootElement.Long("total"), keys);
                return null;
            }
        }

        var changedKeys = changed.Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listings = new Dictionary<string, IReadOnlyList<VfsEntry>>(StringComparer.Ordinal);
        var entries = new List<VfsEntry>();
        if (folder is JiraProjectFolder project)
        {
            entries.Add((await ProjectFileAsync(project.Project.Key, ct).ConfigureAwait(false)).Entry);
        }

        // the unchanged issues as held, those whose listings are not all held read again with the changed ones
        var missing = new List<string>();
        foreach (var entry in issues.Where(e => !changedKeys.Contains(JiraScope.KeyOfFolder(e.Name)!)))
        {
            var issuePath = VfsPath.Join(path, entry.Name);
            if (!held.TryGetValue(issuePath, out var listing)
                || listing.Any(c => c.IsDirectory) && listing.Where(c => c.IsDirectory).Any(c => !held.ContainsKey(VfsPath.Join(issuePath, c.Name))))
            {
                missing.Add(JiraScope.KeyOfFolder(entry.Name)!);
                continue;
            }

            entries.Add(entry);
            listings[issuePath] = listing;
            foreach (var sub in listing.Where(c => c.IsDirectory))
            {
                listings[VfsPath.Join(issuePath, sub.Name)] = held[VfsPath.Join(issuePath, sub.Name)];
            }
        }

        foreach (var batch in missing.Chunk(IssuesPerRequest))
        {
            await foreach (var (json, names) in SearchAsync($"key in ({string.Join(", ", batch)})", AllFields, full: true, IssuesPerRequest, ct, validate: false).ConfigureAwait(false))
            {
                changed.Add(JiraIssue.Parse(json, names));
            }
        }

        foreach (var issue in changed)
        {
            entries.Add(await ListIssueAsync(path, issue, listings, ct).ConfigureAwait(false));
        }

        listings[path] = entries;
        _treeReads[path] = read with { At = started };
        Logger.LogDebug("jira: {Folder}: {Changed:N0} issues changed since the last walk, {Kept:N0} kept", path.Length == 0 ? "the scope" : path, changed.Count, entries.Count - changed.Count);
        return listings;
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

        // the walks stop before what they ask through goes, and what they kept is saved
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
