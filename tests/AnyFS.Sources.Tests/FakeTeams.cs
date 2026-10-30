using AnyFS.Sdk;
using AnyFS.Sources.Teams;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A Teams back end held in memory (spec 7.9), standing in for Microsoft's Teams services in tests: teams and their
/// channels, chats, and each conversation's messages, built from what the lab/sandbox answered (spike 18). It implements
/// <see cref="ITeamsApi"/> directly — the seam the live web session also implements — so a source started with its
/// <see cref="StandIn"/> reads it without a network. Message paging is by page size, oldest link walking back.
/// </summary>
internal sealed class FakeTeams : ITeamsApi
{
    private readonly List<TeamInfo> _teams = [];
    private readonly List<ChatInfo> _chats = [];
    private readonly Dictionary<string, List<TeamsMessage>> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _people = new(StringComparer.OrdinalIgnoreCase);

    public FakeTeams(string account = "tester@contoso.com", string? mri = "8:orgid:00000000-0000-0000-0000-00000000me")
    {
        Account = account;
        Mri = mri;
    }

    public string Account { get; }

    public string? Mri { get; }

    /// <summary>How many conversation-message pages have been asked for: tests check a folder that is not re-read has not been fetched.</summary>
    public int MessageCalls { get; private set; }

    /// <summary>How many times the aggregator's structure (teams and chats together) has been asked for.</summary>
    public int StructureCalls => _structureCalls;

    private int _structureCalls;

    /// <summary>When set, the people lookup fails (the middle tier refusing a batch), as a 400 or 403 would.</summary>
    public bool PeopleFails { get; set; }

    /// <summary>When set, a conversation's newest page comes back empty with a link to older messages.</summary>
    public bool EmptyNewestPage { get; set; }

    /// <summary>A delay on each structure call, so concurrent askers overlap.</summary>
    public TimeSpan StructureDelay { get; set; }

    public TeamInfo AddTeam(string displayName, params ChannelInfo[] channels)
    {
        var team = new TeamInfo($"19:{Guid.NewGuid():N}@thread.tacv2", displayName, Guid.NewGuid().ToString(), channels);
        _teams.Add(team);
        return team;
    }

    public static ChannelInfo Channel(string displayName, bool general = false, bool priv = false)
        => new($"19:{Guid.NewGuid():N}@thread.tacv2", displayName, general, priv);

    public ChatInfo AddChat(ChatKind kind, string? title, IReadOnlyList<string> otherMris, DateTimeOffset? lastUpdated = null, bool hidden = false)
    {
        var chat = new ChatInfo($"19:{Guid.NewGuid():N}", title, kind, otherMris, lastUpdated ?? DateTimeOffset.UtcNow) { Hidden = hidden };
        _chats.Add(chat);
        return chat;
    }

    /// <summary>Replaces a team as the aggregator now gives it (its details), by its id.</summary>
    public void SetTeam(TeamInfo team) => _teams[_teams.FindIndex(t => t.Id == team.Id)] = team;

    /// <summary>Replaces a chat as the aggregator now gives it (its details), by its id.</summary>
    public void SetChat(ChatInfo chat) => _chats[_chats.FindIndex(c => c.Id == chat.Id)] = chat;

