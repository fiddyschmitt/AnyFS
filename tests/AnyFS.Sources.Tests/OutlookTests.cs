using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.Outlook;
using AnyFS.Sources.Outlook.Files;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Tests;

/// <summary>Where the Outlook fixtures are: LibOutlook's PSTs, copied in beside the tests.</summary>
internal static class OutlookFixtures
{
    public static string Pst(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "outlook", name);

    public static readonly byte[] FiguresCsv = Encoding.ASCII.GetBytes("quarter,amount\nQ3,42\nQ4,58\n");

    /// <summary>A started source over a fixture.</summary>
    public static async Task<(OutlookSource Source, TestSourceContext Context)> StartAsync(string name, bool hidden = false)
    {
        var context = new TestSourceContext("outlook");
        var source = new OutlookSource(new OutlookSettings { Path = Pst(name), ShowHidden = hidden, RefreshMinutes = 0 });
        await source.InitialiseAsync(context, CancellationToken.None);
        return (source, context);
    }

    public static async Task<List<VfsEntry>> ListAsync(ISource source, string path)
    {
        var list = new List<VfsEntry>();
        await foreach (var entry in source.ListAsync(path, CancellationToken.None))
        {
            list.Add(entry);
        }

        return list;
    }

    public static async Task<byte[]> ReadAsync(ISource source, string path)
    {
        await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
        var bytes = new byte[file.Length];
        var read = await file.ReadFullyAsync(0, bytes, CancellationToken.None);
        Assert.AreEqual(bytes.Length, read, $"{path}: read whole");
        return bytes;
    }

    public static async Task<string> TextAsync(ISource source, string path) => Encoding.UTF8.GetString(await ReadAsync(source, path));

    /// <summary>The item folder in a month folder whose name ends with the subject (as a folder name has it).</summary>
    public static async Task<string> ItemFolderAsync(ISource source, string monthPath, string subject)
    {
        var entries = await ListAsync(source, monthPath);
        var folder = entries.SingleOrDefault(e => e.IsDirectory && e.Name.EndsWith(" - " + MailNames.Replace(subject), StringComparison.Ordinal));
        Assert.IsNotNull(folder, $"a folder for '{subject}' in {monthPath}: {string.Join(", ", entries.Select(e => e.Name))}");
        return VfsPath.Join(monthPath, folder.Name);
    }
}

[TestClass]
public class OutlookPluginTests
{
    private readonly OutlookPlugin _plugin = new();

    [TestMethod]
    public void TheTypeIsOutlook()
    {
        Assert.AreEqual("outlook", _plugin.TypeId);
        Assert.AreEqual("outlook", _plugin.TypeFolder);
        Assert.AreEqual(typeof(OutlookSettings), _plugin.SettingsType);
        Assert.IsInstanceOfType<OutlookSettings>(_plugin.CreateDefaultSettings());
    }

    [TestMethod]
    public void TheFileNamesTheSource()
    {
        Assert.AreEqual("archive", _plugin.SuggestName(new OutlookSettings { Path = @"C:\Users\me\Documents\Outlook Files\archive.pst" }));
        Assert.AreEqual("jane@contoso.com", _plugin.SuggestName(new OutlookSettings { Path = @"C:\Users\jane\AppData\Local\Microsoft\Outlook\jane@contoso.com.ost" }));
        Assert.IsNull(_plugin.SuggestName(new OutlookSettings()));
        var settings = new OutlookSettings { Path = "  \"C:\\mail\\old.pst\"  " };
        _plugin.NormaliseSettings(settings);
        Assert.AreEqual(@"C:\mail\old.pst", settings.Path);
    }

