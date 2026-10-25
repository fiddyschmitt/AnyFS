using System.Buffers.Binary;
using LibOutlook.Ndb;

namespace LibOutlook.Tests;

/// <summary>
/// Copies of a fixture with chosen heaps damaged the way a real OST's was (25-Oct-2026, years of a mailbox: "HID
/// 0x0000CD20 names allocation 1641 of 616 in heap block 0"): a heap's first page map claims fewer allocations than it
/// holds, so records name allocations it has not. The block's CRC is made again, so the block reads sound and only the
/// heap is wrong, as in a file Outlook itself left so.
/// </summary>
internal static class Damage
{
    /// <summary>A claim of as many allocations as a property context's own BTH takes (a one-level tree's header and records).</summary>
    public const int OwnTree = 0;

    /// <summary>A claim of as many allocations as a table context's own structures take (its TCINFO, its row index and its rows in the heap): the cells' values allocated after them are past the claim.</summary>
    public const int OwnTable = -1;

    /// <summary>
    /// fixtures/damaged.pst from unicode.pst. The heap of the Inbox's Projects folder claims one allocation, its BTH's header:
    /// its property records do not read. The "Plain note" mail's claims only its own BTH's: its records read, the values
    /// allocated after them do not. The Contacts folder's contents table claims one, its TCINFO: its row index does not read.
    /// The Projects folder's contents table claims only its own structures': its row reads, cells naming values allocated
    /// after them name what the table does not hold, as hundreds of a real OST's contents tables' cells did.
    /// </summary>
    public static void Fixture(string unicodePst, string target)
    {
        (uint, int)[] damage;
        using (var pst = PstFile.Open(unicodePst))
        {
            var inbox = pst.TopFolder.FindFolder("Inbox")!;
            var projects = inbox.FindFolder("Projects")!.Nid;
            damage =
            [
                (projects, 1),
                (inbox.Messages.Single(m => m.Subject.StartsWith("Plain note", StringComparison.Ordinal)).Nid, OwnTree),
                (NodeId.WithType(pst.TopFolder.FindFolder("Contacts")!.Nid, NodeId.TypeContentsTable), 1),
                (NodeId.WithType(projects, NodeId.TypeContentsTable), OwnTable),
            ];
        }

        Heaps(unicodePst, target, damage);
    }

