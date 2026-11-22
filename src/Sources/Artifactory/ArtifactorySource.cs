using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Artifactory;

/// <summary>
/// A JFrog Artifactory server, a repository on it or a folder in a repository (type <c>artifactory</c>, spec 7.13),
/// read-only: a folder per repository the account can see, and in each the folders and files it holds, as Artifactory
/// names them. Names are compared in either case, as Windows does. Folders are listed through Artifactory's query
/// language, a folder a request with its files' sizes, times and checksums, and a folder with everything below it in one
/// request where that is not too much; where the server keeps the query language from the account (it does from anyone
/// not signed in), through its storage calls, a request a folder and one more a file. Folder listings are kept (the SDK's
/// <see cref="KeptListings"/>), and a change shows when a walk comes to it: a walk asks each repository what was added or
/// changed since the last, and lists again only the folders that holds. Files are read in ranges, small ones whole.
/// </summary>
public sealed partial class ArtifactorySource : SourceBase, ICountable
{
    // a file up to this long is fetched whole and kept for a while (many small reads of it then ask nothing); a longer one is read in ranges
    private const long SpoolBelow = 8L << 20;

    // what a listing says of each item
    private const string Fields = "\"repo\",\"path\",\"name\",\"type\",\"size\",\"created\",\"modified\",\"actual_sha1\"";

    private readonly ArtifactorySettings _settings;
    private readonly ArtifactoryStandIn? _standIn;
    private readonly CancellationTokenSource _stopping = new();
    private ArtifactorySession? _session;
    private ArtifactoryApi? _api;
    private RequestCache? _cache;
    private KeptListings? _kept;
    private SpoolCache? _spool;
    private ArtRepository? _only;
    private string _within = "";
    private volatile bool _queries = true;
    private volatile bool _listRefused;
    private volatile bool _rangeIgnoredSaid;

    public ArtifactorySource(ArtifactorySettings settings)
        : this(settings, standIn: null)
    {
    }

    internal ArtifactorySource(ArtifactorySettings settings, ArtifactoryStandIn? standIn)
    {
        _settings = settings;
        _standIn = standIn;
    }

    public override SourceCapabilities Capabilities => new(CanWrite: false, CaseInsensitive: true);

    internal ArtifactorySettings Settings => _settings;

    internal ArtifactoryApi Api => _api ?? throw VfsException.Unavailable("Artifactory is not signed in to yet");

    /// <summary>The session (for tests).</summary>
    internal ArtifactorySession Session => _session ?? throw new InvalidOperationException("the source has not started");

    /// <summary>What the address names, once started.</summary>
    internal ArtifactoryScope Scope { get; private set; }

    /// <summary>Whether the server answers the account's queries (its query language): off, folders are listed through its storage calls.</summary>
    internal bool Queries => _queries;

    /// <summary>
    /// How many items a folder and all below it may hold to be read in one request (<see cref="ListTreeAsync"/>); one that
    /// holds more is listed alone, and each folder in it tried the same way.
    /// </summary>
    internal int TreeMost { get; set; } = 50_000;

    /// <summary>How many items of a repository may have changed since a walk for the next to list only their folders; past that the repository is read whole again.</summary>
    internal int ChangesMost { get; set; } = 50_000;

    private RequestCache Cache => _cache ?? throw VfsException.Unavailable("Artifactory is not signed in to yet");

    private KeptListings Kept => _kept ?? throw VfsException.Unavailable("Artifactory is not signed in to yet");

    private SpoolCache Spool => _spool ?? throw VfsException.Unavailable("Artifactory is not signed in to yet");

    private TimeSpan Refresh => _settings.Refresh > TimeSpan.Zero ? _settings.Refresh : TimeSpan.FromSeconds(2);

    private string Host => Session.Address.Base.Host;