    private readonly Dictionary<string, Person> _kinds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A person the people lookup names: a guest, a bot, of an organisation (the middle tier's userType, type, tenantName).</summary>
    public void AddPerson(string mri, string displayName, bool guest = false, bool bot = false, string? organisation = null)
    {
        _people[mri] = displayName;
        _kinds[mri] = new Person(mri, displayName) { Guest = guest, Bot = bot, Organisation = organisation };
    }

    /// <summary>The mris the people lookup was asked about, in order.</summary>
    public List<string> PeopleAsked { get; } = [];

    /// <summary>
    /// Adds a message to a conversation; a reply points at <paramref name="rootId"/> (a channel post's thread). An edited one
    /// carries its edit time, a deleted one its deletion time and no content (as the chat service gives them).
    /// </summary>
    public TeamsMessage Post(string threadId, string fromMri, string? fromName, string html, DateTimeOffset when, string? rootId = null, string messageType = "RichText/Html",
        DateTimeOffset? edited = null, DateTimeOffset? deleted = null, IReadOnlyList<Reaction>? reactions = null, IReadOnlyList<SharedFile>? files = null, bool pinned = false)
    {
        var id = (when.ToUnixTimeMilliseconds()).ToString();
        var message = new TeamsMessage(id, rootId, rootId, fromMri, when, messageType, deleted is null ? html : null, fromName)
        {
            Edited = edited,
            Deleted = deleted,
            Reactions = reactions ?? [],
            Files = files ?? [],
            Images = deleted is null ? TeamsFiles.ImagesIn(html) : [],
            Pinned = pinned,
        };
        if (!_messages.TryGetValue(threadId, out var list))
        {
            _messages[threadId] = list = [];
        }

        list.Add(message);
        if (fromName is { Length: > 0 })
        {
            _people[fromMri] = fromName;
        }

        Bump(threadId, id);
        return message;
    }

    // --- ITeamsApi -----------------------------------------------------------------------------------------------------

    public async Task<TeamsStructure> StructureAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _structureCalls);
        if (StructureDelay > TimeSpan.Zero)
        {
            await Task.Delay(StructureDelay, ct);
        }

