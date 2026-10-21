using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// Sends the browser session's cookies on every request to SharePoint's REST API, and when the site refuses a
/// request because they have lapsed (401, 403, or a redirect to the sign-in host) asks <see cref="BrowserSession"/>
/// to renew them headless and tries the one request again (spec 7.3.2). A renewal that needs a person is left to the
/// source: the refusal then passes through, the source turns Degraded, and the next start signs in with a window.
/// Renewals are serialised, and one serves every request that was in flight when the cookies went stale.
/// SharePoint Online refuses a signed-in account a site or file it may not open exactly as it refuses a lapsed session
/// (403 with X-Forms_Based_Auth_Required), so a refusal renews only when nothing shows the session is good: SharePoint
/// sets a new session cookie on its answers to a signed-in request, refusals included, and a request sent after the
/// refused one and answered shows it too. Nor does a refusal renew within <see cref="DefaultMinRenewalInterval"/> of the
/// last renewal: a browser launch so soon after one would not bring a better sign-in.
/// </summary>
internal sealed class BrowserSessionHandler : DelegatingHandler
{
    /// <summary>The least time between two renewals.</summary>
    public static readonly TimeSpan DefaultMinRenewalInterval = TimeSpan.FromMinutes(2);

    private const string SessionCookie = "FedAuth";

    private readonly CookieContainer _cookies;
    private readonly Uri _site;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SessionCookie>>> _renew;
    private readonly ILogger _logger;
    private readonly TimeSpan _minRenewalInterval;
    private readonly SemaphoreSlim _renewGate = new(1, 1);
    private int _generation;

    // Stopwatch times: when the latest request SharePoint answered as signed in was sent; when the last renewal was tried
    private long _signedInSend = long.MinValue;
    private long _lastRenewal;
    private bool _waitSaid;

