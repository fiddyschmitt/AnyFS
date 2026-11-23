using System.Net;

namespace AnyFS.Sources.Common;

/// <summary>
/// What the sessions' HTTP set-ups have in common, where each built its own and they had begun to differ (the review of
/// 21-Nov-2026, C2).
/// </summary>
public static class SessionHttp
{
    /// <summary>
    /// The sockets a session's requests go out through: no redirect followed, so a sign-in page sent in place of data is
    /// seen for what it is; no cookies of the handler's own, the session's jar carrying them; every answer decompressed;
    /// a connection kept ten minutes at most, so a server that has moved is found again. Any certificate is taken where
    /// the settings say so, and <paramref name="credentials"/> answer a server that asks for a Windows sign-in.
    /// </summary>
    public static SocketsHttpHandler Sockets(bool trustAnyCertificate, ICredentials? credentials = null)
    {
        var sockets = new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        if (trustAnyCertificate)
        {
            sockets.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        if (credentials is not null)
        {
            sockets.Credentials = credentials;
        }

        return sockets;
    }

    /// <summary>
    /// A client over a session's handler, which stays the session's to dispose: its timeout, and AnyFS named to the
    /// server with the version of the source's own assembly.
    /// </summary>
    public static HttpClient Client(HttpMessageHandler handler, TimeSpan timeout, Type source)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(source);
        var http = new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"AnyFS/{source.Assembly.GetName().Version?.ToString(3) ?? "0"}");
        return http;
    }
}

/// <summary>
/// A session's cookies on every request, and every answer's kept, whatever sends the request (a socket, a test's
/// stand-in): one jar holds the browser's or the sign-in's cookies and the server's renewals of them. Three sessions
/// each had this as a class of their own.
/// </summary>
/// <param name="notWithAuthorization">
/// A request that carries its own authorisation (a token, Basic) goes without the cookies: a server that ends at once
/// the session such a request makes.
/// </param>
public sealed class CookieJarHandler(CookieContainer jar, HttpMessageHandler inner, bool notWithAuthorization = false) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var url = request.RequestUri!;

        // a request sent again after a renewal comes with the old cookies among its headers
        request.Headers.Remove("Cookie");
        var header = notWithAuthorization && request.Headers.Authorization is not null ? "" : jar.GetCookieHeader(url);
        if (header.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", header);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Headers.TryGetValues("Set-Cookie", out var set))
        {
            foreach (var cookie in set)
            {
                try
                {
                    jar.SetCookies(url, cookie);
                }
                catch (CookieException)
                {
                    // a cookie .NET will not hold, as its own handler drops one: the session goes without it
                }
            }
        }

        return response;
    }
}
