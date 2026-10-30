using System.Text;
using System.Text.Json;
using AnyFS.Sources.Common;
using AnyFS.Sources.Teams;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Tests;

/// <summary>
/// BRANCH BUILD ONLY (teams-probe): the probe's report keeps nothing of the organisation or its people: shapes of addresses,
/// markup and JSON, with every name, id and text taken out (<see cref="TeamsProbe"/>).
/// </summary>
[TestClass]
public class TeamsProbeTests
{
    [TestMethod]
    public void AnAddressKeepsItsKindAndLosesTheTenantAndTheNames()
    {
        Assert.AreEqual("https://<tenant>-my.sharepoint.us/personal/<x>/Documents/Recordings/<name>.mp4",
            TeamsProbe.UrlShape("https://contoso-my.sharepoint.us/personal/jane_doe_contoso_com/Documents/Recordings/Weekly%20sync-20261030_100000-Meeting%20Recording.mp4"));
        Assert.AreEqual("https://<tenant>.sharepoint.com/sites/<x>/Shared Documents/<x>/<name>.docx?web",
            TeamsProbe.UrlShape("https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/General/Budget.docx?web=1"));
        Assert.AreEqual("https://as-prod.asyncgw.teams.microsoft.com/v1/objects/<x>/views/imgo", TeamsProbe.UrlShape("https://as-prod.asyncgw.teams.microsoft.com/v1/objects/0-sa-d1-abc123/views/imgo"));
        Assert.AreEqual("https://<x>.webhook.office.com/<x>/<x>", TeamsProbe.UrlShape("https://contoso.webhook.office.com/webhookb2/7f1c1d3e"), "a tenant's name in a Microsoft host's first label");
        Assert.AreEqual("https://<x>.<domain>/<x>", TeamsProbe.UrlShape("https://jira.contoso.com/browse"));
        Assert.AreEqual("https://<x>.gov.teams.microsoft.us/", TeamsProbe.UrlShape("https://ic9.gov.teams.microsoft.us"));
        Assert.AreEqual("<not an address>", TeamsProbe.UrlShape("not one"));
    }

    [TestMethod]
    public void MarkupKeepsItsElementsAndKindsAndLosesItsText()
    {
        const string Recording = """<URIObject type="Video.2/CallRecording.1" uri="https://as-prod.asyncgw.teams.microsoft.com/v1/objects/0-x" url_thumbnail="https://x/y"><Title>Weekly sync with Jane Doe</Title><RecordingContent timestamp="2026-10-30T10:00:00Z" duration="1800"><item type="onedriveForBusinessVideo" uri="https://contoso-my.sharepoint.us/personal/jane/Documents/Recordings/a.mp4"/></RecordingContent></URIObject>""";
        var shape = TeamsProbe.XmlShape(Recording);
        Assert.AreEqual("<URIObject type=Video.2/CallRecording.1 uri url_thumbnail><Title><RecordingContent timestamp duration><item type=onedriveForBusinessVideo uri>", shape);
        Assert.IsFalse(shape.Contains("Jane", StringComparison.Ordinal) || shape.Contains("contoso", StringComparison.Ordinal) || shape.Contains("1800", StringComparison.Ordinal));
        Assert.AreEqual("<p><at id>", TeamsProbe.XmlShape("<p>Hi <at id=\"0\">Jane Doe</at>, see this</p>"));
        Assert.AreEqual("", TeamsProbe.XmlShape("plain text, no markup"));
    }

