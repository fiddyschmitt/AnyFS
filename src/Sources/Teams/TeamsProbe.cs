using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnyFS.Sources.Teams;

/// <summary>
/// BRANCH BUILD ONLY (teams-probe, 30-Oct-2026; goes with the branch). A read-only look at what the account's Teams services
/// offer for the features under consideration: meeting recordings and transcripts, call history, saved messages, the
/// activity feed, the cards of bot and connector posts, chat folders, tabs, team rosters, and what Graph's token reaches.
/// It is written as a report that holds no tokens, message text, names, addresses, tenant names, ids or file names: field
/// names, counts, types, status codes, and the shapes of addresses with the tenant and the names taken out. Bounded (some
/// hundred requests, paced as the source's own); a section that fails says so and the rest go on.
/// </summary>
internal sealed partial class TeamsProbe(TeamsRest rest, TeamsCloud cloud, string? mri)
{
    private const int MeetingChats = 20;
    private const int Channels = 30;
    private const int OtherChats = 20;
    private const int PageSize = 50;

    private readonly StringBuilder _text = new();

    // what the structure section found, for the later sections (never written out)
    private readonly List<(string Team, string? Group, List<string> Channels)> _teams = [];
    private readonly List<(string Id, bool Meeting)> _chats = [];
    private TeamsRest.Services? _services;

    // the items the recordings name (their video and transcript, in Teams' media service and in OneDrive), for the checks
    private readonly List<(string Type, string? Uri, string? DriveId, string? ItemId)> _items = [];

    public async Task<string> RunAsync(CancellationToken ct)
    {
        Line($"AnyFS Teams probe (branch teams-probe), {DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC, cloud {cloud.Display}");
        Line("It holds no tokens, message text, names, addresses, tenant names, ids or file names: field names, counts, types,");
        Line("status codes, and the shapes of addresses with the tenant and names taken out. Read it before sending it on.");
        await SectionAsync("tokens", TokensAsync, ct).ConfigureAwait(false);
        await SectionAsync("services", ServicesAsync, ct).ConfigureAwait(false);
        await SectionAsync("teams, channels and chats (the aggregator)", StructureAsync, ct).ConfigureAwait(false);
        await SectionAsync("conversations (the chat service)", ConversationsAsync, ct).ConfigureAwait(false);
        await SectionAsync("special conversations", SpecialAsync, ct).ConfigureAwait(false);
        await SectionAsync($"meeting chats (the latest {MeetingChats}): recordings, transcripts, calls", MeetingsAsync, ct).ConfigureAwait(false);
        await SectionAsync("recordings and transcripts in Teams' media service (the skype token)", MediaAsync, ct).ConfigureAwait(false);
        await SectionAsync($"channel posts (up to {Channels} channels) and chats (up to {OtherChats}): properties and cards", PostsAsync, ct).ConfigureAwait(false);
        await SectionAsync("channel threads (one a team, up to 10): properties and rosters", ThreadsAsync, ct).ConfigureAwait(false);
        await SectionAsync("Microsoft Graph", GraphAsync, ct).ConfigureAwait(false);
        await SectionAsync("OneDrive (SharePoint's token)", OneDriveAsync, ct).ConfigureAwait(false);
        await SectionAsync("saved messages and call history: guesses", GuessesAsync, ct).ConfigureAwait(false);
        Line("");
        Line("End of report.");
        return _text.ToString();
    }

    /// <summary>
    /// The later part: the sections that need SharePoint's or Graph's token, once one is in hand. They are waited for up to
    /// <paramref name="wait"/> (the source asks the page for them behind its listings; after a minute the probe asks once
    /// itself); then the tokens, Graph, OneDrive and the recordings' and transcripts' OneDrive copies are probed. Only this
    /// part is returned, to go after the report.
    /// </summary>
    public async Task<string> RunLaterAsync(TimeSpan wait, CancellationToken ct)
    {
        _text.Clear();
        Line("");
        Line($"[later: SharePoint's and Graph's tokens, waited for up to {wait.TotalMinutes:F0} minutes]");
        var start = DateTime.UtcNow;
        var asked = false;
        while (true)
        {
            var (sharePoint, graph) = (await InHandAsync(TeamsService.SharePoint, ct).ConfigureAwait(false), await InHandAsync(TeamsService.Graph, ct).ConfigureAwait(false));
            if (sharePoint || graph)
            {
                Line($"  in hand after {(DateTime.UtcNow - start).TotalMinutes:F1} min: SharePoint's {(sharePoint ? "yes" : "no")}, Graph's {(graph ? "yes" : "no")}");
                break;
            }

            if (!asked && DateTime.UtcNow - start > TimeSpan.FromMinutes(1))
            {
                asked = true;
                Line($"  asked the page for them itself (a headless load): {(await rest.ProbeSeekAsync(ct).ConfigureAwait(false) ? "one came" : "none came")}");
                continue;
            }

            if (DateTime.UtcNow - start > wait)
            {
                Line($"  neither came within {wait.TotalMinutes:F0} minutes");
                Line("");
                Line("End of the later part.");
                return _text.ToString();
            }

            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }

        await SectionAsync("tokens, now", TokensAsync, ct).ConfigureAwait(false);
        await SectionAsync("Microsoft Graph, now", GraphAsync, ct).ConfigureAwait(false);
        await SectionAsync("OneDrive (SharePoint's token), now", OneDriveAsync, ct).ConfigureAwait(false);
        await SectionAsync("recordings and transcripts in OneDrive", RecordingFilesAsync, ct).ConfigureAwait(false);
        Line("");
        Line("End of the later part.");
        return _text.ToString();
    }

