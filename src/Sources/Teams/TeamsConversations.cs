using System.Collections.Concurrent;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Teams;

/// <summary>
/// A conversation's messages as read (spec 7.9). Its newest messages (chat.md, a channel's posts) are read with its
/// listings: again only as the aggregator's version of it moves on (its newest page merged), whole once a walk interval,
/// back as far as <see cref="NewestMessages"/> or the "within" window. Once its earlier folder is opened, everything
/// before them, back to its start or the window, is its history: read behind the listings, one conversation's at a time,
/// its folder listed afresh as months land.
/// </summary>
public sealed partial class TeamsSource
{
    /// <summary>What a conversation's folders name their earlier messages' folder (a folder per month in it).</summary>
    internal const string EarlierFolderName = "earlier";

    /// <summary>How far back a conversation's newest read goes: what is before it is its history, in its earlier folder.</summary>
    private int NewestMessages => _standIn?.NewestMessages ?? 2_000;

    /// <summary>A page of the chat service's messages: 200 at most ("PageSize query parameter is too large" past it).</summary>
    private int PageSize => _standIn?.PageSize ?? 200;

    /// <summary>How many messages the conversations hold in all before the least used histories are let go (read again when next opened).</summary>
    private const int KeptMessages = 150_000;

    /// <summary>How long an earlier folder's first listing waits for its first months (it is listed again as more land).</summary>
    private static readonly TimeSpan FirstHistoryWait = TimeSpan.FromSeconds(5);

    // how many history pages land between listings of the folders that show them
    private const int RelistEveryPages = 10;

