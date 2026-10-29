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
/// gives then. Requests are paced per host and a 429's Retry-After is honoured.
/// </summary>
/// <param name="tokenFor">The bearer for a service; the second argument is a token the service just refused, else null.</param>
/// <param name="handler">The handler requests go through: a test's fake; null for the network.</param>
internal sealed class TeamsRest(Uri teamsApiHost, Func<TeamsService, string?, CancellationToken, Task<string>> tokenFor, int concurrency, ILogger logger, HttpMessageHandler? handler = null) : ITeamsApi
{
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
                channels.Add(new ChannelInfo(Str(c, "id") ?? "", Str(c, "displayName") ?? "", Bool(c, "isGeneral"), Int(c, "channelType") != 0));
            }

            var groupId = Str(Obj(t, "teamSiteInformation"), "groupId") ?? Str(t, "groupId");
            teams.Add(new TeamInfo(Str(t, "id") ?? "", Str(t, "displayName") ?? "(team)", groupId, channels));
        }

        var chats = new List<ChatInfo>();
        foreach (var c in Arr(doc.RootElement, "chats"))
        {
            var members = Arr(c, "members").Select(m => Str(m, "mri") ?? "").Where(m => m.Length > 0).ToList();
            chats.Add(new ChatInfo(Str(c, "id") ?? "", NullIfEmpty(Str(c, "title")), KindOf(c), members, Time(Str(c, "lastUpdatedTime") ?? Str(c, "version"))));
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
            messages.Add(new TeamsMessage(
                Str(m, "id") ?? "",
                NullIfEmpty(root),
                NullIfEmpty(parent),
                MriFrom(Str(m, "from")),
                Time(Str(m, "composetime") ?? Str(m, "originalarrivaltime")),
                type,
                NullIfEmpty(Str(m, "content")),
                NullIfEmpty(Str(m, "imdisplayname"))));
        }

        var older = Str(Obj(doc.RootElement, "_metadata"), "backwardLink");
        return new MessagePage(messages, NullIfEmpty(older));
    }

    // --- service discovery ---------------------------------------------------------------------------------------------

    private async Task<Services> DiscoverAsync(CancellationToken ct)
    {
        if (_services is { } ready)
        {
            return ready;
        }

        using var doc = await SendAsync(HttpMethod.Post, $"{teamsApiHost.AbsoluteUri.TrimEnd('/')}/api/authsvc/v1.0/authz", TeamsService.MiddleTier, "{}", ct).ConfigureAwait(false);
        var gtms = Obj(doc.RootElement, "regionGtms");

        // each service's own host, else its front door on the Teams host (both clouds name both)
        var aggregator = (Str(gtms, "chatServiceAggregator") ?? Str(gtms, "chatSvcAggAfd") ?? throw VfsException.Unavailable("Teams did not say where its aggregator is")).TrimEnd('/');
        var chatService = (Str(gtms, "chatService") ?? Str(gtms, "chatServiceAfd") ?? throw VfsException.Unavailable("Teams did not say where its chat service is")).TrimEnd('/');
        var middleTier = (Str(gtms, "middleTier") ?? $"{teamsApiHost.AbsoluteUri.TrimEnd('/')}/api/mt/apac").TrimEnd('/');
        logger.LogInformation("teams: services for {Account}: aggregator {Aggregator}, chat service {ChatService}", Account, aggregator, chatService);
        return _services = new Services(aggregator, chatService, middleTier);
    }

    private sealed record Services(string Aggregator, string ChatService, string MiddleTier);

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