    /// <param name="inner">What sends the requests: a handler that keeps the cookies, unless a test stands in for SharePoint.</param>
    public BrowserSessionHandler(IReadOnlyList<SessionCookie> initial, Uri site, Func<CancellationToken, Task<IReadOnlyList<SessionCookie>>> renew, ILogger logger, HttpMessageHandler? inner = null, TimeSpan? minRenewalInterval = null)
    {
        _site = site;
        _renew = renew;
        _logger = logger;
        _minRenewalInterval = minRenewalInterval ?? DefaultMinRenewalInterval;
        _cookies = new CookieContainer();
        Apply(initial);
        InnerHandler = inner ?? new SocketsHttpHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 32,
            AllowAutoRedirect = false,
        };
    }

    /// <summary>The cookies the requests carry.</summary>
    internal CookieContainer Cookies => _cookies;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var generation = Volatile.Read(ref _generation);
        var sent = Stopwatch.GetTimestamp();
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (!IsRefusal(response))
        {
            if (response.IsSuccessStatusCode)
            {
                SignedIn(sent);
            }

            return response;
        }

        if (!Rereadable(request.Content))
        {
            // a streaming upload body cannot be re-sent, so its refusal passes through; a concurrent read renews the
            // shared cookies, and the next attempt at the write succeeds
            return response;
        }

        if (Volatile.Read(ref _generation) == generation)
        {
            if (SetsSessionCookie(response) || Volatile.Read(ref _signedInSend) >= sent)
            {
                // the session is good: SharePoint refuses the account this request (a site or a file it may not open),
                // which no new sign-in would change
                SignedIn(sent);
                _logger.LogDebug("sharepoint: {Method} {Path} was refused ({Status}) with the browser session good; not renewing it", request.Method, request.RequestUri?.AbsolutePath, (int)response.StatusCode);
                return response;
            }

            if (!await RenewAsync(generation, request, response, ct).ConfigureAwait(false))
            {
                return response; // the renewal needs a person, or came too soon after the last; let the refusal reach the source
            }
        }

        // renewed since this request went out (by it, or by another): once more with the new cookies
        response.Dispose();
        return await base.SendAsync(await CloneAsync(request).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    /// <summary>A refusal that a lapsed session would give: 401, 403, or a redirect to the sign-in host (a redirect within the site, often relative, is an answer).</summary>
    private static bool IsRefusal(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return true;
        }

        // an unauthenticated SharePoint page is bounced to the sign-in host rather than refused outright
        return response.StatusCode is HttpStatusCode.Found or HttpStatusCode.MovedPermanently
            && response.Headers.Location is { IsAbsoluteUri: true } location && location.Host.Contains("login.microsoftonline", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the answer sets a new session cookie, as SharePoint Online does on its answers to a signed-in request (not on one that clears it).</summary>
    private static bool SetsSessionCookie(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(c => c.StartsWith(SessionCookie + "=", StringComparison.Ordinal) && c.Length > SessionCookie.Length + 1 && c[SessionCookie.Length + 1] != ';');

    private void SignedIn(long sent)
    {
        for (var seen = Volatile.Read(ref _signedInSend); sent > seen; seen = Volatile.Read(ref _signedInSend))
        {
            if (Interlocked.CompareExchange(ref _signedInSend, sent, seen) == seen)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Renews once per generation: the first caller to see stale cookies renews; the rest reuse that result. False when the
    /// renewal needs a person, or when the last one was too recent to try again.
    /// </summary>
    private async Task<bool> RenewAsync(int seenGeneration, HttpRequestMessage request, HttpResponseMessage response, CancellationToken ct)
    {
        await _renewGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _generation) != seenGeneration)
            {
                return true; // another request already renewed since this one started
            }

            if (_lastRenewal != 0 && Stopwatch.GetElapsedTime(_lastRenewal) < _minRenewalInterval)
            {
                if (!_waitSaid)
                {
                    _waitSaid = true;
                    _logger.LogInformation("sharepoint: {Method} {Path} was refused ({Status}) {Seconds:F0} s after the browser session for {Host} was renewed; not renewing it again before {Minutes:F0} minutes have passed", request.Method, request.RequestUri?.AbsolutePath, (int)response.StatusCode, Stopwatch.GetElapsedTime(_lastRenewal).TotalSeconds, _site.Host, _minRenewalInterval.TotalMinutes);
                }

                return false;
            }

            _logger.LogInformation("sharepoint: the browser session for {Host} was refused ({Status} for {Method} {Path}); renewing it", _site.Host, (int)response.StatusCode, request.Method, request.RequestUri?.AbsolutePath);
            _lastRenewal = Stopwatch.GetTimestamp();
            _waitSaid = false;
            var fresh = await _renew(ct).ConfigureAwait(false);
            Apply(fresh);
            Interlocked.Increment(ref _generation);
            return true;
        }
        catch (SignInRequiredException ex)
        {
            _logger.LogInformation("sharepoint: the browser session for {Host} could not be renewed without a sign-in: {Message}", _site.Host, ex.Message);
            return false;
        }
        finally
        {
            _renewGate.Release();
        }
    }

    private void Apply(IReadOnlyList<SessionCookie> cookies)
    {
        foreach (var c in cookies)
        {
            try
            {
                _cookies.Add(new Cookie(c.Name, c.Value, c.Path, c.Domain) { Secure = c.Secure, HttpOnly = c.HttpOnly });
            }
            catch (CookieException ex)
            {
                _logger.LogDebug(ex, "sharepoint: a session cookie {Name} could not be used", c.Name);
            }
        }
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is { } content)
        {
            var bytes = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    /// <summary>A body we can read again to resend: none, or one already buffered in memory (the digest POST, JSON). An upload's StreamContent is read once.</summary>
    private static bool Rereadable(HttpContent? content) => content is null or ByteArrayContent or StringContent;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renewGate.Dispose();
        }

        base.Dispose(disposing);
    }
}
