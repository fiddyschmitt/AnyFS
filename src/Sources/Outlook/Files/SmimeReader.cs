using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using AnyFS.Sources.Outlook.Mail;
using MimeKit;

namespace AnyFS.Sources.Outlook.Files;

/// <summary>
/// The keys an Outlook source opens encrypted S/MIME mail with: the user's certificates in the system's personal stores
/// (a smart card's appear there while it is in, and its driver asks for the PIN, as it does for Outlook) and, for tests,
/// certificates of their own. A decryption the user declined (the PIN prompt cancelled) is not tried again for a while,
/// so browsing encrypted mail does not ask again at every message.
/// </summary>
internal sealed class SmimeKeys(X509Certificate2Collection? extra = null)
{
    private static readonly TimeSpan PauseAfterDecline = TimeSpan.FromMinutes(10);
    private long _declinedUntil;

    /// <summary>Certificates (with their private keys) tried as well as the system's.</summary>
    public X509Certificate2Collection Extra { get; } = extra ?? [];

    /// <summary>Whether a decryption was declined a short while ago.</summary>
    public bool Paused => DateTime.UtcNow.Ticks < Interlocked.Read(ref _declinedUntil);

    public void Declined() => Interlocked.Exchange(ref _declinedUntil, (DateTime.UtcNow + PauseAfterDecline).Ticks);
}

/// <summary>
/// An S/MIME message opened for reading (spec 7.7, the user's "PIN-prompt emails"): a signed message's content with its
/// signature checked (and whether the signer's certificate is trusted on this computer, revocation not looked up), an
/// encrypted one's content decrypted with the user's certificate, layer by layer, so its Markdown shows the body and its
/// attachments folder the real attachments. The message's .eml stays the message as it came, which Outlook opens itself.
/// What could not be opened, and why, is said in the Markdown instead (<see cref="MailItem.Security"/>).
/// </summary>
internal static class SmimeReader
{
    private const int MaxLayers = 4;

    /// <summary>Whether the item is S/MIME mail by its class (IPM.Note.SMIME, IPM.Note.SMIME.MultipartSigned), as Outlook and Exchange keep it.</summary>
    public static bool IsSmime(MailItem item) => item.MessageClass.StartsWith("IPM.Note.SMIME", StringComparison.OrdinalIgnoreCase);

    /// <summary>The item as its S/MIME content shows it: its body and attachments from inside; as it is, with a note, when the content cannot be had.</summary>
    /// <param name="mime">The message as it came: its .eml.</param>
    public static MailItem Open(MailItem item, byte[] mime, SmimeKeys keys)
    {
        var notes = new List<string>();
        MimeEntity? content;
        try
        {
            content = Unwrap(mime, keys, notes, 0);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or InvalidOperationException or ArgumentException or IOException)
        {
            notes.Add($"its S/MIME content does not read ({ex.Message.TrimEnd('.')})");
            content = null;
        }

        var security = notes.Count == 0 ? null : string.Join("; ", notes);
        return content is null ? item with { Security = security } : Shown(item, content) with { Security = security };
    }

    /// <summary>The innermost content of an S/MIME message: each signed layer checked and taken off, each encrypted one decrypted; null when a layer cannot be opened.</summary>
    private static MimeEntity? Unwrap(byte[] raw, SmimeKeys keys, List<string> notes, int depth)
    {
        var entity = MimeEntity.Load(new MemoryStream(raw, writable: false));
        if (depth >= MaxLayers)
        {
            return entity;
        }

        if (entity.ContentType.IsMimeType("multipart", "signed") && entity is Multipart { Count: 2 } multipart)
        {
            // the content is checked as the bytes it was signed as, not as parsed and written again
            var signed = SignedBytes(raw, entity.ContentType.Boundary) ?? throw new FormatException("a multipart/signed body without its signed part");
            var signature = new SignedCms(new ContentInfo(signed), detached: true);
            signature.Decode(Decoded(multipart[1] as MimePart ?? throw new FormatException("a multipart/signed body whose second part is no signature")));
            notes.Add(SignatureNote(signature));
            return Unwrap(signed, keys, notes, depth + 1);
        }

        if (entity is MimePart part && IsPkcs7(part))
        {
            var der = Decoded(part);
            switch (MimeWriter.SmimeType(der))
            {
                case "signed-data":
                    {
                        var signature = new SignedCms();
                        signature.Decode(der);
                        notes.Add(SignatureNote(signature));
                        return Unwrap(signature.ContentInfo.Content, keys, notes, depth + 1);
                    }

                case "enveloped-data":
                case "authEnveloped-data":
                    return Decrypt(der, keys, notes) is { } opened ? Unwrap(opened, keys, notes, depth + 1) : null;
                default:
                    notes.Add("its S/MIME part is of a kind this does not open");
                    return null;
            }
        }

        return entity;
    }

