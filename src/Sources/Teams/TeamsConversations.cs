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

    /// <summary>
    /// How long from the beginning of a conversation's history read its listings wait for it: a short history is whole in
    /// the listing that begins it (and one that joins it then), a long one fills in as it lands, its listings after that
    /// moment answering at once.
    /// </summary>
    private TimeSpan FirstHistoryWait => _standIn?.HistoryWait ?? TimeSpan.FromSeconds(5);

    // how often the folders that show a conversation's history are listed afresh while it is read (and twice at its end)
    private static readonly TimeSpan RelistEvery = TimeSpan.FromSeconds(20);

    // how soon a history read that failed is begun again
    private static readonly TimeSpan HistoryRetry = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);

    // one page of history read at a time, so the requests stay free for the listings: the turn goes to the conversation used
    // last (the one being looked at, not those a program listed on its way past), the others waiting for it
    private readonly Lock _turns = new();
    private readonly List<(Conversation Store, TaskCompletionSource Turn)> _waiting = [];
    private bool _turnTaken;

    // the conversations' uses so far (Conversation.Used)
    private long _uses;

    // the folders that show a conversation's history (a chat's folder, a channel's posts), by conversation: listed afresh as
    // its history lands
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _historyFolders = new(StringComparer.Ordinal);

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

        /// <summary>When its history read last began: its listings wait for it until <see cref="FirstHistoryWait"/> after.</summary>
        public DateTimeOffset ReadBegan;

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
    /// What chat.md says of a conversation's history while it does not reach back to its start or the window: how far back
    /// it goes, and whether the rest is still being read; null once it does.
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
            return store.Reading.IsCompleted || store.Failed
                ? $"not all could be read just now: this file goes back to {since}, and the rest are read when the chat is next opened"
                : $"still being read: this file goes back to {since} so far, and fills in as they arrive";
        }
    }

    // --- the history ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Begins reading a conversation's history behind the listings, unless it has none left, is being read, or failed a
    /// moment ago; a listing made in the first moment of its reading waits for it (until <see cref="FirstHistoryWait"/> after
    /// it began), so a short history is whole in the listing that begins it, and in one that joins it (an open while a walk
    /// reads it). After that moment a listing answers at once with what is read: a long history's relists, and its folders
    /// found as more of it lands, wait no more than the first did. The rest lands behind, the folders that show it listed
    /// afresh.
    /// </summary>
    private async Task HistoryAsync(Conversation store, CancellationToken ct)
    {
        BeginHistory(store);
        DateTimeOffset deadline;
        lock (store.Gate)
        {
            deadline = store.ReadBegan + FirstHistoryWait;
        }

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

            var left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return;
            }

            try
            {
                await landed.WaitAsync(left, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return;
            }
        }
    }

    /// <summary>Begins reading a conversation's history behind the listings, unless it has none left, is being read, failed a moment ago or the source is stopping: whether it began it.</summary>
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
            store.ReadBegan = DateTimeOffset.UtcNow;
            var stopping = _stopping.Token;
            store.Reading = Task.Run(() => ReadHistoryAsync(store, stopping), CancellationToken.None);
            return true;
        }
    }

    /// <summary>
    /// For a path a listing from before named (a kept listing, after a restart) that is not among what is read yet: waits
    /// until its conversation's history is read past <paramref name="before"/> (the time its name gives), or else to its end;
    /// whether more was read meanwhile (the path is then worth finding again).
    /// </summary>
    private async Task<bool> HistoryPastAsync(string threadId, DateTimeOffset? before, CancellationToken ct)
    {
        if (!_conversations.TryGetValue(threadId, out var store))
        {
            return false;
        }

        BeginHistory(store);
        var waited = false;
        while (true)
        {
            Task landed;
            lock (store.Gate)
            {
                if (store.Next is null || store.Failed || store.Reading.IsCompleted || (before is { } time && store.Oldest is { } oldest && oldest < time))
                {
                    return waited;
                }

                landed = store.Landed.Task;
            }

            await landed.WaitAsync(ct).ConfigureAwait(false);
            waited = true;
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
    /// Ends a page's turn: it stays with this conversation while it has more to read and none waiting was used later, else
    /// goes to the one waiting that was used last; whether it stays.
    /// </summary>
    private bool PassTurn(Conversation store, bool more)
    {
        lock (_turns)
        {
            var next = -1;
            for (var i = 0; i < _waiting.Count; i++)
            {
                if (next < 0 || _waiting[i].Store.Used > _waiting[next].Store.Used)
                {
                    next = i;
                }
            }

            if (more && (next < 0 || store.Used >= _waiting[next].Store.Used))
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

    /// <summary>
    /// Reads a conversation's history, a page at a time back from where its reading stopped, to its start or the window, a
    /// page's turn going to the conversation used last; the folders that show it listed afresh now and then (behind the
    /// reading) and at the end. A failure keeps what was read; the rest is read when its folder is next listed, a minute on
    /// at the soonest.
    /// </summary>
    private async Task ReadHistoryAsync(Conversation store, CancellationToken ct)
    {
        var relisting = Task.CompletedTask;
        var relisted = DateTimeOffset.UtcNow;
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
                if (relisting.IsCompleted && DateTimeOffset.UtcNow - relisted >= RelistEvery)
                {
                    // behind the reading: a folder slow to build holds up no page
                    relisted = DateTimeOffset.UtcNow;
                    relisting = RelistHistoryAsync(store.ThreadId, ct);
                }
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

        // a refresh joins a listing already on its way, which may have begun before the end: then once more, begun after it
        await relisting.ConfigureAwait(false);
        await RelistHistoryAsync(store.ThreadId, ct).ConfigureAwait(false);
        await RelistHistoryAsync(store.ThreadId, ct).ConfigureAwait(false);
    }

    /// <summary>Notes a folder that shows a conversation's history, by its path, to be listed afresh as the history lands.</summary>
    private void ShowsHistory(TmNode folder, string path)
    {
        if (folder is IHistoryFolder shows)
        {
            _historyFolders.GetOrAdd(shows.ThreadId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase)).TryAdd(path, 0);
        }
    }

    /// <summary>Lists afresh the folders that show a conversation's history: one gone since is no longer listed afresh, one that fails is next time.</summary>
    private async Task RelistHistoryAsync(string threadId, CancellationToken ct)
    {
        if (!_historyFolders.TryGetValue(threadId, out var folders))
        {
            return;
        }

        foreach (var path in folders.Keys)
        {
            try
            {
                await Kept.Cache.RefreshAsync(path, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (VfsException ex) when (ex.Error is VfsError.NotFound or VfsError.NotADirectory)
            {
                // gone since (renamed, or past the window): no longer listed afresh
                Logger.LogDebug("teams: {Path} is no longer listed afresh ({Error})", path, ex.Message);
                folders.TryRemove(path, out _);
            }
            catch (Exception ex)
            {
                Logger.LogDebug("teams: {Path} could not be listed afresh ({Error})", path, ex.Message);
            }
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

/// <summary>A folder that shows a conversation's history as it lands: listed afresh as more is read (<see cref="TeamsSource"/>).</summary>
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
