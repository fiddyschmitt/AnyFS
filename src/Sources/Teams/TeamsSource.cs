using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Teams;

/// <summary>
/// A Microsoft Teams account (type <c>teams</c>, spec 7.9), read-only: the account's teams and their channels, each
/// channel's posts and each chat as Markdown, over Microsoft's own Teams services (the live path signs in with the web
/// session; tests use a stand-in). Folder listings are kept (the SDK's <see cref="KeptListings"/>) and "Count files on
/// start" walks the teams, channels and chats; a conversation's messages are read when it is opened.
/// </summary>
public sealed partial class TeamsSource : SourceBase, ICountable
{
    private readonly TeamsSettings _settings;
    private readonly TeamsStandIn? _standIn;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    // a chat's folder name, fixed when first given: the names learned later (a sender's, from the chat's messages) would
    // otherwise rename a chat a kept listing still shows under its first name
    private readonly ConcurrentDictionary<string, string> _chatNames = new(StringComparer.Ordinal);

    // the Markdown a listing measured, by its path: an open serves the bytes of the length the listing reported, not a
    // rebuild from a conversation that has moved on since (the last two builds, so a refresh landing between a stat and an
    // open still finds the one the stat saw)
    private readonly ConcurrentDictionary<string, (byte[] Latest, byte[]? Previous)> _served = new(StringComparer.OrdinalIgnoreCase);
    private ITeamsApi? _api;
    private TeamsCache? _cache;
    private KeptListings? _kept;
    private RootFolder? _root;
    private bool _degraded;

    public TeamsSource(TeamsSettings settings)
        : this(settings, standIn: null)
    {
    }

    internal TeamsSource(TeamsSettings settings, TeamsStandIn? standIn)
    {
        _settings = settings;
        _standIn = standIn;
    }

    public override SourceCapabilities Capabilities => new(CanWrite: false, CaseInsensitive: true);

    internal TeamsSettings Settings => _settings;

    internal ITeamsApi Api => _api ?? throw VfsException.Unavailable("Teams is not signed in to yet");

    private TeamsCache Cache => _cache ?? throw VfsException.Unavailable("Teams is not signed in to yet");

    private KeptListings Kept => _kept ?? throw VfsException.Unavailable("Teams is not signed in to yet");

    protected override async Task OnInitialiseAsync(CancellationToken ct)
    {
        _api = _standIn?.Api ?? await SignInAsync(ct).ConfigureAwait(false);
        _cache = new TeamsCache(_stopping.Token);
        _root = new RootFolder();

        var requests = Math.Clamp(_settings.Concurrency, 1, 32);
        _kept = new KeptListings(ListFolderAsync, Counters, Logger, new KeptListingsOptions
        {
            Keep = _settings.Keeps,
            UnkeptFor = TimeSpan.Zero,
            RefreshAfter = _settings.Refresh,
            WalkEvery = _settings.WalkEvery,
            Parallelism = requests,
            MaxInFlight = requests,
            LeftOut = LeftOutAsync,
            Name = "teams",
        });
        _kept.StartWalks();
        Logger.LogInformation("teams: serving {Account}", _api.Account);
    }

