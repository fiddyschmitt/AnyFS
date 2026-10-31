using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Teams;

/// <summary>
/// A conversation's messages as read (spec 7.9), back to its start or the "within" window: what chat.md and a channel's
/// posts show. Its newest messages are read with its listings: again only as the aggregator's version of it moves on (its
/// newest page merged), whole once a walk interval, back as far as <see cref="NewestMessages"/> or the window. Everything
/// before them is its history: read behind the listings from its first listing on, a page at a time, the conversation used
/// last first; the folder that shows it listed afresh as it lands.
/// </summary>
public sealed partial class TeamsSource
{
    /// <summary>How far back a conversation's newest read goes (the read its listing waits for): what is before it is its history, read behind the listings.</summary>
    private int NewestMessages => _standIn?.NewestMessages ?? 2_000;

    /// <summary>A page of the chat service's messages: 200 at most ("PageSize query parameter is too large" past it).</summary>
    private int PageSize => _standIn?.PageSize ?? 200;

    /// <summary>How many messages the conversations hold in all before the least used histories are let go (read again when next listed).</summary>
    private const int KeptMessages = 150_000;

    // how soon a history read that failed is begun again
    private static readonly TimeSpan HistoryRetry = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);

    // one page of history read at a time, so the requests stay free for the listings: the turn goes first to a conversation
    // a client is waiting on a listing of (the one being looked at, or a search's next folder: not the walk's), then to the
    // one used last, the others waiting for it
    private readonly Lock _turns = new();
    private readonly List<(Conversation Store, TaskCompletionSource Turn)> _waiting = [];
    private bool _turnTaken;

    // the conversations' uses so far (Conversation.Used)
    private long _uses;

    // the folders that show a conversation's history (a chat's folder, a channel's posts), by conversation: which
    // conversations the clients' waits (_awaited) are on
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _historyFolders = new(StringComparer.Ordinal);

    // the folders a client is waiting on a listing of now (its own, or for a stat or an open the parent's), by path, with how
    // many wait: the walks and the refreshes behind the answers are not clients, and wait their turn
    private readonly ConcurrentDictionary<string, int> _awaited = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a conversation goes without its newest messages being read whole: an edit or reaction on an older message among them shows by then (the daily walk).</summary>
    private static readonly TimeSpan ReadWholeEvery = TimeSpan.FromDays(1);

    private sealed class Conversation(string threadId)
    {
        public string ThreadId { get; } = threadId;

        public Lock Gate { get; } = new();

        /// <summary>Every message read, newest read or history, by id.</summary>
        public Dictionary<string, TeamsMessage> Messages { get; } = new(StringComparer.Ordinal);

        public string? Version;

        public DateTimeOffset ReadWhole;

        /// <summary>The newest read's oldest message; null where that read reached the start or the window (no history).</summary>
        public DateTimeOffset? NewestFrom;

        /// <summary>Where the newest read stopped: where its history begins (null where it has none).</summary>
        public string? NewestNext;

        /// <summary>Where reading back goes on (the newest read's stop, then the history's); null once read back to the start or the window.</summary>
        public string? Next;

        public bool HistoryBegun;

        public DateTimeOffset? Oldest;

        public Task Reading = Task.CompletedTask;

        public bool Failed;

        public DateTimeOffset RetryAfter;

        /// <summary>Completed (and replaced) as each page of history lands, and when its reading ends.</summary>
        public TaskCompletionSource Landed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>When it was last used, as a count of the conversations' uses: the higher, the later (a clock's tick would tie two uses a moment apart).</summary>
        public long Used;

        public int Count => Messages.Count;

        public List<TeamsMessage> All()
        {
            lock (Gate)
            {
                return Messages.Values.ToList();
            }
        }

        public void Signal()
        {
            Landed.TrySetResult();
            Landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    // --- the newest messages -------------------------------------------------------------------------------------------

    /// <summary>A conversation as read, its newest messages refreshed at most once a refresh interval, their senders' names learned.</summary>
    private async Task<Conversation> ConversationAsync(string threadId, Func<TeamsStructure, string?> versionOf, CancellationToken ct)
    {
        var store = await Cache.GetAsync($"conversation:{threadId}", _settings.Refresh, async c =>
        {
            var conversation = await RefreshNewestAsync(threadId, versionOf(await StructureAsync(c).ConfigureAwait(false)), c).ConfigureAwait(false);
            await LearnNamesAsync(conversation.All(), c).ConfigureAwait(false);
            return conversation;
        }, ct).ConfigureAwait(false);
        store.Used = Interlocked.Increment(ref _uses);
        return store;
    }

    private Task<Conversation> ChatConversationAsync(ChatInfo chat, CancellationToken ct)
        => ConversationAsync(chat.Id, s => s.Chats.FirstOrDefault(ch => ch.Id == chat.Id)?.Version ?? chat.Version, ct);

    private Task<Conversation> ChannelConversationAsync(string channelId, CancellationToken ct)
        => ConversationAsync(channelId, s => s.Teams.SelectMany(t => t.Channels).FirstOrDefault(ch => ch.Id == channelId)?.Version, ct);

    /// <summary>
    /// A conversation's newest messages, read no more than they must be: as kept while Teams gives it the same version; its
    /// newest page merged over what is kept when the version has moved on; read whole the first time, when the newest page
    /// does not reach back to what is kept, and once each walk interval, so an edit or a reaction on an older message shows
    /// too. What was read before stays: what a whole read no longer reaches is the conversation's history.
    /// </summary>
    private async Task<Conversation> RefreshNewestAsync(string threadId, string? version, CancellationToken ct)
    {
        if (_conversations.Count > 5_000)
        {
            _conversations.Clear();
        }

        var store = _conversations.GetOrAdd(threadId, id => new Conversation(id));
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset readWhole;
        string? kept;
        lock (store.Gate)
        {
            readWhole = store.ReadWhole;
            kept = store.Version;
        }

        if (readWhole != default && now - readWhole < ReadWholeEvery)
        {
            if (version is not null && version == kept)
            {
                return store;
            }

            var newest = await Api.MessagesAsync(threadId, 50, ct).ConfigureAwait(false);
            lock (store.Gate)
            {
                if (newest.OlderLink is null || newest.Messages.Any(m => store.Messages.ContainsKey(m.Id)))
                {
                    foreach (var message in newest.Messages)
                    {
                        store.Messages[message.Id] = message;
                    }

                    store.Version = version;
                    return store;
                }
            }
        }

        var (read, more) = await ReadNewestAsync(threadId, ct).ConfigureAwait(false);
        lock (store.Gate)
        {
            foreach (var message in read)
            {
                store.Messages[message.Id] = message;
            }

            store.Version = version;
            store.ReadWhole = now;
            var oldest = read.Select(m => m.Composed).Where(at => at is not null).Min();
            store.NewestFrom = more is null ? null : oldest;
            store.NewestNext = more;
            if (!store.HistoryBegun)
            {
                store.Next = more;
            }

            if (oldest is { } at && (store.Oldest is null || at < store.Oldest))
            {
                store.Oldest = at;
            }
        }

        return store;
    }

    /// <summary>
    /// A conversation's newest messages: its newest page and back, as far as <see cref="NewestMessages"/> or the "within"
    /// window; with where it stopped where there is more before it.
    /// </summary>
    private async Task<(List<TeamsMessage> Messages, string? More)> ReadNewestAsync(string threadId, CancellationToken ct)
    {
        var messages = new List<TeamsMessage>();
        var page = await Api.MessagesAsync(threadId, PageSize, ct).ConfigureAwait(false);
        messages.AddRange(page.Messages);
        var cutoff = Cutoff;
        while (page.OlderLink is { } link && messages.Count < NewestMessages)
        {
            // past the window once the oldest message read is older than it (an empty newest page reads on)
            if (PastWindow(messages, cutoff))
            {
                return (messages, null);
            }

            page = await Api.OlderAsync(link, ct).ConfigureAwait(false);
            if (page.Messages.Count == 0)
            {
                return (messages, null);
            }

            messages.AddRange(page.Messages);
        }

        return (messages, PastWindow(messages, cutoff) ? null : page.OlderLink);
    }

    private static bool PastWindow(List<TeamsMessage> messages, DateTimeOffset? cutoff)
        => cutoff is { } since && messages.Count > 0 && messages.Min(m => m.Composed ?? DateTimeOffset.MaxValue) < since;

    /// <summary>Whether a message is within the "within" window.</summary>
    private bool Within(TeamsMessage message) => Cutoff is not { } since || message.Composed is not { } at || at >= since;

    /// <summary>A conversation's content messages grouped into threads (the root first, then its replies in time order), within the window, newest thread first.</summary>
    private List<IReadOnlyList<TeamsMessage>> Threads(IEnumerable<TeamsMessage> messages)
    {
        var cutoff = Cutoff;
        return messages.Where(m => m.IsContent)
            .GroupBy(m => m.ThreadId)
            .Select(g => (IReadOnlyList<TeamsMessage>)g.OrderBy(m => m.Composed ?? DateTimeOffset.MinValue).ThenBy(m => m.Id, StringComparer.Ordinal).ToList())
            .Where(t => t.Count > 0 && (cutoff is null || t[^1].Composed is null || t[^1].Composed >= cutoff))
            .OrderByDescending(t => t[^1].Composed ?? DateTimeOffset.MinValue)
            .ToList();
    }

    // --- what the folders show -----------------------------------------------------------------------------------------

    /// <summary>
    /// A chat's messages within the window, as far as read, its history begun behind the listings where its newest read did
    /// not reach its start (the listing that begins it waits a moment); with what chat.md says while it does not reach back
    /// to the start yet (<see cref="HistoryNote"/>).
    /// </summary>
    private async Task<(List<TeamsMessage> Messages, string? Note)> ChatMessagesAsync(ChatInfo chat, CancellationToken ct)
    {
        var store = await ChatConversationAsync(chat, ct).ConfigureAwait(false);
        await HistoryAsync(store, ct).ConfigureAwait(false);
        return (store.All().Where(Within).ToList(), HistoryNote(store));
    }

    /// <summary>
    /// A channel's posts as its posts folder shows them: its threads, newest first, back to its start or the window (an
    /// opening message the history has not brought yet: its thread from what is read of it), its history begun behind the
    /// listings as for a chat (<see cref="ChatMessagesAsync"/>).
    /// </summary>
    internal async Task<IReadOnlyList<IReadOnlyList<TeamsMessage>>> ThreadsAsync(string channelId, CancellationToken ct)
    {
        var store = await ChannelConversationAsync(channelId, ct).ConfigureAwait(false);
        await HistoryAsync(store, ct).ConfigureAwait(false);
        return Threads(store.All());
    }

    /// <summary>
    /// Whether a conversation's messages are all read, back to its start or the window, so a listing made of them now is
    /// whole: not while its history is still being read, failed, or was let go (<see cref="LetGo"/>). A folder listed
    /// otherwise carries no stamp (<see cref="ListingCache.Unstamp"/>), so it is listed afresh rather than kept.
    /// </summary>
    internal bool ConversationWhole(string threadId)
    {
        if (!_conversations.TryGetValue(threadId, out var store))
        {
            return false;
        }

        lock (store.Gate)
        {
            return store.Next is null && !store.Failed;
        }
    }

    /// <summary>
    /// What chat.md says of a conversation's history where it does not reach back to its start or the window (a read that
    /// failed part way): how far back it goes; null once it does.
    /// </summary>
    private static string? HistoryNote(Conversation store)
    {
        lock (store.Gate)
        {
            if (store.Next is null)
            {
                return null;
            }

            var since = store.Oldest?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "?";
            return $"not all could be read just now: this file goes back to {since}, and the rest are read when the chat is next opened";
        }
    }

    // --- the history ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads a conversation's history (unless it has none left, is being read, or failed a moment ago) and waits for it: a
    /// listing is whole, back to the conversation's start or the window, however long that takes, so a search finds the
    /// same files the first time it lists the folder as the tenth (one page at a time, the turn going first to a conversation
    /// a client is waiting on, <see cref="PassTurn"/>). After a read that failed, a listing answers with what was read, and
    /// chat.md says so; the rest is read when its folder is next listed, a minute on at the soonest.
    /// </summary>
    private async Task HistoryAsync(Conversation store, CancellationToken ct)
    {
        BeginHistory(store);
        while (true)
        {
            Task landed;
            lock (store.Gate)
            {
                if (store.Next is null || store.Failed || store.Reading.IsCompleted)
                {
                    return;
                }

                landed = store.Landed.Task;
            }

            await landed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Begins reading a conversation's history, unless it has none left, is being read, failed a moment ago or the source is stopping: whether it began it.</summary>
    private bool BeginHistory(Conversation store)
    {
        lock (store.Gate)
        {
            if (store.Next is null || !store.Reading.IsCompleted || DateTimeOffset.UtcNow < store.RetryAfter || _stopping.IsCancellationRequested)
            {
                return false;
            }

            store.HistoryBegun = true;
            store.Failed = false;
            var stopping = _stopping.Token;
            store.Reading = Task.Run(() => ReadHistoryAsync(store, stopping), CancellationToken.None);
            return true;
        }
    }

    /// <summary>Waits for a page's turn: one page of history is read at a time.</summary>
    private Task TurnAsync(Conversation store, CancellationToken ct)
    {
        lock (_turns)
        {
            if (!_turnTaken)
            {
                _turnTaken = true;
                return Task.CompletedTask;
            }

            var turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Add((store, turn));
            return turn.Task.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Ends a page's turn: it stays with this conversation while it has more to read and none waiting ranks higher, else
    /// goes to the one waiting that ranks highest; whether it stays. A conversation a client is waiting on a listing of
    /// ranks above those only the walks list (a search's next folder, or the one being looked at, is read before the walk's
    /// twentieth), then the one used last.
    /// </summary>
    private bool PassTurn(Conversation store, bool more)
    {
        lock (_turns)
        {
            var next = -1;
            (bool Client, long Used) best = default;
            for (var i = 0; i < _waiting.Count; i++)
            {
                var rank = Rank(_waiting[i].Store);
                if (next < 0 || Higher(rank, best))
                {
                    next = i;
                    best = rank;
                }
            }

            if (more && (next < 0 || !Higher(best, Rank(store))))
            {
                return true;
            }

            if (next < 0)
            {
                _turnTaken = false;
                return false;
            }

            var (_, turn) = _waiting[next];
            _waiting.RemoveAt(next);
            turn.TrySetResult();
            return false;
        }
    }

    /// <summary>A conversation's rank for the history's turn: whether a client is waiting on a listing that shows it, then when it was last used.</summary>
    private (bool Client, long Used) Rank(Conversation store) => (ClientWaits(store), store.Used);

    private static bool Higher((bool Client, long Used) rank, (bool Client, long Used) than)
        => rank.Client != than.Client ? rank.Client : rank.Used > than.Used;

    /// <summary>Whether a client is waiting on a listing of a folder that shows a conversation's history, or of one below it (<see cref="_awaited"/>).</summary>
    private bool ClientWaits(Conversation store)
    {
        if (_awaited.IsEmpty || !_historyFolders.TryGetValue(store.ThreadId, out var folders))
        {
            return false;
        }

        foreach (var waited in _awaited.Keys)
        {
            foreach (var folder in folders.Keys)
            {
                if (waited.Equals(folder, StringComparison.OrdinalIgnoreCase) || (waited.Length > folder.Length && waited[folder.Length] == '/' && waited.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reads a conversation's history, a page at a time back from where its reading stopped, to its start or the window, a
    /// page's turn going to the conversation that ranks highest (<see cref="PassTurn"/>); the listings waiting on it answer
    /// as it ends. A failure keeps what was read; the rest is read when its folder is next listed, a minute on at the
    /// soonest.
    /// </summary>
    private async Task ReadHistoryAsync(Conversation store, CancellationToken ct)
    {
        var turn = false;
        try
        {
            while (true)
            {
                string? link;
                lock (store.Gate)
                {
                    link = store.Next;
                }

                if (link is null)
                {
                    break;
                }

                if (!turn)
                {
                    await TurnAsync(store, ct).ConfigureAwait(false);
                    turn = true;
                }

                var page = await Api.OlderAsync(link, ct).ConfigureAwait(false);
                await LearnNamesAsync(page.Messages, ct).ConfigureAwait(false);
                var cutoff = Cutoff;
                bool more;
                lock (store.Gate)
                {
                    // a message the newest read has stays as that read has it (an edit, a reaction since)
                    foreach (var message in page.Messages)
                    {
                        store.Messages.TryAdd(message.Id, message);
                    }

                    var oldest = page.Messages.Select(m => m.Composed).Where(at => at is not null).Min();
                    if (oldest is { } at && (store.Oldest is null || at < store.Oldest))
                    {
                        store.Oldest = at;
                    }

                    store.Next = page.Messages.Count == 0 || (cutoff is { } since && store.Oldest < since) ? null : page.OlderLink;
                    more = store.Next is not null;
                    store.Signal();
                }

                turn = PassTurn(store, more);
                LetGo(store);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogInformation("teams: a conversation's earlier messages could not all be read ({Error}); what was read is shown, the rest is read when its folder is next listed", ex.Message);
            lock (store.Gate)
            {
                store.Failed = true;
                store.RetryAfter = DateTimeOffset.UtcNow + HistoryRetry;
            }
        }
        finally
        {
            if (turn)
            {
                PassTurn(store, more: false);
            }

            lock (store.Gate)
            {
                store.Signal();
            }
        }
    }

    /// <summary>Notes a folder that shows a conversation's history, by its path (<see cref="ClientWaits"/>).</summary>
    private void ShowsHistory(TmNode folder, string path)
    {
        if (folder is IHistoryFolder shows)
        {
            _historyFolders.GetOrAdd(shows.ThreadId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase)).TryAdd(path, 0);
        }
    }

    /// <summary>
    /// While the conversations hold more than <see cref="KeptMessages"/> messages, the least used ones' histories are let go
    /// (back to their newest messages; read again when their folders are next listed).
    /// </summary>
    private void LetGo(Conversation reading)
    {
        var total = _conversations.Values.Sum(c => c.Count);
        foreach (var store in _conversations.Values.Where(c => c != reading && c.HistoryBegun && c.Reading.IsCompleted).OrderBy(c => c.Used).ToList())
        {
            if (total <= KeptMessages)
            {
                return;
            }

            lock (store.Gate)
            {
                if (store.NewestFrom is not { } from)
                {
                    continue;
                }

                var before = store.Messages.Count;
                foreach (var id in store.Messages.Where(p => p.Value.Composed is { } at && at < from).Select(p => p.Key).ToList())
                {
                    store.Messages.Remove(id);
                }

                store.Next = store.NewestNext;
                store.HistoryBegun = false;
                store.Oldest = store.Messages.Values.Select(m => m.Composed).Where(at => at is not null).Min();
                total -= before - store.Messages.Count;
            }
        }
    }

    /// <summary>The conversations' history reads, for the source's stopping to wait on.</summary>
    private IEnumerable<Task> HistoryReads => _conversations.Values.Select(c => c.Reading);
}

/// <summary>A folder that shows a conversation's history (a chat's folder, a channel's posts): its listing waits for the history to be read (<see cref="TeamsSource"/>).</summary>
internal interface IHistoryFolder
{
    string ThreadId { get; }
}

/// <summary>The stamps the walks keep a folder by where Teams gives no version of its own (<see cref="KeptListingsOptions.StampOf"/>).</summary>
internal static class TeamsStamps
{
    /// <summary>
    /// A thread's stamp: made of what its folder shows of its messages (each one's id, edit and deletion, reactions, files,
    /// images and pin), so a thread a channel's walk reaches again is kept unless one of those changed; a hash, so it stays
    /// short however long the thread.
    /// </summary>
    public static string Of(IReadOnlyList<TeamsMessage> thread)
    {
        var text = new StringBuilder();
        foreach (var message in thread.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            text.Append(message.Id).Append('|').Append(message.Edited?.UtcTicks ?? 0).Append('|').Append(message.Deleted?.UtcTicks ?? 0).Append('|')
                .Append(message.Files.Count).Append('|').Append(message.Images.Count).Append('|').Append(message.Pinned ? '1' : '0').Append('|');
            foreach (var reaction in message.Reactions.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                text.Append(reaction.Key).Append(':').Append(reaction.Mris.Count).Append(',');
            }

            text.Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())).AsSpan(0, 16));
    }
}
