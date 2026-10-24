namespace LibOutlook.Tests;

/// <summary>
/// The Outlook 2013+ OST format (4 KiB pages, compressed blocks, wide HIDs), against a capture of the lab mailbox that
/// is not shipped with the repository: inconclusive where it is absent (see <see cref="Fixtures.Ost"/>).
/// </summary>
[TestClass]
public class OstTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Is_the_4K_format()
    {
        using var ost = Open();
        Assert.AreEqual(PstFormat.Unicode4K, ost.Format);
        Assert.IsTrue(ost.IsOst);
        Assert.AreEqual(PstEncoding.None, ost.Encoding);
        Assert.IsTrue(ost.Version >= 36, $"wVer {ost.Version}");
        Assert.IsTrue(ost.Length > 0);
        Assert.IsNotNull(ost.MessageStore);
        Assert.IsNotNull(ost.DisplayName, "an OST's store carries a display name, empty as it may be");
        TestContext.WriteLine($"'{ost.DisplayName}': wVer {ost.Version}, {ost.Length} bytes, {ost.NamedProperties.Count} named properties");
    }

    [TestMethod]
    public void Has_the_mailbox_tree()
    {
        using var ost = Open();
        var top = ost.TopFolder;
        Assert.AreNotEqual(ost.RootFolder.Nid, top.Nid, "the mailbox's IPM_SUBTREE is found without the store naming it");
        Assert.AreEqual("IPM_SUBTREE", top.Name);
        var names = top.Folders.Select(f => f.Name).ToList();
        TestContext.WriteLine(string.Join(", ", names));
        CollectionAssert.Contains(names, "Inbox");
        CollectionAssert.Contains(names, "Sent Items");
        CollectionAssert.Contains(names, "Deleted Items");
        CollectionAssert.Contains(names, "Drafts");
        var inbox = top.FindFolder("Inbox")!;
        Assert.AreEqual("IPF.Note", inbox.ContainerClass);
        Assert.IsTrue(inbox.AssociatedMessages.Any(), "the Inbox carries Outlook's hidden settings items");
        var searchFolders = ost.EnumerateFolders().Where(f => f.IsSearchFolder).ToList();
        Assert.IsTrue(searchFolders.Count > 0);
        Assert.IsTrue(searchFolders.Any(f => f.Messages.Any()), "a search folder lists the messages it found (Reminders)");
        Assert.IsTrue(ost.NamedProperties.Count > 0);
    }

    [TestMethod]
    public void Reads_every_item()
    {
        using var ost = Open();
        var folders = 0;
        var messages = 0;
        var properties = 0;
        var attachments = 0;
        long bytes = 0;
        foreach (var folder in ost.EnumerateFolders())
        {
            folders++;
            _ = folder.Name;
            _ = folder.Properties.ToList();
            foreach (var message in folder.Messages.Concat(folder.AssociatedMessages))
            {
                messages++;
                properties += message.Properties.Count();
                _ = message.Subject;
                _ = message.Body;
                _ = message.BodyHtml;
                _ = message.BodyRtf;
                _ = message.Recipients.Select(r => (r.Type, r.DisplayName, r.SmtpAddress)).ToList();
                foreach (var attachment in message.Attachments)
                {
                    attachments++;
                    _ = attachment.Properties.ToList();
                    bytes += attachment.GetContent().Length;
                    if (attachment.EmbeddedMessage is { } inner)
                    {
                        _ = inner.Subject;
                        _ = inner.Properties.ToList();
                        _ = inner.Attachments.Select(a => a.GetContent().Length).ToList();
                    }
                }
            }
        }

        TestContext.WriteLine($"{folders} folders, {messages} items, {properties} property values, {attachments} attachments holding {bytes} bytes");
        Assert.IsTrue(folders > 10, $"{folders} folders");
        Assert.IsTrue(messages > 0, "the mailbox holds items");
    }

    [TestMethod]
    public void Lists_the_inbox_with_summary_cells_before_opening_items()
    {
        using var ost = Open();
        var inbox = ost.TopFolder.FindFolder("Inbox")!;
        var listed = inbox.Messages.Select(m => (m.Nid, m.Subject, m.MessageDeliveryTime, m.Size, m.HasAttachments)).ToList();
        TestContext.WriteLine(string.Join(Environment.NewLine, listed.Select(l => $"{l.Nid:X} {l.MessageDeliveryTime:u} {l.Size} {l.HasAttachments} {l.Subject}")));
        Assert.AreEqual(inbox.ContentCount, listed.Count, "the contents table and the folder's own count agree");
        foreach (var (nid, subject, _, _, _) in listed)
        {
            Assert.AreEqual(subject, ost.GetMessage(nid)!.Subject, "the message opened by NID is the one listed");
        }
    }

    /// <summary>The copy taken while Outlook had the file open reads the same as the one taken after it quit.</summary>
    [TestMethod]
    public void Reads_the_copy_taken_while_outlook_ran()
    {
        var live = Fixtures.LiveOst();
        if (live is null)
        {
            Assert.Inconclusive("no live OST capture on this machine");
        }

        using var clean = Open();
        using var running = PstFile.Open(live!);
        Assert.AreEqual(clean.Format, running.Format);
        var expected = clean.EnumerateFolders().Select(f => $"{f.Nid:X} {f.Name} {f.ContentCount}").ToList();
        var actual = running.EnumerateFolders().Select(f => $"{f.Nid:X} {f.Name} {f.ContentCount}").ToList();
        CollectionAssert.AreEqual(expected, actual);
        var items = 0;
        foreach (var folder in running.EnumerateFolders())
        {
            foreach (var message in folder.Messages.Concat(folder.AssociatedMessages))
            {
                items++;
                _ = message.Properties.ToList();
                _ = message.Attachments.Select(a => a.GetContent().Length).ToList();
            }
        }

        TestContext.WriteLine($"{items} items read from the live copy");
        Assert.IsTrue(items > 0);
    }

    private static PstFile Open()
    {
        var path = Fixtures.Ost();
        if (path is null)
        {
            Assert.Inconclusive("no OST capture on this machine: set LIBOUTLOOK_TEST_OST to one");
        }

        return PstFile.Open(path!);
    }
}