    protected override async Task OnInitialiseAsync(CancellationToken ct)
    {
        _session = new ArtifactorySession(_settings, Logger, _standIn, Context.Secrets, Context.UpstreamWatcher);
        await _session.SignInAsync(interactive: Context.Attended, message => Context.ReportProgress(message), ct).ConfigureAwait(false);
        _api = new ArtifactoryApi(_session, _settings.Requests, Logger);
        _cache = new RequestCache(_stopping.Token);
        _spool = new SpoolCache(Path.Combine(Context.CacheDirectory, "spool"), logger: Logger);

        var address = _session.Address;
        Scope = address.Scope;
        Context.ReportProgress(Scope == ArtifactoryScope.Server ? $"Opening {address.Base.Host}" : $"Opening {address.Repository}{(address.Path.Length == 0 ? "" : "/" + address.Path)}");
        try
        {
            await OpenScopeAsync(address, ct).ConfigureAwait(false);
            await AskWhatTheServerTakesAsync(ct).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            throw new SourceFailedException($"Opening {(Scope == ArtifactoryScope.Server ? address.Base.Host : address.Repository)}: {ex.Message}", permanent: ex.Error is VfsError.AccessDenied or VfsError.NotFound, ex);
        }

        // a server's walk goes through "Repositories at a time" repositories at once, each a part of its own with "Requests
        // at a time per repository" of its folders listed together; one repository's walk is one part with the same
        var stamps = _settings.Keeps && _queries;
        _kept = new KeptListings(ListFolderAsync, Counters, Logger, new KeptListingsOptions
        {
            Keep = _settings.Keeps,

            // not kept, a listing lasts the refresh interval: a path is found through its folders' listings
            UnkeptFor = Refresh,
            WalkEvery = _settings.WalkEvery,
            Parallelism = _settings.PerRepository,
            MaxInFlight = Scope == ArtifactoryScope.Server ? _settings.Requests : _settings.PerRepository,
            PartsBelow = Scope == ArtifactoryScope.Server ? (path, _) => ValueTask.FromResult(path.Length == 0) : null,
            PartsAtOnce = _settings.Repositories,
            ListTree = _queries ? ListTreeAsync : null,
            StampOf = stamps ? StampAsync : null,
            StampWholeTree = stamps,
            KeepFailures = true,
            Name = "artifactory",

            // the listings last run saw open at once at the next start: for the same server, account and scope, the same
            // settings that shape the tree, the same way of asking (a listing through the storage calls has no stamps), and
            // what the entries keep in the same form
            SaveTo = Path.Combine(Context.StateDirectory, "listings.json.gz"),
            SaveKey = $"{address.Base}|{_session.Account}|{_only?.Key}/{_within}|{_settings.Shape}|{(_queries ? "aql" : "storage")}|keys{ArtifactoryNames.Format}",
        });

        _session.Renewed += _kept.Cache.ForgetFailures;
        _kept.StartWalksOfHeld();
        Logger.LogInformation("artifactory: serving {Scope} {What} on {Server} as {User}", Scope.ToString().ToLowerInvariant(),
            Scope == ArtifactoryScope.Server ? "" : $"{_only!.Key}/{_within}".TrimEnd('/'), address.Base, _session.Who);
    }

    /// <summary>What the address names, found on the server: the repository, and in it the folder (a file's address is cut back to its folder).</summary>
    private async Task OpenScopeAsync(ArtifactoryAddress address, CancellationToken ct)
    {
        if (Scope == ArtifactoryScope.Server)
        {
            return;
        }

        var repositories = await RepositoriesAsync(ct).ConfigureAwait(false);
        _only = Named(repositories, address.Repository)
            ?? throw new VfsException(VfsError.NotFound, $"Artifactory ({address.Base.Host}) has no repository {address.Repository} that {_session!.Who} can see");
        _within = address.Path;
        if (_within.Length == 0)
        {
            return;
        }

        using var doc = await Api.FindAsync($"storage/{ArtifactoryAddress.Escaped(_only.StorageKey, _within)}", ct).ConfigureAwait(false)
            ?? throw new VfsException(VfsError.NotFound, $"the repository {_only.Key} has no {_within} that {_session!.Who} can see");
        if (!doc.RootElement.TryGetProperty("children", out _))
        {
            // a file's address: its folder
            var slash = _within.LastIndexOf('/');
            _within = slash < 0 ? "" : _within[..slash];
            Scope = _within.Length == 0 ? ArtifactoryScope.Repository : ArtifactoryScope.Folder;
        }
    }

    /// <summary>
    /// Whether the server answers the account's queries, asked once with one that finds nothing: Artifactory keeps its query
    /// language from anyone not signed in, and an edition without it has no such call.
    /// </summary>
    private async Task AskWhatTheServerTakesAsync(CancellationToken ct)
    {
        const string nothing = "items.find({\"repo\":\"anyfs-asks-nothing\",\"name\":\"anyfs-asks-nothing\"}).include(\"name\",\"repo\",\"path\")";
        try
        {
            await Api.AqlAsync(nothing, 1, "a query", ct).ConfigureAwait(false);
        }
        catch (VfsException ex) when (ex.Error is VfsError.AccessDenied or VfsError.NotSupported or VfsError.NotFound)
        {
            _queries = false;
            Logger.LogInformation("artifactory: {Host} does not answer queries from {Who} ({Error}); folders are listed through its storage calls, a request a folder and one more a file where it has no file list", Host, _session!.Who, ex.Message);
        }
    }

