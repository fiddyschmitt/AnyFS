using System.Net;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Atlassian;

/// <summary>
/// Jira's, Confluence's, Bitbucket's or Bamboo's REST API, as the sources ask it (spec 7.10): every request at the
/// server's pace (<see cref="RequestPace"/>, by host) with at most so many open at once, a client's ahead of a walk's; a
/// throttled answer (429, 503 with Retry-After) held and tried again; a server error or a dropped connection tried again
/// twice; a refusal of the sign-in (401, an answer served to the anonymous user after the session lapsed, or a login page
/// in place of data) renewed once through the session and tried again; errors as <see cref="VfsException"/>s by status.
/// The sending is <see cref="RequestCore"/>'s, the same for every source; what is Atlassian's own is here.
/// </summary>
internal sealed class AtlassianApi : RequestCore
{
    /// <summary>Given with a request for a download: an answer to it that is not JSON is what was asked for, not a login page.</summary>
    private static readonly object Download = new();

    private readonly AtlassianSession _session;

    // no pace of AnyFS's own (until 5-Nov-2026 a server was asked at most 25 a second whatever the settings said): a server
    // that asks AnyFS to wait (429, 503 with Retry-After) is waited for, by every request of it together. As many at once as
    // the settings ask for: Bitbucket's and Bamboo's are two settings of up to 64 multiplied (until 22-Nov-2026 the gate
    // stopped at 64 whatever they came to); the session's connections to the server are not bounded either
    public AtlassianApi(AtlassianSession session, int concurrency, ILogger logger)
        : base(session, RequestPace.Unpaced($"{session.Product}:{session.Address.Base.Authority}"), Math.Clamp(concurrency, 1, 64 * 64), logger)
    {
        _session = session;
    }

    public AtlassianAddress Address => _session.Address;

    public AtlassianSession Session => _session;

    private string Name => _session.ProductName;

    protected override string Product => Name;

    protected override string Host => Address.Base.Host;

    protected override string LogName => AtlassianSignInTarget.Name(_session.Product);

    protected override Uri Server => Address.Base;

    /// <summary>An address of the product's REST API (<c>search?jql=…</c> under Jira's <c>rest/api/2/</c>).</summary>
    public Uri Rest(string relative) => new(Address.Api(_session.Product), relative);

    /// <summary>One REST call's JSON (<paramref name="relative"/> to the product's REST API).</summary>
    public Task<JsonDocument> GetAsync(string relative, CancellationToken ct) => GetAsync(Rest(relative), relative, ct);

