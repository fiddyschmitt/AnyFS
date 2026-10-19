using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// SharePoint's own REST API (<c>/_api</c>), the one surface SharePoint Server and SharePoint Online share:
/// files and folders by server-relative path, JSON with <c>odata=nometadata</c>, a request digest for
/// writes, Windows authentication from the handler's credentials (NTLM, or Kerberos where the machine is
/// joined). Throttling (429, 503) is retried with Retry-After or a short backoff; SharePoint's error codes
/// become <see cref="VfsException"/>s.
/// </summary>
internal sealed partial class SharePointRestClient : IDisposable
{
    public const int MaxAttempts = 6;

    /// <summary>Files above this go up in pieces (StartUpload, ContinueUpload, FinishUpload); below, in one request.</summary>
    public const long SimpleUploadLimit = 8 * 1024 * 1024;

    public const int UploadChunkSize = 10 * 1024 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly MediaTypeWithQualityHeaderValue JsonNoMetadata = MediaTypeWithQualityHeaderValue.Parse("application/json;odata=nometadata");

    private readonly HttpClient _http;
    private readonly bool _ownsHttp = true;
    private readonly ILogger _logger;
    private readonly Action<string>? _onTrouble;
    private readonly Action? _onSuccess;
    private readonly SemaphoreSlim _digestGate = new(1, 1);
    private string? _digest;
    private DateTimeOffset _digestExpires;

    /// <param name="siteUrl">The site (web) the calls are about, without a trailing slash.</param>
    /// <param name="credentials">Windows credentials, or null for the process's own logon (Windows only, in practice).</param>
    public SharePointRestClient(Uri siteUrl, ICredentials? credentials, bool trustAnyCertificate, ILogger logger, Action<string>? onTrouble = null, Action? onSuccess = null, HttpMessageHandler? handler = null)
    {
        SiteUrl = new Uri(siteUrl.AbsoluteUri.TrimEnd('/') + "/");
        _logger = logger;
        _onTrouble = onTrouble;
        _onSuccess = onSuccess;
        if (handler is null)
        {
            var sockets = new SocketsHttpHandler
            {
                Credentials = credentials ?? CredentialCache.DefaultNetworkCredentials,
                PreAuthenticate = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = DecompressionMethods.All,
                MaxConnectionsPerServer = 32,
                AllowAutoRedirect = false,
            };
            if (trustAnyCertificate)
            {
                sockets.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            handler = sockets;
        }

        _http = new HttpClient(handler, disposeHandler: true) { Timeout = RequestTimeout };
        _http.DefaultRequestHeaders.Accept.Add(JsonNoMetadata);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"AnyFS/{typeof(SharePointRestClient).Assembly.GetName().Version?.ToString(3) ?? "0"}");
    }

    private SharePointRestClient(SharePointRestClient parent, Uri siteUrl)
    {
        SiteUrl = new Uri(siteUrl.AbsoluteUri.TrimEnd('/') + "/");
        _http = parent._http;
        _ownsHttp = false;
        _logger = parent._logger;
        _onTrouble = parent._onTrouble;
        _onSuccess = parent._onSuccess;
    }

    /// <summary>The site's URL with a trailing slash; <c>_api/...</c> is relative to it.</summary>
    public Uri SiteUrl { get; }

