using System.Net;
using System.Text.RegularExpressions;

namespace AnyFS.Sources.Common;

/// <summary>
/// Whether a site answers at all, asked with one plain request before a browser is launched to sign in to it. A browser
/// shown for a site that cannot be reached (the computer awake before its network, the VPN not up yet) shows the person
/// an error page to sign in on, and its closing or its ten minutes then count as the person's own refusal: the source
/// waited for a Retry by hand though nothing was wrong that waiting would not mend. A site that does not answer fails
/// the sign-in as a failure that may pass, so the host tries again by itself.
/// <para>
/// Anything the site (or a proxy on its behalf) says counts as an answer: a refusal, an error page, a redirect, a TLS
/// it will not do with a plain client (one that wants a certificate of the person's: the browser has it). Only a name
/// that does not resolve, a connection that cannot be made, a proxy that says it could not reach the site, and no
/// answer in time count as none.
/// </para>
/// </summary>
public static partial class SiteProbe
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(15);

    [GeneratedRegex(@"status code '(?<code>\d{3})'", RegexOptions.CultureInvariant)]
    private static partial Regex TunnelStatus();

    /// <summary>Null where <paramref name="site"/> answered; else why it did not, for the message.</summary>
    /// <param name="handler">What sends the request, for tests; null for the network, through the system's proxy as a browser goes.</param>
    public static async Task<string?> UnreachableAsync(Uri site, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(site);
        var own = handler is null;
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,

            // a proxy that asks who is asking is answered as the browser answers it, with the person's own sign-in
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,

            // whether it answers, not who it is: nothing is sent to it but the question
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        using var http = new HttpClient(handler, disposeHandler: own) { Timeout = Within };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(site.GetLeftPart(UriPartial.Authority)));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return null;
        }
        catch (HttpRequestException ex) when (IsNoAnswer(ex))
        {
            return ex.Message;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"no answer within {Within.TotalSeconds:F0} seconds";
        }
    }

    /// <summary>Whether a failed request means the site was not reached: not a failure in something it said.</summary>
    internal static bool IsNoAnswer(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError => true,

        // a proxy's own answer to the tunnel: that it could not reach the site (502, 503, 504) is none; that it will not let
        // this request through (403, 407) says nothing of what the browser, which signs in to the proxy, will get
        HttpRequestError.ProxyTunnelError => TunnelStatus().Match(ex.Message) is { Success: true } status && status.Groups["code"].Value is "502" or "503" or "504",
        _ => false,
    };
}
