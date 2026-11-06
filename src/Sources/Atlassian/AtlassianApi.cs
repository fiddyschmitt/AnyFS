using System.Net;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Atlassian;

/// <summary>
/// Jira's or Confluence's REST API, as the sources ask it (spec 7.10): every request at the server's pace
/// (<see cref="RequestPace"/>, by host) with at most so many open at once; a throttled answer (429, 503 with Retry-After)
/// held and tried again; a server error or a dropped connection tried again twice; a refusal of the sign-in (401, an
/// answer served to the anonymous user after the session lapsed, or a login page in place of data) renewed once through
/// the session and tried again; errors as <see cref="VfsException"/>s by status.
/// </summary>
internal sealed class AtlassianApi : IDisposable
{
    private const int Attempts = 3;
    private const int ThrottledAttempts = 6;

    private readonly AtlassianSession _session;
    private readonly RequestPace _pace;
    private readonly SemaphoreSlim _gate;
    private readonly ILogger _logger;

    public AtlassianApi(AtlassianSession session, int concurrency, ILogger logger)
    {
        _session = session;

        // no pace of AnyFS's own (until 5-Nov-2026 a server was asked at most 25 a second whatever the settings said): a server
        // that asks AnyFS to wait (429, 503 with Retry-After) is waited for, by every request of it together
        _pace = RequestPace.Unpaced($"{session.Product}:{session.Address.Base.Authority}");
        _gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 64));
        _logger = logger;
    }

    public AtlassianAddress Address => _session.Address;

    public AtlassianSession Session => _session;

    private string Name => _session.ProductName;

    private string LogName => AtlassianSignInTarget.Name(_session.Product);

    /// <summary>An address of the product's REST API (<c>search?jql=…</c> under Jira's <c>rest/api/2/</c>).</summary>
    public Uri Rest(string relative) => new(Address.Api(_session.Product), relative);

    /// <summary>One REST call's JSON (<paramref name="relative"/> to the product's REST API).</summary>
    public Task<JsonDocument> GetAsync(string relative, CancellationToken ct) => GetAsync(Rest(relative), relative, ct);

    /// <summary>One call's JSON, at any address of the installation (another REST API's: Jira Software's <c>rest/agile/1.0</c>).</summary>
    public async Task<JsonDocument> GetAsync(Uri url, string what, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), what, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, what, ct).ConfigureAwait(false);
    }

    /// <summary>One REST call's JSON, or null where the server answers 404 (a thing that is not there, or not to this account).</summary>
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
        return await ReadJsonAsync(response, relative, ct).ConfigureAwait(false);
    }

    /// <summary>A download (an attachment): the response with its body unread, for the caller to stream and dispose.</summary>
    public Task<HttpResponseMessage> OpenAsync(Uri url, string what, CancellationToken ct)
        => SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), what, ct, HttpCompletionOption.ResponseHeadersRead, json: false);

    /// <summary>A download's headers alone (its size, its kind), by HEAD; the caller disposes the response.</summary>
    public Task<HttpResponseMessage> HeadAsync(Uri url, string what, CancellationToken ct)
        => SendAsync(() => new HttpRequestMessage(HttpMethod.Head, url), what, ct, HttpCompletionOption.ResponseHeadersRead, json: false);

    /// <summary>
    /// Sends a request (made afresh for each attempt) at the server's pace and returns a successful response; refusals and
    /// failures become <see cref="VfsException"/>s, after the retries the class describes. A call for data
    /// (<paramref name="json"/>) answered with a page instead is a lapsed sign-in too.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, string what, CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, bool json = true)
    {
        var renewed = false;
        for (var attempt = 1; ; attempt++)
        {
            await _pace.WaitAsync(ct).ConfigureAwait(false);
            var generation = _session.Generation;
            HttpResponseMessage response;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var request = make();
                await _session.AuthorizeAsync(request, ct).ConfigureAwait(false);
                response = await _session.Http.SendAsync(request, completion, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested) && attempt < Attempts)
            {
                _logger.LogInformation("{Product}: {What} did not answer ({Error}); trying again", LogName, what, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new VfsException(VfsError.Unavailable, $"{Name} ({Address.Base.Host}) did not answer: {ex.Message}", ex);
            }
            finally
            {
                _gate.Release();
            }

            // a lapsed session is served as the anonymous user where the site lets anyone read (less, not a refusal), or
            // as a login page in place of data where a sign-in proxy answers for the server (at 200, not a redirect)
            var page = json && response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent && !IsJson(response);
            var lapsed = response.StatusCode == HttpStatusCode.Unauthorized || _session.ServedAnonymously(response)
                || (int)response.StatusCode is >= 300 and < 400 && IsLoginRedirect(response) || page;
            if (lapsed && !renewed && _session.CanRenew)
            {
                response.Dispose();
                renewed = true;
                await _session.RenewAsync(generation, ct).ConfigureAwait(false);
                continue;
            }

            if (response.IsSuccessStatusCode && !lapsed)
            {
                return response;
            }

            if (_pace.Hold(response) is { } hold)
            {
                _logger.LogInformation("{Product}: {Host} asks AnyFS to wait {Seconds:N0} s ({Status})", LogName, Address.Base.Host, hold.TotalSeconds, (int)response.StatusCode);
            }

            var status = response.StatusCode;
            var retryAfter = response.Headers.RetryAfter is not null;
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
                throw page
                    ? new VfsException(VfsError.Unavailable, $"{Name} ({Address.Base.Host}) answered {what} with {ContentType(response)}, not data: the sign-in may have lapsed. Retry the source.")
                    : lapsed
                        ? new VfsException(VfsError.Unavailable, $"{Name} ({Address.Base.Host}) no longer takes {_session.Who}. Retry the source to sign in again.")
                        : await ErrorAsync(response, what, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsJson(HttpResponseMessage response) => response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

    private static string ContentType(HttpResponseMessage response) => response.Content.Headers.ContentType?.MediaType is { Length: > 0 } type ? type : "no data";

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
            HttpStatusCode.NotFound => new VfsException(VfsError.NotFound, $"{Name} has no {what} for this account ({message ?? "404 Not Found"})"),
            HttpStatusCode.Forbidden => new VfsException(VfsError.AccessDenied, $"{Name} refused {what}: {message ?? "403 Forbidden"}"),
            HttpStatusCode.Unauthorized => new VfsException(VfsError.Unavailable, $"{Name} ({Address.Base.Host}) no longer takes {_session.Who}: {message ?? "401 Unauthorized"}. Retry the source to sign in again."),
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => new VfsException(VfsError.Unavailable, $"{Name} ({Address.Base.Host}) is busy ({status}){(message is null ? "" : ": " + message)}"),
            _ => new VfsException(VfsError.IoError, $"{Name} answered {status} to {what}{(message is null ? "" : ": " + message)}"),
        };
    }

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

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (!IsJson(response))
        {
            // a page in place of data (SendAsync renews the sign-in for one; a 204 or the like gets here)
            throw new VfsException(VfsError.Unavailable, $"{Name} ({Address.Base.Host}) answered {what} with {ContentType(response)}, not data: the sign-in may have lapsed. Retry the source.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new VfsException(VfsError.IoError, $"{Name} answered {what} with JSON AnyFS cannot read: {ex.Message}", ex);
        }
    }

    public void Dispose() => _gate.Dispose();
}