    // how soon a history read that failed is begun again
    private static readonly TimeSpan HistoryRetry = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);

    // one conversation's history read at a time, so the requests stay free for the listings
    private readonly SemaphoreSlim _historyGate = new(1, 1);

    // the folders listed that show a conversation's history (its earlier folder, a channel's posts), by conversation: listed
    // afresh as its history lands
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _historyFolders = new(StringComparer.Ordinal);

    /// <summary>How long a conversation goes without being read whole: an edit or reaction on an older message shows by then.</summary>
    private TimeSpan ReadWholeEvery => _settings.WalkEvery > TimeSpan.Zero ? _settings.WalkEvery : TimeSpan.FromMinutes(30);

    private sealed class Conversation(string threadId)
    {
        public string ThreadId { get; } = threadId;

        public Lock Gate { get; } = new();

        /// <summary>Every message read, newest read or history, by id.</summary>
        public Dictionary<string, TeamsMessage> Messages { get; } = new(StringComparer.Ordinal);

        public string? Version;

        public DateTimeOffset ReadWhole;

        /// <summary>The newest read's oldest message; null where that read reached the start or the window (nothing earlier).</summary>
        public DateTimeOffset? NewestFrom;

        /// <summary>Where the newest read stopped, and where reading back goes on (the newest read's, then the history's; null when done).</summary>
        public string? NewestNext;

        public string? Next;

        public bool HistoryBegun;

        public DateTimeOffset? Oldest;

        public Task Reading = Task.CompletedTask;

        public bool Failed;

        public DateTimeOffset RetryAfter;

        /// <summary>Completed (and replaced) as each page of history lands, and when its reading ends.</summary>
        public TaskCompletionSource Landed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Used;

        public int Count => Messages.Count;

        /// <summary>What chat.md and the posts folder show: the messages from the newest read's on (all, where nothing is earlier).</summary>
        public List<TeamsMessage> Newest()
        {
            lock (Gate)
            {
                return NewestFrom is { } from ? Messages.Values.Where(m => m.Composed is not { } at || at >= from).ToList() : Messages.Values.ToList();
            }
        }

        /// <summary>The messages before the newest read's (its history, as far as read).</summary>
        public List<TeamsMessage> Earlier()
        {
            lock (Gate)
            {
                return NewestFrom is { } from ? Messages.Values.Where(m => m.Composed is { } at && at < from).ToList() : [];
            }
        }

        public List<TeamsMessage> All()
        {
            lock (Gate)
            {
                return Messages.Values.ToList();
            }
        }

        public DateTimeOffset? From
        {
            get
            {
                lock (Gate)
                {
                    return NewestFrom;
                }
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
            await LearnNamesAsync(conversation.Newest(), c).ConfigureAwait(false);
            return conversation;
        }, ct).ConfigureAwait(false);
        store.Used = Environment.TickCount64;
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

    /// <summary>
    /// A channel's posts as its posts folder shows them: its threads with a message from the newest read on (complete, where
    /// the history has brought their opening messages), newest thread first; and whether it has earlier ones.
    /// </summary>
    internal async Task<(IReadOnlyList<IReadOnlyList<TeamsMessage>> Threads, bool Earlier)> ThreadsAsync(string channelId, CancellationToken ct)
    {
        var store = await ChannelConversationAsync(channelId, ct).ConfigureAwait(false);
        var from = store.From;
        return (Threads(store.All()).Where(t => from is null || t[^1].Composed is not { } last || last >= from).ToList(), from is not null);
    }

    // --- the history ---------------------------------------------------------------------------------------------------

    /// <summary>A chat's months before its chat.md, as far as its history is read (its earlier folder): its reading begun, and waited for a moment the first time.</summary>
    internal async Task<IReadOnlyList<(Month Month, DateTimeOffset? Last)>> ChatMonthsAsync(ChatInfo chat, CancellationToken ct)
    {
        var store = await ChatConversationAsync(chat, ct).ConfigureAwait(false);
        await HistoryAsync(store, null, ct).ConfigureAwait(false);
        return store.Earlier().Where(m => m.IsContent && Within(m) && m.Composed is not null)
            .GroupBy(m => Month.Of(m.Composed!.Value))
            .Select(g => (g.Key, (DateTimeOffset?)g.Max(m => m.Composed)))
            .OrderBy(m => m.Key.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>A month of a chat's earlier messages as Markdown, with its attachments: once its history is read past the month's start.</summary>
    internal async Task<(byte[] Markdown, IReadOnlyList<TmNode> Attachments)> ChatMonthAsync(ChatInfo chat, Month month, CancellationToken ct)
    {
        var store = await ChatConversationAsync(chat, ct).ConfigureAwait(false);
        await HistoryAsync(store, month.Start, ct).ConfigureAwait(false);
        var messages = store.Earlier().Where(m => Within(m) && m.Composed is { } at && Month.Of(at) == month).ToList();
        await LearnNamesAsync(messages, ct).ConfigureAwait(false);
        var (attachments, names) = await AttachmentsAsync(messages, ct).ConfigureAwait(false);
        return (TeamsText.ChatMonth(messages, ChatName(chat), month, NameOf, names, TeamsLinks.Chat(LinkHost, chat)), attachments);
    }

    /// <summary>Waits until a chat's history is read past a month's start (or whole): a month a listing from before named, opened before it is read.</summary>
    internal async Task ChatMonthReadAsync(ChatInfo chat, Month month, CancellationToken ct)
        => await HistoryAsync(await ChatConversationAsync(chat, ct).ConfigureAwait(false), month.Start, ct).ConfigureAwait(false);

    /// <summary>A channel's months of earlier posts (its posts' earlier folder), as for a chat (<see cref="ChatMonthsAsync"/>): each thread in its opening's month.</summary>
    internal async Task<IReadOnlyList<(Month Month, DateTimeOffset? Last)>> PostMonthsAsync(string channelId, CancellationToken ct)
    {
        var store = await ChannelConversationAsync(channelId, ct).ConfigureAwait(false);
        await HistoryAsync(store, null, ct).ConfigureAwait(false);
        return EarlierThreads(store).Where(t => t[0].Composed is not null)
            .GroupBy(t => Month.Of(t[0].Composed!.Value))
            .Select(g => (g.Key, g.Max(t => t[^1].Composed)))
            .OrderBy(m => m.Key.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>A month's earlier threads of a channel (those opened in it), once its history is read past the month's start.</summary>
    internal async Task<IReadOnlyList<IReadOnlyList<TeamsMessage>>> PostMonthAsync(string channelId, Month month, CancellationToken ct)
    {
        var store = await ChannelConversationAsync(channelId, ct).ConfigureAwait(false);
        await HistoryAsync(store, month.Start, ct).ConfigureAwait(false);
        var threads = EarlierThreads(store).Where(t => t[0].Composed is { } first && Month.Of(first) == month).ToList();
        await LearnNamesAsync(threads.SelectMany(t => t).ToList(), ct).ConfigureAwait(false);
        return threads;
    }

    /// <summary>As <see cref="ChatMonthReadAsync"/>, for a channel's earlier posts.</summary>
    internal async Task PostMonthReadAsync(string channelId, Month month, CancellationToken ct)
        => await HistoryAsync(await ChannelConversationAsync(channelId, ct).ConfigureAwait(false), month.Start, ct).ConfigureAwait(false);

    /// <summary>A channel's threads with no message from the newest read on.</summary>
    private List<IReadOnlyList<TeamsMessage>> EarlierThreads(Conversation store)
        => store.From is { } from ? Threads(store.All()).Where(t => t[^1].Composed is { } last && last < from).ToList() : [];

    /// <summary>
    /// Begins reading a conversation's history behind the listings, unless it is read, being read, or failed a moment ago;
    /// then waits: until it is read past <paramref name="past"/> (its oldest message read is older), or else, the first
    /// time, until its first page lands or <see cref="FirstHistoryWait"/> has passed. Never past its reading's end.
    /// </summary>
    private async Task HistoryAsync(Conversation store, DateTimeOffset? past, CancellationToken ct)
    {
        BeginHistory(store);
        var deadline = DateTimeOffset.UtcNow + FirstHistoryWait;
        while (true)
        {
            Task landed;
            lock (store.Gate)
            {
                var some = store.NewestFrom is { } from && store.Oldest is { } oldest && oldest < from;
                if (store.Next is null || store.Failed || store.Reading.IsCompleted
                    || (past is { } before ? store.Oldest is { } read && read < before : some))
                {
                    return;
                }

                landed = store.Landed.Task;
            }

            var left = deadline - DateTimeOffset.UtcNow;
            if (past is null && left <= TimeSpan.Zero)
            {
                return;
            }

            try
            {
                await (past is null ? landed.WaitAsync(left, ct) : landed.WaitAsync(ct)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return;
            }
        }
    }

    private void BeginHistory(Conversation store)
    {
        lock (store.Gate)
        {
            if (store.Next is null || !store.Reading.IsCompleted || DateTimeOffset.UtcNow < store.RetryAfter || _stopping.IsCancellationRequested)
            {
                return;
            }

            store.HistoryBegun = true;
            store.Failed = false;
            var stopping = _stopping.Token;
            store.Reading = Task.Run(() => ReadHistoryAsync(store, stopping), CancellationToken.None);
        }
    }

    /// <summary>
    /// Reads a conversation's history, a page at a time back from where its reading stopped, to its start or the window;
    /// listing afresh the folders that show it every few pages and at the end. A failure keeps what was read; the rest is
    /// read when its earlier folder is next opened.
    /// </summary>
    private async Task ReadHistoryAsync(Conversation store, CancellationToken ct)
    {
        try
        {
            await _historyGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                for (var pages = 1; ; pages++)
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

                    var page = await Api.OlderAsync(link, ct).ConfigureAwait(false);
                    var cutoff = Cutoff;
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
                        store.Signal();
                    }

                    if (pages % RelistEveryPages == 0)
                    {
                        await RelistHistoryAsync(store.ThreadId, ct).ConfigureAwait(false);
                    }

                    LetGo(store);
                }
            }
            finally
            {
                _historyGate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogInformation("teams: a conversation's earlier messages could not all be read ({Error}); what was read is shown, the rest is read when its earlier folder is next opened", ex.Message);
            lock (store.Gate)
            {
                store.Failed = true;
                store.RetryAfter = DateTimeOffset.UtcNow + HistoryRetry;
            }
        }
        finally
        {
            lock (store.Gate)
            {
                store.Signal();
            }
        }

        // a refresh joins a listing already on its way, which may have begun before the end: then once more, begun after it
        await RelistHistoryAsync(store.ThreadId, ct).ConfigureAwait(false);
        await RelistHistoryAsync(store.ThreadId, ct).ConfigureAwait(false);
    }

    /// <summary>Notes a folder that shows a conversation's history, by its path, to be listed afresh as the history lands.</summary>
    private void ShowsHistory(TmFolder folder, string path)
    {
        if (folder is IHistoryFolder shows)
        {
            _historyFolders.GetOrAdd(shows.ThreadId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase)).TryAdd(path, 0);
        }
    }

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
            catch (VfsException ex)
            {
                // gone since (renamed, or past the window): no longer listed afresh
                Logger.LogDebug("teams: {Path} is no longer listed afresh ({Error})", path, ex.Message);
                folders.TryRemove(path, out _);
            }
        }
    }

    /// <summary>
    /// While the conversations hold more than <see cref="KeptMessages"/> messages, the least used ones' histories are let go
    /// (back to their newest messages; read again when their earlier folders are next opened).
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
