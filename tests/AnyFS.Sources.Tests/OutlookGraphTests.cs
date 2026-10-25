using System.Net;
using System.Text;
using AnyFS.Fixtures;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Microsoft365;
using AnyFS.Sources.Outlook;

namespace AnyFS.Sources.Tests;

/// <summary>The mailbox store over the fake Exchange's Graph side: the app sign-in (spec 7.7.1) without the sandbox.</summary>
[TestClass]
public class OutlookGraphTests
{
    private static OutlookSettings Settings(bool hidden = false) => new() { Path = FakeExchange.Account, SignInMethod = OutlookSettings.AppDeviceCode, ShowHidden = hidden, RefreshMinutes = 0, CalendarMonthsBack = 12, CalendarMonthsAhead = 3 };

    private static async Task<(OutlookSource Source, TestSourceContext Context)> StartAsync(FakeExchange fake, bool hidden = false)
    {
        var context = new TestSourceContext("outlook");
        var source = new OutlookSource(Settings(hidden), fake.StandIn());
        await source.InitialiseAsync(context, CancellationToken.None);
        return (source, context);
    }

    private static int Count(FakeExchange fake, Func<string, bool> path) => fake.Actions.Count(a => a.StartsWith("graph ", StringComparison.Ordinal) && path(a["graph ".Length..]));

    [TestMethod]
    public void AnAppSignInIsAMailboxsAndCheckedAsSharePointsIs()
    {
        var plugin = new OutlookPlugin();
        var s = new OutlookSettings { Path = "jane@contoso.com", SignInMethod = OutlookSettings.AppBrowser };
        Assert.AreEqual(OutlookSignIn.AppBrowser, s.EffectiveSignIn);
        Assert.AreEqual(SignInMethod.Browser, s.Method);
        Assert.AreEqual(SignInMethod.DeviceCode, new OutlookSettings { Path = "jane@contoso.com", SignInMethod = OutlookSettings.AppDeviceCode }.Method);
        Assert.IsTrue(plugin.IsVisible(s, nameof(OutlookSettings.Tenant)));
        Assert.IsTrue(plugin.IsVisible(s, nameof(OutlookSettings.ClientId)));
        Assert.IsFalse(plugin.IsVisible(s, nameof(OutlookSettings.BrowserPath)), "no browser of AnyFS's own for an app sign-in");
        Assert.IsFalse(plugin.IsVisible(new OutlookSettings { Path = "jane@contoso.com" }, nameof(OutlookSettings.ClientId)), "the web session needs no app");
        Assert.AreEqual(0, plugin.Validate(s).Count(), string.Join("; ", plugin.Validate(s).Select(m => m.Message)));

        // AnyFS's own registration is the global cloud's alone
        s.Cloud = MicrosoftCloud.UsGovernmentName;
        var own = plugin.Validate(s).Single();
        Assert.AreEqual(nameof(OutlookSettings.ClientId), own.Property);
        StringAssert.Contains(own.Message, OutlookPlugin.MailPermissions);
        s.ClientId = "11111111-2222-3333-4444-555555555555";
        Assert.AreEqual(0, plugin.Validate(s).Count());
        Assert.AreEqual("contoso.com", s.TenantIn(MicrosoftCloud.UsGovernment), "outside the global cloud organizations is the mailbox's own domain");
        Assert.AreEqual("organizations", s.TenantIn(MicrosoftCloud.Global));
        Assert.AreSame(MicrosoftCloud.UsGovernment, new OutlookSettings { Path = "jane@acme.onmicrosoft.us", SignInMethod = OutlookSettings.AppBrowser }.KnownCloud, "an initial domain says its cloud");
        Assert.IsNull(new OutlookSettings { Path = "jane@contoso.com" }.KnownCloud, "the realm lookup tells");

        s.Tenant = "two words";
        Assert.AreEqual(nameof(OutlookSettings.Tenant), plugin.Validate(s).Single().Property);
        s.Tenant = "organizations";
        s.ClientId = "not a guid";
        Assert.IsTrue(plugin.Validate(s).Any(m => m.IsError && m.Property == nameof(OutlookSettings.ClientId)));

        // a server takes a Windows account: an app sign-in there is a note, and it goes as yourself
        var server = new OutlookSettings { Path = "https://mail.contoso.com", SignInMethod = OutlookSettings.AppBrowser };
        Assert.AreEqual(OutlookSignIn.WindowsCurrent, server.EffectiveSignIn);
        Assert.IsTrue(plugin.Validate(server).Any(m => !m.IsError && m.Property == nameof(OutlookSettings.SignInMethod)));
        Assert.IsFalse(plugin.Validate(server).Any(m => m.IsError));
        Assert.IsTrue(plugin.Validate(new OutlookSettings { Path = "jane@contoso.com", SignInMethod = OutlookSettings.Windows }).Any(m => m.IsError && m.Property == nameof(OutlookSettings.SignInMethod)), "a mailbox on Exchange Online takes no Windows account");
    }

