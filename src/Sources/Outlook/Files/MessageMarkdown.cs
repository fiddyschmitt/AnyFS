using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AnyFS.Sources.Outlook.Mail;
using ReverseMarkdown;

namespace AnyFS.Sources.Outlook.Files;

/// <summary>
/// An item as Markdown (spec 7.7): a header block (From, To, Cc, Date, Subject, the attachments as links), then the body:
/// the HTML converted as the SharePoint pages are (ReverseMarkdown), <c>cid:</c> images rewritten to the attachments
/// folder; the plain text when there is no HTML; the RTF unwrapped when there is only that. An appointment's block says
/// when and where, who organises and who attends; a contact's, who they are.
/// </summary>
internal static partial class MessageMarkdown
{
    // the default flavour, as SharePointPages: tables, fenced code and nested lists the GitHub way
    private static readonly Lock ConverterGate = new();
    private static readonly Converter MarkdownConverter = new(new Config
    {
        Tags = { Unknown = Config.UnknownTagsOption.Bypass },
        Links = { SmartHref = true },
        Formatting = { RemoveComments = true, ListBulletChar = '-', OutputLineEnding = "\n" },
    });

    /// <summary>The item's Markdown, UTF-8 without a byte order mark.</summary>
    /// <param name="attachments">The item's attachments with the names they show under in the attachments folder.</param>
    public static byte[] Build(MailItem item, IReadOnlyList<(MailAttachment Attachment, string FileName)> attachments)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(string.IsNullOrWhiteSpace(item.Subject) ? MailNames.NoSubject : item.Subject.Trim()).Append("\n\n");