    /// <summary>
    /// The live web session (spec 7.9): a browser of AnyFS's own, on a profile per account, signs in to Teams; its tokens
    /// for the chat service, the aggregator and the middle tier are read and used to call those services. One interactive
    /// sign-in now, so a lapsed session shows a window at the start, as the other web sources do.
    /// </summary>
    private async Task<ITeamsApi> SignInAsync(CancellationToken ct)
    {
        var cloud = _settings.TheCloud;

        // as the other web sessions: the test variable wins, then the setting (which must exist), then whatever is installed
        var browser = BrowserLocator.Find(_settings.Browser, "ANYFS_TEAMS_BROWSER")
            ?? throw new SourceFailedException(_settings.Browser is { } set
                ? $"The browser in the source's Browser setting was not found: {set}."
                : "No Edge, Chrome or Chromium was found for the Teams web session; set one in the source's Browser setting.", permanent: true);
        var profile = WebSignInTarget.ProfileFor("teams-browser", _settings.AccountHint, "ANYFS_TEAMS_PROFILES");
        var session = new TeamsWebSession(new Uri(cloud.TeamsHost), browser, profile, _settings.AccountHint, Logger);
        try
        {
            await session.TokenAsync("https://chatsvcagg.teams.microsoft.com", interactive: true, m => Context.ReportProgress(m), ct).ConfigureAwait(false);
        }
        catch (SignInRequiredException ex)
        {
            throw new SourceFailedException(ex.Message, permanent: ex.Permanent, ex);
        }

        return new TeamsRest(new Uri(cloud.ApiHost), (aud, c) => Renew(session, aud, c), _settings.Concurrency, Logger)
        {
            Account = session.Account ?? _settings.AccountHint,
            Mri = session.Mri,
        };
    }

    private async Task<string> Renew(TeamsWebSession session, string audience, CancellationToken ct)
    {
        try
        {
            return await session.TokenAsync(audience, interactive: false, _ => { }, ct).ConfigureAwait(false);
        }
        catch (SignInRequiredException ex)
        {
            MarkDegraded(ex.Message);
            throw VfsException.Unavailable(ex.Message);
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

    public override async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        var normalized = VfsPath.Normalize(path);
        if (normalized.Length == 0)
        {
            return VfsEntry.Directory("");
        }

        return await Kept.Cache.StatAsync(normalized, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false);
    }

    public override async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        if (mode.IsWrite())
        {
            throw VfsException.NotSupported("writing to Teams");
        }

        // a conversation's Markdown as the listing measured it: the bytes of the length the client was told
        var normalized = VfsPath.Normalize(path);
        if (_served.TryGetValue(normalized, out var served))
        {
            var length = (await Kept.Cache.StatAsync(normalized, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false))?.Length;
            var bytes = served.Previous is { } previous && length == previous.Length && length != served.Latest.Length ? previous : served.Latest;
            return MarkdownFile.Open(bytes);
        }

        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        return node is TmFile file ? await Guard(() => file.OpenAsync(this, ct)).ConfigureAwait(false) : throw VfsException.IsADirectory(path);
    }

    /// <summary>A folder's listing (what <see cref="KeptListings"/> keeps); the Markdown files' bytes are kept beside it, for the opens.</summary>
    private async IAsyncEnumerable<VfsEntry> ListFolderAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        if (node is not TmFolder folder)
        {
            throw VfsException.NotADirectory(path);
        }