    /// <summary>A copy of <paramref name="source"/> in which each node's heap claims the given number of allocations in its first block (<see cref="OwnTree"/>: its BTH's).</summary>
    public static void Heaps(string source, string target, IReadOnlyList<(uint Nid, int Claimed)> damage)
    {
        var blocks = new List<(long At, int Cb, int Total, ulong Bid, int Claimed)>();
        PstEncoding encoding;
        FileLayout layout;
        using (var pst = PstFile.Open(source))
        {
            encoding = pst.Encoding;
            layout = pst.Blocks.Layout;
            foreach (var (nid, claimed) in damage)
            {
                var node = pst.FindNode(nid) ?? throw new InvalidOperationException($"the file has no node 0x{nid:X}");
                if ((node.BidData & 2) != 0)
                {
                    throw new InvalidOperationException($"node 0x{nid:X}'s data is spread over blocks: its first is not where its BID says");
                }

                var entry = pst.FindBlock(node.BidData) ?? throw new InvalidOperationException($"node 0x{nid:X}'s block is not in the block B-tree");
                blocks.Add(((long)entry.Ib, entry.Cb, layout.BlockTotalSize(entry.Cb), entry.Bid, claimed));
            }
        }

        File.Copy(source, target, overwrite: true);
        using var file = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        foreach (var (at, cb, total, bid, claimed) in blocks)
        {
            var raw = new byte[total];
            file.Position = at;
            file.ReadExactly(raw);
            var data = raw.AsSpan(0, cb).ToArray();
            Crypt.Decode(data, encoding, bid);
            var map = BinaryPrimitives.ReadUInt16LittleEndian(data);
            var claim = claimed switch
            {
                OwnTree => OwnTreeAllocations(data, map),
                OwnTable => OwnTableAllocations(data, map),
                _ => claimed,
            };
            if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(map)) <= claim)
            {
                throw new InvalidOperationException($"block {bid}'s heap holds no more than {claim} allocations: claiming {claim} damages nothing");
            }

            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(map), (ushort)claim);
            Encode(data, encoding);
            data.CopyTo(raw, 0);

            // the CRC of the data as stored: the trailer is cb, wSig, dwCRC, bid in a Unicode file (cb, wSig, bid, dwCRC in an ANSI one)
            var trailer = total - layout.BlockTrailerSize;
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(layout.Is64 ? trailer + 4 : trailer + 8), Crypt.Crc(raw.AsSpan(0, cb)));
            file.Position = at;
            file.Write(raw);
        }
    }

    /// <summary>The highest allocation a PC heap's own BTH takes: its header (the heap's user root) and its root, in a file of 512-byte pages' HIDs.</summary>
    private static int OwnTreeAllocations(byte[] heap, int map)
    {
        static int Index(uint hid) => (int)((hid >> 5) & 0x7FF);
        int Start(int index) => BinaryPrimitives.ReadUInt16LittleEndian(heap.AsSpan(map + 4 + (index - 1) * 2));
        var userRoot = BinaryPrimitives.ReadUInt32LittleEndian(heap.AsSpan(4));
        var header = Start(Index(userRoot));
        if (heap[header + 3] != 0)
        {
            throw new NotSupportedException("a BTH of more than one level");
        }

        return Math.Max(Index(userRoot), Index(BinaryPrimitives.ReadUInt32LittleEndian(heap.AsSpan(header + 4))));
    }

    /// <summary>The highest allocation a TC heap's own structures take: its TCINFO (the user root), its row index's header and root, and its rows when they are in the heap.</summary>
    private static int OwnTableAllocations(byte[] heap, int map)
    {
        static int Index(uint hid) => (int)((hid >> 5) & 0x7FF);
        int Start(int index) => BinaryPrimitives.ReadUInt16LittleEndian(heap.AsSpan(map + 4 + (index - 1) * 2));
        var userRoot = BinaryPrimitives.ReadUInt32LittleEndian(heap.AsSpan(4));
        var info = Start(Index(userRoot));
        var rowIndex = BinaryPrimitives.ReadUInt32LittleEndian(heap.AsSpan(info + 10));
        var rows = BinaryPrimitives.ReadUInt32LittleEndian(heap.AsSpan(info + 14));
        var header = Start(Index(rowIndex));
        if (heap[header + 3] != 0)
        {
            throw new NotSupportedException("a row index of more than one level");
        }

        var claim = Math.Max(Index(userRoot), Math.Max(Index(rowIndex), Index(BinaryPrimitives.ReadUInt32LittleEndian(heap.AsSpan(header + 4)))));
        return (rows & 0x1F) == 0 ? Math.Max(claim, Index(rows)) : claim;
    }

    /// <summary>CryptPermute with fEncrypt true: every byte through mpbbR ([MS-PST] 5.1), the inverse of the decoding.</summary>
    private static void Encode(Span<byte> data, PstEncoding encoding)
    {
        switch (encoding)
        {
            case PstEncoding.None:
                return;
            case PstEncoding.Permute:
                var r = CryptTables.R;
                for (var i = 0; i < data.Length; i++)
                {
                    data[i] = r[data[i]];
                }

                return;
            default:
                throw new NotSupportedException($"damaging a file protected by {encoding}");
        }
    }
}

/// <summary>
/// A file with damaged heaps (fixtures/damaged.pst, see <see cref="Damage"/>): what can be read is read, and what cannot
/// says where it is, as a real OST's damage did not.
/// </summary>
[TestClass]
public class DamagedFileTests
{
    private const ushort AttributeHidden = 0x10F4;

    private static PstFile Open() => PstFile.Open(Fixtures.Path("damaged.pst"));

    [TestMethod]
    public void The_damaged_fixture_is_the_unicode_one_with_its_heaps_damaged()
    {
        var made = Path.Combine(Path.GetTempPath(), $"liboutlook-damaged-{Guid.NewGuid():N}.pst");
        try
        {
            Damage.Fixture(Fixtures.Path("unicode.pst"), made);
            if (Environment.GetEnvironmentVariable("LIBOUTLOOK_MAKE_FIXTURES") is { Length: > 0 } into)
            {
                File.Copy(made, Path.Combine(into, "damaged.pst"), overwrite: true);
            }

            Assert.IsTrue(File.ReadAllBytes(Fixtures.Path("damaged.pst")).AsSpan().SequenceEqual(File.ReadAllBytes(made)), "fixtures/damaged.pst is what Damage.Fixture makes of unicode.pst");
        }
        finally
        {
            File.Delete(made);
        }
    }

    [TestMethod]
    public void A_folder_whose_own_properties_are_damaged_keeps_its_place_and_says_where()
    {
        using var pst = Open();
        var inbox = pst.TopFolder.FindFolder("Inbox")!;
        var projects = inbox.FindFolder("Projects");
        Assert.IsNotNull(projects, "named from the Inbox's subfolder table, which is sound");
        Assert.AreEqual("IPF.Note", projects.ContainerClass, "from the subfolder table too");
        Assert.AreEqual(1, projects.Messages.Count(), "its contents table is a node of its own, and sound");
        var ex = Assert.ThrowsExactly<PstException>(() => projects.Properties.GetBoolean(AttributeHidden));
        StringAssert.StartsWith(ex.Message, $"folder 0x{projects.Nid:X}: HID 0x");
        StringAssert.EndsWith(ex.Message, " of 1 in heap block 0 (a heap of one block)");
        Assert.ThrowsExactly<PstException>(() => pst.GetFolder(projects.Nid)!.Name, "found by its id, it has only its own properties to name it");
    }

