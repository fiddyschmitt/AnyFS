using System.Text;
using System.Text.Json.Nodes;
using AnyFS.Sources.Common;
using AnyFS.Sources.Teams;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Tests;

/// <summary>
/// Which of the page's bearers the Teams web session reads (spec 7.9), with made-up, unsigned tokens, and how its wait
/// for them ends when the browser goes; no browser is launched.
/// </summary>
[TestClass]
public class TeamsWebSessionTests
{
    private const string Contoso = "11111111-1111-1111-1111-111111111111";
    private const string Fabrikam = "22222222-2222-2222-2222-222222222222";
    private const string Personal = "9188040d-6c67-4c5b-b112-36a304b66dad";

    private static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Jwt(string aud, string upn, string tid, string id = "1")
        => $"{B64Url("{\"alg\":\"none\"}")}.{B64Url($"{{\"aud\":\"{aud}\",\"upn\":\"{upn}\",\"tid\":\"{tid}\",\"oid\":\"oid-{id}\",\"exp\":{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}")}.";

    private static TeamsWebSession Session() => new(new Uri("https://teams.test"), "msedge.exe", Path.Combine(Path.GetTempPath(), "anyfs-unused-profile"), "jane@contoso.com", NullLogger.Instance);

    [TestMethod]
    public async Task ASignInNameOtherThanTheEmailIsReadInItsOrganisation()
    {
        var session = Session();
        var chat = Jwt("https://ic3.teams.office.com", "jane.doe@contoso.onmicrosoft.com", Contoso);
        Assert.IsNull(session.Take(chat), "the same organisation under another sign-in name is read");
        Assert.AreEqual(chat, await session.TokenAsync(TeamsService.ChatService, interactive: false, _ => { }, CancellationToken.None));
        Assert.AreEqual("jane.doe@contoso.onmicrosoft.com", session.Account);
    }

    [TestMethod]
    public void AnotherOrganisationOrAPersonalAccountIsNeverRead()
    {
        var session = Session();
        Assert.IsNull(session.Take(Jwt("https://ic3.teams.office.com", "jane@contoso.com", Contoso)));
        StringAssert.Contains(session.Take(Jwt("https://chatsvcagg.teams.microsoft.com", "bob@fabrikam.com", Fabrikam)), "another organisation");

        var personal = Session();
        StringAssert.Contains(personal.Take(Jwt("https://ic3.teams.office.com", "jane@outlook.com", Personal)), "personal Microsoft account");
        Assert.IsNull(personal.Account, "nothing of a personal account is kept");
    }

    [TestMethod]
    public async Task ARefusedTokenIsNotTakenAgainFromThePage()
    {
        var session = Session();
        var old = Jwt("https://ic3.teams.office.com", "jane@contoso.com", Contoso, "old");
        var renewed = Jwt("https://ic3.teams.office.com", "jane@contoso.com", Contoso, "new");
        session.Take(old);
        session.Refused(TeamsService.ChatService, old);
        session.Take(renewed);
        session.Take(old); // the page sends the old one again before it renews it everywhere
        Assert.AreEqual(renewed, await session.TokenAsync(TeamsService.ChatService, interactive: false, _ => { }, CancellationToken.None));
    }

    // far longer than a wait that ends takes: only one that would go on to its ten minutes runs into it
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>The wait for a sign-in nobody finishes, at <paramref name="page"/>, with its ten minutes ahead of it.</summary>
    private static Task<TeamsWebSession.Capture> Wait(TeamsWebSession session, bool headless, Func<bool> closed, Task gone, string page = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize")
        => session.WaitAsync(headless, TimeSpan.FromMinutes(10), [TeamsService.ChatService], () => null, () => page, closed, gone, CancellationToken.None);

    [TestMethod]
    public async Task AWindowThatIsClosedEndsTheWaitAtOnce()
    {
        // the person closes the sign-in window and the browser quits with it, which ends its DevTools connection. Until
        // 22-Nov-2026 the wait slept between its looks and looked at nothing that told it: ten minutes of "Sign in to
        // Teams in the window that opened", the profile held all the while
        var session = Session();
        var gone = new TaskCompletionSource();
        var wait = Wait(session, headless: false, closed: () => false, gone.Task);
        Assert.IsFalse(wait.IsCompleted, "waited on while the browser is there");

        gone.SetResult();
        var capture = await wait.WaitAsync(Patience);
        Assert.AreEqual($"the {session.BrowserName} window was closed before the sign-in to teams.test finished; Retry to sign in", capture.Failure);
        Assert.IsTrue(capture.Final, "the person's answer: no window opens again until they ask for one");
        Assert.IsFalse(capture.Got);
    }

    [TestMethod]
    public async Task AWindowWhoseLastPageHasGoneEndsTheWaitThoughTheBrowserStaysUp()
    {
        // Edge can stay up in the background with its window closed: the connection holds, and no page is left
        var session = Session();
        var pages = new DevToolsPages();
        pages.On("Target.attachedToTarget", new JsonObject { ["sessionId"] = "S1", ["targetInfo"] = new JsonObject { ["targetId"] = "T1", ["type"] = "page", ["url"] = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize" } });
        var wait = session.WaitAsync(headless: false, TimeSpan.FromMinutes(10), [TeamsService.ChatService], () => null, () => pages.Where, () => !pages.Any, new TaskCompletionSource().Task, CancellationToken.None);
        Assert.IsFalse(wait.IsCompleted, "waited on while its page is there");

        pages.On("Target.targetDestroyed", new JsonObject { ["targetId"] = "T1" });
        var capture = await wait.WaitAsync(Patience);
        Assert.AreEqual($"the {session.BrowserName} window was closed before the sign-in to teams.test finished; Retry to sign in", capture.Failure);
        Assert.IsTrue(capture.Final);
    }

    [TestMethod]
    public async Task ABrowserWithoutAWindowThatQuitsEndsTheWaitAtOnce()
    {
        var session = Session();
        var gone = new TaskCompletionSource();
        var wait = Wait(session, headless: true, closed: () => false, gone.Task, page: "https://teams.test/v2/");
        Assert.IsFalse(wait.IsCompleted, "waited on while the browser is there");

        gone.SetResult();
        var capture = await wait.WaitAsync(Patience);
        Assert.AreEqual($"{session.BrowserName} (headless) closed before the sign-in to teams.test was read", capture.Failure);
        Assert.IsFalse(capture.Final, "nothing the person did: a window may still be opened for them");
        Assert.IsFalse(capture.Got);
    }
}