    private async Task<bool> InHandAsync(TeamsService service, CancellationToken ct)
    {
        try
        {
            await rest.ProbeTokenAsync(service, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is AnyFS.Sdk.VfsException or SignInRequiredException)
        {
            return false;
        }
    }

    private void Line(string text) => _text.Append(text).Append('\n');

    private async Task SectionAsync(string title, Func<CancellationToken, Task> section, CancellationToken ct)
    {
        Line("");
        Line($"[{title}]");
        try
        {
            await section(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // its type alone: a message can carry an address
            Line($"  the section failed: {ex.GetType().Name}");
        }
    }

    private async Task<(int Status, JsonDocument? Json)> GetAsync(string url, TeamsService service, CancellationToken ct, string? body = null)
    {
        var (status, text) = await rest.ProbeAsync(body is null ? HttpMethod.Get : HttpMethod.Post, url, service, body, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300 || text.Length == 0)
        {
            return (status, null);
        }

        try
        {
            return (status, JsonDocument.Parse(text));
        }
        catch (JsonException)
        {
            return (status, null);
        }
    }

    // --- the sections --------------------------------------------------------------------------------------------------

    private async Task TokensAsync(CancellationToken ct)
    {
        foreach (var service in Enum.GetValues<TeamsService>())
        {
            string token;
            try
            {
                token = await rest.ProbeTokenAsync(service, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AnyFS.Sdk.VfsException or SignInRequiredException)
            {
                Line($"  {service}: not in hand");
                continue;
            }

            var (audience, scopes) = Scopes(token);
            Line($"  {service}: in hand; audience {AudienceShape(audience)}; scopes {scopes}");
        }
    }

    private async Task ServicesAsync(CancellationToken ct)
    {
        _services = await rest.ServicesAsync(ct).ConfigureAwait(false);
        Line($"  aggregator {UrlShape(_services.Aggregator)}; chat service {UrlShape(_services.ChatService)}; middle tier {UrlShape(_services.MiddleTier)}; media {UrlShape(_services.Ams)}");
        Line($"  skype token: {(_services.SkypeToken is null ? "none" : "given")}");
    }

    private async Task StructureAsync(CancellationToken ct)
    {
        var (status, json) = await GetAsync($"{_services!.Aggregator}/api/v3/teams/users/me?isPrefetch=false&enableMembershipSummary=true", TeamsService.Aggregator, ct).ConfigureAwait(false);
        Line($"  status {status}");
        using (json)
        {
            if (json is null)
            {
                return;
            }

            var root = json.RootElement;
            Line($"  root: {Keys(root)}");
            var teams = Arr(root, "teams").ToList();
            var channels = teams.SelectMany(t => Arr(t, "channels")).ToList();
            var chats = Arr(root, "chats").ToList();
            Line($"  teams: {teams.Count}");
            Describe("team", teams, ["teamType", "accessType", "memberRoleString", "guestUsersCategory", "threadType"]);
            Line($"    with a classification: {teams.Count(t => Str(t, "classification") is { Length: > 0 })}; with a description: {teams.Count(t => Str(t, "description") is { Length: > 0 })}");
            Line($"    teamSettings: {Shape(teams.Select(t => Obj(t, "teamSettings")).FirstOrDefault(s => s.ValueKind == JsonValueKind.Object), 2)}");
            Line($"    teamGuestSettings: {Shape(teams.Select(t => Obj(t, "teamGuestSettings")).FirstOrDefault(s => s.ValueKind == JsonValueKind.Object), 2)}");
            Line($"    membershipSummary: {Shape(teams.Select(t => Obj(t, "membershipSummary")).FirstOrDefault(s => s.ValueKind == JsonValueKind.Object), 2)}");
            Line($"  channels: {channels.Count}");
            Describe("channel", channels, ["channelType", "threadType", "threadSubType", "memberRoleString"]);
            Line($"    with a description: {channels.Count(c => Str(c, "description") is { Length: > 0 })}");
            var tabs = channels.SelectMany(c => Arr(c, "tabs")).ToList();
            Line($"    tabs: {tabs.Count} in {channels.Count(c => Arr(c, "tabs").Any())} channels; a tab: {Union(tabs, 2)}");
            Line($"    tab kinds: {Values(tabs, "tabType", "type", "directive", "entityType")}; settings' subtypes: {Values(tabs.Select(t => Obj(t, "settings")), "subtype")}");
            Line($"    defaultFileSettings: {Shape(channels.Select(c => Obj(c, "defaultFileSettings")).FirstOrDefault(s => s.ValueKind == JsonValueKind.Object), 2)}");
            Line($"    lastMessage: {Shape(channels.Select(c => Obj(c, "lastMessage")).FirstOrDefault(s => s.ValueKind == JsonValueKind.Object), 2)}");
            Line($"  chats: {chats.Count}");
            Describe("chat", chats, ["chatType", "threadType", "chatSubType", "interopType", "relationshipState", "productContext"]);
            Line($"    by chatType/threadType/chatSubType: {string.Join(", ", chats.GroupBy(c => $"{Str(c, "chatType")}/{Str(c, "threadType")}/{Str(c, "chatSubType")}").Select(g => $"{Safe(g.Key)} x{g.Count()}"))}");
            Line($"    a member: {Union(chats.SelectMany(c => Arr(c, "members")).Take(200), 1)}; member roles: {Values(chats.SelectMany(c => Arr(c, "members")), "role")}");
            Line($"    fileReferences: {Union(chats.Select(c => Obj(c, "fileReferences")).Where(f => f.ValueKind != JsonValueKind.Undefined).Take(50), 3)}");
            Line($"    meetingPolicy: {Union(chats.Select(c => Obj(c, "meetingPolicy")).Where(f => f.ValueKind != JsonValueKind.Undefined).Take(50), 2)}");

            // what chat.md reads a chat's pinned messages from (main's 2970f3c reads it leniently): its shape, held as JSON or not
            var pinned = chats.SelectMany(c => JsonIn(c, "pinnedItems")).ToList();
            Line($"    pinnedItems: {chats.Count(c => Obj(c, "pinnedItems").ValueKind != JsonValueKind.Undefined)} chats have it, kinds {Counted(chats.Select(c => Obj(c, "pinnedItems").ValueKind.ToString()))}; parsed: {Union(pinned.Take(50), 3)}");
            Line($"  conversationFolders: {Arr(root, "conversationFolders").Count()}; a folder: {Union(Arr(root, "conversationFolders"), 3)}");
            Line($"  privateFeeds: {string.Join(", ", Arr(root, "privateFeeds").Select(f => Safe(Str(f, "id"))))}; a feed: {Union(Arr(root, "privateFeeds"), 1)}");
            var users = Obj(root, "users");
            Line($"  users: {(users.ValueKind == JsonValueKind.Array ? users.GetArrayLength() : users.ValueKind == JsonValueKind.Object ? users.EnumerateObject().Count() : 0)}; a user: {Union(users.ValueKind == JsonValueKind.Array ? users.EnumerateArray().Take(50) : [], 1)}");

            foreach (var team in teams)
            {
                _teams.Add((Str(team, "id") ?? "", Str(Obj(team, "teamSiteInformation"), "groupId") ?? Str(team, "groupId"), Arr(team, "channels").OrderByDescending(c => Bool(c, "isGeneral")).Select(c => Str(c, "id") ?? "").ToList()));
            }

            foreach (var chat in chats)
            {
                var kind = $"{Str(chat, "chatType")} {Str(chat, "threadType")} {Str(chat, "chatSubType")}";
                _chats.Add((Str(chat, "id") ?? "", kind.Contains("meeting", StringComparison.OrdinalIgnoreCase)));
            }

            Line($"  meeting chats: {_chats.Count(c => c.Meeting)}; with a transcript (hasTranscript): {chats.Count(c => Bool(c, "hasTranscript"))}");
        }
    }

    private async Task ConversationsAsync(CancellationToken ct)
    {
        var (status, json) = await GetAsync($"{_services!.ChatService}/v1/users/ME/conversations?view=msnp24Equivalent&pageSize=200", TeamsService.ChatService, ct).ConfigureAwait(false);
        Line($"  status {status}");
        using (json)
        {
            var list = json is null ? [] : Arr(json.RootElement, "conversations").ToList();
            var ids = list.Select(c => Str(c, "id") ?? "").ToList();
            Line($"  conversations (first page): {ids.Count}; by kind: {string.Join(", ", ids.GroupBy(i => i.Split(':')[0]).Select(g => $"{Safe(g.Key)}: x{g.Count()}"))}");
            Line($"  special: {string.Join(", ", ids.Where(i => i.StartsWith("48:", StringComparison.Ordinal)).Select(Safe))}");
            Line($"  a conversation: {Union(list, 1)}");
            Line($"  its properties: {Union(list.Select(c => Obj(c, "properties")), 1)}");
            Line($"  its threadProperties: {Union(list.Select(c => Obj(c, "threadProperties")), 1)}");
            Line($"  its memberProperties: {Union(list.Select(c => Obj(c, "memberProperties")), 1)}");
            foreach (var id in ids.Where(i => i.StartsWith("48:", StringComparison.Ordinal)))
            {
                _special.Add(id);
            }
        }
    }

    // 48:notes: Teams' chat with yourself, likely kept as a conversation of its own (a "notes to self" folder if so)
    private readonly HashSet<string> _special = new(StringComparer.Ordinal) { "48:notifications", "48:threads", "48:calllogs", "48:mentions", "48:saved", "48:notes" };

    private async Task SpecialAsync(CancellationToken ct)
    {
        foreach (var id in _special.Where(i => i.Length is > 3 and <= 33 && i[3..].All(char.IsAsciiLetter)))
        {
            var (status, json) = await GetAsync(MessagesUrl(id), TeamsService.ChatService, ct).ConfigureAwait(false);
            using (json)
            {
                var messages = json is null ? [] : Arr(json.RootElement, "messages").ToList();
                Line($"  {id}: status {status}; messages {messages.Count}; types {Counted(messages.Select(m => Str(m, "messagetype")))}");
                if (messages.Count > 0)
                {
                    Line($"    a message: {Union(messages, 1)}");
                    Line($"    properties: {Union(messages.Select(m => Obj(m, "properties")), 1)}");
                    Line($"    properties in JSON: {PropertyShapes(messages)}");
                    Line($"    content: {string.Join(" | ", messages.Select(m => XmlShape(Str(m, "content"))).Where(s => s.Length > 0).Distinct().Take(5))}");
                }
            }
        }
    }

    private async Task MeetingsAsync(CancellationToken ct)
    {
        var meetings = _chats.Where(c => c.Meeting).Take(MeetingChats).ToList();
        var all = new List<JsonElement>();
        var docs = new List<JsonDocument>();
        try
        {
            foreach (var (id, _) in meetings)
            {
                var (status, json) = await GetAsync(MessagesUrl(id), TeamsService.ChatService, ct).ConfigureAwait(false);
                if (json is null)
                {
                    Line($"  a meeting chat: status {status}");
                    continue;
                }

                docs.Add(json);
                all.AddRange(Arr(json.RootElement, "messages"));
            }

            Line($"  meeting chats read: {meetings.Count}; messages: {all.Count}; types {Counted(all.Select(m => Str(m, "messagetype")))}");
            foreach (var type in all.Select(m => Str(m, "messagetype") ?? "").Distinct().Where(t => t is not ("RichText/Html" or "Text")))
            {
                var these = all.Where(m => Str(m, "messagetype") == type).ToList();
                Line($"  {Safe(type)} x{these.Count}:");
                Line($"    properties: {Union(these.Select(m => Obj(m, "properties")), 1)}");
                Line($"    properties in JSON: {PropertyShapes(these)}");
                Line($"    content: {string.Join(" | ", these.Select(m => XmlShape(Str(m, "content"))).Where(s => s.Length > 0).Distinct().Take(6))}");
                Line($"    content kinds: {Counted(these.Select(m => ContentKind(Str(m, "content"))))}; as JSON: {string.Join(" | ", these.Select(m => JsonShapeOf(Str(m, "content"))).Where(s => s.Length > 0).Distinct().Take(4))}");
                Line($"    addresses: {string.Join(" | ", these.SelectMany(m => Addresses(Str(m, "content"))).Select(UrlShape).Distinct().Take(12))}");
            }

            foreach (var recording in all.Where(m => Str(m, "messagetype") == "RichText/Media_CallRecording"))
            {
                _items.AddRange(Items(Str(recording, "content")));
            }

            Line($"  items the recordings name: {Counted(_items.Select(i => i.Type))}; with a drive and item id: {_items.Count(i => i.DriveId is not null && i.ItemId is not null)}");

            var files = all.SelectMany(m => FileEntries(Obj(m, "properties"))).ToList();
            Line($"  shared files in meeting chats: {files.Count}; a file: {Union(files, 2)}; kinds: {Counted(files.Select(f => Extension(Str(f, "fileName") ?? Str(f, "title"))))}");
            Line($"  their addresses: {string.Join(" | ", files.Select(f => UrlShape(Str(f, "objectUrl") ?? Str(Obj(f, "fileInfo"), "fileUrl"))).Distinct().Take(8))}");
        }
        finally
        {
            docs.ForEach(d => d.Dispose());
        }
    }

    /// <summary>The recordings' and transcripts' copies in Teams' media service, read (their first bytes) with the skype token, which is always in hand.</summary>
    private async Task MediaAsync(CancellationToken ct)
    {
        if (_items.Count == 0)
        {
            Line("  no recording in the meeting chats read");
            return;
        }

        foreach (var type in _items.Select(i => i.Type).Distinct().Where(t => t.StartsWith("ams", StringComparison.OrdinalIgnoreCase) || t is "video" or "rosterevents"))
        {
            foreach (var item in _items.Where(i => i.Type == type && i.Uri is not null).Take(3))
            {
                Line($"  {Safe(type)} at {UrlShape(item.Uri)}: {HeadText(await rest.ProbeHeadAsync(item.Uri!, TeamsRest.ProbeAuth.Skype, TeamsService.ChatService, ct).ConfigureAwait(false))}");
            }
        }
    }

    /// <summary>The later part's check of the recordings' and transcripts' OneDrive copies: by Graph's drive item, by SharePoint's, and by the address the message gives.</summary>
    private async Task RecordingFilesAsync(CancellationToken ct)
    {
        var files = _items.Where(i => i.Type.StartsWith("onedrive", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0)
        {
            Line("  no OneDrive copy named by the recordings read");
            return;
        }

        foreach (var type in files.Select(f => f.Type).Distinct())
        {
            foreach (var item in files.Where(f => f.Type == type).Take(3))
            {
                Line($"  {Safe(type)}:");
                if (item.DriveId is { } drive && item.ItemId is { } id)
                {
                    await DriveItemAsync("through Graph's drive item", $"{rest.ProbeGraphRoot}/v1.0/drives/{Uri.EscapeDataString(drive)}/items/{Uri.EscapeDataString(id)}", TeamsService.Graph, ct).ConfigureAwait(false);
                    if (rest.ProbeSharePointHost is not null && item.Uri is { } given && Uri.TryCreate(given, UriKind.Absolute, out var site) && TeamsServices.IsSharePointHost(site.Host))
                    {
                        await DriveItemAsync("through SharePoint's drive item", $"https://{site.Host}/_api/v2.0/drives/{Uri.EscapeDataString(drive)}/items/{Uri.EscapeDataString(id)}", TeamsService.SharePoint, ct).ConfigureAwait(false);
                    }
                }

                if (item.Uri is { } uri)
                {
                    Line($"    at the address given, {UrlShape(uri)}, with SharePoint's token: {HeadText(await rest.ProbeHeadAsync(uri, TeamsRest.ProbeAuth.Bearer, TeamsService.SharePoint, ct).ConfigureAwait(false))}");
                }
            }
        }
    }

    /// <summary>A drive item's metadata (its keys, size and whether it gives a download address), and that address's first bytes.</summary>
    private async Task DriveItemAsync(string how, string url, TeamsService service, CancellationToken ct)
    {
        var (status, json) = await GetAsync(url, service, ct).ConfigureAwait(false);
        string? download;
        using (json)
        {
            var root = json?.RootElement ?? default;
            download = Str(root, "@microsoft.graph.downloadUrl") ?? Str(root, "@content.downloadUrl");
            Line($"    {how}: {StatusText(status)}{(json is null ? "" : $"; {Union([root], 1)}; size {Str(root, "size") ?? "?"}; a download address: {(download is null ? "no" : "yes")}; kind {Extension(Str(root, "name"))}")}");
        }

        if (download is not null)
        {
            Line($"      its download address: {HeadText(await rest.ProbeHeadAsync(download, TeamsRest.ProbeAuth.None, TeamsService.SharePoint, ct).ConfigureAwait(false))}");
        }
    }

    /// <summary>A ranged request's answer as the report shows it: status, type, lengths, ranges, and what its first bytes are (never the bytes).</summary>
    private static string HeadText((int Status, string? Type, long? Length, long? Total, bool Ranges, byte[] Head) head)
        => head.Status <= 0
            ? StatusText(head.Status)
            : $"status {head.Status}; type {Safe(head.Type ?? "(none)")}; length {head.Length?.ToString(CultureInfo.InvariantCulture) ?? "?"}; whole {head.Total?.ToString(CultureInfo.InvariantCulture) ?? "?"}; ranges {(head.Ranges ? "yes" : "no")}; starts as {Kind(head.Head)}";

    /// <summary>What a file's first bytes say it is (WebVTT, JSON, MP4, markup, zip, PDF), never the bytes themselves.</summary>
    internal static string Kind(byte[] head)
    {
        if (head.Length == 0)
        {
            return "(nothing)";
        }

        var text = Encoding.ASCII.GetString(head).TrimStart('﻿', ' ', '\r', '\n', '\t', '\0');
        return text.StartsWith("WEBVTT", StringComparison.Ordinal) ? "WebVTT"
            : head.Length >= 8 && Encoding.ASCII.GetString(head, 4, 4) == "ftyp" ? "MP4"
            : text.StartsWith('{') || text.StartsWith('[') ? "JSON"
            : text.StartsWith('<') ? "markup"
            : text.StartsWith("PK", StringComparison.Ordinal) ? "zip"
            : text.StartsWith("%PDF", StringComparison.Ordinal) ? "PDF"
            : head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? "text (UTF-8)"
            : "other";
    }

    /// <summary>A message's content kind: none, markup, JSON or plain text.</summary>
    private static string ContentKind(string? content)
        => string.IsNullOrWhiteSpace(content) ? "empty" : content.TrimStart()[0] switch { '<' => "markup", '{' or '[' => "JSON", _ => "text" };

    /// <summary>A message's content as JSON, its shape (keys and kinds alone); empty where it is no JSON.</summary>
    private static string JsonShapeOf(string? content)
    {
        if (ContentKind(content) != "JSON")
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(content!);
            return Shape(doc.RootElement, 4);
        }
        catch (JsonException)
        {
            return "(not JSON after all)";
        }
    }

    /// <summary>The items a recording names (<c>&lt;item type uri driveId driveItemId&gt;</c>): their kind, address and drive ids.</summary>
    internal static IEnumerable<(string Type, string? Uri, string? DriveId, string? ItemId)> Items(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            yield break;
        }

        foreach (Match tag in Tag().Matches(content))
        {
            if (!tag.Groups["e"].Value.Equals("item", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var attributes = Attribute().Matches(tag.Groups["a"].Value).GroupBy(a => a.Groups["n"].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => System.Net.WebUtility.HtmlDecode(g.First().Groups["v"].Value), StringComparer.OrdinalIgnoreCase);
            if (attributes.GetValueOrDefault("type") is { Length: > 0 } type)
            {
                yield return (type, attributes.GetValueOrDefault("uri"), attributes.GetValueOrDefault("driveId"), attributes.GetValueOrDefault("driveItemId"));
            }
        }
    }

    private async Task PostsAsync(CancellationToken ct)
    {
        var conversations = _teams.SelectMany(t => t.Channels).Take(Channels).Concat(_chats.Where(c => !c.Meeting).Select(c => c.Id).Take(OtherChats)).ToList();
        var all = new List<JsonElement>();
        var docs = new List<JsonDocument>();
        try
        {
            foreach (var id in conversations)
            {
                var (_, json) = await GetAsync(MessagesUrl(id), TeamsService.ChatService, ct).ConfigureAwait(false);
                if (json is not null)
                {
                    docs.Add(json);
                    all.AddRange(Arr(json.RootElement, "messages"));
                }
            }

            Line($"  conversations read: {conversations.Count}; messages: {all.Count}; types {Counted(all.Select(m => Str(m, "messagetype")))}");
            Line($"  a message: {Union(all, 1)}");
            Line($"  properties: {Union(all.Select(m => Obj(m, "properties")), 1)}");
            Line($"  importance: {Counted(all.Select(m => Str(Obj(m, "properties"), "importance")).Where(v => v is not null))}; with a subject: {all.Count(m => Str(Obj(m, "properties"), "subject") is { Length: > 0 })}; with mentions: {all.Count(m => Str(Obj(m, "properties"), "mentions") is { Length: > 2 })}");
            Line($"  properties in JSON: {PropertyShapes(all)}");
            var cards = all.SelectMany(m => JsonIn(Obj(m, "properties"), "cards")).ToList();
            Line($"  messages with cards: {all.Count(m => JsonIn(Obj(m, "properties"), "cards").Any())}; cards: {cards.Count}");
            Line($"    content types: {Counted(cards.Select(c => Str(c, "contentType")))}");
            Line($"    element types: {Counted(cards.SelectMany(TypesIn))}");
            foreach (var shape in cards.Select(c => Shape(c, 4)).Distinct().Take(4))
            {
                Line($"    a card: {shape}");
            }

            var odd = all.Where(m => Str(m, "content") is { } c && (c.Contains("<attachment", StringComparison.OrdinalIgnoreCase) || c.Contains("<URIObject", StringComparison.OrdinalIgnoreCase) || c.Contains("<at ", StringComparison.OrdinalIgnoreCase))).ToList();
            Line($"  content with attachments, objects or mentions: {odd.Count}; {string.Join(" | ", odd.Select(m => XmlShape(Str(m, "content"))).Distinct().Take(6))}");
            Line($"  links in posts and chats: {all.Sum(m => Addresses(Str(m, "content")).Count())}; their hosts: {Counted(all.SelectMany(m => Addresses(Str(m, "content"))).Select(a => Uri.TryCreate(a, UriKind.Absolute, out var u) ? HostShape(u.Host) : "<not an address>"))}");
        }
        finally
        {
            docs.ForEach(d => d.Dispose());
        }
    }

    private async Task ThreadsAsync(CancellationToken ct)
    {
        var properties = new List<JsonElement>();
        var members = new List<JsonElement>();
        var docs = new List<JsonDocument>();
        try
        {
            foreach (var (_, _, channels) in _teams.Take(10))
            {
                if (channels.FirstOrDefault() is not { Length: > 0 } general)
                {
                    continue;
                }

                var (status, json) = await GetAsync($"{_services!.ChatService}/v1/threads/{Uri.EscapeDataString(general)}?view=msnp24Equivalent", TeamsService.ChatService, ct).ConfigureAwait(false);
                if (json is null)
                {
                    Line($"  a thread: status {status}");
                    continue;
                }

                docs.Add(json);
                properties.Add(Obj(json.RootElement, "properties"));
                members.AddRange(Arr(json.RootElement, "members"));
                Line($"  a thread: {Keys(json.RootElement)}; members {Arr(json.RootElement, "members").Count()}; rosterSummary {Shape(Obj(json.RootElement, "rosterSummary"), 2)}");
            }

            Line($"  properties: {Union(properties, 1)}");
            foreach (var key in new[] { "sharepointSiteUrl", "sharepointRootLibrary", "channelDocsFolderRelativeUrl", "sharepointChannelDocsFolder", "notebookId", "description", "teamSmtpAddress", "visibility" })
            {
                Line($"    with {key}: {properties.Count(p => Str(p, key) is { Length: > 0 })} of {properties.Count}");
            }

            Line($"  a member: {Union(members, 1)}; roles {Values(members, "role")}; meeting roles {Values(members, "meetingRole")}");
        }
        finally
        {
            docs.ForEach(d => d.Dispose());
        }
    }

    private async Task GraphAsync(CancellationToken ct)
    {
        var root = rest.ProbeGraphRoot;
        var me = mri is { } m && m.LastIndexOf(':') is var colon and >= 0 ? m[(colon + 1)..] : null;
        var team = _teams.FirstOrDefault(t => t.Group is not null);
        var probes = new List<(string Label, string? Url)>
        {
            ("/me/joinedTeams", $"{root}/v1.0/me/joinedTeams?$select=id"),
            ("/teams/{team}/members", team.Group is null ? null : $"{root}/v1.0/teams/{team.Group}/members?$top=20"),
            ("/teams/{team}/channels/{general}/tabs", team.Group is null || team.Channels.Count == 0 ? null : $"{root}/v1.0/teams/{team.Group}/channels/{Uri.EscapeDataString(team.Channels[0])}/tabs"),
            ("/me/chats", $"{root}/v1.0/me/chats?$top=5"),
            ("/me/onlineMeetings/getAllTranscripts", me is null ? null : $"{root}/v1.0/users/{me}/onlineMeetings/getAllTranscripts(meetingOrganizerUserId='{me}')"),
            ("/me/onlineMeetings/getAllRecordings", me is null ? null : $"{root}/v1.0/users/{me}/onlineMeetings/getAllRecordings(meetingOrganizerUserId='{me}')"),
            ("/me/drive Recordings folder", $"{root}/v1.0/me/drive/root:/Recordings:/children?$top=200&$select=name,file,size"),
            ("/me/drive Microsoft Teams Chat Files", $"{root}/v1.0/me/drive/root:/Microsoft%20Teams%20Chat%20Files:/children?$top=5&$select=name"),
            ("/me/presence", $"{root}/v1.0/me/presence"),
            ("/me/planner/tasks", $"{root}/v1.0/me/planner/tasks?$top=1"),
        };

        foreach (var (label, url) in probes)
        {
            if (url is null)
            {
                Line($"  {label}: not asked (nothing to ask it of)");
                continue;
            }

            var (status, json) = await GetAsync(url, TeamsService.Graph, ct).ConfigureAwait(false);
            using (json)
            {
                var items = json is null ? [] : Arr(json.RootElement, "value").ToList();
                var kinds = label.Contains("drive", StringComparison.Ordinal) ? $"; kinds {Counted(items.Select(i => Extension(Str(i, "name"))))}" : "";
                Line($"  {label}: {StatusText(status)}{(json is null ? "" : $"; items {items.Count}; an item {Union(items, 2)}")}{kinds}");
            }
        }
    }

    private async Task OneDriveAsync(CancellationToken ct)
    {
        if (rest.ProbeSharePointHost is not { } seen)
        {
            Line("  SharePoint's token not in hand (no host seen)");
            return;
        }

        var dot = seen.IndexOf('.', StringComparison.Ordinal);
        var label = TeamsFiles.RootHost(seen)[..TeamsFiles.RootHost(seen).IndexOf('.', StringComparison.Ordinal)];
        var oneDrive = $"{label}-my{seen[dot..]}";
        foreach (var folder in new[] { "Recordings", "Microsoft Teams Chat Files" })
        {
            var (status, json) = await GetAsync($"https://{oneDrive}/_api/v2.0/me/drive/root:/{Uri.EscapeDataString(folder)}:/children?$top=200", TeamsService.SharePoint, ct).ConfigureAwait(false);
            using (json)
            {
                var items = json is null ? [] : Arr(json.RootElement, "value").ToList();
                Line($"  {folder}: {StatusText(status)}{(json is null ? "" : $"; items {items.Count}; kinds {Counted(items.Select(i => Extension(Str(i, "name"))))}")}");
            }
        }
    }

    private async Task GuessesAsync(CancellationToken ct)
    {
        foreach (var (label, url) in new[]
        {
            ("aggregator /api/v1/users/me/savedmessages", $"{_services!.Aggregator}/api/v1/users/me/savedmessages"),
            ("aggregator /api/v2/users/me/savedmessages", $"{_services.Aggregator}/api/v2/users/me/savedmessages"),
            ("aggregator /api/v1/teams/users/me/pinnedChannels", $"{_services.Aggregator}/api/v1/teams/users/me/pinnedChannels"),
        })
        {
            var (status, json) = await GetAsync(url, TeamsService.Aggregator, ct).ConfigureAwait(false);
            using (json)
            {
                Line($"  {label}: {StatusText(status)}{(json is null ? "" : $"; {Shape(json.RootElement, 2)}")}");
            }
        }
    }

    private string MessagesUrl(string conversation) => $"{_services!.ChatService}/v1/users/ME/conversations/{Uri.EscapeDataString(conversation)}/messages?view=msnp24Equivalent|supportsMessageProperties&pageSize={PageSize}&startTime=1";

    private static string StatusText(int status) => status switch
    {
        0 => "not asked (a host the token may not go to)",
        -1 => "unreachable",
        -2 => "no answer in time",
        -3 => "no token in hand",
        _ => $"status {status}",
    };

    // --- shapes, with nothing identifying -------------------------------------------------------------------------------

    private void Describe(string what, List<JsonElement> items, string[] enums)
    {
        Line($"    a {what}: {Union(items, 1)}");
        var flags = items.SelectMany(i => i.ValueKind == JsonValueKind.Object ? i.EnumerateObject() : Enumerable.Empty<JsonProperty>())
            .Where(p => p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            .GroupBy(p => Key(p.Name))
            .Select(g => $"{g.Key} {g.Count(p => p.Value.ValueKind == JsonValueKind.True)}");
        Line($"    flags (how many true of {items.Count}): {string.Join(", ", flags)}");
        Line($"    kinds: {string.Join("; ", enums.Select(e => $"{e} {Values(items, e)}"))}");
    }

    /// <summary>The keys of the objects, all of them once, each with the shape of its value to <paramref name="depth"/> levels.</summary>
    private static string Union(IEnumerable<JsonElement> items, int depth)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items.Where(i => i.ValueKind == JsonValueKind.Object))
        {
            foreach (var p in item.EnumerateObject())
            {
                var key = Key(p.Name);
                if (!keys.ContainsKey(key) || keys[key].Length == 0)
                {
                    keys[key] = depth > 1 ? Inner(p.Value, depth - 1) : "";
                }
            }
        }

        return keys.Count == 0 ? "(none)" : "{" + string.Join(", ", keys.Select(k => k.Key + k.Value)) + "}";
    }

    private static string Keys(JsonElement e) => e.ValueKind == JsonValueKind.Object ? string.Join(", ", e.EnumerateObject().Select(p => Key(p.Name)).Distinct()) : e.ValueKind.ToString();

    /// <summary>A value's shape: an object's keys (each with its value's shape), an array's first item's, a string's value only for a key that names a kind.</summary>
    internal static string Shape(JsonElement e, int depth) => e.ValueKind switch
    {
        JsonValueKind.Object => depth <= 0 ? "{…}" : "{" + string.Join(", ", e.EnumerateObject().Select(p => Key(p.Name) + (Kinds.Contains(p.Name) && p.Value.ValueKind == JsonValueKind.String ? "=" + Safe(p.Value.GetString()) : Inner(p.Value, depth - 1)))) + "}",
        JsonValueKind.Array => depth <= 0 || e.GetArrayLength() == 0 ? "[]" : "[" + Shape(e[0], depth) + (e.GetArrayLength() > 1 ? $" x{e.GetArrayLength()}" : "") + "]",
        JsonValueKind.Undefined => "(none)",
        _ => e.ValueKind.ToString().ToLowerInvariant(),
    };

    private static string Inner(JsonElement e, int depth) => e.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? ":" + Shape(e, depth) : "";

    // keys whose string values name a kind of thing, not a person, a place or a text
    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "contentType", "messagetype", "messageType", "chatType", "threadType", "chatSubType", "threadSubType", "channelType", "teamType",
        "interopType", "importance", "role", "meetingRole", "memberRoleString", "streamType", "activityType", "activitySubtype", "sourceType",
        "callType", "callState", "itemType", "kind", "state", "version", "$schema", "style", "weight", "size", "horizontalAlignment", "spacing",
    };

    /// <summary>The kinds a field takes across the items, each counted.</summary>
    private static string Values(IEnumerable<JsonElement> items, params string[] keys)
        => Counted(items.SelectMany(i => keys.Select(k => i.ValueKind == JsonValueKind.Object && i.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.ToString() : null : null)).Where(v => v is not null));

    private static string Counted(IEnumerable<string?> values)
    {
        var counts = values.Select(v => Safe(v ?? "(none)")).GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} x{g.Count()}").ToList();
        return counts.Count == 0 ? "(none)" : string.Join(", ", counts.Take(30)) + (counts.Count > 30 ? $" (+{counts.Count - 30} more)" : "");
    }

    /// <summary>The shapes of the message properties that hold JSON (as strings or as values): their keys, never their values.</summary>
    private static string PropertyShapes(IEnumerable<JsonElement> messages)
    {
        var shapes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var properties in messages.Select(m => Obj(m, "properties")).Where(p => p.ValueKind == JsonValueKind.Object))
        {
            foreach (var p in properties.EnumerateObject())
            {
                var key = Key(p.Name);
                if (shapes.ContainsKey(key))
                {
                    continue;
                }

                foreach (var value in JsonIn(properties, p.Name).Take(1))
                {
                    shapes[key] = Shape(value, 3);
                }
            }
        }

        return shapes.Count == 0 ? "(none)" : string.Join("; ", shapes.Select(s => $"{s.Key}={s.Value}"));
    }

