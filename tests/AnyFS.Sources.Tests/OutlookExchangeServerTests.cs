using System.Net;
using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Outlook;

namespace AnyFS.Sources.Tests;

/// <summary>An Exchange server's EWS (SOAP, a Windows account) over the fake Exchange: the same mailbox the web session serves.</summary>
[TestClass]
public class OutlookExchangeServerTests
{
    private static OutlookSettings Settings(string signIn = OutlookSettings.Windows) => new()
    {
        Path = "https://exchange.fake.test/owa",
        SignInMethod = signIn,
        Username = @"FAKE\jane",
        Password = "pw",
        TrustAnyCertificate = true,
        RefreshMinutes = 0,
        CalendarMonthsBack = 12,
        CalendarMonthsAhead = 3,
    };

    private static async Task<(OutlookSource Source, TestSourceContext Context)> StartAsync(FakeExchange fake)
    {
        var context = new TestSourceContext("outlook");
        var source = new OutlookSource(Settings(), fake.StandIn());
        await source.InitialiseAsync(context, CancellationToken.None);
        return (source, context);
    }

    [TestMethod]
    public void AServersAddressTakesAWindowsAccountAndItsEwsEndpointIsDerived()
    {
        var plugin = new OutlookPlugin();
        var settings = Settings();
        Assert.AreEqual(OutlookKind.Server, settings.Kind);
        Assert.AreEqual("https://exchange.fake.test/EWS/Exchange.asmx", settings.EwsUrl!.AbsoluteUri);
        Assert.AreEqual("https://mail.contoso.com/ews/exchange.asmx", new OutlookSettings { Path = "https://mail.contoso.com/ews/exchange.asmx" }.EwsUrl!.AbsoluteUri, "an EWS address is taken as it is");
        Assert.AreEqual(OutlookSignIn.Windows, settings.EffectiveSignIn);
        Assert.AreEqual(OutlookSignIn.WindowsCurrent, Settings(OutlookSettings.WindowsCurrent).EffectiveSignIn);
        Assert.AreEqual(OutlookSignIn.WindowsCurrent, Settings(OutlookSettings.WebSession).EffectiveSignIn, "the web session is Exchange Online's; a server goes as yourself");
        Assert.AreEqual("exchange.fake.test", plugin.SuggestName(settings));
        Assert.IsTrue(plugin.IsVisible(settings, nameof(OutlookSettings.Username)));
        Assert.IsTrue(plugin.IsVisible(settings, nameof(OutlookSettings.TrustAnyCertificate)));
        Assert.IsFalse(plugin.IsVisible(settings, nameof(OutlookSettings.Cloud)));
        Assert.IsFalse(plugin.IsVisible(Settings(OutlookSettings.WindowsCurrent), nameof(OutlookSettings.Password)));
        Assert.AreEqual(0, plugin.Validate(settings).Count(m => m.IsError), string.Join("; ", plugin.Validate(settings).Select(m => m.Message)));
        Assert.IsTrue(plugin.Validate(Settings(OutlookSettings.WebSession)).Any(m => !m.IsError && m.Property == nameof(OutlookSettings.SignInMethod)), "the web session on a server is a note, not an error");
        Assert.IsTrue(plugin.Validate(new OutlookSettings { Path = "https://mail.contoso.com", SignInMethod = OutlookSettings.Windows, Username = "two words" }).Any(m => m.IsError && m.Property == nameof(OutlookSettings.Username)));
        Assert.IsTrue(plugin.Validate(new OutlookSettings { Path = "http://mail.contoso.com", SignInMethod = OutlookSettings.Windows, Username = "jane", Password = "x" }).Any(m => !m.IsError && m.Message.Contains("unencrypted", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task TheTestButtonSignsInOverSoapAndCountsTheFolders()
    {
        var fake = FakeExchange.Mailbox();
        var result = await new OutlookPlugin().TestAsync(Settings(), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.StartsWith(result.Message, $"Signed in as {FakeExchange.Account} with a Windows account (exchange.fake.test)");
        StringAssert.Contains(result.Message, "the Inbox holds 3 items");
        CollectionAssert.AreEqual(new[] { "FindFolder" }, fake.Actions);
    }

    [TestMethod]
    public async Task TheMailboxIsServedOverSoapAsOverTheWebSession()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            var root = (await OutlookFixtures.ListAsync(source, "")).Select(e => e.Name).ToList();
            CollectionAssert.AreEquivalent(new[] { "Inbox", "Sent Items", "Drafts", "Deleted Items", "Calendar", "Contacts" }, root);
            Assert.AreEqual(6, fake.Actions.Count(a => a == "SyncFolderItems"));

            var figures = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/03", "Quarterly figures");
            var name = VfsPath.Name(figures);
            var eml = await OutlookFixtures.TextAsync(source, $"{figures}/{name}.eml");
            StringAssert.Contains(eml, "\r\nSubject: Quarterly figures\r\n");
            StringAssert.Contains(eml, Convert.ToBase64String(Encoding.ASCII.GetBytes("quarter,amount\nQ3,42\nQ4,58\n")), "the MIME came from the server whole");
            var markdown = await OutlookFixtures.TextAsync(source, $"{figures}/{name}.md");
            StringAssert.Contains(markdown, "**From:** Adele Vance <adelev@fake.test>");
            StringAssert.Contains(markdown, "\n---\n\nFigures attached.");
            Assert.AreEqual("quarter,amount\nQ3,42\nQ4,58\n", Encoding.ASCII.GetString(await OutlookFixtures.ReadAsync(source, $"{figures}/attachments/figures.csv")));

            var forwarded = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/04", "Fwd: Photo from the site visit");
            var nested = await OutlookFixtures.TextAsync(source, $"{forwarded}/attachments/Photo from the site visit.eml");
            StringAssert.Contains(nested, "\r\nSubject: Photo from the site visit\r\n");

            var october = await OutlookFixtures.ListAsync(source, "Calendar/2026/10");
            Assert.AreEqual(4, october.Count, string.Join(", ", october.Select(e => e.Name)));
            var review = await OutlookFixtures.ItemFolderAsync(source, "Calendar/2026/10", "Design review");
            var ics = (await OutlookFixtures.TextAsync(source, $"{review}/event.ics")).Replace("\r\n ", "", StringComparison.Ordinal);
            StringAssert.Contains(ics, "\r\nLOCATION:Room 4\r\n");
            StringAssert.Contains(ics, "ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED;CN=\"Jane\":mailto:jane@fake.test");

            var card = await OutlookFixtures.TextAsync(source, "Contacts/Adele Vance.vcf");
            StringAssert.Contains(card, "\r\nEMAIL;TYPE=INTERNET,PREF:adelev@fake.test\r\n");
            StringAssert.Contains(card, "\r\nORG:Contoso\r\n");

            var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
            fake.AddMessage(inbox, "New since the start", ("Alex Wilber", "alexw@fake.test"), new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero), "<p>News.</p>", []);
            await source.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(2, (await OutlookFixtures.ListAsync(source, "Inbox/2026/04")).Count, "the delta brought the new message");
        }
    }

    [TestMethod]
    public async Task AWindowsSignInTheServerRefusesFailsForGood()
    {
        var fake = FakeExchange.Mailbox();
        fake.RefuseWindowsSignIn = true;
        using var context = new TestSourceContext("outlook");
        var source = new OutlookSource(Settings(), fake.StandIn());
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(context, CancellationToken.None));
        Assert.IsTrue(ex.Permanent, ex.Message);
        StringAssert.Contains(ex.Message, "refused the Windows sign-in");
        StringAssert.Contains(ex.Message, "Negotiate");

        var result = await new OutlookPlugin().TestAsync(Settings(), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Message, "refused the Windows sign-in");
    }

    [TestMethod]
    public async Task ABusyServerIsWaitedOutAndASoapFaultIsSaidAsSuch()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            var busy = 0;
            fake.Intercept = (request, action) =>
            {
                if (action == "GetItem" && busy++ == 0)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(100));
                    return response;
                }