        if (item.Kind == MailItemKind.Appointment && item.Appointment is { } appointment)
        {
            Line(sb, "When", When(appointment));
            Line(sb, "Where", appointment.Location);
            Line(sb, "Organiser", item.From.IsEmpty ? null : item.From.ToString());
            var attendees = item.Recipients.Select(r => r.Address.ToString() + (r.Response is null ? "" : $" ({r.Response})") + (r.Kind == MailRecipientKind.Cc ? " (optional)" : "")).Where(s => s.Length > 0).ToList();
            Line(sb, "Attendees", attendees.Count == 0 ? null : string.Join("; ", attendees));
            Line(sb, "Repeats", appointment.IsRecurring ? appointment.RecurrencePattern ?? "yes (the occurrences are not listed)" : null);
            Line(sb, "Shown as", appointment.BusyStatus);
        }
        else if (item.Kind == MailItemKind.Contact && item.Contact is { } contact)
        {
            Line(sb, "Name", string.Join(" ", new[] { contact.Prefix, contact.GivenName, contact.MiddleName, contact.Surname, contact.Suffix }.Where(s => !string.IsNullOrWhiteSpace(s))));
            Line(sb, "Company", contact.Company);
            Line(sb, "Title", string.Join(", ", new[] { contact.JobTitle, contact.Department }.Where(s => !string.IsNullOrWhiteSpace(s))));
            Line(sb, "Office", contact.Office);
            Line(sb, "Email", contact.Emails.Count == 0 ? null : string.Join("; ", contact.Emails));
            Line(sb, "Phone", contact.Phones.Count == 0 ? null : string.Join("; ", contact.Phones.Select(p => $"{p.Number} ({p.Type.ToLowerInvariant().Replace(",voice", "", StringComparison.Ordinal)})")));
            foreach (var a in contact.Addresses)
            {
                Line(sb, $"Address ({a.Type.ToLowerInvariant()})", string.Join(", ", new[] { a.Street.ReplaceLineEndings(", "), a.City, a.State, a.PostalCode, a.Country }.Where(s => !string.IsNullOrWhiteSpace(s))));
            }

            Line(sb, "Web", contact.WebPage);
            Line(sb, "IM", contact.ImAddress);
            Line(sb, "Birthday", contact.Birthday?.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.InvariantCulture));
        }
        else
        {
            Line(sb, "From", item.From.IsEmpty ? null : item.From.ToString());
            Line(sb, "To", Join(item.To));
            Line(sb, "Cc", Join(item.Cc));
            Line(sb, "Bcc", item.IsDraft ? Join(item.Bcc) : null);
            Line(sb, "Date", Date(item.Received ?? item.Sent ?? item.Created));
            if (item.Received is not null && item.Sent is not null && (item.Received.Value - item.Sent.Value).Duration() > TimeSpan.FromMinutes(1))
            {
                Line(sb, "Sent", Date(item.Sent));
            }

            Line(sb, "Importance", item.Importance switch { 2 => "high", 0 => "low", _ => null });
            Line(sb, "Status", item.IsDraft ? "draft, never sent" : null);
            Line(sb, "Security", item.Security);
        }

        var shown = attachments.Where(a => !a.Attachment.IsContactPhoto).ToList();
        if (shown.Count > 0)
        {
            Line(sb, "Attachments", string.Join(", ", shown.Select(a => $"[{Escape(a.FileName)}]({Link(a.FileName)})" + (a.Attachment.IsMessage ? " (a message)" : $" ({Size(a.Attachment.Size)})"))));
        }

        sb.Append("\n---\n\n");
        sb.Append(Body(item, attachments));
        sb.Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>The body alone, as Markdown: HTML converted, else the text, else the RTF unwrapped; "" for none.</summary>
    public static string Body(MailItem item, IReadOnlyList<(MailAttachment Attachment, string FileName)> attachments)
    {
        var html = item.BodyHtml ?? (item.BodyRtf is { } rtf && item.BodyText is null ? RtfText.HtmlFromRtf(rtf) : null);
        if (html is not null)
        {
            return FromHtml(html, attachments);
        }

        if (item.BodyText is { } text)
        {
            return text.ReplaceLineEndings("\n").Trim();
        }

        return item.BodyRtf is { } only ? RtfText.PlainText(only).ReplaceLineEndings("\n").Trim() : "";
    }

    /// <summary>The plain text of the body, for a calendar description or a text part: the text, else the HTML stripped by way of Markdown, else the RTF's.</summary>
    public static string PlainText(MailItem item)
    {
        if (item.BodyText is { } text)
        {
            return text.ReplaceLineEndings("\n").Trim();
        }

        return Body(item, []);
    }

    private static string FromHtml(string html, IReadOnlyList<(MailAttachment Attachment, string FileName)> attachments)
    {
        var byContentId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (attachment, fileName) in attachments)
        {
            if (attachment.ContentId is { } cid)
            {
                byContentId.TryAdd(cid.Trim().Trim('<', '>'), fileName);
            }
        }

        if (byContentId.Count > 0)
        {
            html = CidReference().Replace(html, m => byContentId.TryGetValue(Uri.UnescapeDataString(m.Groups[2].Value), out var file) ? m.Groups[1].Value + Link(file) : m.Value);
        }

        string markdown;
        lock (ConverterGate)
        {
            markdown = MarkdownConverter.Convert(html);
        }

        // ReverseMarkdown leaves the runs of blank lines a mail's HTML has
        return BlankLines().Replace(markdown.Replace("\r\n", "\n", StringComparison.Ordinal), "\n\n").Trim();
    }

    private static void Line(StringBuilder sb, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.Append("**").Append(label).Append(":** ").Append(value.ReplaceLineEndings(" ").Trim()).Append("  \n");
        }
    }

    private static string? Join(IEnumerable<MailRecipient> recipients)
    {
        var list = recipients.Select(r => r.Address.ToString()).Where(s => s.Length > 0).ToList();
        return list.Count == 0 ? null : string.Join("; ", list);
    }

    private static string? Date(DateTimeOffset? time) => time?.ToLocalTime().ToString("ddd d MMM yyyy HH:mm (UTCzzz)", CultureInfo.InvariantCulture);

    private static string When(AppointmentDetail a)
    {
        if (a.Start is not { } start)
        {
            return "";
        }

        var s = start.ToLocalTime();
        if (a.AllDay)
        {
            var e = a.End?.ToLocalTime();
            return e is { } end && (end - s).TotalDays > 1
                ? $"{s:ddd d MMM yyyy} to {end.AddDays(-1):ddd d MMM yyyy} (all day)"
                : $"{s:ddd d MMM yyyy} (all day)";
        }

        if (a.End?.ToLocalTime() is { } until)
        {
            return until.Date == s.Date
                ? $"{s:ddd d MMM yyyy HH:mm} to {until:HH:mm} (UTC{s:zzz})"
                : $"{s:ddd d MMM yyyy HH:mm} to {until:ddd d MMM yyyy HH:mm} (UTC{s:zzz})";
        }

        return $"{s:ddd d MMM yyyy HH:mm} (UTC{s:zzz})";
    }

    /// <summary>A link into the attachments folder, the name percent-encoded so spaces and brackets survive Markdown.</summary>
    private static string Link(string fileName) => MailNames.AttachmentsFolder + "/" + Uri.EscapeDataString(fileName);

    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };

    [GeneratedRegex("""((?:src|href|background)\s*=\s*["']?)cid:([^"'\s>)]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CidReference();

    [GeneratedRegex(@"\n[ \t]*\n(?:[ \t]*\n)+")]
    private static partial Regex BlankLines();
}
