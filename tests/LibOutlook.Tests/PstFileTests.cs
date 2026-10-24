using System.Text;

namespace LibOutlook.Tests;

/// <summary>
/// The two PST fixtures Outlook wrote (tests/LibOutlook.Tests/fixtures/README.md): the same content in the ANSI and
/// the Unicode format.
/// </summary>
[TestClass]
public class PstFileTests
{
    private static readonly Guid PsetidAddress = new("00062004-0000-0000-C000-000000000046");
    private static readonly Guid PsetidAppointment = new("00062002-0000-0000-C000-000000000046");
    private const string Tester = "anyfs-tester@y0w7606wv.onmicrosoft.com";
    private const string UmlautSubject = "Plain note: ümlauts & \"quotes\" / slashes: kept?";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("ansi.pst", PstFormat.Ansi)]
    [DataRow("unicode.pst", PstFormat.Unicode)]
    public void Opens_and_describes_the_file(string name, PstFormat format)
    {
        using var pst = PstFile.Open(Fixtures.Path(name));
        Assert.AreEqual(format, pst.Format);
        Assert.AreEqual(PstEncoding.Permute, pst.Encoding);
        Assert.IsFalse(pst.IsOst);
        Assert.IsTrue(format == PstFormat.Ansi ? pst.Version is 14 or 15 : pst.Version == 23, $"wVer {pst.Version}");
        Assert.AreEqual(new FileInfo(Fixtures.Path(name)).Length, pst.Length);
        Assert.AreEqual("AnyFS fixture", pst.DisplayName);
        Assert.IsNotNull(pst.MessageStore);
        Assert.AreEqual("Top of Outlook data file", pst.TopFolder.Name);
        Assert.AreNotEqual(pst.RootFolder.Nid, pst.TopFolder.Nid);
        Assert.AreEqual(pst.TopFolder.Nid, pst.GetSpecialFolder(PstSpecialFolder.TopOfStore)!.Nid);
        Assert.AreEqual("Deleted Items", pst.GetSpecialFolder(PstSpecialFolder.DeletedItems)!.Name);
        Assert.AreEqual("Search Root", pst.GetSpecialFolder(PstSpecialFolder.SearchRoot)!.Name);
        Assert.IsNull(pst.GetSpecialFolder(PstSpecialFolder.Outbox), "a PST has no Outbox entry id");
    }

    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public void Lists_the_folder_tree(string name)
    {
        using var pst = PstFile.Open(Fixtures.Path(name));
        var top = pst.TopFolder;
        Assert.IsTrue(top.HasSubfolders);
        var names = top.Folders.Select(f => f.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "Inbox", "Contacts", "Calendar", "Deleted Items" }, names);

        var inbox = top.FindFolder("inbox")!;
        Assert.AreEqual("Inbox", inbox.Name);
        Assert.AreEqual("IPF.Note", inbox.ContainerClass);
        Assert.AreEqual(5, inbox.ContentCount);
        Assert.IsTrue(inbox.HasSubfolders);
        Assert.AreEqual(1, inbox.Folders.Count);

        var projects = inbox.Folders[0];
        Assert.AreEqual("Projects", projects.Name);
        Assert.AreEqual(1, projects.ContentCount);
        Assert.IsFalse(projects.HasSubfolders);
        Assert.AreEqual(0, projects.Folders.Count);

        Assert.AreEqual("IPF.Contact", top.FindFolder("Contacts")!.ContainerClass);
        Assert.AreEqual("IPF.Appointment", top.FindFolder("Calendar")!.ContainerClass);
        Assert.AreEqual(4, inbox.UnreadCount, "four of the five drafts are unread");
        Assert.IsTrue(inbox.Properties.Contains(PstPropertyIds.DisplayName));

        var all = pst.EnumerateFolders().ToList();
        Assert.AreEqual(pst.RootFolder.Nid, all[0].Nid);
        Assert.IsTrue(all.Any(f => f.IsSearchFolder), "Outlook makes a search folder under the root");
        Assert.AreEqual(0, all.First(f => f.IsSearchFolder).Messages.Count(), "an empty search folder lists nothing");
        Assert.AreEqual(inbox.Nid, pst.GetFolder(inbox.Nid)!.Nid);
        Assert.IsNull(pst.GetFolder(0x12345678));
    }

    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public void Lists_messages(string name)
    {
        using var pst = PstFile.Open(Fixtures.Path(name));
        var inbox = pst.TopFolder.FindFolder("Inbox")!;
        var messages = inbox.Messages.ToList();
        CollectionAssert.AreEquivalent(
            new[] { "Quarterly figures", "Photo from the site visit", UmlautSubjectIn(name), "Forwarded: site visit", "Large attachment" },
            messages.Select(m => m.Subject).ToArray());
        foreach (var m in messages)
        {
            Assert.AreEqual("IPM.Note", m.MessageClass, m.Subject);
            Assert.IsTrue(m.IsUnsent, $"{m.Subject} was never sent");
            Assert.IsTrue(m.Size > 0, m.Subject);
            Assert.IsNotNull(m.CreationTime, m.Subject);
            Assert.IsNotNull(m.LastModificationTime, m.Subject);
            Assert.AreEqual(m.Subject, pst.GetMessage(m.Nid)!.Subject);
        }

        Assert.IsTrue(messages.Single(m => m.Subject == "Quarterly figures").HasAttachments);
        Assert.IsFalse(messages.Single(m => m.Subject == UmlautSubjectIn(name)).HasAttachments);
        Assert.AreEqual(2, messages.Single(m => m.Subject == "Quarterly figures").Importance);

        var kickOff = inbox.FindFolder("Projects")!.Messages.Single();
        Assert.AreEqual("RE: Project kick-off", kickOff.Subject);
        Assert.AreEqual("RE: Project kick-off", kickOff.ConversationTopic, "a subject set whole keeps its RE: in the topic");
        Assert.IsNull(kickOff.Properties.GetString(PstPropertyIds.SubjectPrefix), "Outlook computes the prefix at display time, the file has none");
    }

    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public void Reads_bodies(string name)
    {
        using var pst = PstFile.Open(Fixtures.Path(name));
        var inbox = pst.TopFolder.FindFolder("Inbox")!;

        var figures = inbox.Messages.Single(m => m.Subject == "Quarterly figures");
        Assert.AreEqual("<p>Figures attached.</p>", figures.BodyHtml, "an HTML draft keeps the HTML as given");
        Assert.IsNull(figures.Body, "and stores no plain-text body");
        Assert.IsNull(figures.BodyRtf);
        Assert.AreEqual(20127, figures.InternetCodepage, "us-ascii");
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("<p>Figures attached.</p>"), figures.BodyHtmlBytes);

        var note = inbox.Messages.Single(m => m.Subject == UmlautSubjectIn(name));
        Assert.IsNull(note.BodyHtml, "a plain-text draft stores no HTML");
        if (name == "ansi.pst")
        {
            // text an ANSI file's code page cannot hold: Outlook keeps it as RTF marked \fromtext, with no plain body
            Assert.IsNull(note.Body);
            StringAssert.StartsWith(note.BodyRtf, "{\\rtf1\\ansi\\ansicpg1252\\fromtext");
            StringAssert.Contains(note.BodyRtf, "Name edge cases.");
        }
        else
        {
            Assert.AreEqual("Name edge cases. naïve café – 日本語", note.Body);
            Assert.IsNull(note.BodyRtf);
        }

        var kickOff = inbox.FindFolder("Projects")!.Messages.Single();
        var rtf = kickOff.BodyRtf;
        Assert.IsNotNull(rtf, "a rich-text mail stores compressed RTF");
        StringAssert.StartsWith(rtf, "{\\rtf1\\ansi\\ansicpg1252");
        StringAssert.Contains(rtf, "In a subfolder.");
        Assert.IsNull(kickOff.BodyHtml);
        Assert.IsTrue(kickOff.Properties.Contains(PstPropertyIds.RtfCompressed));
    }

    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public void Reads_recipients(string name)
    {
        using var pst = PstFile.Open(Fixtures.Path(name));
        var inbox = pst.TopFolder.FindFolder("Inbox")!;

        var figures = inbox.Messages.Single(m => m.Subject == "Quarterly figures");
        var to = figures.Recipients.Single();
        Assert.AreEqual(PstRecipientType.To, to.Type);
        StringAssert.Contains(to.DisplayName, Tester);
        StringAssert.Contains(figures.DisplayTo, Tester);

        var kickOff = inbox.FindFolder("Projects")!.Messages.Single();
        var recipients = kickOff.Recipients;
        Assert.AreEqual(3, recipients.Count);
        CollectionAssert.AreEqual(new[] { PstRecipientType.To, PstRecipientType.To, PstRecipientType.Cc }, recipients.Select(r => r.Type).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { Tester, "adelev@example.test", "megan.bowen@example.test" },
            recipients.Select(r => r.SmtpAddress ?? r.EmailAddress ?? r.DisplayName).ToArray());
        Assert.IsTrue(recipients.All(r => r.Properties.Contains(PstPropertyIds.RecipientType)));
        StringAssert.Contains(kickOff.DisplayCc, "megan.bowen@example.test");
    }

    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public void Reads_attachments(string name)
    {
        using var pst = PstFile.Open(Fixtures.Path(name));
        var inbox = pst.TopFolder.FindFolder("Inbox")!;

        var csv = inbox.Messages.Single(m => m.Subject == "Quarterly figures").Attachments.Single();
        Assert.AreEqual(PstAttachMethod.ByValue, csv.Method);
        Assert.AreEqual("figures.csv", csv.FileName);
        Assert.AreEqual("figures.csv", csv.LongFileName);
        Assert.AreEqual("figures.csv", csv.DisplayName);
        Assert.IsFalse(csv.IsInline);
        Assert.IsNull(csv.ContentId);
        Assert.AreEqual(-1, csv.RenderingPosition);
        Assert.AreEqual("quarter,amount\nQ3,42\nQ4,58\n", Encoding.ASCII.GetString(csv.GetContent()));
        Assert.AreEqual(27, csv.Size);
        Assert.IsTrue(csv.Properties.GetInt64(PstPropertyIds.AttachSize) > 27, "PidTagAttachSize counts the overhead");

        var png = inbox.Messages.Single(m => m.Subject == "Photo from the site visit").Attachments.Single();
        Assert.AreEqual("dot.png", png.FileName);
        Assert.AreEqual("anyfs-dot", png.ContentId);
        Assert.AreEqual(0x4, png.Flags & 0x4, "rendered in the body");
        Assert.IsTrue(png.IsInline);
        CollectionAssert.AreEqual(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="), png.GetContent());
        Assert.AreEqual(70, png.Size);
    }

    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public void Streams_a_large_attachment(string name)
    {
        using var pst = PstFile.Open(Fixtures.Path(name));
        var large = pst.TopFolder.FindFolder("Inbox")!.Messages.Single(m => m.Subject == "Large attachment").Attachments.Single();
        Assert.AreEqual("pattern.bin", large.FileName);
        Assert.AreEqual(300_000, large.Size);

        var content = large.GetContent();
        Assert.AreEqual(300_000, content.Length);
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != (byte)(i % 251))
            {
                Assert.Fail($"byte {i} is {content[i]}, not {i % 251}");
            }
        }

        using var stream = large.OpenContent();
        Assert.IsTrue(stream.CanSeek);
        Assert.AreEqual(300_000, stream.Length);
        stream.Position = 123_456;
        var piece = new byte[10];
        stream.ReadExactly(piece);
        CollectionAssert.AreEqual(Enumerable.Range(123_456, 10).Select(i => (byte)(i % 251)).ToArray(), piece);
        stream.Seek(-3, SeekOrigin.End);
        Assert.AreEqual(3, stream.Read(new byte[8], 0, 8));
        Assert.AreEqual(0, stream.Read(new byte[8], 0, 8));
    }

    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public void Reads_an_embedded_message(string name)
    {
        using var pst = PstFile.Open(Fixtures.Path(name));
        var forwarded = pst.TopFolder.FindFolder("Inbox")!.Messages.Single(m => m.Subject == "Forwarded: site visit");
        var attachment = forwarded.Attachments.Single();
        Assert.AreEqual(PstAttachMethod.EmbeddedMessage, attachment.Method);
        Assert.AreEqual(0, attachment.GetContent().Length, "an embedded message has no bytes of its own");

        var inner = attachment.EmbeddedMessage;
        Assert.IsNotNull(inner);
        Assert.AreEqual("Photo from the site visit", inner.Subject);
        Assert.AreEqual("IPM.Note", inner.MessageClass);
        StringAssert.Contains(inner.BodyHtml, "Here it is:");
        var png = inner.Attachments.Single();
        Assert.AreEqual("anyfs-dot", png.ContentId);
        Assert.AreEqual(70, png.GetContent().Length);
        Assert.AreEqual(1, inner.Recipients.Count);
    }

    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public void Reads_named_properties(string name)
    {
        using var pst = PstFile.Open(Fixtures.Path(name));
        Assert.IsTrue(pst.NamedProperties.Count > 100, $"{pst.NamedProperties.Count} named properties");

        var contact = pst.TopFolder.FindFolder("Contacts")!.Messages.Single();
        Assert.AreEqual("IPM.Contact", contact.MessageClass);
        Assert.AreEqual("Adele Vance", contact.Subject);
        Assert.AreEqual("Adele", contact.Properties.GetString(PstPropertyIds.GivenName));
        Assert.AreEqual("Vance", contact.Properties.GetString(PstPropertyIds.Surname));
        Assert.AreEqual("Contoso", contact.Properties.GetString(PstPropertyIds.CompanyName));
        Assert.AreEqual("Retail Manager", contact.Properties.GetString(PstPropertyIds.Title));
        var email1 = pst.NamedProperties.Find(PsetidAddress, 0x8083);   // PidLidEmail1EmailAddress
        Assert.IsNotNull(email1);
        Assert.AreEqual("adelev@example.test", contact.Properties.GetString(email1.PropertyId));
        var decoded = contact.Properties.Get(email1.PropertyId)!.Value;
        Assert.IsTrue(decoded.IsNamed);
        Assert.AreEqual(email1, decoded.Named);

        var appointment = pst.TopFolder.FindFolder("Calendar")!.Messages.Single();
        Assert.AreEqual("IPM.Appointment", appointment.MessageClass);
        Assert.AreEqual("Design review", appointment.Subject);
        StringAssert.Contains(appointment.BodyHtml, "Agenda: the Outlook source.");
        var location = pst.NamedProperties.Find(PsetidAppointment, 0x8208)!;   // PidLidLocation
        Assert.AreEqual("Room 4", appointment.Properties.GetString(location.PropertyId));
        var start = pst.NamedProperties.Find(PsetidAppointment, 0x820D)!;      // PidLidAppointmentStartWhole
        Assert.IsNotNull(appointment.Properties.GetDateTime(start.PropertyId));
        Assert.AreEqual(DateTimeKind.Utc, appointment.Properties.GetDateTime(start.PropertyId)!.Value.Kind);

        var keywords = pst.NamedProperties.Find(PstNamedProperties.PublicStringsPropertySet, "Keywords")!;
        var figures = pst.TopFolder.FindFolder("Inbox")!.Messages.Single(m => m.Subject == "Quarterly figures");
        CollectionAssert.AreEqual(new[] { "Red", "Blue" }, (string[])figures.Properties[keywords.PropertyId]!);
        Assert.AreEqual("Keywords", figures.Properties.Get(keywords.PropertyId)!.Value.Name);
    }

    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public void Every_property_of_every_item_decodes(string name)
    {
        using var pst = PstFile.Open(Fixtures.Path(name));
        var items = 0;
        var values = 0;
        foreach (var folder in pst.EnumerateFolders())
        {
            values += folder.Properties.Count();
            foreach (var message in folder.Messages.Concat(folder.AssociatedMessages))
            {
                items++;
                foreach (var property in message.Properties)
                {
                    values++;
                    Assert.AreEqual(property.Id, message.Properties.Get(property.Id)!.Value.Id);
                    if (property.Name is null && !property.IsNamed)
                    {
                        Assert.IsTrue(property.Id < 0x8000);
                    }
                }

                values += message.Recipients.Sum(r => r.Properties.Count());
                foreach (var attachment in message.Attachments)
                {
                    values += attachment.Properties.Count();
                    _ = attachment.GetContent();
                }
            }
        }

        TestContext.WriteLine($"{items} items, {values} property values");
        Assert.AreEqual(8, items);
        Assert.IsTrue(values > 200);
    }

    [TestMethod]
    [DataRow("ansi.pst")]
    [DataRow("unicode.pst")]
    public void Reads_from_a_stream_too(string name)
    {
        using var fromPath = PstFile.Open(Fixtures.Path(name));
        using var stream = new MemoryStream(File.ReadAllBytes(Fixtures.Path(name)));
        using var fromStream = PstFile.Open(stream, leaveOpen: true);
        Assert.AreEqual(fromPath.Format, fromStream.Format);
        CollectionAssert.AreEqual(
            fromPath.EnumerateFolders().Select(f => $"{f.Nid:X} {f.Name}").ToList(),
            fromStream.EnumerateFolders().Select(f => $"{f.Nid:X} {f.Name}").ToList());
        var csv = fromStream.TopFolder.FindFolder("Inbox")!.Messages.Single(m => m.Subject == "Quarterly figures").Attachments.Single();
        Assert.AreEqual(27, csv.GetContent().Length);
        Assert.IsTrue(stream.CanRead, "leaveOpen keeps the stream");
    }

    [TestMethod]
    public void Refuses_a_file_that_is_not_a_pst()
    {
        var temp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(temp, new byte[4096]);
            var ex = Assert.ThrowsExactly<PstException>(() => PstFile.Open(temp));
            StringAssert.Contains(ex.Message, "not an Outlook data file");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>The ANSI file stores the subject in code page 1252, where ü survives; the Unicode file stores UTF-16.</summary>
    private static string UmlautSubjectIn(string name) => UmlautSubject;
}
