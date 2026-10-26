using System.Text;
using AnyFS.Sources.Outlook.Ews;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The address book client's wire reader (<see cref="NspiReader"/>) and its row-to-card mapping
/// (<see cref="NspiAddressBook.EntryOf"/>), apart from the network. The whole path against a real Exchange server is
/// covered by the live test <c>TheLabExchangeServerIsServedOverSoap</c>, which proved the reader against real bytes.
/// </summary>
[TestClass]
public sealed class NspiAddressBookTests
{
    // property ids ([MS-OXPROPS], the high word of the tag): what EntryOf keys a row by
    private const ushort DisplayName = 0x3001, Smtp = 0x39FE, Title = 0x3A17, Department = 0x3A18, Office = 0x3A19, Company = 0x3A16;
    private const ushort BusinessPhone = 0x3A08, MobilePhone = 0x3A1C, Street = 0x3A29, City = 0x3A27, State = 0x3A28, PostalCode = 0x3A2A, Country = 0x3A26;
    private const ushort Proxies = 0x800F, DisplayType = 0x3900, DisplayTypeEx = 0x3905, GivenName = 0x3A06, Surname = 0x3A11;

    [TestMethod]
    public void AFullRowBecomesACardWithEveryField()
    {
        var entry = NspiAddressBook.EntryOf(new Dictionary<ushort, object?>
        {
            [DisplayName] = "Exchange Other",
            [GivenName] = "Exchange",
            [Surname] = "Other",
            [Smtp] = "ex_other@anyfs.lab",
            [Title] = "Lab Engineer",
            [Department] = "Testing",
            [Company] = "AnyFS Lab",
            [Office] = "Building 4, 2.10",
            [BusinessPhone] = "+61 2 5550 0101",
            [MobilePhone] = "+61 400 000 001",
            [Street] = "1 Lab Street",
            [City] = "Sydney",
            [State] = "NSW",
            [PostalCode] = "2000",
            [Country] = "Australia",
            [Proxies] = new[] { "SMTP:ex_other@anyfs.lab", "smtp:other@anyfs.lab", "SIP:ex_other@anyfs.lab", "X400:c=US;a=;p=Lab;" },
            [DisplayType] = 0u,
        })!;

        Assert.AreEqual("Exchange Other", entry.Name);
        Assert.AreEqual("Person", entry.Kind);
        Assert.AreEqual("Lab Engineer", entry.JobTitle);
        Assert.AreEqual("Testing", entry.Department);
        Assert.AreEqual("Building 4, 2.10", entry.Office);
        Assert.AreEqual("AnyFS Lab", entry.Company);
        CollectionAssert.AreEqual(new[] { "ex_other@anyfs.lab", "other@anyfs.lab" }, entry.Emails.ToList(), "the primary SMTP, then the other SMTP proxy; the X.400 one left out");
        CollectionAssert.AreEqual(new[] { new DirectoryPhone("WORK,VOICE", "+61 2 5550 0101"), new DirectoryPhone("CELL,VOICE", "+61 400 000 001") }, entry.Phones.ToList());
        Assert.AreEqual("sip:ex_other@anyfs.lab", entry.ImAddress);
        var card = Encoding.UTF8.GetString(Outlook.Files.ContactFile.Build(entry)).Replace("\r\n ", "", StringComparison.Ordinal);
        StringAssert.Contains(card, "ORG:AnyFS Lab;Testing\r\n");
        StringAssert.Contains(card, "ADR;TYPE=WORK:;Building 4" + (char)92 + ", 2.10;1 Lab Street;Sydney;NSW;2000;Australia\r\n");
    }

    [TestMethod]
    public void ARoomAndABareEntryAreReadToo()
    {
        var room = NspiAddressBook.EntryOf(new Dictionary<ushort, object?> { [DisplayName] = "Conf Room 1", [Smtp] = "room1@anyfs.lab", [DisplayTypeEx] = 0x07u })!;
        Assert.AreEqual("Room", room.Kind);

        var bare = NspiAddressBook.EntryOf(new Dictionary<ushort, object?> { [Smtp] = "list@anyfs.lab", [DisplayType] = 1u })!;
        Assert.AreEqual("list@anyfs.lab", bare.Name, "no display name: its address stands in");
        Assert.AreEqual("DistributionList", bare.Kind);

        Assert.IsNull(NspiAddressBook.EntryOf(new Dictionary<ushort, object?> { [Title] = "nobody" }), "neither a name nor an address: nothing");
    }

    [TestMethod]
    public void TheReaderParsesAPlainRow()
    {
        uint[] columns = [0x3001001F, 0x3A08001F, 0x800F101F]; // display name, business phone, proxy addresses
        var body = Build(w =>
        {
            w.Write((byte)0); // Flags: plain values
            PutString(w, "Ann Example");
            w.Write((byte)0); // the phone absent (HasValue false)
            PutStrings(w, "SMTP:ann@example.test", "smtp:ann2@example.test");
        });

        var row = new NspiReader(body, 0, "test").Row(columns);
        Assert.AreEqual("Ann Example", row[0x3001]);
        Assert.IsNull(row[0x3A08]);
        CollectionAssert.AreEqual(new[] { "SMTP:ann@example.test", "smtp:ann2@example.test" }, (string[])row[0x800F]!);
    }

    [TestMethod]
    public void TheReaderParsesAFlaggedRowWithAMissingAndAnErrorValue()
    {
        uint[] columns = [0x3001001F, 0x3A17001F, 0x3A08001F]; // display name, title, business phone
        var body = Build(w =>
        {
            w.Write((byte)1); // Flags: each value led by a flag byte
            w.Write((byte)0); // present
            PutString(w, "Bo Example");
            w.Write((byte)1); // not present
            w.Write((byte)0xA); // an error where the value would be
            w.Write(0x8004010Fu); // MAPI_E_NOT_FOUND
        });

        var row = new NspiReader(body, 0, "test").Row(columns);
        Assert.AreEqual("Bo Example", row[0x3001]);
        Assert.IsNull(row[0x3A17], "the missing value");
        Assert.IsNull(row[0x3A08], "the error value");
    }

    [TestMethod]
    public void TheReaderParsesAHierarchyValueList()
    {
        var body = Build(w =>
        {
            w.Write(2u); // two tagged values
            w.Write((ushort)0x001F); // PropertyType PtypString
            w.Write((ushort)0x3001); // PropertyId DisplayName
            PutString(w, "All Users");
            w.Write((ushort)0x0003); // PtypInteger32
            w.Write((ushort)0xFFFD); // ContainerId
            w.Write(12u);
        });

        var values = new NspiReader(body, 0, "test").ValueList();
        Assert.AreEqual("All Users", values[0x3001]);
        Assert.AreEqual(12u, values[0xFFFD]);
    }

    private static byte[] Build(Action<BinaryWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var w = new BinaryWriter(buffer, Encoding.Unicode, leaveOpen: true))
        {
            write(w);
        }

        return buffer.ToArray();
    }

    private static void PutString(BinaryWriter w, string value)
    {
        w.Write((byte)0xFF); // HasValue
        w.Write(Encoding.Unicode.GetBytes(value));
        w.Write((ushort)0); // the terminating null character
    }

    private static void PutStrings(BinaryWriter w, params string[] values)
    {
        w.Write((byte)0xFF); // HasValue
        w.Write((uint)values.Length);
        foreach (var value in values)
        {
            PutString(w, value);
        }
    }
}
