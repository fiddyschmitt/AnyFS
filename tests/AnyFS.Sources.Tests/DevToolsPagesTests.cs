using System.Text.Json.Nodes;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The pages a browser driven over DevTools has open, kept from its Target events for the web sessions that listen
/// (Outlook's, Teams'), with the events as the browser words them; no browser is launched.
/// </summary>
[TestClass]
public class DevToolsPagesTests
{
    private static JsonObject Info(string target, string url, string type) => new() { ["targetId"] = target, ["type"] = type, ["title"] = "", ["url"] = url, ["attached"] = true };

    private static void Attach(DevToolsPages pages, string session, string target, string url, string type = "page")
        => pages.On("Target.attachedToTarget", new JsonObject { ["sessionId"] = session, ["targetInfo"] = Info(target, url, type), ["waitingForDebugger"] = false });

    private static void Change(DevToolsPages pages, string target, string url, string type = "page")
        => pages.On("Target.targetInfoChanged", new JsonObject { ["targetInfo"] = Info(target, url, type) });

    private static void Detach(DevToolsPages pages, string session) => pages.On("Target.detachedFromTarget", new JsonObject { ["sessionId"] = session });

    private static void Destroy(DevToolsPages pages, string target) => pages.On("Target.targetDestroyed", new JsonObject { ["targetId"] = target });

    [TestMethod]
    public void APageIsThereFromItsAttachingUntilItIsDetachedOrDestroyed()
    {
        var pages = new DevToolsPages();
        Assert.AreEqual((false, (string?)null, ""), (pages.Any, pages.FirstSession, pages.Where), "a browser whose window has not opened yet");

        Attach(pages, "S1", "T1", "about:blank");
        Attach(pages, "S2", "T2", "https://teams.test/v2/");
        Assert.AreEqual((true, "S1", "about:blank"), (pages.Any, pages.FirstSession, pages.Where), "the first is the one the browser opened with");

        // a tab closed: the browser says both, in either order
        Detach(pages, "S1");
        Assert.AreEqual((true, "S2", "https://teams.test/v2/"), (pages.Any, pages.FirstSession, pages.Where));
        Destroy(pages, "T1");
        Assert.IsTrue(pages.Any, "said twice, gone once");

        // the last one: the window is closed, though the browser may stay up in the background
        Destroy(pages, "T2");
        Assert.AreEqual((false, (string?)null, ""), (pages.Any, pages.FirstSession, pages.Where));
        Detach(pages, "S2");
        Assert.IsFalse(pages.Any);

        Attach(pages, "S3", "T3", "https://teams.test/v2/");
        Assert.AreEqual((true, "S3"), (pages.Any, pages.FirstSession), "a window opened again is a page again");
    }

    [TestMethod]
    public void APageThatMovesChangesItsOwnAddressAlone()
    {
        var pages = new DevToolsPages();
        Attach(pages, "S1", "T1", "");
        Attach(pages, "S2", "T2", "https://outlook.test/mail/");
        Assert.AreEqual("https://outlook.test/mail/", pages.Where, "the first page that has an address");

        Change(pages, "T2", "https://login.microsoftonline.com/common/oauth2/authorize");
        Assert.AreEqual("https://login.microsoftonline.com/common/oauth2/authorize", pages.Where);
        Detach(pages, "S2");
        Assert.AreEqual("", pages.Where, "the other page is where it was: nowhere yet");

        Change(pages, "T1", "https://outlook.test/mail/");
        Assert.AreEqual("https://outlook.test/mail/", pages.Where);
        Change(pages, "T9", "https://elsewhere.test/");
        Assert.AreEqual("https://outlook.test/mail/", pages.Where, "a page that is not among them changes none of them");
    }

    [TestMethod]
    public void AServiceWorkerOrAFrameIsNoPage()
    {
        var pages = new DevToolsPages();
        Attach(pages, "S1", "T1", "https://teams.test/sw.js", "service_worker");
        Attach(pages, "S2", "T2", "https://login.microsoftonline.com/frame", "iframe");
        Attach(pages, "S3", "T3", "", "browser");
        Assert.AreEqual((false, (string?)null, ""), (pages.Any, pages.FirstSession, pages.Where), "a worker left running is not a window left open");

        Attach(pages, "S4", "T4", "https://teams.test/v2/");
        Change(pages, "T1", "https://teams.test/sw-2.js", "service_worker");
        Detach(pages, "S2");
        Destroy(pages, "T1");
        pages.On("Network.requestWillBeSent", new JsonObject { ["requestId"] = "7", ["request"] = new JsonObject { ["url"] = "https://teams.test/api" } });
        pages.On("Target.targetCreated", new JsonObject { ["targetInfo"] = Info("T5", "https://teams.test/other", "page") });
        Assert.AreEqual((true, "S4", "https://teams.test/v2/"), (pages.Any, pages.FirstSession, pages.Where), "what happens to the others, and any other event, leaves the page as it is");

        Destroy(pages, "T4");
        Assert.IsFalse(pages.Any, "the workers do not keep the window open");
    }
}
