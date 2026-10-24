using System.Globalization;
using System.Text;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Outlook.Files;

/// <summary>A contact as a vCard (version 3.0, RFC 2426): name, organisation, emails, phones, addresses, web, birthday, notes, photo.</summary>
internal static class ContactFile
{
    public static byte[] Build(MailItem item)
    {
        var c = item.Contact ?? new ContactDetail(item.Subject, null, null, null, null, null, null, null, null, null, [], [], [], null, null, null, null);
        var sb = new StringBuilder();
        CalendarFile.Line(sb, "BEGIN:VCARD");
        CalendarFile.Line(sb, "VERSION:3.0");
        var fullName = string.Join(" ", new[] { c.Prefix, c.GivenName, c.MiddleName, c.Surname, c.Suffix }.Where(s => !string.IsNullOrWhiteSpace(s)));
        CalendarFile.Line(sb, "FN:" + Escape(!string.IsNullOrWhiteSpace(fullName) ? fullName : c.FileAs ?? item.Subject));
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

        foreach (var a in c.Addresses)
        {
            CalendarFile.Line(sb, $"ADR;TYPE={a.Type}:;;{Escape(a.Street)};{Escape(a.City)};{Escape(a.State)};{Escape(a.PostalCode)};{Escape(a.Country)}");
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

        var notes = MessageMarkdown.PlainText(item);
        if (notes.Length > 0)
        {
            CalendarFile.Line(sb, "NOTE:" + Escape(notes));
        }

        if (item.Attachments.FirstOrDefault(a => a.IsContactPhoto) is { } photo)
        {
            using var content = photo.Open();
            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var type = photo.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? photo.ContentType[6..].ToUpperInvariant() : "JPEG";
            CalendarFile.Line(sb, $"PHOTO;ENCODING=b;TYPE={type}:" + Convert.ToBase64String(buffer.ToArray()));
        }

        if (item.Modified is { } modified)
        {
            CalendarFile.Line(sb, "REV:" + modified.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        }

        CalendarFile.Line(sb, "END:VCARD");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string Escape(string text) => CalendarFile.Escape(text);
}
