using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Microsoft365;

/// <summary>
/// The few Microsoft Graph calls a file system needs, over <see cref="HttpClient"/> and <see cref="JsonDocument"/>:
/// no SDK. Every request carries a bearer token from the sign-in; 429 and 5xx answers are retried with
/// Retry-After or a short backoff (Graph throttles per app and per tenant, and a fresh tenant 500s for a
/// while: spike 9); a 401 renews the token once. Errors become <see cref="VfsException"/>s by Graph's error
/// code. Pre-authenticated URLs (SharePoint's downloads and upload sessions) go out without a token.
/// </summary>
public sealed class GraphClient : IDisposable
{
    public const int MaxAttempts = 6;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, bool, Task<string>> _token;
    private readonly ILogger _logger;
    private readonly Action<string>? _onTrouble;
    private readonly Action? _onSuccess;
    private readonly RequestPace? _pace;

    /// <param name="token">(cancellation, forceRefresh) → a bearer token for Graph.</param>
    /// <param name="service">The service the calls are for, as the messages and log lines name it.</param>
    /// <param name="onTrouble">Called with a reason when the upstream misbehaves (throttling, outages), for the source's health.</param>
    /// <param name="onSuccess">Called after a request succeeded, so the source can turn healthy again.</param>
    /// <param name="cloud">Whose Graph to call (the token must be for the same cloud); null: the global cloud.</param>
    /// <param name="pace">The tenant's pace the requests go at (<see cref="RequestPace"/>); null for none.</param>
    public GraphClient(Func<CancellationToken, bool, Task<string>> token, ILogger logger, Microsoft365Service service, Action<string>? onTrouble = null, Action? onSuccess = null, HttpMessageHandler? handler = null, MicrosoftCloud? cloud = null, RequestPace? pace = null)
    {
        _token = token;
        _logger = logger;
        Service = service;
        _onTrouble = onTrouble;
        _onSuccess = onSuccess;
        _pace = pace;
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

    /// <summary>The service the calls are for, as the messages name it.</summary>
    public Microsoft365Service Service { get; }

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
            throw MapError(response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), pathForErrors, Service);
        }
    }

    // --- content: downloads --------------------------------------------------------------------------------

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
        throw MapError(status, body, pathForErrors, Service);
    }

    // --- errors --------------------------------------------------------------------------------------------

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
    public static VfsException MapError(HttpStatusCode status, string body, string path, Microsoft365Service service)
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
            ("quotaLimitReached", _) or (_, HttpStatusCode.InsufficientStorage) => new VfsException(VfsError.IoError, $"'{path}': {service.Storage} is out of storage ({message})"),
            ("activityLimitReached", _) or (_, HttpStatusCode.TooManyRequests) => VfsException.Unavailable($"'{path}': {service.Name} is throttling this app; try again shortly"),
            ("invalidRequest", _) or (_, HttpStatusCode.BadRequest) => VfsException.InvalidPath(path, message.Length > 0 ? message : $"{service.Name} refused the request"),
            _ when (int)status >= 500 => VfsException.Unavailable($"'{path}': {service.Name} answered {(int)status} {code} {message}".TrimEnd()),
            _ => new VfsException(VfsError.IoError, $"'{path}': HTTP {(int)status} {code} {message}".TrimEnd()),
        };
    }

    // --- the one send path ---------------------------------------------------------------------------------

    /// <summary>An address relative to the API root made absolute; an absolute one (a nextLink) as it is.</summary>
    public string Absolute(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : BaseUrl + url;

    /// <summary>A Graph answer as JSON; a failed one as <see cref="MapError(HttpStatusCode, string, string, Microsoft365Service)"/> has it.</summary>
    public async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string pathForErrors, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapError(response.StatusCode, body, pathForErrors, Service);
        }

        try
        {
            return JsonDocument.Parse(body.Length == 0 ? "{}" : body);
        }
        catch (JsonException)
        {
            // a proxy's page, a captive portal's: something answering in Graph's place
            throw VfsException.Unavailable($"'{pathForErrors}': Graph answered {(int)response.StatusCode} with something other than JSON ({response.Content.Headers.ContentType?.MediaType ?? "no content type"})");
        }
    }

    /// <summary>
    /// Sends, with the retries described on the class. <paramref name="make"/> builds a fresh request per attempt
    /// (a request can't be sent twice). Non-idempotent requests are retried only when the server says it did
    /// nothing (429, 503, 504).
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, HttpCompletionOption completion, bool authenticated, bool idempotent, CancellationToken ct)
    {
        var renewedToken = false;
        var refreshToken = false;
        for (var attempt = 1; ; attempt++)
        {
            if (_pace is not null)
            {
                await _pace.WaitAsync(ct).ConfigureAwait(false);
            }

            var request = make();
            if (authenticated)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct, refreshToken).ConfigureAwait(false));
                refreshToken = false;
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
                    _logger.LogWarning("{Prefix}: {Method} {Url} failed ({Error}); retrying in {Delay}s", Service.LogPrefix, request.Method, request.RequestUri?.AbsolutePath, ex.Message, delay.TotalSeconds);
                    _onTrouble?.Invoke(ex.Message);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                _onTrouble?.Invoke(ex.Message);
                throw VfsException.Unavailable($"{Service.Name} could not be reached: {ex.Message}");
            }

            var status = response.StatusCode;
            if (_pace?.Hold(response) is { } hold)
            {
                _logger.LogInformation("{Prefix}: {Service} is throttling ({Status}); no requests to it for {Seconds:F0} s", Service.LogPrefix, Service.Name, (int)status, hold.TotalSeconds);
            }

            if (status == HttpStatusCode.Unauthorized && authenticated && attempt < MaxAttempts && !renewedToken)
            {
                // once: a 401 to a token just renewed is the sign-in's answer, not a lapse
                response.Dispose();
                renewedToken = refreshToken = true;
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

                _logger.LogWarning("{Prefix}: {Method} {Url} answered {Status}; retrying in {Delay}s", Service.LogPrefix, request.Method, request.RequestUri?.AbsolutePath, (int)status, delay.TotalSeconds);
                _onTrouble?.Invoke($"{Service.Name} answered {(int)status}");
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
