using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Atlassian.Confluence;

/// <summary>
/// A Confluence server, space or page (type <c>confluence</c>, spec 7.10), read-only: a folder per space, every page a
/// folder holding its Markdown, its attachments and its child pages' folders, and each space's blog. Names are compared in
/// either case, as Windows does. Folder listings are kept (the SDK's <see cref="KeptListings"/>), each asked for again
/// behind the answer once older than the refresh interval; "Count files on start" walks the tree into them, a space whole
/// at a time (its pages, attachments and comments in bulk); a space whose newest change and content count have not moved
/// is kept as it was. A folder whose listing failed stays so until the next walk.
/// </summary>
public sealed class ConfluenceSource : SourceBase, ICountable
{
    // pages with their bodies a request carries (Confluence caps a request with bodies lower than one without)
    private const int BodiesPerRequest = 50;

    // things without bodies a request carries
    private const int ItemsPerRequest = 200;

    // pages read whole and kept for the listings and opens that follow
    private const int PagesKept = 2_000;

    private const string Summary = "version,history";
    private const string Whole = "body.storage,version,history,metadata.labels,ancestors,space";

    private readonly ConfluenceSettings _settings;
    private readonly AtlassianStandIn? _standIn;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, (ConfluenceContent Content, long At)> _pages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _reading = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _people = new(StringComparer.Ordinal);
    private AtlassianSession? _session;
    private AtlassianApi? _api;
    private RequestCache? _cache;
    private KeptListings? _kept;
    private SpoolCache? _spool;
    private CoFolder? _root;
    private bool _degraded;

    public ConfluenceSource(ConfluenceSettings settings)
        : this(settings, standIn: null)
    {
    }

    internal ConfluenceSource(ConfluenceSettings settings, AtlassianStandIn? standIn)
    {
        _settings = settings;
        _standIn = standIn;
    }

    public override SourceCapabilities Capabilities => new(CanWrite: false, CaseInsensitive: true);

    internal ConfluenceSettings Settings => _settings;

    internal AtlassianApi Api => _api ?? throw VfsException.Unavailable("Confluence is not signed in to yet");

    internal AtlassianSession Session => _session ?? throw new InvalidOperationException("the source has not started");

    /// <summary>What the address names, once started.</summary>
    internal ConfluenceScope Scope { get; private set; } = ConfluenceScope.Server;

    private RequestCache Cache => _cache ?? throw VfsException.Unavailable("Confluence is not signed in to yet");

    private KeptListings Kept => _kept ?? throw VfsException.Unavailable("Confluence is not signed in to yet");

    private SpoolCache Spool => _spool ?? throw VfsException.Unavailable("Confluence is not signed in to yet");

    private TimeSpan Refresh => _settings.Refresh > TimeSpan.Zero ? _settings.Refresh : TimeSpan.FromSeconds(2);

    /// <summary>The installation's address (its base, with a slash at the end).</summary>
    internal Uri Installation => Session.Address.Base;

    protected override async Task OnInitialiseAsync(CancellationToken ct)
    {
        _session = new AtlassianSession(AtlassianProduct.Confluence, _settings, Logger, _standIn, Context.Secrets);
        await _session.SignInAsync(interactive: true, message => Context.ReportProgress(message), ct).ConfigureAwait(false);
        _api = new AtlassianApi(_session, _settings.Concurrency, Logger);
        _cache = new RequestCache(_stopping.Token);
        _spool = new SpoolCache(Path.Combine(Context.CacheDirectory, "spool"), logger: Logger);

        Scope = ConfluenceScope.From(_session.Address, out var problem) ?? throw new SourceFailedException(problem!, permanent: true);
        Context.ReportProgress(Scope.Kind switch
        {
            ConfluenceScopeKind.Space => $"Opening the space {Scope.Space}",
            ConfluenceScopeKind.Page => "Opening the page",
            _ => $"Opening {_session.Address.Base.Host}",
        });
        try
        {
            _root = await RootAsync(Scope, ct).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            throw new SourceFailedException($"Opening {(Scope.Kind == ConfluenceScopeKind.Server ? _session.Address.Base.Host : "the address")}: {ex.Message}", permanent: ex.Error is VfsError.AccessDenied or VfsError.NotFound, ex);
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
            FailureKeptFor = _ => _settings.FailureKeptFor,
            Name = "confluence",
            SaveTo = Path.Combine(Context.StateDirectory, "listings.json.gz"),
            SaveKey = $"{_session.Address.Server}|{_session.User?.Name}|{Scope}|{_settings.Shape}|md{ConfluenceDocuments.Format}",
        });

