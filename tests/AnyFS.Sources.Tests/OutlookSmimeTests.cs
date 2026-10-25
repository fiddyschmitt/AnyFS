using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AnyFS.Sources.Outlook;
using AnyFS.Sources.Outlook.Files;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Tests;

/// <summary>
/// S/MIME mail opened for its Markdown and attachments folder (spec 7.7): an encrypted message decrypted with the user's
/// certificate, a signed one's signature checked, layers peeled in turn; what cannot be opened said instead. The .eml
/// stays the message as it came. Certificates are made here; the system's stores hold none that open these.
/// </summary>
[TestClass]
public class OutlookSmimeTests
{
    /// <summary>The content inside: an HTML body and a file, as an S/MIME client writes it.</summary>
    private static readonly byte[] Inner = Encoding.ASCII.GetBytes(
        "Content-Type: multipart/mixed; boundary=\"inner-1\"\r\n\r\n--inner-1\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<p>The secret body.</p>\r\n"
        + "--inner-1\r\nContent-Type: application/octet-stream; name=\"report.bin\"\r\nContent-Disposition: attachment; filename=\"report.bin\"\r\nContent-Transfer-Encoding: base64\r\n\r\nAQID\r\n--inner-1--\r\n");

    private static X509Certificate2 Certificate(string name, string email)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}, E={email}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.4")], critical: false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddEmailAddress(email);
        request.CertificateExtensions.Add(names.Build());
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // through a PKCS#12 so the key is one the platform's CMS decrypts with (not kept on disk)
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    private static byte[] Encrypt(byte[] content, X509Certificate2 recipient)
    {
        var envelope = new EnvelopedCms(new ContentInfo(content));
        envelope.Encrypt(new CmsRecipient(recipient));
        return envelope.Encode();
    }

    /// <summary>A multipart/signed entity over the content, as Outlook keeps one: its own MIME-Version and Content-Type first.</summary>
    private static byte[] SignedEntity(byte[] content, X509Certificate2 signer)
    {
        var signature = new SignedCms(new ContentInfo(content), detached: true);
        signature.ComputeSignature(new CmsSigner(signer) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") }, silent: true);
        var text = "MIME-Version: 1.0\r\nContent-Type: multipart/signed; protocol=\"application/pkcs7-signature\"; micalg=sha-256; boundary=\"outer-1\"\r\n\r\n"
            + "This is an S/MIME signed message\r\n\r\n--outer-1\r\n";
        var tail = "\r\n--outer-1\r\nContent-Type: application/pkcs7-signature; name=\"smime.p7s\"\r\nContent-Transfer-Encoding: base64\r\n\r\n"
            + Convert.ToBase64String(signature.Encode(), Base64FormattingOptions.InsertLineBreaks) + "\r\n--outer-1--\r\n";
        return [.. Encoding.ASCII.GetBytes(text), .. content, .. Encoding.ASCII.GetBytes(tail)];
    }

    private static MailItem Item(string messageClass, string name, string contentType, byte[] content, string? bodyHtml = null) => new()
    {
        Summary = new MailItemSummary("200064", MailItemKind.Message, "Secret", "Jane Doe", new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero), null, null, 0, true),
        MessageClass = messageClass,
        From = new MailAddress("Jane Doe", "jane@contoso.test"),
        Recipients = [new MailRecipient(MailRecipientKind.To, new MailAddress("Sam", "sam@contoso.test"))],
        Sent = new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero),
        BodyHtml = bodyHtml,
        Attachments = [new MailAttachment(name, contentType, null, false, content.Length, _ => Task.FromResult<Stream>(new MemoryStream(content)), null)],
    };

    private static async Task<(string Markdown, string Eml, Dictionary<string, byte[]> Attachments)> FilesAsync(MailItem item, SmimeKeys keys)
    {
        var files = await ItemFiles.BuildAsync(item, item.Summary, "x", CancellationToken.None, keys);
        var attachments = new Dictionary<string, byte[]>();
        foreach (var file in files.Attachments)
        {
            await using var stream = await file.Attachment!.OpenAsync(CancellationToken.None);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            attachments[file.Name] = copy.ToArray();
        }

        return (Encoding.UTF8.GetString(files.File("message.md")!.Bytes!), Encoding.ASCII.GetString(files.File("message.eml")!.Bytes!), attachments);
    }

    [TestMethod]
    public async Task AnEncryptedMessageIsOpenedWithTheUsersCertificate()
    {
        using var mine = Certificate("Sam", "sam@contoso.test");
        var item = Item("IPM.Note.SMIME", "smime.p7m", "application/pkcs7-mime", Encrypt(Inner, mine));

        var (markdown, eml, attachments) = await FilesAsync(item, new SmimeKeys([mine]));
        StringAssert.Contains(markdown, "\n---\n\nThe secret body.", "the body from inside");
        StringAssert.Contains(markdown, "**Security:** encrypted for you, opened with your certificate");
        StringAssert.Contains(markdown, "[report.bin](attachments/report.bin)");
        CollectionAssert.AreEquivalent(new[] { "report.bin" }, attachments.Keys.ToList(), "the real attachments, not the smime.p7m");
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, attachments["report.bin"]);
        StringAssert.Contains(eml, "Content-Type: application/pkcs7-mime; smime-type=enveloped-data", "the .eml stays the message as it came");
    }

    [TestMethod]
    public async Task AnEncryptedMessageNoCertificateHereOpensSaysSo()
    {
        using var someoneElse = Certificate("Kim", "kim@contoso.test");
        var item = Item("IPM.Note.SMIME", "smime.p7m", "application/pkcs7-mime", Encrypt(Inner, someoneElse));

        var (markdown, _, attachments) = await FilesAsync(item, new SmimeKeys());
        StringAssert.Contains(markdown, "**Security:** encrypted, and none of the certificates on this computer can open it");
        CollectionAssert.AreEquivalent(new[] { "smime.p7m" }, attachments.Keys.ToList(), "what it is, as it is");
    }

    [TestMethod]
    public async Task ASignedMessageShowsItsContentAndWhetherItsSignatureHolds()
    {
        using var jane = Certificate("Jane Doe", "jane@contoso.test");
        var entity = SignedEntity(Inner, jane);

        // as Outlook keeps it: one nameless multipart/signed attachment, the body promoted beside it
        var (markdown, eml, attachments) = await FilesAsync(Item("IPM.Note.SMIME.MultipartSigned", "attachment", "multipart/signed", entity, bodyHtml: "<p>The secret body.</p>"), new SmimeKeys());
        StringAssert.Contains(markdown, "\n---\n\nThe secret body.");
        StringAssert.Contains(markdown, "**Security:** signed by Jane Doe <jane@contoso.test>; the signature holds, but the certificate is not trusted here");
        CollectionAssert.AreEquivalent(new[] { "report.bin" }, attachments.Keys.ToList(), "the real attachments, not the signed entity");
        StringAssert.Contains(eml, "Content-Type: multipart/signed;");

        // a message changed after it was signed
        var changed = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(entity).Replace("The secret body.", "The secret b0dy.", StringComparison.Ordinal));
        var (tampered, _, _) = await FilesAsync(Item("IPM.Note.SMIME.MultipartSigned", "attachment", "multipart/signed", changed), new SmimeKeys());
        StringAssert.Contains(tampered, "**Security:** signed by Jane Doe <jane@contoso.test>, but the signature does not match the message: it was changed after it was signed");
    }

    [TestMethod]
    public async Task AMessageEncryptedAndSignedIsOpenedLayerByLayer()
    {
        using var jane = Certificate("Jane Doe", "jane@contoso.test");
        using var sam = Certificate("Sam", "sam@contoso.test");
        var signed = SignedEntity(Inner, jane)[(Encoding.ASCII.GetByteCount("MIME-Version: 1.0\r\n"))..];
        var item = Item("IPM.Note.SMIME", "smime.p7m", "application/pkcs7-mime", Encrypt(signed, sam));

        var (markdown, _, attachments) = await FilesAsync(item, new SmimeKeys([sam]));
        StringAssert.Contains(markdown, "**Security:** encrypted for you, opened with your certificate; signed by Jane Doe <jane@contoso.test>; the signature holds");
        StringAssert.Contains(markdown, "\n---\n\nThe secret body.");
        CollectionAssert.AreEquivalent(new[] { "report.bin" }, attachments.Keys.ToList());
    }
}
