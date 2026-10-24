using System.Globalization;
using System.Text;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Outlook.Files;

/// <summary>
/// An appointment as iCalendar (RFC 5545): one VEVENT with its times (UTC; a date for an all-day event), summary,
/// location, description, organiser and attendees with their answers. A recurring appointment is written as its first
/// occurrence with Outlook's words for the pattern in <c>X-ANYFS-RECURRENCE</c>: the occurrences are not expanded.
/// </summary>
internal static class CalendarFile
{
    public static byte[] Build(MailItem item)
    {
        var a = item.Appointment ?? new AppointmentDetail(item.Received ?? item.Created, null, false, null, false, null, null, null);
        var sb = new StringBuilder();
        Line(sb, "BEGIN:VCALENDAR");
        Line(sb, "VERSION:2.0");
        Line(sb, "PRODID:-//AnyFS//Outlook source//EN");
        Line(sb, "METHOD:PUBLISH");
        Line(sb, "BEGIN:VEVENT");
        Line(sb, "UID:" + (a.Uid ?? item.Summary.Id.Replace('/', '.') + "@anyfs.invalid"));
        Line(sb, "DTSTAMP:" + Utc(item.Modified ?? item.Created ?? DateTimeOffset.UtcNow));
        if (a.Start is { } start)
        {
            if (a.AllDay)
            {
                Line(sb, "DTSTART;VALUE=DATE:" + start.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                if (a.End is { } end)
                {
                    Line(sb, "DTEND;VALUE=DATE:" + end.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                }
            }
            else
            {
                Line(sb, "DTSTART:" + Utc(start));
                if (a.End is { } end)
                {
                    Line(sb, "DTEND:" + Utc(end));
                }
            }
        }

        Line(sb, "SUMMARY:" + Escape(string.IsNullOrWhiteSpace(item.Subject) ? MailNames.NoSubject : item.Subject));
        if (a.Location is { } location)
        {
            Line(sb, "LOCATION:" + Escape(location));
        }

        var description = MessageMarkdown.PlainText(item);
        if (description.Length > 0)
        {
            Line(sb, "DESCRIPTION:" + Escape(description));
        }

        if (!item.From.IsEmpty)
        {
            Line(sb, "ORGANIZER" + Person(item.From));
        }

        foreach (var attendee in item.Recipients)
        {
            var role = attendee.Kind == MailRecipientKind.Cc ? "OPT-PARTICIPANT" : "REQ-PARTICIPANT";
            var partstat = attendee.Response switch
            {
                "accepted" => "ACCEPTED",
                "declined" => "DECLINED",
                "tentative" => "TENTATIVE",
                _ => "NEEDS-ACTION",
            };
            Line(sb, $"ATTENDEE;ROLE={role};PARTSTAT={partstat}" + Person(attendee.Address));
        }

        Line(sb, "STATUS:CONFIRMED");
        Line(sb, "TRANSP:" + (a.BusyStatus == "free" ? "TRANSPARENT" : "OPAQUE"));
        if (a.BusyStatus is { } busy)
        {
            Line(sb, "X-MICROSOFT-CDO-BUSYSTATUS:" + busy.ToUpperInvariant().Replace(' ', '-').Replace("OUT-OF-OFFICE", "OOF", StringComparison.Ordinal));
        }

        if (a.IsRecurring)
        {
            Line(sb, "X-ANYFS-RECURRENCE:" + Escape(a.RecurrencePattern ?? "recurring; the occurrences are not listed"));
        }

        if (item.Created is { } created)
        {
            Line(sb, "CREATED:" + Utc(created));
        }

        if (item.Modified is { } modified)
        {
            Line(sb, "LAST-MODIFIED:" + Utc(modified));
        }

        Line(sb, "END:VEVENT");
        Line(sb, "END:VCALENDAR");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string Person(MailAddress address)
    {
        var cn = string.IsNullOrWhiteSpace(address.Name) ? "" : $";CN=\"{address.Name.Replace("\"", "", StringComparison.Ordinal)}\"";
        return $"{cn}:mailto:{address.Address ?? "unknown@invalid"}";
    }

    private static string Utc(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Text for a property value: backslashes, semicolons, commas and line breaks escaped (RFC 5545 3.3.11).</summary>
    public static string Escape(string text) => text.ReplaceLineEndings("\n")
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>A content line, folded at 75 octets (RFC 5545 3.1), CRLF-ended.</summary>
    public static void Line(StringBuilder sb, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= 75)
        {
            sb.Append(line).Append("\r\n");
            return;
        }

        var at = 0;
        var first = true;
        while (at < bytes.Length)
        {
            var take = Math.Min(first ? 75 : 74, bytes.Length - at);
            // never split a UTF-8 sequence
            while (at + take < bytes.Length && (bytes[at + take] & 0xC0) == 0x80)
            {
                take--;
            }

            sb.Append(first ? "" : " ").Append(Encoding.UTF8.GetString(bytes, at, take)).Append("\r\n");
            at += take;
            first = false;
        }
    }
}
