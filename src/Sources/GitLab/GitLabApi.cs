using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
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
/// header, offset and keyset alike; errors as <see cref="VfsException"/>s by status. The sending is
/// <see cref="RequestCore"/>'s, the same for every source; what is GitLab's own is here.
/// </summary>
internal sealed class GitLabApi : RequestCore
{
    private readonly GitLabSession _session;

    // a client's request (a listing or an open someone waits on) takes the next free slot ahead of the walk's
    public GitLabApi(GitLabSession session, int concurrency, ILogger logger)
        : base(session, HoldsFor(session.Address.Server.Host), Math.Clamp(concurrency, 1, 64 * 64), logger)
    {
        _session = session;
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
        return await JsonAsync(response, relative, ct).ConfigureAwait(false);
    }

    /// <summary>One REST call's JSON, or null where GitLab answers 404 (a thing that is not there, or not to this account).</summary>
    public Task<JsonDocument?> FindAsync(string relative, CancellationToken ct) => FoundAsync(GetAsync(relative, ct));


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
            using var doc = await JsonAsync(response, relative, ct).ConfigureAwait(false);
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
        var doc = await JsonAsync(response, "graphql", ct).ConfigureAwait(false);
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

    protected override string Product => "GitLab";

    protected override string Host => Address.Server.Host;

    protected override string LogName => "gitlab";

    protected override Uri Server => Address.Server;

    // an upload kept in object storage is answered with where it is, for a time
    protected override bool FollowsRedirects => true;

    protected override bool RedirectsChangeMethod => true;

    // every answer: a limit named, what is left of it, a hold asked for
    protected override bool HoldsOnEveryAnswer => true;

    protected override string? KindOf(HttpRequestMessage request) => Kind(request);

    protected override Uri Rebase(Uri target) => Rebased(target);

    // sent to the sign-in page: a page GitLab serves to a browser session alone, or a session that has lapsed
    protected override bool IsSignInPage(Uri target) => target.AbsolutePath.Contains("/users/sign_in", StringComparison.Ordinal);

    protected override VfsException SentToSignIn(string what)
        => _session.CanRenew
            ? new VfsException(VfsError.Unavailable, $"GitLab ({Address.Server.Host}) sent {what} to its sign-in page: the session has lapsed. Retry the source to sign in again.")
            : new VfsException(VfsError.AccessDenied, $"GitLab serves {what} to a browser session, not to {_session.Settings.Who}: it sent the request to its sign-in page.");

    protected override void OnHold(TimeSpan hold, HttpResponseMessage response, string? kind, string what)
    {
        var why = Why(response);
        Logger.LogInformation("gitlab: {Host} asks AnyFS to wait {Seconds:N0} s before more requests like {Kind} ({Why}); the one it answered so: {What}", Address.Server.Host, hold.TotalSeconds, kind, why, what);
        Held?.Invoke(hold, why);
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

        // a hold is asked for by a throttled answer, or by what any answer (a 404's too) says is left of the limit: nothing
        var observed = Header(response, "RateLimit-Observed");
        var allowed = Header(response, "RateLimit-Limit");
        var counts = observed is not null && allowed is not null ? $": {observed} of {allowed} requests used" : "";
        return response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable ? $"{status}, {limit}{counts}" : $"the quota of {limit} is used up{counts}";
    }

    protected override string Who => _session.Settings.Who;

    protected override string? Words(string body) => Message(body);
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

    /// <summary>Whether a response's length is known (Content-Length), for a file's size.</summary>
    public static long? LengthOf(HttpResponseMessage response) => response.Content.Headers.ContentLength;
}
