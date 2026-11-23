using System.Net;
using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Common;
using AnyFS.Sources.Outlook;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Tests;

/// <summary>
/// An item's folder listed from what its files came to when last made (spec 7.7: names, lengths and dates, kept between
/// starts) while the item is as it was then, and an item's neighbours opened with it, over the fake's Outlook on the web.
/// </summary>
[TestClass]
public class OutlookItemListingTests
{
    private const string May = "Inbox/2026/05";

    private static OutlookSettings Settings() => new() { Path = FakeExchange.Account, RefreshMinutes = 0, CalendarMonthsBack = 12, CalendarMonthsAhead = 3 };

    private static async Task<OutlookSource> StartAsync(FakeExchange fake, TestSourceContext context)
    {
        var source = new OutlookSource(Settings(), fake.StandIn());
        await source.InitialiseAsync(context, CancellationToken.None);
        return source;
    }

    /// <summary>The fake's mailbox with twenty messages in May 2026, each with a file of the day's number of bytes attached.</summary>
    private static FakeExchange WithMay(out List<FakeExchange.Item> may)
    {
        var fake = FakeExchange.Mailbox();
        var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
        may = [];
        for (var day = 1; day <= 20; day++)
        {
            may.Add(fake.AddMessage(inbox, $"Report {day:D2}", ("Adele Vance", "adelev@fake.test"), new DateTimeOffset(2026, 5, day, 9, 0, 0, TimeSpan.Zero), $"<p>Day {day}.</p>",
                [new FakeExchange.Attachment($"day-{day:D2}.txt", "text/plain", Encoding.ASCII.GetBytes(new string('x', day)), false, null)]));
        }

        return fake;
    }

    private static async Task<List<string>> ItemFoldersAsync(ISource source, string month) => [.. (await OutlookFixtures.ListAsync(source, month)).Select(e => VfsPath.Join(month, e.Name))];

    private static List<string> Shown(IEnumerable<VfsEntry> entries) => [.. entries.Select(e => $"{e.Name} {e.Length} {e.Modified:O} {e.Created:O}")];

    private static int Count(FakeExchange fake, string action)
    {
        lock (fake.Actions)
        {
            return fake.Actions.Count(a => a == action);
        }
    }