    /// <summary>A property's JSON: an array's or an object's items, also where it is held as a string.</summary>
    private static IEnumerable<JsonElement> JsonIn(JsonElement properties, string name)
    {
        if (properties.ValueKind != JsonValueKind.Object || !properties.TryGetProperty(name, out var value))
        {
            return [];
        }

        var held = value.ValueKind == JsonValueKind.String ? value.GetString()?.TrimStart() : null;
        if (held is not null && (held.StartsWith('[') || held.StartsWith('{')))
        {
            var text = held;
            try
            {
                using var doc = JsonDocument.Parse(text);
                value = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return [];
            }
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().ToList(),
            JsonValueKind.Object => [value],
            _ => [],
        };
    }

    private static IEnumerable<JsonElement> FileEntries(JsonElement properties) => JsonIn(properties, "files");

    /// <summary>The kinds named in a card: every "type" in it.</summary>
    private static IEnumerable<string?> TypesIn(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.NameEquals("type") && p.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return p.Value.GetString();
                    }

                    foreach (var inner in TypesIn(p.Value))
                    {
                        yield return inner;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                {
                    foreach (var inner in TypesIn(item))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// The structure of a message's HTML or XML: each element once, with its attributes' names; an attribute that names a
    /// kind (type, itemtype) with its value. Never a text, an address or an id.
    /// </summary>
    internal static string XmlShape(string? content)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains('<', StringComparison.Ordinal))
        {
            return "";
        }

        var seen = new List<string>();
        foreach (Match tag in Tag().Matches(content))
        {
            var attributes = Attribute().Matches(tag.Groups["a"].Value)
                .Select(a => a.Groups["n"].Value is var n && n is "type" or "itemtype" && Safe(a.Groups["v"].Value) is var v && !v.StartsWith('<') ? $"{n}={v}" : a.Groups["n"].Value)
                .Distinct();
            var shape = $"<{tag.Groups["e"].Value}{string.Concat(attributes.Select(a => " " + a))}>";
            if (!seen.Contains(shape))
            {
                seen.Add(shape);
            }
        }

        return string.Concat(seen.Take(40));
    }

