using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.SharePoint;

/// <summary>A document library (a Graph drive) of the site.</summary>
internal sealed record DriveInfo(string Id, string Name, long? QuotaTotal, long? QuotaRemaining);

/// <summary>
/// The few Microsoft Graph calls a file system needs, over <see cref="HttpClient"/> and <see cref="JsonDocument"/>:
/// no SDK. Every request carries a bearer token from the sign-in; 429 and 5xx answers are retried with
/// Retry-After or a short backoff (Graph throttles per app and per tenant, and a fresh tenant 500s for a
/// while: spike 9); a 401 renews the token once. Errors become <see cref="VfsException"/>s by Graph's error
/// code. Pre-authenticated SharePoint URLs (downloads, upload sessions) go out without a token.
/// </summary>
internal sealed class GraphClient : IDisposable
{
    public const int MaxAttempts = 6;

    /// <summary>Graph's limit for a one-request upload; bigger files use an upload session.</summary>
    public const long SimpleUploadLimit = 4 * 1024 * 1024;

    /// <summary>An upload session's chunks must be multiples of 320 KiB; 10 MiB is Graph's own suggestion.</summary>
    public const int UploadChunkSize = 32 * 320 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, bool, Task<string>> _token;
    private readonly ILogger _logger;
    private readonly Action<string>? _onTrouble;
    private readonly Action? _onSuccess;