    [TestMethod]
    public async Task AnItemsFolderIsListedWithItsNeighboursInOneRequest()
    {
        var fake = WithMay(out _);
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            // newest first: the 20th, the 19th...
            var folders = await ItemFoldersAsync(source, May);
            Assert.AreEqual(20, folders.Count);
            CollectionAssert.AreEquivalent(new[] { "message.md", "message.eml", "attachments" }, (await OutlookFixtures.ListAsync(source, folders[0])).Select(e => e.Name).ToList());

            // the folder with the fifteen after it in one request, and the next sixteen (the four left) ahead in another; nobody
            // waits for those, and their attachments are the last thing they ask for
            for (var i = 0; i < 200 && fake.AttachmentsFetched < 20; i++)
            {
                await Task.Delay(10);
            }

            CollectionAssert.AreEquivalent(new[] { OutlookSource.BatchItems, 20 - OutlookSource.BatchItems }, fake.ItemsPerRequest, "the items each GetItem asked for");
            Assert.AreEqual(2, Count(fake, "GetAttachment"), "their attachments in one more each");
            Assert.AreEqual(20, fake.AttachmentsFetched);
            for (var i = 0; i < folders.Count; i++)
            {
                var attachment = (await OutlookFixtures.ListAsync(source, folders[i] + "/attachments")).Single();
                Assert.AreEqual(20 - i, attachment.Length, $"{attachment.Name}: the content's length");
            }

            StringAssert.Contains(await OutlookFixtures.TextAsync(source, folders[3] + "/message.eml"), "Subject: Report 17", "a file read is the one made with the listing");
            Assert.AreEqual(2, Count(fake, "GetItem"), "every folder of the month, and a file, from those two");
        }
    }

    [TestMethod]
    public async Task ListingsAreKeptBetweenStartsAndAChangedItemIsListedAfresh()
    {
        var fake = WithMay(out var may);
        byte[] listings;
        byte[] mailbox;
        List<VfsEntry> before;
        using (var first = new TestSourceContext("outlook"))
        {
            var source = await StartAsync(fake, first);
            await using (source)
            {
                foreach (var folder in await ItemFoldersAsync(source, May))
                {
                    await OutlookFixtures.ListAsync(source, folder);
                }

                before = await OutlookFixtures.ListAsync(source, await OutlookFixtures.ItemFolderAsync(source, May, "Report 07"));
            }

            listings = await File.ReadAllBytesAsync(Path.Combine(first.StateDirectory, ItemListings.FileName));
            mailbox = await File.ReadAllBytesAsync(Path.Combine(first.StateDirectory, MailboxIndexFile.FileName));
        }

        lock (fake.Actions)
        {
            fake.Actions.Clear();
        }

        var fetched = fake.ItemsFetched;
        using var second = new TestSourceContext("outlook");
        await File.WriteAllBytesAsync(Path.Combine(second.StateDirectory, ItemListings.FileName), listings);
        await File.WriteAllBytesAsync(Path.Combine(second.StateDirectory, MailboxIndexFile.FileName), mailbox);
        var restarted = await StartAsync(fake, second);
        await using (restarted)
        {
            foreach (var folder in await ItemFoldersAsync(restarted, May))
            {
                await OutlookFixtures.ListAsync(restarted, folder);
                await OutlookFixtures.ListAsync(restarted, folder + "/attachments");
            }

            Assert.AreEqual(0, Count(fake, "GetItem") + Count(fake, "GetAttachment"), "every folder listed as kept");
            var report07 = await OutlookFixtures.ItemFolderAsync(restarted, May, "Report 07");
            CollectionAssert.AreEqual(Shown(before), Shown(await OutlookFixtures.ListAsync(restarted, report07)), "names, lengths and dates as they were");

            // changed on the server: a longer subject, so a longer Markdown; asked for alone, its neighbours as they were
            fake.Update(may[6], m => m.Subject = "Report 07, revised");
            await restarted.RefreshAsync(CancellationToken.None);
            var revised = await OutlookFixtures.ItemFolderAsync(restarted, May, "Report 07, revised");
            var after = await OutlookFixtures.ListAsync(restarted, revised);
            Assert.AreEqual(1, Count(fake, "GetItem"));
            Assert.AreEqual(fetched + 1, fake.ItemsFetched, "the changed item alone");
            Assert.IsTrue(after.Single(e => e.Name == "message.md").Length > before.Single(e => e.Name == "message.md").Length, "listed afresh");
            Assert.AreEqual(Encoding.UTF8.GetByteCount(await OutlookFixtures.TextAsync(restarted, revised + "/message.md")), after.Single(e => e.Name == "message.md").Length);
        }
    }

    [TestMethod]
    public async Task ANameAnItemsFolderCannotHoldIsNotLookedFor()
    {
        var fake = FakeExchange.Mailbox();
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            var figures = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/03", "Quarterly figures");
            Assert.IsNull(await source.StatAsync(figures + "/desktop.ini", CancellationToken.None));
            Assert.IsNull(await source.StatAsync(figures + "/folder.jpg", CancellationToken.None));
            Assert.IsNull(await source.StatAsync(figures + "/message.md/inside", CancellationToken.None));
            Assert.AreEqual(0, Count(fake, "GetItem"), "Explorer's probes open nothing");

            Assert.AreEqual(27, (await source.StatAsync(figures + "/attachments/figures.csv", CancellationToken.None))!.Length);
            Assert.IsNotNull(await source.StatAsync(figures + "/message.eml", CancellationToken.None));
            Assert.AreEqual(1, Count(fake, "GetItem"));
        }
    }

    [TestMethod]
    public async Task AKeptLengthThatIsOutIsPutRightWhenTheItemIsOpened()
    {
        var fake = FakeExchange.Mailbox();
        byte[] kept;
        string figures;
        using (var first = new TestSourceContext("outlook"))
        {
            var source = await StartAsync(fake, first);
            await using (source)
            {
                figures = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/03", "Quarterly figures");
                await OutlookFixtures.ListAsync(source, figures);
            }

            // as a change to how the Markdown is made would leave them: each message.md kept five bytes short
            var path = Path.Combine(first.StateDirectory, ItemListings.FileName);
            var index = StateFile.Load(path, ItemListingsJson.Default.ItemListingIndex) ?? throw new AssertFailedException("the listings were kept");
            StateFile.Save(path, index with { Items = [.. index.Items.Select(i => i with { Files = [.. i.Files.Select(f => f.Name == "message.md" ? f with { Length = f.Length - 5 } : f)] })] }, ItemListingsJson.Default.ItemListingIndex);
            kept = await File.ReadAllBytesAsync(path);
        }

        using var second = new TestSourceContext("outlook");
        await File.WriteAllBytesAsync(Path.Combine(second.StateDirectory, ItemListings.FileName), kept);
        var restarted = await StartAsync(fake, second);
        await using (restarted)
        {
            var listed = (await OutlookFixtures.ListAsync(restarted, figures)).Single(e => e.Name == "message.md").Length;
            var markdown = await OutlookFixtures.ReadAsync(restarted, figures + "/message.md");
            Assert.AreEqual(listed + 5, markdown.Length, "listed as kept, five short");
            Assert.AreEqual(markdown.Length, (await OutlookFixtures.ListAsync(restarted, figures)).Single(e => e.Name == "message.md").Length, "put right when the item was opened");
        }
    }

    // --- one thing the server will not give is that thing's alone (the review of 21-Nov-2026, R12) -----------------------

    /// <summary>Exchange's refusal of one call, as it answers one: at 200, the error in the response message.</summary>
    private static HttpResponseMessage Refused(string action, string code = "ErrorAccessDenied", string text = "Access is denied. Check credentials and try again.")
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"Header":{"ServerVersionInfo":{"MajorVersion":15,"MinorVersion":21,"Version":"V2018_01_08"}},"Body":{"__type":"@ACTION@Response:#Exchange","ResponseMessages":{"Items":[{"__type":"@ACTION@ResponseMessage:#Exchange","ResponseClass":"Error","ResponseCode":"@CODE@","MessageText":"@TEXT@"}]}}}""".Replace("@ACTION@", action, StringComparison.Ordinal).Replace("@CODE@", code, StringComparison.Ordinal).Replace("@TEXT@", text, StringComparison.Ordinal),
                Encoding.UTF8, "application/json"),
        };

    private static bool Names(HttpRequestMessage request, string id) => request.Content!.ReadAsStringAsync().Result.Contains(id, StringComparison.Ordinal);

    [TestMethod]
    public async Task AFolderTheServerRefusesIsThatFoldersAloneNotTheMailboxs()
    {
        // a mailbox's folders were read one after another with nothing to catch one that failed: a folder the server
        // refused the account meant no start at all
        var fake = WithMay(out _);
        var sent = fake.Folders.Values.Single(f => f.Distinguished == "sentitems");
        fake.Intercept = (request, action) => action == "SyncFolderItems" && Names(request, sent.Id) ? Refused(action) : null;
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            var root = (await OutlookFixtures.ListAsync(source, "")).Select(e => e.Name).ToList();
            CollectionAssert.Contains(root, "Inbox");
            CollectionAssert.Contains(root, "Sent Items", "the folder shows: that is where it says it could not be read");
            Assert.AreEqual(20, (await ItemFoldersAsync(source, May)).Count, "the other folders are read as ever");

            var ex = await Assert.ThrowsExactlyAsync<VfsException>(() => OutlookFixtures.ListAsync(source, "Sent Items"));
            Assert.AreEqual(VfsError.AccessDenied, ex.Error);
            StringAssert.Contains(ex.Message, "Access is denied");
        }
    }

    [TestMethod]
    public async Task AMailboxOfWhichNoFolderCanBeReadDoesNotStart()
    {
        // every folder refused is the mailbox refused: the start says so, in place of a tree of folders that each say it
        var fake = WithMay(out _);
        fake.Intercept = (_, action) => action is "SyncFolderItems" or "FindItem" ? Refused(action) : null;
        using var context = new TestSourceContext("outlook");
        var source = new OutlookSource(Settings(), fake.StandIn());
        await using (source)
        {
            var ex = await Assert.ThrowsAsync<Exception>(() => source.InitialiseAsync(context, CancellationToken.None));
            StringAssert.Contains(ex.Message, "Access is denied");
        }
    }

    [TestMethod]
    public async Task ARefreshGoesOnPastAFolderTheServerRefuses()
    {
        // the refresh synced the folders one after another and ended at the first that failed, at every refresh: no new
        // mail in any folder from then on
        var fake = WithMay(out _);
        var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
        var sent = fake.Folders.Values.Single(f => f.Distinguished == "sentitems");
        fake.AddMessage(sent, "Sent before", ("Megan Bowen", "meganb@fake.test"), new DateTimeOffset(2026, 5, 2, 9, 0, 0, TimeSpan.Zero), "<p>Sent.</p>", []);
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            Assert.AreEqual(1, (await ItemFoldersAsync(source, "Sent Items/2026/05")).Count);

            // both folders change; the server refuses one of them from now on
            fake.Intercept = (request, action) => action == "SyncFolderItems" && Names(request, sent.Id) ? Refused(action) : null;
            fake.AddMessage(sent, "Sent since", ("Megan Bowen", "meganb@fake.test"), new DateTimeOffset(2026, 5, 3, 9, 0, 0, TimeSpan.Zero), "<p>Sent.</p>", []);
            fake.AddMessage(inbox, "Arrived since", ("Adele Vance", "adelev@fake.test"), new DateTimeOffset(2026, 5, 21, 9, 0, 0, TimeSpan.Zero), "<p>New.</p>", []);
            await source.RefreshAsync(CancellationToken.None);

            Assert.AreEqual(21, (await ItemFoldersAsync(source, May)).Count, "the inbox has its new message");
            Assert.AreEqual(1, (await ItemFoldersAsync(source, "Sent Items/2026/05")).Count, "what was read of the refused folder stands");
        }
    }

    [TestMethod]
    public async Task AContactTheServerCannotGiveIsListedWithItsNeighbours()
    {
        // a folder of contacts lists each with its card's size, which is the card read: one the server failed on failed
        // the listing of the whole folder
        var fake = FakeExchange.Mailbox();
        var contacts = fake.Folders.Values.Single(f => f.Distinguished == "contacts");
        var bad = fake.AddContact(contacts, "Unreadable Card", "Unreadable", "Card", "card@fake.test", "Contoso", "None", "+1 555 0199");
        fake.Intercept = (request, action) => action == "GetItem" && Names(request, bad.Id) ? Refused(action, "ErrorItemCorrupt", "The item is corrupt.") : null;
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            var listed = await OutlookFixtures.ListAsync(source, "Contacts");
            CollectionAssert.Contains(listed.Select(e => e.Name).ToList(), "Adele Vance.vcf");
            CollectionAssert.Contains(listed.Select(e => e.Name).ToList(), "Unreadable Card.vcf", "listed, though its card cannot be made");
            Assert.IsTrue(listed.Single(e => e.Name == "Adele Vance.vcf").Length > 0, "its neighbours with their cards' sizes");

            var failed = await Assert.ThrowsExactlyAsync<VfsException>(() => OutlookFixtures.ReadAsync(source, "Contacts/Unreadable Card.vcf"));
            StringAssert.Contains(failed.Message, "corrupt");
        }
    }

    [TestMethod]
    public async Task ARequestThatTimesOutIsTriedAgainNotLeftAsACancellation()
    {
        // the client's own time limit came out as a cancellation nobody had asked for: not tried again, and not told as
        // a mailbox that did not answer
        var fake = WithMay(out _);
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            var timedOut = 0;
            fake.Intercept = (_, action) => action == "GetItem" && timedOut++ == 0 ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 120 seconds elapsing.") : null;
            var folders = await ItemFoldersAsync(source, May);
            StringAssert.Contains(await OutlookFixtures.TextAsync(source, $"{folders[0]}/message.eml"), "Subject: Report");
            Assert.IsTrue(timedOut >= 2, "asked again");
        }
    }
    [TestMethod]
    public async Task AnAttachmentTheServerFailsOnFailsItsOwnItemAlone()
    {
        var fake = FakeExchange.Mailbox();
        var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
        Attachment(fake, inbox, 1, "notes.txt");
        var bad = Attachment(fake, inbox, 2, "damaged.bin");
        Attachment(fake, inbox, 3, "minutes.txt");
        var poison = $"\"{bad.Attachments.Single().Id}\"";
        fake.Intercept = (request, action) => action == "GetAttachment" && request.Content!.ReadAsStringAsync().Result.Contains(poison, StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"Body":{"ErrorCode":500,"ExceptionName":"StoragePermanentException","FaultMessage":"The attachment could not be read.","IsTransient":false,"ResponseCode":"ErrorInternalServerError"}}""", Encoding.UTF8, "application/json") }
            : null;
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            var folders = await ItemFoldersAsync(source, "Inbox/2026/06");
            Assert.AreEqual("minutes.txt", (await OutlookFixtures.ListAsync(source, folders[0] + "/attachments")).Single().Name);
            var failed = await Assert.ThrowsExactlyAsync<VfsException>(() => OutlookFixtures.ListAsync(source, folders[1]));
            StringAssert.Contains(failed.Message, "could not be read");
            Assert.AreEqual("notes.txt", (await OutlookFixtures.ListAsync(source, folders[2] + "/attachments")).Single().Name, "its neighbours list");
        }
    }

    [TestMethod]
    public async Task AfterTheCountAPassOverEverythingAsksTheServerNothing()
    {
        var fake = WithMay(out _);
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            await ((ICountable)source).CountFilesAsync(CancellationToken.None);
            await UpstreamRequestRows.AllEndAsync(context);
            var requests = context.UpstreamRequests.Requests.Select(r => r.ToString()).ToList();
            var before = requests.Count;
            var folders = await ListEverything.OfAsync(source);
            Assert.IsTrue(folders > 20, folders.ToString());
            var after = context.UpstreamRequests.Requests.Select(r => r.ToString()).ToList();
            Assert.AreEqual(before, after.Count, "asked for after the walk: " + ListEverything.Since(after, before));
        }
    }

    [TestMethod]
    public async Task ACountsReportsKeepTheOrderOfItsCountWhileSeveralBatchesEndTogether()
    {
        // batches that end together report together, and a busy host takes reports made at once in no set order: each one
        // says no less than the one before, and the last says the whole count (CI, 6-Nov-2026: "9 of 29 items" came last)
        var fake = FakeExchange.Mailbox();
        var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
        for (var i = 0; i < 240; i++)
        {
            fake.AddMessage(inbox, $"Note {i:D3}", ("Adele Vance", "adelev@fake.test"), new DateTimeOffset(2026, 4, 1 + (i % 28), 9, 0, 0, TimeSpan.Zero), $"<p>Note {i}.</p>", []);
        }

        using var inner = new TestSourceContext("outlook");
        var context = new SlowReportsContext(inner);
        var settings = Settings();
        settings.Concurrency = 8;
        var source = new OutlookSource(settings, fake.StandIn());
        await source.InitialiseAsync(context, CancellationToken.None);
        await using (source)
        {
            await ((ICountable)source).CountFilesAsync(CancellationToken.None);
        }

        var said = context.Said("Counting files: ");
        Assert.IsTrue(said.Count >= 240 / OutlookSource.BatchItems, string.Join("; ", said.Select(s => s.Message)));
        for (var i = 1; i < said.Count; i++)
        {
            Assert.IsTrue(said[i].Fraction >= said[i - 1].Fraction, $"report {i} went back: " + string.Join("; ", said.Select(s => s.Message)));
        }

        Assert.AreEqual(1.0, said[^1].Fraction, said[^1].Message);
    }

    [TestMethod]
    public async Task CountingOnStartListsEveryItemAndKeepsNewMailListed()
    {
        var fake = WithMay(out _);
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            Assert.AreEqual(EnumerationState.Unknown, source.Counters.State, "so far: no message opened yet");
            Assert.AreEqual(source.Tree.FileCount, source.Counters.FilesKnown);

            await ((ICountable)source).CountFilesAsync(CancellationToken.None);
            Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
            Assert.AreEqual(source.Tree.FileCount + 3 + 20, source.Counters.FilesKnown, "every attachment: the mailbox's three and May's twenty");
            Assert.IsTrue(fake.ItemsPerRequest.All(n => n <= OutlookSource.BatchItems), string.Join(",", fake.ItemsPerRequest));
            List<(string Message, double? Fraction)> progress;
            lock (context.Progress)
            {
                progress = [.. context.Progress];
            }

            Assert.AreEqual(1.0, progress[^1].Fraction, "how far it got, to the end: " + progress[^1].Message);
            StringAssert.StartsWith(progress[^1].Message, "Counting files: ");

            // every folder is listed now without asking
            var asked = Count(fake, "GetItem");
            foreach (var folder in (await ItemFoldersAsync(source, May)).Concat(await ItemFoldersAsync(source, "Inbox/2026/03")))
            {
                await OutlookFixtures.ListAsync(source, folder);
            }

            Assert.IsNotNull(await source.StatAsync("Contacts/Adele Vance.vcf", CancellationToken.None));
            Assert.AreEqual(asked, Count(fake, "GetItem"));

            // new mail found by a refresh is listed behind it, and the count is whole again
            var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
            fake.AddMessage(inbox, "Late report", ("Adele Vance", "adelev@fake.test"), new DateTimeOffset(2026, 5, 28, 9, 0, 0, TimeSpan.Zero), "<p>Late.</p>",
                [new FakeExchange.Attachment("late.txt", "text/plain", Encoding.ASCII.GetBytes("late"), false, null)]);
            var before = source.Counters.FilesKnown;
            await source.RefreshAsync(CancellationToken.None);
            for (var i = 0; i < 200 && source.Counters.State != EnumerationState.Complete; i++)
            {
                await Task.Delay(10);
            }

            Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
            Assert.AreEqual(before + 3, source.Counters.FilesKnown, "its message.md, message.eml and attachment");
        }
    }

    [TestMethod]
    public async Task TheCountSaysWhichFolderOfTheMailboxItIsIn()
    {
        var fake = WithMay(out _);
        using var arrived = new SemaphoreSlim(0);
        using var held = new ManualResetEventSlim();
        fake.Intercept = (_, action) =>
        {
            if (action == "GetItem")
            {
                arrived.Release();
                held.Wait(TimeSpan.FromSeconds(30));
            }

            return null;
        };
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            Assert.IsNull(source.Counters.CurrentFolder, "no count yet");
            var counting = ((ICountable)source).CountFilesAsync(CancellationToken.None);
            try
            {
                Assert.IsTrue(await arrived.WaitAsync(TimeSpan.FromSeconds(10)), "a batch is under way");
                var folder = source.Counters.CurrentFolder;
                Assert.IsFalse(string.IsNullOrEmpty(folder), "a folder of the mailbox, while its items are listed");
                Assert.IsTrue((await source.StatAsync(folder, CancellationToken.None))?.IsDirectory, $"'{folder}' is a folder of the source");
            }
            finally
            {
                held.Set();
            }

            await counting;
            Assert.IsNull(source.Counters.CurrentFolder, "the count is over");
        }
    }

    [TestMethod]
    public async Task TheCountShowsEachBatchOfAFolderItGoesThrough()
    {
        var fake = WithMay(out _);
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            var watcher = new RecordingFolderWatcher();
            source.Counters.FolderWatcher = watcher;
            await ((ICountable)source).CountFilesAsync(CancellationToken.None);
            var lines = watcher.Lines.ToList();
            var listed = lines.Where(l => l.StartsWith($"listed {May} ", StringComparison.Ordinal)).ToList();
            Assert.IsTrue(listed.Count >= 2, string.Join("; ", lines));
            Assert.AreEqual(20, listed.Sum(l => int.Parse(l.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture)), "May's twenty messages: " + string.Join("; ", listed));
            Assert.AreEqual(0, lines.Count(l => l.StartsWith("failed ", StringComparison.Ordinal)), string.Join("; ", lines));
        }
    }

    /// <summary>
    /// A count cut short as the source stops (the window closed while two mailboxes were counting, 26-Oct-2026) waits for
    /// the batches it had started: they gave their slots back after the pass had let the slots go, and each of those
    /// failures was left to the finalizer ("a background task failed and nothing awaited it").
    /// </summary>
    [TestMethod]
    public async Task ACountCutShortWaitsForTheBatchesItStarted()
    {
        var fake = WithMay(out _);
        using var arrived = new SemaphoreSlim(0);
        using var held = new ManualResetEventSlim();
        fake.Intercept = (_, action) =>
        {
            if (action == "GetItem")
            {
                arrived.Release();
                held.Wait(TimeSpan.FromSeconds(30));
            }

            return null;
        };
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            using var cut = new CancellationTokenSource();
            var counting = ((ICountable)source).CountFilesAsync(cut.Token);
            Assert.IsTrue(await arrived.WaitAsync(TimeSpan.FromSeconds(10)), "a batch is under way");
            await cut.CancelAsync();
            await Task.Delay(300);
            Assert.IsFalse(counting.IsCompleted, "the count waits for the batches it started");

            held.Set();
            await Assert.ThrowsAsync<OperationCanceledException>(() => counting);
        }
    }

    [TestMethod]
    public async Task ADataFilesCountTakesInEveryAttachmentAndIsWholeAtTheNextStart()
    {
        var settings = new OutlookSettings { Path = OutlookFixtures.Pst("unicode.pst"), RefreshMinutes = 0 };
        byte[] listings;
        long counted;
        using (var first = new TestSourceContext("outlook"))
        {
            var source = new OutlookSource(settings);
            await source.InitialiseAsync(first, CancellationToken.None);
            await using (source)
            {
                Assert.AreEqual(EnumerationState.Unknown, source.Counters.State);
                await ((ICountable)source).CountFilesAsync(CancellationToken.None);
                Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
                counted = source.Counters.FilesKnown;
                Assert.IsTrue(counted > source.Tree.FileCount, $"the attachments: {counted} of {source.Tree.FileCount} without");
            }

            listings = await File.ReadAllBytesAsync(Path.Combine(first.StateDirectory, ItemListings.FileName));
        }

        using var second = new TestSourceContext("outlook");
        await File.WriteAllBytesAsync(Path.Combine(second.StateDirectory, ItemListings.FileName), listings);
        var restarted = new OutlookSource(settings);
        await restarted.InitialiseAsync(second, CancellationToken.None);
        await using (restarted)
        {
            Assert.AreEqual(EnumerationState.Complete, restarted.Counters.State, "every item's listing kept");
            Assert.AreEqual(counted, restarted.Counters.FilesKnown);
        }
    }

    [TestMethod]
    public async Task AMessageThatWillNotListLeavesTheCountSoFar()
    {
        var fake = FakeExchange.Mailbox();
        var inbox = fake.Folders.Values.Single(f => f.Distinguished == "inbox");
        var bad = Attachment(fake, inbox, 2, "damaged.bin");
        var poison = $"\"{bad.Attachments.Single().Id}\"";
        fake.Intercept = (request, action) => action == "GetAttachment" && request.Content!.ReadAsStringAsync().Result.Contains(poison, StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"Body":{"ErrorCode":500,"ExceptionName":"StoragePermanentException","FaultMessage":"The attachment could not be read.","IsTransient":false,"ResponseCode":"ErrorInternalServerError"}}""", Encoding.UTF8, "application/json") }
            : null;
        using var context = new TestSourceContext("outlook");
        var source = await StartAsync(fake, context);
        await using (source)
        {
            await ((ICountable)source).CountFilesAsync(CancellationToken.None);
            Assert.AreEqual(EnumerationState.Unknown, source.Counters.State, "one message did not list: the count is so far");
            Assert.AreEqual(source.Tree.FileCount + 3, source.Counters.FilesKnown, "the rest's attachments are counted");
        }
    }

    /// <summary>The items' files kept a while are trimmed, and forgotten at a refresh, as others are kept: a sort or a copy of the dictionary itself threw as it grew.</summary>
    [TestMethod]
    public async Task FilesKeptByManyThreadsAtOnceAreTrimmedWithoutAFault()
    {
        const int Threads = 4;
        const int Each = 2_000;
        static MailItemSummary Summary(string id) => new(id, MailItemKind.Message, "Hello", "Jane", null, null, null, 0, false);
        var item = new MailItem { Summary = Summary("0"), MessageClass = "IPM.Note", BodyText = "Hi" };
        var files = await ItemFiles.BuildAsync(item, item.Summary, "x", CancellationToken.None);
        await using var source = new OutlookSource(new OutlookSettings());
        var faults = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var go = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
        {
            go.Wait();
            try
            {
                for (var n = 1; n <= Each; n++)
                {
                    source.KeepFiles(Summary($"{t}-{n}"), files);
                    if (n % 500 == 0)
                    {
                        source.ForgetFiles();
                    }
                }
            }
            catch (Exception ex)
            {
                faults.Enqueue(ex);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        go.Set();
        threads.ForEach(t => t.Join());
        Assert.AreEqual(0, faults.Count, faults.FirstOrDefault()?.ToString());
        Assert.IsTrue(source.ItemsWithFilesKept <= OutlookSource.ItemsKept, $"{source.ItemsWithFilesKept:N0} items' files kept");
    }

    private static FakeExchange.Item Attachment(FakeExchange fake, FakeExchange.Folder inbox, int day, string name)
        => fake.AddMessage(inbox, $"Minutes {day}", ("Diego Siciliani", "diegos@fake.test"), new DateTimeOffset(2026, 6, day, 9, 0, 0, TimeSpan.Zero), "<p>Attached.</p>",
            [new FakeExchange.Attachment(name, "application/octet-stream", Encoding.ASCII.GetBytes(name), false, null)]);
}
