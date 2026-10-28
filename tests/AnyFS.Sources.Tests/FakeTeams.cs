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

    /// <summary>Adds a message to a conversation; a reply points at <paramref name="rootId"/> (a channel post's thread).</summary>
    public TeamsMessage Post(string threadId, string fromMri, string? fromName, string html, DateTimeOffset when, string? rootId = null, string messageType = "RichText/Html")
    {
        var id = (when.ToUnixTimeMilliseconds()).ToString();
        var message = new TeamsMessage(id, rootId, rootId, fromMri, when, messageType, html, fromName);
        if (!_messages.TryGetValue(threadId, out var list))
        {
            _messages[threadId] = list = [];
        }

        list.Add(message);
        if (fromName is { Length: > 0 })
        {
            _people[fromMri] = fromName;
        }

        return message;
    }

    // --- ITeamsApi -----------------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<TeamInfo>> TeamsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TeamInfo>>(_teams);

    public Task<IReadOnlyList<ChatInfo>> ChatsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ChatInfo>>(_chats);

    public Task<MessagePage> MessagesAsync(string threadId, int max, CancellationToken ct)
    {
        MessageCalls++;
        var all = Ordered(threadId);
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
        => Task.FromResult<IReadOnlyList<Person>>(mris.Where(_people.ContainsKey).Select(m => new Person(m, _people[m])).ToList());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public TeamsStandIn StandIn() => new(this);

    private List<TeamsMessage> Ordered(string threadId)
        => _messages.TryGetValue(threadId, out var list)
            ? list.OrderBy(m => m.Composed ?? DateTimeOffset.MinValue).ThenBy(m => m.Id, StringComparer.Ordinal).ToList()
            : [];
}
