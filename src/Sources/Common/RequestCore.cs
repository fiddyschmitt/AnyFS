using System.Net;
using System.Text.Json;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Common;

/// <summary>
/// What a <see cref="RequestCore"/> signs its requests with: a source's session, which knows the sign-in and can make it
/// again when the server no longer takes it.
/// </summary>
public interface IRequestSession
{
    /// <summary>The client the requests go through.</summary>
    HttpClient Http { get; }

    /// <summary>A number that moves each time the sign-in is made again: read as a request is signed, so a renewal asked for an answer to it is skipped where another request's has been since.</summary>
    int Generation { get; }

    /// <summary>Whether the sign-in can be made again without the person (a session, not a token they typed).</summary>
    bool CanRenew { get; }

    /// <summary>Puts the sign-in on a request.</summary>
    ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken ct);

    /// <summary>Makes the sign-in again, unless it has been since <paramref name="seen"/>.</summary>
    Task RenewAsync(int seen, CancellationToken ct);
}

/// <summary>
/// The one loop the sources' HTTP clients send a request through, where each had a copy of its own that had drifted
/// from the others (the review of 21-Nov-2026, C1). A request is made afresh for each attempt and goes at the server's
/// pace (<see cref="RequestPace"/>: past any hold it asked for) with at most so many open at once, a person's ahead of
/// a walk's (<see cref="PriorityGate"/>). A connection that drops or does not answer is tried again, its place given
/// back for the pause; so is a throttled answer (429, or 503 with Retry-After), which is waited out, and a server's own
/// failure (500, 502, 503, 504). A sign-in the server no longer takes is made again once through the session and the
/// request tried again. A request sent on elsewhere is followed where the client says so. What is left is an error, as a
/// <see cref="VfsException"/> by the answer's status, the same for every client (<see cref="Error"/>).
/// <para>
/// A client is one of these with the hooks filled in for its product: what a lapsed sign-in looks like there, what its
/// holds are called, how its errors read. What all share is here once, and is changed here once.
/// </para>
/// </summary>
public abstract class RequestCore : IDisposable
{
    private readonly IRequestSession _session;
    private readonly RequestPace _pace;
    private readonly PriorityGate _gate;