    /// <summary>One call's JSON, at any address of the installation (another REST API's: Jira Software's <c>rest/agile/1.0</c>).</summary>
    public async Task<JsonDocument> GetAsync(Uri url, string what, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), what, ct).ConfigureAwait(false);
        return await JsonAsync(response, what, ct).ConfigureAwait(false);
    }

    /// <summary>One REST call's JSON, or null where the server answers 404 (a thing that is not there, or not to this account).</summary>
    public Task<JsonDocument?> FindAsync(string relative, CancellationToken ct) => FoundAsync(GetAsync(relative, ct));


    /// <summary>A POST of JSON (Jira's search, whose JQL may not fit an address) and the JSON it answers.</summary>
    public async Task<JsonDocument> PostAsync(string relative, string json, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Rest(relative)) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

            // a POST that only reads, with a session's cookies, still passes the XSRF check this way
            request.Headers.TryAddWithoutValidation("X-Atlassian-Token", "no-check");
            return request;
        }, relative, ct).ConfigureAwait(false);
        return await JsonAsync(response, relative, ct).ConfigureAwait(false);
    }

    /// <summary>A download (an attachment): the response with its body unread, for the caller to stream and dispose.</summary>
    public Task<HttpResponseMessage> OpenAsync(Uri url, string what, CancellationToken ct)
        => SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), what, ct, HttpCompletionOption.ResponseHeadersRead, Download);

    /// <summary>A download's headers alone (its size, its kind), by HEAD; the caller disposes the response.</summary>
    public Task<HttpResponseMessage> HeadAsync(Uri url, string what, CancellationToken ct)
        => SendAsync(() => new HttpRequestMessage(HttpMethod.Head, url), what, ct, HttpCompletionOption.ResponseHeadersRead, Download);

    // a lapsed session is served as the anonymous user where the site lets anyone read (less, not a refusal), or as a
    // login page in place of data where a sign-in proxy answers for the server (at 200, not a redirect)
    protected override bool IsLapsed(HttpResponseMessage response, Sent sent)
        => response.StatusCode == HttpStatusCode.Unauthorized || _session.ServedAnonymously(response)
            || (int)response.StatusCode is >= 300 and < 400 && IsLoginRedirect(response) || IsPage(response, sent);

    protected override VfsException? Lapsed(HttpResponseMessage response, string what, Sent sent)
        => IsPage(response, sent)
            ? NotData(response, what)
            : new VfsException(VfsError.Unavailable, $"{Name} ({Address.Base.Host}) no longer takes {_session.Who}. Retry the source to sign in again.");

    /// <summary>Whether a call for data was answered with a page instead.</summary>
    private static bool IsPage(HttpResponseMessage response, Sent sent)
        => sent.Hint != Download && response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent && !IsJson(response);

    /// <summary>Whether a redirect goes to the login page or to single sign-on: a session that lapsed, asked for a page rather than data.</summary>
    private bool IsLoginRedirect(HttpResponseMessage response)
    {
        if (response.Headers.Location is not { } location)
        {
            return false;
        }

        var to = location.IsAbsoluteUri ? location : new Uri(Address.Base, location);
        var path = to.AbsolutePath;
        return !to.Host.Equals(Address.Base.Host, StringComparison.OrdinalIgnoreCase)
            || path.Contains("login", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/plugins/servlet/samlsso", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/plugins/servlet/external-login", StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task<VfsException> ErrorAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var body = await BodyAsync(response, ct).ConfigureAwait(false);

        var message = Message(body);
        var status = (int)response.StatusCode;
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => new VfsException(VfsError.NotFound, $"{Name} has no {what} for this account ({message ?? "404 Not Found"})"),
            HttpStatusCode.Forbidden => new VfsException(VfsError.AccessDenied, $"{Name} refused {what}: {message ?? "403 Forbidden"}"),
            HttpStatusCode.Unauthorized => new VfsException(VfsError.Unavailable, $"{Name} ({Address.Base.Host}) no longer takes {_session.Who}: {message ?? "401 Unauthorized"}. Retry the source to sign in again."),
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => new VfsException(VfsError.Unavailable, $"{Name} ({Address.Base.Host}) is busy ({status}){(message is null ? "" : ": " + message)}"),

            // the server's verdict on what was asked (a query it cannot run), told apart by what it carries (IsRejected)
            HttpStatusCode.BadRequest => new VfsException(VfsError.IoError, $"{Name} answered {status} to {what}{(message is null ? "" : ": " + message)}", new HttpRequestException(message, null, HttpStatusCode.BadRequest)),
            _ => new VfsException(VfsError.IoError, $"{Name} answered {status} to {what}{(message is null ? "" : ": " + message)}"),
        };
    }

    /// <summary>
    /// Whether a failure is the server's refusal of the request itself (400: a query it cannot run), which asking again does
    /// not change, rather than trouble answering it.
    /// </summary>
    public static bool IsRejected(VfsException ex) => ex.InnerException is HttpRequestException { StatusCode: HttpStatusCode.BadRequest };

    /// <summary>
    /// The product's own words for an error: Jira's <c>errorMessages</c> and <c>errors</c>, Confluence's and Bamboo's
    /// <c>message</c>, Bitbucket's <c>errors</c> list with a <c>message</c> each, else a short start of the body.
    /// </summary>
    internal static string? Message(string body)
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
                var parts = root.Arr("errorMessages").Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()!).ToList();
                if (root.Obj("errors") is { ValueKind: JsonValueKind.Object } errors)
                {
                    parts.AddRange(errors.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => $"{p.Name}: {p.Value.GetString()}"));
                }

                parts.AddRange(root.Arr("errors").Select(e => e.ValueKind == JsonValueKind.Object ? e.Str("message") : e.ValueKind == JsonValueKind.String ? e.GetString() : null).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!));
                if (parts.Count > 0)
                {
                    return string.Join("; ", parts);
                }

                return root.Str("message") ?? root.Obj("data").Str("message") ?? root.Str("reason");
            }
        }
        catch (JsonException)
        {
        }

        var text = body.Trim();
        return text.StartsWith('<') ? null : text.Length <= 200 ? text : text[..200];
    }
}