    [TestMethod]
    public void A_message_whose_own_values_are_damaged_is_still_listed_from_its_folder()
    {
        using var pst = Open();
        var inbox = pst.TopFolder.FindFolder("Inbox")!;
        var messages = inbox.Messages.ToList();
        Assert.AreEqual(5, messages.Count);
        var plain = messages.Single(m => m.Subject.StartsWith("Plain note", StringComparison.Ordinal));
        var ex = Assert.ThrowsExactly<PstException>(() => plain.Body);
        StringAssert.StartsWith(ex.Message, $"message 0x{plain.Nid:X}, property 0x1000 (Body) of type 0x001F: HID 0x");

        var listed = plain.AsListed();
        Assert.IsNotNull(listed);
        StringAssert.StartsWith(listed.Subject, "Plain note");
        Assert.IsNotNull(listed.LastModificationTime, "from the contents table's row");
        Assert.IsNull(listed.Body, "the folder's table holds no body, and the message itself is not read");
        Assert.IsNull(listed.CreationTime, "nor its creation time, which only the message holds");
        Assert.IsNull(pst.GetMessage(plain.Nid)!.AsListed(), "a message found by its id was listed from no folder");
        Assert.IsTrue(messages.Where(m => m != plain).All(m => m.Body is not null || m.BodyHtml is not null || m.BodyRtf is not null), "the other mails read whole");
    }

    [TestMethod]
    public void A_damaged_contents_table_says_whose_it_is()
    {
        using var pst = Open();
        var contacts = pst.TopFolder.FindFolder("Contacts")!;
        var ex = Assert.ThrowsExactly<PstException>(() => contacts.Messages.ToList());
        StringAssert.StartsWith(ex.Message, $"the contents table of folder 0x{contacts.Nid:X}: HID 0x");
        Assert.AreEqual(1, pst.TopFolder.FindFolder("Calendar")!.Messages.Count(), "the other folders' tables are sound");
    }

    [TestMethod]
    public void A_cell_naming_a_value_its_table_does_not_hold_reads_as_unset_and_the_item_stands_in()
    {
        // the Projects folder's contents table holds its row, but not the values its cells name (as hundreds of a real OST's did)
        using var pst = Open();
        var projects = pst.TopFolder.FindFolder("Inbox")!.FindFolder("Projects")!;
        Assert.AreEqual(0, pst.MissingValues, "nothing read yet");
        var mail = projects.Messages.Single();
        Assert.AreEqual("RE: Project kick-off", mail.Subject, "the mail's own subject stands in for its cell");
        Assert.AreEqual("IPM.Note", mail.MessageClass);
        Assert.IsTrue(pst.MissingValues >= 2, $"{pst.MissingValues} cells");
        var example = pst.MissingValueExamples.First(e => e.Contains("(Subject)", StringComparison.Ordinal));
        StringAssert.StartsWith(example, $"the contents table of folder 0x{projects.Nid:X}, the row of 0x{mail.Nid:X}, property 0x0037 (Subject) of type 0x001F in a cell of 4 bytes: HID 0x");
        StringAssert.EndsWith(example, " in heap block 0 (a heap of one block)");

        var listed = mail.AsListed()!;
        Assert.AreEqual("", listed.Subject, "as the folder lists it: the cell reads as unset");
        Assert.IsNotNull(listed.LastModificationTime, "the cells held in the row itself read");
        Assert.AreEqual(projects.Messages.Single().Recipients.Count, mail.Recipients.Count, "the mail itself is sound");
    }

    [TestMethod]
    public void A_table_cell_that_is_not_set_is_not_looked_for_in_the_item()
    {
        // a column of the table whose cell is unset is a property the item has not: the item is not read for it. The Projects
        // folder's own properties do not read at all, so any look in them throws.
        using var pst = Open();
        var projects = pst.TopFolder.FindFolder("Inbox")!.FindFolder("Projects")!;
        Assert.IsNull(projects.Properties.Get(0x0E30), "PidTagReplItemid: a column of the Inbox's subfolder table, unset in the row");
        Assert.ThrowsExactly<PstException>(() => projects.Properties.Get(PstPropertyIds.CreationTime), "no column of the table: the folder's own properties are read for it");

        var mail = pst.TopFolder.FindFolder("Inbox")!.Messages.Single(m => m.Subject.StartsWith("Plain note", StringComparison.Ordinal));
        Assert.IsNull(mail.ClientSubmitTime, "a draft's: unset in the Inbox's row");
        Assert.IsFalse(mail.HasAttachments, "the row's message flags say so");
    }
}
