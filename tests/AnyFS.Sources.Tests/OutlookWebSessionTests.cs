using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Common;
using AnyFS.Sources.Microsoft365;
using AnyFS.Sources.Outlook;
using AnyFS.Sources.Outlook.Ews;
using AnyFS.Sources.Outlook.Mail;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Tests;

/// <summary>The mailbox store over the fake Exchange: the web session's shapes (spec 7.7.2) without the sandbox.</summary>
[TestClass]
public class OutlookWebSessionTests
{
    private static OutlookSettings Settings(bool hidden = false) => new() { Path = FakeExchange.Account, ShowHidden = hidden, RefreshMinutes = 0, CalendarMonthsBack = 12, CalendarMonthsAhead = 3 };

    private static async Task<(OutlookSource Source, TestSourceContext Context)> StartAsync(FakeExchange fake, bool hidden = false)
    {
        var context = new TestSourceContext("outlook");
        var source = new OutlookSource(Settings(hidden), fake.StandIn());
        await source.InitialiseAsync(context, CancellationToken.None);
        return (source, context);
    }

    [TestMethod]
    public async Task EveryRequestOfTheServerIsARowThatEnds()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            await OutlookFixtures.ListAsync(source, "");
            await UpstreamRequestRows.AllEndAsync(context);
            var asked = context.UpstreamRequests.Requests;
            Assert.IsTrue(asked.Count > 0);
            Assert.IsTrue(asked.All(r => r.Target.Contains("/owa/", StringComparison.Ordinal) && r.Answer is not null), string.Join("; ", asked));
        }
    }

    [TestMethod]
    public void AnAddressIsAMailboxAndAnAddressOfAServerIsAServer()
    {
        var plugin = new OutlookPlugin();
        var mailbox = new OutlookSettings { Path = " Jane@Contoso.com " };
        Assert.AreEqual(OutlookKind.Mailbox, mailbox.Kind);
        Assert.AreEqual("jane@contoso.com", plugin.SuggestName(mailbox), "the cloud is the sign-in's to find: no host yet");
        Assert.AreEqual("jane@agency.gov (outlook.office365.us)", plugin.SuggestName(new OutlookSettings { Path = "Jane@Agency.gov", Cloud = MicrosoftCloud.UsGovernmentName }));
        Assert.AreEqual("jane@contoso.com (outlook.office.com)", plugin.SuggestName(new OutlookSettings { Path = "jane@contoso.com", Cloud = MicrosoftCloud.GlobalName }));
        Assert.AreEqual("adele@contoso.onmicrosoft.com (outlook.office.com)", plugin.SuggestName(new OutlookSettings { Path = "adele@contoso.onmicrosoft.com" }), "a tenant's own domain says its cloud");
        plugin.NormaliseSettings(mailbox);
        Assert.AreEqual("jane@contoso.com", mailbox.Path);
        Assert.AreEqual(0, plugin.Validate(mailbox).Count(m => m.IsError), string.Join("; ", plugin.Validate(mailbox).Select(m => m.Message)));
        Assert.IsTrue(plugin.IsVisible(mailbox, nameof(OutlookSettings.Cloud)));
        Assert.IsFalse(plugin.IsVisible(new OutlookSettings { Path = @"C:\a.pst" }, nameof(OutlookSettings.Cloud)));

        var server = new OutlookSettings { Path = "https://mail.contoso.com/EWS/Exchange.asmx" };
        Assert.AreEqual(OutlookKind.Server, server.Kind);
        Assert.AreEqual(OutlookSignIn.WindowsCurrent, server.EffectiveSignIn, "the web session is Exchange Online's; a server takes a Windows account");
        Assert.AreEqual(OperatingSystem.IsWindows() ? 0 : 1, plugin.Validate(server).Count(m => m.IsError), "as yourself, which works on Windows only");
        Assert.IsFalse(plugin.IsVisible(server, nameof(OutlookSettings.Cloud)));
        Assert.AreEqual(OutlookKind.None, new OutlookSettings { Path = "not an address" }.Kind);
        Assert.AreEqual(OutlookKind.DataFile, new OutlookSettings { Path = @"C:\mail\archive.ost" }.Kind);
    }

    [TestMethod]
    public void OutlookOnTheWebsOwnAddressIsAnExchangeOnlineMailboxNotAServer()
    {
        // what a person in GCC High pastes from the browser: once taken for a server, and refused ("Bearer asked for")
        var plugin = new OutlookPlugin();
        var gcc = new OutlookSettings { Path = " https://outlook.office365.us/mail/inbox " };
        Assert.AreEqual(OutlookKind.Mailbox, gcc.Kind);
        Assert.IsNull(gcc.Address, "the mailbox is the one signed in to there");
        Assert.IsNull(gcc.ServerUrl);
        Assert.AreSame(MicrosoftCloud.UsGovernment, gcc.KnownCloud);
        Assert.AreEqual(OutlookSignIn.WebSession, gcc.EffectiveSignIn);
        Assert.AreEqual("outlook.office365.us", plugin.SuggestName(gcc));
        Assert.AreEqual(0, plugin.Validate(gcc).Count(m => m.IsError), string.Join("; ", plugin.Validate(gcc).Select(m => m.Message)));
        Assert.IsFalse(plugin.IsVisible(gcc, nameof(OutlookSettings.Cloud)), "the address names the cloud");
        Assert.IsTrue(plugin.IsVisible(gcc, nameof(OutlookSettings.BrowserPath)));
        Assert.IsFalse(plugin.IsVisible(gcc, nameof(OutlookSettings.Username)));
        plugin.NormaliseSettings(gcc);
        Assert.AreEqual("https://outlook.office365.us", gcc.Path);

        foreach (var global in new[] { "https://outlook.office.com/mail/", "https://outlook.office365.com", "https://OUTLOOK.cloud.microsoft" })
        {
            var s = new OutlookSettings { Path = global };
            Assert.AreEqual(OutlookKind.Mailbox, s.Kind, global);
            Assert.AreSame(MicrosoftCloud.Global, s.KnownCloud, global);
        }

        // an app sign-in needs the mailbox's address; a Windows account is a server's
        Assert.IsTrue(plugin.Validate(new OutlookSettings { Path = "https://outlook.office365.us", SignInMethod = OutlookSettings.AppBrowser }).Any(m => m.IsError && m.Property == nameof(OutlookSettings.SignInMethod)));
        Assert.IsTrue(plugin.Validate(new OutlookSettings { Path = "https://outlook.office365.us", SignInMethod = OutlookSettings.WindowsCurrent }).Any(m => m.IsError && m.Property == nameof(OutlookSettings.SignInMethod)));
        Assert.AreEqual(OutlookKind.Server, new OutlookSettings { Path = "https://mail.contoso.com/owa" }.Kind, "any other host is still a server");
    }

    [TestMethod]
    public async Task AMailboxGivenByOutlookOnTheWebsAddressIsTheOneSignedInThere()
    {
        var fake = FakeExchange.Mailbox();
        var settings = new OutlookSettings { Path = "https://outlook.office365.us", RefreshMinutes = 0, CalendarMonthsBack = 12, CalendarMonthsAhead = 3 };
        var result = await new OutlookPlugin().TestAsync(settings, new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.StartsWith(result.Message, $"Signed in as {FakeExchange.Account} through the web session");

        using var context = new TestSourceContext("outlook");
        await using var source = new OutlookSource(settings, fake.StandIn());
        await source.InitialiseAsync(context, CancellationToken.None);
        CollectionAssert.IsSubsetOf(new[] { "Inbox", "Calendar", "Contacts" }, (await OutlookFixtures.ListAsync(source, "")).Select(e => e.Name).ToList());
    }

    [TestMethod]
    public async Task TheTestButtonSignsInAndCountsTheFolders()
    {
        var fake = FakeExchange.Mailbox();
        var result = await new OutlookPlugin().TestAsync(Settings(), new Progress<string>(), fake.StandIn(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.StartsWith(result.Message, $"Signed in as {FakeExchange.Account} through the web session");
        StringAssert.Contains(result.Message, "the Inbox holds 3 items");
        StringAssert.Contains(result.Message, "Inbox");
        CollectionAssert.AreEqual(new[] { "FindFolder" }, fake.Actions);
    }

    /// <summary>The fake's mailbox with twenty messages in May 2026 in the Inbox, the sync giving seven changes a page.</summary>
    private static FakeExchange WithManyPages()
    {
        var fake = FakeExchange.Mailbox();
        var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
        for (var day = 1; day <= 20; day++)
        {
            fake.AddMessage(inbox, $"Report {day:D2}", ("Adele Vance", "adelev@fake.test"), new DateTimeOffset(2026, 5, day, 9, 0, 0, TimeSpan.Zero), $"<p>Day {day}.</p>", []);
        }

        fake.MostChangesPerPage = 7;
        return fake;
    }

    [TestMethod]
    public async Task AFolderOfManyPagesIsReadWhole()
    {
        // Outlook on the web says IncludesLastItemInRange on a sync's last page only; each folder was read to its first page
        var fake = WithManyPages();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            Assert.AreEqual(20, (await OutlookFixtures.ListAsync(source, "Inbox/2026/05")).Count, "every page of the Inbox's 23 items, seven at a time");
            Assert.AreEqual(2, (await OutlookFixtures.ListAsync(source, "Inbox/2026/03")).Count);
        }
    }

    [TestMethod]
    public async Task AMailboxKeptFromItsFoldersFirstPagesFillsInAtTheNextRefresh()
    {
        // as the source kept it before 26-Oct-2026: each folder synced to its first page alone, and kept so
        var fake = WithManyPages();
        fake.EveryPageSaysLast = true;
        byte[] kept;
        using (var first = new TestSourceContext("outlook"))
        {
            var source = new OutlookSource(Settings(false), fake.StandIn());
            await source.InitialiseAsync(first, CancellationToken.None);
            await using (source)
            {
                Assert.AreEqual(4, (await OutlookFixtures.ListAsync(source, "Inbox/2026/05")).Count, "the first page's seven: March's two, April's one and four of May's");
            }

            kept = await File.ReadAllBytesAsync(Path.Combine(first.StateDirectory, MailboxIndexFile.FileName));
        }

        // the next start serves what was kept and asks for what came after it: the rest of each folder
        fake.EveryPageSaysLast = false;
        var wholeReads = fake.FullSyncs;
        using var second = new TestSourceContext("outlook");
        await File.WriteAllBytesAsync(Path.Combine(second.StateDirectory, MailboxIndexFile.FileName), kept);
        var restarted = new OutlookSource(Settings(false), fake.StandIn());
        await restarted.InitialiseAsync(second, CancellationToken.None);
        await using (restarted)
        {
            var may = 0;
            for (var i = 0; i < 200 && may < 20; i++)
            {
                await Task.Delay(20);
                may = (await OutlookFixtures.ListAsync(restarted, "Inbox/2026/05")).Count;
            }

            Assert.AreEqual(20, may, "filled in by the refresh at the start");
            Assert.AreEqual(wholeReads, fake.FullSyncs, "no folder read whole again");
        }
    }

    /// <summary>Every name below <paramref name="path"/>, folders and files, however deep.</summary>
    private static async Task<List<string>> EverythingAsync(OutlookSource source, string path = "")
    {
        var names = new List<string>();
        var folders = new Queue<string>();
        folders.Enqueue(path);
        while (folders.Count > 0)
        {
            var at = folders.Dequeue();
            foreach (var entry in await OutlookFixtures.ListAsync(source, at))
            {
                var child = at.Length == 0 ? entry.Name : at + "/" + entry.Name;
                names.Add(child);
                if (entry.IsDirectory)
                {
                    folders.Enqueue(child);
                }
            }
        }

        return names;
    }

    [TestMethod]
    public async Task ARefreshThatFindsNothingChangedMakesNoTreeAndOneThatFindsAChangeMakesIt()
    {
        // The tree was made again at every refresh whatever the mailbox said: every folder sorted and every item named, a
        // kilobyte allocated for each item of the mailbox each time, 83 MB for 80,000 of them every five minutes (the
        // review of 21-Nov-2026, P9)
        var fake = FakeExchange.Mailbox();
        var inbox = fake.Folders.Values.Single(f => f.Name == "Inbox");
        var calendar = fake.Folders.Values.Single(f => f.Name == "Calendar");
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            await source.RefreshAsync(CancellationToken.None);
            var before = await EverythingAsync(source);
            var quiet = source.QuietRefreshes;
            for (var n = 0; n < 3; n++)
            {
                await source.RefreshAsync(CancellationToken.None);
            }

            Assert.AreEqual(quiet + 3, source.QuietRefreshes, "nothing changed: no tree made");
            CollectionAssert.AreEqual(before, await EverythingAsync(source), "and everything is where it was");

            // a message come since: taken in by the refresh, and the tree made of it
            var come = fake.AddMessage(inbox, "Come since the start", ("Adele Vance", "adelev@fake.test"), new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), "<p>New.</p>", []);
            await source.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(quiet + 3, source.QuietRefreshes);
            Assert.IsTrue((await EverythingAsync(source)).Any(n => n.Contains("Come since the start", StringComparison.Ordinal)), "shown");
            await source.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(quiet + 4, source.QuietRefreshes, "and quiet again after it");

            // a meeting put in the calendar: its view is other than it was
            fake.AddAppointment(calendar, "Put in since", DateTimeOffset.UtcNow.AddDays(2), TimeSpan.FromHours(1), "Room 5", ("Adele Vance", "adelev@fake.test"));
            await source.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(quiet + 4, source.QuietRefreshes);
            Assert.IsTrue((await EverythingAsync(source)).Any(n => n.Contains("Put in since", StringComparison.Ordinal)));
            await source.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(quiet + 5, source.QuietRefreshes);

            // a message gone
            fake.Delete(come);
            await source.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(quiet + 5, source.QuietRefreshes);
            Assert.IsFalse((await EverythingAsync(source)).Any(n => n.Contains("Come since the start", StringComparison.Ordinal)), "gone from the tree");

            // and a folder made, with something in it
            var made = fake.AddFolder("Made since", "IPF.Note", inbox, null);
            fake.AddMessage(made, "In the new folder", ("Adele Vance", "adelev@fake.test"), new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero), "<p>New.</p>", []);
            await source.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(quiet + 5, source.QuietRefreshes);
            Assert.IsTrue((await EverythingAsync(source)).Any(n => n.Contains("Made since", StringComparison.Ordinal)));

            // (its items were first read as the tree was made, which the store counts as a change after the tree's
            // revision was taken: the tree is made once more at the next refresh, and stands from the one after)
            await source.RefreshAsync(CancellationToken.None);
            await source.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(quiet + 6, source.QuietRefreshes);
            Assert.IsTrue((await EverythingAsync(source)).Any(n => n.Contains("In the new folder", StringComparison.Ordinal)));
        }
    }

    [TestMethod]
    public async Task AFolderWithNothingInItIsNotShownUntilSomethingIs()
    {
        var fake = FakeExchange.Mailbox();
        var top = fake.Folders.Values.Single(f => f.IsRoot);
        var journal = fake.AddFolder("Journal", "IPF.Journal", top, "journal");
        var archive = fake.AddFolder("Archive", "IPF.Note", top, null);
        fake.AddFolder("2019", "IPF.Note", archive, null);
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            var root = (await OutlookFixtures.ListAsync(source, "")).Select(e => e.Name).ToList();
            CollectionAssert.AreEquivalent(new[] { "Inbox", "Calendar", "Contacts" }, root, "Journal, Drafts, Sent Items and Deleted Items are empty; Archive holds only an empty folder");
            Assert.IsNull(await source.StatAsync("Journal", CancellationToken.None));

            // something in it, found by a refresh: shown
            fake.AddMessage(journal, "Phone call with Adele", ("Adele Vance", "adelev@fake.test"), new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), "<p>Called.</p>", []);
            await source.RefreshAsync(CancellationToken.None);
            CollectionAssert.Contains((await OutlookFixtures.ListAsync(source, "")).Select(e => e.Name).ToList(), "Journal");
        }

        // asked for Outlook's hidden folders, the empty ones are shown too
        var (all, context2) = await StartAsync(fake, hidden: true);
        await using (all)
        using (context2)
        {
            CollectionAssert.IsSubsetOf(new[] { "Archive", "Drafts", "Sent Items", "Deleted Items" }, (await OutlookFixtures.ListAsync(all, "")).Select(e => e.Name).ToList());
            CollectionAssert.AreEqual(new[] { "2019" }, (await OutlookFixtures.ListAsync(all, "Archive")).Select(e => e.Name).ToList());
        }
    }

    [TestMethod]
    public async Task TheMailboxTreeLeavesOutlooksOwnFoldersOut()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            Assert.IsFalse(fake.Actions.Contains("GetItem"), "no item is opened to build the tree");
            Assert.AreEqual(1, fake.Actions.Count(a => a == "FindFolder"));
            Assert.AreEqual(6, fake.Actions.Count(a => a == "SyncFolderItems"), "each shown folder but the calendar was synced once: " + string.Join(",", fake.Actions));
            Assert.AreEqual(EnumerationState.Unknown, source.Counters.State, "so far: a message's attachments are known once it is opened");
            var root = (await OutlookFixtures.ListAsync(source, "")).Select(e => e.Name).ToList();
            CollectionAssert.AreEquivalent(new[] { "Inbox", "Calendar", "Contacts" }, root, "the empty Sent Items, Drafts and Deleted Items are not shown");
            CollectionAssert.AreEquivalent(new[] { "Adele Vance.vcf" }, (await OutlookFixtures.ListAsync(source, "Contacts")).Select(e => e.Name).ToList(), "the Recipient Cache under Contacts is Outlook's own");
            Assert.AreEqual(1, fake.Actions.Count(a => a == "GetItem"), "a contact is opened for its card's size when its folder is listed");
            CollectionAssert.AreEquivalent(new[] { "2026", "Projects" }, (await OutlookFixtures.ListAsync(source, "Inbox")).Select(e => e.Name).ToList());
            CollectionAssert.AreEquivalent(new[] { "03", "04" }, (await OutlookFixtures.ListAsync(source, "Inbox/2026")).Select(e => e.Name).ToList());
        }

        var (all, context2) = await StartAsync(fake, hidden: true);
        await using (all)
        using (context2)
        {
            var root = (await OutlookFixtures.ListAsync(all, "")).Select(e => e.Name).ToList();
            CollectionAssert.IsSubsetOf(new[] { "Conversation Action Settings", "Yammer Root" }, root);
            CollectionAssert.Contains((await OutlookFixtures.ListAsync(all, "Contacts")).Select(e => e.Name).ToList(), "Recipient Cache");
        }
    }

    [TestMethod]
    public async Task AMessageIsServedWithTheServersOwnMime()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            var month = await OutlookFixtures.ListAsync(source, "Inbox/2026/03");
            Assert.AreEqual(2, month.Count);
            var figures = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/03", "Quarterly figures");
            StringAssert.Contains(VfsPath.Name(figures), " - Adele Vance - ");
            var name = VfsPath.Name(figures);
            var files = await OutlookFixtures.ListAsync(source, figures);
            CollectionAssert.AreEquivalent(new[] { "message.md", "message.eml", "attachments" }, files.Select(e => e.Name).ToList());

            var eml = await OutlookFixtures.ReadAsync(source, $"{figures}/message.eml");
            var text = Encoding.ASCII.GetString(eml);
            StringAssert.Contains(text, "\r\nSubject: Quarterly figures\r\n");
            StringAssert.Contains(text, "From: Adele Vance <adelev@fake.test>");
            StringAssert.Contains(text, Convert.ToBase64String(Encoding.ASCII.GetBytes("quarter,amount\nQ3,42\nQ4,58\n")), "the MIME came from the server whole");
            Assert.AreEqual(1, fake.Actions.Count(a => a == "GetItem"), "one GetItem, MIME included, serves the folder");

            var markdown = await OutlookFixtures.TextAsync(source, $"{figures}/message.md");
            StringAssert.Contains(markdown, "**From:** Adele Vance <adelev@fake.test>");
            StringAssert.Contains(markdown, "**To:** Jane <jane@fake.test>");
            StringAssert.Contains(markdown, "[figures.csv](attachments/figures.csv) (27 bytes)", "the content's size, not the server's, which counts the attachment's properties too");
            StringAssert.Contains(markdown, "\n---\n\nFigures attached.");

            Assert.AreEqual(27, (await OutlookFixtures.ListAsync(source, $"{figures}/attachments")).Single().Length);
            var csv = await OutlookFixtures.ReadAsync(source, $"{figures}/attachments/figures.csv");
            Assert.AreEqual("quarter,amount\nQ3,42\nQ4,58\n", Encoding.ASCII.GetString(csv));
            Assert.AreEqual(1, fake.Actions.Count(a => a == "GetAttachment"), "the attachment is fetched once, when its message's folder is opened");
            Assert.IsNull(await source.StatAsync("Inbox/2026/03/nothing here", CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task InlineImagesAndAttachedMessagesComeFromTheServer()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            var photo = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/03", "Photo from the site visit");
            var markdown = await OutlookFixtures.TextAsync(source, $"{photo}/message.md");
            StringAssert.Contains(markdown, "![](attachments/dot.png)");
            Assert.AreEqual(70, (await OutlookFixtures.ReadAsync(source, $"{photo}/attachments/dot.png")).Length);

            var forwarded = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/04", "Fwd: Photo from the site visit");
            var attachments = await OutlookFixtures.ListAsync(source, $"{forwarded}/attachments");
            Assert.AreEqual("Photo from the site visit.eml", attachments.Single().Name);
            var nested = await OutlookFixtures.TextAsync(source, $"{forwarded}/attachments/Photo from the site visit.eml");
            StringAssert.Contains(nested, "\r\nSubject: Photo from the site visit\r\n");
            StringAssert.Contains(nested, "Content-ID: <site-dot>");
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
            // folded at 75 octets as RFC 5545 asks; unfolded here to read it whole
            var ics = (await OutlookFixtures.TextAsync(source, $"{review}/event.ics")).Replace("\r\n ", "", StringComparison.Ordinal);
            StringAssert.Contains(ics, "\r\nSUMMARY:Design review\r\n");
            StringAssert.Contains(ics, "\r\nLOCATION:Room 4\r\n");
            StringAssert.Contains(ics, "\r\nDTSTART:20261027T040000Z\r\n");
            StringAssert.Contains(ics, "\r\nORGANIZER;CN=\"Adele Vance\":mailto:adelev@fake.test\r\n");
            StringAssert.Contains(ics, "ATTENDEE;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED;CN=\"Jane\":mailto:jane@fake.test");
            var md = await OutlookFixtures.TextAsync(source, $"{review}/event.md");
            StringAssert.Contains(md, "**Attendees:** Jane <jane@fake.test> (accepted)");
            Assert.IsTrue(fake.Actions.Count(a => a == "FindItem") >= 2, "a calendar view a year at a time");

            var card = await OutlookFixtures.TextAsync(source, "Contacts/Adele Vance.vcf");
            StringAssert.Contains(card, "\r\nFN:Adele Vance\r\n");
            StringAssert.Contains(card, "\r\nEMAIL;TYPE=INTERNET,PREF:adelev@fake.test\r\n");
            StringAssert.Contains(card, "\r\nTEL;TYPE=WORK,VOICE:+1 555 0100\r\n");
            StringAssert.Contains(card, "\r\nORG:Contoso\r\n");
        }
    }

    /// <summary>A meeting request in the Inbox is mail: filed by when it came, named by its sender, the meeting's .ics beside its message files.</summary>
    [TestMethod]
    public async Task AMeetingRequestInTheInboxIsAMessageWithTheMeetingBesideIt()
    {
        var fake = FakeExchange.Mailbox();
        var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
        var received = new DateTimeOffset(2026, 5, 6, 9, 0, 0, TimeSpan.Zero);
        fake.AddMeetingRequest(inbox, "Design review", ("Adele Vance", "adelev@fake.test"), received, new DateTimeOffset(2026, 10, 27, 4, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1), "Room 4");
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            Assert.AreEqual(1, (await OutlookFixtures.ListAsync(source, "Inbox/2026/05")).Count, "filed by when it came, not by the meeting's day months ahead");
            CollectionAssert.DoesNotContain((await OutlookFixtures.ListAsync(source, "Inbox/2026")).Select(e => e.Name).ToList(), "10");
            var request = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/05", "Design review");
            var local = received.ToLocalTime().ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual($"{local} - Adele Vance - Design review", VfsPath.Name(request), "named as the mail around it, by its sender");
            CollectionAssert.AreEquivalent(new[] { "message.md", "message.eml", "event.ics" }, (await OutlookFixtures.ListAsync(source, request)).Select(e => e.Name).ToList());
            var md = await OutlookFixtures.TextAsync(source, $"{request}/message.md");
            StringAssert.Contains(md, "**Where:** Room 4");
            StringAssert.Contains(md, "**Organiser:** Adele Vance <adelev@fake.test>");
            StringAssert.Contains(await OutlookFixtures.TextAsync(source, $"{request}/message.eml"), "\r\nSubject: Design review\r\n");
            var ics = (await OutlookFixtures.TextAsync(source, $"{request}/event.ics")).Replace("\r\n ", "", StringComparison.Ordinal);
            StringAssert.Contains(ics, "\r\nDTSTART:20261027T040000Z\r\n");
            StringAssert.Contains(ics, "\r\nLOCATION:Room 4\r\n");

            // the appointment in the Calendar is still an event, by its start
            var review = await OutlookFixtures.ItemFolderAsync(source, "Calendar/2026/10", "Design review");
            CollectionAssert.AreEquivalent(new[] { "event.md", "event.ics" }, (await OutlookFixtures.ListAsync(source, review)).Select(e => e.Name).ToList());
        }
    }

    /// <summary>
    /// A mailbox's listing is kept between starts: a restart serves the last listing at once and asks the server only what
    /// changed since, from each folder's sync state, never every folder whole; a listing kept under other settings is not used.
    /// </summary>
    [TestMethod]
    public async Task ARestartServesTheKeptListingAndAsksOnlyWhatChanged()
    {
        var fake = FakeExchange.Mailbox();
        byte[] listing;
        var (first, firstContext) = await StartAsync(fake);
        await using (first)
        using (firstContext)
        {
            Assert.IsTrue(fake.FullSyncs > 0, "the first start lists each folder whole");
            var kept = Path.Combine(firstContext.StateDirectory, "mailbox.json.gz");
            Assert.IsTrue(File.Exists(kept), "the listing is kept in the state folder");
            listing = await File.ReadAllBytesAsync(kept);
        }

        var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
        fake.AddMessage(inbox, "While AnyFS was off", ("Alex Wilber", "alexw@fake.test"), new DateTimeOffset(2026, 4, 21, 9, 0, 0, TimeSpan.Zero), "<p>News.</p>", []);
        var full = fake.FullSyncs;
        using (var context = new TestSourceContext("outlook"))
        {
            await File.WriteAllBytesAsync(Path.Combine(context.StateDirectory, "mailbox.json.gz"), listing);
            var source = new OutlookSource(Settings(), fake.StandIn());
            await source.InitialiseAsync(context, CancellationToken.None);
            await using (source)
            {
                Assert.AreEqual(full, fake.FullSyncs, "the restart lists no folder whole");
                await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/03", "Quarterly figures");
                await source.RefreshAsync(CancellationToken.None);
                var april = (await OutlookFixtures.ListAsync(source, "Inbox/2026/04")).Select(e => e.Name).ToList();
                Assert.IsTrue(april.Any(n => n.EndsWith(" - Alex Wilber - While AnyFS was off", StringComparison.Ordinal)), "what changed since is taken in: " + string.Join(", ", april));
                Assert.AreEqual(full, fake.FullSyncs, "by the folders' deltas alone");
            }
        }

        using (var other = new TestSourceContext("outlook"))
        {
            await File.WriteAllBytesAsync(Path.Combine(other.StateDirectory, "mailbox.json.gz"), listing);
            var shown = new OutlookSource(Settings(hidden: true), fake.StandIn());
            await shown.InitialiseAsync(other, CancellationToken.None);
            await using (shown)
            {
                Assert.IsTrue(fake.FullSyncs > full, "with hidden folders shown the kept listing is not this one's: the mailbox is listed whole");
            }
        }
    }

    /// <summary>
    /// A refresh syncs only the folders whose change marks moved (Exchange's PidTagLocalCommitTimeMax and
    /// PidTagDeletedCountTotal, read with the folders): a quiet mailbox's refresh is the one FindFolder.
    /// </summary>
    [TestMethod]
    public async Task ARefreshSyncsOnlyTheFoldersWhoseChangeMarksMoved()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            var syncs = fake.Syncs;
            await source.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(syncs, fake.Syncs, "nothing changed: no folder synced");

            var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
            fake.AddMessage(inbox, "Fresh news", ("Alex Wilber", "alexw@fake.test"), new DateTimeOffset(2026, 4, 21, 9, 0, 0, TimeSpan.Zero), "<p>News.</p>", []);
            await source.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(syncs + 1, fake.Syncs, "the Inbox alone: its marks moved");
            var april = (await OutlookFixtures.ListAsync(source, "Inbox/2026/04")).Select(e => e.Name).ToList();
            Assert.IsTrue(april.Any(n => n.EndsWith(" - Alex Wilber - Fresh news", StringComparison.Ordinal)), string.Join(", ", april));
        }
    }

    [TestMethod]
    public async Task ARefreshTakesInWhatChangedThroughTheFolderDelta()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            var before = await OutlookFixtures.ListAsync(source, "Inbox/2026/04");
            Assert.AreEqual(1, before.Count);
            var syncsBefore = fake.Actions.Count(a => a == "SyncFolderItems");

            var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
            var added = fake.AddMessage(inbox, "New since the start", ("Alex Wilber", "alexw@fake.test"), new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero), "<p>News.</p>", []);
            await source.RefreshAsync(CancellationToken.None);
            var after = (await OutlookFixtures.ListAsync(source, "Inbox/2026/04")).Select(e => e.Name).ToList();
            Assert.AreEqual(2, after.Count, string.Join(", ", after));
            Assert.IsTrue(after.Any(n => n.EndsWith(" - Alex Wilber - New since the start", StringComparison.Ordinal)));
            Assert.AreEqual(syncsBefore + 1, fake.Actions.Count(a => a == "SyncFolderItems"), "one delta call, for the folder whose change marks moved; the quiet ones are not synced, and nothing is listed whole");
            Assert.AreEqual(2, fake.Actions.Count(a => a == "FindFolder"));

            fake.Delete(added);
            await source.RefreshAsync(CancellationToken.None);
            Assert.AreEqual(1, (await OutlookFixtures.ListAsync(source, "Inbox/2026/04")).Count);
        }
    }

    [TestMethod]
    public async Task ARefusedTokenIsRenewedOnceAndAHoldIsWaitedOut()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            fake.RotateToken();
            var held = 0;
            fake.Intercept = (request, action) =>
            {
                if (action == "GetAttachment" && held++ == 0)
                {
                    var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(200));
                    return busy;
                }

                return null;
            };
            var folder = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/03", "Quarterly figures");
            var eml = await OutlookFixtures.TextAsync(source, $"{folder}/message.eml");
            StringAssert.Contains(eml, "Subject: Quarterly figures");
            Assert.AreEqual(1, fake.Refusals, "the old token was refused once; the stand-in handed over the new one");
            Assert.AreEqual(ComponentHealth.Ready, context.LastHealth ?? ComponentHealth.Ready);

            // the attachment was read when its message's folder was opened, held once on the way
            var csv = await OutlookFixtures.ReadAsync(source, $"{folder}/attachments/figures.csv");
            Assert.AreEqual(27, csv.Length);
            Assert.AreEqual(2, held, "the request was held once and then sent again");
        }
    }

    [TestMethod]
    public async Task AnItemThatIsGoneIsNotFoundAndATokenRefusedForGoodIsDegraded()
    {
        var fake = FakeExchange.Mailbox();
        var (source, context) = await StartAsync(fake);
        await using (source)
        using (context)
        {
            var folder = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/03", "Quarterly figures");
            var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
            var figures = inbox.Changes.Select(c => c.ItemId).First();
            fake.Delete(new FakeExchange.Item(figures, inbox.Id, "Message", "IPM.Note"));
            var ex = await Assert.ThrowsExactlyAsync<VfsException>(() => OutlookFixtures.ReadAsync(source, $"{folder}/message.eml"));
            Assert.AreEqual(VfsError.NotFound, ex.Error);

            fake.Intercept = (_, _) =>
            {
                var refused = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                refused.Headers.WwwAuthenticate.ParseAdd("Bearer");
                return refused;
            };
            // March's other message came with the figures above; April's is asked for now
            var other = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/04", "Fwd: Photo from the site visit");
            var refusedTwice = await Assert.ThrowsExactlyAsync<VfsException>(() => OutlookFixtures.ReadAsync(source, $"{other}/message.eml"));
            Assert.AreEqual(VfsError.Unavailable, refusedTwice.Error);
            Assert.AreEqual(ComponentHealth.Degraded, context.LastHealth);
        }
    }

    [TestMethod]
    public void TheTokensClaimsAreRead()
    {
        static string Jwt(string payloadJson) => "h." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".s";
        var (account, expires) = OwaTokenSession.Claims(Jwt("""{"upn":"jane@contoso.com","exp":1800000000}"""));
        Assert.AreEqual("jane@contoso.com", account);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1800000000), expires);
        Assert.AreEqual(("jane@contoso.com", (DateTimeOffset?)null), OwaTokenSession.Claims(Jwt("""{"preferred_username":"jane@contoso.com"}""")));
        Assert.AreEqual(((string?)null, (DateTimeOffset?)null), OwaTokenSession.Claims("not a token"));
    }

    // far longer than a wait that ends takes: only one that would go on to its ten minutes runs into it
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static OwaTokenSession TokenSession() => new(new Uri("https://outlook.test"), "msedge.exe", Path.Combine(Path.GetTempPath(), "anyfs-unused-profile"), NullLogger.Instance);

    /// <summary>The wait for a token no call carries, the page at <paramref name="page"/>, with its ten minutes ahead of it; no browser is launched.</summary>
    private static Task<OwaTokenSession.Capture> Wait(OwaTokenSession session, bool headless, Func<bool> noPage, Task gone, string page = "https://login.microsoftonline.com/common/oauth2/authorize")
        => session.WaitAsync(headless, TimeSpan.FromMinutes(10), () => null, () => page, noPage, gone, CancellationToken.None);

    [TestMethod]
    public async Task AWindowThatIsClosedEndsTheWaitAtOnce()
    {
        // the person closes the sign-in window and the browser quits with it, which ends its DevTools connection. Until
        // 22-Nov-2026 the wait slept between its looks and looked at nothing that told it: ten minutes of "Sign in to
        // ... in the window that opened", the profile held all the while
        var session = TokenSession();
        var gone = new TaskCompletionSource();
        var wait = Wait(session, headless: false, noPage: () => false, gone.Task);
        Assert.IsFalse(wait.IsCompleted, "waited on while the browser is there");

        gone.SetResult();
        var capture = await wait.WaitAsync(Patience);
        Assert.AreEqual($"the {session.BrowserName} window was closed before the sign-in to outlook.test finished; Retry to sign in", capture.Failure);
        Assert.IsTrue(capture.Final, "the person's answer: no window opens again until they ask for one");
        Assert.IsNull(capture.Token);
    }

    [TestMethod]
    public async Task AWindowWhoseLastPageHasGoneEndsTheWaitThoughTheBrowserStaysUp()
    {
        // Edge can stay up in the background with its window closed: the connection holds, and no page is left
        var session = TokenSession();
        var pages = new DevToolsPages();
        pages.On("Target.attachedToTarget", new JsonObject { ["sessionId"] = "S1", ["targetInfo"] = new JsonObject { ["targetId"] = "T1", ["type"] = "page", ["url"] = "https://login.microsoftonline.com/common/oauth2/authorize" } });
        var wait = session.WaitAsync(headless: false, TimeSpan.FromMinutes(10), () => null, () => pages.Where, () => !pages.Any, new TaskCompletionSource().Task, CancellationToken.None);
        Assert.IsFalse(wait.IsCompleted, "waited on while its page is there");

        pages.On("Target.detachedFromTarget", new JsonObject { ["sessionId"] = "S1" });
        var capture = await wait.WaitAsync(Patience);
        Assert.AreEqual($"the {session.BrowserName} window was closed before the sign-in to outlook.test finished; Retry to sign in", capture.Failure);
        Assert.IsTrue(capture.Final);
    }

    [TestMethod]
    public async Task ABrowserWithoutAWindowThatQuitsEndsTheWaitAtOnce()
    {
        var session = TokenSession();
        var gone = new TaskCompletionSource();
        var wait = Wait(session, headless: true, noPage: () => false, gone.Task, page: "https://outlook.test/mail/");
        Assert.IsFalse(wait.IsCompleted, "waited on while the browser is there");

        gone.SetResult();
        var capture = await wait.WaitAsync(Patience);
        Assert.AreEqual($"{session.BrowserName} quit before the sign-in was read", capture.Failure);
        Assert.IsFalse(capture.Final, "nothing the person did: a window may still be opened for them");
        Assert.IsNull(capture.Token);
    }

    [TestMethod]
    public async Task ATokenReadEndsTheWaitWhateverBecameOfTheWindow()
    {
        // the person signs in and closes the window as the mailbox loads: the token its calls carried is the answer
        var session = TokenSession();
        var capture = await session.WaitAsync(headless: false, TimeSpan.FromMinutes(10), () => "a-token", () => "https://outlook.test/mail/", () => true, Task.CompletedTask, CancellationToken.None).WaitAsync(Patience);
        Assert.AreEqual(("a-token", (string?)null), (capture.Token, capture.Failure));
    }

    [TestMethod]
    public void TheCloudComesFromTheChoiceOrTheAddress()
    {
        Assert.AreEqual(new Uri("https://outlook.office.com"), MicrosoftCloud.FromName(MicrosoftCloud.GlobalName)!.OutlookOrigin);
        Assert.AreEqual(new Uri("https://outlook.office365.us"), MicrosoftCloud.FromName(MicrosoftCloud.UsGovernmentName)!.OutlookOrigin);
        Assert.IsNull(MicrosoftCloud.FromName(MicrosoftCloud.AutomaticName));
        Assert.AreSame(MicrosoftCloud.UsGovernment, MicrosoftCloud.FindAsync("jane@contoso.onmicrosoft.us", null, CancellationToken.None).Result, "a .onmicrosoft.us address needs no lookup");
    }

    /// <summary>The sandbox mailbox through a real browser profile: set ANYFS_TEST_OWA_MAILBOX (the address) and ANYFS_OUTLOOK_PROFILES (where a signed-in profile for it is); ANYFS_TEST_OWA_LOG names a file for the source's log.</summary>
    [TestMethod]
    public async Task TheSandboxMailboxIsServedThroughTheWebSession()
    {
        var address = Environment.GetEnvironmentVariable("ANYFS_TEST_OWA_MAILBOX");
        if (string.IsNullOrEmpty(address) || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANYFS_OUTLOOK_PROFILES")))
        {
            Assert.Inconclusive("set ANYFS_TEST_OWA_MAILBOX and ANYFS_OUTLOOK_PROFILES to run against a signed-in profile");
        }

        using var context = new TestSourceContext("outlook", Environment.GetEnvironmentVariable("ANYFS_TEST_OWA_LOG") is { Length: > 0 } log ? new SpoTestServer.FileLogger(log) : null);
        await using var source = new OutlookSource(new OutlookSettings { Path = address!, RefreshMinutes = 0, CalendarMonthsBack = 12, CalendarMonthsAhead = 3 });
        await source.InitialiseAsync(context, CancellationToken.None);
        var root = (await OutlookFixtures.ListAsync(source, "")).Select(e => e.Name).ToList();
        CollectionAssert.Contains(root, "Inbox");
        foreach (var folder in root.Where(n => n != "Address Books"))
        {
            Assert.IsTrue((await OutlookFixtures.ListAsync(source, folder)).Count > 0, $"{folder}: a folder shown has something in it");
        }
        var years = await OutlookFixtures.ListAsync(source, "Inbox");
        Assert.IsTrue(years.Count > 0);
        var months = await OutlookFixtures.ListAsync(source, VfsPath.Join("Inbox", years.First(e => e.Name.Length == 4).Name));
        var items = await OutlookFixtures.ListAsync(source, VfsPath.Join(VfsPath.Join("Inbox", years.First(e => e.Name.Length == 4).Name), months[0].Name));
        Assert.IsTrue(items.Count > 0);
        var first = VfsPath.Join(VfsPath.Join(VfsPath.Join("Inbox", years.First(e => e.Name.Length == 4).Name), months[0].Name), items[0].Name);
        var files = await OutlookFixtures.ListAsync(source, first);
        var eml = files.Single(f => f.Name.EndsWith(".eml", StringComparison.Ordinal));
        var text = await OutlookFixtures.TextAsync(source, VfsPath.Join(first, eml.Name));
        StringAssert.Contains(text, "\r\nSubject: ");

        // the month's messages listed sixteen to a GetItem, their attachments in GetAttachments: what each folder lists is
        // what its files come to when read
        var month = VfsPath.Join(VfsPath.Join("Inbox", years.First(e => e.Name.Length == 4).Name), months[0].Name);
        foreach (var folder in items.Where(e => e.IsDirectory).Take(40).Select(e => VfsPath.Join(month, e.Name)))
        {
            var listed = await OutlookFixtures.ListAsync(source, folder);
            var inside = listed.Where(f => !f.IsDirectory).Select(f => (Path: VfsPath.Join(folder, f.Name), f.Length)).ToList();
            if (listed.Any(f => f.IsDirectory && f.Name == "attachments"))
            {
                inside.AddRange((await OutlookFixtures.ListAsync(source, folder + "/attachments")).Select(f => (Path: VfsPath.Join(folder + "/attachments", f.Name), f.Length)));
            }

            foreach (var (path, length) in inside)
            {
                Assert.AreEqual(length, (await OutlookFixtures.ReadAsync(source, path)).Length, path);
            }
        }

        // Count files on start: every item listed, every list read, the count whole
        await ((ICountable)source).CountFilesAsync(CancellationToken.None);
        Assert.AreEqual(EnumerationState.Complete, source.Counters.State, "every item listed");
        Assert.IsTrue(source.Counters.FilesKnown > source.Tree.FileCount, $"{source.Counters.FilesKnown:N0} files, {source.Tree.FileCount:N0} without the attachments and the address books");

        // the organisation's address lists (the sandbox's: the Default Global Address List and nine more, Spike 11)
        CollectionAssert.Contains(root, "Address Books");
        CollectionAssert.Contains((await OutlookFixtures.ListAsync(source, "Address Books")).Select(e => e.Name).ToList(), "Default Global Address List");
        var cards = await OutlookFixtures.ListAsync(source, "Address Books/Default Global Address List");
        Assert.IsTrue(cards.Count > 0, "the sandbox's global list has people");
        var card = await OutlookFixtures.TextAsync(source, "Address Books/Default Global Address List/" + cards[0].Name);
        StringAssert.StartsWith(card, "BEGIN:VCARD\r\n");
        StringAssert.Contains(card, "\r\nEMAIL;TYPE=INTERNET,PREF:");
        Assert.AreEqual(cards[0].Length, Encoding.UTF8.GetByteCount(card));

        // the sandbox's people have titles, departments and offices: fields Exchange Online gives only when asked for
        var texts = new List<string>();
        foreach (var entry in cards)
        {
            texts.Add(await OutlookFixtures.TextAsync(source, "Address Books/Default Global Address List/" + entry.Name));
        }

        Assert.IsTrue(texts.Any(t => Line(t, "TITLE:") is not null), "a card with a title");
        Assert.IsTrue(texts.Any(t => Line(t, "ORG:")?.Contains(';', StringComparison.Ordinal) == true), "a card with a department (ORG's second part)");
        Assert.IsTrue(texts.Any(t => Line(t, "ADR;TYPE=WORK:")?.Split(';')[2] is { Length: > 0 }), "a card with an office (the work address's extended part)");
    }

    /// <summary>A card's line that starts so, unfolded; null for none.</summary>
    private static string? Line(string card, string start)
        => card.Replace("\r\n ", "", StringComparison.Ordinal).Split("\r\n").FirstOrDefault(l => l.StartsWith(start, StringComparison.Ordinal));
}