    [TestMethod]
    public void JsonKeepsItsKeysAndKindsAndLosesItsValues()
    {
        using var card = JsonDocument.Parse("""{"contentType":"application/vnd.microsoft.card.adaptive","content":{"type":"AdaptiveCard","version":"1.4","body":[{"type":"TextBlock","text":"Build 42 failed for jane@contoso.com","weight":"Bolder"}]}}""");
        var shape = TeamsProbe.Shape(card.RootElement, 4);
        Assert.AreEqual("{contentType=application/vnd.microsoft.card.adaptive, content:{type=AdaptiveCard, version=1.4, body:[{type=TextBlock, text, weight=Bolder}]}}", shape);

        // ids in keys taken out; a value that is not plain taken out; a special conversation's name kept
        Assert.AreEqual("tab::<guid>", TeamsProbe.Key("tab::27ed7eeb-258c-4b1d-bb48-709c48f15579"));
        Assert.AreEqual("<mri>", TeamsProbe.Key("19:Tqmc8UzXDWnSfQ@thread.tacv2"));
        Assert.AreEqual("8:orgid:<guid>", TeamsProbe.Key("8:orgid:30b2c664-2460-4669-8b48-d770b2ebd8e4"));
        Assert.AreEqual("48:notifications", TeamsProbe.Safe("48:notifications"));
        Assert.AreEqual("<address>", TeamsProbe.Safe("jane@contoso.com"));
        Assert.AreEqual("<value>", TeamsProbe.Safe("Hello, world! This is a message's text with \"quotes\""));
        Assert.AreEqual(".mp4", TeamsProbe.Extension("Weekly sync.MP4"));
        Assert.AreEqual("(none)", TeamsProbe.Extension("README"));
    }

    [TestMethod]
    public void ATokensScopesAreReadWithoutTheToken()
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"{B64("{\"alg\":\"none\"}")}.{B64("{\"aud\":\"https://graph.microsoft.us\",\"scp\":\"Files.ReadWrite.All Group.Read.All\",\"upn\":\"jane@contoso.com\"}")}.";
        Assert.AreEqual(("https://graph.microsoft.us", "Files.ReadWrite.All Group.Read.All"), TeamsProbe.Scopes(token));
    }

    /// <summary>The probe against the sandbox: it runs whole, and its report holds nothing of the tenant, the tester or the content.</summary>
    [TestMethod]
    [Timeout(300_000)]
    public async Task TheProbesReportOfTheSandboxHoldsNothingIdentifying()
    {
        var account = Environment.GetEnvironmentVariable("ANYFS_TEST_TEAMS_ACCOUNT");
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANYFS_TEAMS_PROFILES")))
        {
            Assert.Inconclusive("set ANYFS_TEST_TEAMS_ACCOUNT and ANYFS_TEAMS_PROFILES to probe the sandbox");
        }

        var browser = BrowserLocator.Find(null, "ANYFS_TEAMS_BROWSER") ?? throw new AssertInconclusiveException("no browser");
        var session = new TeamsWebSession(new Uri(TeamsCloud.Global.TeamsHost), browser, WebSignInTarget.ProfileFor("teams-browser", account, "ANYFS_TEAMS_PROFILES"), account, NullLogger.Instance);
        await session.TokenAsync(TeamsService.Aggregator, interactive: false, _ => { }, CancellationToken.None);

        // tokens as the source hands them out: SharePoint's and Graph's only while in hand
        await using var rest = new TeamsRest(new Uri(TeamsCloud.Global.ApiHost), (s, _, c) => s is TeamsService.SharePoint or TeamsService.Graph
                ? session.InHand(s) is { } held ? Task.FromResult(held) : throw TokenNotHandedOutException.Refusal(s)
                : session.TokenAsync(s, false, _ => { }, c), 4, NullLogger.Instance,
            sharePointHost: () => session.SharePointHost, inHand: session.InHand)
        {
            Account = session.Account ?? account,
            Mri = session.Mri,
        };

        var report = await new TeamsProbe(rest, TeamsCloud.Global, session.Mri).RunAsync(CancellationToken.None);
        Console.WriteLine(report);

        StringAssert.Contains(report, "[teams, channels and chats (the aggregator)]");
        StringAssert.Contains(report, "End of report.");
        var tenantLabel = account.Split('@')[1].Split('.')[0];
        var oid = session.Mri?[(session.Mri.LastIndexOf(':') + 1)..];
        foreach (var secret in new[] { account, tenantLabel, "anyfs-tester", "AnyFS Lab", "AnyFS tester", "Mark 8", "Seeded", "A thread with replies", "anyfs-seed", "9dcede70", oid })
        {
            if (!string.IsNullOrEmpty(secret))
            {
                Assert.IsFalse(report.Contains(secret, StringComparison.OrdinalIgnoreCase), $"the report holds '{secret}'");
            }
        }

        Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(report, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"), "no id of any kind");
        Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(report, @"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z0-9.-]+"), "no address (a key such as @type is no address)");
        Assert.IsFalse(report.Contains("section failed", StringComparison.Ordinal), "every section ran");
    }
}