    // --- the VFS -------------------------------------------------------------------------------------------------------

    public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var found = await FindAsync(path, ct).ConfigureAwait(false);
        if (found.Entry is { IsDirectory: false })
        {
            throw VfsException.NotADirectory(path);
        }

        foreach (var entry in await Kept.Cache.GetAsync(found.Path, ct).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    /// <summary>A file or folder from its folder's listing (kept, or asked for), the root as it is.</summary>
    public override async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        try
        {
            var found = await FindAsync(path, ct).ConfigureAwait(false);
            return found.Entry ?? VfsEntry.Directory("");
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
            throw VfsException.NotSupported("writing to Artifactory");
        }

        var found = await FindAsync(path, ct).ConfigureAwait(false);
        if (found.Entry is not { IsDirectory: false } entry || found.Repository is not { } repository)
        {
            throw VfsException.IsADirectory(path);
        }

        if (entry.Length == 0)
        {
            return new MemoryFile([]);
        }

        // read under the key its listing came from: a remote repository's cache, so a read fetches nothing from the other server
        var url = Session.Address.File(repository.StorageKey, found.Own);
        var what = $"{repository.Key}/{found.Own}";
        if (entry.Length <= SpoolBelow)
        {
            // by what it holds where the listing told (the same file in two repositories is one download), else by where and when
            var key = $"artifactory:{Session.Address.Base.Authority}{Session.Address.Base.AbsolutePath}:"
                + (entry.ETag is { Length: > 0 } sha1 ? $"sha1:{sha1}:{entry.Length}" : $"{repository.StorageKey}/{found.Own}@{entry.Modified?.UtcTicks}:{entry.Length}");
            return await Spool.OpenAsync(key, entry.Length,
                async c => await ResponseStream.OpenAsync(await Guard(() => Api.OpenAsync(url, 0, what, c)).ConfigureAwait(false), c).ConfigureAwait(false), ct).ConfigureAwait(false);
        }

        return new RandomAccessOverStream(entry.Length, async (offset, c) =>
        {
            var response = await Guard(() => Api.OpenAsync(url, offset, what, c)).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                response.Dispose();
                return Stream.Null;
            }

            // a server (or a proxy before it) that ignores the range sends the whole file: read past the start
            return await ResponseStream.FromOffsetAsync(response, offset, path, () =>
            {
                if (!_rangeIgnoredSaid)
                {
                    _rangeIgnoredSaid = true;
                    Logger.LogInformation("artifactory: {Host} sends '{Path}' whole when asked for a range; reading past the first {Offset:N0} bytes", Host, path, offset);
                }
            }, c).ConfigureAwait(false);
        }, maxStreams: Math.Clamp(_settings.PerRepository, 1, 16), maxSkipBytes: 1 << 20);
    }

    // --- finding a path --------------------------------------------------------------------------------------------------

    /// <summary>
    /// A path of the source, found: as its folders' listings write it (a client may ask in another case), the repository it
    /// is in and its own path there (null and empty for the server's root), and its entry (null for the source's root).
    /// </summary>
    private sealed record Found(string Path, ArtRepository? Repository, string Own, VfsEntry? Entry, string Root);

    /// <summary>
    /// Finds a path a folder at a time from the root, each step from its folder's listing (kept, or asked for): a name shows
    /// as Windows takes it and in either case, and its entry carries what Artifactory calls it where that is another.
    /// </summary>
    private async Task<Found> FindAsync(string path, CancellationToken ct)
    {
        var segments = VfsPath.Split(VfsPath.Normalize(path));
        var at = "";
        var own = _within;
        var repository = _only;
        var from = 0;
        if (Scope == ArtifactoryScope.Server)
        {
            if (segments.Length == 0)
            {
                return new Found("", null, "", null, "");
            }

            // from the root's listing, kept as the rest are: the repositories list is asked when the root is listed, and only then
            var top = await Kept.Cache.StatAsync(segments[0], ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
            repository = ArtRepository.Of(top);
            at = top.Name;
            own = "";
            from = 1;
            if (segments.Length == 1)
            {
                return new Found(at, repository, own, top, at);
            }
        }

        var root = at;
        VfsEntry? entry = null;
        for (var i = from; i < segments.Length; i++)
        {
            if (entry is { IsDirectory: false })
            {
                throw VfsException.NotADirectory(path);
            }

            entry = await Kept.Cache.StatAsync(VfsPath.Join(at, segments[i]), ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
            at = VfsPath.Join(at, entry.Name);
            own = own.Length == 0 ? ArtifactoryNames.Own(entry) : own + "/" + ArtifactoryNames.Own(entry);
        }

        return new Found(at, repository, own, entry, root);
    }

    /// <summary>A repository by its key in either case; a remote repository by its cache's key too (the tree in Artifactory's own pages shows that one).</summary>
    private static ArtRepository? Named(IReadOnlyList<ArtRepository> repositories, string key)
        => repositories.FirstOrDefault(r => r.Key.Equals(key, StringComparison.Ordinal))
            ?? repositories.FirstOrDefault(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? (key.EndsWith(ArtRepository.CacheSuffix, StringComparison.OrdinalIgnoreCase)
                ? repositories.FirstOrDefault(r => r.IsRemote && r.Key.Equals(key[..^ArtRepository.CacheSuffix.Length], StringComparison.OrdinalIgnoreCase))
                : null);

    // --- what the folders ask for ----------------------------------------------------------------------------------------

    /// <summary>
    /// Every repository the account can see, by key, asked again once the refresh interval has passed; the first time, what
    /// the sign-in's check has just read, where it is that fresh.
    /// </summary>
    internal Task<List<ArtRepository>> RepositoriesAsync(CancellationToken ct)
        => Cache.GetAsync("repositories", Refresh, async c =>
        {
            var repositories = Session.TakeRepositories(Refresh);
            if (repositories is null)
            {
                using var doc = await Api.GetAsync("repositories", c).ConfigureAwait(false);
                repositories = ArtRepository.ListOf(doc.RootElement);
            }

            Logger.LogInformation("artifactory: {User} can see {Count:N0} repositories", Session.Who, repositories.Count);
            return repositories;
        }, ct);

    /// <summary>The repositories a server's root shows: those of the kinds the settings ask for.</summary>
    private async Task<List<ArtRepository>> ShownAsync(CancellationToken ct)
        => (await RepositoriesAsync(ct).ConfigureAwait(false)).Where(r => r.IsRemote ? _settings.RemoteRepositories : r.IsVirtual ? _settings.VirtualRepositories : _settings.LocalRepositories).ToList();

    /// <summary>A folder's listing from Artifactory (what <see cref="KeptListings"/> keeps): a server's root its repositories, any other folder its items.</summary>
    private async IAsyncEnumerable<VfsEntry> ListFolderAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var found = await FindAsync(path, ct).ConfigureAwait(false);
        if (found.Repository is not { } repository)
        {
            var shown = await Guard(() => ShownAsync(ct)).ConfigureAwait(false);
            var names = SafeNames.Unique(shown.Select(r => r.Key).ToList());
            for (var i = 0; i < shown.Count; i++)
            {
                yield return shown[i].Entry(names[i]);
            }

            yield break;
        }

        if (found.Entry is { IsDirectory: false })
        {
            throw VfsException.NotADirectory(path);
        }

        if (found.Path == found.Root)
        {
            Volatile.Read(ref _counted)?.Reached(repository);
        }

        IReadOnlyList<VfsEntry> entries;
        try
        {
            entries = ArtifactoryNames.Entries(await Guard(() => FolderAsync(repository, found.Own, ct)).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NoteFailure(found.Root, ex);
            throw;
        }

        foreach (var entry in entries)
        {
            yield return entry;
        }
    }

    /// <summary>
    /// A folder's own items: through the query language, or through the storage calls, for an account the server keeps the
    /// query language from, and for a virtual repository, which only the storage calls show as the one tree it is (a query
    /// gives each member's items apart, a folder they share once for each).
    /// </summary>
    private Task<List<ArtItem>> FolderAsync(ArtRepository repository, string path, CancellationToken ct)
        => Asked(repository) ? FolderByQueryAsync(repository, path, ct) : FolderByStorageAsync(repository, path, ct);

    /// <summary>Whether a repository's folders are listed through the query language: where the server answers the account's queries, and not for a virtual repository.</summary>
    private bool Asked(ArtRepository repository) => _queries && !repository.IsVirtual;

    /// <summary>A text as the query language takes one: in quotes, a quote or a backslash in it escaped, anything else as it is.</summary>
    internal static string Quoted(string text) => "\"" + JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(text) + "\"";

    /// <summary>The query for a folder's own items, folders and files alike (the repository's root is the path <c>.</c>).</summary>
    private static string FolderQuery(ArtRepository repository, string path)
        => $"items.find({{\"repo\":{Quoted(repository.StorageKey)},\"path\":{Quoted(path.Length == 0 ? "." : path)},\"type\":\"any\"}}).include({Fields})";

    /// <summary>
    /// A folder's own items through the query language: one request, whatever it holds. Where the server cuts the answer at
    /// a limit of its own (a hosted Artifactory's half a million items), the folder is listed through the storage calls,
    /// which have none.
    /// </summary>
    private async Task<List<ArtItem>> FolderByQueryAsync(ArtRepository repository, string path, CancellationToken ct)
    {
        var answer = await Api.AqlAsync(FolderQuery(repository, path), int.MaxValue, $"the listing of {repository.Key}/{path}", ct).ConfigureAwait(false);
        if (!answer.More)
        {
            return answer.Items;
        }

        Logger.LogInformation("artifactory: {Host} cut its answer for {Repository}/{Path} short at {Count:N0} items; listing it through the storage calls", Host, repository.Key, path, answer.Items.Count);
        return await FolderByStorageAsync(repository, path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A folder's own items through the storage calls, for an account the server keeps its query language from: the folder's
    /// children by name, then their sizes and times from the file list call (one request) where the server has it for the
    /// account, else from each file's own (a request a file, a few at once).
    /// </summary>
    private async Task<List<ArtItem>> FolderByStorageAsync(ArtRepository repository, string path, CancellationToken ct)
    {
        var address = ArtifactoryAddress.Escaped(repository.StorageKey, path);
        List<(string Name, bool Folder)> children;
        using (var doc = await Api.GetAsync($"storage/{address}", ct).ConfigureAwait(false))
        {
            if (!doc.RootElement.TryGetProperty("children", out _))
            {
                throw new VfsException(VfsError.NotADirectory, $"{repository.Key}/{path} is a file, not a folder");
            }

            children = doc.RootElement.Arr("children").Select(c => ((c.Str("uri") ?? "").Trim('/'), c.Bool("folder"))).Where(c => c.Item1.Length > 0).ToList();
        }

        if (!_listRefused && children.Any(c => !c.Folder))
        {
            try
            {
                using var list = await Api.GetAsync($"storage/{address}?list&deep=0&listFolders=1", ct).ConfigureAwait(false);
                return list.RootElement.Arr("files").Select(e => ArtItem.ParseListed(e, path)).OfType<ArtItem>().ToList();
            }
            catch (VfsException ex) when (ex.Error is VfsError.AccessDenied or VfsError.NotSupported)
            {
                _listRefused = true;
                Logger.LogInformation("artifactory: {Host} has no file list for {Who} ({Error}); each file's size is asked for on its own", Host, Session.Who, ex.Message);
            }
        }

        var items = new ArtItem?[children.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, children.Count), new ParallelOptions { MaxDegreeOfParallelism = _settings.PerRepository, CancellationToken = ct }, async (i, c) =>
        {
            var (name, folder) = children[i];
            if (folder)
            {
                items[i] = new ArtItem(path, name, true, 0, null, null, null);
                return;
            }

            // one gone since the folder told of it is left out
            using var info = await Api.FindAsync($"storage/{ArtifactoryAddress.Escaped(repository.StorageKey, path.Length == 0 ? name : path + "/" + name)}", c).ConfigureAwait(false);
            if (info is not null)
            {
                var root = info.RootElement;
                items[i] = new ArtItem(path, name, false, Math.Max(0, root.Long("size")), root.Date("created"), root.Date("lastModified"), ArtItem.Hex(root.Obj("checksums").Str("sha1")));
            }
        }).ConfigureAwait(false);
        return items.OfType<ArtItem>().ToList();
    }

    // --- stopping ------------------------------------------------------------------------------------------------------

    public override async ValueTask DisposeAsync()
    {
        // the walks stop first, and what they kept is written down; then what they asked through goes
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