    /// <summary>
    /// A client for another site on the same server (a subsite), by its server-relative URL: SharePoint answers for a
    /// file only through its own site's <c>_api</c>. It shares this client's connections and sign-in and keeps its own
    /// request digest; dispose it before this one.
    /// </summary>
    public SharePointRestClient ForSite(string serverRelativeUrl) => new(this, new Uri(SiteUrl, SiteAddress.EscapePath(serverRelativeUrl)));

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }

        _digestGate.Dispose();
    }

    /// <summary>A path for a <c>decodedurl='...'</c> parameter: quotes doubled, then percent-encoded.</summary>
    public static string Encode(string decodedUrl) => Uri.EscapeDataString(decodedUrl.Replace("'", "''"));

    public static string FolderApi(string serverRelativeUrl) => $"web/GetFolderByServerRelativePath(decodedurl='{Encode(serverRelativeUrl)}')";

    public static string FileApi(string serverRelativeUrl) => $"web/GetFileByServerRelativePath(decodedurl='{Encode(serverRelativeUrl)}')";

    // --- JSON ----------------------------------------------------------------------------------------------

    public async Task<JsonDocument> GetJsonAsync(string api, string pathForErrors, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Api(api)), HttpCompletionOption.ResponseContentRead, idempotent: true, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, pathForErrors, ct).ConfigureAwait(false);
    }

    /// <summary>Like <see cref="GetJsonAsync"/>, null when the answer maps to NotFound.</summary>
    public async Task<JsonDocument?> TryGetJsonAsync(string api, string pathForErrors, CancellationToken ct)
    {
        try
        {
            return await GetJsonAsync(api, pathForErrors, ct).ConfigureAwait(false);
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            return null;
        }
    }

    /// <summary>POST with an optional JSON body and the request digest; the answer as JSON (empty for no content).</summary>
    public async Task<JsonDocument> PostJsonAsync(string api, string? json, string pathForErrors, CancellationToken ct, params (string Name, string Value)[] headers)
    {
        var digest = await DigestAsync(ct).ConfigureAwait(false);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Api(api));
            request.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json;odata=nometadata");
            }

            return request;
        }, HttpCompletionOption.ResponseContentRead, idempotent: false, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, pathForErrors, ct).ConfigureAwait(false);
    }

    /// <summary>POST a binary body (a file, or a piece of one) with the request digest.</summary>
    public async Task<JsonDocument> PostBinaryAsync(string api, Func<Stream> openContent, long length, string pathForErrors, CancellationToken ct)
    {
        var digest = await DigestAsync(ct).ConfigureAwait(false);
        using var response = await SendAsync(() =>
        {
            var content = new StreamContent(openContent());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = length;
            var request = new HttpRequestMessage(HttpMethod.Post, Api(api)) { Content = content };
            request.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
            return request;
        }, HttpCompletionOption.ResponseContentRead, idempotent: false, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, pathForErrors, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A CSOM request to <c>_vti_bin/client.svc/ProcessQuery</c> (the XML the client object model sends), for the
    /// one thing REST cannot do: set a list item's system fields with <c>UpdateOverwriteVersion</c>. The answer is a
    /// JSON array whose first element carries <c>ErrorInfo</c>; that is mapped like a REST error.
    /// </summary>
    public async Task PostCsomAsync(string xml, string pathForErrors, CancellationToken ct)
    {
        var digest = await DigestAsync(ct).ConfigureAwait(false);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(SiteUrl, "_vti_bin/client.svc/ProcessQuery"))
            {
                Content = new StringContent(xml, Encoding.UTF8, "text/xml"),
            };
            request.Headers.TryAddWithoutValidation("X-RequestDigest", digest);
            return request;
        }, HttpCompletionOption.ResponseContentRead, idempotent: false, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapError(response.StatusCode, body, pathForErrors);
        }

        JsonElement first;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                throw new VfsException(VfsError.IoError, $"'{pathForErrors}': SharePoint's client.svc answered with something other than a result array");
            }

            first = doc.RootElement[0].Clone();
        }
        catch (JsonException ex)
        {
            throw new VfsException(VfsError.IoError, $"'{pathForErrors}': SharePoint's client.svc answered with something other than JSON: {ex.Message}");
        }

        if (first.TryGetProperty("ErrorInfo", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var message = error.TryGetProperty("ErrorMessage", out var m) ? m.GetString() ?? "" : "";
            var type = error.TryGetProperty("ErrorTypeName", out var tn) ? tn.GetString() ?? "" : "";
            var code = error.TryGetProperty("ErrorCode", out var c) ? c.ToString() : "";
            // the same shape MapError reads from a REST body
            var fake = "{\"odata.error\":{\"code\":" + Json.Quote(code + ", " + type) + ",\"message\":{\"value\":" + Json.Quote(message) + "}}}";
            throw MapError(HttpStatusCode.InternalServerError, fake, pathForErrors);
        }
    }

    /// <summary>Deletes a file or folder (POST with X-HTTP-Method DELETE and IF-MATCH *).</summary>
    public async Task DeleteAsync(string api, string pathForErrors, CancellationToken ct)
    {
        using var _ = await PostJsonAsync(api, null, pathForErrors, ct, ("X-HTTP-Method", "DELETE"), ("IF-MATCH", "*")).ConfigureAwait(false);
    }

    // --- content ---------------------------------------------------------------------------------------------

    /// <summary>
    /// GET a file by its own URL (not the API), from <paramref name="offset"/>: a Range request when it is not 0.
    /// <c>Translate: f</c> asks for the stored bytes rather than a rendered page. 416 past the end gives an empty body.
    /// </summary>
    public async Task<HttpResponseMessage> GetContentAsync(string serverRelativeUrl, long offset, string pathForErrors, CancellationToken ct)
    {
        var url = new Uri(SiteUrl, SiteAddress.EscapePath(serverRelativeUrl));
        var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            request.Headers.TryAddWithoutValidation("Translate", "f");
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
            }

            return request;
        }, HttpCompletionOption.ResponseHeadersRead, idempotent: true, ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return response;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = response.StatusCode;
        response.Dispose();
        throw MapError(status, body, pathForErrors);
    }

    // --- errors ----------------------------------------------------------------------------------------------

    /// <summary>SharePoint's error code and message from an <c>odata.error</c> body ("-2147024894, System.IO.FileNotFoundException", "File Not Found.").</summary>
    public static (string Code, string Message) ErrorDetails(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return ("", "");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("odata.error", out var error) || doc.RootElement.TryGetProperty("error", out error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                var message = "";
                if (error.TryGetProperty("message", out var m))
                {
                    message = m.ValueKind == JsonValueKind.Object && m.TryGetProperty("value", out var v) ? v.GetString() ?? "" : m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                }

                return (code, message);
            }
        }
        catch (JsonException)
        {
        }

        return ("", HtmlTitle().Match(body) is { Success: true } title ? title.Groups[1].Value.Trim() : "");
    }

    public static VfsException MapError(HttpStatusCode status, string body, string path)
    {
        var (code, message) = ErrorDetails(body);
        var hresult = code.Split(',')[0].Trim();
        var text = message.Length > 0 ? message : code;
        if (code.Contains("InvalidClientQueryException", StringComparison.Ordinal))
        {
            // a request this server does not understand (a parameter a newer SharePoint has): ours to fix, not a missing file
            return new VfsException(VfsError.IoError, $"SharePoint rejected the request for '{path}': {text}");
        }

        if (hresult is "-2147024894" or "-2147024893" or "-2130575338" or "-2146232832" || code.Contains("FileNotFoundException", StringComparison.Ordinal) || code.Contains("DirectoryNotFoundException", StringComparison.Ordinal) || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) || message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return VfsException.NotFound(path);
        }

        if (hresult is "-2130575257" or "-2147024713" || message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return VfsException.AlreadyExists(path);
        }

        if (hresult is "-2130575245" or "-2147018894" || message.Contains("checked out", StringComparison.OrdinalIgnoreCase) || message.Contains("locked", StringComparison.OrdinalIgnoreCase))
        {
            return new VfsException(VfsError.AccessDenied, $"'{path}' is locked: {text}");
        }

        if (hresult is "-2130575251" or "-2130575253" or "-2147024809" || message.Contains("invalid", StringComparison.OrdinalIgnoreCase) && (message.Contains("name", StringComparison.OrdinalIgnoreCase) || message.Contains("character", StringComparison.OrdinalIgnoreCase)))
        {
            return VfsException.InvalidPath(path, text);
        }

        return status switch
        {
            HttpStatusCode.NotFound => VfsException.NotFound(path),
            HttpStatusCode.Unauthorized => new VfsException(VfsError.AccessDenied, $"'{path}': SharePoint refused the sign-in (401){(text.Length > 0 ? ": " + text : "")}"),
            HttpStatusCode.Forbidden => new VfsException(VfsError.AccessDenied, $"access to '{path}' was denied{(text.Length > 0 ? ": " + text : "")}"),
            HttpStatusCode.Locked => new VfsException(VfsError.AccessDenied, $"'{path}' is locked{(text.Length > 0 ? ": " + text : "")}"),
            HttpStatusCode.Conflict => VfsException.AlreadyExists(path),
            HttpStatusCode.BadRequest => VfsException.InvalidPath(path, text.Length > 0 ? text : "SharePoint refused the request"),
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => VfsException.Unavailable($"'{path}': SharePoint is throttling or unavailable (HTTP {(int)status})"),
            _ when (int)status >= 500 && code.Length == 0 => VfsException.Unavailable($"'{path}': SharePoint answered {(int)status}{(text.Length > 0 ? " " + text : "")}"),
            _ => new VfsException(VfsError.IoError, $"'{path}': HTTP {(int)status} {code} {message}".TrimEnd()),
        };
    }

    // --- the one send path -----------------------------------------------------------------------------------

    private Uri Api(string api) => new(SiteUrl, "_api/" + api);

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string pathForErrors, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapError(response.StatusCode, body, pathForErrors);
        }

        return body.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(body);
    }

    /// <summary>The request digest writes need, fetched from <c>contextinfo</c> and kept until it is about to expire.</summary>
    private async Task<string> DigestAsync(CancellationToken ct)
    {
        if (_digest is { } fresh && DateTimeOffset.UtcNow < _digestExpires)
        {
            return fresh;
        }

        await _digestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_digest is { } again && DateTimeOffset.UtcNow < _digestExpires)
            {
                return again;
            }

            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Api("contextinfo")), HttpCompletionOption.ResponseContentRead, idempotent: true, ct).ConfigureAwait(false);
            using var doc = await ReadJsonAsync(response, SiteUrl.ToString(), ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var value = root.TryGetProperty("FormDigestValue", out var fdv) ? fdv.GetString() : null;
            var seconds = root.TryGetProperty("FormDigestTimeoutSeconds", out var fts) && fts.TryGetInt32(out var s) ? s : 1800;
            _digest = value ?? throw new VfsException(VfsError.IoError, $"{SiteUrl}: contextinfo returned no request digest");
            _digestExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, seconds - 120));
            return _digest;
        }
        finally
        {
            _digestGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, HttpCompletionOption completion, bool idempotent, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var request = make();
            HttpResponseMessage response;
            var started = Stopwatch.GetTimestamp();
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
                throw VfsException.Unavailable($"{SiteUrl.Host} could not be reached: {ex.Message}");
            }

            var status = response.StatusCode;
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("sharepoint: {Method} {Url} -> {Status} in {Ms} ms", request.Method, request.RequestUri?.PathAndQuery, (int)status, Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture));
            }

            if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout && attempt < MaxAttempts)
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

    [GeneratedRegex("<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlTitle();
}