    [GeneratedRegex(@"<(?<e>[A-Za-z][\w:.-]*)(?<a>[^>]*)>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"(?<n>[A-Za-z_][\w:.-]*)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)')", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    /// <summary>The addresses in a message's markup: its attributes that hold one (href, src, uri, url...).</summary>
    private static IEnumerable<string> Addresses(string? content)
        => string.IsNullOrEmpty(content) ? [] : Attribute().Matches(content).Select(a => System.Net.WebUtility.HtmlDecode(a.Groups["v"].Value)).Where(v => v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || v.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// An address's shape: its scheme, its host with any tenant taken out (<see cref="HostShape"/>), each path segment taken out
    /// but for the words Microsoft's services name things by, a last segment's file kind kept, and the query's keys alone.
    /// </summary>
    internal static string UrlShape(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return address is null ? "(none)" : "<not an address>";
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        var path = string.Join('/', segments.Select((s, i) => Words.Contains(s) ? s : i == segments.Count - 1 && Extension(s) is { } kind && kind != "(none)" ? "<name>" + kind : "<x>"));
        var query = uri.Query.Length > 1 ? "?" + string.Join('&', uri.Query[1..].Split('&').Select(p => Safe(Uri.UnescapeDataString(p.Split('=')[0])))) : "";
        return $"{uri.Scheme}://{HostShape(uri.Host)}/{path}{query}";
    }

    // the path words of Microsoft's services, which name a kind of place and nothing of the organisation's
    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        "personal", "sites", "teams", "Documents", "Shared Documents", "Recordings", "Microsoft Teams Chat Files", "Microsoft Teams Data",
        "_layouts", "15", "stream.aspx", "download.aspx", "Doc.aspx", "WopiFrame.aspx", "guestaccess.aspx", "v1", "v2", "v2.0", "v1.0",
        "objects", "views", "imgo", "imgpsh_fullsize_anim", "original", "content", "thumbnail", "video", "audio", "api", "drives", "items",
        "root", "me", "drive", "users", "ME", "conversations", "messages", "threads", "l", "meetup-join", "message", "file", "chat", "channel",
        "apps", "entity", "transcripts", "recordings", "onlineMeetings", "Forms", "AllItems.aspx",
        "mt", "csa", "chatsvc", "apac", "emea", "amer", "gcch", "dod", "beta", "authsvc",
    };

    /// <summary>
    /// A host with nothing of the organisation in it: a tenant's SharePoint or OneDrive host as <c>&lt;tenant&gt;.sharepoint.us</c>
    /// (<c>&lt;tenant&gt;-my</c> for OneDrive); any other host as its last two labels, each label before them kept only when it
    /// is a word Microsoft's services name their hosts by, else <c>&lt;x&gt;</c>.
    /// </summary>
    internal static string HostShape(string host)
    {
        host = host.ToLowerInvariant();
        foreach (var suffix in new[] { ".sharepoint.com", ".sharepoint.us", ".sharepoint-mil.us", ".sharepoint.cn" })
        {
            if (host.EndsWith(suffix, StringComparison.Ordinal))
            {
                return (host[..^suffix.Length].EndsWith("-my", StringComparison.Ordinal) ? "<tenant>-my" : "<tenant>") + suffix;
            }
        }

        var labels = host.Split('.');
        if (labels.Length <= 2)
        {
            return Microsoft.Contains(host) ? host : "<host>";
        }

        var domain = string.Join('.', labels[^2..]);
        return string.Join('.', labels[..^2].Select(l => HostWords.Contains(l) ? l : "<x>")) + "." + (Microsoft.Contains(domain) ? domain : "<domain>");
    }

    private static readonly HashSet<string> Microsoft = new(StringComparer.Ordinal)
    {
        "microsoft.com", "microsoft.us", "office.com", "office365.com", "office365.us", "office.net", "skype.com", "live.com", "microsoftonline.com",
        "microsoftonline.us", "cloud.microsoft", "svc.ms", "sharepointonline.com", "azureedge.net", "windows.net", "adaptivecards.io",
    };

    private static readonly HashSet<string> HostWords = new(StringComparer.Ordinal)
    {
        "teams", "gov", "dod", "api", "asm", "ams", "asyncgw", "as-prod", "statics", "graph", "dod-graph", "login", "outlook", "substrate", "msg", "ng",
        "chatsvcagg", "middletier", "presence", "flightproxy", "trouter", "go", "stream", "webhook", "emea", "amer", "apac", "us-api", "eu-api",
        "www", "res", "cdn", "media", "blob", "core", "spoprod", "content", "office", "onedrive", "sharepoint", "my", "mt", "csa", "authsvc", "prod",
        "ic3", "spaces", "chatsvc", "msgapi", "uis", "people", "loki", "gcchigh",
    };

    /// <summary>A file name's kind (its extension, lower case), else "(none)"; never the name.</summary>
    internal static string Extension(string? name)
    {
        var kind = Path.GetExtension(name ?? "").ToLowerInvariant();
        return kind.Length is > 1 and <= 8 && kind[1..].All(char.IsAsciiLetterOrDigit) ? kind : "(none)";
    }

    /// <summary>A key as the report shows it: with any id in it (a guid, an mri, an address, a long number) taken out.</summary>
    internal static string Key(string key)
    {
        var shown = Guid().Replace(key, "<guid>");
        shown = Mri().Replace(shown, "<mri>");
        shown = Mail().Replace(shown, "<address>");
        shown = Number().Replace(shown, "<n>");
        return shown.Length <= 60 ? shown : shown[..60] + "…";
    }

    /// <summary>A value that names a kind, as the report shows it: kept when it is short and plain, else taken out.</summary>
    internal static string Safe(string? value)
    {
        if (value is null)
        {
            return "(none)";
        }

        var shown = Key(value);
        return shown.Length <= 80 && !shown.Contains('@', StringComparison.Ordinal) && PlainValue().IsMatch(shown) ? shown : "<value>";
    }

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex Guid();

    // an mri or a thread id (8:orgid:…, 19:…@thread.v2): a number, a colon, and something with a digit or an @ in it; a
    // special conversation's name (48:notifications) has neither and stays
    [GeneratedRegex(@"\b\d{1,2}:(?:[A-Za-z]+:)?(?=[A-Za-z0-9._@-]*[0-9@])[A-Za-z0-9._@-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Mri();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Mail();

    [GeneratedRegex(@"\d{6,}", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"^[A-Za-z0-9 ._/:+#<>()-]{1,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainValue();

    /// <summary>A token's audience and scopes (the names of what it may do), read from its claims; the token itself goes nowhere.</summary>
    internal static (string? Audience, string Scopes) Scopes(string token)
    {
        try
        {
            var parts = token.Split('.');
            var payload = parts.Length > 1 ? parts[1].Replace('-', '+').Replace('_', '/') : "";
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')));
            var root = doc.RootElement;
            var scp = root.TryGetProperty("scp", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var roles = root.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array ? string.Join(' ', r.EnumerateArray().Select(x => x.GetString())) : null;
            var audience = root.TryGetProperty("aud", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            return (audience, string.Join(' ', new[] { scp, roles }.Where(x => !string.IsNullOrEmpty(x))) is { Length: > 0 } names ? names : "(none named)");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return (null, "(not readable)");
        }
    }

    private static string AudienceShape(string? audience)
        => audience is null ? "(none)" : Uri.TryCreate(audience, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{HostShape(uri.Host)}" : Safe(audience);

    // --- JSON helpers --------------------------------------------------------------------------------------------------

    private static JsonElement Obj(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null,
        } : null;

    private static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
