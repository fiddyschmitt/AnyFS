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

            // the folder with the fifteen after it in one request, and the next sixteen (the four left) ahead in another
            for (var i = 0; i < 200 && fake.ItemsFetched < 20; i++)
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

    private static FakeExchange.Item Attachment(FakeExchange fake, FakeExchange.Folder inbox, int day, string name)
        => fake.AddMessage(inbox, $"Minutes {day}", ("Diego Siciliani", "diegos@fake.test"), new DateTimeOffset(2026, 6, day, 9, 0, 0, TimeSpan.Zero), "<p>Attached.</p>",
            [new FakeExchange.Attachment(name, "application/octet-stream", Encoding.ASCII.GetBytes(name), false, null)]);
}
