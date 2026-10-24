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
/// joined). Requests to SharePoint Online go at its <see cref="RequestPace"/>; throttling (429, 503) is retried with
/// Retry-After or a short backoff; SharePoint's error codes become <see cref="VfsException"/>s.
/// </summary>
internal sealed partial class SharePointRestClient : IDisposable
{
    public const int MaxAttempts = 6;

    /// <summary>The attempts a probe makes at a request (<see cref="Probe"/>).</summary>
    public const int ProbeAttempts = 2;

    /// <summary>
    /// Files above this go up in pieces (StartUpload, ContinueUpload, FinishUpload); below, in one request. A piece's size:
    /// StartUpload's piece is never the last (only FinishUpload makes the file), so a file sent in pieces needs two at
    /// least, and one of 8 to 10 MiB was left empty while this was 8 MiB.
    /// </summary>
    public const long SimpleUploadLimit = UploadChunkSize;

    public const int UploadChunkSize = 10 * 1024 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);
    private static readonly MediaTypeWithQualityHeaderValue JsonNoMetadata = MediaTypeWithQualityHeaderValue.Parse("application/json;odata=nometadata");

    private readonly HttpClient _http;
    private readonly bool _ownsHttp = true;
    private readonly ILogger _logger;
    private readonly Action<string>? _onTrouble;
    private readonly Action? _onSuccess;
    private readonly bool _probe;
    private readonly RequestPace? _pace;
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
        _pace = RequestPace.ForHost(SiteUrl.Host);
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

    private SharePointRestClient(SharePointRestClient parent, Uri siteUrl, bool probe = false)
    {
        SiteUrl = new Uri(siteUrl.AbsoluteUri.TrimEnd('/') + "/");
        _http = parent._http;
        _ownsHttp = false;
        _logger = parent._logger;
        _pace = RequestPace.ForHost(SiteUrl.Host);
        _probe = probe || parent._probe;
        _onTrouble = _probe ? null : parent._onTrouble;
        _onSuccess = _probe ? null : parent._onSuccess;
    }

    /// <summary>The site's URL with a trailing slash; <c>_api/...</c> is relative to it.</summary>
    public Uri SiteUrl { get; }

    /// <summary>
    /// A client for looking sites up, as are those it makes for other sites: <see cref="ProbeAttempts"/> attempts at a
    /// request, and nothing said of the source's health. For the sites a source of several is given, where a server that is
    /// down should leave its site out, not keep the source waiting or show it all as degraded. It shares this client's
    /// connections and sign-in; dispose it before this one.
    /// </summary>
    public SharePointRestClient Probe() => new(this, SiteUrl, probe: true);

    /// <summary>
    /// A client for another site on the same server (a subsite), by its server-relative URL: SharePoint answers for a
    /// file only through its own site's <c>_api</c>. It shares this client's connections and sign-in and keeps its own
    /// request digest; dispose it before this one.
    /// </summary>
    public SharePointRestClient ForSite(string serverRelativeUrl) => new(this, new Uri(SiteUrl, SiteAddress.EscapePath(serverRelativeUrl)));

    /// <summary>The same for a site by its address, which may be on another server (a Windows sign-in goes to any).</summary>
    public SharePointRestClient ForSite(Uri siteUrl) => new(this, siteUrl);

    /// <summary>A site's address from its server-relative URL, on this client's server: the key a subsite or another site goes by.</summary>
    public string AddressOf(string serverRelativeUrl) => new Uri(SiteUrl, SiteAddress.EscapePath(serverRelativeUrl.TrimEnd('/'))).AbsoluteUri.TrimEnd('/');

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
            throw MapError(response.StatusCode, body, pathForErrors, Challenge(response));
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
        var challenge = Challenge(response);
        response.Dispose();
        throw MapError(status, body, pathForErrors, challenge);
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

    /// <summary>
    /// What a refusal asks for, for its message: on a 401 the schemes the server offers (Negotiate, NTLM, Bearer), or a
    /// sign-in in a browser where SharePoint says forms or a federated sign-in is required; on a redirect, where it
    /// sends the request (a sign-in page). Null for anything else.
    /// </summary>
    public static string? Challenge(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            if (response.Headers.Contains("X-Forms_Based_Auth_Required"))
            {
                return "a sign-in in a browser (forms, or a federated sign-in such as ADFS)";
            }

            var schemes = response.Headers.WwwAuthenticate.Select(h => h.Scheme).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return schemes.Count == 0 ? "no sign-in it names" : string.Join(", ", schemes);
        }

        return (int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location
            ? (location.IsAbsoluteUri ? location.GetLeftPart(UriPartial.Path) : location.OriginalString)
            : null;
    }

    /// <summary>
    /// A page of the site as a browser gets it: its HTML as SharePoint renders it, web parts and all. Null when SharePoint
    /// sends the browser to another address instead (a modern site's default.aspx forwards to its home page in Site Pages).
    /// </summary>
    public async Task<string?> GetPageAsync(Uri page, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, page);
            request.Headers.Accept.ParseAdd("text/html");
            return request;
        }, HttpCompletionOption.ResponseContentRead, idempotent: true, ct).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapError(response.StatusCode, body, page.AbsoluteUri, Challenge(response));
        }

        return body;
    }

    /// <summary>Whether SharePoint refused the account itself (signed in, but without access), not the sign-in: see <see cref="MapError"/>.</summary>
    public static bool IsDenial(VfsException ex) => ex.Error == VfsError.AccessDenied && ex.InnerException is UnauthorizedAccessException;

    public static VfsException MapError(HttpStatusCode status, string body, string path, string? challenge = null)
    {
        var (code, message) = ErrorDetails(body);
        var hresult = code.Split(',')[0].Trim();
        var text = message.Length > 0 ? message : code;
        if ((int)status is >= 300 and < 400 && challenge is not null)
        {
            // an unauthenticated request bounced to a sign-in page (a federated sign-in, forms): Windows credentials do not reach it
            return new VfsException(VfsError.AccessDenied, $"'{path}': SharePoint sent the request to {challenge} ({(int)status}) instead of answering: it wants a sign-in there");
        }

        if (code.Contains("InvalidClientQueryException", StringComparison.Ordinal))
        {
            // a request this server does not understand (a parameter a newer SharePoint has): ours to fix, not a missing file
            return new VfsException(VfsError.IoError, $"SharePoint rejected the request for '{path}': {text}");
        }

        if (hresult == "-2147024891" || code.Contains("UnauthorizedAccessException", StringComparison.Ordinal))
        {
            // SharePoint's own refusal (E_ACCESSDENIED; a 401 for a Windows sign-in, which a refused sign-in gives with no
            // body at all): it ran the request as the account, which may not do this
            return new VfsException(VfsError.AccessDenied, $"access to '{path}' was denied{(text.Length > 0 ? ": " + text : "")}", new UnauthorizedAccessException(text));
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
            HttpStatusCode.Unauthorized => new VfsException(VfsError.AccessDenied, $"'{path}': SharePoint refused the sign-in (401{(challenge is null ? "" : $"; it asks for {challenge}")}){(text.Length > 0 ? ": " + text : "")}"),
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
            throw MapError(response.StatusCode, body, pathForErrors, Challenge(response));
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
        var attempts = _probe ? ProbeAttempts : MaxAttempts;
        var retrying = _probe ? LogLevel.Debug : LogLevel.Warning;
        for (var attempt = 1; ; attempt++)
        {
            if (_pace is not null)
            {
                await _pace.WaitAsync(ct).ConfigureAwait(false);
            }

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
                if (attempt < attempts && idempotent)
                {
                    var delay = Backoff(attempt);
                    _logger.Log(retrying, "sharepoint: {Method} {Url} failed ({Error}); retrying in {Delay}s", request.Method, request.RequestUri?.AbsolutePath, ex.Message, delay.TotalSeconds);
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

            if (_pace?.Hold(response) is { } hold)
            {
                _logger.LogInformation("sharepoint: {Host} is throttling ({Status}); no requests to it for {Seconds:F0} s", SiteUrl.Host, (int)status, hold.TotalSeconds);
            }

            if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout && attempt < attempts)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : null);
                var delay = retryAfter is { } ra && ra > TimeSpan.Zero ? ra : Backoff(attempt);
                if (delay > TimeSpan.FromMinutes(2))
                {
                    delay = TimeSpan.FromMinutes(2);
                }

                _logger.Log(retrying, "sharepoint: {Method} {Url} answered {Status}; retrying in {Delay}s", request.Method, request.RequestUri?.AbsolutePath, (int)status, delay.TotalSeconds);
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
