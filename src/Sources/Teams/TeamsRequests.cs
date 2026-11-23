using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Teams;

/// <summary>
/// Teams' requests, sent by the loop every source's client shares (<see cref="RequestCore"/>; the review of 21-Nov-2026,
/// R10). Until then Teams had a loop of its own, which tried again only an answer that said it was busy: a connection
/// that dropped, a request that timed out or a failure of the service's own (500, 502, 504) failed the call at once, and
/// with failures held until the next walk a blip cost a folder for a whole walk. It kept its place through every wait.
/// A file's bytes, a recording and an inline image went round it altogether: no second try, no place taken, and a
/// request that timed out thrown as it came.
/// <para>
/// What is Teams' own: each call carries the bearer for its service's audience, and a 401 hands the token refused back
/// to whoever supplies them and tries once more with the one given then; a 403 is the account's permission and is not
/// asked again. A file's bytes are asked at an address that carries its own leave to read, which is why that address is
/// in no message.
/// </para>
/// </summary>
internal sealed class TeamsRequests : RequestCore
{
    /// <summary>Given with a file's bytes asked at an address that carries its own leave to read: no token goes with it.</summary>
    public static readonly object Download = new();

    /// <summary>Given with a request of Teams' media service (a recording, an inline image): the skype token is on it already.</summary>
    public static readonly object Media = new();

    private readonly Uri _teams;
    private readonly HttpClient _http;

    /// <param name="teams">Teams' own address, named where a request has none of its own.</param>
    /// <param name="http">The client the requests go through, this one's to dispose.</param>
    /// <param name="tokenFor">The bearer for a service; the second argument is a token the service just refused, else null.</param>
    public TeamsRequests(Uri teams, HttpClient http, Func<TeamsService, string?, CancellationToken, Task<string>> tokenFor, int concurrency, ILogger logger)

        // no pace of AnyFS's own (until 5-Nov-2026 at most 8 a second whatever the settings said): a service that asks
        // AnyFS to wait (429, 503 with Retry-After) is waited for, by every request of it together
        : base(new Tokens(http, tokenFor), RequestPace.Unpaced("teams"), Math.Clamp(concurrency, 1, 64), logger)
    {
        _teams = teams;
        _http = http;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override string Product => "Teams";

    protected override string Host => _teams.Host;

    protected override string LogName => "teams";

    protected override Uri Server => _teams;

    protected override string Who => "the web session";

    // each service has a host of its own, and the files are SharePoint's: a message names the one that was asked
    protected override string HostOf(Uri? address) => address?.Host ?? Host;

    protected override string? Words(string body) => body.Length == 0 ? null : Trim(body);

    /// <summary>One call's JSON, with the bearer for its <paramref name="service"/>; an answer with nothing in it is an empty object.</summary>
    public async Task<JsonDocument> JsonAsync(HttpMethod method, string url, TeamsService service, string? body, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Accept.ParseAdd("application/json");
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            return request;
        }, url, ct, hint: new Call(service)).ConfigureAwait(false);
        return await JsonOrNothingAsync(response, url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// An answer with its body unread, for the caller to stream and dispose: a <see cref="Download"/> or a request of the
    /// <see cref="Media"/> service. One asked past its end (416) is an answer too.
    /// </summary>
    public Task<HttpResponseMessage> OpenAsync(Func<HttpRequestMessage> make, object kind, string what, CancellationToken ct)
        => SendAsync(make, what, ct, HttpCompletionOption.ResponseHeadersRead, kind);

    protected override bool IsAnswer(HttpResponseMessage response, Sent sent)
        => response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && sent.Hint is not Call;

    // a token goes with a call alone: a 401 to anything else is its answer
    protected override bool MayRenew(Sent sent) => sent.Hint is Call;

    // a token the service no longer takes before its expiry (revoked, or a claims challenge): once more, with a fresh one
    protected override Task RenewAsync(int generation, Sent sent, CancellationToken ct)
    {
        if (sent.Hint is Call call)
        {
            call.Refused = call.Token;
            Logger.LogInformation("teams: {Host} refused the web session's token; asking for a fresh one", HostOf(sent.Address));
        }

        return Task.CompletedTask;
    }

    protected override void OnHold(TimeSpan hold, HttpResponseMessage response, string? kind, string what)
        => Logger.LogInformation("teams: {Host} is throttling; holding {Seconds:F0}s", HostOf(response.RequestMessage?.RequestUri), hold.TotalSeconds);

    protected override VfsException NotData(HttpResponseMessage response, string what)

        // a proxy's or a portal's page in place of the service's answer
        => new(VfsError.IoError, $"Teams ({HostOf(response.RequestMessage?.RequestUri)}) answered with something other than JSON ({ContentType(response)})");

    protected override async Task<VfsException> ErrorAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var status = response.StatusCode;
        var code = (int)status;
        var kind = HintOf(response.RequestMessage);
        if (ReferenceEquals(kind, Media))
        {
            return status switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.Gone => new VfsException(VfsError.NotFound, "Teams' media service has no such recording (any more)"),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new VfsException(VfsError.AccessDenied, $"Teams' media service refused the recording ({code})"),
                _ => VfsException.Unavailable($"Teams' media service answered {code}"),
            };
        }

        var message = $"Teams ({HostOf(response.RequestMessage?.RequestUri)}) answered {code}";
        var said = Trim(await BodyAsync(response, ct).ConfigureAwait(false));
        return status switch
        {
            // a refusal after the one fresh token is the account's reach, not a sign-in to make again
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => VfsException.AccessDenied($"{message}: the sign-in does not reach this ({said})"),

            // the address of a call names what was asked; a download's carries its leave to read, and stays out
            HttpStatusCode.NotFound => ReferenceEquals(kind, Download) ? new VfsException(VfsError.NotFound, $"{message}: {what} is not there (any more)") : VfsException.NotFound(what),

            // busy at every try: the service's, as in every source (it was the request's own failure here, and the tile stayed Ready)
            HttpStatusCode.TooManyRequests => VfsException.Unavailable($"{message}: it is busy"),
            >= HttpStatusCode.InternalServerError => VfsException.Unavailable(message),
            _ => new VfsException(VfsError.IoError, $"{message}: {said}"),
        };
    }

    private static string Trim(string body) => body.Length <= 200 ? body : body[..200];

    /// <summary>A call of one of Teams' services: whose bearer goes with it, and the one it last went with.</summary>
    private sealed class Call(TeamsService service)
    {
        public TeamsService Service => service;

        public string? Token { get; set; }

        /// <summary>The token the service just refused, to hand back with the next ask for one.</summary>
        public string? Refused { get; set; }
    }

    /// <summary>The bearers, as the loop asks for a sign-in: one for a call's service, none for anything else.</summary>
    private sealed class Tokens(HttpClient http, Func<TeamsService, string?, CancellationToken, Task<string>> tokenFor) : IRequestSession
    {
        public HttpClient Http => http;

        // a refusal is told by the token it refused (Call.Refused), not by a count
        public int Generation => 0;

        public bool CanRenew => true;

        public async ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (HintOf(request) is Call call)
            {
                var token = await tokenFor(call.Service, call.Refused, ct).ConfigureAwait(false);
                call.Refused = null;
                call.Token = token;
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        public Task RenewAsync(int seen, CancellationToken ct) => Task.CompletedTask;
    }
}
