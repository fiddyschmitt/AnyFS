using System.Net;
using AnyFS.Sources.SharePoint;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Tests;

/// <summary>
/// When the web session renews its cookies (spec 7.3.2): SharePoint Online refuses a signed-in account what it may not
/// open just as it refuses a lapsed session (403 with X-Forms_Based_Auth_Required, seen 20-Oct-2026), so only a refusal
/// with nothing to show the session is good renews, and not again soon after a renewal; a request on its way during a
/// renewal is sent again with the new cookies.
/// </summary>
[TestClass]
public class BrowserSessionHandlerTests
{
    private static readonly Uri Site = new("https://contoso.sharepoint.com/sites/a");

    /// <summary>SharePoint Online as the web session meets it: each request answered by its path and the session cookie it carries.</summary>
    private sealed class Online(Func<string, string?, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public CookieContainer? Jar { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => answer(request.RequestUri!.AbsolutePath, Jar?.GetCookies(request.RequestUri)["FedAuth"]?.Value);
    }

    /// <summary>SharePoint Online's refusal; a signed-in one sets a new session cookie, as its answers to a signed-in request do.</summary>
    private static HttpResponseMessage Refused(bool signedIn)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"odata.error":{"code":"-2147024891, System.UnauthorizedAccessException","message":{"lang":"en-US","value":"Attempted to perform an unauthorized operation."}}}"""),
        };
        response.Headers.TryAddWithoutValidation("X-Forms_Based_Auth_Required", "https://contoso.sharepoint.com/_forms/default.aspx?ReturnUrl=/_layouts/15/error.aspx");
        response.Headers.TryAddWithoutValidation("X-MSDAVEXT_Error", "917656; Access+denied.");
        if (signedIn)
        {
            response.Headers.TryAddWithoutValidation("Set-Cookie", "FedAuth=77u/PD94bWwgdmVyc2lvbj0; expires=Sun, 25-Oct-2026 10:09:35 GMT; path=/; SameSite=None; secure; HttpOnly");
        }

        return response;
    }

    private static HttpResponseMessage Answered() => new(HttpStatusCode.OK) { Content = new StringContent("{}") };

    private static (HttpClient Http, Func<int> Renewals) Session(Online online, TimeSpan? minRenewalInterval = null)
    {
        var renewals = 0;
        var handler = new BrowserSessionHandler(
            [new SessionCookie("FedAuth", "old", Site.Host, "/", true, true)],
            Site,
            async ct =>
            {
                Interlocked.Increment(ref renewals);
                await Task.Delay(50, ct);
                return [new SessionCookie("FedAuth", "new", Site.Host, "/", true, true)];
            },
            NullLogger.Instance,
            online,
            minRenewalInterval);
        online.Jar = handler.Cookies;
        return (new HttpClient(handler), () => Volatile.Read(ref renewals));
    }

    [TestMethod]
    public async Task AnAccountRefusedWhatItMayNotOpenKeepsItsSession()
    {
        var online = new Online((path, _) => Task.FromResult(path.EndsWith("/secret", StringComparison.Ordinal) ? Refused(signedIn: true) : Answered()));
        var (http, renewals) = Session(online);
        using (http)
        {
            using var refused = await http.GetAsync($"{Site}/_api/web/lists/secret");
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode, "the refusal is the source's to see");
            using var answered = await http.GetAsync($"{Site}/_api/web/title");
            Assert.AreEqual(HttpStatusCode.OK, answered.StatusCode);
            Assert.AreEqual(0, renewals(), "no browser launched for a refusal that set a new session cookie");
        }
    }

    [TestMethod]
    public async Task ARefusalAnsweredAfterALaterRequestKeepsTheSessionToo()
    {
        var arrived = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var online = new Online(async (path, _) =>
        {
            if (path.EndsWith("/secret", StringComparison.Ordinal))
            {
                arrived.TrySetResult();
                await release.Task;
                return Refused(signedIn: false);   // a refusal that says nothing of the session
            }

            return Answered();
        });
        var (http, renewals) = Session(online);
        using (http)
        {
            var secret = http.GetAsync($"{Site}/_api/web/lists/secret");
            await arrived.Task;
            using (var later = await http.GetAsync($"{Site}/_api/web/title"))
            {
                Assert.AreEqual(HttpStatusCode.OK, later.StatusCode);
            }

            release.SetResult();
            using var refused = await secret;
            Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.AreEqual(0, renewals(), "a request sent after it was answered: the session is good");
        }
    }

    [TestMethod]
    public async Task ALapsedSessionRenewsOnceForAllTheRequestsOnTheirWay()
    {
        var online = new Online((_, fedAuth) => Task.FromResult(fedAuth == "new" ? Answered() : Refused(signedIn: false)));
        var (http, renewals) = Session(online);
        using (http)
        {
            var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => http.GetAsync($"{Site}/_api/web/lists/l{i}")));
            Assert.IsTrue(answers.All(a => a.StatusCode == HttpStatusCode.OK), "each sent again with the new cookies");
            Assert.AreEqual(1, renewals());
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task ARenewalThatDidNotHelpIsNotTriedAgainAtOnce()
    {
        var online = new Online((_, _) => Task.FromResult(Refused(signedIn: false)));
        var (http, renewals) = Session(online, TimeSpan.FromMilliseconds(400));
        using (http)
        {
            (await http.GetAsync($"{Site}/_api/web/title")).Dispose();
            Assert.AreEqual(1, renewals(), "renewed, and refused all the same");
            for (var i = 0; i < 5; i++)
            {
                using var refused = await http.GetAsync($"{Site}/_api/web/title");
                Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
            }

            Assert.AreEqual(1, renewals(), "not again so soon (the user saw one 18 seconds after another)");
            await Task.Delay(500);
            (await http.GetAsync($"{Site}/_api/web/title")).Dispose();
            Assert.AreEqual(2, renewals(), "and again once the interval has passed");
        }
    }

    [TestMethod]
    public async Task ARedirectWithinTheSiteIsAnAnswerAndOneToTheSignInRenews()
    {
        var online = new Online((path, fedAuth) =>
        {
            if (fedAuth == "new")
            {
                return Task.FromResult(Answered());
            }

            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = path.EndsWith("/default.aspx", StringComparison.Ordinal)
                ? new Uri("/sites/a/SitePages/Home.aspx", UriKind.Relative)   // as SharePoint Online sends a browser to a site's home page
                : new Uri("https://login.microsoftonline.us/organizations/oauth2/authorize?client_id=00000003-0000-0ff1-ce00-000000000000");
            return Task.FromResult(response);
        });
        var (http, renewals) = Session(online);
        using (http)
        {
            using (var home = await http.GetAsync($"{Site}/default.aspx"))
            {
                Assert.AreEqual(HttpStatusCode.Found, home.StatusCode, "a relative Location, which has no host to read");
                Assert.AreEqual(0, renewals());
            }

            using var page = await http.GetAsync($"{Site}/SitePages/Home.aspx");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
            Assert.AreEqual(1, renewals(), "bounced to the sign-in host (the US Government cloud's)");
        }
    }
}