    /// <param name="concurrency">How many requests may be open with the server at once.</param>
    protected RequestCore(IRequestSession session, RequestPace pace, int concurrency, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pace);
        ArgumentNullException.ThrowIfNull(logger);
        _session = session;
        _pace = pace;
        _gate = new PriorityGate(concurrency);
        Logger = logger;
    }

    protected ILogger Logger { get; }

    /// <summary>The holds and the pace the requests keep to.</summary>
    protected RequestPace Pace => _pace;

    /// <summary>How many requests may be open at once.</summary>
    protected int Slots => _gate.Slots;

    /// <summary>The product as a message names it: "GitLab".</summary>
    protected abstract string Product { get; }

    /// <summary>The server's host, for the messages.</summary>
    protected abstract string Host { get; }

    /// <summary>What the log lines begin with: "gitlab".</summary>
    protected abstract string LogName { get; }

    /// <summary>Where a relative Location is taken from when an answer names no request of its own.</summary>
    protected abstract Uri Server { get; }

    /// <summary>How many times a request is sent before a connection that drops, or a server's own failure, is its answer.</summary>
    protected virtual int Attempts => 3;

    /// <summary>How many times a request is sent before a throttled answer is its answer.</summary>
    protected virtual int ThrottledAttempts => 6;

    /// <summary>How many times a request is followed where it is sent on.</summary>
    protected virtual int MostRedirects => 5;

    /// <summary>Whether an answer that sends the request on (301, 302, 303, 307, 308 with a Location) is followed here; where not, it is an answer like any other.</summary>
    protected virtual bool FollowsRedirects => false;

    /// <summary>Whether a request sent on becomes a GET as browsers make it one (303; 301 and 302 to a POST).</summary>
    protected virtual bool RedirectsChangeMethod => false;

    /// <summary>Whether every answer is looked at for a hold or a quota (a service that names what is left on each), not only one that is no success.</summary>
    protected virtual bool HoldsOnEveryAnswer => false;

    /// <summary>How long to pause before a request is sent again: after no answer, or after a failure of the server's own that named no time.</summary>
    protected virtual TimeSpan Pause(int attempt, bool answered) => TimeSpan.FromSeconds(answered ? attempt * 2 : attempt);

    /// <summary>A request's kind, for a service that holds one kind of request and not another; null where every request is one kind.</summary>
    protected virtual string? KindOf(HttpRequestMessage request) => null;

    /// <summary>Where a request is sent on to, as this client sends it there (an address put back under the client's own where it left it).</summary>
    protected virtual Uri Rebase(Uri target) => target;

    /// <summary>
    /// Whether a request sent on goes to the sign-in page: a session that lapsed, or a page served to a session alone.
    /// Such a one is not followed: the sign-in is made again, or it is <see cref="SentToSignIn"/>.
    /// </summary>
    protected virtual bool IsSignInPage(Uri target) => false;

    /// <summary>What a request sent to the sign-in page comes to once the sign-in cannot be made again (or has been).</summary>
    protected virtual VfsException SentToSignIn(string what)
        => new(VfsError.Unavailable, $"{Product} ({Host}) sent {what} to its sign-in page: the session has lapsed. Retry the source to sign in again.");

    /// <summary>
    /// Whether an answer says the sign-in is no longer taken: 401, unless the client knows more (an answer served to the
    /// anonymous user, a login page in place of data).
    /// </summary>
    protected virtual bool IsLapsed(HttpResponseMessage response, Sent sent)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.StatusCode == HttpStatusCode.Unauthorized;
    }

    /// <summary>What a lapsed answer comes to where the sign-in cannot be made again, or has been; null for <see cref="ErrorAsync"/>'s word on the answer.</summary>
    protected virtual VfsException? Lapsed(HttpResponseMessage response, string what, Sent sent) => null;

    /// <summary>Whether the sign-in may be made again for this request (not for one that has been sent on to another server).</summary>
    protected virtual bool MayRenew(Sent sent) => true;

    /// <summary>A hold began or lengthened: for the client's log line, and whoever it tells.</summary>
    protected virtual void OnHold(TimeSpan hold, HttpResponseMessage response, string? kind, string what)
    {
        ArgumentNullException.ThrowIfNull(response);
        Logger.LogInformation("{Source}: {Host} asks AnyFS to wait {Seconds:N0} s ({Status})", LogName, Host, hold.TotalSeconds, (int)response.StatusCode);
    }

    /// <summary>An answer that is to end the request with an exception of the client's own before it is tried again (a refusal its caller sends again itself); null for none.</summary>
    protected virtual Exception? Refusal(HttpResponseMessage response, Sent sent) => null;

    /// <summary>The account as a message names it.</summary>
    protected abstract string Who { get; }

    /// <summary>The product's own words for an error, from an answer's body; null where it has none (a gateway's page).</summary>
    protected abstract string? Words(string body);

    /// <summary>The error an answer that is no success comes to: <see cref="Error"/>, with the product's words for it.</summary>
    protected virtual async Task<VfsException> ErrorAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Error(response.StatusCode, what, Words(await BodyAsync(response, ct).ConfigureAwait(false)));
    }

    /// <summary>
    /// What a status comes to once the tries are over, the same whichever product answered (each client had a table of
    /// its own, and the same answer was a different error by source: the review of 21-Nov-2026, R8). A thing that is not
    /// there, or not to this account, is NotFound; a refusal of the account is AccessDenied. A server that cannot be had
    /// now is Unavailable, which is what turns a source's tile: a sign-in it no longer takes, a server that is busy (429,
    /// 503) or one that what stands in front of it cannot reach (502, 504). Anything else is the request's own failure
    /// (IoError), a 500 among them: a server's fault on the one thing asked says nothing of the rest. Each carries the
    /// status (<see cref="StatusOf"/>).
    /// </summary>
    protected virtual VfsException Error(HttpStatusCode status, string what, string? message)
    {
        var code = (int)status;
        var said = message is null ? "" : ": " + message;
        var answer = new HttpRequestException(message, null, status);
        return status switch
        {
            HttpStatusCode.NotFound => new VfsException(VfsError.NotFound, $"{Product} has no {what} for this account ({message ?? "404 Not Found"})", answer),
            HttpStatusCode.Forbidden => new VfsException(VfsError.AccessDenied, $"{Product} refused {what}: {message ?? "403 Forbidden"}", answer),
            HttpStatusCode.Unauthorized => new VfsException(VfsError.Unavailable, $"{Product} ({Host}) no longer takes {Who}: {message ?? "401 Unauthorized"}. Retry the source to sign in again.", answer),
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => new VfsException(VfsError.Unavailable, $"{Product} ({Host}) is busy ({code}){said}", answer),
            HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout => new VfsException(VfsError.Unavailable, $"{Product} ({Host}) is not answering ({code}){said}", answer),
            _ => new VfsException(VfsError.IoError, $"{Product} answered {code} to {what}{said}", answer),
        };
    }

    /// <summary>The status of the answer an error came from; null for one that came from no answer (a connection that dropped, a sign-in that could not be made again).</summary>
    public static HttpStatusCode? StatusOf(VfsException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return (error.InnerException as HttpRequestException)?.StatusCode;
    }

    /// <summary>
    /// Sends a request (made afresh for each attempt) and returns a successful response; refusals and failures become
    /// <see cref="VfsException"/>s, after the retries the class describes.
    /// </summary>
    /// <param name="hint">What the client knows of the request, which its hooks read back from <see cref="Sent.Hint"/>.</param>
    protected async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, string what, CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, object? hint = null)
    {
        ArgumentNullException.ThrowIfNull(make);
        var renewed = false;
        var redirects = 0;
        Uri? sentOn = null;          // where an answer sent the request on, in place of its own address
        HttpMethod? sentAs = null;   // and as what: GET where it said so, else as it was
        for (var attempt = 1; ; attempt++)
        {
            var generation = 0;
            HttpResponseMessage? response = null;
            Exception? dropped = null;
            HttpMethod own;
            string? kind;
            using (var request = make())
            {
                own = request.Method;
                if (sentOn is not null)
                {
                    request.RequestUri = sentOn;
                    if (sentAs is not null && sentAs != own)
                    {
                        request.Method = sentAs;
                        request.Content = null;
                    }
                }

                kind = KindOf(request);
                using var turn = await _pace.TakeAsync(kind, ct).ConfigureAwait(false);
                await _gate.WaitAsync(urgent: !Walks.Current, ct).ConfigureAwait(false);
                try
                {
                    // the sign-in this request goes with: read as it is signed and not before the waits, where another
                    // request's renewal may come between and this one's refusal would then ask for none
                    generation = _session.Generation;
                    await _session.AuthorizeAsync(request, ct).ConfigureAwait(false);

                    // a HEAD sent on as a GET: its headers are all that is wanted of the answer
                    var read = own == HttpMethod.Head && request.Method != own ? HttpCompletionOption.ResponseHeadersRead : completion;
                    response = await _session.Http.SendAsync(request, read, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    dropped = ex;
                }
                finally
                {
                    _gate.Release();
                }

                if (response is not null)
                {
                    if (HoldsOnEveryAnswer)
                    {
                        NoteHold(response, kind, what);
                    }

                    turn.Answered();
                }
            }

            if (response is null)
            {
                if (attempt >= Attempts)
                {
                    throw new VfsException(VfsError.Unavailable, $"{Product} ({Host}) did not answer: {dropped!.Message}", dropped);
                }

                // paused with the place given back: each client held it through the pause, so a person's request waited
                // behind a walk's that slept
                Logger.LogInformation("{Source}: {What} did not answer ({Error}); trying again", LogName, what, dropped!.Message);
                await Task.Delay(Pause(attempt, answered: false), ct).ConfigureAwait(false);
                continue;
            }

            var sent = new Sent(sentOn, hint);
            var lapsed = IsLapsed(response, sent);
            if (response.IsSuccessStatusCode && !lapsed)
            {
                return response;
            }

            var status = response.StatusCode;
            if (FollowsRedirects
                && status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                && response.Headers.Location is { } location)
            {
                var target = location.IsAbsoluteUri ? location : new Uri(response.RequestMessage?.RequestUri ?? Server, location);
                response.Dispose();
                if (IsSignInPage(target))
                {
                    if (!renewed && _session.CanRenew)
                    {
                        renewed = true;
                        await _session.RenewAsync(generation, ct).ConfigureAwait(false);
                        continue;
                    }

                    throw SentToSignIn(what);
                }

                if (++redirects > MostRedirects)
                {
                    throw new VfsException(VfsError.IoError, $"{Product} sent {what} on more than {MostRedirects} times, the last to {target.Host}.");
                }

                sentOn = Rebase(target);
                if (RedirectsChangeMethod)
                {
                    sentAs = status is HttpStatusCode.SeeOther || status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && own == HttpMethod.Post ? HttpMethod.Get : sentAs;
                }

                attempt = 0;
                continue;
            }

            if (lapsed && !renewed && _session.CanRenew && MayRenew(sent))
            {
                response.Dispose();
                renewed = true;
                await _session.RenewAsync(generation, ct).ConfigureAwait(false);
                continue;
            }

            if (!HoldsOnEveryAnswer)
            {
                NoteHold(response, kind, what);
            }

            if (Refusal(response, sent) is { } refusal)
            {
                response.Dispose();
                throw refusal;
            }

            var retryAfter = response.Headers.RetryAfter is not null;
            if ((status is HttpStatusCode.TooManyRequests || status is HttpStatusCode.ServiceUnavailable && retryAfter) && attempt < ThrottledAttempts
                || status is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable && attempt < Attempts)
            {
                response.Dispose();
                if (!retryAfter)
                {
                    await Task.Delay(Pause(attempt, answered: true), ct).ConfigureAwait(false);
                }

                continue;
            }

            using (response)
            {
                throw (lapsed ? Lapsed(response, what, sent) : null) ?? await ErrorAsync(response, what, ct).ConfigureAwait(false);
            }
        }
    }

    private void NoteHold(HttpResponseMessage response, string? kind, string what)
    {
        if (_pace.Hold(response, kind) is { } hold)
        {
            OnHold(hold, response, kind, what);
        }
    }

    /// <summary>Whether an answer carries JSON, by what it says it carries.</summary>
    protected static bool IsJson(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>What an answer says it carries, for a message.</summary>
    protected static string ContentType(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Content.Headers.ContentType?.MediaType is { Length: > 0 } type ? type : "no data";
    }

    /// <summary>An answer's JSON, read from its stream; the answer stays the caller's to dispose.</summary>
    protected async Task<JsonDocument> JsonAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!IsJson(response))
        {
            throw NotData(response, what);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new VfsException(VfsError.IoError, $"{Product} answered {what} with JSON AnyFS cannot read: {ex.Message}", ex);
            }
        }
    }

    /// <summary>What a successful answer that is not JSON comes to where JSON was asked for: a page served in place of data, as a lapsed session is somewhere a 401 does not say so.</summary>
    protected virtual VfsException NotData(HttpResponseMessage response, string what)
        => new(VfsError.Unavailable, $"{Product} ({Host}) answered {what} with {ContentType(response)}, not data: the sign-in may have lapsed. Retry the source.");

    /// <summary>An answer's body as text for an error's words; empty where it cannot be read.</summary>
    protected static async Task<string> BodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return "";
        }
    }

    /// <summary>What a call answers, or null where the server answers 404 (a thing that is not there, or not to this account).</summary>
    public static async Task<T?> FoundAsync<T>(Task<T> call)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            return await call.ConfigureAwait(false);
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            return null;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>What the hooks are told of the request an answer is to.</summary>
    /// <param name="SentOn">Where an earlier answer sent it on to; null while it goes to its own address.</param>
    /// <param name="Hint">What the client gave <see cref="SendAsync"/> with it.</param>
    protected readonly record struct Sent(Uri? SentOn, object? Hint);
}