        // a sign-in renewed: what it refused may be answered now
        _session.Renewed += _kept.Cache.ForgetFailures;
        Logger.LogInformation("confluence: serving {Scope} on {Server} as {User}", Scope, _session.Address.Server, _session.User?.Name);
    }

    /// <summary>The folder the scope is: the server's spaces, a space, or a page (with the pages below it).</summary>
    private async Task<CoFolder> RootAsync(ConfluenceScope scope, CancellationToken ct)
    {
        switch (scope.Kind)
        {
            case ConfluenceScopeKind.Space:
                using (var space = await Api.FindAsync($"space/{Uri.EscapeDataString(scope.Space)}?expand=homepage", ct).ConfigureAwait(false)
                    ?? throw new VfsException(VfsError.NotFound, $"Confluence has no space {scope.Space} that {Session.User?.Name} can read"))
                {
                    return new ConfluenceSpaceFolder(ConfluenceSpace.Parse(space.RootElement), "");
                }

            case ConfluenceScopeKind.Page:
                var page = scope.PageId is { } id
                    ? await FindContentAsync($"content/{id}?expand={Whole}", ct).ConfigureAwait(false)
                    : await FindContentAsync($"content?spaceKey={Uri.EscapeDataString(scope.Space)}&title={Uri.EscapeDataString(scope.Title ?? "")}&type={(scope.PostingDay is null ? "page" : "blogpost")}"
                        + (scope.PostingDay is { } day ? $"&postingDay={day:yyyy-MM-dd}" : "") + $"&expand={Whole}", ct).ConfigureAwait(false);
                if (page is null)
                {
                    throw new VfsException(VfsError.NotFound, $"Confluence has no such page that {Session.User?.Name} can read");
                }

                Remember(page);
                return new ConfluencePageFolder(page, "", page.ParentId, page.IsBlogPost);

            default:
                return new ConfluenceServerRoot();
        }
    }

    /// <summary>A page by an address that answers it alone or as the first of a list; null where there is none.</summary>
    private async Task<ConfluenceContent?> FindContentAsync(string relative, CancellationToken ct)
    {
        using var doc = await Api.FindAsync(relative, ct).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        var element = doc.RootElement.Has("results") ? doc.RootElement.Arr("results").FirstOrDefault() : doc.RootElement;
        return element.ValueKind == JsonValueKind.Object ? ConfluenceContent.Parse(element) : null;
    }

    // --- the VFS -------------------------------------------------------------------------------------------------------

    public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var entry in await Kept.Cache.GetAsync(VfsPath.Normalize(path), ct).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    public override async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        var normalized = VfsPath.Normalize(path);
        if (normalized.Length == 0)
        {
            return (_root ?? throw VfsException.Unavailable("Confluence is not signed in to yet")).Entry;
        }

        return await Kept.Cache.StatAsync(normalized, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<VfsEntry> ListFolderAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var entry in await ListedAsync(path, ct).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    /// <summary>A folder's listing from Confluence. One that fails is said once in the log: a drive shows it as an empty folder, and it stays so until the next walk.</summary>
    private async Task<List<VfsEntry>> ListedAsync(string path, CancellationToken ct)
    {
        try
        {
            if (await ResolveAsync(path, ct).ConfigureAwait(false) is not CoFolder folder)
            {
                throw VfsException.NotADirectory(path);
            }

            return (await Guard(() => folder.ListAsync(this, ct)).ConfigureAwait(false)).Select(child => child.Entry).ToList();
        }
        catch (VfsException ex) when (ex.Error is not (VfsError.NotFound or VfsError.NotADirectory))
        {
            if (_unlisted.TryAdd(path, 0))
            {
                Logger.LogInformation("confluence: '{Path}' could not be listed ({Error}); it shows as an empty folder until the next walk", path, ex.Message);
            }

            throw;
        }
    }

    // the folders whose listing failed, each said once in the log
    private readonly ConcurrentDictionary<string, byte> _unlisted = new(StringComparer.Ordinal);

    public override async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        if (mode.IsWrite())
        {
            throw VfsException.NotSupported("writing to Confluence");
        }

        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        return node is CoFile file ? await Guard(() => file.OpenAsync(this, ct)).ConfigureAwait(false) : throw VfsException.IsADirectory(path);
    }

    private async Task<CoNode> ResolveAsync(string path, CancellationToken ct)
    {
        CoNode node = _root ?? throw VfsException.Unavailable("Confluence is not signed in to yet");
        var normalized = VfsPath.Normalize(path);
        if (normalized.Length == 0)
        {
            return node;
        }

        foreach (var segment in VfsPath.Split(normalized))
        {
            if (node is not CoFolder folder)
            {
                throw VfsException.NotADirectory(path);
            }

            node = await Guard(() => folder.FindAsync(this, segment, ct)).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
        }

        return node;
    }

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

    /// <summary>
    /// Every element of a listing, page after page as its <c>_links.next</c> leads (offset and cursor paging alike):
    /// <paramref name="relative"/> to the REST API, a limit in it. Each element lives while the caller handles it (its
    /// page's document goes when the next element is asked for): the models read what they keep out of it.
    /// </summary>
    internal async IAsyncEnumerable<JsonElement> PagesAsync(string relative, [EnumeratorCancellation] CancellationToken ct)
    {
        Uri? next = Api.Rest(relative);
        while (next is not null)
        {
            using var doc = await Api.GetAsync(next, relative, ct).ConfigureAwait(false);
            var results = doc.RootElement.Arr("results").ToList();
            foreach (var result in results)
            {
                yield return result;
            }

            next = results.Count > 0 && doc.RootElement.Obj("_links").Str("next") is { Length: > 0 } link ? Next(link) : null;
        }
    }

    /// <summary>A <c>_links.next</c> at the source's installation: relative to the installation, or a path that starts with its own.</summary>
    private Uri Next(string link)
    {
        if (Uri.TryCreate(link, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            return new Uri(Installation, absolute.PathAndQuery.StartsWith(Installation.AbsolutePath, StringComparison.Ordinal) ? absolute.PathAndQuery[Installation.AbsolutePath.Length..] : absolute.PathAndQuery.TrimStart('/'));
        }

        var context = Installation.AbsolutePath.TrimEnd('/');
        var path = context.Length > 0 && link.StartsWith(context + "/", StringComparison.Ordinal) ? link[context.Length..] : link;
        return new Uri(Installation, path.TrimStart('/'));
    }

    /// <summary>The spaces the account can read: global ones; personal ones too, and archived ones, where those folders are shown.</summary>
    internal Task<List<ConfluenceSpace>> SpacesAsync(CancellationToken ct)
        => Cache.GetAsync("spaces", Refresh, async c =>
        {
            var spaces = new List<ConfluenceSpace>();
            var type = _settings.PersonalSpaces ? "" : "&type=global";
            foreach (var status in _settings.ArchivedSpaces ? new[] { "current", "archived" } : ["current"])
            {
                await foreach (var s in PagesAsync($"space?status={status}{type}&limit={ItemsPerRequest}&expand=homepage", c).ConfigureAwait(false))
                {
                    var space = ConfluenceSpace.Parse(s);
                    spaces.Add(space with { Status = status });
                }
            }

            Logger.LogInformation("confluence: {User} can read {Count:N0} spaces", Session.User?.Name, spaces.Count);
            return spaces.Where(s => s.Key.Length > 0).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Key, StringComparer.Ordinal).ToList();
        }, ct);

    /// <summary>A space's top-level pages (its home page among them), without bodies.</summary>
    internal Task<List<ConfluenceContent>> TopPagesAsync(string space, CancellationToken ct)
        => ContentsAsync($"top:{space}", $"space/{Uri.EscapeDataString(space)}/content/page?depth=root&limit={ItemsPerRequest}&expand={Summary}", ct);

    /// <summary>A page's child pages, without bodies.</summary>
    internal Task<List<ConfluenceContent>> ChildPagesAsync(string pageId, CancellationToken ct)
        => ContentsAsync($"children:{pageId}", $"content/{pageId}/child/page?limit={ItemsPerRequest}&expand={Summary}", ct);

    /// <summary>A space's blog posts, newest first, without bodies.</summary>
    internal Task<List<ConfluenceContent>> BlogPostsAsync(string space, CancellationToken ct)
        => ContentsAsync($"blog:{space}", $"content?spaceKey={Uri.EscapeDataString(space)}&type=blogpost&limit={ItemsPerRequest}&expand={Summary}", ct);

    internal async Task<bool> HasBlogAsync(string space, CancellationToken ct) => (await BlogPostsAsync(space, ct).ConfigureAwait(false)).Count > 0;

    private Task<List<ConfluenceContent>> ContentsAsync(string key, string relative, CancellationToken ct)
        => Cache.GetAsync(key, Refresh, async c =>
        {
            var contents = new List<ConfluenceContent>();
            await foreach (var e in PagesAsync(relative, c).ConfigureAwait(false))
            {
                contents.Add(ConfluenceContent.Parse(e));
            }

            return contents;
        }, ct);

    /// <summary>A page's attachments (their newest versions), oldest first: the order a name taken twice is numbered in.</summary>
    internal Task<List<ConfluenceAttachment>> AttachmentsAsync(string pageId, CancellationToken ct)
        => Cache.GetAsync($"attachments:{pageId}", Refresh, async c =>
        {
            var attachments = new List<ConfluenceAttachment>();
            await foreach (var e in PagesAsync($"content/{pageId}/child/attachment?limit={ItemsPerRequest}&expand=version", c).ConfigureAwait(false))
            {
                attachments.Add(ConfluenceAttachment.Parse(e, pageId));
            }

            return attachments.OrderBy(a => a.Id.Length).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
        }, ct);

    /// <summary>A page's comments, footer and inline, replies among them, oldest first.</summary>
    internal Task<List<ConfluenceComment>> CommentsAsync(string pageId, CancellationToken ct)
        => Cache.GetAsync($"comments:{pageId}", Refresh, async c =>
        {
            var comments = new List<ConfluenceComment>();
            await foreach (var e in PagesAsync($"content/{pageId}/child/comment?depth=all&limit={BodiesPerRequest}&expand=body.storage,version,history,ancestors,extensions.inlineProperties", c).ConfigureAwait(false))
            {
                comments.Add(ConfluenceComment.Parse(e, pageId));
            }

            return comments.OrderBy(x => x.Created ?? DateTimeOffset.MinValue).ToList();
        }, ct);

    /// <summary>
    /// A page with its body, kept a refresh interval: read with the pages beside it (its parent's children, or the space's
    /// top-level pages, or its blog), as a client goes on through a folder; read alone where that read did not have it.
    /// </summary>
    internal async Task<ConfluenceContent> PageAsync(ConfluencePageFolder folder, CancellationToken ct)
    {
        var id = folder.Content.Id;
        if (Fresh(id, folder.Content.Version) is { } kept)
        {
            return kept;
        }

        var siblings = folder.ParentId is { } parent
            ? $"content/{parent}/child/page?limit={BodiesPerRequest}&expand={Whole}"
            : folder.Blog
                ? $"content?spaceKey={Uri.EscapeDataString(folder.Content.SpaceKey)}&type=blogpost&limit={BodiesPerRequest}&expand={Whole}"
                : folder.Content.SpaceKey.Length > 0 ? $"space/{Uri.EscapeDataString(folder.Content.SpaceKey)}/content/page?depth=root&limit={BodiesPerRequest}&expand={Whole}" : null;
        if (siblings is not null)
        {
            var reading = _reading.GetOrAdd(siblings, s => new Lazy<Task>(() => ReadTogetherAsync(s))).Value;
            try
            {
                await reading.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (VfsException ex) when (ex.Error is not VfsError.Unavailable)
            {
                Logger.LogDebug("confluence: the pages beside {Id} could not be read together ({Error}); reading it alone", id, ex.Message);
            }

            if (Fresh(id, null) is { } together)
            {
                return together;
            }
        }

        var alone = await Cache.GetAsync($"page:{id}", Refresh, async c => await FindContentAsync($"content/{id}?expand={Whole}", c).ConfigureAwait(false) is { } page
            ? new Box<ConfluenceContent>(page)
            : throw VfsException.NotFound(folder.Content.Title), ct).ConfigureAwait(false);
        Remember(alone.Value);
        return alone.Value;
    }

    private async Task ReadTogetherAsync(string relative)
    {
        await Task.Yield();
        try
        {
            await foreach (var e in PagesAsync(relative, _stopping.Token).ConfigureAwait(false))
            {
                Remember(ConfluenceContent.Parse(e));
            }
        }
        finally
        {
            _reading.TryRemove(relative, out _);
        }
    }

    /// <summary>A page read whole not long ago, at the version a listing names (any, given none).</summary>
    private ConfluenceContent? Fresh(string id, int? version)
        => _pages.TryGetValue(id, out var kept) && Stopwatch.GetElapsedTime(kept.At) < Refresh && (version is null || version == 0 || kept.Content.Version >= version) ? kept.Content : null;

    private void Remember(ConfluenceContent content)
    {
        if (content.Storage is null)
        {
            return;
        }

        _pages[content.Id] = (content, Stopwatch.GetTimestamp());
        if (_pages.Count > PagesKept)
        {
            foreach (var old in _pages.OrderBy(p => p.Value.At).Take(PagesKept / 4).ToList())
            {
                _pages.TryRemove(old);
            }
        }
    }

    /// <summary>The names of the people a page mentions, each asked of Confluence once (by key, else by user name) and kept for the run.</summary>
    private async Task PeopleAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        var missing = keys.Where(k => !_people.ContainsKey(k)).Distinct(StringComparer.Ordinal).ToList();
        await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (key, c) =>
        {
            try
            {
                using var doc = await Api.FindAsync($"user?key={Uri.EscapeDataString(key)}", c).ConfigureAwait(false)
                    ?? await Api.FindAsync($"user?username={Uri.EscapeDataString(key)}", c).ConfigureAwait(false);
                _people[key] = doc?.RootElement.Str("displayName");
            }
            catch (VfsException ex) when (ex.Error is not VfsError.Unavailable)
            {
                _people[key] = null;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// A page folder's children: the page's Markdown (named as its folder, with <c>.md</c>), its attachments, its child
    /// pages' folders; a name taken twice numbered, the Markdown and the child pages keeping theirs.
    /// </summary>
    internal async Task<IReadOnlyList<CoNode>> PageChildrenAsync(ConfluencePageFolder folder, CancellationToken ct)
    {
        var id = folder.Content.Id;
        var pageTask = PageAsync(folder, ct);
        var childrenTask = folder.Blog ? Task.FromResult(new List<ConfluenceContent>()) : ChildPagesAsync(id, ct);
        var attachmentsTask = _settings.Attachments ? AttachmentsAsync(id, ct) : Task.FromResult(new List<ConfluenceAttachment>());
        var commentsTask = _settings.Comments ? CommentsAsync(id, ct) : Task.FromResult(new List<ConfluenceComment>());
        await Task.WhenAll(pageTask, childrenTask, attachmentsTask, commentsTask).ConfigureAwait(false);
        return await BuildPageAsync(folder.Name, await pageTask.ConfigureAwait(false), await childrenTask.ConfigureAwait(false),
            await attachmentsTask.ConfigureAwait(false), _settings.Comments ? await commentsTask.ConfigureAwait(false) : null, folder.Blog, ct).ConfigureAwait(false);
    }

    /// <summary>A page folder's children from what was read of it (alone, or with its whole space).</summary>
    private async Task<IReadOnlyList<CoNode>> BuildPageAsync(string folderName, ConfluenceContent page, IReadOnlyList<ConfluenceContent> children,
        IReadOnlyList<ConfluenceAttachment> attachments, IReadOnlyList<ConfluenceComment>? comments, bool blog, CancellationToken ct)
    {
        // child pages by title, however Confluence ordered them, so a name taken twice is numbered alike from either read
        children = children.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        var markdownName = folderName.Length == 0 ? AtlassianNames.Safe(ConfluenceDocuments.PageFolderName(page) + ".md") : folderName + ".md";
        var childNames = AtlassianNames.Unique(children.Select(ConfluenceDocuments.PageFolderName).ToList(), [markdownName]);
        var attachmentNames = AtlassianNames.Unique(attachments.Select(a => a.FileName).ToList(), childNames.Append(markdownName));
        var byFile = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < attachments.Count; i++)
        {
            byFile.TryAdd(attachments[i].FileName, attachmentNames[i]);
        }

        await PeopleAsync(ConfluenceStorage.Mentioned(page.Storage).Concat(comments?.SelectMany(c => ConfluenceStorage.Mentioned(c.Storage)) ?? []), ct).ConfigureAwait(false);
        var context = new StorageContext(Installation, page.SpaceKey, page.Id,
            file => byFile.TryGetValue(file, out var shown) ? AtlassianMarkdown.RelativeLink(shown) : null,
            key => _people.GetValueOrDefault(key));
        var nodes = new List<CoNode> { new CoText(markdownName, ConfluenceDocuments.Page(page, context, comments), page.Changed, page.Created) };
        nodes.AddRange(attachments.Select((a, i) => (CoNode)new ConfluenceAttachmentFile(a, attachmentNames[i])));
        nodes.AddRange(children.Select((c, i) => (CoNode)new ConfluencePageFolder(c, childNames[i], page.Id, blog)));
        return nodes;
    }

    /// <summary>An attachment's bytes, spooled from Confluence as they arrive and kept by its id and version.</summary>
    internal Task<IVfsFile> OpenAttachmentAsync(ConfluenceAttachment attachment, CancellationToken ct)
    {
        if (attachment.Size == 0)
        {
            return Task.FromResult<IVfsFile>(new MemoryFile([]));
        }

        var url = attachment.Download is { Length: > 0 } download
            ? Next(download)
            : new Uri(Installation, $"download/attachments/{attachment.ContainerId}/{Uri.EscapeDataString(attachment.FileName)}?api=v2");
        return Spool.OpenAsync($"confluence:{Installation.Authority}{Installation.AbsolutePath}:attachment:{attachment.Id}:v{attachment.Version}", attachment.Size,
            async c => await ResponseStream.OpenAsync(await Api.OpenAsync(url, "an attachment", c).ConfigureAwait(false), c).ConfigureAwait(false), ct);
    }

    // --- counting and walking ------------------------------------------------------------------------------------------

    /// <summary>"Count files on start": the first walk of the tree (<see cref="KeptListings"/>), each space whole at once (<see cref="ListTreeAsync"/>).</summary>
    public Task CountFilesAsync(CancellationToken ct) => Kept.CountAsync(ct);

    /// <summary>The listings as kept, for tests.</summary>
    internal ListingCache Listings => Kept.Cache;

    /// <summary>One walk now (a test's: the walks after the count come by the clock), stamps kept unless <paramref name="full"/>.</summary>
    internal Task<bool> WalkAsync(SourceCounters counters, bool full, CancellationToken ct) => Kept.WalkAsync(counters, full, ct);

    /// <summary>
    /// A folder's stamp for the walks (<see cref="KeptListingsOptions.StampOf"/>): a space's folder has its newest change
    /// and its content count by CQL (pages, blog posts, comments and attachments alike), one search with one result, so a
    /// space nothing changed in is kept whole; a page that is the source's root the same over itself and the pages below
    /// it. Any other folder none.
    /// </summary>
    internal async ValueTask<string?> StampAsync(string path, string? held, CancellationToken ct)
    {
        var cql = await ResolveAsync(path, ct).ConfigureAwait(false) switch
        {
            ConfluenceSpaceFolder space => $"space = {ConfluenceScope.Quote(space.Space.Key)}",
            ConfluencePageFolder page when path.Length == 0 => $"(id = {page.Content.Id} or ancestor = {page.Content.Id})",
            _ => null,
        };
        if (cql is null)
        {
            return null;
        }

        using var doc = await Api.GetAsync($"search?cql={Uri.EscapeDataString(cql + " order by lastmodified desc")}&limit=1&excerpt=none", ct).ConfigureAwait(false);
        var newest = doc.RootElement.Arr("results").FirstOrDefault();
        var when = newest.Str("lastModified") ?? newest.Obj("content").Obj("version").Str("when");
        var count = doc.RootElement.Has("totalSize") ? doc.RootElement.Long("totalSize") : doc.RootElement.Long("size");
        return $"{when}|{count}";
    }

    /// <summary>
    /// For a walk: a space and everything below it. Read whole (its pages with their bodies, its blog posts, its
    /// attachments and comments by CQL, the people they mention) the first time and once a day; between, only the pages
    /// whose content, comments or attachments changed since the last read (CQL's <c>lastmodified &gt;= now("-Nm")</c>),
    /// the others' listings kept as they were, unless what changed moves the tree (a page new, renamed, moved or gone),
    /// and the space is read whole. Null for any other folder, which the walk lists alone.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>?> ListTreeAsync(string path, CancellationToken ct)
    {
        if (await ResolveAsync(path, ct).ConfigureAwait(false) is not ConfluenceSpaceFolder folder)
        {
            return null;
        }

        return await Guard(async () =>
        {
            if (_spaceReads.TryGetValue(path, out var read) && DateTimeOffset.UtcNow - read.Whole < ReadWholeEvery
                && await ChangedSpaceAsync(path, folder, read, ct).ConfigureAwait(false) is { } changed)
            {
                return changed;
            }

            return await WholeSpaceAsync(path, folder, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static readonly TimeSpan ReadWholeEvery = TimeSpan.FromDays(1);

    // pages beyond which a change is cheaper read with the whole space
    private const int MostChangedPages = 100;

    /// <summary>When a space was last read whole and last read at all, and its content's count then (CQL's, every type).</summary>
    private sealed record SpaceRead(DateTimeOffset Whole, DateTimeOffset At, long Count);

    /// <summary>Where a page's folder was in the last whole read of its space: its path, its parent's id, whether a blog post's.</summary>
    private sealed record PagePlace(string Path, string? ParentId, bool Blog);

    // by space folder path, for the run (a restart reads a changed space whole once)
    private readonly ConcurrentDictionary<string, SpaceRead> _spaceReads = new(StringComparer.Ordinal);

    // by page id
    private readonly ConcurrentDictionary<string, PagePlace> _places = new(StringComparer.Ordinal);

    /// <summary>A CQL count of a space's content (every type), and how many of them were made in the last minutes.</summary>
    private async Task<long> CountAsync(string cql, CancellationToken ct)
    {
        using var doc = await Api.GetAsync($"search?cql={Uri.EscapeDataString(cql)}&limit=1&excerpt=none", ct).ConfigureAwait(false);
        return doc.RootElement.Has("totalSize") ? doc.RootElement.Long("totalSize") : doc.RootElement.Long("size");
    }

    /// <summary>
    /// A space from what is held of it and the pages changed since its last read: null where that cannot be trusted (a
    /// page the last read did not have, a renamed or moved one, a count that does not add up, too many changes), and the
    /// space is read whole.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>?> ChangedSpaceAsync(string path, ConfluenceSpaceFolder folder, SpaceRead read, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var minutes = (long)Math.Ceiling((started - read.At).TotalMinutes) + 2;
        var space = $"space = {ConfluenceScope.Quote(folder.Space.Key)}";

        // a page, comment or attachment gone leaves nothing to find by its change: the counts tell
        var count = await CountAsync(space, ct).ConfigureAwait(false);
        var made = await CountAsync($"{space} and created >= now(\"-{minutes}m\")", ct).ConfigureAwait(false);
        if (count != read.Count + made)
        {
            Logger.LogDebug("confluence: {Space} has {Count:N0} items, not {Was:N0} and {Made:N0} new: reading it whole", folder.Space.Key, count, read.Count, made);
            return null;
        }

        var affected = new Dictionary<string, ConfluenceContent?>(StringComparer.Ordinal);
        await foreach (var e in PagesAsync($"content/search?cql={Uri.EscapeDataString($"{space} and lastmodified >= now(\"-{minutes}m\")")}&limit={ItemsPerRequest}&expand=version,container,ancestors", ct).ConfigureAwait(false))
        {
            var type = e.Str("type");
            if (type is "page" or "blogpost")
            {
                var content = ConfluenceContent.Parse(e);
                if (!_places.TryGetValue(content.Id, out var place) || place.ParentId != content.ParentId
                    || !place.Path[(place.Path.LastIndexOf('/') + 1)..].Equals(ConfluenceDocuments.PageFolderName(content), StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogDebug("confluence: {Space}: the page {Id} is new, renamed or moved: reading it whole", folder.Space.Key, content.Id);
                    return null;
                }

                affected[content.Id] = null;
            }
            else if (e.Obj("container").Str("id") is { } container)
            {
                if (!_places.ContainsKey(container))
                {
                    return null;
                }

                affected.TryAdd(container, null);
            }

            if (affected.Count > MostChangedPages)
            {
                return null;
            }
        }

        // the unchanged as held; the changed listed again, at the paths they had
        var listings = new Dictionary<string, IReadOnlyList<VfsEntry>>(Kept.Cache.Under(path), StringComparer.Ordinal);
        foreach (var id in affected.Keys)
        {
            var place = _places[id];
            var name = place.Path[(place.Path.LastIndexOf('/') + 1)..];
            var page = await FindContentAsync($"content/{id}?expand={Whole}", ct).ConfigureAwait(false);
            if (page is null)
            {
                return null;
            }

            Remember(page);
            var children = place.Blog ? new List<ConfluenceContent>() : await FreshAsync(() => ChildPagesAsync(id, ct), $"children:{id}").ConfigureAwait(false);
            var attachments = _settings.Attachments ? await FreshAsync(() => AttachmentsAsync(id, ct), $"attachments:{id}").ConfigureAwait(false) : [];
            var comments = _settings.Comments ? await FreshAsync(() => CommentsAsync(id, ct), $"comments:{id}").ConfigureAwait(false) : null;
            var nodes = await BuildPageAsync(name, page, children, attachments, comments, place.Blog, ct).ConfigureAwait(false);
            listings[place.Path] = nodes.Select(n => n.Entry).ToList();
        }

        _spaceReads[path] = read with { At = started, Count = count };
        Logger.LogDebug("confluence: {Space}: {Changed:N0} pages changed since the last walk", folder.Space.Key, affected.Count);
        return listings;
    }

    /// <summary>A read through the request cache, asked afresh: what the cache held may be older than the change being read.</summary>
    private async Task<T> FreshAsync<T>(Func<Task<T>> read, string key)
    {
        Cache.Forget(key);
        return await read().ConfigureAwait(false);
    }

    /// <summary>A space read whole: its listing and every folder's below it.</summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>> WholeSpaceAsync(string path, ConfluenceSpaceFolder folder, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var key = ConfluenceScope.Quote(folder.Space.Key);
        var countAtStart = await CountAsync($"space = {key}", ct).ConfigureAwait(false);
        var pages = new List<ConfluenceContent>();
        await foreach (var e in ScanAsync(folder.Space.Key, ct).ConfigureAwait(false))
        {
            pages.Add(ConfluenceContent.Parse(e));
        }

        var posts = new List<ConfluenceContent>();
        if (_settings.BlogPosts)
        {
            await foreach (var e in PagesAsync($"content?spaceKey={Uri.EscapeDataString(folder.Space.Key)}&type=blogpost&limit={BodiesPerRequest}&expand={Whole}", ct).ConfigureAwait(false))
            {
                posts.Add(ConfluenceContent.Parse(e));
            }
        }

        var attachments = new Dictionary<string, List<ConfluenceAttachment>>(StringComparer.Ordinal);
        if (_settings.Attachments)
        {
            await foreach (var e in PagesAsync($"content/search?cql={Uri.EscapeDataString($"space = {key} and type = attachment")}&limit={ItemsPerRequest}&expand=version,container", ct).ConfigureAwait(false))
            {
                var attachment = ConfluenceAttachment.Parse(e);
                if (attachment.ContainerId is { } container)
                {
                    (attachments.TryGetValue(container, out var list) ? list : attachments[container] = []).Add(attachment);
                }
            }
        }

        var comments = new Dictionary<string, List<ConfluenceComment>>(StringComparer.Ordinal);
        if (_settings.Comments)
        {
            await foreach (var e in PagesAsync($"content/search?cql={Uri.EscapeDataString($"space = {key} and type = comment")}&limit={BodiesPerRequest}&expand=body.storage,version,history,container,ancestors,extensions.inlineProperties", ct).ConfigureAwait(false))
            {
                var comment = ConfluenceComment.Parse(e);
                if (comment.ContainerId is { } container)
                {
                    (comments.TryGetValue(container, out var list) ? list : comments[container] = []).Add(comment);
                }
            }
        }

        foreach (var page in pages.Concat(posts))
        {
            Remember(page);
        }

        var byId = pages.GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byParent = pages.GroupBy(p => p.ParentId is { } parent && byId.ContainsKey(parent) ? parent : "").ToDictionary(g => g.Key, g => g.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.Ordinal);
        var listings = new Dictionary<string, IReadOnlyList<VfsEntry>>(StringComparer.Ordinal);

        async Task ListPageAsync(string pagePath, string name, ConfluenceContent page, bool blog)
        {
            var children = blog ? [] : byParent.GetValueOrDefault(page.Id) ?? [];
            var nodes = await BuildPageAsync(name, page, children,
                (attachments.GetValueOrDefault(page.Id) ?? []).OrderBy(a => a.Id.Length).ThenBy(a => a.Id, StringComparer.Ordinal).ToList(),
                _settings.Comments ? (comments.GetValueOrDefault(page.Id) ?? []).OrderBy(c => c.Created ?? DateTimeOffset.MinValue).ToList() : null,
                blog, ct).ConfigureAwait(false);
            listings[pagePath] = nodes.Select(n => n.Entry).ToList();
            _places[page.Id] = new PagePlace(pagePath, page.ParentId, blog);
            foreach (var child in nodes.OfType<ConfluencePageFolder>())
            {
                await ListPageAsync(VfsPath.Join(pagePath, child.Name), child.Name, byId.GetValueOrDefault(child.Content.Id) ?? child.Content, blog).ConfigureAwait(false);
            }
        }

        var top = (byParent.GetValueOrDefault("") ?? []).OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        var reserved = _settings.BlogPosts && posts.Count > 0 ? new[] { ConfluenceDocuments.BlogFolder } : [];
        var topNames = AtlassianNames.Unique(top.Select(ConfluenceDocuments.PageFolderName).ToList(), reserved);
        var rootEntries = new List<VfsEntry>();
        for (var i = 0; i < top.Count; i++)
        {
            rootEntries.Add(new ConfluencePageFolder(top[i], topNames[i], null).Entry);
            await ListPageAsync(VfsPath.Join(path, topNames[i]), topNames[i], top[i], blog: false).ConfigureAwait(false);
        }

        if (reserved.Length > 0)
        {
            var blogPath = VfsPath.Join(path, ConfluenceDocuments.BlogFolder);
            rootEntries.Add(new ConfluenceBlogFolder(folder.Space).Entry);
            var postNames = AtlassianNames.Unique(posts.Select(ConfluenceDocuments.PageFolderName).ToList());
            listings[blogPath] = posts.Select((p, i) => new ConfluencePageFolder(p, postNames[i], null, blog: true).Entry).ToList();
            for (var i = 0; i < posts.Count; i++)
            {
                await ListPageAsync(VfsPath.Join(blogPath, postNames[i]), postNames[i], posts[i], blog: true).ConfigureAwait(false);
            }
        }

        listings[path] = rootEntries;
        _spaceReads[path] = new SpaceRead(started, started, countAtStart);
        Logger.LogDebug("confluence: read the space {Space} whole: {Pages:N0} pages, {Posts:N0} blog posts", folder.Space.Key, pages.Count, posts.Count);
        return listings;
    }

    /// <summary>
    /// A space's pages with their bodies and ancestors: the scan's cursor pages (Confluence 7.18+), else the content list's
    /// offset pages.
    /// </summary>
    private async IAsyncEnumerable<JsonElement> ScanAsync(string space, [EnumeratorCancellation] CancellationToken ct)
    {
        var scan = $"content/scan?spaceKey={Uri.EscapeDataString(space)}&type=page&limit={BodiesPerRequest}&expand={Whole}";
        var list = $"content?spaceKey={Uri.EscapeDataString(space)}&type=page&limit={BodiesPerRequest}&expand={Whole}";
        var useScan = true;
        try
        {
            using var probe = await Api.GetAsync($"content/scan?spaceKey={Uri.EscapeDataString(space)}&type=page&limit=1", ct).ConfigureAwait(false);
        }
        catch (VfsException ex) when (ex.Error is VfsError.NotFound or VfsError.IoError)
        {
            useScan = false;
        }

        await foreach (var e in PagesAsync(useScan ? scan : list, ct).ConfigureAwait(false))
        {
            yield return e;
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