        foreach (var child in await Guard(() => folder.ListAsync(this, ct)).ConfigureAwait(false))
        {
            if (child is MarkdownFile markdown)
            {
                Serve(VfsPath.Join(VfsPath.Normalize(path), child.Name), markdown.Content);
            }

            yield return child.Entry;
        }
    }

    private void Serve(string path, byte[] content)
    {
        if (_served.Count > 50_000)
        {
            _served.Clear();
        }

        _served.AddOrUpdate(path, _ => (content, null), (_, kept) => kept.Latest.AsSpan().SequenceEqual(content) ? kept : (content, kept.Latest));
    }

    private async Task<TmNode> ResolveAsync(string path, CancellationToken ct)
    {
        TmNode node = _root ?? throw VfsException.Unavailable("Teams is not signed in to yet");
        var normalized = VfsPath.Normalize(path);
        if (normalized.Length == 0)
        {
            return node;
        }

        foreach (var segment in VfsPath.Split(normalized))
        {
            if (node is not TmFolder folder)
            {
                throw VfsException.NotADirectory(path);
            }

            var children = await Guard(() => folder.ListAsync(this, ct)).ConfigureAwait(false);
            node = children.FirstOrDefault(c => c.Name.Equals(segment, StringComparison.OrdinalIgnoreCase)) ?? throw VfsException.NotFound(path);
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

    internal async Task<IReadOnlyList<TeamInfo>> TeamsAsync(CancellationToken ct) => (await StructureAsync(ct).ConfigureAwait(false)).Teams;

    internal async Task<IReadOnlyList<ChatInfo>> ChatsAsync(CancellationToken ct) => (await StructureAsync(ct).ConfigureAwait(false)).Chats;

    /// <summary>
    /// The teams and the chats, from one aggregator answer kept for the refresh interval; the chats changed within the
    /// "within" window only, and their members' names learned (a hundred a call; a call that fails leaves those as mris).
    /// </summary>
    private Task<TeamsStructure> StructureAsync(CancellationToken ct)
        => Cache.GetAsync("structure", _settings.Refresh, async c =>
        {
            var structure = await Api.StructureAsync(c).ConfigureAwait(false);
            Remember(structure.Teams.SelectMany(t => t.Channels).Select(ch => (ch.Id, ch.DisplayName)));
            var cutoff = Cutoff;
            var chats = cutoff is { } since ? structure.Chats.Where(ch => ch.LastUpdated is null || ch.LastUpdated >= since).ToList() : structure.Chats;

            // the people a chat is named for: learn their names once, so a 1:1 or untitled group is named, not an mri
            var mris = chats.SelectMany(ch => ch.MemberMris).Where(m => m != Api.Mri && !_names.ContainsKey(m)).Distinct().ToList();
            foreach (var batch in mris.Chunk(100))
            {
                try
                {
                    var people = await Api.PeopleAsync(batch, c).ConfigureAwait(false);
                    Remember(people.Select(p => (p.Mri, p.DisplayName)));
                }
                catch (VfsException ex)
                {
                    Logger.LogInformation("teams: {Count} people's names could not be read ({Error}); their chats are named by id", batch.Length, ex.Message);
                }
            }

            return structure with { Chats = chats };
        }, ct);

    /// <summary>The oldest a conversation's last message may be to show ("within" days), or null for all.</summary>
    private DateTimeOffset? Cutoff => _settings.Within is { } within ? DateTimeOffset.UtcNow - within : null;

    /// <summary>A channel's posts: its content messages grouped into threads (the root first, then its replies in time order), newest thread first; within the "within" window.</summary>
    internal Task<IReadOnlyList<IReadOnlyList<TeamsMessage>>> ThreadsAsync(string channelId, CancellationToken ct)
        => Cache.GetAsync($"threads:{channelId}", _settings.Refresh, async c =>
        {
            var messages = await ConversationAsync(channelId, c).ConfigureAwait(false);
            LearnNames(messages);
            var cutoff = Cutoff;
            var threads = messages.Where(m => m.IsContent)
                .GroupBy(m => m.ThreadId)
                .Select(g => (IReadOnlyList<TeamsMessage>)g.OrderBy(m => m.Composed ?? DateTimeOffset.MinValue).ThenBy(m => m.Id, StringComparer.Ordinal).ToList())
                .Where(t => t.Count > 0 && (cutoff is null || t[^1].Composed is null || t[^1].Composed >= cutoff))
                .OrderByDescending(t => t[^1].Composed ?? DateTimeOffset.MinValue)
                .ToList();
            return (IReadOnlyList<IReadOnlyList<TeamsMessage>>)threads;
        }, ct);

    internal async Task<byte[]> ChatMarkdownAsync(ChatInfo chat, CancellationToken ct)
    {
        var messages = await Cache.GetAsync($"chat:{chat.Id}", _settings.Refresh, async c =>
        {
            var all = await ConversationAsync(chat.Id, c).ConfigureAwait(false);
            LearnNames(all);
            return all;
        }, ct).ConfigureAwait(false);
        return TeamsText.Chat(messages, ChatName(chat), NameOf);
    }

    /// <summary>A conversation's messages, newest page first and back as far as the "within" window (or 2,000 messages) reaches.</summary>
    private async Task<IReadOnlyList<TeamsMessage>> ConversationAsync(string threadId, CancellationToken ct)
    {
        var messages = new List<TeamsMessage>();
        var page = await Api.MessagesAsync(threadId, 50, ct).ConfigureAwait(false);
        messages.AddRange(page.Messages);
        var cutoff = Cutoff;
        while (page.OlderLink is { } link && messages.Count < 2000)
        {
            // past the window once the oldest message read is older than it (an empty newest page reads on)
            if (cutoff is { } since && messages.Count > 0 && messages.Min(m => m.Composed ?? DateTimeOffset.MaxValue) < since)
            {
                break;
            }

            page = await Api.OlderAsync(link, ct).ConfigureAwait(false);
            if (page.Messages.Count == 0)
            {
                break;
            }

            messages.AddRange(page.Messages);
        }

        return messages;
    }

    // --- names ---------------------------------------------------------------------------------------------------------

    /// <summary>A person's display name for the Markdown: what is known, else a short form of the mri.</summary>
    internal string NameOf(string? mri) => mri is null ? "Unknown" : _names.TryGetValue(mri, out var name) ? name : ShortMri(mri);

    /// <summary>A chat's folder name, fixed for the source's life once given (the topic, else the other people's names as known then).</summary>
    internal string ChatName(ChatInfo chat) => _chatNames.GetOrAdd(chat.Id, _ => NameChat(chat));

    private string NameChat(ChatInfo chat)
    {
        if (!string.IsNullOrWhiteSpace(chat.Title))
        {
            return TeamsNames.Safe(chat.Title);
        }

        var others = chat.MemberMris.Where(m => m != Api.Mri).Select(NameOf).Where(n => n.Length > 0).Distinct().ToList();
        var name = others.Count switch
        {
            0 => chat.Kind == ChatKind.Meeting ? "Meeting" : "Chat",
            <= 3 => string.Join(", ", others),
            _ => $"{string.Join(", ", others.Take(2))} (+{others.Count - 2})",
        };
        return TeamsNames.Safe(chat.Kind == ChatKind.Meeting ? $"Meeting — {name}" : name);
    }

    private void LearnNames(IEnumerable<TeamsMessage> messages)
        => Remember(messages.Where(m => m.FromMri is not null && !string.IsNullOrEmpty(m.FromDisplayName)).Select(m => (m.FromMri!, m.FromDisplayName!)));

    private void Remember(IEnumerable<(string Key, string Name)> pairs)
    {
        foreach (var (key, name) in pairs)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _names[key] = name;
            }
        }
    }

    private static string ShortMri(string mri)
    {
        var at = mri.IndexOf('@');
        var tail = at > 0 ? mri[..at] : mri;
        var colon = tail.LastIndexOf(':');
        return colon >= 0 && colon < tail.Length - 1 ? tail[(colon + 1)..] : tail;
    }

    // --- counting ------------------------------------------------------------------------------------------------------

    public Task CountFilesAsync(CancellationToken ct) => Kept.CountAsync(ct);

    /// <summary>For a walk: of the folders in a folder, those not worth a walk (a conversation's messages), listed when opened.</summary>
    private async ValueTask<IReadOnlySet<string>?> LeftOutAsync(string path, CancellationToken ct)
    {
        if (await ResolveAsync(path, ct).ConfigureAwait(false) is not TmFolder folder)
        {
            return null;
        }

        var left = (await Guard(() => folder.ListAsync(this, ct)).ConfigureAwait(false))
            .OfType<TmFolder>().Where(f => !f.Walked).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        return left.Count == 0 ? null : left;
    }

    // --- health --------------------------------------------------------------------------------------------------------

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

        if (_api is not null)
        {
            await _api.DisposeAsync().ConfigureAwait(false);
        }

        _stopping.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
