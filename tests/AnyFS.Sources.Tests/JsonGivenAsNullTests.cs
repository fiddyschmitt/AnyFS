using System.Net;
using System.Text;
using System.Text.Json;
using AnyFS.Sources.Common;
using AnyFS.Sources.Outlook.Ews;
using AnyFS.Sources.SharePoint;
using Microsoft.Extensions.Logging.Abstractions;
using Drive = AnyFS.Sources.SharePoint.DriveInfo;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A value of another kind than the one looked for, a null above all, reads as nothing. .NET's reads of a number, a time
/// or a flag throw for a null, and where they were not looked at first that failed far more than the value: a kept sign-in
/// went whole, a listing failed, a source did not start (the review of 21-Nov-2026, R16's rest).
/// </summary>
[TestClass]
public class JsonGivenAsNullTests
{
    [TestMethod]
    public void ACookieWithANullTimeOrFlagIsKeptAndTheOthersWithIt()
    {
        var jar = new CookieContainer();
        var text = """
            [
              {"name":"session","value":"1","path":"/","domain":"server.test","expires":null,"secure":null,"httpOnly":true},
              {"name":"other","value":"2","path":"/","domain":"server.test","expires":"2099-01-01T00:00:00Z","secure":true,"httpOnly":false},
              {"name":null,"value":"3"}
            ]
            """;
        Assert.AreEqual(2, CookieJar.Import(jar, text, "server.test"), "the nameless one alone is left out");
        var cookies = jar.GetCookies(new Uri("https://server.test/"));
        Assert.AreEqual("1", cookies["session"]!.Value);
        Assert.IsFalse(cookies["session"]!.Secure);
        Assert.IsTrue(cookies["session"]!.HttpOnly);
        Assert.AreEqual("2", cookies["other"]!.Value);
        Assert.AreEqual(-1, CookieJar.Import(new CookieContainer(), """{"name":"not a list"}""", "server.test"));
    }

    [TestMethod]
    public void AGraphDriveWithANullQuotaAndAnItemWithANullSizeAreRead()
    {
        using var noQuota = JsonDocument.Parse("""{"id":"d1","name":"Documents","quota":null}""");
        using var nullNumbers = JsonDocument.Parse("""{"id":"d2","name":"Shared","quota":{"total":null,"remaining":null,"used":5}}""");
        using var given = JsonDocument.Parse("""{"id":"d3","name":"Given","quota":{"total":100,"remaining":40}}""");
        Assert.AreEqual(new Drive("d1", "Documents", null, null), GraphDrives.ParseDrive(noQuota.RootElement));
        Assert.AreEqual(new Drive("d2", "Shared", null, null), GraphDrives.ParseDrive(nullNumbers.RootElement));
        Assert.AreEqual(new Drive("d3", "Given", 100, 40), GraphDrives.ParseDrive(given.RootElement));

        using var item = JsonDocument.Parse("""{"id":"i1","name":"a.txt","size":null,"file":{}}""");
        var (entry, _) = GraphDrives.ParseItem(item.RootElement);
        Assert.AreEqual("a.txt", entry.Name);
        Assert.AreEqual(0, entry.Length);
    }

    [TestMethod]
    public async Task OutlookOnTheWebsFlagGivenAsNullIsTakenAsNotGiven()
    {
        var fake = FakeExchange.Mailbox();
        var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
        using var web = new OwaJsonService(fake.Origin, FakeExchange.Account, (_, _) => Task.FromResult(fake.ServerToken), fake.Handler(), NullLogger.Instance);
        fake.Intercept = (_, action) => action == "FindItem"
            ? Answer(action, """{"__type":"FindItemResponseMessage:#Exchange","ResponseClass":"Success","ResponseCode":"NoError","RootFolder":{"__type":"FindItemParentWrapper:#Exchange","Items":[],"TotalItemsInView":0,"IncludesLastItemInRange":null}}""")
            : null;

        var page = await web.FindItemsAsync(inbox.Id, 0, 10, CancellationToken.None);
        Assert.IsTrue(page.IncludesLastItemInRange, "as when it is left out: the last page");
        var now = DateTimeOffset.UtcNow;
        Assert.AreEqual(0, (await web.CalendarViewAsync(inbox.Id, now.AddDays(-30), now, CancellationToken.None)).Count, "and the range is not halved for it");
    }

    private static HttpResponseMessage Answer(string action, string message)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Header":{"ServerVersionInfo":{"MajorVersion":15,"MinorVersion":21,"Version":"V2018_01_08"}},"Body":{"__type":"@ACTION@Response:#Exchange","ResponseMessages":{"Items":[@MESSAGE@]}}}""".Replace("@ACTION@", action, StringComparison.Ordinal).Replace("@MESSAGE@", message, StringComparison.Ordinal),
                Encoding.UTF8, "application/json"),
        };
}
