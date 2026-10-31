using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
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

    // the cloud signed in to: the one chosen, else found from the account at the start (a stand-in's: the global cloud)
    private TeamsCloud _cloud = TeamsCloud.Global;

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

    /// <summary>The host Teams' own links open on, in the cloud signed in to (<see cref="TeamsLinks"/>).</summary>
    internal string LinkHost => _cloud.ApiHost.TrimEnd('/');

    internal ITeamsApi Api => _api ?? throw VfsException.Unavailable("Teams is not signed in to yet");

    private TeamsCache Cache => _cache ?? throw VfsException.Unavailable("Teams is not signed in to yet");

    private KeptListings Kept => _kept ?? throw VfsException.Unavailable("Teams is not signed in to yet");

    protected override async Task OnInitialiseAsync(CancellationToken ct)
    {
        _cloud = _settings.ChosenCloud ?? TeamsCloud.Global;
        _api = _standIn?.Api ?? await SignInAsync(ct).ConfigureAwait(false);
        _cache = new TeamsCache(_stopping.Token);
        _root = new RootFolder();

        // the walks take half the requests, so whoever browses meanwhile has the other half
        var requests = Math.Clamp(_settings.Concurrency, 1, 32);
        var walking = Math.Max(1, requests / 2);
        _kept = new KeptListings(ListFolderAsync, Counters, Logger, new KeptListingsOptions
        {
            Keep = _settings.Keeps,
            UnkeptFor = TimeSpan.Zero,
            RefreshAfter = _settings.Refresh,
            WalkEvery = _settings.WalkEvery,
            Parallelism = walking,
            MaxInFlight = walking,
            StampOf = StampAsync,
            Name = "teams",

            // a folder that cannot be listed answers so at once for a while, so a search over the whole tree pays for it once,
            // not on every pass: a service's answer (a group's site the account cannot open, an error it gave) for a walk
            // interval, the walks asking again; no answer (a service not reached, a token not handed out yet) for the refresh
            // interval
            FailureKeptFor = ex => ex.Error != VfsError.Unavailable && _settings.WalkEvery > TimeSpan.Zero ? _settings.WalkEvery : _settings.Refresh,

            // the listings last run saw open at once at the next start: for the same account and cloud, the same settings
            // that shape the tree, and the same Markdown (a file's size is a fact about how it is written)
            SaveTo = Path.Combine(Context.StateDirectory, "listings.json.gz"),
            SaveKey = $"{_api.Account}|{_cloud.Name}|{_settings.Shape}|md{TeamsText.Format}",
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
        // the cloud before the browser opens: its sign-in page is on the cloud's own Teams host
        var (cloud, how) = await TeamsCloud.ResolveAsync(_settings, Context.StateDirectory, handler: null, ct).ConfigureAwait(false);
        _cloud = cloud;
        Logger.LogInformation("teams: {Account} signs in to {Cloud} ({How})", _settings.AccountHint, cloud.Display, how);
        Context.ReportProgress($"Signing in to Teams in {cloud.Display}");

        // as the other web sessions: the test variable wins, then the setting (which must exist), then whatever is installed
        // (a Chromium browser, else Firefox, which the session drives through WebDriver)
        var browser = BrowserLocator.Find(_settings.Browser, "ANYFS_TEAMS_BROWSER")
            ?? (_settings.Browser is null ? BrowserLocator.FindKind(BrowserKind.Firefox) : null)
            ?? throw new SourceFailedException(_settings.Browser is { } set
                ? $"The browser in the source's Browser setting was not found: {set}."
                : "No Edge, Chrome, Chromium or Firefox was found for the Teams web session; set one in the source's Browser setting.", permanent: true);
        var profile = WebSignInTarget.ProfileFor("teams-browser", _settings.AccountHint, "ANYFS_TEAMS_PROFILES");
        var session = new TeamsWebSession(new Uri(cloud.TeamsHost), browser, profile, _settings.AccountHint, Logger);
        try
        {
            await session.TokenAsync(TeamsService.Aggregator, interactive: true, m => Context.ReportProgress(m), ct).ConfigureAwait(false);
        }
        catch (SignInRequiredException ex)
        {
            throw new SourceFailedException(ex.Message, permanent: ex.Permanent, ex);
        }

        // SharePoint's token (the files, the group sites) and Graph's (every group: GCC High's client mints one, Microsoft
        // 365's never, spike 18) are used while in hand and asked for behind the listings, never waited for in one
        var sought = new List<TeamsService>();
        if (_settings.Files || _settings.Groups)
        {
            sought.Add(TeamsService.SharePoint);
        }

        if (_settings.Groups && cloud.Name != TeamsCloud.Global.Name)
        {
            sought.Add(TeamsService.Graph);
        }

        return new TeamsRest(new Uri(cloud.ApiHost), (service, refused, c) => Renew(session, service, refused, c), _settings.Concurrency, Logger, sharePointHost: () => session.SharePointHost,
            inHand: session.InHand, seek: c => session.SeekAsync(sought, c, _teamSite), graphHost: cloud.GraphHost)
        {
            Account = session.Account ?? _settings.AccountHint,
            Mri = session.Mri,
        };
    }

    /// <summary>
    /// A service's bearer, from the session; after a refusal, a fresh one (the page renews it) rather than the one refused.
    /// SharePoint's and Graph's only while in hand: the page mints them as it gets round to it, and a request waiting on a
    /// browser load for one would hang the listing that made it (they are asked for behind the listings instead).
    /// </summary>
    private async Task<string> Renew(TeamsWebSession session, TeamsService service, string? refused, CancellationToken ct)
    {
        if (refused is not null)
        {
            session.Refused(service, refused);
        }

        if (!TeamsServices.Core.Contains(service))
        {
            if (session.InHand(service) is { } inHand)
            {
                return inHand;
            }

            SeekTokens();
            throw TokenNotHandedOutException.Refusal(service);
        }

        try
        {
            return await session.TokenAsync(service, interactive: false, _ => { }, ct).ConfigureAwait(false);
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
        if (node is MarkdownFile markdown)
        {
            // built now (the last run's listing named it): served as built, and the listing put right where its length differs
            Serve(normalized, markdown.Content);
            if ((await Kept.Cache.StatAsync(normalized, ct, StringComparison.OrdinalIgnoreCase).ConfigureAwait(false))?.Length is { } told && told != markdown.Content.Length)
            {
                Kept.Cache.Update(normalized, markdown.Entry, StringComparison.OrdinalIgnoreCase);
            }
        }

        return node is TmFile file ? await Guard(() => file.OpenAsync(this, ct)).ConfigureAwait(false) : throw VfsException.IsADirectory(path);
    }

    // the folders whose listing failed, each said once in the log: a drive shows one as empty, and the failure is kept a
    // while (FailureKeptFor), so the log is where to see why
    private readonly ConcurrentDictionary<string, byte> _unlisted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A folder's listing (what <see cref="KeptListings"/> keeps); the Markdown files' bytes are kept beside it, for the opens.</summary>
    private async IAsyncEnumerable<VfsEntry> ListFolderAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        TmFolder folder;
        IReadOnlyList<TmNode> children;
        try
        {
            folder = await ResolveAsync(path, ct).ConfigureAwait(false) as TmFolder ?? throw VfsException.NotADirectory(path);
            ShowsHistory(folder, VfsPath.Normalize(path));
            children = await Guard(() => folder.ListAsync(this, ct)).ConfigureAwait(false);
        }
        catch (VfsException ex) when (ex.Error is not (VfsError.NotFound or VfsError.NotADirectory))
        {
            if (_unlisted.TryAdd(VfsPath.Normalize(path), 0))
            {
                Logger.LogInformation("teams: '{Path}' could not be listed ({Error}); answered so for a while, the walks asking again", VfsPath.Normalize(path), ex.Message);
            }

            throw;
        }

        // a conversation listed while its history is still being read is not whole: not kept by its stamp
        if (folder is IHistoryFolder shows && !ConversationWhole(shows.ThreadId))
        {
            Kept.Cache.Unstamp(VfsPath.Normalize(path));
        }

        foreach (var child in children)
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

        var segments = VfsPath.Split(normalized);
        var names = new List<string>(segments.Length);
        (TmNode Folder, int Index, string ThreadId)? history = null;
        for (var i = 0; i < segments.Length; i++)
        {
            if (node is not TmFolder folder)
            {
                throw VfsException.NotADirectory(path);
            }

            var found = await Guard(() => folder.FindAsync(this, segments[i], ct)).ConfigureAwait(false);
            if (found is null)
            {
                // what a listing from before named (a kept one, after a restart) may be in a conversation's history not read
                // yet: found again once the history is read past the time its name gives, or else to its end
                var time = segments.Skip(i).Select(TeamsNames.TimeOf).FirstOrDefault(t => t is not null);
                if (history is { } at && await HistoryPastAsync(at.ThreadId, time, ct).ConfigureAwait(false))
                {
                    (node, i) = (at.Folder, at.Index);
                    names.RemoveRange(i + 1, names.Count - i - 1);
                    continue;
                }

                throw VfsException.NotFound(path);
            }

            names.Add(found.Name);
            if (found is IHistoryFolder shows)
            {
                history = (found, i, shows.ThreadId);
                ShowsHistory(found, VfsPath.Join(names));
            }

            node = found;
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
        catch (VfsException ex) when (ex.Error == VfsError.Unavailable && !TokenNotHandedOutException.IsCause(ex))
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

            // the people a chat is named for: learn their names once, so a 1:1 or untitled group is named, not an mri; and the
            // account's own organisation, so chat.md can tell another's
            var me = Api.Mri is { } mri ? new[] { mri } : [];
            await LearnPeopleAsync(chats.SelectMany(ch => ch.MemberMris).Where(m => !IsMe(m)).Concat(me), "their chats are named by id", c).ConfigureAwait(false);
            return structure with { Chats = chats };
        }, ct);

    // the people looked up, by mri (chat.md marks a guest, a bot, another organisation's person), and each mri asked about:
    // once, whether the middle tier names it or not
    private readonly ConcurrentDictionary<string, Person> _people = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _asked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The people not yet asked about, a hundred a call: their names and kinds; a call that fails leaves those as mris, to ask again.</summary>
    private async Task LearnPeopleAsync(IEnumerable<string> mris, string otherwise, CancellationToken ct)
    {
        foreach (var batch in mris.Where(m => !_asked.ContainsKey(m)).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(100))
        {
            try
            {
                var people = await Api.PeopleAsync(batch, ct).ConfigureAwait(false);
                foreach (var person in people)
                {
                    _people[person.Mri] = person;
                }

                Remember(people.Select(p => (p.Mri, p.DisplayName)));
                foreach (var mri in batch)
                {
                    _asked.TryAdd(mri, 0);
                }
            }
            catch (VfsException ex)
            {
                Logger.LogInformation("teams: {Count} people's names could not be read ({Error}); {Otherwise}", batch.Length, ex.Message, otherwise);
            }
        }
    }

    /// <summary>The names of a conversation's senders, from its messages, and of the people who reacted, looked up.</summary>
    private Task LearnNamesAsync(IReadOnlyList<TeamsMessage> messages, CancellationToken ct)
    {
        LearnNames(messages);
        return LearnPeopleAsync(messages.SelectMany(m => m.Reactions).SelectMany(r => r.Mris), "their reactions show ids", ct);
    }

    /// <summary>The oldest a conversation's last message may be to show ("within" days), or null for all.</summary>
    private DateTimeOffset? Cutoff => _settings.Within is { } within ? DateTimeOffset.UtcNow - within : null;

    /// <summary>
    /// A chat as Markdown, the whole conversation (a long one's history read behind the listings, its folder listed afresh as
    /// it lands), with its attachments (the files it shares, its inline images) and its meetings: a meeting chat's
    /// meeting.md, and its recordings.
    /// </summary>
    internal async Task<ChatParts> ChatAsync(ChatInfo chat, CancellationToken ct)
    {
        var (messages, note) = await ChatMessagesAsync(chat, ct).ConfigureAwait(false);
        var (attachments, names) = await AttachmentsAsync(messages, ct).ConfigureAwait(false);
        var now = (await StructureAsync(ct).ConfigureAwait(false)).Chats.FirstOrDefault(ch => ch.Id == chat.Id) ?? chat;
        var (meeting, recordings, recorded) = await MeetingsAsync(now, messages, ct).ConfigureAwait(false);
        var markdown = TeamsText.Chat(messages, ChatName(chat), NameOf, names with { Recordings = recorded }, DetailsOf(now, messages, note));
        return new ChatParts(markdown, attachments, meeting, recordings);
    }

    /// <summary>A chat's attachments and recordings alone, for finding a file among them: no chat.md built (a folder of images opened is a find per image).</summary>
    internal async Task<(IReadOnlyList<TmNode> Attachments, IReadOnlyList<TmNode> Recordings)> ChatFilesAsync(ChatInfo chat, CancellationToken ct)
    {
        var (messages, _) = await ChatMessagesAsync(chat, ct).ConfigureAwait(false);
        var (attachments, _) = await AttachmentsAsync(messages, ct).ConfigureAwait(false);
        var (_, recordings, _) = await RecordingsAsync(messages, ct).ConfigureAwait(false);
        return (attachments, recordings);
    }

    // a meeting recording's length, by its video's address (-1: the media service does not serve it), once asked
    private readonly ConcurrentDictionary<string, long> _mediaLengths = new(StringComparer.Ordinal);

    /// <summary>
    /// A chat's meetings (spec 7.9), from the chat and its newest messages: a meeting chat's meeting.md, where it names its
    /// meeting, has held it or has a recording (null else); and its recordings, each a file of the length the media
    /// service gives (one it does not serve is left out), with their names by their videos' addresses.
    /// </summary>
    private async Task<(byte[]? Meeting, IReadOnlyList<TmNode> Recordings, IReadOnlyDictionary<string, string> Names)> MeetingsAsync(
        ChatInfo chat, IReadOnlyList<TeamsMessage> messages, CancellationToken ct)
    {
        var (recordings, nodes, files) = await RecordingsAsync(messages, ct).ConfigureAwait(false);
        if (chat.Kind != ChatKind.Meeting || !_settings.Meetings)
        {
            return (null, nodes, files);
        }

        var calls = TeamsMeetings.Calls(messages);
        if (chat.Meeting is null && calls.Count == 0 && recordings.Count == 0)
        {
            return (null, nodes, files);
        }

        var organiser = await OrganiserAsync(chat.Meeting?.OrganizerId, ct).ConfigureAwait(false);
        return (TeamsText.Meeting(ChatName(chat), chat.Meeting, organiser, calls, recordings, files, TeamsLinks.Chat(LinkHost, chat)), nodes, files);
    }

    /// <summary>A meeting's organiser by name: their object id as an mri, looked up once ("you" for the account; an id where the lookup names no one).</summary>
    private async Task<string?> OrganiserAsync(string? id, CancellationToken ct)
    {
        if (id is null)
        {
            return null;
        }

        var mri = id.Contains(':', StringComparison.Ordinal) ? id : $"8:orgid:{id}";
        if (IsMe(mri))
        {
            return "you";
        }

        await LearnPeopleAsync([mri], "a meeting's organiser shows as an id", ct).ConfigureAwait(false);
        return NameOf(mri);
    }

    /// <summary>
    /// A conversation's meeting recordings (a chat's, or a channel post's): each a file of the length the media service
    /// gives (one it does not serve is left out), with their names by their videos' addresses; none with Meetings off.
    /// </summary>
    internal async Task<(IReadOnlyList<MeetingRecording> All, IReadOnlyList<TmNode> Files, IReadOnlyDictionary<string, string> Names)> RecordingsAsync(
        IReadOnlyList<TeamsMessage> messages, CancellationToken ct)
    {
        if (!_settings.Meetings)
        {
            return ([], [], new Dictionary<string, string>());
        }

        var recordings = TeamsMeetings.Recordings(messages);
        var served = new List<(MeetingRecording Recording, long Length)>();
        foreach (var recording in recordings)
        {
            if (await MediaLengthAsync(recording.Video, ct).ConfigureAwait(false) is { } length)
            {
                served.Add((recording, length));
            }
        }

        var names = TeamsNames.Unique(served.Select(s => RecordingName(s.Recording)).ToList());
        var files = served.Select((s, i) => (s.Recording.Video, Name: names[i])).ToDictionary(f => f.Video, f => f.Name, StringComparer.Ordinal);
        return (recordings, served.Select((s, i) => (TmNode)new RecordingFile(names[i], s.Recording.Video, s.Length, s.Recording.At)).ToList(), files);
    }

    /// <summary>A recording's file name: as Teams named it, else its time and title; an MP4's, as the media service serves it.</summary>
    private static string RecordingName(MeetingRecording recording)
    {
        var name = recording.FileName
            ?? string.Join(' ', new[] { recording.At?.ToLocalTime().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture), recording.Title ?? "Meeting recording" }.OfType<string>());
        return name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? name : name + ".mp4";
    }

    /// <summary>A recording's length, from the media service's answer to a read from its start, kept; null where it does not serve it (kept so too, unless it could not be reached).</summary>
    private async Task<long?> MediaLengthAsync(string address, CancellationToken ct)
    {
        if (_mediaLengths.TryGetValue(address, out var known))
        {
            return known > 0 ? known : null;
        }

        try
        {
            using var response = await Api.MediaAsync(address, 0, ct).ConfigureAwait(false);
            var length = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
            _mediaLengths[address] = length is > 0 ? length.Value : -1;
            return length is > 0 ? length : null;
        }
        catch (VfsException ex)
        {
            Logger.LogInformation("teams: a meeting recording is left out ({Error})", ex.Message);
            if (ex.Error is VfsError.NotFound or VfsError.AccessDenied)
            {
                _mediaLengths[address] = -1;
            }

            return null;
        }
    }

    /// <summary>A meeting recording, read in ranges from Teams' media service as it is read: nothing is copied until it is.</summary>
    internal IVfsFile OpenMedia(string address, string name, long length)
        => new RandomAccessOverStream(length, async (offset, ct) =>
        {
            var response = await Api.MediaAsync(address, offset, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                response.Dispose();
                return Stream.Null;
            }

            return await ResponseStream.FromOffsetAsync(response, offset, name, null, ct).ConfigureAwait(false);
        }, maxStreams: Math.Clamp(_settings.Concurrency, 1, 8), maxSkipBytes: 1 << 20);

    /// <summary>
    /// What chat.md says of its chat above the messages: its link, kind and members (the account as "you", then by name; a
    /// guest, a bot or another organisation's person marked), who started it and when, whether it is with other
    /// organisations or muted, its pinned messages among those read, and, while chat.md does not reach back to the
    /// chat's start yet, how far it does (<paramref name="history"/>).
    /// </summary>
    private ChatDetails DetailsOf(ChatInfo chat, IReadOnlyList<TeamsMessage> messages, string? history)
    {
        var home = _people.Values.FirstOrDefault(p => IsMe(p.Mri))?.Organisation;
        var members = chat.MemberMris.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(m => (Me: IsMe(m), Name: MemberName(m, home)))
            .OrderBy(m => m.Me ? 0 : 1).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => m.Name).ToList();
        var pinnedIds = chat.PinnedIds.ToHashSet(StringComparer.Ordinal);
        var pinned = messages.Where(m => m.IsContent && m.Deleted is null && (m.Pinned || pinnedIds.Contains(m.Id)))
            .OrderBy(m => m.Composed ?? DateTimeOffset.MinValue).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        return new ChatDetails(TeamsLinks.Chat(LinkHost, chat), chat.Kind, members.Take(TeamsText.MembersShown).ToList(), members.Count, chat.Created,
            chat.CreatorMri is { } creator ? (IsMe(creator) ? "you" : NameOf(creator)) : null, chat.External, chat.Muted, pinned, history);
    }

    /// <summary>A chat member as chat.md names them: "you", else their name, marked where a guest, a bot or of an organisation other than the account's.</summary>
    private string MemberName(string mri, string? home)
    {
        if (IsMe(mri))
        {
            return "you";
        }

        var name = NameOf(mri);
        var note = !_people.TryGetValue(mri, out var person) ? null
            : person.Bot ? "bot"
            : person.Guest ? "guest"
            : person.Organisation is { } organisation && home is not null && !organisation.Equals(home, StringComparison.OrdinalIgnoreCase) ? organisation
            : null;
        return note is null ? name : $"{name} ({note})";
    }

    // --- groups --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether what reads the files is in hand (never waited for): SharePoint's token, or Microsoft Graph's where the client
    /// mints one (GCC High's). Other groups, the files folders and the shared files are served only while it is; when it is
    /// not, it is asked for behind the listings.
    /// </summary>
    internal bool FilesReady()
    {
        if (Api.SharePointReady || Api.GraphReady)
        {
            return true;
        }

        SeekTokens();
        return false;
    }

    /// <summary>
    /// The group sites the account can open other than its teams' (Other groups); none while nothing that reads them is in
    /// hand. A site found without its group (Microsoft Search names none) is told from a team's by the team's own site.
    /// </summary>
    internal async Task<IReadOnlyList<GroupSite>> OtherGroupsAsync(CancellationToken ct)
    {
        var teams = (await StructureAsync(ct).ConfigureAwait(false)).Teams;
        var groups = teams.Select(t => t.GroupId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sites = await GroupSitesAsync(FirstSitesWait, ct).ConfigureAwait(false) ?? [];
        var teamSites = sites.Any(s => s.GroupId is null) ? await TeamSitesAsync(teams, ct).ConfigureAwait(false) : [];
        return sites.Where(s => s.GroupId is { } group ? !groups.Contains(group) : s.SiteUrl is not { } url || !teamSites.Contains(TeamsFiles.SiteKey(url)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The account's teams' own sites (as <see cref="TeamsFiles.SiteKey"/>), each looked up once a walk interval; a team whose site cannot be found is left out.</summary>
    private Task<HashSet<string>> TeamSitesAsync(IReadOnlyList<TeamInfo> teams, CancellationToken ct)
        => Cache.GetAsync("teamsites", ReadWholeEvery, async c =>
        {
            var sites = await Task.WhenAll(teams.Where(t => t.GroupId is not null).Select(async team =>
            {
                try
                {
                    return await Api.SiteOfGroupAsync(team.GroupId!, c).ConfigureAwait(false);
                }
                catch (VfsException ex)
                {
                    Logger.LogInformation("teams: the site of the team {Team} could not be found ({Error})", team.DisplayName, ex.Message);
                    return null;
                }
            })).ConfigureAwait(false);
            return sites.OfType<string>().Select(TeamsFiles.SiteKey).ToHashSet(StringComparer.Ordinal);
        }, ct);

    // the group sites as last read, and when; one older than a walk interval is answered and read again behind (a group list
    // changes seldom, and a large organisation's takes many requests)
    private sealed record SitesRead(IReadOnlyList<GroupSite>? Sites, DateTimeOffset At);

    private SitesRead? _sitesRead;
    private Task _sitesReading = Task.CompletedTask;

    // how long Other groups' first listing waits for the first read before answering with none (it is listed again when the
    // read lands)
    private static readonly TimeSpan FirstSitesWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The group sites the account can open (<see cref="ITeamsApi.GroupSitesAsync"/>): null while nothing that reads them is
    /// in hand (asked for behind the listings, never waited for). Read behind the listings: the first read is waited for up to
    /// <paramref name="firstWait"/> (a large organisation's takes longer: Other groups is listed again when it lands); after
    /// it, the last read answers at once and one older than a walk interval is read again behind. A read that fails keeps
    /// the last one.
    /// </summary>
    private async Task<IReadOnlyList<GroupSite>?> GroupSitesAsync(TimeSpan firstWait, CancellationToken ct)
    {
        if (!FilesReady())
        {
            return null;
        }

        if (_sitesRead is { } read)
        {
            if (DateTimeOffset.UtcNow - read.At > ReadWholeEvery)
            {
                Behind(ref _sitesReading, "reading the group sites", ReadSitesAsync);
            }

            return read.Sites;
        }

        Behind(ref _sitesReading, "reading the group sites", ReadSitesAsync);
        Task reading;
        lock (_behind)
        {
            reading = _sitesReading;
        }

        try
        {
            await reading.WaitAsync(firstWait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // answered with none for now
        }

        return _sitesRead?.Sites;
    }

    private async Task ReadSitesAsync(CancellationToken ct)
    {
        IReadOnlyList<GroupSite>? sites = null;
        try
        {
            sites = await Api.GroupSitesAsync(ct).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            Logger.LogInformation("teams: the group sites could not be listed ({Error})", ex.Message);
        }

        _sitesRead = new SitesRead(sites ?? _sitesRead?.Sites, DateTimeOffset.UtcNow);

        // Other groups' listing shows what came
        try
        {
            if (_settings.Groups && FilesReady())
            {
                await Kept.Cache.RefreshAsync("Other groups", ct).ConfigureAwait(false);
            }
        }
        catch (VfsException ex)
        {
            Logger.LogInformation("teams: Other groups could not be listed afresh ({Error})", ex.Message);
        }
    }

    // a file built behind the listings (groups.md, all-groups.md, calls.md, activity.md) as last built: its bytes (null:
    // nothing to show); how far it reached and how far the tokens in hand let it try (all-groups.md: 2 every group, from
    // Graph; 1 the groups SharePoint's search shows; groups.md: 1 the account's groups of every kind, from Graph; 0 its
    // teams'; calls.md and activity.md: 0; -1 unread); when
    private sealed record Built(byte[]? Bytes, int Reached, int Tried, DateTimeOffset At);

    private sealed class BuiltFile(string name, Func<string, byte[]> unread)
    {
        public string Name { get; } = name;

        /// <summary>What the file says when it could not be built and none was before: why.</summary>
        public Func<string, byte[]> Unread { get; } = unread;

        public Built? Last;

        public Task Building = Task.CompletedTask;
    }

    private readonly BuiltFile _myGroups = new("groups.md", TeamsText.GroupsUnread);
    private readonly BuiltFile _allGroups = new("all-groups.md", TeamsText.GroupsUnread);

    // how soon a build that failed, or reached less than the one kept, is tried again
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    /// <summary>
    /// <c>groups.md</c> (spec 7.9): the groups the account is in, with their details: of every kind from Microsoft Graph, else
    /// its teams'. Built behind the listings; until the first build lands the file says it is being read.
    /// </summary>
    internal byte[] GroupsMarkdown() => Markdown(_myGroups, Api.GraphReady ? 1 : 0, BuildMyGroupsAsync) ?? TeamsText.GroupsBeingRead();

    /// <summary>
    /// <c>all-groups.md</c> (spec 7.9): every group there is, with its details: the organisation's every group from Microsoft
    /// Graph, else those SharePoint's search shows. Built behind the listings (Graph lists a large organisation's groups a
    /// page at a time, tens of megabytes of them); null, so not shown, while there is no token that lists more than
    /// groups.md does and none was built.
    /// </summary>
    internal byte[]? AllGroupsMarkdown()
    {
        var reach = Api.GraphReady ? 2 : Api.SharePointReady ? 1 : 0;
        if (reach == 0 && _allGroups.Last is null)
        {
            return null;
        }

        return Markdown(_allGroups, reach, BuildAllGroupsAsync) ?? (_allGroups.Last is null ? TeamsText.GroupsBeingRead() : null);
    }

    private readonly BuiltFile _calls = new("calls.md", why => TeamsText.Unread("Calls", "The call history", why));
    private readonly BuiltFile _activity = new("activity.md", why => TeamsText.Unread("Activity", "The activity feed", why));

    // how far back calls.md and activity.md read
    private const int MaxCalls = 1000;
    private const int MaxActivity = 200;

    /// <summary>How old calls.md and activity.md may be before they are built again: the refresh interval, a minute at the least.</summary>
    private TimeSpan FeedsEvery => _settings.Refresh > TimeSpan.FromMinutes(1) ? _settings.Refresh : TimeSpan.FromMinutes(1);

    /// <summary>
    /// <c>calls.md</c> (spec 7.9): the account's call history (within the "within" window), built behind the listings and
    /// again each refresh interval; null, so not shown, where the account has none or before the first build lands.
    /// </summary>
    internal byte[]? CallsMarkdown() => Markdown(_calls, 0, BuildCallsAsync, FeedsEvery);

    /// <summary><c>activity.md</c> (spec 7.9): the account's activity feed and mentions, as <see cref="CallsMarkdown"/> is built.</summary>
    internal byte[]? ActivityMarkdown() => Markdown(_activity, 0, BuildActivityAsync, FeedsEvery);

    private async Task<(byte[]? Bytes, int Reached)> BuildCallsAsync(CancellationToken ct)
    {
        if (await Api.CallsAsync(MaxCalls, ct).ConfigureAwait(false) is not { } calls)
        {
            return (null, 0);
        }

        return (TeamsText.Calls(Cutoff is { } since ? calls.Where(c => c.Start is null || c.Start >= since).ToList() : calls), 0);
    }

    private async Task<(byte[]? Bytes, int Reached)> BuildActivityAsync(CancellationToken ct)
    {
        if (await Api.ActivityAsync(MaxActivity, ct).ConfigureAwait(false) is not { } items)
        {
            return (null, 0);
        }

        // where each happened: a channel as its team and name, a chat by the name its folder has
        var structure = await StructureAsync(ct).ConfigureAwait(false);
        var where = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var team in structure.Teams)
        {
            foreach (var channel in team.Channels)
            {
                where.TryAdd(channel.Id, $"{team.DisplayName} / {channel.DisplayName}");
            }
        }

        foreach (var chat in structure.Chats)
        {
            where.TryAdd(chat.Id, $"Chat: {ChatName(chat)}");
        }

        var shown = Cutoff is { } since ? items.Where(i => i.When is null || i.When >= since).ToList() : items;
        return (TeamsText.Activity(shown, i => i.ThreadId is { } id && where.TryGetValue(id, out var place) ? place : i.Topic), 0);
    }

    /// <summary>
    /// A channel's thread details (its SharePoint site and folder, how many are in it), kept for a walk interval, a thread the
    /// chat service does not answer for too (its details are then left out of channel.md and team.md).
    /// </summary>
    internal async Task<ThreadDetails?> ThreadDetailsAsync(string threadId, CancellationToken ct)
        => (await Cache.GetAsync($"thread:{threadId}", ReadWholeEvery, async c =>
        {
            try
            {
                var details = await Api.ThreadDetailsAsync(threadId, c).ConfigureAwait(false);
                _teamSite ??= details?.SiteUrl;
                return new[] { details };
            }
            catch (VfsException ex)
            {
                Logger.LogInformation("teams: a channel's details could not be read ({Error})", ex.Message);
                return new ThreadDetails?[] { null };
            }
        }, ct).ConfigureAwait(false))[0];

    /// <summary>
    /// What team.md tells beyond the aggregator: its General channel's thread details (<see cref="ThreadDetailsAsync"/>); with
    /// the names of the people who made its channels looked up for channel.md, once a walk interval (one not found shows
    /// as an id).
    /// </summary>
    internal async Task<ThreadDetails?> TeamDetailsAsync(TeamInfo team, CancellationToken ct)
    {
        await Cache.GetAsync($"makers:{team.Id}", ReadWholeEvery, async c =>
        {
            await LearnPeopleAsync(team.Channels.Select(ch => ch.CreatorMri).OfType<string>(), "its channels' makers show as ids", c).ConfigureAwait(false);
            return team;
        }, ct).ConfigureAwait(false);
        return team.Channels.FirstOrDefault(c => c.IsGeneral) is { } general ? await ThreadDetailsAsync(general.Id, ct).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// A file built behind the listings, never in them: as last built, and built again at first, each walk interval (or
    /// <paramref name="every"/>), and as soon as a token comes that reaches further; a token gone leaves what was built.
    /// </summary>
    private byte[]? Markdown(BuiltFile file, int reach, Func<CancellationToken, Task<(byte[]? Bytes, int Reached)>> build, TimeSpan? every = null)
    {
        var last = file.Last;
        var interval = every ?? ReadWholeEvery;
        var stale = last is not null && DateTimeOffset.UtcNow - last.At > interval;
        if (last is null || reach > last.Tried || (stale && reach >= last.Reached))
        {
            Behind(ref file.Building, $"reading {file.Name}", ct => BuildAsync(file, reach, build, interval, ct));
        }

        return last?.Bytes;
    }

    /// <summary>
    /// Builds a file: kept, unless it reached less than the one before (a token gone, a list refused or throttled part way),
    /// which then stays and is tried again soon; a failure keeps the one before, else the file says why. The root is listed
    /// afresh after the first build and whenever the file changed, so its listing measures it.
    /// </summary>
    private async Task BuildAsync(BuiltFile file, int tried, Func<CancellationToken, Task<(byte[]? Bytes, int Reached)>> build, TimeSpan every, CancellationToken ct)
    {
        // tried again in five minutes, or the file's own interval where that is sooner
        var first = file.Last is null;
        var before = file.Last?.Bytes;
        var retry = DateTimeOffset.UtcNow - every + (every < RetryAfterFailure ? every : RetryAfterFailure);
        try
        {
            var (bytes, reached) = await build(ct).ConfigureAwait(false);
            file.Last = file.Last is { Bytes: not null } kept && reached < kept.Reached
                ? kept with { At = retry }
                : new Built(bytes, reached, Math.Max(tried, reached), DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.LogInformation("teams: {File} could not be read ({Error})", file.Name, ex.Message);
            file.Last = file.Last is { Reached: >= 0 } kept ? kept with { At = retry } : new Built(file.Unread(ex.Message), -1, tried, retry);
        }

        var after = file.Last?.Bytes;
        if (first || (before is null ? after is not null : after is null || !before.AsSpan().SequenceEqual(after)))
        {
            // a refresh joins a listing of the root already on its way, which may have measured the file before this build
            // landed (the files build at once at the start): then once more, begun after it
            if (!Measured(await Kept.Cache.RefreshAsync("", ct).ConfigureAwait(false), file))
            {
                await Kept.Cache.RefreshAsync("", ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether a root listing has a built file as it is now: served with its bytes, or not shown where it has none.</summary>
    private bool Measured(IReadOnlyList<VfsEntry> root, BuiltFile file)
        => file.Last?.Bytes is { } bytes
            ? _served.TryGetValue(file.Name, out var served) && served.Latest.AsSpan().SequenceEqual(bytes)
            : !root.Any(e => e.Name.Equals(file.Name, StringComparison.OrdinalIgnoreCase));

    private async Task<(byte[]? Bytes, int Reached)> BuildMyGroupsAsync(CancellationToken ct)
    {
        var directory = await Api.MyGroupsAsync((await StructureAsync(ct).ConfigureAwait(false)).Teams, ct).ConfigureAwait(false);
        if (directory.Scope == GroupsScope.Teams)
        {
            // the account's other groups need Graph's token
            SeekTokens();
        }

        return (TeamsText.Groups(directory, Api.Account), directory.Scope == GroupsScope.Yours ? 1 : 0);
    }

    private async Task<(byte[]? Bytes, int Reached)> BuildAllGroupsAsync(CancellationToken ct)
    {
        var teams = (await StructureAsync(ct).ConfigureAwait(false)).Teams;

        // Graph lists every group with no search: the sites a search has already found fill in where each group's site is;
        // without Graph, SharePoint's search is the list, waited for whole
        var sites = Api.GraphReady ? _sitesRead?.Sites : await GroupSitesAsync(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        var directory = await Api.AllGroupsAsync(teams, sites, ct).ConfigureAwait(false);
        if (directory is not { Scope: GroupsScope.Organisation })
        {
            SeekTokens();
        }

        return directory is null ? (null, 0) : (TeamsText.Groups(directory, Api.Account), directory.Scope == GroupsScope.Organisation ? 2 : 1);
    }

    // --- behind the listings -------------------------------------------------------------------------------------------

    // work no listing waits on (asking for tokens, reading the groups and the group sites): one of each at a time, awaited
    // when the source stops
    private readonly object _behind = new();
    private Task _seek = Task.CompletedTask;

    /// <summary>Starts work behind the listings unless the same work is still running or the source is stopping; a failure is logged, never thrown.</summary>
    private void Behind(ref Task running, string what, Func<CancellationToken, Task> work)
    {
        lock (_behind)
        {
            if (!running.IsCompleted || _stopping.IsCancellationRequested)
            {
                return;
            }

            var stopping = _stopping.Token;
            running = Task.Run(async () =>
            {
                try
                {
                    await work(stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    Logger.LogInformation("teams: {What} failed behind the listings ({Error})", what, ex.Message);
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Asks behind the listings for the tokens the page has not handed out (SharePoint's; Graph's in the US Gov clouds), one ask
    /// at a time and as the session's back-off allows: by a load of a team's SharePoint site where one is known (its own
    /// page hands both out), else of Teams' page. When one comes, the root and the channels are listed afresh, so Other
    /// groups, the files folders and groups.md show what it opens.
    /// </summary>
    private void SeekTokens() => Behind(ref _seek, "asking for the tokens", async ct =>
    {
        await KnowATeamSiteAsync(ct).ConfigureAwait(false);
        if (await Api.SeekTokensAsync(ct).ConfigureAwait(false))
        {
            // the group sites read again: behind the answer when there is one, else at the next asking; and the folders that
            // failed for want of a token asked again when next listed
            _sitesRead = _sitesRead is { Sites: not null } read ? read with { At = DateTimeOffset.MinValue } : null;
            Kept.Cache.ForgetFailures();
            await RelistAsync(ct).ConfigureAwait(false);
        }
    });

    // a team's SharePoint site, as a channel's thread names it: the page the tokens are asked for with (see SeekTokens)
    private string? _teamSite;

    /// <summary>
    /// Learns a team's SharePoint site for the token load, where none is known yet: the first team's whose General channel's
    /// thread names one (read with the core tokens, kept as channel.md keeps it); none where no thread names one.
    /// </summary>
    private async Task KnowATeamSiteAsync(CancellationToken ct)
    {
        try
        {
            foreach (var team in _teamSite is null ? (await StructureAsync(ct).ConfigureAwait(false)).Teams : [])
            {
                if (team.Channels.FirstOrDefault(c => c.IsGeneral) is { } general && await ThreadDetailsAsync(general.Id, ct).ConfigureAwait(false) is { SiteUrl: not null })
                {
                    return;
                }
            }
        }
        catch (VfsException ex)
        {
            Logger.LogInformation("teams: no team's SharePoint site was found to ask the tokens with ({Error}); Teams' page is asked", ex.Message);
        }
    }

    /// <summary>The root and every channel listed afresh behind their answers: what a token that came since opens shows.</summary>
    private async Task RelistAsync(CancellationToken ct)
    {
        await Kept.Cache.RefreshAsync("", ct).ConfigureAwait(false);
        if (!_settings.Files || _root is not { } root)
        {
            return;
        }

        foreach (var team in (await root.ListAsync(this, ct).ConfigureAwait(false)).OfType<TeamFolder>())
        {
            foreach (var channel in (await team.ListAsync(this, ct).ConfigureAwait(false)).OfType<ChannelFolder>())
            {
                await Kept.Cache.RefreshAsync(VfsPath.Join(team.Name, channel.Name), ct).ConfigureAwait(false);
            }
        }
    }

    // --- files ---------------------------------------------------------------------------------------------------------

    // inline images by address: an image's bytes never change; kept to 64 MiB in all, then begun again. Each one's length
    // and extension are kept once read, so a listing reads an image once to measure it, and its bytes are read again
    // only when it is opened after they were let go
    private const long ImagesKept = 64L << 20;
    private readonly ConcurrentDictionary<string, TeamsImage> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (long Length, string Extension)> _imageSizes = new(StringComparer.Ordinal);
    private long _imageBytes;

    /// <summary>
    /// A conversation's attachments (spec 7.9): the files its messages share, each once, as SharePoint or OneDrive has them now
    /// (one gone or out of reach is left out), and its inline images, read from Teams' media service and held in memory,
    /// named by their message's time (<c>2026-10-29_1003_image-1.png</c>); with the names its Markdown links them by.
    /// </summary>
    internal async Task<(IReadOnlyList<TmNode> Nodes, AttachmentNames Names)> AttachmentsAsync(IReadOnlyList<TeamsMessage> messages, CancellationToken ct)
    {
        if (!_settings.Files)
        {
            return ([], AttachmentNames.None);
        }

        // the shared files only while what reads them is in hand (never waited for); the images are Teams' own
        var shared = messages.Any(m => m.Files.Count > 0) && FilesReady();
        var found = new List<(string Name, Func<string, TmNode> Make, string? Image, string? File)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in messages.Where(m => m.IsContent && m.Deleted is null).OrderBy(m => m.Composed ?? DateTimeOffset.MinValue).ThenBy(m => m.Id, StringComparer.Ordinal))
        {
            foreach (var file in shared ? message.Files : [])
            {
                if (!seen.Add(file.Url) || TeamsFiles.Locate(file.Url, file.SiteUrl) is not { } where || await SharedAsync(where, file.Url, ct).ConfigureAwait(false) is not { } entry)
                {
                    continue;
                }

                found.Add((file.Name, name => new LibraryFile(name, where.SiteUrl, where.Path, entry.Length, entry.Modified, file.Url), null, file.Url));
            }

            var number = 0;
            foreach (var src in message.Images)
            {
                if (!seen.Add(src) || await ImageSizeAsync(src, ct).ConfigureAwait(false) is not { } image)
                {
                    continue;
                }

                var stamp = message.Composed?.ToLocalTime().ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture) ?? "image";
                var when = message.Composed;
                found.Add(($"{stamp}_image-{++number}{image.Extension}", name => new ImageFile(name, src, image.Length, when), src, null));
            }
        }

        var names = TeamsFiles.Unique(found.Select(f => f.Name).ToList());
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < found.Count; i++)
        {
            if (found[i].Image is { } src)
            {
                images[src] = names[i];
            }
            else if (found[i].File is { } url)
            {
                files[url] = names[i];
            }
        }

        return (found.Select((f, i) => f.Make(names[i])).ToList(), new AttachmentNames(images, files));
    }

    /// <summary>A shared file as SharePoint or OneDrive has it now; null when it is gone or out of reach (it is left out, not failed on).</summary>
    private async Task<DriveEntry?> SharedAsync((string SiteUrl, string Path) where, string url, CancellationToken ct)
    {
        try
        {
            return await Cache.GetAsync($"shared:{where.SiteUrl}/{where.Path}", _settings.Refresh, async c => new[] { await Api.FileAsync(where.SiteUrl, where.Path, url, c).ConfigureAwait(false) }, ct).ConfigureAwait(false) is [var entry] ? entry : null;
        }
        catch (VfsException ex) when (ex.Error is VfsError.AccessDenied or VfsError.Unavailable or VfsError.IoError)
        {
            Logger.LogInformation("teams: a shared file could not be read and is left out ({Error})", ex.Message);
            return null;
        }
    }

    /// <summary>An inline image's length and extension, as first read (the image kept for an open soon after); null when it cannot be read.</summary>
    private async Task<(long Length, string Extension)?> ImageSizeAsync(string src, CancellationToken ct)
        => _imageSizes.TryGetValue(src, out var size) ? size
            : await ImageAsync(src, ct).ConfigureAwait(false) is { } image ? (image.Bytes.Length, image.Extension) : null;

    /// <summary>An inline image's bytes, for an open: as kept, else read again.</summary>
    internal async Task<byte[]> ImageBytesAsync(string src, CancellationToken ct)
        => (await ImageAsync(src, ct).ConfigureAwait(false))?.Bytes ?? throw new VfsException(VfsError.IoError, "an inline image could not be read from Teams' media service");

    /// <summary>An inline image, read once while kept; null when it cannot be read (an address on no Teams media host is never asked).</summary>
    private async Task<TeamsImage?> ImageAsync(string src, CancellationToken ct)
    {
        if (_images.TryGetValue(src, out var kept))
        {
            return kept;
        }

        var image = await Api.ImageAsync(src, ct).ConfigureAwait(false);
        if (image is null)
        {
            return null;
        }

        if (_imageSizes.Count >= 100_000)
        {
            _imageSizes.Clear();
        }

        _imageSizes[src] = (image.Bytes.Length, image.Extension);
        if (_images.TryAdd(src, image) && Interlocked.Add(ref _imageBytes, image.Bytes.Length) > ImagesKept)
        {
            _images.Clear();
            Interlocked.Exchange(ref _imageBytes, 0);
        }

        return image;
    }

    /// <summary>
    /// A SharePoint or OneDrive file (a shared one by its own address too), read in ranges through a download address fetched
    /// now; one that lapses mid-read (about an hour) is fetched again.
    /// </summary>
    internal async Task<IVfsFile> OpenLibraryFileAsync(string siteUrl, string path, string? url, long length, CancellationToken ct)
    {
        var entry = await Api.FileAsync(siteUrl, path, url, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
        var address = entry.DownloadUrl ?? throw VfsException.Unavailable($"SharePoint gave no download address for '{path}'");
        return new RandomAccessOverStream(length, async (offset, openCt) =>
        {
            HttpResponseMessage response;
            try
            {
                response = await Api.ContentAsync(address, offset, openCt).ConfigureAwait(false);
            }
            catch (VfsException ex) when (ex.Error == VfsError.AccessDenied)
            {
                var again = await Api.FileAsync(siteUrl, path, url, openCt).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
                address = again.DownloadUrl ?? throw VfsException.Unavailable($"SharePoint gave no download address for '{path}'");
                response = await Api.ContentAsync(address, offset, openCt).ConfigureAwait(false);
            }

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                response.Dispose();
                return Stream.Null;
            }

            return await ResponseStream.FromOffsetAsync(response, offset, path, null, openCt).ConfigureAwait(false);
        }, maxStreams: Math.Clamp(_settings.Concurrency, 1, 8), maxSkipBytes: 1 << 20);
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

        var others = chat.MemberMris.Where(m => !IsMe(m)).Select(NameOf).Where(n => n.Length > 0).Distinct().ToList();
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

    /// <summary>
    /// Whether an mri is the signed-in account's. Its own mri is built from a token's object id (<c>8:orgid:&lt;oid&gt;</c>);
    /// matching on the id holds whatever prefix a cloud's chats carry.
    /// </summary>
    private bool IsMe(string mri) => Api.Mri is { } me && ShortMri(mri).Equals(ShortMri(me), StringComparison.OrdinalIgnoreCase);

    private static string ShortMri(string mri)
    {
        var at = mri.IndexOf('@');
        var tail = at > 0 ? mri[..at] : mri;
        var colon = tail.LastIndexOf(':');
        return colon >= 0 && colon < tail.Length - 1 ? tail[(colon + 1)..] : tail;
    }

    // --- counting ------------------------------------------------------------------------------------------------------

    /// <summary>"Count files on start": the first walk of the whole tree, conversations and all (<see cref="KeptListings"/>).</summary>
    public Task CountFilesAsync(CancellationToken ct) => Kept.CountAsync(ct);

    /// <summary>The listings as kept, for tests.</summary>
    internal ListingCache Listings => Kept.Cache;

    /// <summary>
    /// A folder's stamp for the walks (<see cref="KeptListingsOptions.StampOf"/>): a chat's folder and a channel's posts have
    /// their conversation's version as the aggregator gives it now (it moves on with every message; a listing made while
    /// the conversation's history was still being read carries none, <see cref="ConversationWhole"/>), a thread's folder
    /// its messages' (<see cref="TeamsStamps.Of"/>); any other folder none, being cheap to list. The structure is the one
    /// call every stamp shares, kept a refresh interval. A folder that cannot be one of those by its path is not looked
    /// for: finding one lists every folder above it, which for a library's folders asks SharePoint again.
    /// </summary>
    internal async ValueTask<string?> StampAsync(string path, string? held, CancellationToken ct)
    {
        if (!MayBeStamped(path))
        {
            return null;
        }

        switch (await ResolveAsync(path, ct).ConfigureAwait(false))
        {
            case ChatFolder chat:
                return (await StructureAsync(ct).ConfigureAwait(false)).Chats.FirstOrDefault(c => c.Id == chat.Chat.Id)?.Version;
            case PostsFolder posts:
                return (await StructureAsync(ct).ConfigureAwait(false)).Teams.SelectMany(t => t.Channels).FirstOrDefault(c => c.Id == posts.ThreadId)?.Version;
            case ThreadFolder thread:
                return TeamsStamps.Of(thread.Thread);
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether a folder may be a chat's (<c>Chats/&lt;chat&gt;</c>), a channel's posts (<c>&lt;team&gt;/&lt;channel&gt;/posts</c>)
    /// or a thread's (one below those posts) by its path: the root's and a channel's own names are fixed (a team of the
    /// root's names is numbered). A library's folder of one of those names is found and then has none.
    /// </summary>
    private static bool MayBeStamped(string path)
    {
        var segments = VfsPath.Split(path);
        return segments.Length switch
        {
            2 => segments[0].Equals(ChatsFolder.FolderName, StringComparison.OrdinalIgnoreCase),
            3 or 4 => segments[2].Equals("posts", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
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
        Task[] behind;
        lock (_behind)
        {
            behind = [_seek, _myGroups.Building, _allGroups.Building, _sitesReading, _calls.Building, _activity.Building, .. HistoryReads];
        }

        try
        {
            await Task.WhenAll(behind).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

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
