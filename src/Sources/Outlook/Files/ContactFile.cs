using System.Globalization;
using System.Text;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Outlook.Files;

/// <summary>A contact as a vCard (version 3.0, RFC 2426): name, organisation, emails, phones, addresses, web, birthday, notes, photo.</summary>
internal static class ContactFile
{
    /// <summary>A contact of the mailbox's: its fields, its notes, its photo.</summary>
    public static async Task<byte[]> BuildAsync(MailItem item, CancellationToken ct)
    {
        var c = item.Contact ?? new ContactDetail(item.Subject, null, null, null, null, null, null, null, null, null, [], [], [], null, null, null, null);
        (string Type, byte[] Bytes)? photo = null;
        if (item.Attachments.FirstOrDefault(a => a.IsContactPhoto) is { } attachment)
        {
            using var content = await attachment.OpenAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
            photo = (attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? attachment.ContentType[6..].ToUpperInvariant() : "JPEG", buffer.ToArray());
        }

        return Build(c, item.Subject, MessageMarkdown.PlainText(item), photo, item.Modified);
    }

    /// <summary>An entry of an address list: what the directory says of it, dated by when it was last seen to change.</summary>
    public static byte[] Build(DirectoryEntry e)
    {
        var detail = new ContactDetail(
            null, null, e.GivenName, e.MiddleName, e.Surname, null, e.Nickname, e.Company, e.JobTitle, e.Department, e.Emails,
            e.Phones.Select(p => (p.Type, p.Number)).ToList(),
            e.Addresses.Select(a => (a.Type, a.Street ?? "", a.City ?? "", a.State ?? "", a.PostalCode ?? "", a.Country ?? "")).ToList(),
            null, e.ImAddress, null, null) { Office = e.Office };
        return Build(detail, e.Name, "", null, e.Changed);
    }

    private static byte[] Build(ContactDetail c, string name, string notes, (string Type, byte[] Bytes)? photo, DateTimeOffset? modified)
    {
        var sb = new StringBuilder();
        CalendarFile.Line(sb, "BEGIN:VCARD");
        CalendarFile.Line(sb, "VERSION:3.0");
        var fullName = string.Join(" ", new[] { c.Prefix, c.GivenName, c.MiddleName, c.Surname, c.Suffix }.Where(s => !string.IsNullOrWhiteSpace(s)));
        CalendarFile.Line(sb, "FN:" + Escape(!string.IsNullOrWhiteSpace(fullName) ? fullName : c.FileAs ?? name));
        CalendarFile.Line(sb, $"N:{Escape(c.Surname ?? "")};{Escape(c.GivenName ?? "")};{Escape(c.MiddleName ?? "")};{Escape(c.Prefix ?? "")};{Escape(c.Suffix ?? "")}");
        if (c.Nickname is { } nickname)
        {
            CalendarFile.Line(sb, "NICKNAME:" + Escape(nickname));
        }

        if (c.Company is not null || c.Department is not null)
        {
            CalendarFile.Line(sb, "ORG:" + Escape(c.Company ?? "") + (c.Department is null ? "" : ";" + Escape(c.Department)));
        }

        if (c.JobTitle is { } title)
        {
            CalendarFile.Line(sb, "TITLE:" + Escape(title));
        }

        for (var i = 0; i < c.Emails.Count; i++)
        {
            CalendarFile.Line(sb, $"EMAIL;TYPE=INTERNET{(i == 0 ? ",PREF" : "")}:{Escape(c.Emails[i])}");
        }

        foreach (var (type, number) in c.Phones)
        {
            CalendarFile.Line(sb, $"TEL;TYPE={type}:{Escape(number)}");
        }

        // the office is the work address's extended part (RFC 2426 3.2.1: "suite, building"), or a work address of its own
        var office = c.Office;
        foreach (var a in c.Addresses)
        {
            var extended = a.Type == "WORK" && office is not null ? Escape(office) : "";
            if (a.Type == "WORK")
            {
                office = null;
            }

            CalendarFile.Line(sb, $"ADR;TYPE={a.Type}:;{extended};{Escape(a.Street)};{Escape(a.City)};{Escape(a.State)};{Escape(a.PostalCode)};{Escape(a.Country)}");
        }

        if (office is not null)
        {
            CalendarFile.Line(sb, $"ADR;TYPE=WORK:;{Escape(office)};;;;;");
        }

        if (c.WebPage is { } web)
        {
            CalendarFile.Line(sb, "URL:" + Escape(web));
        }

        if (c.ImAddress is { } im)
        {
            CalendarFile.Line(sb, "X-MS-IMADDRESS:" + Escape(im));
        }

        if (c.Birthday is { } birthday)
        {
            CalendarFile.Line(sb, "BDAY:" + birthday.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (c.Anniversary is { } anniversary)
        {
            CalendarFile.Line(sb, "X-ANNIVERSARY:" + anniversary.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (notes.Length > 0)
        {
            CalendarFile.Line(sb, "NOTE:" + Escape(notes));
        }

        if (photo is { } p)
        {
            CalendarFile.Line(sb, $"PHOTO;ENCODING=b;TYPE={p.Type}:" + Convert.ToBase64String(p.Bytes));
        }

        if (modified is { } m)
        {
            CalendarFile.Line(sb, "REV:" + m.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        }

        CalendarFile.Line(sb, "END:VCARD");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string Escape(string text) => CalendarFile.Escape(text);
}