    [TestMethod]
    public void ValidationNamesWhatIsWrong()
    {
        Assert.IsTrue(_plugin.Validate(new OutlookSettings()).Any(m => m.IsError && m.Property == nameof(OutlookSettings.Path)));
        var mailbox = _plugin.Validate(new OutlookSettings { Path = "jane@contoso.com" }).Single(m => m.IsError);
        StringAssert.Contains(mailbox.Message, "not served yet");
        StringAssert.Contains(_plugin.Validate(new OutlookSettings { Path = @"C:\notes.txt" }).Single(m => m.IsError).Message, ".pst or .ost");
        var missing = _plugin.Validate(new OutlookSettings { Path = @"C:\no\such\file.pst" }).ToList();
        Assert.IsTrue(missing.Any(m => !m.IsError && m.Property == nameof(OutlookSettings.Path)), "a missing file is a warning: it may be on a drive not plugged in");
        Assert.IsFalse(missing.Any(m => m.IsError));
        Assert.IsTrue(_plugin.Validate(new OutlookSettings { Path = OutlookFixtures.Pst("unicode.pst"), RefreshMinutes = -1 }).Any(m => m.IsError && m.Property == nameof(OutlookSettings.RefreshMinutes)));
        Assert.AreEqual(0, _plugin.Validate(new OutlookSettings { Path = OutlookFixtures.Pst("unicode.pst") }).Count());
    }

    [TestMethod]
    public async Task TheTestOpensTheFileAndSaysWhatItHolds()
    {
        var result = await _plugin.TestAsync(new OutlookSettings { Path = OutlookFixtures.Pst("unicode.pst") }, new Progress<string>(), CancellationToken.None);
        Assert.IsTrue(result.Success, result.Message);
        StringAssert.Contains(result.Message, "AnyFS fixture");
        StringAssert.Contains(result.Message, "Unicode PST");
        StringAssert.Contains(result.Message, "Inbox");

        var ansi = await _plugin.TestAsync(new OutlookSettings { Path = OutlookFixtures.Pst("ansi.pst") }, new Progress<string>(), CancellationToken.None);
        StringAssert.Contains(ansi.Message, "ANSI PST");

        var missing = await _plugin.TestAsync(new OutlookSettings { Path = @"C:\no\such\file.pst" }, new Progress<string>(), CancellationToken.None);
        Assert.IsFalse(missing.Success);
    }
}

