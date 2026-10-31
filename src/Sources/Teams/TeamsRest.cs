using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Teams;

/// <summary>
/// Reads Microsoft's Teams services over HTTP (spec 7.9, spike 18): the middle tier discovers the regional service URLs
/// (the <c>authz</c> call), the aggregator gives the teams, channels and chats, and the chat service gives a
/// conversation's messages. Each call carries the bearer for that service's audience, which <paramref name="tokenFor"/>
/// supplies (the live web session, or a test's); a 401 hands it back the token refused and tries once more with the one it
/// gives then. Requests are paced per host and a 429's Retry-After is honoured. Files are read from SharePoint and OneDrive:
/// through their v2.0 drive API while the SharePoint token is in hand, else through Microsoft Graph with its token (drives,
/// a channel's filesFolder, the shares API, Microsoft Search for the group sites); each token only ever goes to its own
/// service's hosts. Inline images come from Teams' media service (with the authz call's skype token, only ever on the
/// media service's hosts).
/// </summary>
/// <param name="tokenFor">The bearer for a service; the second argument is a token the service just refused, else null.</param>
/// <param name="handler">The handler requests go through: a test's fake; null for the network.</param>
/// <param name="sharePointHost">The host the page used the SharePoint token on (the tenant's OneDrive or SharePoint), once that token is read.</param>
/// <param name="inHand">The token for a service the page mints only as it gets round to it (SharePoint's, Graph's) if one is in hand, else null: never a wait.</param>
/// <param name="seek">Asks the page for those tokens in the background; true when one came.</param>
/// <param name="graphHost">Microsoft Graph's address in the cloud, where a Graph token's audience does not name it.</param>
internal sealed class TeamsRest(Uri teamsApiHost, Func<TeamsService, string?, CancellationToken, Task<string>> tokenFor, int concurrency, ILogger logger, HttpMessageHandler? handler = null, Func<string?>? sharePointHost = null,
    Func<TeamsService, string?>? inHand = null, Func<CancellationToken, Task<bool>>? seek = null, string graphHost = "https://graph.microsoft.com") : ITeamsApi
{
    // the client version Teams' media service takes its "platform id" from (49: Teams for work), as purple-teams sends it
    private const string ClientVersion = "49/25113001312";
    private const int MaxImageBytes = 50 << 20;

    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
    {
        Timeout = TimeSpan.FromSeconds(100),
    };

    private readonly SemaphoreSlim _inFlight = new(Math.Clamp(concurrency, 1, 32), Math.Clamp(concurrency, 1, 32));
    private readonly RequestPace _pace = RequestPace.For("teams", burst: 40, perSecond: 8);
    private Services? _services;

    public string Account { get; set; } = "";

    public string? Mri { get; set; }

    public async Task<TeamsStructure> StructureAsync(CancellationToken ct)
    {
        var services = await DiscoverAsync(ct).ConfigureAwait(false);
        using var doc = await GetAsync($"{services.Aggregator}/api/v3/teams/users/me?isPrefetch=false&enableMembershipSummary=true", TeamsService.Aggregator, ct).ConfigureAwait(false);
        var teams = new List<TeamInfo>();
        foreach (var t in Arr(doc.RootElement, "teams"))
        {
            var channels = new List<ChannelInfo>();
            foreach (var c in Arr(t, "channels"))
            {
                channels.Add(new ChannelInfo(Str(c, "id") ?? "", Str(c, "displayName") ?? "", Bool(c, "isGeneral"), Int(c, "channelType") != 0)
                {
                    FilesPath = NullIfEmpty(Str(Obj(c, "defaultFileSettings"), "filesRelativePath")),
                    Version = NullIfEmpty(Str(c, "version")),
                    Description = NullIfEmpty(Str(c, "description")),
                    Created = Time(Str(c, "creationTime")),
                    CreatorMri = NullIfEmpty(Str(c, "creator")),
                    Shared = Bool(c, "isShared"),
                    Favourite = Bool(c, "isFavorite"),
                    Tabs = Arr(c, "tabs").Select(TabOf).OfType<TabInfo>().ToList(),
                });
            }

            var groupId = Str(Obj(t, "teamSiteInformation"), "groupId") ?? Str(t, "groupId");
            var summary = Obj(t, "membershipSummary");
            teams.Add(new TeamInfo(Str(t, "id") ?? "", Str(t, "displayName") ?? "(team)", groupId, channels)
            {
                Owner = string.Equals(Str(t, "memberRoleString"), "admin", StringComparison.OrdinalIgnoreCase) || Int(t, "memberRole") == 2,
                AccessType = Int(t, "accessType"),
                Members = Int(summary, "totalMemberCount") is > 0 and var count ? count : null,
                Owners = summary.ValueKind == JsonValueKind.Object && summary.TryGetProperty("adminRoleCount", out _) ? Int(summary, "adminRoleCount") : null,
                Bots = summary.ValueKind == JsonValueKind.Object && summary.TryGetProperty("botCount", out _) ? Int(summary, "botCount") : null,
                Description = NullIfEmpty(Str(t, "description")),
                Mail = NullIfEmpty(Str(t, "smtpAddress")),
                Classification = NullIfEmpty(Str(t, "classification")),
                Favourite = Bool(t, "isFavorite"),
                Archived = Bool(t, "isArchived"),
                Locked = Bool(t, "isTeamLocked"),
            });
        }

        var chats = new List<ChatInfo>();
        foreach (var c in Arr(doc.RootElement, "chats"))
        {
            var members = Arr(c, "members").Select(m => Str(m, "mri") ?? "").Where(m => m.Length > 0).ToList();
            chats.Add(new ChatInfo(Str(c, "id") ?? "", NullIfEmpty(Str(c, "title")), KindOf(c), members, Time(Str(c, "lastUpdatedTime") ?? Str(c, "version")))
            {
                Version = NullIfEmpty(Str(c, "version")),
                Created = Time(Str(c, "createdAt")),
                CreatorMri = NullIfEmpty(Str(c, "creator")),
                External = Bool(c, "isExternal"),
                Muted = Arr(c, "members").Any(m => Bool(m, "isMuted") && Mri is { } me && SameMri(Str(m, "mri"), me)),
                PinnedIds = PinnedIdsOf(c),
                Meeting = MeetingOf(c),
            });
        }

        return new TeamsStructure(teams, chats);
    }

    /// <summary>
    /// What a meeting's chat says of its meeting, as a GCC High tenant's chats carry it (the work probe, 30-Oct-2026): its
    /// <c>meetingInformation</c> (subject, location, start and end, cancelled, join address, organiser, recurrence, kind)
    /// and the account's answer (<c>userMeetingInfo.rsvpStatus</c>); each an object or a JSON string of one. Null for a chat
    /// with none.
    /// </summary>
    internal static MeetingInfo? MeetingOf(JsonElement chat)
    {
        if (JsonValues(chat, "meetingInformation").FirstOrDefault() is not { ValueKind: JsonValueKind.Object } info)
        {
            return null;
        }

        var join = NullIfEmpty(Str(info, "meetingJoinUrl"));
        return new MeetingInfo(NullIfEmpty(Str(info, "subject")), NullIfEmpty(Str(info, "location")), Time(Str(info, "startTime")), Time(Str(info, "endTime")),
            Bool(info, "isCancelled"), join is not null && Uri.TryCreate(join, UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeHttps ? join : null,
            NullIfEmpty(Str(info, "organizerId")), Recurrence(Obj(info, "eventRecurrencePattern"), Obj(info, "eventRecurrenceRange")),
            NullIfEmpty(Str(info, "meetingType")) ?? NullIfEmpty(Str(info, "appointmentType")),
            NullIfEmpty(Str(JsonValues(chat, "userMeetingInfo").FirstOrDefault(), "rsvpStatus")));
    }

    /// <summary>A meeting's recurrence in words, from its pattern (<c>patternType</c>; weekly's <c>interval</c> and <c>daysOfTheWeek</c>) and range (<c>startDate</c>, <c>endDate</c>); null where it has none.</summary>
    private static string? Recurrence(JsonElement pattern, JsonElement range)
    {
        var type = NullIfEmpty(Str(pattern, "patternType"));
        var weekly = Obj(pattern, "weekly");
        var interval = Str(weekly, "interval") is { } every && every != "1" && every != "0" ? $"every {every}" : null;
        var days = string.Join(", ", Arr(weekly, "daysOfTheWeek").Select(d => d.ValueKind == JsonValueKind.String ? d.GetString() : d.ToString()).OfType<string>().Where(d => d.Length > 0));
        var from = NullIfEmpty(Str(range, "startDate"));
        var to = NullIfEmpty(Str(range, "endDate"));
        var parts = new[] { type, interval, days.Length > 0 ? $"on {days}" : null, from is null ? null : $"from {from}", to is null ? null : $"to {to}" }.OfType<string>().ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>Whether two mris are one person's: by the object id they end with, whatever prefix a cloud's chats carry.</summary>
    private static bool SameMri(string? a, string b) => a is not null && Tail(a).Equals(Tail(b), StringComparison.OrdinalIgnoreCase);

    private static string Tail(string mri) => mri[(mri.LastIndexOf(':') + 1)..];

    /// <summary>
    /// The messages a chat has pinned, as its <c>pinnedItems</c> names them: an array (or a JSON string of one) of objects
    /// naming the message (<c>itemId</c>, as a GCC High tenant's are <c>{itemType, itemId}</c>; else <c>messageId</c>,
    /// <c>id</c>, <c>clientMessageId</c>) or of ids; none where it names none.
    /// </summary>
    internal static IReadOnlyList<string> PinnedIdsOf(JsonElement chat)
    {
        var items = Obj(chat, "pinnedItems");
        if (items.ValueKind == JsonValueKind.String && items.GetString() is { } held && held.TrimStart().StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(held);
                items = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return [];
            }
        }

        if (items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return items.EnumerateArray()
            .Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : Str(i, "itemId") ?? Str(i, "messageId") ?? Str(i, "messageid") ?? Str(i, "id") ?? Str(i, "clientMessageId") ?? Str(i, "clientmessageid"))
            .OfType<string>().Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// A channel's tab as the aggregator gives it (<c>name</c>, <c>tabType</c>, <c>settings.subtype</c>, the website's
    /// <c>settings.websiteUrl</c>): a website, a pinned file, an app; null for one with no name.
    /// </summary>
    internal static TabInfo? TabOf(JsonElement tab)
    {
        if (NullIfEmpty(Str(tab, "name")) is not { } name)
        {
            return null;
        }

        var settings = Obj(tab, "settings");
        var kind = $"{Str(tab, "tabType")} {Str(settings, "subtype")}";
        var website = kind.Contains("webpage", StringComparison.OrdinalIgnoreCase) || kind.Contains("website", StringComparison.OrdinalIgnoreCase);
        var address = NullIfEmpty(Str(settings, "websiteUrl")) ?? NullIfEmpty(Str(settings, "url"));
        return new TabInfo(name,
            website ? "Website"
                : kind.Contains("pin", StringComparison.OrdinalIgnoreCase) ? "Pinned file"
                : kind.Contains("extension", StringComparison.OrdinalIgnoreCase) ? "App"
                : "Tab",
            website && Uri.TryCreate(address, UriKind.Absolute, out var site) && site.Scheme is "https" or "http" ? address : null);
    }

    // --- the call history, the activity feed, a channel's thread ---------------------------------------------------------

    public async Task<IReadOnlyList<CallRecord>?> CallsAsync(int max, CancellationToken ct)
    {
        if (await SpecialAsync("48:calllogs", max, ct).ConfigureAwait(false) is not { } messages)
        {
            return null;
        }

        var calls = new List<CallRecord>();
        foreach (var message in messages)
        {
            foreach (var log in JsonValues(Obj(message, "properties"), "call-log"))
            {
                var participants = Arr(log, "participantList").Count() is > 0 and var listed ? listed : Arr(log, "participants").Count();
                calls.Add(new CallRecord(Time(Str(log, "startTime")), Time(Str(log, "connectTime")), Time(Str(log, "endTime")), NullIfEmpty(Str(log, "callDirection")),
                    NullIfEmpty(Str(log, "callType")), NullIfEmpty(Str(log, "callState")), NullIfEmpty(Str(Obj(log, "originatorParticipant"), "displayName")),
                    NullIfEmpty(Str(Obj(log, "targetParticipant"), "displayName")), participants));
            }
        }

        return calls.OrderByDescending(c => c.Start ?? DateTimeOffset.MinValue).ToList();
    }

    public async Task<IReadOnlyList<ActivityItem>?> ActivityAsync(int max, CancellationToken ct)
    {
        var feed = await SpecialAsync("48:notifications", max, ct).ConfigureAwait(false);
        var mentions = await SpecialAsync("48:mentions", max, ct).ConfigureAwait(false);
        if (feed is null && mentions is null)
        {
            return null;
        }

        // the feed holds the mentions too (activityType "mentions"): each mention once, by the message it is in
        var items = new List<ActivityItem>();
        var mentioned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (messages, fromMentions) in new[] { (mentions ?? [], true), (feed ?? [], false) })
        {
            foreach (var message in messages)
            {
                foreach (var activity in JsonValues(Obj(message, "properties"), "activity"))
                {
                    var type = Str(activity, "activityType") ?? "activity";
                    var subtype = NullIfEmpty(Str(activity, "activitySubtype"));
                    var when = Time(Str(activity, "activityTimestamp")) ?? Time(Str(message, "composetime"));
                    var thread = NullIfEmpty(Str(activity, "sourceThreadId"));
                    var mention = fromMentions || $"{type} {subtype}".Contains("mention", StringComparison.OrdinalIgnoreCase);
                    if (mention && !mentioned.Add($"{thread}|{NullIfEmpty(Str(activity, "sourceMessageId")) ?? when?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}"))
                    {
                        continue;
                    }

                    items.Add(new ActivityItem(when, type, subtype, NullIfEmpty(Str(activity, "sourceUserImDisplayName")) ?? NullIfEmpty(Str(message, "imdisplayname")),
                        NullIfEmpty(Str(activity, "sourceThreadTopic")), thread, NullIfEmpty(Str(activity, "messagePreview")), mention));
                }
            }
        }

        return items.OrderByDescending(i => i.When ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>
    /// A special conversation's messages (the call history, the activity feed), newest first, as raw JSON, up to
    /// <paramref name="max"/>; null where the account has none of it (404).
    /// </summary>
    private async Task<List<JsonElement>?> SpecialAsync(string id, int max, CancellationToken ct)
    {
        var services = await DiscoverAsync(ct).ConfigureAwait(false);
        var messages = new List<JsonElement>();
        string? next = $"{services.ChatService}/v1/users/ME/conversations/{Uri.EscapeDataString(id)}/messages?view=msnp24Equivalent|supportsMessageProperties&pageSize={Math.Clamp(max, 1, 200)}&startTime=1";
        try
        {
            while (next is not null && messages.Count < max)
            {
                using var doc = await GetAsync(next, TeamsService.ChatService, ct).ConfigureAwait(false);
                var page = Arr(doc.RootElement, "messages").Select(m => m.Clone()).ToList();
                messages.AddRange(page);
                next = page.Count == 0 ? null : NullIfEmpty(Str(Obj(doc.RootElement, "_metadata"), "backwardLink"));
            }
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            return null;
        }

        return messages.Take(max).ToList();
    }

    /// <summary>A property's JSON objects: an object, an array's objects, or either held as a string.</summary>
    private static IEnumerable<JsonElement> JsonValues(JsonElement properties, string name)
    {
        var value = Obj(properties, name);
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.TrimStart() is { Length: > 0 } held && (held[0] == '{' || held[0] == '['))
        {
            try
            {
                using var doc = JsonDocument.Parse(held);
                value = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return [];
            }
        }

        return value.ValueKind switch
        {
            JsonValueKind.Object => [value],
            JsonValueKind.Array => value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object).ToList(),
            _ => [],
        };
    }

    public async Task<ThreadDetails?> ThreadDetailsAsync(string threadId, CancellationToken ct)
    {
        var services = await DiscoverAsync(ct).ConfigureAwait(false);
        try
        {
            using var doc = await GetAsync($"{services.ChatService}/v1/threads/{Uri.EscapeDataString(threadId)}?view=msnp24Equivalent", TeamsService.ChatService, ct).ConfigureAwait(false);
            var properties = Obj(doc.RootElement, "properties");
            var roster = Obj(doc.RootElement, "rosterSummary");
            int? Count(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
            var site = NullIfEmpty(Str(properties, "sharepointSiteUrl"));
            return new ThreadDetails(site is not null && Uri.TryCreate(site, UriKind.Absolute, out var uri) && TeamsServices.IsSharePointHost(uri.Host) ? site.TrimEnd('/') : null,
                NullIfEmpty(Str(properties, "channelDocsFolderRelativeUrl")), Count(roster, "memberCount"), Count(Obj(roster, "roleCounts"), "Admin"),
                Count(roster, "externalMemberCount"), Count(roster, "botCount"));
        }
        catch (VfsException ex) when (ex.Error is VfsError.NotFound or VfsError.AccessDenied)
        {
            return null;
        }
    }

    public async Task<MessagePage> MessagesAsync(string threadId, int max, CancellationToken ct)
    {
        var services = await DiscoverAsync(ct).ConfigureAwait(false);
        var url = $"{services.ChatService}/v1/users/ME/conversations/{Uri.EscapeDataString(threadId)}/messages?view=msnp24Equivalent|supportsMessageProperties&pageSize={max}&startTime=1";
        return await PageAsync(url, ct).ConfigureAwait(false);
    }

    public Task<MessagePage> OlderAsync(string olderLink, CancellationToken ct) => PageAsync(olderLink, ct);

    public async Task<IReadOnlyList<Person>> PeopleAsync(IReadOnlyList<string> mris, CancellationToken ct)
    {
        if (mris.Count == 0)
        {
            return [];
        }

        var services = await DiscoverAsync(ct).ConfigureAwait(false);
        var body = new System.Text.Json.Nodes.JsonArray(mris.Select(m => (System.Text.Json.Nodes.JsonNode)m).ToArray()).ToJsonString();
        using var doc = await SendAsync(HttpMethod.Post, $"{services.MiddleTier}/beta/users/fetchShortProfile?isMailEnabled=false&enableGuest=true&includeIBBarredUsers=true", TeamsService.MiddleTier, body, ct).ConfigureAwait(false);
        var people = new List<Person>();
        var root = doc.RootElement;
        var list = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : Arr(root, "value");
        foreach (var p in list)
        {
            var mri = Str(p, "mri") ?? Str(p, "id");
            var name = Str(p, "displayName") ?? Str(p, "givenName");
            if (mri is not null && !string.IsNullOrWhiteSpace(name))
            {
                people.Add(new Person(mri, name!)
                {
                    Guest = string.Equals(Str(p, "userType"), "Guest", StringComparison.OrdinalIgnoreCase),
                    Bot = string.Equals(Str(p, "type"), "BOT", StringComparison.OrdinalIgnoreCase),
                    Organisation = NullIfEmpty(Str(p, "tenantName")),
                });
            }
        }

        return people;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _inFlight.Dispose();
        return ValueTask.CompletedTask;
    }

    // --- files: SharePoint and OneDrive, through SharePoint's own API or Microsoft Graph ------------------------------------

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<TeamSite?>> _sites = new(StringComparer.OrdinalIgnoreCase);

    // the files are read with SharePoint's token while it is in hand, else with Microsoft Graph's: GCC High's client mints
    // one with Files.ReadWrite.All and Sites.ReadWrite.All (Microsoft 365's none). Both address a file by its site and its
    // path in the site's default library, so what one listed the other opens.
    private bool ViaGraph => !SharePointReady && GraphReady;

    public async Task<(string SiteUrl, string Path)?> ChannelFilesAsync(TeamInfo team, ChannelInfo channel, ThreadDetails? thread, CancellationToken ct)
    {
        // as the channel's thread names them, whichever reads them: a private or shared channel's own site, which the
        // team's site and the aggregator's path can miss (and Graph's filesFolder misses for a channel shared from another team)
        if (ThreadFolder(thread) is { } named)
        {
            return named;
        }

        if (ViaGraph)
        {
            return team.GroupId is { } group ? await GraphChannelFilesAsync(group, channel, ct).ConfigureAwait(false) : null;
        }

        // the SharePoint token first: the host it was used on is the tenant's
        await tokenFor(TeamsService.SharePoint, null, ct).ConfigureAwait(false);
        var host = sharePointHost?.Invoke() is { } seen ? TeamsFiles.RootHost(seen) : null;
        if (channel.FilesPath is { } files && host is not null && TeamsFiles.Locate($"https://{host}{(files.StartsWith('/') ? "" : "/")}{files}", null) is { } given)
        {
            return given;
        }

        if (team.GroupId is null || host is null)
        {
            return null;
        }

        return await GroupSiteAsync(host, team.GroupId).ConfigureAwait(false) is { } site ? (site.Url, channel.DisplayName) : null;
    }

    /// <summary>The channel's folder as its thread names it: its site, and the folder's path within the site's default library; null where it names none.</summary>
    internal static (string SiteUrl, string Path)? ThreadFolder(ThreadDetails? thread)
        => thread?.SiteUrl is { } site && thread.FolderPath is { } folder && Uri.TryCreate(site, UriKind.Absolute, out var root)
            ? TeamsFiles.Locate($"{root.Scheme}://{root.Host}{(folder.StartsWith('/') ? "" : "/")}{folder}", site)
            : null;

    /// <summary>A group's site from SharePoint, kept once it has one (none yet, or a failure, is asked again).</summary>
    private async Task<TeamSite?> GroupSiteAsync(string host, string groupId)
    {
        var lookup = _sites.GetOrAdd(groupId, id => SiteAsync($"https://{host}", id, CancellationToken.None));
        try
        {
            var site = await lookup.ConfigureAwait(false);
            if (site is null)
            {
                _sites.TryRemove(new KeyValuePair<string, Task<TeamSite?>>(groupId, lookup));
            }

            return site;
        }
        catch (VfsException)
        {
            _sites.TryRemove(new KeyValuePair<string, Task<TeamSite?>>(groupId, lookup));
            throw;
        }
    }

    /// <summary>A group's site and its default library (GroupSiteManager.GetSiteStatus, status 2 when ready); null while it has none.</summary>
    private async Task<TeamSite?> SiteAsync(string root, string groupId, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"{root}/_api/GroupSiteManager/GetSiteStatus", TeamsService.SharePoint, new System.Text.Json.Nodes.JsonObject { ["groupId"] = groupId }.ToJsonString(), ct).ConfigureAwait(false);
        var site = Str(doc.RootElement, "SiteUrl");
        var documents = Str(doc.RootElement, "DocumentsUrl");
        if (Int(doc.RootElement, "SiteStatus") != 2 || string.IsNullOrEmpty(site) || string.IsNullOrEmpty(documents) || !Uri.TryCreate(documents, UriKind.Absolute, out var library))
        {
            logger.LogInformation("teams: the group {Group} has no SharePoint site yet (status {Status})", groupId, Int(doc.RootElement, "SiteStatus"));
            return null;
        }

        return new TeamSite(site.TrimEnd('/'), Uri.UnescapeDataString(library.AbsolutePath));
    }

    /// <summary>A channel's folder as Graph has it (filesFolder: a renamed channel's and a private channel's own site's too); null while the team has no site.</summary>
    private async Task<(string SiteUrl, string Path)?> GraphChannelFilesAsync(string groupId, ChannelInfo channel, CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync($"{GraphRoot()}/v1.0/teams/{Uri.EscapeDataString(groupId)}/channels/{Uri.EscapeDataString(channel.Id)}/filesFolder", TeamsService.Graph, ct).ConfigureAwait(false);
            if (Str(doc.RootElement, "webUrl") is not { } address || TeamsFiles.Locate(address, null) is not { } where)
            {
                return null;
            }

            if (Str(Obj(doc.RootElement, "parentReference"), "driveId") is { Length: > 0 } drive)
            {
                _drives.TryAdd(where.SiteUrl, Task.FromResult<string?>(drive));
            }

            return where;
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            // "Folder location for this channel is not ready yet": a team with no site
            return null;
        }
    }

    public async Task<IReadOnlyList<DriveEntry>> FolderAsync(string siteUrl, string path, CancellationToken ct)
    {
        var entries = new List<DriveEntry>();
        var service = ViaGraph ? TeamsService.Graph : TeamsService.SharePoint;
        string? next;
        if (service == TeamsService.Graph)
        {
            if (await DriveOfAsync(siteUrl, ct).ConfigureAwait(false) is not { } drive)
            {
                return entries;
            }

            next = $"{GraphRoot()}/v1.0/drives/{drive}/{(path.Length == 0 ? "root/children" : $"root:/{EscapePath(path)}:/children")}?$top=999&$select=name,size,folder,file,lastModifiedDateTime";
        }
        else
        {
            next = path.Length == 0 ? $"{SharePointSite(siteUrl)}/_api/v2.0/drive/root/children?$top=999" : $"{SharePointSite(siteUrl)}/_api/v2.0/drive/root:/{EscapePath(path)}:/children?$top=999";
        }

        try
        {
            while (next is not null)
            {
                using var doc = await GetAsync(next, service, ct).ConfigureAwait(false);
                entries.AddRange(Arr(doc.RootElement, "value").Select(DriveEntryOf).OfType<DriveEntry>());
                next = Str(doc.RootElement, "@odata.nextLink") is { } link && MayCarry(service, link) ? link : null;
            }
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            // a channel nobody has shared a file in has no folder yet
        }

        return entries;
    }

    public async Task<DriveEntry?> FileAsync(string siteUrl, string path, string? url, CancellationToken ct)
    {
        try
        {
            if (ViaGraph)
            {
                // a message's shared file by its own address: Graph's shares API reaches a file shared with the account
                // in a site (another's OneDrive) it cannot open
                var address = url is not null
                    ? $"{GraphRoot()}/v1.0/shares/{TeamsFiles.ShareId(url)}/driveItem"
                    : await DriveOfAsync(siteUrl, ct).ConfigureAwait(false) is { } drive ? $"{GraphRoot()}/v1.0/drives/{drive}/root:/{EscapePath(path)}" : null;
                if (address is null)
                {
                    return null;
                }

                using var item = await GetAsync(address, TeamsService.Graph, ct).ConfigureAwait(false);
                return DriveEntryOf(item.RootElement);
            }

            using var doc = await GetAsync($"{SharePointSite(siteUrl)}/_api/v2.0/drive/root:/{EscapePath(path)}", TeamsService.SharePoint, ct).ConfigureAwait(false);
            return DriveEntryOf(doc.RootElement);
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            return null;
        }
    }

    // a site's default library's Graph drive, by the site's address (null: it has none); kept, a failure not
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string?>> _drives = new(StringComparer.OrdinalIgnoreCase);

    private async Task<string?> DriveOfAsync(string siteUrl, CancellationToken ct)
    {
        var site = new Uri(SharePointSite(siteUrl));
        var lookup = _drives.GetOrAdd(siteUrl, _ => GraphDriveAsync(site, CancellationToken.None));
        try
        {
            return await lookup.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (VfsException) when (lookup.IsFaulted)
        {
            _drives.TryRemove(new KeyValuePair<string, Task<string?>>(siteUrl, lookup));
            throw;
        }
    }

    private async Task<string?> GraphDriveAsync(Uri site, CancellationToken ct)
    {
        var path = Uri.UnescapeDataString(site.AbsolutePath.Trim('/'));
        var address = path.Length == 0 ? $"{GraphRoot()}/v1.0/sites/{site.Host}/drive?$select=id" : $"{GraphRoot()}/v1.0/sites/{site.Host}:/{EscapePath(path)}:/drive?$select=id";
        try
        {
            using var doc = await GetAsync(address, TeamsService.Graph, ct).ConfigureAwait(false);
            return NullIfEmpty(Str(doc.RootElement, "id"));
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            return null;
        }
    }

    // a group's site as Graph found it from the group's drive (null: none), kept; a failure is not
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<string?>> _groupSites = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string?> SiteOfGroupAsync(string group, CancellationToken ct)
    {
        if (ViaGraph)
        {
            var lookup = _groupSites.GetOrAdd(group, id => GraphSiteOfGroupAsync(id, CancellationToken.None));
            try
            {
                return await lookup.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (VfsException) when (lookup.IsFaulted)
            {
                _groupSites.TryRemove(new KeyValuePair<string, Task<string?>>(group, lookup));
                throw;
            }
        }

        await tokenFor(TeamsService.SharePoint, null, ct).ConfigureAwait(false);
        return sharePointHost?.Invoke() is { } seen ? (await GroupSiteAsync(TeamsFiles.RootHost(seen), group).ConfigureAwait(false))?.Url : null;
    }

    /// <summary>A group's site from its drive (sharePointIds, else the library's address), its drive kept for the site; null for a group with none (404).</summary>
    private async Task<string?> GraphSiteOfGroupAsync(string group, CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync($"{GraphRoot()}/v1.0/groups/{Uri.EscapeDataString(group)}/drive?$select=id,webUrl,sharePointIds", TeamsService.Graph, ct).ConfigureAwait(false);
            if ((NullIfEmpty(Str(Obj(doc.RootElement, "sharePointIds"), "siteUrl")) ?? TeamsFiles.SiteOfLibrary(Str(doc.RootElement, "webUrl"))) is not { } site)
            {
                return null;
            }

            if (Str(doc.RootElement, "id") is { Length: > 0 } drive)
            {
                _drives.TryAdd(site.TrimEnd('/'), Task.FromResult<string?>(drive));
            }

            return site.TrimEnd('/');
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            return null;
        }
    }

    public async Task<HttpResponseMessage> ContentAsync(string downloadUrl, long offset, CancellationToken ct)
    {
        // pre-authenticated: no token goes with it
        await _pace.WaitAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw VfsException.Unavailable($"SharePoint ({new Uri(downloadUrl).Host}) could not be reached: {ex.Message}");
        }

        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return response;
        }

        using (response)
        {
            throw Error(response.StatusCode, downloadUrl, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
    }

    /// <summary>A site address on a SharePoint host, or a refusal: the SharePoint token is never sent elsewhere.</summary>
    private static string SharePointSite(string siteUrl)
        => Uri.TryCreate(siteUrl, UriKind.Absolute, out var site) && site.Scheme == Uri.UriSchemeHttps && TeamsServices.IsSharePointHost(site.Host)
            ? siteUrl.TrimEnd('/')
            : throw VfsException.AccessDenied(siteUrl);

    private static string EscapePath(string path) => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private static DriveEntry? DriveEntryOf(JsonElement item)
    {
        var name = Str(item, "name");
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var folder = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("folder", out _);
        var size = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;

        // SharePoint's v2.0 API names the download address one way, Graph another
        return new DriveEntry(name!, folder, folder ? 0 : size, Time(Str(item, "lastModifiedDateTime")), folder ? null : Str(item, "@content.downloadUrl") ?? Str(item, "@microsoft.graph.downloadUrl"));
    }

    public bool SharePointReady => inHand?.Invoke(TeamsService.SharePoint) is not null && sharePointHost?.Invoke() is not null;

    public bool GraphReady => inHand?.Invoke(TeamsService.Graph) is not null;

    public Task<bool> SeekTokensAsync(CancellationToken ct) => seek?.Invoke(ct) ?? Task.FromResult(false);

    // --- groups: SharePoint search, Graph -------------------------------------------------------------------------------

    public async Task<IReadOnlyList<GroupSite>> GroupSitesAsync(CancellationToken ct)
    {
        if (ViaGraph)
        {
            return await GraphGroupSitesAsync(ct).ConfigureAwait(false);
        }

        await tokenFor(TeamsService.SharePoint, null, ct).ConfigureAwait(false);
        var host = sharePointHost?.Invoke() is { } seen ? TeamsFiles.RootHost(seen) : throw VfsException.Unavailable("Teams on the web named no SharePoint host for the groups");
        var sites = new Dictionary<string, GroupSite>(StringComparer.OrdinalIgnoreCase);
        for (var start = 0; ; start += 500)
        {
            using var doc = await GetAsync($"https://{host}/_api/search/query?querytext='WebTemplate:GROUP'&selectproperties='Title,Path,GroupId,Description,Created'&rowlimit=500&startrow={start}&trimduplicates=false", TeamsService.SharePoint, ct).ConfigureAwait(false);
            var results = Obj(Obj(doc.RootElement, "PrimaryQueryResult"), "RelevantResults");
            var rows = Arr(Obj(results, "Table"), "Rows").ToList();
            foreach (var row in rows)
            {
                var cells = Arr(row, "Cells").Where(c => Str(c, "Key") is not null).GroupBy(c => Str(c, "Key")!).ToDictionary(g => g.Key, g => Str(g.First(), "Value"));
                if (cells.GetValueOrDefault("GroupId") is { Length: > 0 } group && cells.GetValueOrDefault("Path") is { Length: > 0 } path
                    && Uri.TryCreate(path, UriKind.Absolute, out var site) && TeamsServices.IsSharePointHost(site.Host))
                {
                    sites.TryAdd(group, new GroupSite(group, cells.GetValueOrDefault("Title") ?? group, path.TrimEnd('/'), NullIfEmpty(cells.GetValueOrDefault("Description")), Time(cells.GetValueOrDefault("Created"))));
                }
            }

            if (rows.Count == 0 || start + rows.Count >= Int(results, "TotalRows"))
            {
                return sites.Values.ToList();
            }
        }
    }

    /// <summary>
    /// The Microsoft 365 groups whose sites the account can open, from Graph's list of the organisation's groups
    /// (Group.Read.All, which GCC High's Graph token carries): the public ones and those it is in, as SharePoint's search
    /// would show them, each named without its site (found when the group is opened). Where Graph refuses the list,
    /// Microsoft Search.
    /// </summary>
    private async Task<IReadOnlyList<GroupSite>> GraphGroupSitesAsync(CancellationToken ct)
    {
        var root = GraphRoot();
        try
        {
            var member = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await foreach (var g in GraphPagesAsync($"{root}/v1.0/me/memberOf/microsoft.graph.group?$select=id&$top=999", ct).ConfigureAwait(false))
            {
                member.Add(Str(g, "id") ?? "");
            }

            var groups = new List<GroupSite>();
            await foreach (var g in GraphPagesAsync($"{root}/v1.0/groups?$filter=groupTypes/any(c:c%20eq%20'Unified')&$select=id,displayName,description,createdDateTime,visibility&$top=999", ct).ConfigureAwait(false))
            {
                if (Str(g, "id") is { Length: > 0 } id && (string.Equals(Str(g, "visibility"), "Public", StringComparison.OrdinalIgnoreCase) || member.Contains(id)))
                {
                    groups.Add(new GroupSite(id, Str(g, "displayName") ?? id, null, NullIfEmpty(Str(g, "description")), Time(Str(g, "createdDateTime"))));
                }
            }

            return groups;
        }
        catch (VfsException ex) when (ex.Error == VfsError.AccessDenied)
        {
            logger.LogInformation("teams: Microsoft Graph did not list the groups ({Error}); searching their sites instead, as far as Microsoft Search reaches", ex.Message);
            return await SearchGroupSitesAsync(root, ct).ConfigureAwait(false);
        }
    }

    // Microsoft Search's reach for SharePoint's kinds, and a page of it (Microsoft Learn: no more than 1,000 results)
    private const int SearchReach = 1000;
    private const int SearchPage = 500;

    /// <summary>
    /// The group sites the account can open, from Microsoft Search through Graph (Sites.Read.All): the index SharePoint's own
    /// search reads, trimmed alike. It names no site's group, and reaches no further than its first 1,000 results; paging
    /// stops there, or where a page brings nothing new (its paging is reported to run on past the end on large sets).
    /// </summary>
    private async Task<IReadOnlyList<GroupSite>> SearchGroupSitesAsync(string root, CancellationToken ct)
    {
        var sites = new Dictionary<string, GroupSite>(StringComparer.OrdinalIgnoreCase);
        for (var from = 0; from < SearchReach; from += SearchPage)
        {
            var body = $$"""{"requests":[{"entityTypes":["site"],"query":{"queryString":"WebTemplate:GROUP"},"from":{{from}},"size":{{SearchPage}}}]}""";
            using var doc = await SendAsync(HttpMethod.Post, $"{root}/v1.0/search/query", TeamsService.Graph, body, ct).ConfigureAwait(false);
            var found = Arr(Arr(doc.RootElement, "value").FirstOrDefault(), "hitsContainers").FirstOrDefault();
            var before = sites.Count;
            var hits = Arr(found, "hits").ToList();
            foreach (var site in hits.Select(h => Obj(h, "resource")))
            {
                if (Str(site, "webUrl") is { Length: > 0 } address && Uri.TryCreate(address, UriKind.Absolute, out var uri) && TeamsServices.IsSharePointHost(uri.Host))
                {
                    var url = address.TrimEnd('/');
                    sites.TryAdd(url, new GroupSite(null, NullIfEmpty(Str(site, "displayName")) ?? NullIfEmpty(Str(site, "name")) ?? url, url, NullIfEmpty(Str(site, "description")), Time(Str(site, "createdDateTime"))));
                }
            }

            if (sites.Count == before || !Bool(found, "moreResultsAvailable"))
            {
                break;
            }
        }

        return sites.Values.ToList();
    }

    public async Task<GroupsDirectory?> AllGroupsAsync(IReadOnlyList<TeamInfo> joined, IReadOnlyList<GroupSite>? sites, CancellationToken ct)
    {
        // the sites by their group, where the search named both
        var bySite = (sites ?? []).Where(s => s.GroupId is not null && s.SiteUrl is not null).GroupBy(s => s.GroupId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // SharePoint's search's sites (each with its group and site), the groups it shows, where Graph lists none
        var visible = SharePointReady && sites is not null && sites.All(s => s.GroupId is not null && s.SiteUrl is not null);
        if (GraphReady)
        {
            try
            {
                return await GraphGroupsAsync(bySite, joined, ct).ConfigureAwait(false);
            }
            catch (VfsException ex)
            {
                logger.LogInformation("teams: Microsoft Graph did not list every group ({Error}){Instead}", ex.Message, visible ? "; listing those SharePoint shows" : "");
            }
        }

        return visible ? await SharePointGroupsAsync(bySite, joined, ct).ConfigureAwait(false) : null;
    }

    public async Task<GroupsDirectory> MyGroupsAsync(IReadOnlyList<TeamInfo> joined, CancellationToken ct)
    {
        if (GraphReady)
        {
            try
            {
                var root = GraphRoot();
                var groups = new List<GroupInfo>();
                var named = 0;
                await foreach (var g in GraphPagesAsync($"{root}/v1.0/me/memberOf/microsoft.graph.group?$select={GroupFields}&$top=999", ct).ConfigureAwait(false))
                {
                    if (Str(g, "displayName") is not null)
                    {
                        named++;
                        groups.Add(GraphGroupOf(g, [], joined));
                    }
                    else if (Str(g, "id") is { Length: > 0 } id)
                    {
                        groups.Add(joined.FirstOrDefault(t => string.Equals(t.GroupId, id, StringComparison.OrdinalIgnoreCase)) is { } team ? GroupInfo.OfTeam(team) : new GroupInfo(id, id));
                    }
                }

                // a token that may not read the groups (without Group.Read.All, as the AnyFS app's) gets their ids alone
                if (named == 0)
                {
                    logger.LogInformation("teams: Microsoft Graph gave the account's groups without their details; listing its teams'");
                    return GroupsDirectory.OfTeams(joined);
                }

                var owner = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await foreach (var g in GraphPagesAsync($"{root}/v1.0/me/ownedObjects/microsoft.graph.group?$select=id&$top=999", ct).ConfigureAwait(false))
                {
                    owner.Add(Str(g, "id") ?? "");
                }

                return new GroupsDirectory(groups, groups.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase), owner, GroupsScope.Yours, "Microsoft Graph");
            }
            catch (VfsException ex)
            {
                logger.LogInformation("teams: Microsoft Graph did not list the account's groups ({Error}); listing its teams'", ex.Message);
            }
        }

        return GroupsDirectory.OfTeams(joined);
    }

    // the group fields Graph lists them with: all a group's own, none that costs a request per group
    private const string GroupFields = "id,displayName,description,mail,mailNickname,visibility,groupTypes,securityEnabled,mailEnabled,createdDateTime,renewedDateTime,expirationDateTime,classification,resourceProvisioningOptions,membershipRule,isAssignableToRole,onPremisesSyncEnabled";

    /// <summary>Every group of the organisation (Graph's /groups), with the account's memberships and ownerships, and each one's site where SharePoint showed it.</summary>
    private async Task<GroupsDirectory> GraphGroupsAsync(Dictionary<string, GroupSite> sites, IReadOnlyList<TeamInfo> joined, CancellationToken ct)
    {
        var root = GraphRoot();
        var groups = new List<GroupInfo>();
        await foreach (var g in GraphPagesAsync($"{root}/v1.0/groups?$select={GroupFields}&$top=999", ct).ConfigureAwait(false))
        {
            groups.Add(GraphGroupOf(g, sites, joined));
        }

        var member = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var g in GraphPagesAsync($"{root}/v1.0/me/memberOf/microsoft.graph.group?$select=id&$top=999", ct).ConfigureAwait(false))
        {
            member.Add(Str(g, "id") ?? "");
        }

        var owner = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var g in GraphPagesAsync($"{root}/v1.0/me/ownedObjects/microsoft.graph.group?$select=id&$top=999", ct).ConfigureAwait(false))
        {
            owner.Add(Str(g, "id") ?? "");
        }

        logger.LogInformation("teams: Microsoft Graph lists {Count:N0} groups; the account is in {Member:N0}", groups.Count, member.Count);
        return new GroupsDirectory(groups, member, owner, GroupsScope.Organisation, "Microsoft Graph");
    }

    /// <summary>A group as Graph lists it (<see cref="GroupFields"/>), with its site where SharePoint's search showed it and the aggregator's count for a team the account is in.</summary>
    private static GroupInfo GraphGroupOf(JsonElement g, Dictionary<string, GroupSite> sites, IReadOnlyList<TeamInfo> joined)
    {
        var id = Str(g, "id") ?? "";
        var types = Arr(g, "groupTypes").Select(t => t.GetString()).ToList();
        var unified = types.Contains("Unified", StringComparer.OrdinalIgnoreCase);
        var mailEnabled = Bool(g, "mailEnabled");
        var securityEnabled = Bool(g, "securityEnabled");
        return new GroupInfo(id, Str(g, "displayName") ?? id)
        {
            Kind = unified ? "Microsoft 365 group" : mailEnabled && securityEnabled ? "Mail-enabled security group" : securityEnabled ? "Security group" : mailEnabled ? "Distribution list" : "Group",
            Visibility = Str(g, "visibility") switch { "HiddenMembership" => "Hidden membership", { Length: > 0 } v => v, _ => null },
            Team = unified ? Arr(g, "resourceProvisioningOptions").Any(o => string.Equals(o.GetString(), "Team", StringComparison.OrdinalIgnoreCase)) : null,
            Dynamic = types.Contains("DynamicMembership", StringComparer.OrdinalIgnoreCase),
            Description = NullIfEmpty(Str(g, "description")),
            Mail = NullIfEmpty(Str(g, "mail")),
            Alias = NullIfEmpty(Str(g, "mailNickname")),
            Classification = NullIfEmpty(Str(g, "classification")),
            MembershipRule = NullIfEmpty(Str(g, "membershipRule")),
            SiteUrl = sites.GetValueOrDefault(id)?.SiteUrl,
            Created = Time(Str(g, "createdDateTime")),
            Renewed = Time(Str(g, "renewedDateTime")),
            Expires = Time(Str(g, "expirationDateTime")),
            OnPremisesSynced = OptionalBool(g, "onPremisesSyncEnabled"),
            RoleAssignable = OptionalBool(g, "isAssignableToRole"),
            Members = joined.FirstOrDefault(t => string.Equals(t.GroupId, id, StringComparison.OrdinalIgnoreCase))?.Members,
        };
    }

    /// <summary>Graph's address: the host the audience of the Graph token in hand names, else the cloud's.</summary>
    private string GraphRoot()
        => inHand?.Invoke(TeamsService.Graph) is { } token && TeamsWebSession.Claims(token).Aud is { } aud && Uri.TryCreate(aud.Trim(), UriKind.Absolute, out var uri) && TeamsServices.IsGraphHost(uri.Host)
            ? $"https://{uri.Host}"
            : graphHost.TrimEnd('/');

    /// <summary>A Graph collection's items, page by page (its nextLink, only ever on a Graph host).</summary>
    private async IAsyncEnumerable<JsonElement> GraphPagesAsync(string url, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? next = url;
        while (next is not null)
        {
            using var doc = await GetAsync(next, TeamsService.Graph, ct).ConfigureAwait(false);
            foreach (var item in Arr(doc.RootElement, "value"))
            {
                yield return item.Clone();
            }

            next = Str(doc.RootElement, "@odata.nextLink") is { } link && MayCarry(TeamsService.Graph, link) ? link : null;
        }
    }

    /// <summary>The group sites the account can open, each with SharePoint's details of its group (SP.Directory), and the aggregator's word on the teams it is in.</summary>
    private async Task<GroupsDirectory> SharePointGroupsAsync(Dictionary<string, GroupSite> sites, IReadOnlyList<TeamInfo> joined, CancellationToken ct)
    {
        var host = sharePointHost?.Invoke() is { } seen ? TeamsFiles.RootHost(seen) : null;
        var details = await Task.WhenAll(sites.Select(async pair =>
        {
            var (id, site) = pair;
            try
            {
                using var doc = await GetAsync($"https://{host}/_api/SP.Directory.DirectorySession/Group('{Uri.EscapeDataString(id)}')", TeamsService.SharePoint, ct).ConfigureAwait(false);
                var g = doc.RootElement;
                return (id, site, Public: OptionalBool(g, "isPublic"), Mail: NullIfEmpty(Str(g, "mail")), Alias: NullIfEmpty(Str(g, "alias")), Description: NullIfEmpty(Str(g, "description")));
            }
            catch (VfsException)
            {
                return (id, site, Public: (bool?)null, Mail: (string?)null, Alias: (string?)null, Description: site.Description);
            }
        })).ConfigureAwait(false);

        var groups = new List<GroupInfo>();
        foreach (var (id, site, isPublic, mail, alias, description) in details)
        {
            var team = joined.FirstOrDefault(t => string.Equals(t.GroupId, id, StringComparison.OrdinalIgnoreCase));
            groups.Add(new GroupInfo(id, site.Name)
            {
                Kind = "Microsoft 365 group",
                Visibility = team?.AccessType == 3 ? "Org-wide" : isPublic switch { true => "Public", false => "Private", _ => null },
                Team = team is not null ? true : null,
                Description = description ?? site.Description,
                Mail = mail,
                Alias = alias,
                SiteUrl = site.SiteUrl,
                Created = site.Created,
                Members = team?.Members,
            });
        }

        // a team with no site SharePoint shows (a sample team never given one) is still a group the account is in
        groups.AddRange(joined.Where(t => t.GroupId is not null && !sites.ContainsKey(t.GroupId!)).Select(GroupInfo.OfTeam));

        var member = joined.Select(t => t.GroupId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owner = joined.Where(t => t.Owner).Select(t => t.GroupId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new GroupsDirectory(groups, member, owner, GroupsScope.Visible, "SharePoint");
    }

    private static bool? OptionalBool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    // --- inline images: Teams' media service -----------------------------------------------------------------------------

    public async Task<TeamsImage?> ImageAsync(string src, CancellationToken ct)
    {
        var services = await DiscoverAsync(ct).ConfigureAwait(false);
        if (services.SkypeToken is not { } skype || !TeamsFiles.IsMediaService(src, services.Ams))
        {
            return null;
        }

        await _pace.WaitAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, src);
        request.Headers.TryAddWithoutValidation("Authorization", $"skype_token {skype}");
        request.Headers.TryAddWithoutValidation("x-ms-client-version", ClientVersion);
        request.Headers.Accept.ParseAdd("image/*");
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes)
            {
                logger.LogInformation("teams: an inline image could not be read ({Status}, {Length} bytes)", (int)response.StatusCode, response.Content.Headers.ContentLength);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return new TeamsImage(bytes, TeamsFiles.ExtensionOf(bytes, response.Content.Headers.ContentType?.MediaType));
        }
        catch (HttpRequestException ex)
        {
            logger.LogInformation("teams: an inline image could not be read ({Error})", ex.Message);
            return null;
        }
    }

    public async Task<HttpResponseMessage> MediaAsync(string address, long offset, CancellationToken ct)
    {
        var services = await DiscoverAsync(ct).ConfigureAwait(false);
        if (services.SkypeToken is not { } skype || !TeamsFiles.IsMediaService(address, services.Ams))
        {
            throw new VfsException(VfsError.AccessDenied, "a recording off Teams' media service, or no skype token to read it with");
        }

        await _pace.WaitAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.TryAddWithoutValidation("Authorization", $"skype_token {skype}");
        request.Headers.TryAddWithoutValidation("x-ms-client-version", ClientVersion);
        request.Headers.Range = new RangeHeaderValue(offset, null);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw VfsException.Unavailable($"Teams' media service could not be reached: {ex.Message}");
        }

        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return response;
        }

        var status = response.StatusCode;
        response.Dispose();
        throw status switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone => new VfsException(VfsError.NotFound, "Teams' media service has no such recording (any more)"),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new VfsException(VfsError.AccessDenied, $"Teams' media service refused the recording ({(int)status})"),
            _ => VfsException.Unavailable($"Teams' media service answered {(int)status}"),
        };
    }

    // --- the messages page ---------------------------------------------------------------------------------------------

    private async Task<MessagePage> PageAsync(string url, CancellationToken ct)
    {
        using var doc = await GetAsync(url, TeamsService.ChatService, ct).ConfigureAwait(false);
        var messages = new List<TeamsMessage>();
        foreach (var m in Arr(doc.RootElement, "messages"))
        {
            var type = Str(m, "messagetype") ?? Str(m, "messageType") ?? "";
            var root = Str(m, "rootMessageId") ?? Str(m, "rootmessageid");
            var parent = Str(m, "parentMessageId") ?? Str(m, "parentmessageid");
            var properties = Obj(m, "properties");
            messages.Add(new TeamsMessage(
                Str(m, "id") ?? "",
                NullIfEmpty(root),
                NullIfEmpty(parent),
                MriFrom(Str(m, "from")),
                Time(Str(m, "composetime") ?? Str(m, "originalarrivaltime")),
                type,
                NullIfEmpty(Str(m, "content")),
                NullIfEmpty(Str(m, "imdisplayname")))
            {
                Edited = Millis(Str(properties, "edittime")),
                Deleted = Millis(Str(properties, "deletetime")),
                Reactions = ReactionsOf(properties),
                Files = FilesOf(properties),
                Images = TeamsFiles.ImagesIn(Str(m, "content")),
                Pinned = PinnedOf(properties),
            });
        }

        var older = Str(Obj(doc.RootElement, "_metadata"), "backwardLink");
        return new MessagePage(messages, NullIfEmpty(older));
    }

    /// <summary>
    /// Whether a message is pinned (<c>properties.pinned</c>: at a GCC High tenant <c>{creatorId, pinnedTime}</c>, read
    /// leniently): true, a time, or what says who pinned it and when, as an object or a JSON string of one; not where it
    /// says false or its pin time is none.
    /// </summary>
    internal static bool PinnedOf(JsonElement properties)
    {
        var pinned = Obj(properties, "pinned");
        if (pinned.ValueKind == JsonValueKind.String && pinned.GetString()?.Trim() is { Length: > 0 } text)
        {
            if (!text.StartsWith('{'))
            {
                return !text.Equals("false", StringComparison.OrdinalIgnoreCase) && text != "0" && !text.Equals("null", StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                pinned = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return pinned.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => pinned.TryGetInt64(out var at) && at > 0,
            JsonValueKind.Object => pinned.EnumerateObject().Any() && !pinned.EnumerateObject().Any(p => p.Name.Contains("pinned", StringComparison.OrdinalIgnoreCase)
                && (p.Value.ValueKind is JsonValueKind.False or JsonValueKind.Null || p.Value.ToString() is "" or "0")),
            _ => false,
        };
    }

    // --- service discovery ---------------------------------------------------------------------------------------------

    /// <summary>The region's services and the skype token the authz call hands out, as discovered (again once that token runs low).</summary>
    internal Task<Services> ServicesAsync(CancellationToken ct) => DiscoverAsync(ct);

    private async Task<Services> DiscoverAsync(CancellationToken ct)
    {
        if (_services is { } ready && ready.SkypeExpires - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(10))
        {
            return ready;
        }

        using var doc = await SendAsync(HttpMethod.Post, $"{teamsApiHost.AbsoluteUri.TrimEnd('/')}/api/authsvc/v1.0/authz", TeamsService.MiddleTier, "{}", ct).ConfigureAwait(false);
        var gtms = Obj(doc.RootElement, "regionGtms");

        // each service's own host, else its front door on the Teams host (both clouds name both)
        var aggregator = (Str(gtms, "chatServiceAggregator") ?? Str(gtms, "chatSvcAggAfd") ?? throw VfsException.Unavailable("Teams did not say where its aggregator is")).TrimEnd('/');
        var chatService = (Str(gtms, "chatService") ?? Str(gtms, "chatServiceAfd") ?? throw VfsException.Unavailable("Teams did not say where its chat service is")).TrimEnd('/');
        var middleTier = (Str(gtms, "middleTier") ?? $"{teamsApiHost.AbsoluteUri.TrimEnd('/')}/api/mt/apac").TrimEnd('/');
        var ams = (Str(gtms, "amsV2") ?? Str(gtms, "ams"))?.TrimEnd('/');

        // the skype token Teams' media service (inline images) takes, with its life in seconds (purple-teams reads both forms)
        var tokens = Obj(doc.RootElement, "tokens");
        var skypeToken = Str(tokens, "skypeToken") ?? Str(Obj(doc.RootElement, "skypeToken"), "skypetoken");
        var life = Int(tokens, "expiresIn") is > 0 and var seconds ? seconds : Int(Obj(doc.RootElement, "skypeToken"), "expiresIn");
        var skypeExpires = skypeToken is null ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow.AddSeconds(life > 0 ? life : 3600);
        logger.LogInformation("teams: services for {Account}: aggregator {Aggregator}, chat service {ChatService}", Account, aggregator, chatService);
        return _services = new Services(aggregator, chatService, middleTier, ams, skypeToken, skypeExpires);
    }

    /// <summary>The region's services (authz regionGtms), the media service's host, and the skype token it takes.</summary>
    internal sealed record Services(string Aggregator, string ChatService, string MiddleTier, string? Ams, string? SkypeToken, DateTimeOffset SkypeExpires);

    // --- the send path -------------------------------------------------------------------------------------------------

    private Task<JsonDocument> GetAsync(string url, TeamsService service, CancellationToken ct) => SendAsync(HttpMethod.Get, url, service, null, ct);

    /// <summary>Whether a service's token may go to an address: SharePoint's to SharePoint and OneDrive alone, Graph's to Graph alone, over https.</summary>
    internal static bool MayCarry(TeamsService service, string url)
        => service switch
        {
            TeamsService.SharePoint => Uri.TryCreate(url, UriKind.Absolute, out var site) && site.Scheme == Uri.UriSchemeHttps && TeamsServices.IsSharePointHost(site.Host),
            TeamsService.Graph => Uri.TryCreate(url, UriKind.Absolute, out var graph) && graph.Scheme == Uri.UriSchemeHttps && TeamsServices.IsGraphHost(graph.Host),
            _ => true,
        };

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, TeamsService service, string? body, CancellationToken ct)
    {
        if (!MayCarry(service, url))
        {
            throw VfsException.AccessDenied(url);
        }

        await _inFlight.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? refused = null;
            var renewed = false;
            for (var attempt = 0; ; attempt++)
            {
                await _pace.WaitAsync(ct).ConfigureAwait(false);
                var token = await tokenFor(service, refused, ct).ConfigureAwait(false);
                refused = null;
                using var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.ParseAdd("application/json");
                if (body is not null)
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw VfsException.Unavailable($"Teams ({new Uri(url).Host}) could not be reached: {ex.Message}");
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw VfsException.Unavailable($"Teams ({new Uri(url).Host}) did not answer within {_http.Timeout.TotalSeconds:F0} s");
                }

                try
                {
                    if ((int)response.StatusCode is 429 or 503 && attempt < 5)
                    {
                        _pace.Hold(response);
                        var after = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow ?? TimeSpan.FromSeconds(Math.Min(30, 1 << attempt));
                        logger.LogInformation("teams: {Host} is throttling; holding {Seconds:F0}s", new Uri(url).Host, Math.Max(0, after.TotalSeconds));
                        await Task.Delay(after > TimeSpan.Zero ? after : TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                        continue;
                    }

                    // a token the service no longer takes before its expiry (revoked, or a claims challenge): once more, with
                    // a fresh one; a 403 is the account's permission, not the token, so it is not asked again
                    if (response.StatusCode == HttpStatusCode.Unauthorized && !renewed)
                    {
                        renewed = true;
                        refused = token;
                        logger.LogInformation("teams: {Host} refused the web session's token; asking for a fresh one", new Uri(url).Host);
                        continue;
                    }

                    var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw Error(response.StatusCode, url, text);
                    }

                    try
                    {
                        return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
                    }
                    catch (JsonException)
                    {
                        // a proxy's or a portal's page in place of the service's answer
                        throw new VfsException(VfsError.IoError, $"Teams ({new Uri(url).Host}) answered with something other than JSON: {Trim(text)}");
                    }
                }
                finally
                {
                    response.Dispose();
                }
            }
        }
        finally
        {
            _inFlight.Release();
        }
    }

    private static VfsException Error(HttpStatusCode code, string url, string body)
    {
        var host = new Uri(url).Host;
        var message = $"Teams ({host}) answered {(int)code}";
        return code switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => VfsException.AccessDenied($"{message}: the sign-in does not reach this ({Trim(body)})"),
            HttpStatusCode.NotFound => VfsException.NotFound(url),
            >= (HttpStatusCode)500 => VfsException.Unavailable(message),
            _ => new VfsException(VfsError.IoError, $"{message}: {Trim(body)}"),
        };
    }

    private static string Trim(string body) => body.Length <= 200 ? body : body[..200];

    // --- JSON helpers --------------------------------------------------------------------------------------------------

    private static JsonElement Obj(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    private static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>
    /// A message's shared files: <c>properties.files</c>, a JSON string holding an array (or the array itself) whose entries
    /// name the file (<c>fileName</c>, else <c>title</c>), its address (<c>objectUrl</c>, else <c>fileInfo.fileUrl</c>) and its
    /// site (<c>fileInfo.siteUrl</c>, else <c>baseUrl</c>).
    /// </summary>
    internal static IReadOnlyList<SharedFile> FilesOf(JsonElement properties)
    {
        if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty("files", out var files))
        {
            return [];
        }

        if (files.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var parsed = JsonDocument.Parse(files.GetString() ?? "[]");
                return Shared(parsed.RootElement);
            }
            catch (JsonException)
            {
                return [];
            }
        }

        return Shared(files);

        static IReadOnlyList<SharedFile> Shared(JsonElement array)
        {
            if (array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var shared = new List<SharedFile>();
            foreach (var file in array.EnumerateArray())
            {
                var info = Obj(file, "fileInfo");
                var name = Str(file, "fileName") ?? Str(file, "title");
                var url = Str(file, "objectUrl") ?? Str(info, "fileUrl");
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                {
                    shared.Add(new SharedFile(name!, url!, NullIfEmpty(Str(info, "siteUrl")) ?? NullIfEmpty(Str(file, "baseUrl"))));
                }
            }

            return shared;
        }
    }

    /// <summary>A time the chat service gives in milliseconds since 1970 (edittime, deletetime); null when absent or zero.</summary>
    internal static DateTimeOffset? Millis(string? value)
        => long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

    /// <summary>
    /// A message's reactions: <c>properties.emotions</c>, an array of <c>{key, users[{mri, time, value}]}</c> (as the seeded
    /// sandbox stores it; purple-teams reads the same), or that array as a JSON string; a key nobody holds any more is left out.
    /// </summary>
    internal static IReadOnlyList<Reaction> ReactionsOf(JsonElement properties)
    {
        if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty("emotions", out var emotions))
        {
            return [];
        }

        if (emotions.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var parsed = JsonDocument.Parse(emotions.GetString() ?? "[]");
                return Reactions(parsed.RootElement);
            }
            catch (JsonException)
            {
                return [];
            }
        }

        return Reactions(emotions);

        static IReadOnlyList<Reaction> Reactions(JsonElement array)
        {
            if (array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var reactions = new List<Reaction>();
            foreach (var emotion in array.EnumerateArray())
            {
                var mris = Arr(emotion, "users").Select(u => Str(u, "mri")).OfType<string>().Where(m => m.Length > 0).Distinct().ToList();
                if (Str(emotion, "key") is { Length: > 0 } key && mris.Count > 0)
                {
                    reactions.Add(new Reaction(key, mris));
                }
            }

            return reactions;
        }
    }

    private static ChatKind KindOf(JsonElement chat)
    {
        if (Bool(chat, "isOneOnOne"))
        {
            return ChatKind.OneOnOne;
        }

        var markers = $"{Str(chat, "chatType")} {Str(chat, "threadType")} {Str(chat, "chatSubType")}";
        return markers.Contains("meeting", StringComparison.OrdinalIgnoreCase) ? ChatKind.Meeting : ChatKind.Group;
    }

    /// <summary>The mri a message's <c>from</c> URL ends with (<c>…/contacts/8:orgid:&lt;id&gt;</c>); the value itself when it is already an mri.</summary>
    private static string? MriFrom(string? from)
    {
        if (string.IsNullOrEmpty(from))
        {
            return null;
        }

        var slash = from.LastIndexOf('/');
        var tail = slash >= 0 ? from[(slash + 1)..] : from;
        return tail.StartsWith("8:", StringComparison.Ordinal) || tail.StartsWith("19:", StringComparison.Ordinal) ? tail : from;
    }

    /// <summary>A time from an ISO string or epoch milliseconds; null for neither.</summary>
    private static DateTimeOffset? Time(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (long.TryParse(value, out var ms) && ms > 1_000_000_000_000)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when) ? when : null;
    }
}
