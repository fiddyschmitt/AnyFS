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
    public void TheIconIsOutlooksLogoAsA64PixelPng()
    {
        using var icon = _plugin.OpenIcon();
        Assert.IsNotNull(icon, "embedded in the plugin");
        var header = new byte[24];
        icon.ReadExactly(header);
        CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, header[..8], "a PNG: the window draws no SVG yet");
        Assert.AreEqual(64, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), "width");
        Assert.AreEqual(64, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)), "height");
    }

    [TestMethod]
    public void TheFileNamesTheSource()
    {
        var documents = OperatingSystem.IsWindows() ? @"C:\Users\me\Documents\Outlook Files\" : "/home/me/Documents/Outlook Files/";
        var outlook = OperatingSystem.IsWindows() ? @"C:\Users\jane\AppData\Local\Microsoft\Outlook\" : "/home/jane/mail/";
        Assert.AreEqual("archive.pst", _plugin.SuggestName(new OutlookSettings { Path = documents + "archive.pst" }));
        Assert.AreEqual("jane@contoso.com.ost", _plugin.SuggestName(new OutlookSettings { Path = outlook + "jane@contoso.com.ost" }), "the whole name, as Outlook gave it");
        Assert.IsNull(_plugin.SuggestName(new OutlookSettings()));
        var settings = new OutlookSettings { Path = "  \"C:\\mail\\old.pst\"  " };
        _plugin.NormaliseSettings(settings);
        Assert.AreEqual(@"C:\mail\old.pst", settings.Path);
    }

    [TestMethod]
    public void ValidationNamesWhatIsWrong()
    {
        Assert.IsTrue(_plugin.Validate(new OutlookSettings()).Any(m => m.IsError && m.Property == nameof(OutlookSettings.Path)));
        var server = _plugin.Validate(new OutlookSettings { Path = "https://mail.contoso.com/EWS/Exchange.asmx" }).ToList();
        Assert.AreEqual(OperatingSystem.IsWindows() ? 0 : 1, server.Count(m => m.IsError), "a server's address with the default sign-in goes as yourself: nothing to type; elsewhere than Windows the choice is an error");
        StringAssert.Contains(server.Single(m => m.Property == nameof(OutlookSettings.SignInMethod) && !m.IsError).Message, "Windows account");
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

            Assert.AreEqual(EnumerationState.Unknown, source.Counters.State, "so far: a message's attachments are known once it is opened");
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
            CollectionAssert.AreEquivalent(new[] { "message.md", "message.eml", "attachments" }, files.Select(e => e.Name).ToList());
            Assert.IsTrue(files.Single(e => e.Name == "attachments").IsDirectory);
            Assert.IsTrue(files.Where(e => !e.IsDirectory).All(e => e.Length > 100), "the files have their real sizes in the listing");

            var markdown = await OutlookFixtures.TextAsync(source, $"{folder}/message.md");
            StringAssert.StartsWith(markdown, "# Quarterly figures\n");
            Assert.IsFalse(markdown.Contains("**From:**", StringComparison.Ordinal), "a draft has no sender");
            StringAssert.Contains(markdown, "**To:** anyfs-tester@y0w7606wv.onmicrosoft.com");
            StringAssert.Contains(markdown, "**Importance:** high");
            StringAssert.Contains(markdown, "**Status:** draft, never sent");
            StringAssert.Contains(markdown, "[figures.csv](attachments/figures.csv) (27 bytes)");
            StringAssert.Contains(markdown, "\n---\n\nFigures attached.");

            var eml = await OutlookFixtures.TextAsync(source, $"{folder}/message.eml");
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
            var markdown = await OutlookFixtures.TextAsync(source, $"{photo}/message.md");
            StringAssert.Contains(markdown, "![](attachments/dot.png)", "cid: rewritten to the attachments folder");
            var eml = await OutlookFixtures.TextAsync(source, $"{photo}/message.eml");
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
            var outer = await OutlookFixtures.TextAsync(source, $"{forwarded}/message.eml");
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
            var markdown = await OutlookFixtures.TextAsync(source, $"{note}/message.md");
            StringAssert.Contains(markdown, "# Plain note: ümlauts & \"quotes\" / slashes: kept?\n");
            StringAssert.Contains(markdown, "\n---\n\nName edge cases. naïve café – 日本語");
            var eml = await OutlookFixtures.TextAsync(source, $"{note}/message.eml");
            StringAssert.Contains(eml, "Subject: =?utf-8?B?", "a subject beyond ASCII is an encoded word");
            StringAssert.Contains(eml, "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable");
            StringAssert.Contains(eml, "na=C3=AFve caf=C3=A9", "UTF-8, quoted-printable");

            var kickOff = await OutlookFixtures.ItemFolderAsync(source, "Inbox/Projects/2026/10", "RE: Project kick-off");
            var rich = await OutlookFixtures.TextAsync(source, $"{kickOff}/message.md");
            StringAssert.Contains(rich, "**To:** AnyFS tester <anyfs-tester@y0w7606wv.onmicrosoft.com>; adelev@example.test");
            StringAssert.Contains(rich, "**Cc:** megan.bowen@example.test");
            StringAssert.Contains(rich, "\n---\n\nIn a subfolder.", "the rich-text body as text");
            var richEml = await OutlookFixtures.TextAsync(source, $"{kickOff}/message.eml");
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
        var wrong = new OutlookSource(new OutlookSettings { Path = "neither an address nor a file" });
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

    [TestMethod]
    public async Task ADamagedFileIsServedAllButWhatDoesNotRead()
    {
        // damaged.pst (LibOutlook's tests make it): unicode.pst whose Inbox\Projects folder and "Plain note" mail have their
        // own properties damaged, whose Contacts folder's contents table does not read, and whose Projects folder's contents
        // table names values it does not hold, as a real OST of years failed whole ("HID 0x0000CD20 names allocation 1641 of
        // 616 in heap block 0"), its contents tables' cells by the hundred
        var log = new LineLogger();
        using var context = new TestSourceContext("outlook", log);
        await using var source = new OutlookSource(new OutlookSettings { Path = OutlookFixtures.Pst("damaged.pst"), RefreshMinutes = 0 });
        await source.InitialiseAsync(context, CancellationToken.None);

        CollectionAssert.AreEquivalent(new[] { "2026", "Projects" }, (await OutlookFixtures.ListAsync(source, "Inbox")).Select(e => e.Name).ToList(), "Projects keeps its place, named from the Inbox's table");
        CollectionAssert.AreEqual(new[] { "2026" }, (await OutlookFixtures.ListAsync(source, "Inbox/Projects")).Select(e => e.Name).ToList(), "and its mail, in a table of its own");
        var month = await OutlookFixtures.ListAsync(source, "Inbox/2026/10");
        Assert.AreEqual(5, month.Count, "the damaged mail is listed from the Inbox's table, its own properties not read: " + string.Join(", ", month.Select(e => e.Name)));
        Assert.AreEqual(0, (await OutlookFixtures.ListAsync(source, "Contacts")).Count, "a contents table that does not read lists nothing");
        Assert.IsTrue((await OutlookFixtures.ListAsync(source, "Calendar")).Count > 0, "the other folders are whole");

        // the damaged mail says why it does not open; the others open, and the source stays healthy
        var plain = "Inbox/2026/10/" + month.Single(e => e.Name.Contains(" - Plain note", StringComparison.Ordinal)).Name;
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await OutlookFixtures.ListAsync(source, plain));
        Assert.AreEqual(VfsError.IoError, ex.Error);
        StringAssert.Contains(ex.Message, "message 0x");
        StringAssert.Contains(ex.Message, " names allocation ");
        var figures = await OutlookFixtures.ItemFolderAsync(source, "Inbox/2026/10", "Quarterly figures");
        StringAssert.StartsWith(await OutlookFixtures.TextAsync(source, $"{figures}/message.md"), "# Quarterly figures\n");
        Assert.AreNotEqual(ComponentHealth.Degraded, context.LastHealth ?? ComponentHealth.Ready);

        var lines = log.Lines;
        Assert.IsTrue(lines.Any(l => l.Contains("the folder 'Projects' in damaged.pst is shown with what reads of it", StringComparison.Ordinal)), log.ToString());
        Assert.IsFalse(lines.Any(l => l.Contains("'Plain note", StringComparison.Ordinal)), "the damaged mail lists from its row alone: nothing to say until it is opened");
        Assert.IsTrue(lines.Any(l => l.Contains("the items of 'Contacts' in damaged.pst could not all be read, 0 are listed: the contents table of folder 0x", StringComparison.Ordinal)), log.ToString());
        Assert.IsTrue(lines.Any(l => l.Contains("2 places of damaged.pst do not read in full", StringComparison.Ordinal)), log.ToString());

        // the Projects folder's table names values it does not hold (as a real OST's did, hundreds of cells): its mail is
        // listed with them read from the mail itself, and one line says so
        var projects = await OutlookFixtures.ListAsync(source, "Inbox/Projects/2026/10");
        Assert.AreEqual(1, projects.Count);
        StringAssert.EndsWith(projects[0].Name, " - RE_ Project kick-off");
        var missing = lines.Single(l => l.Contains("cells of the table of 'Projects' in damaged.pst name values the table does not hold", StringComparison.Ordinal));
        StringAssert.Contains(missing, "(Subject) of type 0x001F in a cell of 4 bytes: HID 0x");
    }

    /// <summary>
    /// S/MIME mail as Outlook keeps it in a data file (smime.pst, made by Outlook): a clear-signed message is one nameless
    /// multipart/signed attachment beside the body Outlook promoted, an encrypted one a smime.p7m and no body at all. Each
    /// message.eml is the message as it came: the signed entity byte for byte, or the CMS under an S/MIME Content-Type,
    /// so Outlook checks the signature or decrypts it (a real OST's encrypted mail opened with a blank body, 25-Oct-2026).
    /// </summary>
    [TestMethod]
    public async Task SmimeMailIsServedAsTheSignedOrEncryptedMessageItCameAs()
    {
        // what the file holds, as LibOutlook reads it
        byte[] entity;
        byte[] cms;
        using (var pst = LibOutlook.PstFile.Open(OutlookFixtures.Pst("smime.pst")))
        {
            var inbox = pst.TopFolder.FindFolder("Inbox")!;
            var signed = inbox.Messages.Single(m => m.Subject == "AnyFS lab: a signed message");
            Assert.AreEqual("IPM.Note.SMIME.MultipartSigned", signed.MessageClass);
            Assert.AreEqual("multipart/signed", signed.Attachments.Single().MimeTag);
            entity = signed.Attachments.Single().GetContent();
            var encrypted = inbox.Messages.Single(m => m.Subject == "AnyFS lab: an encrypted message");
            Assert.AreEqual("IPM.Note.SMIME", encrypted.MessageClass);
            Assert.IsTrue(encrypted.Body is null && encrypted.BodyHtml is null && encrypted.BodyRtf is null, "an encrypted message has no body of its own");
            cms = encrypted.Attachments.Single().GetContent();
        }

        var (source, context) = await OutlookFixtures.StartAsync("smime.pst");
        await using (source)
        using (context)
        {
            var year = (await OutlookFixtures.ListAsync(source, "Inbox")).Single().Name;
            var month = $"Inbox/{year}/{(await OutlookFixtures.ListAsync(source, $"Inbox/{year}")).Single().Name}";

            var signedFolder = await OutlookFixtures.ItemFolderAsync(source, month, "AnyFS lab: a signed message");
            var eml = await OutlookFixtures.ReadAsync(source, $"{signedFolder}/message.eml");
            var text = Encoding.ASCII.GetString(eml);
            var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            StringAssert.Contains(text[..headerEnd], "\r\nSubject: AnyFS lab: a signed message\r\n");
            StringAssert.Contains(text[..headerEnd], "\r\nContent-Type: multipart/signed;");
            Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(text[..headerEnd], "^MIME-Version:", System.Text.RegularExpressions.RegexOptions.Multiline).Count);
            var entityBody = Encoding.ASCII.GetString(entity).IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
            CollectionAssert.AreEqual(entity[entityBody..], eml[(headerEnd + 4)..], "the signed body as Outlook keeps it, byte for byte");

            var encryptedFolder = await OutlookFixtures.ItemFolderAsync(source, month, "AnyFS lab: an encrypted message");
            var sealedText = await OutlookFixtures.TextAsync(source, $"{encryptedFolder}/message.eml");
            var sealedEnd = sealedText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            StringAssert.Contains(sealedText[..sealedEnd], "\r\nSubject: AnyFS lab: an encrypted message\r\n");
            StringAssert.Matches(sealedText[..sealedEnd], new System.Text.RegularExpressions.Regex(@"\r\nContent-Type: application/(x-)?pkcs7-mime;[^\r]*(\r\n [^\r]*)*smime-type=enveloped-data"));
            StringAssert.Contains(sealedText[..sealedEnd], "\r\nContent-Transfer-Encoding: base64");
            CollectionAssert.AreEqual(cms, Convert.FromBase64String(sealedText[(sealedEnd + 4)..]), "the CMS as Outlook keeps it");
        }
    }

    /// <summary>
    /// S/MIME mail from a data file opened for its Markdown and attachments folder (smime.pst, made by Outlook): the signed
    /// message's signature checked against the content Outlook kept and its real attachment listed (not the signed entity);
    /// the encrypted one, which no certificate here opens (its key is the lab's), says so and keeps its smime.p7m. With the
    /// lab's test key (ANYFS_TEST_SMIME_PFX, kept outside the repository) the encrypted one opens too.
    /// </summary>
    [TestMethod]
    public async Task SmimeMailIsOpenedForItsMarkdown()
    {
        var pfx = Environment.GetEnvironmentVariable("ANYFS_TEST_SMIME_PFX");
        var keys = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
        if (!string.IsNullOrEmpty(pfx))
        {
            keys.Add(System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(pfx, null, System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable));
        }

        var context = new TestSourceContext("outlook");
        var source = new OutlookSource(new OutlookSettings { Path = OutlookFixtures.Pst("smime.pst"), RefreshMinutes = 0 }) { SmimeCertificates = keys };
        await source.InitialiseAsync(context, CancellationToken.None);
        await using (source)
        using (context)
        {
            var year = (await OutlookFixtures.ListAsync(source, "Inbox")).Single().Name;
            var month = $"Inbox/{year}/{(await OutlookFixtures.ListAsync(source, $"Inbox/{year}")).Single().Name}";

            var signed = await OutlookFixtures.ItemFolderAsync(source, month, "AnyFS lab: a signed message");
            var md = await OutlookFixtures.TextAsync(source, $"{signed}/message.md");
            StringAssert.Contains(md, "**Security:** signed by AnyFS lab S/MIME <anyfs-tester@y0w7606wv.onmicrosoft.com>; the signature holds, but the certificate is not trusted here");
            StringAssert.Contains(md, "The body of the signed message, made in the AnyFS lab.");
            var files = await OutlookFixtures.ListAsync(source, $"{signed}/attachments");
            Assert.AreEqual(("report.bin", 2000L), (files.Single().Name, files.Single().Length), "the attachment inside the signed message");

            var encrypted = await OutlookFixtures.ItemFolderAsync(source, month, "AnyFS lab: an encrypted message");
            var sealedMd = await OutlookFixtures.TextAsync(source, $"{encrypted}/message.md");
            if (keys.Count == 0)
            {
                StringAssert.Contains(sealedMd, "**Security:** encrypted, and none of the certificates on this computer can open it");
                Assert.AreEqual("smime.p7m", (await OutlookFixtures.ListAsync(source, $"{encrypted}/attachments")).Single().Name);
            }
            else
            {
                StringAssert.Contains(sealedMd, "**Security:** encrypted for you, opened with your certificate");
                StringAssert.Contains(sealedMd, "The body of the encrypted message, made in the AnyFS lab.");
                var inside = await OutlookFixtures.ListAsync(source, $"{encrypted}/attachments");
                Assert.AreEqual(("report.bin", 2000L), (inside.Single().Name, inside.Single().Length));
            }
        }
    }

    /// <summary>
    /// A data file's recurring appointments are listed as their occurrences, as a mailbox's calendar is (recurrence.pst,
    /// made by Outlook in the Pacific zone; LibOutlook's tests check the expansion against Outlook's own): each occurrence
    /// an event folder at its own time, a deleted one left out, a moved one at its new time under its own subject and
    /// place, a finite series whole, one without an end a year ahead; the changed occurrences Outlook keeps for a series
    /// are no attachments of it.
    /// </summary>
    [TestMethod]
    public async Task RecurringAppointmentsAreListedAsTheirOccurrences()
    {
        var (source, context) = await OutlookFixtures.StartAsync("recurrence.pst");
        await using (source)
        using (context)
        {
            var folders = new List<string>();
            foreach (var year in await OutlookFixtures.ListAsync(source, "Calendar"))
            {
                foreach (var month in await OutlookFixtures.ListAsync(source, $"Calendar/{year.Name}"))
                {
                    folders.AddRange((await OutlookFixtures.ListAsync(source, $"Calendar/{year.Name}/{month.Name}")).Select(e => $"Calendar/{year.Name}/{month.Name}/{e.Name}"));
                }
            }

            int Count(string subject) => folders.Count(f => VfsPath.Name(f).EndsWith(" - " + subject, StringComparison.Ordinal));
            string? Find(DateTimeOffset start, string subject) => folders.SingleOrDefault(f => VfsPath.Name(f) == MailNames.AppointmentFolder(start, subject));

            Assert.AreEqual(19, Count("Weekly sync") + Count("Weekly sync (moved)"), "twenty occurrences, one deleted");
            Assert.IsNull(Find(new DateTimeOffset(2026, 9, 17, 17, 0, 0, TimeSpan.Zero), "Weekly sync"), "the deleted occurrence");
            Assert.IsNotNull(Find(new DateTimeOffset(2026, 10, 29, 17, 0, 0, TimeSpan.Zero), "Weekly sync"), "10:00 in daylight time");
            Assert.IsNotNull(Find(new DateTimeOffset(2026, 11, 3, 18, 0, 0, TimeSpan.Zero), "Weekly sync"), "10:00 in standard time");
            Assert.AreEqual(10, Count("Month end review"), "a finite series whole");
            Assert.AreEqual(1, Count("One-off"));
            Assert.IsTrue(Count("Last Friday") >= 12, "a series without an end, a year ahead");

            var moved = new DateTimeOffset(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
            var movedFolder = Find(moved, "Weekly sync (moved)");
            Assert.IsNotNull(movedFolder, "the moved occurrence at its new time, under its own subject");
            var (movedYear, movedMonth) = MailNames.Period(moved);
            Assert.AreEqual($"Calendar/{movedYear}/{movedMonth}", movedFolder[..movedFolder.LastIndexOf('/')]);
            CollectionAssert.AreEquivalent(new[] { "event.md", "event.ics" }, (await OutlookFixtures.ListAsync(source, movedFolder)).Select(e => e.Name).ToList(),
                "the series' exception attachments are no attachments of it");
            var ics = (await OutlookFixtures.TextAsync(source, $"{movedFolder}/event.ics")).Replace("\r\n ", "", StringComparison.Ordinal);
            StringAssert.Contains(ics, "\r\nDTSTART:20261013T180000Z\r\n");
            StringAssert.Contains(ics, "\r\nDTEND:20261013T184500Z\r\n");
            StringAssert.Contains(ics, "\r\nSUMMARY:Weekly sync (moved)\r\n");
            StringAssert.Contains(ics, "\r\nLOCATION:Room 9\r\n");
            var md = await OutlookFixtures.TextAsync(source, $"{movedFolder}/event.md");
            StringAssert.StartsWith(md, "# Weekly sync (moved)\n");
            StringAssert.Contains(md, "**Where:** Room 9");
            StringAssert.Contains(md, "**Repeats:** ");

            // an occurrence as the series has it
            var first = Find(new DateTimeOffset(2026, 9, 1, 17, 0, 0, TimeSpan.Zero), "Weekly sync")!;
            var firstIcs = (await OutlookFixtures.TextAsync(source, $"{first}/event.ics")).Replace("\r\n ", "", StringComparison.Ordinal);
            StringAssert.Contains(firstIcs, "\r\nDTSTART:20260901T170000Z\r\n");
            StringAssert.Contains(firstIcs, "\r\nLOCATION:Room 4\r\n");
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

    /// <summary>A message's files are made again every few minutes: the same bytes each time, or a sync tool sees them change.</summary>
    [TestMethod]
    public void TheSameMessageIsTheSameBytesEachTimeItIsWritten()
    {
        MailItem Item(string id) => new()
        {
            Summary = new MailItemSummary(id, MailItemKind.Message, "Hello", "Jane", null, null, null, 0, true),
            MessageClass = "IPM.Note",
            BodyText = "Hi",
            BodyHtml = "<p>Hi</p>",
            Attachments =
            [
                new MailAttachment("dot.png", "image/png", "dot", true, 3, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])), null),
                File("a.txt", "text/plain", [65]),
            ],
        };

        var first = MimeWriter.Build(Item("200024"));
        CollectionAssert.AreEqual(first, MimeWriter.Build(Item("200024")), "written again, the same bytes");
        var boundaries = System.Text.RegularExpressions.Regex.Matches(Encoding.ASCII.GetString(first), "boundary=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.AreEqual(3, boundaries.Count);
        Assert.AreEqual(3, boundaries.Distinct().Count(), "alternative, related and mixed each have their own");
        Assert.IsTrue(boundaries.All(b => b.StartsWith("----=_AnyFS_", StringComparison.Ordinal)), "after =_, which no encoded content holds");
        CollectionAssert.AreNotEqual(first, MimeWriter.Build(Item("200044")), "another item, other boundaries");
    }

    // --- S/MIME as a data file keeps it ([MS-OXOSMIME] 2.1.3) -------------------------------------------------------------

    /// <summary>The signed entity as Outlook stores it: the whole multipart/signed entity, its own MIME-Version first, the Content-Type folded.</summary>
    private const string SignedEntity =
        "MIME-Version: 1.0\r\nContent-Type: multipart/signed;\r\n\tprotocol=\"application/pkcs7-signature\";\r\n\tmicalg=sha-256;\r\n\tboundary=\"outer-1\"\r\n\r\n"
        + "This is an S/MIME signed message\r\n\r\n--outer-1\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: 7bit\r\n\r\n<p>Signed.</p>\r\n"
        + "--outer-1\r\nContent-Type: application/pkcs7-signature; name=\"smime.p7s\"\r\nContent-Transfer-Encoding: base64\r\n\r\nMIIBsignature\r\n--outer-1--\r\n";

    /// <summary>The start of a CMS ContentInfo holding enveloped data (1.2.840.113549.1.7.3), indefinite length, then some bytes.</summary>
    private static readonly byte[] EnvelopedCms = [0x30, 0x80, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x03, 0xA0, 0x80, .. Enumerable.Range(0, 300).Select(i => (byte)(i * 7))];

    private static MailItem Smime(string messageClass, MailAttachment attachment, string? bodyHtml = null, string? internetContentType = null) => new()
    {
        Summary = new MailItemSummary("1", MailItemKind.Message, "Secret", "Jane", null, null, null, 0, true),
        MessageClass = messageClass,
        From = new MailAddress("Jane Doe", "jane@example.test"),
        Recipients = [new MailRecipient(MailRecipientKind.To, new MailAddress("Sam", "sam@example.test"))],
        Sent = new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero),
        TransportHeaders = "Received: from a by b\r\nSubject: Secret\r\nMIME-Version: 1.0\r\nContent-Type: multipart/signed; boundary=\"outer-1\"\r\n",
        BodyHtml = bodyHtml,
        Attachments = [attachment],
        InternetContentType = internetContentType,
    };

    private static MailAttachment File(string name, string contentType, byte[] content)
        => new(name, contentType, null, false, content.Length, _ => Task.FromResult<Stream>(new MemoryStream(content)), null);

    [TestMethod]
    public void AClearSignedMessageIsItsSignedEntityWithTheMessagesHeaders()
    {
        // Outlook keeps it as IPM.Note.SMIME.MultipartSigned, one nameless multipart/signed attachment, the body promoted
        var entity = Encoding.ASCII.GetBytes(SignedEntity);
        var eml = MimeWriter.Build(Smime("IPM.Note.SMIME.MultipartSigned", File("attachment", "multipart/signed", entity), bodyHtml: "<p>Signed.</p>"));
        var text = Encoding.ASCII.GetString(eml);

        StringAssert.StartsWith(text, "Received: from a by b\r\nSubject: Secret\r\nFrom: Jane Doe <jane@example.test>\r\nTo: Sam <sam@example.test>\r\n");
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        StringAssert.EndsWith(text[..headerEnd], "MIME-Version: 1.0\r\nContent-Type: multipart/signed;\r\n\tprotocol=\"application/pkcs7-signature\";\r\n\tmicalg=sha-256;\r\n\tboundary=\"outer-1\"",
            "the entity's own Content-Type, folded as it was, is the message's");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(text, "^MIME-Version:", System.Text.RegularExpressions.RegexOptions.Multiline).Count, "the entity's MIME-Version is dropped");
        Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(text, "^Content-Type: multipart/signed", System.Text.RegularExpressions.RegexOptions.Multiline).Count, "the transport headers' Content-Type is dropped");
        var entityBody = entity.AsSpan(SignedEntity.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4);
        CollectionAssert.AreEqual(entityBody.ToArray(), eml.AsSpan(headerEnd + 4).ToArray(), "the signed body byte for byte, so the signature holds");
        Assert.IsFalse(text.Contains("multipart/mixed", StringComparison.Ordinal), "no wrapper and no copy of the promoted body");
    }

    [TestMethod]
    public void AClearSignedMessageWhoseAttachmentHasNoTypeIsKnownByItsClass()
    {
        var entity = Encoding.ASCII.GetBytes(SignedEntity);
        var text = Encoding.ASCII.GetString(MimeWriter.Build(Smime("IPM.Note.SMIME.MultipartSigned", File("attachment", "application/octet-stream", entity))));
        StringAssert.Contains(text, "\r\nContent-Type: multipart/signed;\r\n\tprotocol=");
        StringAssert.EndsWith(text, "--outer-1--\r\n");
    }

    [TestMethod]
    public void AnEncryptedMessageIsItsCmsUnderTheContentTypeItCameWith()
    {
        var eml = Encoding.ASCII.GetString(MimeWriter.Build(Smime("IPM.Note.SMIME", File("smime.p7m", "application/pkcs7-mime", EnvelopedCms),
            internetContentType: "application/x-pkcs7-mime;\r\n\tsmime-type=enveloped-data;\r\n\tname=\"smime.p7m\"")));
        StringAssert.Contains(eml, "\r\nMIME-Version: 1.0\r\nContent-Type: application/x-pkcs7-mime; smime-type=enveloped-data;\r\n name=\"smime.p7m\"\r\nContent-Disposition: attachment; filename=\"smime.p7m\"\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        var body = eml[(eml.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        CollectionAssert.AreEqual(EnvelopedCms, Convert.FromBase64String(body), "the CMS as it is stored");
        Assert.IsTrue(body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).All(l => l.Length <= 76), "base64 in 76-character lines");
        Assert.IsFalse(eml.Contains("multipart/", StringComparison.Ordinal), "the message is the CMS alone: no empty body beside it");
    }

    [TestMethod]
    public void AnEncryptedMessageWithoutItsContentTypeGetsOneFromTheCms()
    {
        var eml = Encoding.ASCII.GetString(MimeWriter.Build(Smime("IPM.Note.SMIME", File("attachment", "application/pkcs7-mime", EnvelopedCms))));
        StringAssert.Contains(eml, "\r\nContent-Type: application/pkcs7-mime; smime-type=enveloped-data;\r\n name=\"smime.p7m\"\r\nContent-Disposition: attachment; filename=\"smime.p7m\"\r\n");
        Assert.AreEqual("enveloped-data", MimeWriter.SmimeType(EnvelopedCms));
        Assert.AreEqual("signed-data", MimeWriter.SmimeType([0x30, 0x82, 0x01, 0x00, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x02]));
        Assert.IsNull(MimeWriter.SmimeType("Content-Type: text/plain"u8));
    }

    [TestMethod]
    public void AnSmimeMessageAttachedToAnotherIsWrittenSoInside()
    {
        var inner = Smime("IPM.Note.SMIME", File("smime.p7m", "application/pkcs7-mime", EnvelopedCms));
        var outer = new MailItem
        {
            Summary = new MailItemSummary("2", MailItemKind.Message, "Fwd: Secret", "Sam", null, null, null, 0, true),
            MessageClass = "IPM.Note",
            BodyText = "See below.",
            Attachments = [new MailAttachment("Secret.eml", "message/rfc822", null, false, 0, _ => Task.FromResult(Stream.Null), _ => Task.FromResult<MailItem?>(inner))],
        };
        var eml = Encoding.ASCII.GetString(MimeWriter.Build(outer));
        StringAssert.Contains(eml, "Content-Type: message/rfc822; name=\"Secret.eml\"");
        StringAssert.Contains(eml, "Content-Type: application/pkcs7-mime; smime-type=enveloped-data;\r\n name=\"smime.p7m\"\r\n");
    }

    [TestMethod]
    public void AMessageClassedSmimeButNotShapedSoIsWrittenAsAnyOther()
    {
        var item = Smime("IPM.Note.SMIME", File("report.pdf", "application/pdf", [1, 2, 3]), bodyHtml: "<p>Hi</p>");
        var eml = Encoding.ASCII.GetString(MimeWriter.Build(item));
        StringAssert.Contains(eml, "Content-Type: multipart/mixed; boundary=");
        StringAssert.Contains(eml, "Content-Disposition: attachment; filename=\"report.pdf\"");

        // an entity without a multipart/signed header is no signed message either
        var odd = Encoding.ASCII.GetString(MimeWriter.Build(Smime("IPM.Note.SMIME.MultipartSigned", File("attachment", "multipart/signed", "no header block at all"u8.ToArray()))));
        StringAssert.Contains(odd, "Content-Type: multipart/mixed; boundary=");
    }
}