    /// <summary>The content of an enveloped CMS, decrypted with the user's certificate; null (with a note) when it cannot be.</summary>
    private static byte[]? Decrypt(byte[] der, SmimeKeys keys, List<string> notes)
    {
        if (keys.Paused)
        {
            notes.Add("encrypted; opening it was declined a short while ago, so it is not asked again yet (message.eml opens in Outlook, which decrypts it)");
            return null;
        }

        var envelope = new EnvelopedCms();
        envelope.Decode(der);
        try
        {
            envelope.Decrypt(keys.Extra);
        }
        catch (CryptographicException ex) when (Declined(ex))
        {
            keys.Declined();
            notes.Add("encrypted; opening it was declined (the PIN was not given), so encrypted mail is not opened for the next 10 minutes (message.eml opens in Outlook, which decrypts it)");
            return null;
        }
        catch (CryptographicException ex)
        {
            notes.Add(ex.HResult == unchecked((int)0x8009100B) || ex.Message.Contains("recipient", StringComparison.OrdinalIgnoreCase)
                ? "encrypted, and none of the certificates on this computer can open it (message.eml opens in Outlook where the right one is)"
                : $"encrypted, and it could not be opened here ({ex.Message.TrimEnd('.')}; message.eml opens in Outlook, which decrypts it)");
            return null;
        }

        notes.Add("encrypted for you, opened with your certificate");
        return envelope.ContentInfo.Content;
    }

    /// <summary>A decryption the user declined: the smart card's PIN prompt cancelled.</summary>
    private static bool Declined(CryptographicException ex)
        => ex.HResult is unchecked((int)0x8010006E) or unchecked((int)0x800704C7) or unchecked((int)0x80090036)
            || ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase);

    /// <summary>Who signed, and whether the signature holds and the signer's certificate is trusted on this computer (revocation not looked up).</summary>
    private static string SignatureNote(SignedCms cms)
    {
        var signer = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0].Certificate : null;
        var who = signer is null ? "an unknown signer" : Describe(signer);
        try
        {
            cms.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException)
        {
            return $"signed by {who}, but the signature does not match the message: it was changed after it was signed";
        }

        if (signer is null)
        {
            return $"signed by {who}; the signature holds";
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ExtraStore.AddRange(cms.Certificates);
        return chain.Build(signer)
            ? $"signed by {who}; the signature holds and the certificate is trusted here"
            : $"signed by {who}; the signature holds, but the certificate is not trusted here";
    }

    private static string Describe(X509Certificate2 certificate)
    {
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        var email = certificate.GetNameInfo(X509NameType.EmailName, forIssuer: false);
        return string.IsNullOrEmpty(email) || string.Equals(name, email, StringComparison.OrdinalIgnoreCase) ? name : $"{name} <{email}>";
    }

    /// <summary>The bytes of a multipart/signed body's first part, exactly as they stand between its first two delimiters.</summary>
    private static byte[]? SignedBytes(byte[] raw, string? boundary)
    {
        if (string.IsNullOrEmpty(boundary))
        {
            return null;
        }

        var span = raw.AsSpan();
        var delimiter = System.Text.Encoding.ASCII.GetBytes("--" + boundary);
        var headerEnd = span.IndexOf("\r\n\r\n"u8);
        var at = headerEnd < 0 ? 0 : headerEnd + 2;
        while (true)
        {
            var found = span[at..].IndexOf(delimiter);
            if (found < 0)
            {
                return null;
            }

            at += found;
            if (at == 0 || span[at - 1] == '\n')
            {
                break;
            }

            at += delimiter.Length;
        }

        var lineEnd = span[at..].IndexOf("\n"u8);
        if (lineEnd < 0)
        {
            return null;
        }

        var start = at + lineEnd + 1;
        var next = span[start..].IndexOf([.. "\r\n"u8, .. delimiter]);
        return next < 0 ? null : span.Slice(start, next).ToArray();
    }

    private static bool IsPkcs7(MimePart part)
        => part.ContentType.IsMimeType("application", "pkcs7-mime") || part.ContentType.IsMimeType("application", "x-pkcs7-mime")
            || part.ContentType.IsMimeType("application", "octet-stream") && (part.FileName ?? "").EndsWith(".p7m", StringComparison.OrdinalIgnoreCase);

    private static byte[] Decoded(MimePart part)
    {
        using var bytes = new MemoryStream();
        part.Content?.DecodeTo(bytes);
        return bytes.ToArray();
    }

    /// <summary>The item with its body and attachments from the S/MIME content: the HTML and text bodies, every other part a file, an attached message an .eml.</summary>
    private static MailItem Shown(MailItem item, MimeEntity content)
    {
        var message = new MimeMessage { Body = content };
        var html = message.HtmlBody;
        var text = message.TextBody;
        var attachments = new List<MailAttachment>();
        var unnamed = 0;
        foreach (var part in message.BodyParts)
        {
            if (part is TextPart body && !body.IsAttachment && body.FileName is null && (body.IsHtml || body.IsPlain))
            {
                continue;
            }

            byte[] bytes;
            string name;
            string type;
            if (part is MessagePart attached)
            {
                using var written = new MemoryStream();
                attached.Message?.WriteTo(written);
                bytes = written.ToArray();
                name = (string.IsNullOrWhiteSpace(attached.Message?.Subject) ? "message" : attached.Message!.Subject.Trim()) + ".eml";
                type = "message/rfc822";
            }
            else if (part is MimePart file)
            {
                bytes = Decoded(file);
                name = file.FileName ?? $"attachment {++unnamed}";
                type = file.ContentType.MimeType;
            }
            else
            {
                continue;
            }

            var data = bytes;
            attachments.Add(new MailAttachment(name, type, part.ContentId, !part.IsAttachment && part.ContentId is not null, data.Length,
                _ => Task.FromResult<Stream>(new MemoryStream(data, writable: false)), null));
        }

        return item with { BodyHtml = html, BodyText = text, BodyRtf = null, Attachments = attachments };
    }
}
