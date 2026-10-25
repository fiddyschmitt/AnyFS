# LibOutlook test fixtures

Two PST files written by classic Outlook itself (Microsoft 365 Apps 16.0.20527, 24-Oct-2026) through its object model,
so they are exactly what Outlook writes, with the same content in both on-disk formats:

| File | Format | Made with |
|---|---|---|
| `ansi.pst` | ANSI (wVer 14/15, 32-bit, 512-byte pages, permuted blocks) | `Namespace.AddStoreEx(path, olStoreANSI = 3)` |
| `unicode.pst` | Unicode (wVer 23, 64-bit, 512-byte pages, permuted blocks) | `Namespace.AddStoreEx(path, olStoreUnicode = 2)` |

Content of each (store name "AnyFS fixture", top folder "Top of Outlook data file"):

- `Inbox` (IPF.Note), five mails, all unsent drafts addressed to the lab mailbox (an HTML draft stores only HTML, no plain-text body):
  - **Quarterly figures**: HTML body "Figures attached.", `figures.csv` attached (27 bytes), categories Red and Blue, high importance.
  - **Photo from the site visit**: HTML body with `<img src="cid:anyfs-dot">`, `dot.png` attached as an inline image (70 bytes, content id `anyfs-dot`, PidTagAttachFlags rendered-in-body).
  - **Plain note: ümlauts & "quotes" / slashes: kept?**: plain-text body "Name edge cases. naïve café – 日本語". The Unicode file holds it as PidTagBody; the ANSI file, whose code page cannot hold the CJK, holds it as compressed RTF marked `\fromtext` and no plain body.
  - **Forwarded: site visit**: the photo mail attached as an embedded message (PtypObject); the one draft of the five Outlook marked read.
  - **Large attachment**: `pattern.bin`, 300,000 bytes where byte *i* is *i* mod 251 (stored over many data blocks).
- `Inbox\Projects` (IPF.Note): **RE: Project kick-off**, rich-text body "In a subfolder.", two To recipients and one Cc.
- `Contacts` (IPF.Contact): Adele Vance, Contoso, Retail Manager, adelev@example.test.
- `Calendar` (IPF.Appointment): Design review, Room 4, 60 minutes, body "Agenda: the Outlook source.".
- `Deleted Items` and Outlook's own `Search Root` and search folder, empty.

The script that makes them is `make-pst-fixtures.ps1` (kept with the lab tooling, outside the repository); it runs in the
lab's Windows VM with Outlook signed in, and the files are copied here as they come. The OST format (Outlook 2013+,
4 KiB pages) is tested against a capture of the lab mailbox that stays outside the repository (`OstTests`, inconclusive
without it: set `LIBOUTLOOK_TEST_OST` to point at one).

`smime.pst` (Unicode, made by Outlook the same way on 25-Oct-2026; store "AnyFS S/MIME fixture") holds S/MIME mail as
Outlook keeps it. Its `Inbox` has two messages that Outlook took in from .eml files signed and encrypted with a
self-signed lab certificate, each carrying an HTML body and a 2,000-byte random file `report.bin`:

- **AnyFS lab: a signed message**: clear-signed. Kept as `IPM.Note.SMIME.MultipartSigned` with the HTML body promoted
  and one attachment, nameless, MIME tag `multipart/signed`, holding the whole signed entity (its own `MIME-Version`
  and `Content-Type` header, then the body the signature covers).
- **AnyFS lab: an encrypted message**: kept as `IPM.Note.SMIME` with no body at all and one attachment `smime.p7m`,
  MIME tag `application/pkcs7-mime`, holding the binary CMS; the Content-Type it came with is in the PS_INTERNET_HEADERS
  property "content-type".

The scripts that make it, `make-smime.ps1` and `make-smime-pst.ps1`, are kept with the lab tooling as well.

`recurrence.pst` (Unicode, made by Outlook the same way on 25-Oct-2026 in the lab VM's zone, Pacific; store "AnyFS
recurrence fixture") holds a `Calendar` of recurring series of every kind Outlook's object model offers, plus one single
appointment ("One-off", 3 November 2026 14:00): "Weekly sync" (Tuesday and Thursday 10:00, 20 occurrences from
1 September 2026, the 17 September one deleted, the 13 October one moved to 11:00-11:45 as "Weekly sync (moved)" in
Room 9), "Every other day" (08:15, 20 October to 10 November), "Month end review" (day 31 of each month, ten from
January: the last day of shorter months), "Last Friday" (no end), "Quarterly planning" (the first weekday every three
months), "Anniversary" (all day, 15 March each year, no end), "Fourth Thursday dinner" (yearly, three), "Weekday
standup" (Monday to Friday for two weeks) and "Every third Wednesday" (eight, the 23 September one moved to the 24th).
They cross the change to standard time on 1 November 2026. `recurrence.occurrences.txt` is Outlook's own expansion of
that calendar (`Items.IncludeRecurrences`, every occurrence starting in 2026 or 2027 local time: start and end in UTC,
all-day or timed, subject, location), the oracle the recurrence tests are checked against. The script that makes both is
`make-recurrence-pst.ps1`, with the lab tooling.

`damaged.pst` is `unicode.pst` with four heaps damaged the way a real OST's were (records naming allocations their
heap's page map does not list: "HID 0x0000CD20 names allocation 1641 of 616 in heap block 0", by the hundred in its
contents tables' cells), each block's CRC made again so that only the heap is wrong: the Inbox's Projects folder claims
one allocation (its property records do not read), the "Plain note" mail claims only its own BTH's (its records read,
its values do not), the Contacts folder's contents table claims one (its row index does not read), and the Projects
folder's contents table claims only its own structures' (its row reads, the values its cells name do not).
`Damage.Fixture` in `DamagedFileTests.cs` makes it, and a test checks the file here is what it makes; to make it again,
run that test with `LIBOUTLOOK_MAKE_FIXTURES` set to this folder.