    /// <param name="token">(cancellation, forceRefresh) → a bearer token for Graph.</param>
    /// <param name="onTrouble">Called with a reason when the upstream misbehaves (throttling, outages), for the source's health.</param>
    /// <param name="onSuccess">Called after a request succeeded, so the source can turn healthy again.</param>
    /// <param name="cloud">Whose Graph to call (the token must be for the same cloud); null: the global cloud.</param>
    public GraphClient(Func<CancellationToken, bool, Task<string>> token, ILogger logger, Action<string>? onTrouble = null, Action? onSuccess = null, HttpMessageHandler? handler = null, MicrosoftCloud? cloud = null)
    {
        _token = token;
        _logger = logger;
        _onTrouble = onTrouble;
        _onSuccess = onSuccess;
        BaseUrl = (cloud ?? MicrosoftCloud.Global).GraphBaseUrl;
        handler ??= new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 32,
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"AnyFS/{typeof(GraphClient).Assembly.GetName().Version?.ToString(3) ?? "0"}");
    }

    /// <summary>Graph's v1.0 root that relative addresses are resolved against: https://graph.microsoft.com/v1.0/ in the global cloud.</summary>
    public string BaseUrl { get; }

    public void Dispose() => _http.Dispose();

    // --- JSON calls to Graph ------------------------------------------------------------------------------

    /// <summary>GET <paramref name="url"/> (relative to the API root, or absolute as a nextLink is) as JSON; errors map to <see cref="VfsException"/>.</summary>
    public async Task<JsonDocument> GetJsonAsync(string url, string pathForErrors, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Absolute(url)), HttpCompletionOption.ResponseContentRead, authenticated: true, idempotent: true, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, pathForErrors, ct).ConfigureAwait(false);
    }

    /// <summary>Like <see cref="GetJsonAsync"/>, but null for 404.</summary>
    public async Task<JsonDocument?> TryGetJsonAsync(string url, string pathForErrors, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Absolute(url)), HttpCompletionOption.ResponseContentRead, authenticated: true, idempotent: true, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadJsonAsync(response, pathForErrors, ct).ConfigureAwait(false);
    }

    /// <summary>POST or PATCH a JSON body; the answer as JSON (an empty document for 204).</summary>
    public async Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, string json, string pathForErrors, CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(method, Absolute(url)) { Content = new StringContent(json, Encoding.UTF8, "application/json") },
            HttpCompletionOption.ResponseContentRead, authenticated: true, idempotent: false, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, pathForErrors, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string url, string pathForErrors, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, Absolute(url)), HttpCompletionOption.ResponseContentRead, authenticated: true, idempotent: true, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapError(response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), pathForErrors);
        }
    }

    // --- content: downloads and uploads ---------------------------------------------------------------------

    /// <summary>
    /// GET a pre-authenticated download URL, from <paramref name="offset"/> (a Range request when it is not 0), with
    /// the headers read and the body left to stream. 416 (past the end) gives an empty stream.
    /// </summary>
    public async Task<HttpResponseMessage> GetContentAsync(string downloadUrl, long offset, string pathForErrors, CancellationToken ct)
    {
        var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
            }

            return request;
        }, HttpCompletionOption.ResponseHeadersRead, authenticated: false, idempotent: true, ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return response;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = response.StatusCode;
        response.Dispose();
        throw MapError(status, body, pathForErrors);
    }

    /// <summary>PUT a whole small file (at most <see cref="SimpleUploadLimit"/>) to an item address's <c>/content</c>; the created or updated item comes back.</summary>
    public async Task<JsonDocument> PutContentAsync(string itemAddress, Func<Stream> openContent, long length, string pathForErrors, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var content = new StreamContent(openContent());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = length;
            return new HttpRequestMessage(HttpMethod.Put, Absolute($"{itemAddress}/content?@microsoft.graph.conflictBehavior=replace")) { Content = content };
        }, HttpCompletionOption.ResponseContentRead, authenticated: true, idempotent: true, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, pathForErrors, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads a file of any size through an upload session: one request to create it, then
    /// <see cref="UploadChunkSize"/> pieces, each retried on its own. The item comes back with the last piece.
    /// </summary>
    public async Task<JsonDocument> UploadAsync(string itemAddress, Stream content, long length, string pathForErrors, CancellationToken ct)
    {
        var session = await SendJsonAsync(HttpMethod.Post, $"{itemAddress}/createUploadSession",
            """{"item":{"@microsoft.graph.conflictBehavior":"replace"}}""", pathForErrors, ct).ConfigureAwait(false);
        string uploadUrl;
        using (session)
        {
            uploadUrl = session.RootElement.GetProperty("uploadUrl").GetString() ?? throw new VfsException(VfsError.IoError, $"'{pathForErrors}': Graph returned no upload URL");
        }

        var buffer = new byte[UploadChunkSize];
        long offset = 0;
        try
        {
            while (true)
            {
                var want = (int)Math.Min(buffer.Length, length - offset);
                var read = 0;
                while (read < want)
                {
                    var n = await content.ReadAsync(buffer.AsMemory(read, want - read), ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }

                    read += n;
                }

                if (read < want)
                {
                    throw new VfsException(VfsError.IoError, $"'{pathForErrors}': the file shrank while it was being uploaded");
                }

                var from = offset;
                var chunk = read;
                using var response = await SendAsync(() =>
                {
                    var piece = new ByteArrayContent(buffer, 0, chunk);
                    piece.Headers.ContentRange = new ContentRangeHeaderValue(from, from + chunk - 1, length);
                    return new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = piece };
                }, HttpCompletionOption.ResponseContentRead, authenticated: false, idempotent: true, ct).ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw MapError(response.StatusCode, body, pathForErrors);
                }

                offset += chunk;
                if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
                {
                    return JsonDocument.Parse(body);
                }

                if (offset >= length)
                {
                    throw new VfsException(VfsError.IoError, $"'{pathForErrors}': the upload session ended without the item (HTTP {(int)response.StatusCode})");
                }
            }
        }
        catch (Exception) when (TryCancelSession(uploadUrl))
        {
            throw; // never reached: TryCancelSession returns false
        }
    }

    private bool TryCancelSession(string uploadUrl)
    {
        // best effort, fire and forget: an abandoned session expires by itself after a few days
        _ = _http.DeleteAsync(uploadUrl).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return false;
    }

    // --- parsing ---------------------------------------------------------------------------------------------

    /// <summary>A drive item as a listing entry plus the ids the source needs later.</summary>
    public static (VfsEntry Entry, ItemRef Item) ParseItem(JsonElement item)
    {
        var name = item.GetProperty("name").GetString() ?? "";
        var isFolder = item.TryGetProperty("folder", out _);
        var size = item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var s) ? s : 0;
        var id = item.GetProperty("id").GetString() ?? "";
        var cTag = item.TryGetProperty("cTag", out var ct) ? ct.GetString() : null;
        var eTag = item.TryGetProperty("eTag", out var et) ? et.GetString() : null;
        var downloadUrl = item.TryGetProperty("@microsoft.graph.downloadUrl", out var du) ? du.GetString() : null;
        DateTimeOffset? created = null, modified = null;
        if (item.TryGetProperty("fileSystemInfo", out var fsi))
        {
            created = ReadTime(fsi, "createdDateTime");
            modified = ReadTime(fsi, "lastModifiedDateTime");
        }

        created ??= ReadTime(item, "createdDateTime");
        modified ??= ReadTime(item, "lastModifiedDateTime");
        var entry = new VfsEntry(name, isFolder, isFolder ? 0 : size, created, modified, VfsAttributes.None, cTag ?? eTag);
        return (entry, new ItemRef(id, cTag, downloadUrl, size, isFolder));
    }

    public static DriveInfo ParseDrive(JsonElement drive)
    {
        long? total = null, remaining = null;
        if (drive.TryGetProperty("quota", out var quota))
        {
            if (quota.TryGetProperty("total", out var t) && t.TryGetInt64(out var tv))
            {
                total = tv;
            }

            if (quota.TryGetProperty("remaining", out var r) && r.TryGetInt64(out var rv))
            {
                remaining = rv;
            }
        }

        return new DriveInfo(drive.GetProperty("id").GetString() ?? "", drive.GetProperty("name").GetString() ?? "", total, remaining);
    }

    /// <summary>Graph's error code from an error body ("itemNotFound"), or "" when the body is not one.</summary>
    public static string ErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code) ? code.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return body.Length > 200 ? body[..200] : body;
        }
    }

    /// <summary>The <see cref="VfsException"/> for a failed Graph answer, by status and error code.</summary>
    public static VfsException MapError(HttpStatusCode status, string body, string path)
    {
        var code = ErrorCode(body);
        var message = ErrorMessage(body);
        // Graph's error code first (it is the precise one), the HTTP status when the body has none
        return (code, status) switch
        {
            ("itemNotFound" or "resourceNotFound", _) or (_, HttpStatusCode.NotFound) => VfsException.NotFound(path),
            ("nameAlreadyExists", _) or (_, HttpStatusCode.Conflict) => VfsException.AlreadyExists(path),
            ("resourceLocked", _) or (_, HttpStatusCode.Locked) => new VfsException(VfsError.AccessDenied, $"'{path}' is locked: {message}"),
            ("accessDenied" or "notAllowed", _) or (_, HttpStatusCode.Forbidden) => new VfsException(VfsError.AccessDenied, $"access to '{path}' was denied: {message}"),
            ("unauthenticated", _) or (_, HttpStatusCode.Unauthorized) => VfsException.Unavailable($"'{path}': the sign-in is no longer accepted; restart the source to sign in again"),
            ("notSupported", _) => VfsException.NotSupported(message.Length > 0 ? message : "that operation"),
            ("quotaLimitReached", _) or (_, HttpStatusCode.InsufficientStorage) => new VfsException(VfsError.IoError, $"'{path}': the site is out of storage ({message})"),
            ("activityLimitReached", _) or (_, HttpStatusCode.TooManyRequests) => VfsException.Unavailable($"'{path}': SharePoint is throttling this app; try again shortly"),
            ("invalidRequest", _) or (_, HttpStatusCode.BadRequest) => VfsException.InvalidPath(path, message.Length > 0 ? message : "SharePoint refused the request"),
            _ when (int)status >= 500 => VfsException.Unavailable($"'{path}': SharePoint answered {(int)status} {code} {message}".TrimEnd()),
            _ => new VfsException(VfsError.IoError, $"'{path}': HTTP {(int)status} {code} {message}".TrimEnd()),
        };
    }

    // --- the one send path ---------------------------------------------------------------------------------

    private string Absolute(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : BaseUrl + url;

    private static DateTimeOffset? ReadTime(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var time) ? time : null;

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string pathForErrors, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapError(response.StatusCode, body, pathForErrors);
        }

        return body.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(body);
    }

    /// <summary>
    /// Sends, with the retries described on the class. <paramref name="make"/> builds a fresh request per attempt
    /// (a request can't be sent twice). Non-idempotent requests are retried only when the server says it did
    /// nothing (429, 503, 504).
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, HttpCompletionOption completion, bool authenticated, bool idempotent, CancellationToken ct)
    {
        var renewedToken = false;
        for (var attempt = 1; ; attempt++)
        {
            var request = make();
            if (authenticated)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct, renewedToken).ConfigureAwait(false));
                renewedToken = false;
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, completion, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                request.Dispose();
                if (attempt < MaxAttempts && idempotent)
                {
                    var delay = Backoff(attempt);
                    _logger.LogWarning("sharepoint: {Method} {Url} failed ({Error}); retrying in {Delay}s", request.Method, request.RequestUri?.AbsolutePath, ex.Message, delay.TotalSeconds);
                    _onTrouble?.Invoke(ex.Message);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                _onTrouble?.Invoke(ex.Message);
                throw VfsException.Unavailable($"SharePoint could not be reached: {ex.Message}");
            }

            var status = response.StatusCode;
            if (status == HttpStatusCode.Unauthorized && authenticated && attempt < MaxAttempts && !renewedToken)
            {
                response.Dispose();
                renewedToken = true;
                continue;
            }

            var transient = status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                || idempotent && status is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway;
            if (transient && attempt < MaxAttempts)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : null);
                var delay = retryAfter is { } ra && ra > TimeSpan.Zero ? ra : Backoff(attempt);
                if (delay > TimeSpan.FromMinutes(2))
                {
                    delay = TimeSpan.FromMinutes(2);
                }

                _logger.LogWarning("sharepoint: {Method} {Url} answered {Status}; retrying in {Delay}s", request.Method, request.RequestUri?.AbsolutePath, (int)status, delay.TotalSeconds);
                _onTrouble?.Invoke($"SharePoint answered {(int)status}");
                response.Dispose();
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                _onSuccess?.Invoke();
            }

            return response;
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(32, 1 << attempt));
}
