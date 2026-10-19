using System.Net;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// Sends the browser session's cookies on every request to SharePoint's REST API, and when the site refuses a
/// request because they have lapsed (401, 403, or a redirect to the sign-in host) asks <see cref="BrowserSession"/>
/// to renew them headless and tries the one request again (spec 7.3.2). A renewal that needs a person is left to the
/// source: the refusal then passes through, the source turns Degraded, and the next start signs in with a window.
/// Renewals are serialised, and one serves every request that was in flight when the cookies went stale.
/// </summary>
internal sealed class BrowserSessionHandler : DelegatingHandler
{
    private readonly CookieContainer _cookies;
    private readonly Uri _site;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SessionCookie>>> _renew;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _renewGate = new(1, 1);
    private int _generation;

    public BrowserSessionHandler(IReadOnlyList<SessionCookie> initial, Uri site, Func<CancellationToken, Task<IReadOnlyList<SessionCookie>>> renew, ILogger logger)
    {
        _site = site;
        _renew = renew;
        _logger = logger;
        _cookies = new CookieContainer();
        Apply(initial);
        InnerHandler = new SocketsHttpHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 32,
            AllowAutoRedirect = false,
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var generation = Volatile.Read(ref _generation);
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (!NeedsRenewal(response) || !Rereadable(request.Content))
        {
            // a streaming upload body cannot be re-sent, so its refusal passes through; a concurrent read renews the
            // shared cookies, and the next attempt at the write succeeds
            return response;
        }

        if (!await RenewAsync(generation, ct).ConfigureAwait(false))
        {
            return response; // the renewal needs a person; let the refusal reach the source
        }

        response.Dispose();
        return await base.SendAsync(await CloneAsync(request).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    private bool NeedsRenewal(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return true;
        }

        // an unauthenticated SharePoint page is bounced to the sign-in host rather than refused outright
        return response.StatusCode is HttpStatusCode.Found or HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently
            && response.Headers.Location is { Host: var host } && host.Contains("login.microsoftonline", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Renews once per generation: the first caller to see stale cookies renews; the rest reuse that result.</summary>
    private async Task<bool> RenewAsync(int seenGeneration, CancellationToken ct)
    {
        await _renewGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _generation) != seenGeneration)
            {
                return true; // another request already renewed since this one started
            }

            _logger.LogInformation("sharepoint: the browser session for {Host} was refused; renewing it", _site.Host);
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