/// <summary>The tree an Outlook source serves from a PST (spec 7.7), both fixtures.</summary>
[TestClass]
public class OutlookSourceTests
{
    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public async Task TheMailboxFoldersAreTheRoot(string fixture)
    {
        var (source, context) = await OutlookFixtures.StartAsync(fixture);
        await using (source)
        using (context)
        {
            var root = await OutlookFixtures.ListAsync(source, "");
            CollectionAssert.IsSubsetOf(new[] { "Inbox", "Contacts", "Calendar", "Deleted Items" }, root.Select(e => e.Name).ToList());
            Assert.IsTrue(root.All(e => e.IsDirectory));
            Assert.IsFalse(root.Any(e => e.Name == "Search Root"), "Outlook's own folders under the root are not the user's tree");

            var inbox = await OutlookFixtures.ListAsync(source, "Inbox");
            CollectionAssert.AreEquivalent(new[] { "2026", "Projects" }, inbox.Select(e => e.Name).ToList());
            CollectionAssert.AreEqual(new[] { "10" }, (await OutlookFixtures.ListAsync(source, "Inbox/2026")).Select(e => e.Name).ToList());

            var month = await OutlookFixtures.ListAsync(source, "inbox/2026/10");
            Assert.AreEqual(5, month.Count, string.Join(", ", month.Select(e => e.Name)));
            Assert.IsTrue(month.All(e => e.IsDirectory && e.Name.StartsWith("2026-10-2", StringComparison.Ordinal) && e.Name.Contains(" - anyfs-tester@y0w7606wv.onmicrosoft.com - ", StringComparison.Ordinal)),
                "a draft names no sender, so its first recipient stands in: " + string.Join(", ", month.Select(e => e.Name)));
            CollectionAssert.Contains(month.Select(e => e.Name[(e.Name.LastIndexOf(" - ", StringComparison.Ordinal) + 3)..]).ToList(), "Forwarded_ site visit", "a colon is no part of a folder name");
            Assert.IsTrue(month.All(e => e.Modified is not null), "item folders carry the item's date");

            Assert.AreEqual(EnumerationState.Complete, source.Counters.State);
            Assert.IsTrue(source.Counters.FilesKnown >= 13, $"{source.Counters.FilesKnown} files");
            Assert.AreEqual(ComponentHealth.Ready, context.LastHealth ?? ComponentHealth.Ready);
        }
    }

    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public async Task AMessageIsAFolderWithMarkdownEmlAndAttachments(string fixture)
    {
        var (source, context) = await OutlookFixtures.StartAsync(fixture);
        await using (source)
        using (context)
        {
            var folder = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/10", "Quarterly figures");
            var name = VfsPath.Name(folder);
            var files = await OutlookFixtures.ListAsync(source, folder);
            CollectionAssert.AreEquivalent(new[] { name + ".md", name + ".eml", "attachments" }, files.Select(e => e.Name).ToList());
            Assert.IsTrue(files.Single(e => e.Name == "attachments").IsDirectory);
            Assert.IsTrue(files.Where(e => !e.IsDirectory).All(e => e.Length > 100), "the files have their real sizes in the listing");

            var markdown = await OutlookFixtures.TextAsync(source, $"{folder}/{name}.md");
            StringAssert.StartsWith(markdown, "# Quarterly figures\n");
            Assert.IsFalse(markdown.Contains("**From:**", StringComparison.Ordinal), "a draft has no sender");
            StringAssert.Contains(markdown, "**To:** anyfs-tester@y0w7606wv.onmicrosoft.com");
            StringAssert.Contains(markdown, "**Importance:** high");
            StringAssert.Contains(markdown, "**Status:** draft, never sent");
            StringAssert.Contains(markdown, "[figures.csv](attachments/figures.csv) (27 bytes)");
            StringAssert.Contains(markdown, "\n---\n\nFigures attached.");

            var eml = await OutlookFixtures.TextAsync(source, $"{folder}/{name}.eml");
            StringAssert.Contains(eml, "\r\nSubject: Quarterly figures\r\n");
            StringAssert.Contains(eml, "\r\nX-Unsent: 1\r\n");
            StringAssert.Contains(eml, "\r\nImportance: High\r\n");
            StringAssert.Contains(eml, "Content-Type: multipart/mixed; boundary=");
            StringAssert.Contains(eml, "Content-Type: text/html; charset=utf-8");
            StringAssert.Contains(eml, "Content-Disposition: attachment; filename=\"figures.csv\"; size=27");
            StringAssert.Contains(eml, Convert.ToBase64String(OutlookFixtures.FiguresCsv));
            StringAssert.Contains(eml, "<p>Figures attached.</p>");

            var attachments = await OutlookFixtures.ListAsync(source, $"{folder}/attachments");
            Assert.AreEqual(1, attachments.Count);
            Assert.AreEqual(("figures.csv", 27L, false), (attachments[0].Name, attachments[0].Length, attachments[0].IsDirectory));
            CollectionAssert.AreEqual(OutlookFixtures.FiguresCsv, await OutlookFixtures.ReadAsync(source, $"{folder}/attachments/figures.csv"));

            var stat = await source.StatAsync($"{folder}/attachments/figures.csv", CancellationToken.None);
            Assert.AreEqual(27, stat!.Length);
            Assert.IsNull(await source.StatAsync($"{folder}/attachments/nothing.csv", CancellationToken.None));
            Assert.IsNull(await source.StatAsync("Inbox/2026/10/no such message", CancellationToken.None));
        }
    }

    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public async Task InlineImagesAndAttachedMessagesAreInTheAttachmentsFolder(string fixture)
    {
        var (source, context) = await OutlookFixtures.StartAsync(fixture);
        await using (source)
        using (context)
        {
            var photo = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/10", "Photo from the site visit");
            var markdown = await OutlookFixtures.TextAsync(source, $"{photo}/{VfsPath.Name(photo)}.md");
            StringAssert.Contains(markdown, "![](attachments/dot.png)", "cid: rewritten to the attachments folder");
            var eml = await OutlookFixtures.TextAsync(source, $"{photo}/{VfsPath.Name(photo)}.eml");
            StringAssert.Contains(eml, "Content-Type: multipart/related; boundary=");
            StringAssert.Contains(eml, "Content-ID: <anyfs-dot>");
            StringAssert.Contains(eml, "Content-Disposition: inline; filename=\"dot.png\"; size=70");
            StringAssert.Contains(eml, "iVBORw0KGgo", "the PNG, base64");
            Assert.AreEqual(70, (await OutlookFixtures.ReadAsync(source, $"{photo}/attachments/dot.png")).Length);

            var forwarded = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/10", "Forwarded: site visit");
            var attachments = await OutlookFixtures.ListAsync(source, $"{forwarded}/attachments");
            Assert.AreEqual(1, attachments.Count);
            Assert.AreEqual("Photo from the site visit.eml", attachments[0].Name);
            var nested = await OutlookFixtures.TextAsync(source, $"{forwarded}/attachments/Photo from the site visit.eml");
            StringAssert.Contains(nested, "\r\nSubject: Photo from the site visit\r\n");
            StringAssert.Contains(nested, "Content-ID: <anyfs-dot>");
            var outer = await OutlookFixtures.TextAsync(source, $"{forwarded}/{VfsPath.Name(forwarded)}.eml");
            StringAssert.Contains(outer, "Content-Type: message/rfc822; name=\"Photo from the site visit.eml\"");
            StringAssert.Contains(outer, "\r\nSubject: Photo from the site visit\r\n", "the attached message is inside the .eml");
        }
    }

    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public async Task ALargeAttachmentIsReadInRanges(string fixture)
    {
        var (source, context) = await OutlookFixtures.StartAsync(fixture);
        await using (source)
        using (context)
        {
            var folder = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/10", "Large attachment");
            var path = $"{folder}/attachments/pattern.bin";
            Assert.AreEqual(300_000, (await source.StatAsync(path, CancellationToken.None))!.Length);
            await using var file = await source.OpenAsync(path, VfsOpenMode.Read, CancellationToken.None);
            var piece = new byte[1000];
            Assert.AreEqual(1000, await file.ReadFullyAsync(123_456, piece, CancellationToken.None));
            CollectionAssert.AreEqual(Enumerable.Range(123_456, 1000).Select(i => (byte)(i % 251)).ToArray(), piece);
            Assert.AreEqual(5, await file.ReadAsync(299_995, new byte[100], CancellationToken.None));
            Assert.AreEqual(0, await file.ReadAsync(300_000, new byte[100], CancellationToken.None));
            await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.WriteAsync(0, new byte[1], CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task TextAndRichTextBodiesAndUnicodeNames()
    {
        var (source, context) = await OutlookFixtures.StartAsync("unicode.pst");
        await using (source)
        using (context)
        {
            var note = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/10", "Plain note_ ümlauts & _quotes_ _ slashes_ kept_");
            var markdown = await OutlookFixtures.TextAsync(source, $"{note}/{VfsPath.Name(note)}.md");
            StringAssert.Contains(markdown, "# Plain note: ümlauts & \"quotes\" / slashes: kept?\n");
            StringAssert.Contains(markdown, "\n---\n\nName edge cases. naïve café – 日本語");
            var eml = await OutlookFixtures.TextAsync(source, $"{note}/{VfsPath.Name(note)}.eml");
            StringAssert.Contains(eml, "Subject: =?utf-8?B?", "a subject beyond ASCII is an encoded word");
            StringAssert.Contains(eml, "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable");
            StringAssert.Contains(eml, "na=C3=AFve caf=C3=A9", "UTF-8, quoted-printable");

            var kickOff = await OutlookFixtures.ItemFolderAsync(source, "Inbox/Projects/2026/10", "RE: Project kick-off");
            var rich = await OutlookFixtures.TextAsync(source, $"{kickOff}/{VfsPath.Name(kickOff)}.md");
            StringAssert.Contains(rich, "**To:** AnyFS tester <anyfs-tester@y0w7606wv.onmicrosoft.com>; adelev@example.test");
            StringAssert.Contains(rich, "**Cc:** megan.bowen@example.test");
            StringAssert.Contains(rich, "\n---\n\nIn a subfolder.", "the rich-text body as text");
            var richEml = await OutlookFixtures.TextAsync(source, $"{kickOff}/{VfsPath.Name(kickOff)}.eml");
            StringAssert.StartsWith(richEml, "To: AnyFS tester <anyfs-tester@y0w7606wv.onmicrosoft.com>,\r\n adelev@example.test\r\nCc: megan.bowen@example.test\r\nSubject: RE: Project kick-off\r\n");
            StringAssert.Contains(richEml, "\r\n\r\nIn a subfolder.\r\n", "the rich text as a text part, nothing after it");
            StringAssert.EndsWith(richEml, "In a subfolder.\r\n");
        }
    }

    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public async Task ContactsAreVCardsAndAppointmentsAreCalendars(string fixture)
    {
        var (source, context) = await OutlookFixtures.StartAsync(fixture);
        await using (source)
        using (context)
        {
            var contacts = await OutlookFixtures.ListAsync(source, "Contacts");
            Assert.AreEqual(1, contacts.Count);
            Assert.AreEqual("Adele Vance.vcf", contacts[0].Name);
            Assert.IsFalse(contacts[0].IsDirectory);
            Assert.IsTrue(contacts[0].Length > 50, "the card's real size");
            var card = await OutlookFixtures.TextAsync(source, "Contacts/Adele Vance.vcf");
            StringAssert.StartsWith(card, "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Adele Vance\r\nN:Vance;Adele;;;\r\n");
            StringAssert.Contains(card, "\r\nORG:Contoso\r\n");
            StringAssert.Contains(card, "\r\nTITLE:Retail Manager\r\n");
            StringAssert.Contains(card, "\r\nEMAIL;TYPE=INTERNET,PREF:adelev@example.test\r\n");
            StringAssert.EndsWith(card, "END:VCARD\r\n");
            Assert.AreEqual(card.Length, (await source.StatAsync("Contacts/Adele Vance.vcf", CancellationToken.None))!.Length);

            var months = await OutlookFixtures.ListAsync(source, "Calendar/2026");
            Assert.AreEqual(1, months.Count);
            var month = VfsPath.Join("Calendar/2026", months[0].Name);
            var review = await OutlookFixtures.ItemFolderAsync(source, month, "Design review");
            CollectionAssert.AreEquivalent(new[] { "event.md", "event.ics" }, (await OutlookFixtures.ListAsync(source, review)).Select(e => e.Name).ToList());
            var ics = await OutlookFixtures.TextAsync(source, $"{review}/event.ics");
            StringAssert.StartsWith(ics, "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n");
            StringAssert.Contains(ics, "\r\nSUMMARY:Design review\r\n");
            StringAssert.Contains(ics, "\r\nLOCATION:Room 4\r\n");
            StringAssert.Contains(ics, "\r\nDTSTART:2026");
            StringAssert.Contains(ics, "\r\nDTEND:2026");
            StringAssert.Contains(ics, "\r\nDESCRIPTION:Agenda: the Outlook source.");
            var md = await OutlookFixtures.TextAsync(source, $"{review}/event.md");
            StringAssert.Contains(md, "**When:** ");
            StringAssert.Contains(md, "**Where:** Room 4");
        }
    }

    [TestMethod]
    public async Task HiddenItemsShowOnlyWhenAsked()
    {
        var (plain, context) = await OutlookFixtures.StartAsync("unicode.pst");
        await using (plain)
        using (context)
        {
            Assert.IsFalse((await OutlookFixtures.ListAsync(plain, "")).Any(e => e.Name.StartsWith("Search", StringComparison.Ordinal)));
        }

        var (hidden, context2) = await OutlookFixtures.StartAsync("unicode.pst", hidden: true);
        await using (hidden)
        using (context2)
        {
            var root = await OutlookFixtures.ListAsync(hidden, "");
            Assert.IsTrue(root.Count >= 4);
        }
    }

    [TestMethod]
    public async Task AWrongFileFailsPermanentlyAndAMissingOneDoesNot()
    {
        using var context = new TestSourceContext("outlook");
        var wrong = new OutlookSource(new OutlookSettings { Path = "jane@contoso.com" });
        var ex = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => wrong.InitialiseAsync(context, CancellationToken.None));
        Assert.IsTrue(ex.Permanent);

        var missing = new OutlookSource(new OutlookSettings { Path = @"C:\no\such\file.pst" });
        var gone = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => missing.InitialiseAsync(context, CancellationToken.None));
        Assert.IsFalse(gone.Permanent);

        var temp = Path.GetTempFileName() + ".pst";
        File.WriteAllBytes(temp, new byte[8192]);
        try
        {
            var corrupt = new OutlookSource(new OutlookSettings { Path = temp });
            var bad = await Assert.ThrowsExactlyAsync<SourceFailedException>(() => corrupt.InitialiseAsync(context, CancellationToken.None));
            Assert.IsTrue(bad.Permanent);
            StringAssert.Contains(bad.Message, "not an Outlook data file");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>The OST capture, where it is on this machine (see LibOutlook's tests): a whole mailbox's tree.</summary>
    [TestMethod]
    public async Task AnOstServesTheMailboxTree()
    {
        var env = Environment.GetEnvironmentVariable("LIBOUTLOOK_TEST_OST");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AnyFS.slnx")))
        {
            dir = dir.Parent;
        }

        var ost = !string.IsNullOrEmpty(env) ? env : dir?.Parent is null ? null : Path.Combine(dir.Parent.FullName, "AnyFS local", "spikes", "Spike13.OstProbe", "fixtures", "anyfs-tester-clean.ost");
        if (ost is null || !File.Exists(ost))
        {
            Assert.Inconclusive("no OST capture on this machine");
        }

        using var context = new TestSourceContext("outlook");
        await using var source = new OutlookSource(new OutlookSettings { Path = ost!, RefreshMinutes = 0 });
        await source.InitialiseAsync(context, CancellationToken.None);
        var root = (await OutlookFixtures.ListAsync(source, "")).Select(e => e.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "Inbox", "Sent Items", "Drafts", "Deleted Items", "Calendar", "Contacts" }, root);
        CollectionAssert.DoesNotContain(root, "Conversation Action Settings", "Outlook's hidden folders are left out");
        var inboxMonths = await OutlookFixtures.ListAsync(source, "Inbox/2026");
        Assert.IsTrue(inboxMonths.Count > 0);
        var items = await OutlookFixtures.ListAsync(source, VfsPath.Join("Inbox/2026", inboxMonths[0].Name));
        Assert.IsTrue(items.Count > 0);
        var first = VfsPath.Join(VfsPath.Join("Inbox/2026", inboxMonths[0].Name), items[0].Name);
        var files = await OutlookFixtures.ListAsync(source, first);
        Assert.IsTrue(files.Any(f => f.Name.EndsWith(".eml", StringComparison.Ordinal)));
        var holidays = await OutlookFixtures.ListAsync(source, "Calendar/United States holidays");
        Assert.IsTrue(holidays.Count > 0, "a year folder per year of holidays");
    }
}

/// <summary>The read-only source contract over the Unicode fixture.</summary>
[TestClass]
public class OutlookContractTests : SourceContractTests
{
    protected override async Task<SourceContractSetup> SetUpAsync()
    {
        var (source, _) = await OutlookFixtures.StartAsync("unicode.pst");
        var folder = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/10", "Quarterly figures");
        return new SourceContractSetup(source, folder + "/attachments", folder + "/attachments/figures.csv", OutlookFixtures.FiguresCsv);
    }
}

[TestClass]
public class MailNamesTests
{
    [TestMethod]
    public void AMessageFolderIsDateSenderSubject()
    {
        var date = new DateTimeOffset(2026, 10, 24, 10, 57, 29, TimeSpan.Zero);
        var local = date.ToLocalTime().ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.AreEqual($"{local} - Adele Vance - Quarterly figures", MailNames.MessageFolder(date, "Adele Vance", "Quarterly figures"));
        Assert.AreEqual($"{local} - (no sender) - (no subject)", MailNames.MessageFolder(date, " ", ""));
        Assert.AreEqual("undated - A - B", MailNames.MessageFolder(null, "A", "B"));
        Assert.AreEqual($"{local} - a_b - c_d _ e_", MailNames.MessageFolder(date, "a/b", "c:d\r\n| e?"), "the characters a Windows name cannot hold, and line breaks squashed");
        Assert.IsTrue(MailNames.MessageFolder(date, "x", new string('y', 500)).Length <= MailNames.MaxFolderName);
        Assert.AreEqual("Adele Vance.vcf", MailNames.ContactFile("Adele Vance"));
        Assert.AreEqual("report (2).pdf", MailNames.NumberedFile("report.pdf", 2));
        Assert.AreEqual("name (2)", MailNames.NumberedFolder("name", 2));
        var (year, month) = MailNames.Period(date);
        Assert.AreEqual((date.ToLocalTime().Year.ToString("0000", System.Globalization.CultureInfo.InvariantCulture), date.ToLocalTime().Month.ToString("00", System.Globalization.CultureInfo.InvariantCulture)), (year, month));
        Assert.AreEqual(("undated", (string?)null), MailNames.Period(null));
    }
}

[TestClass]
public class RtfTextTests
{
    [TestMethod]
    public void HtmlIsUnwrappedFromOutlooksRtf()
    {
        const string rtf = @"{\rtf1\ansi\ansicpg1252\fromhtml1 \deff0{\fonttbl{\f0\fswiss Arial;}}{\colortbl\red0\green0\blue0;}" +
            @"{\*\htmltag64 <html>}{\*\htmltag64 <body>}{\*\htmltag64 <p>}\htmlrtf \pard\plain\f0 \htmlrtf0 Caf\'e9 {\*\htmltag84 &amp;} more\htmlrtf\par\htmlrtf0" +
            @"{\*\htmltag72 </p>}{\*\htmltag58 </body>}{\*\htmltag58 </html>}}";
        Assert.IsTrue(RtfText.WrapsHtml(rtf));
        Assert.AreEqual("<html><body><p>Café &amp; more</p></body></html>", RtfText.HtmlFromRtf(rtf));
    }

    [TestMethod]
    public void PlainTextIsTheRtfsText()
    {
        const string rtf = @"{\rtf1\ansi\ansicpg1252\deff0\nouicompat{\fonttbl{\f0\fnil\fcharset0 Calibri;}}{\*\generator Riched20 10.0.19041}\viewkind4\uc1 \pard\sa200\sl276\slmult1\f0\fs22\lang9 In a subfolder.\par Second \u8211?line \'fcber \{braces\}\par}";
        Assert.IsNull(RtfText.HtmlFromRtf(rtf));
        Assert.AreEqual("In a subfolder.\r\nSecond –line über {braces}", RtfText.PlainText(rtf));
    }
}

[TestClass]
public class MimeWriterTests
{
    [TestMethod]
    public void HeadersBeyondAsciiAreEncodedWords()
    {
        Assert.AreEqual("plain", MimeWriter.EncodeWord("plain"));
        StringAssert.StartsWith(MimeWriter.EncodeWord("ümlauts & more"), "=?utf-8?B?");
        var longText = new string('é', 60);
        var words = MimeWriter.EncodeWord(longText).Split(' ');
        Assert.IsTrue(words.Length > 1 && words.All(w => w.Length <= 75), "split into words within the limit");
        Assert.AreEqual(longText, string.Concat(words.Select(w => Encoding.UTF8.GetString(Convert.FromBase64String(w[10..^2])))));
    }

    [TestMethod]
    public void AMessageWithTransportHeadersKeepsThemAndDropsTheContentOnes()
    {
        var item = new MailItem
        {
            Summary = new MailItemSummary("1", MailItemKind.Message, "Hello", "Jane", null, null, null, 0, false),
            MessageClass = "IPM.Note",
            From = new MailAddress("Jane Doe", "jane@example.test"),
            TransportHeaders = "Received: from a by b\r\n with ESMTP\r\nMessage-ID: <abc@example.test>\r\nContent-Type: text/plain\r\nMIME-Version: 1.0\r\nSubject: Hello\r\n",
            BodyText = "Hi there\r\n",
        };
        var eml = Encoding.ASCII.GetString(MimeWriter.Build(item));
        StringAssert.StartsWith(eml, "Received: from a by b with ESMTP\r\nMessage-ID: <abc@example.test>\r\nSubject: Hello\r\nFrom: Jane Doe <jane@example.test>\r\n");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(eml, "^MIME-Version:", System.Text.RegularExpressions.RegexOptions.Multiline).Count);
        StringAssert.Contains(eml, "\r\n\r\nHi there\r\n");
        StringAssert.Contains(eml, "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n");
    }
}