        return new TeamsStructure(_teams.ToList(), _chats.ToList());
    }

    public Task<MessagePage> MessagesAsync(string threadId, int max, CancellationToken ct)
    {
        MessageCalls++;
        var all = Ordered(threadId);
        if (EmptyNewestPage)
        {
            return Task.FromResult(new MessagePage([], all.Count > 0 ? $"{threadId}|{all.Count}" : null));
        }

        var page = all.TakeLast(max).ToList();
        var older = all.Count > max ? $"{threadId}|{all.Count - max}" : null;
        return Task.FromResult(new MessagePage(page, older));
    }

    /// <summary>A delay on each older page, so a test can open a month before the history has reached it.</summary>
    public TimeSpan OlderDelay { get; set; }

    public async Task<MessagePage> OlderAsync(string olderLink, CancellationToken ct)
    {
        MessageCalls++;
        if (OlderDelay > TimeSpan.Zero)
        {
            await Task.Delay(OlderDelay, ct);
        }

        var bar = olderLink.LastIndexOf('|');
        var threadId = olderLink[..bar];
        var before = int.Parse(olderLink[(bar + 1)..]);
        var all = Ordered(threadId);
        const int size = 50;
        var start = Math.Max(0, before - size);
        var page = all.Skip(start).Take(before - start).ToList();
        var older = start > 0 ? $"{threadId}|{start}" : null;
        return new MessagePage(page, older);
    }

    public Task<IReadOnlyList<Person>> PeopleAsync(IReadOnlyList<string> mris, CancellationToken ct)
    {
        if (PeopleFails)
        {
            throw new VfsException(VfsError.IoError, "Teams (fake) answered 400: the people lookup refused");
        }

        lock (PeopleAsked)
        {
            PeopleAsked.AddRange(mris);
        }

        return Task.FromResult<IReadOnlyList<Person>>(mris.Where(_people.ContainsKey)
            .Select(m => _kinds.TryGetValue(m, out var kind) ? kind with { DisplayName = _people[m] } : new Person(m, _people[m])).ToList());
    }

    /// <summary>Renames a person (as when the source later learns a sender's name from a message).</summary>
    public void Rename(string mri, string displayName) => _people[mri] = displayName;

    /// <summary>Moves a conversation's version on, as the aggregator does when it changes (a post does it itself).</summary>
    public void Bump(string threadId, string version)
    {
        for (var i = 0; i < _chats.Count; i++)
        {
            if (_chats[i].Id == threadId)
            {
                _chats[i] = _chats[i] with { Version = version };
            }
        }

        for (var i = 0; i < _teams.Count; i++)
        {
            if (_teams[i].Channels.Any(c => c.Id == threadId))
            {
                _teams[i] = _teams[i] with { Channels = _teams[i].Channels.Select(c => c.Id == threadId ? c with { Version = version } : c).ToList() };
            }
        }
    }

    /// <summary>Replaces a message as it is now (edited, reacted to), without moving its conversation's version on.</summary>
    public void Replace(string threadId, TeamsMessage message)
    {
        var list = _messages[threadId];
        list[list.FindIndex(m => m.Id == message.Id)] = message;
    }

    // --- files ---------------------------------------------------------------------------------------------------------

    private readonly Dictionary<string, string> _sites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Site, string Path, byte[] Bytes, DateTimeOffset? Modified)> _library = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _images = new(StringComparer.Ordinal);

    /// <summary>How many inline images and file contents have been asked for.</summary>
    public int ImageCalls { get; private set; }

    public int ContentCalls { get; private set; }

    /// <summary>Gives a team a SharePoint site: its channels' files are folders named for them in its library.</summary>
    public void AddSite(TeamInfo team, string siteUrl) => _sites[team.GroupId!] = siteUrl;

    /// <summary>A file in a site's library, by its path within the library (<c>General/plan.txt</c>).</summary>
    public void AddLibraryFile(string siteUrl, string path, byte[] content, DateTimeOffset? modified = null) => _library[$"{siteUrl}|{path}"] = (siteUrl, path, content, modified);

    /// <summary>An inline image in Teams' media service, by its address.</summary>
    public void AddImage(string src, byte[] bytes) => _images[src] = bytes;

    // --- the tokens the page mints as it gets round to it ---------------------------------------------------------------

    /// <summary>
    /// Whether SharePoint's token is in hand (the live page's often is, not always). With neither it nor Graph's, every file
    /// call is refused at once, as the live seam refuses; with Graph's alone, the group sites come as Microsoft Search gives
    /// them, without their groups.
    /// </summary>
    public bool SharePointReady { get; set; } = true;

    /// <summary>Whether a Graph token is in hand: every group (<see cref="Directory"/>) is listed, and the files are read with it while SharePoint's token is not in hand.</summary>
    public bool GraphReady { get; set; }

    /// <summary>The shared files' own addresses asked for (Graph opens a shared file by it).</summary>
    public List<string> SharedUrlsAsked { get; } = [];

    /// <summary>When set, a background ask for the tokens brings SharePoint's (once <see cref="SeekHold"/> lets it).</summary>
    public bool SharePointComesWhenAsked { get; set; }

    /// <summary>What a background ask waits on before it brings the token: a test's say-so (the live page takes its time).</summary>
    public Task SeekHold { get; set; } = Task.CompletedTask;

    /// <summary>How many times the tokens have been asked for behind the listings.</summary>
    public int SeekCalls => _seekCalls;

    private int _seekCalls;

    /// <summary>How many SharePoint calls were made (one without its token is refused at once).</summary>
    public int SharePointCalls => _sharePointCalls;

    private int _sharePointCalls;

    public async Task<bool> SeekTokensAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _seekCalls);
        if (SharePointReady || !SharePointComesWhenAsked)
        {
            return false;
        }

        await SeekHold.WaitAsync(ct);
        SharePointReady = true;
        return true;
    }

    private void SharePoint()
    {
        Interlocked.Increment(ref _sharePointCalls);
        if (!SharePointReady && !GraphReady)
        {
            throw TokenNotHandedOutException.Refusal(TeamsService.SharePoint);
        }
    }

    public Task<string?> SiteOfGroupAsync(string groupId, CancellationToken ct)
    {
        SharePoint();
        return Task.FromResult(_sites.TryGetValue(groupId, out var site) ? site : _groupSites.FirstOrDefault(s => s.GroupId == groupId)?.SiteUrl);
    }

    /// <summary>As the live seam: the folder the channel's thread names (a private or shared channel's own site), else the channel's name in its team's site.</summary>
    public Task<(string SiteUrl, string Path)?> ChannelFilesAsync(TeamInfo team, ChannelInfo channel, ThreadDetails? thread, CancellationToken ct)
    {
        SharePoint();
        return Task.FromResult(TeamsRest.ThreadFolder(thread)
            ?? (team.GroupId is { } group && _sites.TryGetValue(group, out var site) ? ((string, string)?)(site, channel.DisplayName) : null));
    }

    public Task<IReadOnlyList<DriveEntry>> FolderAsync(string siteUrl, string path, CancellationToken ct)
    {
        SharePoint();
        var prefix = path.Length == 0 ? "" : path + "/";
        var entries = new Dictionary<string, DriveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (site, file, bytes, modified) in _library.Values)
        {
            if (!site.Equals(siteUrl, StringComparison.OrdinalIgnoreCase) || !file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = file[prefix.Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                entries[rest] = new DriveEntry(rest, false, bytes.Length, modified, Download(site, file));
            }
            else
            {
                entries.TryAdd(rest[..slash], new DriveEntry(rest[..slash], true, 0, modified, null));
            }
        }

        return Task.FromResult<IReadOnlyList<DriveEntry>>(entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public Task<DriveEntry?> FileAsync(string siteUrl, string path, string? url, CancellationToken ct)
    {
        SharePoint();
        if (url is not null && !SharePointReady)
        {
            lock (SharedUrlsAsked)
            {
                SharedUrlsAsked.Add(url);
            }
        }

        return Task.FromResult(_library.TryGetValue($"{siteUrl}|{path}", out var f) ? new DriveEntry(Path.GetFileName(path), false, f.Bytes.Length, f.Modified, Download(siteUrl, path)) : null);
    }

    public Task<HttpResponseMessage> ContentAsync(string downloadUrl, long offset, CancellationToken ct)
    {
        ContentCalls++;
        var key = Uri.UnescapeDataString(downloadUrl["https://download.test/".Length..]);
        var bytes = _library[key].Bytes;
        var response = new HttpResponseMessage(offset > 0 ? System.Net.HttpStatusCode.PartialContent : System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[(int)offset..]) };
        if (offset > 0)
        {
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
        }

        return Task.FromResult(response);
    }

    public Task<TeamsImage?> ImageAsync(string src, CancellationToken ct)
    {
        ImageCalls++;
        return Task.FromResult(_images.TryGetValue(src, out var bytes) ? new TeamsImage(bytes, TeamsFiles.ExtensionOf(bytes, null)) : null);
    }

    private readonly Dictionary<string, byte[]> _media = new(StringComparer.Ordinal);

    /// <summary>A recording's video in Teams' media service, by its address (amsVideo).</summary>
    public void AddMedia(string address, byte[] bytes) => _media[address] = bytes;

    /// <summary>How many reads of the media service there were.</summary>
    public int MediaCalls { get; private set; }

    /// <summary>As the media service: the object from the offset, as a range; one it does not have, not found.</summary>
    public Task<HttpResponseMessage> MediaAsync(string address, long offset, CancellationToken ct)
    {
        MediaCalls++;
        if (!_media.TryGetValue(address, out var bytes))
        {
            throw new VfsException(VfsError.NotFound, "Teams (fake) has no such recording");
        }

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes[(int)Math.Min(offset, bytes.Length)..]) };
        response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
        return Task.FromResult(response);
    }

    private static string Download(string site, string path) => "https://download.test/" + Uri.EscapeDataString($"{site}|{path}");

    // --- groups --------------------------------------------------------------------------------------------------------

    private readonly List<GroupSite> _groupSites = [];

    /// <summary>What Graph lists with its token in hand (<see cref="GraphReady"/>): every group of the organisation (all-groups.md), and of them the account's (groups.md).</summary>
    public GroupsDirectory? Directory { get; set; }

    /// <summary>When set, Graph refuses its list of groups (a token without Group.Read.All): the group sites come from Microsoft Search, without their groups.</summary>
    public bool GraphListsNoGroups { get; set; }

    /// <summary>A group site the account can open (its library's files added with <see cref="AddLibraryFile"/>).</summary>
    public void AddGroupSite(GroupSite site) => _groupSites.Add(site);

    /// <summary>As the live seam: SharePoint's search with its token; else Graph's list of groups (named without their sites), or Microsoft Search's sites (without their groups) where Graph refuses that list.</summary>
    public Task<IReadOnlyList<GroupSite>> GroupSitesAsync(CancellationToken ct)
    {
        SharePoint();
        return Task.FromResult<IReadOnlyList<GroupSite>>(
            SharePointReady ? _groupSites.ToList()
            : GraphListsNoGroups ? _groupSites.Select(s => s with { GroupId = null }).ToList()
            : _groupSites.Select(s => s with { SiteUrl = null }).ToList());
    }

    /// <summary>As the live seam: Graph's every group with its token in hand, else the group sites SharePoint's search gave, else none.</summary>
    public Task<GroupsDirectory?> AllGroupsAsync(IReadOnlyList<TeamInfo> joined, IReadOnlyList<GroupSite>? sites, CancellationToken ct)
        => Task.FromResult(GraphReady && Directory is { } graph && !GraphListsNoGroups ? graph : !SharePointReady || sites is null ? null : new GroupsDirectory(
            sites.Where(s => s.GroupId is not null).Select(s => new GroupInfo(s.GroupId!, s.Name) { Kind = "Microsoft 365 group", SiteUrl = s.SiteUrl, Description = s.Description, Created = s.Created }).ToList(),
            joined.Select(t => t.GroupId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase),
            joined.Where(t => t.Owner).Select(t => t.GroupId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase),
            GroupsScope.Visible,
            "SharePoint"));

    /// <summary>As the live seam: the account's groups of every kind from Graph, else its teams'.</summary>
    public Task<GroupsDirectory> MyGroupsAsync(IReadOnlyList<TeamInfo> joined, CancellationToken ct)
        => Task.FromResult(GraphReady && Directory is { } graph && !GraphListsNoGroups
            ? graph with { Groups = graph.Groups.Where(g => graph.Member.Contains(g.Id) || graph.Owner.Contains(g.Id)).ToList(), Scope = GroupsScope.Yours }
            : GroupsDirectory.OfTeams(joined));

    // --- the call history, the activity feed, a channel's thread --------------------------------------------------------

    /// <summary>The account's call history, newest first; null for an account with none (the chat service's 404).</summary>
    public List<CallRecord>? Calls { get; set; }

    /// <summary>The account's activity feed and mentions, newest first; null for an account with neither.</summary>
    public List<ActivityItem>? Activity { get; set; }

    /// <summary>When set, the call history is refused (as a 403 would be).</summary>
    public bool CallsFail { get; set; }

    /// <summary>How many times the call history has been asked for.</summary>
    public int CallsCalls => _callsCalls;

    private int _callsCalls;

    /// <summary>What the chat service tells of a channel's thread, by its id; one not here is not answered for.</summary>
    public Dictionary<string, ThreadDetails> Threads { get; } = new(StringComparer.Ordinal);

    /// <summary>How many channels' thread details have been asked for.</summary>
    public int ThreadCalls => _threadCalls;

    private int _threadCalls;

    public Task<IReadOnlyList<CallRecord>?> CallsAsync(int max, CancellationToken ct)
    {
        Interlocked.Increment(ref _callsCalls);
        return CallsFail
            ? throw new VfsException(VfsError.AccessDenied, "Teams (fake) answered 403: the call history refused")
            : Task.FromResult<IReadOnlyList<CallRecord>?>(Calls?.Take(max).ToList());
    }

    public Task<IReadOnlyList<ActivityItem>?> ActivityAsync(int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<ActivityItem>?>(Activity?.Take(2 * max).ToList());

    public Task<ThreadDetails?> ThreadDetailsAsync(string threadId, CancellationToken ct)
    {
        Interlocked.Increment(ref _threadCalls);
        return Task.FromResult(Threads.TryGetValue(threadId, out var details) ? details : null);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The seam a source reads this fake through; with a newest read of <paramref name="newest"/> messages, <paramref name="pageSize"/> to a page, so a few messages have a history.</summary>
    public TeamsStandIn StandIn(int newest = 2_000, int pageSize = 200) => new(this, newest, pageSize);

    private List<TeamsMessage> Ordered(string threadId)
        => _messages.TryGetValue(threadId, out var list)
            ? list.OrderBy(m => m.Composed ?? DateTimeOffset.MinValue).ThenBy(m => m.Id, StringComparer.Ordinal).ToList()
            : [];
}