    [TestMethod]
    public async Task TheTestButtonSignsInAsTheAppAndCountsTheFolders()
    {
        var fake = FakeExchange.Mailbox();
        var result = await new OutlookPlugin().TestAsync(Settings(), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.StartsWith(result.Message, $"Signed in as {FakeExchange.Account} with an app sign-in (Microsoft Graph)");
        StringAssert.Contains(result.Message, "the Inbox holds 3 items");
        Assert.AreEqual(0, Count(fake, p => p.Contains("/delta", StringComparison.Ordinal)), "the Test lists the folders only");
    }

    [TestMethod]
    public async Task AnotherCloudNeedsTheOrganisationsOwnRegistration()
    {
        var fake = FakeExchange.Mailbox();
        var settings = Settings();
        settings.Cloud = MicrosoftCloud.UsGovernmentName;
        using var context = new TestSourceContext("outlook");
        await using var source = new OutlookSource(settings, fake.StandIn());
        var failed = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => source.InitialiseAsync(context, CancellationToken.None));
        Assert.IsTrue(failed.Permanent, failed.Message);
        StringAssert.Contains(failed.Message, "global cloud");
        Assert.AreEqual(0, fake.Actions.Count, "refused before any request");
    }

    [TestMethod]
    public async Task TheMailboxTreeComesFromGraph()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            Assert.AreEqual(0, Count(fake, p => p.StartsWith("me/messages/", StringComparison.Ordinal)), "no item is opened to build the tree");
            var deltas = fake.Actions.Where(a => a.StartsWith("graph me/mailFolders/", StringComparison.Ordinal) && a.EndsWith("/messages/delta", StringComparison.Ordinal)).ToList();
            Assert.AreEqual(5, deltas.Distinct().Count(), "each shown mail folder's delta: " + string.Join(", ", fake.Actions));
            Assert.IsTrue(deltas.Count > 5, "the Inbox's three messages came two to a page");
            Assert.AreEqual(1, Count(fake, p => p.EndsWith("/contacts/delta", StringComparison.Ordinal)));
            Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
            CollectionAssert.AreEquivalent(new[] { "Inbox", "Sent Items", "Drafts", "Deleted Items", "Calendar", "Contacts" }, (await OutlookFixtures.ListAsync(source, "")).Select(e => e.Name).ToList());
            CollectionAssert.AreEquivalent(new[] { "Adele Vance.vcf" }, (await OutlookFixtures.ListAsync(source, "Contacts")).Select(e => e.Name).ToList());
            CollectionAssert.AreEquivalent(new[] { "2026", "Projects" }, (await OutlookFixtures.ListAsync(source, "Inbox")).Select(e => e.Name).ToList());
            CollectionAssert.AreEquivalent(new[] { "03", "04" }, (await OutlookFixtures.ListAsync(source, "Inbox/2026")).Select(e => e.Name).ToList());
            Assert.AreEqual(1, (await OutlookFixtures.ListAsync(source, "Inbox/Projects/2026/04")).Count);
            Assert.IsTrue(fake.GraphPreferences.All(p => p.Contains("IdType=\"ImmutableId\"", StringComparison.Ordinal) && p.Contains("outlook.timezone=\"UTC\"", StringComparison.Ordinal)),
                "every request asks for immutable ids and UTC: " + string.Join(" | ", fake.GraphPreferences.Distinct()));
            Assert.IsTrue(fake.GraphPreferences.Any(p => p.StartsWith("odata.maxpagesize=", StringComparison.Ordinal)), "a delta asks for its page size");
        }

        var (all, context2) = await StartAsync(fake, hidden: true);
        await using (all)
        using (context2)
        {
            CollectionAssert.IsSubsetOf(new[] { "Conversation Action Settings", "Yammer Root" }, (await OutlookFixtures.ListAsync(all, "")).Select(e => e.Name).ToList(), "the hidden mail folders, asked for");
        }
    }

    [TestMethod]
    public async Task AMessageComesWithTheServersMimeAndItsAttachmentsWhole()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            var figures = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/03", "Quarterly figures");
            var name = VfsPath.Name(figures);
            CollectionAssert.AreEquivalent(new[] { name + ".md", name + ".eml", "attachments" }, (await OutlookFixtures.ListAsync(source, figures)).Select(e => e.Name).ToList());
            var eml = Encoding.ASCII.GetString(await OutlookFixtures.ReadAsync(source, $"{figures}/{name}.eml"));
            StringAssert.Contains(eml, "\r\nSubject: Quarterly figures\r\n");
            Assert.AreEqual(1, Count(fake, p => p.StartsWith("me/messages/", StringComparison.Ordinal) && p.EndsWith("/$value", StringComparison.Ordinal) && !p.Contains("/attachments/", StringComparison.Ordinal)), "the .eml is the server's MIME");

            var markdown = await OutlookFixtures.TextAsync(source, $"{figures}/{name}.md");
            StringAssert.Contains(markdown, "**From:** Adele Vance <adelev@fake.test>");
            StringAssert.Contains(markdown, "[figures.csv](attachments/figures.csv) (27 bytes)", "the content's size, not Graph's, which counts the attachment's properties too");
            Assert.AreEqual(27, (await OutlookFixtures.ListAsync(source, $"{figures}/attachments")).Single().Length);
            Assert.AreEqual("quarter,amount\nQ3,42\nQ4,58\n", Encoding.ASCII.GetString(await OutlookFixtures.ReadAsync(source, $"{figures}/attachments/figures.csv")));

            var photo = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/03", "Photo from the site visit");
            StringAssert.Contains(await OutlookFixtures.TextAsync(source, $"{photo}/{VfsPath.Name(photo)}.md"), "![](attachments/dot.png)", "the inline image, by its content id");
            Assert.AreEqual(70, (await OutlookFixtures.ReadAsync(source, $"{photo}/attachments/dot.png")).Length);

            var forwarded = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/04", "Fwd: Photo from the site visit");
            Assert.AreEqual("Photo from the site visit.eml", (await OutlookFixtures.ListAsync(source, $"{forwarded}/attachments")).Single().Name);
            var nested = await OutlookFixtures.TextAsync(source, $"{forwarded}/attachments/Photo from the site visit.eml");
            StringAssert.Contains(nested, "\r\nSubject: Photo from the site visit\r\n");
            StringAssert.Contains(nested, "Content-ID: <site-dot>", "the attached message's own MIME");
        }
    }

    [TestMethod]
    public async Task TheCalendarIsAViewOfOccurrencesAndContactsAreCards()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            var october = await OutlookFixtures.ListAsync(source, "Calendar/2026/10");
            Assert.AreEqual(4, october.Count, string.Join(", ", october.Select(e => e.Name)));
            Assert.AreEqual(3, october.Count(e => e.Name.EndsWith(" - Stand-up", StringComparison.Ordinal)), "each occurrence the server expanded is a folder");
            var review = await OutlookFixtures.ItemFolderAsync(source, "Calendar/2026/10", "Design review");
            var ics = (await OutlookFixtures.TextAsync(source, $"{review}/event.ics")).Replace("\r\n ", "", StringComparison.Ordinal);
            StringAssert.Contains(ics, "\r\nSUMMARY:Design review\r\n");
            StringAssert.Contains(ics, "\r\nLOCATION:Room 4\r\n");
            StringAssert.Contains(ics, "\r\nDTSTART:20261027T040000Z\r\n");
            StringAssert.Contains(ics, "\r\nORGANIZER;CN=\"Adele Vance\":mailto:adelev@fake.test\r\n");
            StringAssert.Contains(ics, "ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED;CN=\"Jane\":mailto:jane@fake.test");
            StringAssert.Contains(await OutlookFixtures.TextAsync(source, $"{review}/event.md"), "**Attendees:** Jane <jane@fake.test> (accepted)");
            Assert.IsTrue(Count(fake, p => p.EndsWith("/calendarView", StringComparison.Ordinal)) >= 2, "a calendar view a year at a time");

            var card = await OutlookFixtures.TextAsync(source, "Contacts/Adele Vance.vcf");
            StringAssert.Contains(card, "\r\nFN:Adele Vance\r\n");
            StringAssert.Contains(card, "\r\nEMAIL;TYPE=INTERNET,PREF:adelev@fake.test\r\n");
            StringAssert.Contains(card, "\r\nTEL;TYPE=WORK,VOICE:+1 555 0100\r\n");
            StringAssert.Contains(card, "\r\nORG:Contoso\r\n");
        }
    }

    [TestMethod]
    public async Task ARefreshTakesInTheDeltaAndALinkGraphNoLongerTakesReadsTheFolderAgain()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            Assert.AreEqual(1, (await OutlookFixtures.ListAsync(source, "Inbox/2026/04")).Count);
            var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
            var added = fake.AddMessage(inbox, "New since the start", ("Alex Wilber", "alexw@fake.test"), new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero), "<p>News.</p>", []);
            await source.RefreshAsync(CancellationToken.None);
            var after = (await OutlookFixtures.ListAsync(source, "Inbox/2026/04")).Select(e => e.Name).ToList();
            Assert.AreEqual(2, after.Count, string.Join(", ", after));
            Assert.IsTrue(after.Any(n => n.EndsWith(" - Alex Wilber - New since the start", StringComparison.Ordinal)));

            fake.Delete(added);
            fake.ForgetDeltas();
            await source.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(1, (await OutlookFixtures.ListAsync(source, "Inbox/2026/04")).Count, "the folder was read again whole once Graph refused its link");
            Assert.AreEqual(2, (await OutlookFixtures.ListAsync(source, "Inbox/2026/03")).Count);
        }
    }

    [TestMethod]
    public async Task ARefusedTokenIsRenewedOnceAndThrottlingIsWaitedOut()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            fake.RotateToken();
            var throttled = 0;
            fake.Intercept = (_, action) =>
            {
                if (action.EndsWith("/attachments", StringComparison.Ordinal) && throttled++ == 0)
                {
                    var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"error":{"code":"ApplicationThrottled","message":"Application is over its MailboxConcurrency limit."}}""", Encoding.UTF8, "application/json") };
                    busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(200));
                    return busy;
                }

                return null;
            };
            var figures = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/03", "Quarterly figures");
            StringAssert.Contains(await OutlookFixtures.TextAsync(source, $"{figures}/{VfsPath.Name(figures)}.eml"), "Subject: Quarterly figures");
            Assert.AreEqual(1, fake.Refusals, "the old token was refused once; the client asked for a fresh one");
            Assert.AreEqual(2, throttled, "the throttled listing waited out its Retry-After and went again");
            Assert.AreEqual(ComponentHealth.Ready, context.LastHealth ?? ComponentHealth.Ready);
        }
    }

    [TestMethod]
    public async Task AnItemThatIsGoneIsNotFound()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            var folder = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/03", "Quarterly figures");
            var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
            fake.Delete(new FakeExchange.Item(inbox.Changes.Select(c => c.ItemId).First(), inbox.Id, "Message", "IPM.Note"));
            var gone = await Assert.ThrowsExactlyAsync<VfsException>(() => OutlookFixtures.ReadAsync(source, $"{folder}/{VfsPath.Name(folder)}.eml"));
            Assert.AreEqual(VfsError.NotFound, gone.Error, gone.Message);
        }
    }

    /// <summary>
    /// The sandbox's test mailbox through Graph: set ANYFS_TEST_GRAPH_MAILBOX (anyfs-tester@…), and sign in once with
    /// tests/fixtures/SharePointSignIn --account &lt;it&gt; --scopes mail (ANYFS_TEST_GRAPH_CACHE names the file, else its
    /// default). The sandbox's administrator must have consented to Mail.Read, Calendars.Read and Contacts.Read for AnyFS.
    /// </summary>
    [TestMethod]
    public async Task TheSandboxMailboxIsServedThroughGraph()
    {
        var address = Environment.GetEnvironmentVariable("ANYFS_TEST_GRAPH_MAILBOX");
        if (string.IsNullOrEmpty(address))
        {
            Assert.Inconclusive("set ANYFS_TEST_GRAPH_MAILBOX (and sign in with tests/fixtures/SharePointSignIn --scopes mail) to run against the sandbox");
        }

        var cache = Environment.GetEnvironmentVariable("ANYFS_TEST_GRAPH_CACHE") ?? SignInCacheFile.DefaultPath;
        var saved = SignInCacheFile.Read(cache);
        if (saved is null)
        {
            Assert.Inconclusive($"no sign-in at {cache}: run tests/fixtures/SharePointSignIn --account {address} --scopes mail");
            return;
        }

        var settings = new OutlookSettings { Path = address!, SignInMethod = OutlookSettings.AppDeviceCode, RefreshMinutes = 0, CalendarMonthsBack = 12, CalendarMonthsAhead = 3 };
        EntraSignIn.Remember(settings.EffectiveClientId, settings.TenantIn(MicrosoftCloud.Global), settings.Address, saved);
        using var context = new TestSourceContext("outlook");
        await using var source = new OutlookSource(settings);
        await source.InitialiseAsync(context, CancellationToken.None);
        CollectionAssert.IsSubsetOf(new[] { "Inbox", "Sent Items", "Drafts", "Deleted Items", "Calendar", "Contacts" }, (await OutlookFixtures.ListAsync(source, "")).Select(e => e.Name).ToList());

        // every attachment of the Inbox's messages (Spike 12's two drafts have a CSV and an inline image) is as long as its file says
        var read = 0;
        foreach (var year in await OutlookFixtures.ListAsync(source, "Inbox"))
        {
            foreach (var month in year.IsDirectory && year.Name.Length == 4 ? await OutlookFixtures.ListAsync(source, $"Inbox/{year.Name}") : [])
            {
                foreach (var item in await OutlookFixtures.ListAsync(source, $"Inbox/{year.Name}/{month.Name}"))
                {
                    var folder = $"Inbox/{year.Name}/{month.Name}/{item.Name}";
                    var files = await OutlookFixtures.ListAsync(source, folder);
                    StringAssert.Contains(await OutlookFixtures.TextAsync(source, $"{folder}/{item.Name}.eml"), "MIME-Version", StringComparison.OrdinalIgnoreCase, "the server's MIME");
                    foreach (var attachment in files.Any(f => f.Name == "attachments") ? await OutlookFixtures.ListAsync(source, $"{folder}/attachments") : [])
                    {
                        Assert.AreEqual(attachment.Length, (await OutlookFixtures.ReadAsync(source, $"{folder}/attachments/{attachment.Name}")).Length, attachment.Name);
                        read++;
                    }
                }
            }
        }

        Assert.IsTrue(read > 0, "the Inbox had no attachment to read");
    }
}
