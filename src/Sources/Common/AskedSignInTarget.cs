using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace AnyFS.Sources.Common;

/// <summary>
/// A sign-in the server itself is asked about. A page of the server open in the browser, with cookies, does not say
/// the person has signed in: the sign-in page is such a page. So a call that only a signed-in session is answered is
/// made with the browser's cookies, every two seconds while the browser shows the server. GitLab's, Atlassian's and
/// 3DX's targets each had this whole, with a client of their own for the asking (the review of 21-Nov-2026, C2).
/// </summary>
/// <param name="trustAnyCertificate">Whether the asking takes any certificate, as the source's settings say.</param>
public abstract class AskedSignInTarget(Uri page, string profileDir, bool trustAnyCertificate) : WebSignInTarget(page, profileDir)
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);

    private long _lastCheck;

    /// <summary>
    /// Whether what the browser shows and holds may be a sign-in, before the server is asked: a page of the server and
    /// some cookie, unless the product knows which.
    /// </summary>
    protected virtual bool MayBeSignedIn(IReadOnlyList<Uri> pages, IReadOnlyList<SessionCookie> cookies)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(cookies);
        return pages.Any(IsOwnPage) && cookies.Count > 0;
    }

    /// <summary>Whether the server takes the cookies <paramref name="http"/> carries as a signed-in session: asked of a call that answers no one else.</summary>
    protected abstract Task<bool> AnswersAsync(HttpClient http, CancellationToken ct);

    public sealed override async ValueTask<SignInCheck> CheckAsync(IReadOnlyList<Uri> pages, IReadOnlyList<SessionCookie> cookies, CancellationToken ct)
    {
        if (!MayBeSignedIn(pages, cookies))
        {
            return SignInCheck.NotYet;
        }

        // now and then: the page may stand for a while before the person has signed in
        if (_lastCheck != 0 && Stopwatch.GetElapsedTime(_lastCheck) < CheckEvery)
        {
            return SignInCheck.NotYet;
        }

        _lastCheck = Stopwatch.GetTimestamp();
        return await SignedInAsync(cookies, ct).ConfigureAwait(false) ? SignInCheck.SignedIn : SignInCheck.NotYet;
    }

    /// <summary>Whether the server takes the browser's cookies as a signed-in session; false too where it does not answer.</summary>
    public async Task<bool> SignedInAsync(IReadOnlyList<SessionCookie> cookies, CancellationToken ct)
    {
        using var http = Client(cookies);
        try
        {
            return await AnswersAsync(http, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Whether a page the browser shows is the server's: its host and port, at or under the target's page.</summary>
    protected bool IsOwnPage(Uri shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        return shown.Host.Equals(Page.Host, StringComparison.OrdinalIgnoreCase) && shown.Port == Page.Port
            && (shown.AbsolutePath.TrimEnd('/') + "/").StartsWith(Page.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A client that sends the browser's cookies and follows no redirect (one means not signed in), the caller's to
    /// dispose. Its jar has room for an identity provider's cookies too: the default holds twenty a domain and lets the
    /// oldest go.
    /// </summary>
    protected HttpClient Client(IReadOnlyList<SessionCookie> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        var jar = CookieJar.New();
        CookieJar.Add(jar, cookies);
        var handler = new SocketsHttpHandler { CookieContainer = jar, UseCookies = true, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All };
        if (trustAnyCertificate)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(20) };
    }
}
