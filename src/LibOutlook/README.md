# LibOutlook

A reader of Outlook's data files, PST and OST, in plain C# from Microsoft's open specification [MS-PST]. No
dependencies, no MAPI, no Outlook: it reads the file itself, on any platform .NET runs on.

- All three on-disk formats: ANSI (Outlook 97 to 2002), Unicode (Outlook 2003 onwards, every PST made since), and the
  4 KiB-page OST format Outlook 2013 and later keep mailboxes in (compressed blocks included).
- Both obfuscations a PST may use ("compressible" and "high" encryption).
- Folders, messages, recipients, attachments, embedded messages, the plain, HTML and RTF bodies (compressed RTF
  decoded), every MAPI property with its type, and named properties resolved through the file's name-to-id map.
- Read-only, and able to read a file Outlook has open: a path is opened with full sharing and read with ordinary
  positional reads, through the system's file cache; the range Outlook's byte-range lock bars (its header) is read
  through a memory-mapped view, which the lock does not reach. A file that is being written is read with every page
  and block checked (checksums, signatures, back pointers) and re-read when caught mid-write.
- Large attachments are streamed block by block, not loaded whole.
- A file damaged in places reads as far as it reads: a fault names where it is (`message 0x200064, property 0x0037
  (Subject) of type 0x001F: HID 0x000000E0 names allocation 7 of 3 in heap block 0`), a folder's table still gives an
  item's cells when the item's own properties do not read, and `PstMessage.AsListed()` is a message as its folder
  lists it, the message itself not read. A table's cell naming a value the table does not hold reads as unset, as
  libpff reads it, the item's own property standing in (`PstFile.MissingValues` counts them, with examples).
- Listing a folder reads its contents table alone where the table has what is asked: an unset cell is a property the
  item has not, so a mailbox's mail lists without each message being read.
- Recurring appointments: `PstMessage.Recurrence` reads the series ([MS-OXOCAL] 2.2.1.44: daily, weekly, monthly,
  nth-weekday and yearly patterns, the deleted and modified occurrences with their own subject and place) and
  `Occurrences(until)` lists them as Outlook does, in UTC by the time zone the appointment keeps (`PstTimeZone`, from its
  TZDEFINITION or TZREG, whatever the system's zone database says): checked against Outlook's own expansion.
- The 4 KiB OST format's *column stores*, which [MS-PST] does not describe: Outlook keeps some columns of a contents
  table (the sender's name, the conversation id) in a heap of their own, a subnode of the table, and the row's cell
  points into that heap rather than the table's. Read as the table's heap, such a cell gives another row's subject, or
  noise; LibOutlook places each store from the rows themselves and reads the cells right.

```csharp
using LibOutlook;

using var pst = PstFile.Open(@"C:\Users\me\Documents\Outlook Files\archive.pst");
Console.WriteLine($"{pst.DisplayName} ({pst.Format})");

foreach (var folder in pst.TopFolder.Descendants())
{
    Console.WriteLine($"{folder.Name}: {folder.ContentCount} items");
    foreach (var message in folder.Messages)
    {
        Console.WriteLine($"  {message.MessageDeliveryTime:u}  {message.SenderName}  {message.Subject}");
        foreach (var attachment in message.Attachments.Where(a => !a.IsInline))
        {
            using var content = attachment.OpenContent();
            // ...
        }
    }
}

// any property, by MAPI id; named properties through the file's map
var contact = pst.TopFolder.FindFolder("Contacts")!.Messages.First();
var email1 = pst.NamedProperties.Find(new Guid("00062004-0000-0000-C000-000000000046"), 0x8083);
Console.WriteLine(contact.Properties.GetString(email1!.PropertyId));
```

Not covered: writing, password-protected files beyond reading them (the password is not enforced by the format),
Windows Information Protection files, and search-folder criteria.
