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

    /// <summary>
    /// Every cell a folder's table holds for an item agrees with the item's own property: the table mirrors the item, so
    /// a listing from the table alone is the one Outlook shows. Prints how each big table's heap is laid out, to tell a
    /// reader's mistake from a file's damage (a real OST listed one sender in five wrong, 25-Oct-2026). Point
    /// LIBOUTLOOK_TEST_OST at any OST to check it.
    /// </summary>
    [TestMethod]
    public void Table_cells_agree_with_the_items()
    {
        using var ost = Open();
        var compared = 0;
        var tableOnly = new Dictionary<ushort, int>();
        var differing = new List<string>();
        foreach (var folder in ost.EnumerateFolders())
        {
            var messages = folder.Messages.ToList();
            if (messages.Count > 100)
            {
                TestContext.WriteLine($"'{folder.Name}': {messages.Count} items; the contents table's heap: {HeapLayout(ost, folder)}");
            }

            foreach (var message in messages)
            {
                var listed = message.AsListed()!;
                var own = ost.GetMessage(message.Nid);
                Assert.IsNotNull(own, $"message 0x{message.Nid:X} of '{folder.Name}' is in the node B-tree");
                foreach (var cell in listed.Properties)
                {
                    var mine = own.Properties.Get(cell.Id);
                    if (mine is null)
                    {
                        tableOnly[cell.Id] = tableOnly.GetValueOrDefault(cell.Id) + 1;
                        continue;
                    }

                    compared++;
                    if (!Same(cell.Value, mine.Value.Value))
                    {
                        differing.Add($"'{folder.Name}' 0x{message.Nid:X}: the table says {cell}; the item has {mine}");
                    }
                }
            }
        }

        TestContext.WriteLine($"{compared} cells compared; columns the items have not: {string.Join(", ", tableOnly.OrderBy(t => t.Key).Select(t => $"0x{t.Key:X4} ({t.Value})"))}");
        TestContext.WriteLine(string.Join(Environment.NewLine, differing.Take(80)));
        Assert.AreEqual(0, differing.Count, $"{differing.Count} of {compared} cells differ from the items' own properties (the first are printed)");
    }

    /// <summary>
    /// The 4 KiB OST format's column stores (subnodes of a contents table holding a heap of client signature 0xA5, one
    /// allocation per value, which the row's cell indexes instead of the table's heap): every store with values in it is
    /// placed in a column, and the file has them (the sender's name and the conversation id, under subnodes 0x541 and
    /// 0x521 in every contents table seen). Prints each table's stores and which columns' cells still equal their row's
    /// number + 1, as a fresh table's do (a churned one's no longer: the cells are allocation indices, stable across
    /// deletions).
    /// </summary>
    [TestMethod]
    public void Column_stores_are_placed_in_every_contents_table()
    {
        using var ost = Open();
        var withStores = 0;
        foreach (var folder in ost.EnumerateFolders())
        foreach (var kind in new[] { LibOutlook.NodeId.TypeContentsTable, LibOutlook.NodeId.TypeAssocContentsTable, LibOutlook.NodeId.TypeHierarchyTable, LibOutlook.NodeId.TypeSearchContentsTable })
        {
            var node = ost.FindNode(LibOutlook.NodeId.WithType(folder.Nid, kind));
            if (node is null)
            {
                continue;
            }

            var context = ost.Context(node.Value);
            LibOutlook.Ltp.TableContext? table;
            try
            {
                table = LibOutlook.Ltp.TableContext.TryOpen(context);
            }
            catch (PstException ex)
            {
                TestContext.WriteLine($"'{folder.Name}': {ex.Message}");
                continue;
            }

            if (table is null || table.RowCount == 0)
            {
                continue;
            }

            var rows = table.Rows().ToList();
            var heaps = new List<string>();
            foreach (var (nid, sub) in context.Subnodes.OrderBy(s => s.Key))
            {
                var data = context.Blocks.ReadDataBlocks(sub.BidData);
                if (data[0].Length > 4 && data[0][2] == 0xEC)
                {
                    var pages = new LibOutlook.Ltp.HeapNode(data, context.WideHids).Pages().ToList();
                    heaps.Add($"0x{nid:X} (index {nid >> 5}, kind 0x{data[0][3]:X2}, {pages.Sum(p => p.Allocations)} allocations in {pages.Count} block(s))");
                    if (pages.Sum(p => p.Allocations) > 0)
                    {
                        Assert.IsTrue(kind == LibOutlook.NodeId.TypeContentsTable, $"a column store under {LibOutlook.NodeId.Describe(node.Value.Nid)}: only contents tables were known to have them");
                        withStores++;
                    }
                }
            }

            // reading a cell places the stores; one no column could be placed in is noted among the file's missing values
            var noted = ost.MissingValues;
            _ = rows[0].Get(0x0042);
            _ = rows[0].Get(0x3013);
            Assert.AreEqual(noted, ost.MissingValues, $"{LibOutlook.NodeId.Describe(node.Value.Nid)} '{folder.Name}': {string.Join(" | ", ost.MissingValueExamples)}");

            var indexed = new List<string>();
            foreach (var column in table.Columns.Where(c => c.Size == 4 && LibOutlook.Ltp.PropertyValues.FixedSize(c.Type) is 0 or > 8))
            {
                var set = 0;
                var byRow = 0;
                for (var i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Reference(column.Id) is { } r && r != 0)
                    {
                        set++;
                        if (r == (uint)(i + 1) << 5)
                        {
                            byRow++;
                        }
                    }
                }

                if (byRow > 0)
                {
                    indexed.Add($"{column.Id:X4}:{column.Type:X4} bit {column.Bit} ({byRow} of {set} set cells)");
                }
            }

            if (heaps.Count > 0 || indexed.Count > 0)
            {
                var senderCells = rows.Select(r => r.Reference(0x0042)).Select(r => r is null ? "unset" : $"0x{r:X}").Take(4);
                TestContext.WriteLine($"{LibOutlook.NodeId.Describe(node.Value.Nid)} '{folder.Name}': {rows.Count} rows, {table.Columns.Count} columns; subnode heaps: {string.Join("; ", heaps)}; columns whose cells equal their row + 1: {string.Join("; ", indexed)}; first sender cells {string.Join(", ", senderCells)}");
            }
        }

        Assert.IsTrue(withStores > 0, "a 4 KiB OST keeps column stores");
    }

    private static string HeapLayout(PstFile ost, PstFolder folder)
    {
        var node = ost.FindNode(NodeId.WithType(folder.Nid, NodeId.TypeContentsTable));
        if (node is null)
        {
            return "no table";
        }

        var pages = ost.Context(node.Value).Heap().Pages().ToList();
        return $"{pages.Count} block{(pages.Count == 1 ? "" : "s")}: " + string.Join(", ", pages.Select(p => $"{p.Allocations} allocations ({p.Freed} freed) in {p.Size} bytes"));
    }

    private static bool Same(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        _ => Equals(a, b),
    };

    /// <summary>The copy taken while Outlook had the file open reads the same as the one taken after it quit.</summary>
    [TestMethod]
    public void Reads_the_copy_taken_while_outlook_ran()
    {
        var live = Fixtures.LiveOst();
        var cleanPath = Fixtures.CleanOst();
        if (live is null || cleanPath is null)
        {
            Assert.Inconclusive("no live OST capture on this machine");
        }

        using var clean = PstFile.Open(cleanPath!);
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
