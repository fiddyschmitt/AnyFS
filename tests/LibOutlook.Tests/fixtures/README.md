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
