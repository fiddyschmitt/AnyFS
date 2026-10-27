using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.GitLab;

/// <summary>
/// GitLab's REST API (v4) and GraphQL, as the source asks them (spec 7.8): every request at the server's pace
/// (<see cref="RequestPace"/>, by host) with at most so many open at once; a throttled answer (429, 503 with
/// Retry-After) held and tried again; a server error or a dropped connection tried again twice; a refusal of the sign-in
/// (401) renewed once through the session and tried again; pages followed by their Link header, offset and keyset alike;
/// errors as <see cref="VfsException"/>s by status. GraphQL with a browser session carries the session's CSRF token, as
/// GitLab's own pages send it.
/// </summary>
internal sealed class GitLabApi : IDisposable
{
    private const int Attempts = 3;
    private const int ThrottledAttempts = 6;

    private readonly GitLabSession _session;
    private readonly RequestPace _pace;
    private readonly SemaphoreSlim _gate;
    private readonly ILogger _logger;

    public GitLabApi(GitLabSession session, int concurrency, ILogger logger)
    {
        _session = session;
        _pace = PaceFor(session.Address.Server.Host);
        _gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 64));
        _logger = logger;
    }

    /// <summary>
    /// A server's pace: GitLab.com allows an account 2,000 API requests a minute; a server of one's own sets its own
    /// limits (off by default), so it goes faster, still within what a crawl should ask of a shared server.
    /// </summary>
    public static RequestPace PaceFor(string host)
        => host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".gitlab.com", StringComparison.OrdinalIgnoreCase)
            ? RequestPace.For("gitlab:" + host, burst: 100, perSecond: 25)
            : RequestPace.For("gitlab:" + host, burst: 200, perSecond: 50);

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
            var request = new HttpRequestMessage(HttpMethod.Post, Address.GraphQl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Options.Set(GitLabSession.GraphQlRequest, true);
            return request;
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

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            if (_pace.Hold(response) is { } hold)
            {
                _logger.LogInformation("gitlab: {Host} asks AnyFS to wait {Seconds:N0} s ({Status})", Address.Server.Host, hold.TotalSeconds, (int)response.StatusCode);
            }

            var status = response.StatusCode;
            var retryAfter = response.Headers.RetryAfter is not null;
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

    public void Dispose() => _gate.Dispose();
}
