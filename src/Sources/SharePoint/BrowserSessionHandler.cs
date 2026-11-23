using System.Diagnostics;
using System.Net;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// Sends the browser session's cookies on every request to SharePoint's REST API, and when the site refuses a
/// request because they have lapsed (401, 403, or a redirect to the sign-in host) asks <see cref="BrowserSession"/>
/// to renew them headless and tries the one request again (spec 7.3.2). A renewal that needs a person is left to the
/// source: its <see cref="SignInRequiredException"/> is thrown for the refused request, and for every refused one after
/// it until a renewal may be tried again, so the source turns Degraded, and the next start signs in with a window.
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
    private readonly SignInRenewal _renewal;

    // Stopwatch time: when the latest request SharePoint answered as signed in was sent
    private long _signedInSend = long.MinValue;
    private bool _waitSaid;

    // the refusal that last asked for a renewal, for the log's line when one begins
    private string _refused = "";

    /// <param name="inner">
    /// What sends the requests: a handler that keeps the cookies, unless a test stands in for SharePoint, when the cookies
    /// are carried through the one given as the default handler carries them (<see cref="CookieJarHandler"/>).
    /// </param>
    /// <param name="watcher">Who hears of each request made (<see cref="ISourceContext.UpstreamWatcher"/>): one sent again after a renewal is a request again.</param>
    public BrowserSessionHandler(IReadOnlyList<SessionCookie> initial, Uri site, Func<CancellationToken, Task<IReadOnlyList<SessionCookie>>> renew, ILogger logger, HttpMessageHandler? inner = null, TimeSpan? minRenewalInterval = null, IUpstreamWatcher? watcher = null)
    {
        _site = site;
        _renew = renew;
        _logger = logger;
        _minRenewalInterval = minRenewalInterval ?? DefaultMinRenewalInterval;
        _renewal = new SignInRenewal(RenewNowAsync, _minRenewalInterval);
        _cookies = new CookieContainer();
        Apply(initial);
        InnerHandler = inner is not null ? new CookieJarHandler(_cookies, UpstreamRequestsHandler.Around(inner, watcher)) : UpstreamRequestsHandler.Around(new SocketsHttpHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false,
        }, watcher);
    }

    /// <summary>The cookies the requests carry.</summary>
    internal CookieContainer Cookies => _cookies;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var generation = _renewal.Generation;
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

        if (_renewal.Generation == generation)
        {
            if (SetsSessionCookie(response) || Volatile.Read(ref _signedInSend) >= sent)
            {
                // the session is good: SharePoint refuses the account this request (a site or a file it may not open),
                // which no new sign-in would change
                SignedIn(sent);
                _logger.LogDebug("sharepoint: {Method} {Path} was refused ({Status}) with the browser session good; not renewing it", request.Method, request.RequestUri?.AbsolutePath, (int)response.StatusCode);
                return response;
            }

            bool renewed;
            try
            {
                renewed = await RenewAsync(generation, request, response, ct).ConfigureAwait(false);
            }
            catch
            {
                response.Dispose();
                throw;
            }

            if (!renewed)
            {
                return response; // too soon after the last renewal; let the refusal reach the source
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
    /// Renews once per generation: the first caller to see stale cookies begins the renewal; the rest wait for that one,
    /// each only as long as its own request lasts (<see cref="SignInRenewal"/>). False when the last one was too recent to
    /// try again; the failure of one that failed (the <see cref="SignInRequiredException"/> of one that needed a person),
    /// until another may be tried.
    /// </summary>
    private async Task<bool> RenewAsync(int seenGeneration, HttpRequestMessage request, HttpResponseMessage response, CancellationToken ct)
    {
        Volatile.Write(ref _refused, $"{(int)response.StatusCode} for {request.Method} {request.RequestUri?.AbsolutePath}");
        if (await _renewal.RenewAsync(seenGeneration, ct).ConfigureAwait(false))
        {
            return true;
        }

        if (!_waitSaid)
        {
            _waitSaid = true;
            _logger.LogInformation("sharepoint: {Method} {Path} was refused ({Status}) soon after the browser session for {Host} was renewed; not renewing it again before {Minutes:F0} minutes have passed", request.Method, request.RequestUri?.AbsolutePath, (int)response.StatusCode, _site.Host, _minRenewalInterval.TotalMinutes);
        }

        return false;
    }

    /// <summary>The renewal itself, on the handler's lifetime and no request's.</summary>
    private async Task RenewNowAsync(CancellationToken ct)
    {
        _logger.LogInformation("sharepoint: the browser session for {Host} was refused ({Refused}); renewing it", _site.Host, Volatile.Read(ref _refused));
        _waitSaid = false;
        IReadOnlyList<SessionCookie> fresh;
        try
        {
            fresh = await _renew(ct).ConfigureAwait(false);
        }
        catch (SignInRequiredException ex)
        {
            _logger.LogInformation("sharepoint: the browser session for {Host} could not be renewed without a sign-in: {Message}", _site.Host, ex.Message);
            throw;
        }

        Apply(fresh);
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
            _renewal.Dispose();
        }

        base.Dispose(disposing);
    }
}
