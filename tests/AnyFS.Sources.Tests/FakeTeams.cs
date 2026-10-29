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

    public ChatInfo AddChat(ChatKind kind, string? title, IReadOnlyList<string> otherMris, DateTimeOffset? lastUpdated = null)
    {
        var chat = new ChatInfo($"19:{Guid.NewGuid():N}", title, kind, otherMris, lastUpdated ?? DateTimeOffset.UtcNow);
        _chats.Add(chat);
        return chat;
    }

    public void AddPerson(string mri, string displayName) => _people[mri] = displayName;

    /// <summary>
    /// Adds a message to a conversation; a reply points at <paramref name="rootId"/> (a channel post's thread). An edited one
    /// carries its edit time, a deleted one its deletion time and no content (as the chat service gives them).
    /// </summary>
    public TeamsMessage Post(string threadId, string fromMri, string? fromName, string html, DateTimeOffset when, string? rootId = null, string messageType = "RichText/Html",
        DateTimeOffset? edited = null, DateTimeOffset? deleted = null, IReadOnlyList<Reaction>? reactions = null, IReadOnlyList<SharedFile>? files = null)
    {
        var id = (when.ToUnixTimeMilliseconds()).ToString();
        var message = new TeamsMessage(id, rootId, rootId, fromMri, when, messageType, deleted is null ? html : null, fromName)
        {
            Edited = edited,
            Deleted = deleted,
            Reactions = reactions ?? [],
            Files = files ?? [],
            Images = deleted is null ? TeamsFiles.ImagesIn(html) : [],
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

    public Task<MessagePage> OlderAsync(string olderLink, CancellationToken ct)
    {
        MessageCalls++;
        var bar = olderLink.LastIndexOf('|');
        var threadId = olderLink[..bar];
        var before = int.Parse(olderLink[(bar + 1)..]);
        var all = Ordered(threadId);
        const int size = 50;
        var start = Math.Max(0, before - size);
        var page = all.Skip(start).Take(before - start).ToList();
        var older = start > 0 ? $"{threadId}|{start}" : null;
        return Task.FromResult(new MessagePage(page, older));
    }

    public Task<IReadOnlyList<Person>> PeopleAsync(IReadOnlyList<string> mris, CancellationToken ct)
        => PeopleFails
            ? throw new VfsException(VfsError.IoError, "Teams (fake) answered 400: the people lookup refused")
            : Task.FromResult<IReadOnlyList<Person>>(mris.Where(_people.ContainsKey).Select(m => new Person(m, _people[m])).ToList());

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

    public Task<(string SiteUrl, string Path)?> ChannelFilesAsync(TeamInfo team, ChannelInfo channel, CancellationToken ct)
        => Task.FromResult(team.GroupId is { } group && _sites.TryGetValue(group, out var site) ? ((string, string)?)(site, channel.DisplayName) : null);

    public Task<IReadOnlyList<DriveEntry>> FolderAsync(string siteUrl, string path, CancellationToken ct)
    {
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

    public Task<DriveEntry?> FileAsync(string siteUrl, string path, CancellationToken ct)
        => Task.FromResult(_library.TryGetValue($"{siteUrl}|{path}", out var f) ? new DriveEntry(Path.GetFileName(path), false, f.Bytes.Length, f.Modified, Download(siteUrl, path)) : null);

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

    private static string Download(string site, string path) => "https://download.test/" + Uri.EscapeDataString($"{site}|{path}");

    // --- groups --------------------------------------------------------------------------------------------------------

    private readonly List<GroupSite> _groupSites = [];

    /// <summary>What GroupsAsync answers, as Graph lists every group; null answers the group sites, as SharePoint shows them.</summary>
    public GroupsDirectory? Directory { get; set; }

    /// <summary>A group site the account can open (its library's files added with <see cref="AddLibraryFile"/>).</summary>
    public void AddGroupSite(GroupSite site) => _groupSites.Add(site);

    public Task<IReadOnlyList<GroupSite>> GroupSitesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<GroupSite>>(_groupSites.ToList());

    public Task<GroupsDirectory> GroupsAsync(IReadOnlyList<TeamInfo> joined, CancellationToken ct)
        => Task.FromResult(Directory ?? new GroupsDirectory(
            _groupSites.Select(s => new GroupInfo(s.GroupId, s.Name) { Kind = "Microsoft 365 group", SiteUrl = s.SiteUrl, Description = s.Description, Created = s.Created }).ToList(),
            joined.Select(t => t.GroupId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase),
            joined.Where(t => t.Owner).Select(t => t.GroupId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase),
            Complete: false,
            "SharePoint"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public TeamsStandIn StandIn() => new(this);

    private List<TeamsMessage> Ordered(string threadId)
        => _messages.TryGetValue(threadId, out var list)
            ? list.OrderBy(m => m.Composed ?? DateTimeOffset.MinValue).ThenBy(m => m.Id, StringComparer.Ordinal).ToList()
            : [];
}