                return null;
            };
            var figures = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/03", "Quarterly figures");
            var eml = await OutlookFixtures.TextAsync(source, $"{figures}/{VfsPath.Name(figures)}.eml");
            StringAssert.Contains(eml, "Subject: Quarterly figures");
            Assert.AreEqual(2, busy);

            fake.Intercept = (_, action) => action == "GetAttachment"
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>a:ErrorSchemaValidation</faultcode><faultstring>The request failed schema validation.</faultstring></s:Fault></s:Body></s:Envelope>""", Encoding.UTF8, "text/xml") }
                : null;
            var failed = await Assert.ThrowsExactlyAsync<VfsException>(() => OutlookFixtures.ReadAsync(source, $"{figures}/attachments/figures.csv"));
            StringAssert.Contains(failed.Message, "schema validation");
        }
    }

    /// <summary>
    /// A real Exchange server (the lab's anyfs-ex-1): set ANYFS_TEST_EWS_URL (https://192.168.0.89 or its EWS address),
    /// ANYFS_TEST_EWS_USER (ANYFS\ex_user) and ANYFS_TEST_EWS_PASSWORD. The certificate is the lab's own, so any is trusted.
    /// </summary>
    [TestMethod]
    public async Task TheLabExchangeServerIsServedOverSoap()
    {
        var url = Environment.GetEnvironmentVariable("ANYFS_TEST_EWS_URL");
        var user = Environment.GetEnvironmentVariable("ANYFS_TEST_EWS_USER");
        var password = Environment.GetEnvironmentVariable("ANYFS_TEST_EWS_PASSWORD");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        {
            Assert.Inconclusive("set ANYFS_TEST_EWS_URL, ANYFS_TEST_EWS_USER and ANYFS_TEST_EWS_PASSWORD to run against an Exchange server");
        }

        var settings = new OutlookSettings { Path = url!, SignInMethod = OutlookSettings.Windows, Username = user, Password = password, TrustAnyCertificate = true, RefreshMinutes = 0, CalendarMonthsBack = 12, CalendarMonthsAhead = 3 };
        var result = await new OutlookPlugin().TestAsync(settings, new Progress<string>(), null, CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "with a Windows account");

        using var context = new TestSourceContext("outlook");
        await using var source = new OutlookSource(settings);
        await source.InitialiseAsync(context, CancellationToken.None);
        var root = (await OutlookFixtures.ListAsync(source, "")).Select(e => e.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "Inbox", "Sent Items", "Drafts", "Deleted Items", "Calendar", "Contacts" }, root);
        var years = await OutlookFixtures.ListAsync(source, "Inbox");
        Assert.IsTrue(years.Count > 0, "the Inbox is empty: the lab's seeded messages are missing");
        var year = VfsPath.Join("Inbox", years.First(e => e.Name.Length == 4).Name);
        var months = await OutlookFixtures.ListAsync(source, year);
        var items = await OutlookFixtures.ListAsync(source, VfsPath.Join(year, months[0].Name));
        Assert.IsTrue(items.Count > 0);
        var first = VfsPath.Join(VfsPath.Join(year, months[0].Name), items[0].Name);
        var files = await OutlookFixtures.ListAsync(source, first);
        var eml = files.Single(f => f.Name.EndsWith(".eml", StringComparison.Ordinal));
        var text = await OutlookFixtures.TextAsync(source, VfsPath.Join(first, eml.Name));
        StringAssert.Contains(text, "\r\nSubject: ");
        var markdown = await OutlookFixtures.TextAsync(source, VfsPath.Join(first, files.Single(f => f.Name.EndsWith(".md", StringComparison.Ordinal)).Name));
        StringAssert.Contains(markdown, "**From:**");
    }
}
