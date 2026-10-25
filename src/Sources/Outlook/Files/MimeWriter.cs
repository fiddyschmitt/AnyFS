using System.Globalization;
using System.Text;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Outlook.Files;

/// <summary>
/// An item as an Internet message (.eml, RFC 5322 and MIME): the headers it came with (a received message's transport
/// headers, less the content ones) or headers made from its properties, then the body as multipart/mixed over
/// multipart/related over multipart/alternative as the parts call for: text and HTML quoted-printable in UTF-8, files
/// base64, inline images with their Content-ID, attached messages as message/rfc822 inside. An S/MIME message, which a
/// data file keeps as one attachment holding the signed or encrypted content, is written back as that content
/// ([MS-OXOSMIME] 2.1.3), so a mail client checks the signature or decrypts it as it does the original.
/// </summary>
internal static class MimeWriter
{
    private const int MaxDepth = 8;

    private static readonly HashSet<string> DroppedTransportHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "MIME-Version", "Content-Type", "Content-Transfer-Encoding", "Content-Disposition", "Content-ID", "Content-Description", "Content-Length", "Content-Language", "Content-Class",
    };

    /// <summary>The message as bytes; its attachments go under the names <see cref="AttachmentNames"/> gives them, the same as in the attachments folder.</summary>
    public static byte[] Build(MailItem item)
    {
        var stream = new MemoryStream();
        WriteMessage(stream, item, 0);
        return stream.ToArray();
    }

    private static void WriteMessage(Stream s, MailItem item, int depth)
    {
        foreach (var (name, value) in Headers(item))
        {
            Ascii(s, Fold(name + ": " + value));
            Ascii(s, "\r\n");
        }

        var body = SmimeBody(item) ?? BodyOf(item, AttachmentNames.LookupFor(item.Attachments), depth);
        body.WriteHeaders(s);
        Ascii(s, "\r\n");
        body.WriteContent(s);
    }

    // --- S/MIME -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// An S/MIME message's body as the message came, from the one attachment a data file keeps it in ([MS-OXOSMIME]
    /// 2.1.3; a real OST's encrypted mail opened in Outlook from AnyFS's .eml with a blank body until 25-Oct-2026):
    /// <list type="bullet">
    /// <item>Clear-signed (IPM.Note.SMIME.MultipartSigned; the attachment multipart/signed, often nameless): the attachment
    /// is the signed entity. Its header fields are dropped but the last Content-Type, its body is written byte for byte,
    /// so the signature still holds (2.1.3.1.2.3). The body the store promoted for display is not written twice.</item>
    /// <item>Encrypted or opaque-signed (IPM.Note.SMIME; the attachment application/pkcs7-mime, or a .p7m file): the
    /// attachment is the CMS, written base64 under the Content-Type the message came with, else one made from the
    /// attachment's type and name (2.1.3.2.2.3), with the smime-type the CMS shows.</item>
    /// </list>
    /// Null for any other message, and for an S/MIME message not shaped so (more attachments, an entity with no
    /// multipart/signed header): those are written as any message is.
    /// </summary>
    private static Part? SmimeBody(MailItem item)
    {
        if (!item.MessageClass.StartsWith("IPM.Note.SMIME", StringComparison.OrdinalIgnoreCase) || item.Attachments.Count != 1 || item.Attachments[0].IsMessage)
        {
            return null;
        }

        var attachment = item.Attachments[0];
        var type = MediaType(attachment.ContentType);
        var opaque = type is "application/pkcs7-mime" or "application/x-pkcs7-mime"
            || type == "application/octet-stream" && attachment.Name.EndsWith(".p7m", StringComparison.OrdinalIgnoreCase);
        var signed = type == "multipart/signed" || !opaque && item.MessageClass.EndsWith(".MultipartSigned", StringComparison.OrdinalIgnoreCase);
        if (!opaque && !signed)
        {
            return null;
        }

        var content = ReadAll(attachment);
        if (signed)
        {
            return SignedPart.From(content);
        }

        return new OpaquePart(content, OpaqueContentType(item.InternetContentType, attachment, content), OpaqueFileName(attachment));
    }

    /// <summary>"multipart/signed" of "Multipart/Signed; boundary=..."; "" for nothing.</summary>
    private static string MediaType(string? contentType) => (contentType ?? "").Split(';', 2)[0].Trim().ToLowerInvariant();

    private static byte[] ReadAll(MailAttachment attachment)
    {
        // the PST store's streams come at once; a server's attachment is in hand by the time its message is written
        using var content = attachment.OpenAsync(CancellationToken.None).GetAwaiter().GetResult();
        using var copy = new MemoryStream();
        content.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The name an encrypted message's body goes under: the attachment's when it is a .p7m, else smime.p7m (RFC 8551).</summary>
    private static string OpaqueFileName(MailAttachment attachment)
        => attachment.Name.EndsWith(".p7m", StringComparison.OrdinalIgnoreCase) ? attachment.Name : "smime.p7m";

    /// <summary>
    /// The Content-Type of an encrypted message's body: the one it came with, unfolded, when that is an S/MIME type; else
    /// the attachment's type with the smime-type the CMS shows and the file's name.
    /// </summary>
    private static string OpaqueContentType(string? received, MailAttachment attachment, byte[] content)
    {
        if (received is not null)
        {
            var unfolded = string.Join(' ', received.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (MediaType(unfolded) is "application/pkcs7-mime" or "application/x-pkcs7-mime" or "application/octet-stream")
            {
                return unfolded;
            }
        }

        var type = MediaType(attachment.ContentType) is { Length: > 0 } t && t != "application/octet-stream" ? t : "application/pkcs7-mime";
        var smimeType = SmimeType(content);
        return $"{type}; {(smimeType is null ? "" : $"smime-type={smimeType}; ")}{ParameterValue("name", OpaqueFileName(attachment))}";
    }

    /// <summary>
    /// What a CMS ContentInfo holds, by its content type ([RFC 8551] 3.2.2): enveloped-data, signed-data,
    /// authEnveloped-data or compressed-data; null when the bytes are no CMS or another kind.
    /// </summary>
    internal static string? SmimeType(ReadOnlySpan<byte> der)
    {
        // ContentInfo ::= SEQUENCE { contentType OBJECT IDENTIFIER, ... }, definite or indefinite length
        if (der.Length < 4 || der[0] != 0x30)
        {
            return null;
        }

        var at = 1 + ((der[1] & 0x80) != 0 ? 1 + (der[1] & 0x7F) : 1);
        if (at + 2 > der.Length || der[at] != 0x06)
        {
            return null;
        }

        var oid = der.Slice(at + 2, Math.Min(der[at + 1], der.Length - at - 2));
        ReadOnlySpan<byte> pkcs7 = [0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07];          // 1.2.840.113549.1.7
        ReadOnlySpan<byte> smime = [0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x09, 0x10, 0x01]; // 1.2.840.113549.1.9.16.1
        if (oid.Length == pkcs7.Length + 1 && oid.StartsWith(pkcs7))
        {
            return oid[^1] switch { 0x03 => "enveloped-data", 0x02 => "signed-data", _ => null };
        }

        if (oid.Length == smime.Length + 1 && oid.StartsWith(smime))
        {
            return oid[^1] switch { 0x17 => "authEnveloped-data", 0x09 => "compressed-data", _ => null };
        }

        return null;
    }

    // --- headers ----------------------------------------------------------------------------------------------------------

    private static List<(string Name, string Value)> Headers(MailItem item)
    {
        var headers = new List<(string, string)>();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (item.TransportHeaders is { } transport)
        {
            foreach (var (name, value) in Unfold(transport))
            {
                if (!DroppedTransportHeaders.Contains(name) && !name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                {
                    headers.Add((name, value));
                    present.Add(name);
                }
            }
        }

        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value) && present.Add(name))
            {
                headers.Add((name, value));
            }
        }

        Add("From", item.From.IsEmpty ? null : Mailbox(item.From));
        Add("To", Mailboxes(item.To));
        Add("Cc", Mailboxes(item.Cc));
        if (item.IsDraft)
        {
            Add("Bcc", Mailboxes(item.Bcc));
        }

        Add("Subject", EncodeWord(item.Subject));
        Add("Date", (item.Sent ?? item.Received ?? item.Created ?? item.Modified)?.ToString("ddd, dd MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture).Replace(":", "", StringComparison.Ordinal) is { } d ? FixDate(d) : null);
        Add("Message-ID", Angle(item.InternetMessageId) ?? $"<{item.Summary.Id.Replace('/', '.')}@anyfs.invalid>");
        Add("In-Reply-To", Angle(item.InReplyTo));
        Add("References", item.References);
        switch (item.Importance)
        {
            case 2:
                Add("Importance", "High");
                Add("X-Priority", "1");
                break;
            case 0:
                Add("Importance", "Low");
                Add("X-Priority", "5");
                break;
        }

        if (item.IsDraft)
        {
            Add("X-Unsent", "1");
        }

        Add("MIME-Version", "1.0");
        return headers;
    }

    /// <summary>The headers of a raw header block, continuation lines joined to theirs.</summary>
    private static IEnumerable<(string Name, string Value)> Unfold(string block)
    {
        string? name = null;
        var value = new StringBuilder();
        foreach (var raw in block.ReplaceLineEndings("\n").Split('\n'))
        {
            if (raw.Length == 0)
            {
                break;   // the blank line before a body, should the block hold one
            }

            if (raw[0] is ' ' or '\t')
            {
                if (name is not null)
                {
                    value.Append(' ').Append(raw.Trim());
                }

                continue;
            }

            if (name is not null)
            {
                yield return (name, value.ToString());
            }

            var colon = raw.IndexOf(':');
            if (colon <= 0)
            {
                name = null;
                continue;
            }

            name = raw[..colon].Trim();
            value.Clear().Append(raw[(colon + 1)..].Trim());
        }

        if (name is not null)
        {
            yield return (name, value.ToString());
        }
    }

    // "+1000" needs no colon; .NET writes "+10:00", stripped above along with the time's colons, which are put back here
    private static string FixDate(string d)
    {
        // "Fri, 24 Oct 2026 105729 +1000" → "Fri, 24 Oct 2026 10:57:29 +1000"
        var parts = d.Split(' ');
        if (parts.Length == 6 && parts[4].Length == 6)
        {
            parts[4] = $"{parts[4][..2]}:{parts[4][2..4]}:{parts[4][4..]}";
        }

        return string.Join(' ', parts);
    }

    private static string? Angle(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        id = id.Trim();
        return id.StartsWith('<') ? id : $"<{id}>";
    }

    private static string? Mailboxes(IEnumerable<MailRecipient> recipients)
    {
        var list = recipients.Select(r => r.Address).Where(a => !a.IsEmpty).Select(Mailbox).ToList();
        return list.Count == 0 ? null : string.Join(",\r\n ", list);
    }

    /// <summary>"Name &lt;address&gt;" with the name quoted or encoded as it needs; the address alone when there is no name.</summary>
    private static string Mailbox(MailAddress address)
    {
        var name = address.Name?.Trim();
        var addr = address.Address?.Trim();
        if (string.IsNullOrEmpty(addr))
        {
            // an unresolved recipient is its address shown as a name
            return name is null ? "" : name.Contains('@') && !name.Any(char.IsWhiteSpace) ? name : Phrase(name);
        }

        if (string.IsNullOrEmpty(name) || string.Equals(name, addr, StringComparison.OrdinalIgnoreCase))
        {
            return addr;
        }

        return $"{Phrase(name)} <{addr}>";
    }

    private static string Phrase(string name)
    {
        if (name.Any(c => c > 126 || c < 32))
        {
            return EncodeWord(name);
        }

        return name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '-' or '_' or '\'')
            ? name
            : "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>Text for a header: as it is when ASCII, else RFC 2047 encoded words of UTF-8, each within the 75-character limit.</summary>
    public static string EncodeWord(string text)
    {
        if (text.All(c => c is >= ' ' and <= '~'))
        {
            return text;
        }

        var words = new List<string>();
        var chunk = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > 45)
            {
                words.Add(Encoded(chunk.ToString()));
                chunk.Clear();
                bytes = 0;
            }

            chunk.Append(rune.ToString());
            bytes += size;
        }

        if (chunk.Length > 0)
        {
            words.Add(Encoded(chunk.ToString()));
        }

        return string.Join(" ", words);

        static string Encoded(string s) => "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?=";
    }

    /// <summary>A header line folded at 78 characters where it can be: after a comma or at a space.</summary>
    private static string Fold(string line)
    {
        if (line.Length <= 78 || line.Contains("\r\n", StringComparison.Ordinal))
        {
            return line;
        }

        var sb = new StringBuilder();
        var start = 0;
        // the first word stays beside the header's name; a continuation line starts at the space it was cut at
        var first = line.IndexOf(": ", StringComparison.Ordinal) + 2;
        while (line.Length - start > 78)
        {
            var earliest = start == 0 ? first : start + 1;
            var cut = line.LastIndexOf(' ', start + 78, 78);
            if (cut < earliest)
            {
                cut = line.IndexOf(' ', earliest);
                if (cut < 0)
                {
                    break;
                }
            }

            sb.Append(line, start, cut - start).Append("\r\n");
            start = cut;
        }

        sb.Append(line, start, line.Length - start);
        return sb.ToString();
    }

    // --- the body ---------------------------------------------------------------------------------------------------------

    private static Part BodyOf(MailItem item, Func<MailAttachment, string> fileNameOf, int depth)
    {
        var html = item.BodyHtml ?? (item.BodyRtf is { } rtf && item.BodyText is null ? RtfText.HtmlFromRtf(rtf) : null);
        var text = item.BodyText ?? (html is null && item.BodyRtf is { } only ? RtfText.PlainText(only) : null);
        Part content;
        if (html is not null && text is not null)
        {
            content = new Multipart("alternative", [new TextPart("text/plain", text), new TextPart("text/html", html)]);
        }
        else if (html is not null)
        {
            content = new TextPart("text/html", html);
        }
        else
        {
            content = new TextPart("text/plain", text ?? "");
        }

        var inline = item.Attachments.Where(a => a.IsInline && a.ContentId is not null && !a.IsMessage).ToList();
        if (inline.Count > 0)
        {
            content = new Multipart("related", [content, .. inline.Select(a => (Part)new FilePart(a, fileNameOf(a), inline: true))]);
        }

        var rest = item.Attachments.Where(a => !inline.Contains(a)).ToList();
        if (rest.Count > 0)
        {
            var parts = new List<Part> { content };
            foreach (var a in rest)
            {
                if (a.IsMessage && depth < MaxDepth && a.Embedded!(CancellationToken.None).GetAwaiter().GetResult() is { } embedded)
                {
                    parts.Add(new MessagePart(embedded, fileNameOf(a), depth + 1));
                }
                else if (!a.IsMessage)
                {
                    parts.Add(new FilePart(a, fileNameOf(a), inline: false));
                }
            }

            content = new Multipart("mixed", parts);
        }

        return content;
    }

    private abstract class Part
    {
        public abstract void WriteHeaders(Stream s);

        public abstract void WriteContent(Stream s);
    }

    private sealed class TextPart(string type, string text) : Part
    {
        public override void WriteHeaders(Stream s)
        {
            Ascii(s, $"Content-Type: {type}; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n");
        }

        public override void WriteContent(Stream s) => QuotedPrintable(s, Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n")));
    }

    private sealed class FilePart(MailAttachment attachment, string fileName, bool inline) : Part
    {
        public override void WriteHeaders(Stream s)
        {
            var name = ParameterValue("name", fileName);
            Ascii(s, $"Content-Type: {attachment.ContentType}; {name}\r\n");
            Ascii(s, $"Content-Disposition: {(inline ? "inline" : "attachment")}; {ParameterValue("filename", fileName)}; size={attachment.Size.ToString(CultureInfo.InvariantCulture)}\r\n");
            if (attachment.ContentId is { } cid)
            {
                Ascii(s, $"Content-ID: {Angle(cid)}\r\n");
            }

            Ascii(s, "Content-Transfer-Encoding: base64\r\n");
        }

        public override void WriteContent(Stream s)
        {
            // the PST store's streams come at once; a server's attachment is in hand by the time its message is written
            using var content = attachment.OpenAsync(CancellationToken.None).GetAwaiter().GetResult();
            Base64(s, content);
        }
    }

    private sealed class MessagePart(MailItem embedded, string fileName, int depth) : Part
    {
        public override void WriteHeaders(Stream s)
        {
            Ascii(s, $"Content-Type: message/rfc822; {ParameterValue("name", fileName)}\r\n");
            Ascii(s, $"Content-Disposition: attachment; {ParameterValue("filename", fileName)}\r\n");
            Ascii(s, "Content-Transfer-Encoding: 7bit\r\n");
        }

        public override void WriteContent(Stream s)
        {
            if (embedded.RawMime is { } raw)
            {
                s.Write(raw, 0, raw.Length);
            }
            else
            {
                WriteMessage(s, embedded, depth);
            }
        }
    }

    /// <summary>A clear-signed message's body: the signed entity's last Content-Type field, then its body byte for byte.</summary>
    private sealed class SignedPart(string contentType, byte[] entity, int bodyStart) : Part
    {
        /// <summary>The entity's parts, or null when it has no header block ending in an empty line, or no multipart/signed Content-Type.</summary>
        public static SignedPart? From(byte[] entity)
        {
            // the header block ends at the first empty line: CRLF CRLF, or LF LF in an entity kept with bare line feeds
            var span = entity.AsSpan();
            var crlf = span.IndexOf("\r\n\r\n"u8);
            var lf = span.IndexOf("\n\n"u8);
            var (end, separator) = lf >= 0 && (crlf < 0 || lf < crlf) ? (lf, 2) : (crlf, 4);
            if (end < 0)
            {
                return null;
            }

            // header fields with their continuation lines; the last Content-Type is the one kept (2.1.3.1.2.3)
            string? contentType = null;
            var current = new StringBuilder();
            foreach (var raw in Encoding.Latin1.GetString(entity, 0, end).Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length > 0 && line[0] is ' ' or '\t')
                {
                    current.Append("\r\n").Append(line);
                    continue;
                }

                if (current.Length > 0 && current.ToString().StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = current.ToString();
                }

                current.Clear().Append(line);
            }

            if (current.Length > 0 && current.ToString().StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
            {
                contentType = current.ToString();
            }

            return contentType is not null && MediaType(contentType["Content-Type:".Length..]) == "multipart/signed"
                ? new SignedPart(contentType, entity, end + separator)
                : null;
        }

        public override void WriteHeaders(Stream s)
        {
            var field = Encoding.Latin1.GetBytes(contentType + "\r\n");
            s.Write(field, 0, field.Length);
        }

        public override void WriteContent(Stream s) => s.Write(entity, bodyStart, entity.Length - bodyStart);
    }

    /// <summary>An encrypted or opaque-signed message's body: the CMS, base64, as the attachment it is to a mail client.</summary>
    private sealed class OpaquePart(byte[] cms, string contentType, string fileName) : Part
    {
        public override void WriteHeaders(Stream s)
        {
            Ascii(s, Fold("Content-Type: " + contentType) + "\r\n");
            Ascii(s, $"Content-Disposition: attachment; {ParameterValue("filename", fileName)}\r\n");
            Ascii(s, "Content-Transfer-Encoding: base64\r\n");
        }

        public override void WriteContent(Stream s) => Base64(s, new MemoryStream(cms, writable: false));
    }

    private sealed class Multipart(string subtype, IReadOnlyList<Part> parts) : Part
    {
        private readonly string _boundary = "----=_AnyFS_" + Guid.NewGuid().ToString("N");

        public override void WriteHeaders(Stream s) => Ascii(s, $"Content-Type: multipart/{subtype}; boundary=\"{_boundary}\"\r\n");

        public override void WriteContent(Stream s)
        {
            foreach (var part in parts)
            {
                Ascii(s, $"--{_boundary}\r\n");
                part.WriteHeaders(s);
                Ascii(s, "\r\n");
                part.WriteContent(s);
                Ascii(s, "\r\n");
            }

            Ascii(s, $"--{_boundary}--\r\n");
        }
    }

    // --- encodings --------------------------------------------------------------------------------------------------------

    /// <summary>A name or filename parameter: plain when ASCII, else RFC 2231 (UTF-8, percent-encoded) with an RFC 2047 fallback.</summary>
    private static string ParameterValue(string parameter, string value)
    {
        if (value.All(c => c is >= ' ' and <= '~' && c != '"' && c != '\\'))
        {
            return $"{parameter}=\"{value}\"";
        }

        var encoded = string.Concat(Encoding.UTF8.GetBytes(value).Select(b => b is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A or 0x2E or 0x2D or 0x5F ? ((char)b).ToString() : $"%{b:X2}"));
        return $"{parameter}=\"{EncodeWord(value)}\"; {parameter}*=utf-8''{encoded}";
    }

    private static void QuotedPrintable(Stream s, ReadOnlySpan<byte> data)
    {
        var line = new StringBuilder(80);
        void Flush(bool soft)
        {
            if (soft)
            {
                line.Append('=');
            }

            line.Append("\r\n");
            Ascii(s, line.ToString());
            line.Clear();
        }

        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b == '\r' && i + 1 < data.Length && data[i + 1] == '\n')
            {
                // a line's trailing space or tab is encoded, else the line ends plain
                if (line.Length > 0 && line[^1] is ' ' or '\t')
                {
                    var last = line[^1];
                    line.Length--;
                    line.Append('=').Append(((int)last).ToString("X2", CultureInfo.InvariantCulture));
                }

                Flush(soft: false);
                i++;
                continue;
            }

            string piece = b is >= 33 and <= 126 && b != '=' || b is 32 or 9 ? ((char)b).ToString() : "=" + b.ToString("X2", CultureInfo.InvariantCulture);
            if (line.Length + piece.Length > 75)
            {
                Flush(soft: true);
            }

            line.Append(piece);
        }

        if (line.Length > 0)
        {
            Flush(soft: false);
        }
    }

    private static void Base64(Stream s, Stream content)
    {
        var buffer = new byte[57 * 64];   // 57 bytes make one 76-character line
        int n;
        while ((n = content.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)) > 0)
        {
            var text = Convert.ToBase64String(buffer, 0, n);
            for (var at = 0; at < text.Length; at += 76)
            {
                Ascii(s, text.Substring(at, Math.Min(76, text.Length - at)));
                Ascii(s, "\r\n");
            }
        }
    }

    private static void Ascii(Stream s, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
    }
}
