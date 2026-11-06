using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.GitLab;

/// <summary>
/// GitLab's REST API (v4) and GraphQL, as the source asks them (spec 7.8): at most so many requests open at once (the
/// settings' projects at a time times the requests at a time per project), and no pace of AnyFS's own: until 5-Nov-2026 a
/// server of one's own was asked at most 50 a second (GitLab.com 25) whatever the settings said, which held the user's 3,500
/// projects to a few requests in flight. A throttled answer (429, 503 with Retry-After) holds the requests it names
/// (<see cref="RequestPace.Unpaced"/>, by host: the limit GitLab names in <c>RateLimit-Name</c>, for the kinds of request it
/// has named it for, else that kind of request) and is tried again, and an answer that says a limit's quota is used up holds
/// the same until it resets, so the server is not asked what it would refuse; a server error or a dropped connection tried
/// again twice; a refusal of the sign-in (401, or a page sent to the sign-in page) renewed once through the session and tried
/// again; a request sent on elsewhere (an upload kept in object storage: a signed address there for a time, 302 to a GET and
/// 303 to a HEAD as Grape answers them) followed, the token and the cookies kept to the server; pages followed by their Link
/// header, offset and keyset alike; errors as <see cref="VfsException"/>s by status.
/// </summary>
internal sealed class GitLabApi : IDisposable
{
    private const int Attempts = 3;
    private const int ThrottledAttempts = 6;
    private const int MostRedirects = 5;

    private readonly GitLabSession _session;
    private readonly RequestPace _pace;
    private readonly PriorityGate _gate;
    private readonly ILogger _logger;

    public GitLabApi(GitLabSession session, int concurrency, ILogger logger)
    {
        _session = session;
        _pace = HoldsFor(session.Address.Server.Host);

        // a client's request (a listing or an open someone waits on) takes the next free slot ahead of the walk's
        _gate = new PriorityGate(Math.Clamp(concurrency, 1, 64 * 64));
        _logger = logger;
    }

    /// <summary>The holds a server asks for (429, 503 with Retry-After, a quota used up), shared by every source and client of that host; no pace besides.</summary>
    public static RequestPace HoldsFor(string host) => RequestPace.Unpaced("gitlab:" + host);

    /// <summary>A hold began (or lengthened): how long, and why in GitLab's words (the status, the limit it named): for the tile.</summary>
    public event Action<TimeSpan, string>? Held;

    public GitLabAddress Address => _session.Address;

