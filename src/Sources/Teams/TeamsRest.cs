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
/// gives then. Requests are paced per host and a 429's Retry-After is honoured. Files are read from SharePoint and OneDrive
/// (their v2.0 drive API, with the SharePoint token, only ever on a SharePoint host) and inline images from Teams' media
/// service (with the authz call's skype token, only ever on the media service's hosts).
/// </summary>
/// <param name="tokenFor">The bearer for a service; the second argument is a token the service just refused, else null.</param>
/// <param name="handler">The handler requests go through: a test's fake; null for the network.</param>
/// <param name="sharePointHost">The host the page used the SharePoint token on (the tenant's OneDrive or SharePoint), once that token is read.</param>
internal sealed class TeamsRest(Uri teamsApiHost, Func<TeamsService, string?, CancellationToken, Task<string>> tokenFor, int concurrency, ILogger logger, HttpMessageHandler? handler = null, Func<string?>? sharePointHost = null) : ITeamsApi
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
                });
            }

            var groupId = Str(Obj(t, "teamSiteInformation"), "groupId") ?? Str(t, "groupId");
            teams.Add(new TeamInfo(Str(t, "id") ?? "", Str(t, "displayName") ?? "(team)", groupId, channels));
        }

        var chats = new List<ChatInfo>();
        foreach (var c in Arr(doc.RootElement, "chats"))
        {
            var members = Arr(c, "members").Select(m => Str(m, "mri") ?? "").Where(m => m.Length > 0).ToList();
            chats.Add(new ChatInfo(Str(c, "id") ?? "", NullIfEmpty(Str(c, "title")), KindOf(c), members, Time(Str(c, "lastUpdatedTime") ?? Str(c, "version")))
            {
                Version = NullIfEmpty(Str(c, "version")),
            });
        }

        return new TeamsStructure(teams, chats);
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
                people.Add(new Person(mri, name!));
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

    // --- files: SharePoint and OneDrive ----------------------------------------------------------------------------------

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<TeamSite?>> _sites = new(StringComparer.OrdinalIgnoreCase);

    public async Task<(string SiteUrl, string Path)?> ChannelFilesAsync(TeamInfo team, ChannelInfo channel, CancellationToken ct)
    {
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

        var site = await _sites.GetOrAdd(team.GroupId, id => SiteAsync($"https://{host}", id, CancellationToken.None)).ConfigureAwait(false);
        if (site is null)
        {
            _sites.TryRemove(team.GroupId, out _);
            return null;
        }

        return (site.Url, channel.DisplayName);
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

    public async Task<IReadOnlyList<DriveEntry>> FolderAsync(string siteUrl, string path, CancellationToken ct)
    {
        var entries = new List<DriveEntry>();
        string? next = path.Length == 0 ? $"{SharePointSite(siteUrl)}/_api/v2.0/drive/root/children?$top=999" : $"{SharePointSite(siteUrl)}/_api/v2.0/drive/root:/{EscapePath(path)}:/children?$top=999";
        try
        {
            while (next is not null)
            {
                using var doc = await GetAsync(next, TeamsService.SharePoint, ct).ConfigureAwait(false);
                entries.AddRange(Arr(doc.RootElement, "value").Select(DriveEntryOf).OfType<DriveEntry>());
                next = Str(doc.RootElement, "@odata.nextLink") is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) && TeamsServices.IsSharePointHost(uri.Host) ? link : null;
            }
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            // a channel nobody has shared a file in has no folder yet
        }

        return entries;
    }

    public async Task<DriveEntry?> FileAsync(string siteUrl, string path, CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync($"{SharePointSite(siteUrl)}/_api/v2.0/drive/root:/{EscapePath(path)}", TeamsService.SharePoint, ct).ConfigureAwait(false);
            return DriveEntryOf(doc.RootElement);
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
        return new DriveEntry(name!, folder, folder ? 0 : size, Time(Str(item, "lastModifiedDateTime")), folder ? null : Str(item, "@content.downloadUrl"));
    }

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
            });
        }

        var older = Str(Obj(doc.RootElement, "_metadata"), "backwardLink");
        return new MessagePage(messages, NullIfEmpty(older));
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

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, TeamsService service, string? body, CancellationToken ct)
    {
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

                    return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
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
