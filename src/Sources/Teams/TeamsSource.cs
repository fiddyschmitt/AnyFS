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

    /// <summary>The live web session (spec 7.9): built in a later commit. Tests use the stand-in.</summary>
    private Task<ITeamsApi> SignInAsync(CancellationToken ct)
        => throw new SourceFailedException("The Teams web session is not built yet (spec 7.9, in progress).", permanent: true);

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

        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        return node is TmFile file ? await Guard(() => file.OpenAsync(this, ct)).ConfigureAwait(false) : throw VfsException.IsADirectory(path);
    }

    private async IAsyncEnumerable<VfsEntry> ListFolderAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var node = await ResolveAsync(path, ct).ConfigureAwait(false);
        if (node is not TmFolder folder)
        {
            throw VfsException.NotADirectory(path);
        }

        foreach (var child in await Guard(() => folder.ListAsync(this, ct)).ConfigureAwait(false))
        {
            yield return child.Entry;
        }
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

    internal Task<IReadOnlyList<TeamInfo>> TeamsAsync(CancellationToken ct)
        => Cache.GetAsync("teams", _settings.Refresh, async c =>
        {
            var teams = await Api.TeamsAsync(c).ConfigureAwait(false);
            Remember(teams.SelectMany(t => t.Channels).Select(ch => (ch.Id, ch.DisplayName)));
            return teams;
        }, ct);

    internal Task<IReadOnlyList<ChatInfo>> ChatsAsync(CancellationToken ct)
        => Cache.GetAsync("chats", _settings.Refresh, async c =>
        {
            var chats = await Api.ChatsAsync(c).ConfigureAwait(false);

            // the people a chat is named for: learn their names once, so a 1:1 or untitled group is named, not an mri
            var mris = chats.SelectMany(ch => ch.MemberMris).Where(m => m != Api.Mri && !_names.ContainsKey(m)).Distinct().ToList();
            if (mris.Count > 0)
            {
                var people = await Api.PeopleAsync(mris, c).ConfigureAwait(false);
                Remember(people.Select(p => (p.Mri, p.DisplayName)));
            }

            return chats;
        }, ct);

    /// <summary>A channel's posts: its content messages grouped into threads (the root first, then its replies in time order), newest thread first.</summary>
    internal Task<IReadOnlyList<IReadOnlyList<TeamsMessage>>> ThreadsAsync(string channelId, CancellationToken ct)
        => Cache.GetAsync($"threads:{channelId}", _settings.Refresh, async c =>
        {
            var messages = await ConversationAsync(channelId, c).ConfigureAwait(false);
            LearnNames(messages);
            var threads = messages.Where(m => m.IsContent)
                .GroupBy(m => m.ThreadId)
                .Select(g => (IReadOnlyList<TeamsMessage>)g.OrderBy(m => m.Composed ?? DateTimeOffset.MinValue).ThenBy(m => m.Id, StringComparer.Ordinal).ToList())
                .Where(t => t.Count > 0)
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

    /// <summary>A conversation's content messages, newest page and as far back as the "within" window asks (or one page).</summary>
    private async Task<IReadOnlyList<TeamsMessage>> ConversationAsync(string threadId, CancellationToken ct)
    {
        var messages = new List<TeamsMessage>();
        var page = await Api.MessagesAsync(threadId, 50, ct).ConfigureAwait(false);
        messages.AddRange(page.Messages);
        var oldest = _settings.Within is { } within ? DateTimeOffset.UtcNow - within : (DateTimeOffset?)null;
        while (page.OlderLink is { } link && messages.Count < 2000 && (oldest is null || messages.Min(m => m.Composed ?? DateTimeOffset.MaxValue) > oldest))
        {
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

    internal string ChatName(ChatInfo chat)
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