    /// <summary>One REST call's JSON (<paramref name="relative"/> to <c>/api/v4/</c>).</summary>
    public async Task<JsonDocument> GetAsync(string relative, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, new Uri(Address.Api, relative)), relative, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, relative, ct).ConfigureAwait(false);
    }

    /// <summary>One REST call's JSON, or null where GitLab answers 404 (a thing that is not there, or not to this account).</summary>
    public async Task<JsonDocument?> FindAsync(string relative, CancellationToken ct)
    {
        try
        {
            return await GetAsync(relative, ct).ConfigureAwait(false);
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Every element of a listing, page after page as the Link header leads (offset and keyset pagination alike): ask for
    /// 100 a page in <paramref name="relative"/>. The total, where GitLab gives it (X-Total), goes to <paramref name="total"/>.
    /// </summary>
    public async IAsyncEnumerable<JsonElement> PagesAsync(string relative, [EnumeratorCancellation] CancellationToken ct, Action<long>? total = null)
    {
        Uri? next = new(Address.Api, relative);
        var first = true;
        while (next is not null)
        {
            var url = next;
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), relative, ct).ConfigureAwait(false);
            if (first && total is not null && response.Headers.TryGetValues("X-Total", out var totals) && long.TryParse(totals.FirstOrDefault(), out var t))
            {
                total(t);
            }

            first = false;
            next = NextPage(response);
            using var doc = await ReadJsonAsync(response, relative, ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                yield return element.Clone();
            }
        }
    }

    /// <summary>
    /// The <c>rel="next"</c> address of a Link header, or null on the last page. GitLab writes it with its own configured
    /// address (its external URL), which need not be the one the source reaches it by (an address, an alias, a name only
    /// the server's own network knows): the next page is asked at the source's address, the link's path and query kept.
    /// </summary>
    internal Uri? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var links))
        {
            return null;
        }

        foreach (var part in string.Join(',', links).Split(','))
        {
            var pieces = part.Split(';');
            if (pieces.Length >= 2 && pieces.Skip(1).Any(p => p.Trim().Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase)))
            {
                var url = pieces[0].Trim().TrimStart('<').TrimEnd('>');
                if (Uri.TryCreate(url, UriKind.Absolute, out var next))
                {
                    return Rebased(next);
                }
            }
        }

        return null;
    }

    /// <summary>An API address GitLab gave, at the source's own address: its part after <c>/api/v4/</c> kept as it was escaped.</summary>
    internal Uri Rebased(Uri url)
    {
        const string marker = "/api/v4/";
        var path = url.GetComponents(UriComponents.Path | UriComponents.KeepDelimiter, UriFormat.UriEscaped);
        var at = path.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? url : new Uri(Address.Api, path[(at + marker.Length)..] + url.GetComponents(UriComponents.Query | UriComponents.KeepDelimiter, UriFormat.UriEscaped));
    }

    /// <summary>A GraphQL query's <c>data</c>; GraphQL's own errors become a <see cref="VfsException"/>.</summary>
    public async Task<JsonDocument> GraphQlAsync(string query, JsonObject variables, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString();
        using var response = await SendAsync(() =>
        {
            return new HttpRequestMessage(HttpMethod.Post, Address.GraphQl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }, "graphql", ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, "graphql", ct).ConfigureAwait(false);
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var message = string.Join("; ", errors.EnumerateArray().Select(e => e.Str("message")).Where(m => m is not null));
            doc.Dispose();
            throw new VfsException(VfsError.IoError, $"GitLab's GraphQL refused a query: {message}");
        }

        return doc;
    }

    /// <summary>A HEAD request's headers (relative to <c>/api/v4/</c>), by name in either case.</summary>
    public Task<Dictionary<string, string>> HeadAsync(string relative, CancellationToken ct) => HeadAsync(new Uri(Address.Api, relative), relative, ct);

    /// <summary>A HEAD request's headers (any address of the server: a page's), by name in either case.</summary>
    public async Task<Dictionary<string, string>> HeadAsync(Uri url, string what, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Head, url), what, ct).ConfigureAwait(false);
        return response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A download (a raw file, an upload, a job's artifact): the response with its body unread, for the caller to stream and dispose.</summary>
    public Task<HttpResponseMessage> OpenAsync(Uri url, string what, CancellationToken ct)
        => SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), what, ct, HttpCompletionOption.ResponseHeadersRead);

    /// <summary>A download relative to <c>/api/v4/</c>.</summary>
    public Task<HttpResponseMessage> OpenAsync(string relative, CancellationToken ct) => OpenAsync(new Uri(Address.Api, relative), relative, ct);

    /// <summary>
    /// Bytes at an offset (relative to <c>/api/v4/</c>), asked for with a Range header: whether the server gave just them
    /// (206), or ignored the range and sent everything (200), and what it sent.
    /// </summary>
    public async Task<(bool Partial, byte[] Bytes)> RangeAsync(string relative, long offset, int length, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Address.Api, relative));
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
            return request;
        }, relative, ct).ConfigureAwait(false);
        return (response.StatusCode == HttpStatusCode.PartialContent, await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
    }

    /// <summary>A small download's bytes.</summary>
    public async Task<byte[]> BytesAsync(Uri url, string what, CancellationToken ct)
    {
        using var response = await OpenAsync(url, what, ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request (made afresh for each attempt) at the server's pace and returns a successful response; refusals and
    /// failures become <see cref="VfsException"/>s, after the retries the class describes.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, string what, CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var renewed = false;
        var redirects = 0;
        Uri? sentOn = null;          // where an answer sent the request on, in place of its own address
        HttpMethod? sentAs = null;   // and as what: GET where it said so (303; 301 and 302 to a POST), else as it was
        for (var attempt = 1; ; attempt++)
        {
            var generation = _session.Generation;
            HttpResponseMessage response;
            string kind;
            HttpMethod own;
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

                kind = Kind(request);
                using var turn = await _pace.TakeAsync(kind, ct).ConfigureAwait(false);
                await _gate.WaitAsync(urgent: !Walks.Current, ct).ConfigureAwait(false);
                try
                {
                    await _session.AuthorizeAsync(request, ct).ConfigureAwait(false);

                    // a HEAD sent on as a GET: its headers are all that is wanted of the answer
                    var read = own == HttpMethod.Head && request.Method != own ? HttpCompletionOption.ResponseHeadersRead : completion;
                    response = await _session.Http.SendAsync(request, read, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when ((ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested) && attempt < Attempts)
                {
                    _logger.LogInformation("gitlab: {What} did not answer ({Error}); trying again", what, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    throw new VfsException(VfsError.Unavailable, $"GitLab ({Address.Server.Host}) did not answer: {ex.Message}", ex);
                }
                finally
                {
                    _gate.Release();
                }

                // every answer: a limit named, what is left of it, a hold asked for
                if (_pace.Hold(response, kind) is { } hold)
                {
                    var why = Why(response);
                    _logger.LogInformation("gitlab: {Host} asks AnyFS to wait {Seconds:N0} s before more requests like {Kind} ({Why}); the one it answered so: {What}", Address.Server.Host, hold.TotalSeconds, kind, why, what);
                    Held?.Invoke(hold, why);
                }

                turn.Answered();
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = response.StatusCode;
            var retryAfter = response.Headers.RetryAfter is not null;
            if (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                && response.Headers.Location is { } location)
            {
                var target = location.IsAbsoluteUri ? location : new Uri(response.RequestMessage?.RequestUri ?? Address.Server, location);
                response.Dispose();
                if (target.AbsolutePath.Contains("/users/sign_in", StringComparison.Ordinal))
                {
                    // sent to the sign-in page: a page GitLab serves to a browser session alone, or a session that has lapsed
                    if (!renewed && _session.CanRenew)
                    {
                        renewed = true;
                        await _session.RenewAsync(generation, ct).ConfigureAwait(false);
                        continue;
                    }

                    throw _session.CanRenew
                        ? new VfsException(VfsError.Unavailable, $"GitLab ({Address.Server.Host}) sent {what} to its sign-in page: the session has lapsed. Retry the source to sign in again.")
                        : new VfsException(VfsError.AccessDenied, $"GitLab serves {what} to a browser session, not to {_session.Settings.Who}: it sent the request to its sign-in page.");
                }

                if (++redirects > MostRedirects)
                {
                    throw new VfsException(VfsError.IoError, $"GitLab sent {what} on more than {MostRedirects} times, the last to {target.Host}.");
                }

                sentOn = Rebased(target);
                sentAs = status is HttpStatusCode.SeeOther || status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && own == HttpMethod.Post ? HttpMethod.Get : sentAs;
                attempt = 0;
                continue;
            }

            if (status == HttpStatusCode.Unauthorized && !renewed && _session.CanRenew)
            {
                response.Dispose();
                renewed = true;
                await _session.RenewAsync(generation, ct).ConfigureAwait(false);
                continue;
            }

            if ((status is HttpStatusCode.TooManyRequests || status is HttpStatusCode.ServiceUnavailable && retryAfter) && attempt < ThrottledAttempts
                || status is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable && attempt < Attempts)
            {
                response.Dispose();
                if (!retryAfter)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
                }

                continue;
            }

            using (response)
            {
                throw await ErrorAsync(response, what, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A request's kind, for the holds: its method and the shape of its path, with what names one thing (an id, a hash, a
    /// file's path as GitLab escapes it) elided, so a limit on one kind of request holds that kind and no other:
    /// <c>GET projects/*/repository/tree</c>.
    /// </summary>
    internal static string Kind(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        var api = path.IndexOf("/api/", StringComparison.Ordinal);
        var segments = (api >= 0 ? path[(api + "/api/".Length)..] : path).Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != "v4")
            .Select(s => s.All(char.IsAsciiDigit) || s.Length >= 32 && s.All(char.IsAsciiHexDigitLower) || s.Contains('%') ? "*" : s);
        return $"{request.Method.Method} {string.Join('/', segments)}";
    }

    /// <summary>Why a hold: the answer's status, and the limit GitLab named with what it observed of it.</summary>
    private static string Why(HttpResponseMessage response)
    {
        static string? Header(HttpResponseMessage r, string name) => r.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        var status = (int)response.StatusCode;
        var limit = Header(response, "RateLimit-Name");
        if (limit is null)
        {
            return status.ToString(CultureInfo.InvariantCulture);
        }

        var observed = Header(response, "RateLimit-Observed");
        var allowed = Header(response, "RateLimit-Limit");
        var counts = observed is not null && allowed is not null ? $": {observed} of {allowed} requests used" : "";
        return response.IsSuccessStatusCode ? $"the quota of {limit} is used up{counts}" : $"{status}, {limit}{counts}";
    }

    private async Task<VfsException> ErrorAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
        }

        var message = Message(body);
        var status = (int)response.StatusCode;
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => new VfsException(VfsError.NotFound, $"GitLab has no {what} for this account ({message ?? "404 Not Found"})"),
            HttpStatusCode.Forbidden => new VfsException(VfsError.AccessDenied, $"GitLab refused {what}: {message ?? "403 Forbidden"}"),
            HttpStatusCode.Unauthorized => new VfsException(VfsError.Unavailable, $"GitLab ({Address.Server.Host}) no longer takes {_session.Settings.Who}: {message ?? "401 Unauthorized"}. Retry the source to sign in again."),
            _ => new VfsException(VfsError.IoError, $"GitLab answered {status} to {what}{(message is null ? "" : ": " + message)}"),
        };
    }

    /// <summary>GitLab's own words for an error: its JSON's message or error, else a short start of the body.</summary>
    private static string? Message(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("message", out var m))
                {
                    return m.ValueKind == JsonValueKind.String ? m.GetString() : m.GetRawText();
                }

                return root.Str("error_description") ?? root.Str("error");
            }
        }
        catch (JsonException)
        {
        }

        var text = body.Trim();
        return text.StartsWith('<') ? null : text.Length <= 200 ? text : text[..200];
    }

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            // a sign-in page served in place of data: the session lapsed somewhere a 401 did not say so
            throw new VfsException(VfsError.Unavailable, $"GitLab ({Address.Server.Host}) answered {what} with {(type.Length == 0 ? "no data" : type)}, not data: the sign-in may have lapsed. Retry the source.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new VfsException(VfsError.IoError, $"GitLab answered {what} with JSON AnyFS cannot read: {ex.Message}", ex);
        }
    }

    /// <summary>Whether a response's length is known (Content-Length), for a file's size.</summary>
    public static long? LengthOf(HttpResponseMessage response) => response.Content.Headers.ContentLength;

    public void Dispose()
    {
    }
}
