using System.Globalization;
using LibOutlook;

namespace AnyFS.Sources.Outlook.Mail;

/// <summary>
/// A PST or OST file as a mail store (spec 7.7.3), read through LibOutlook: the folders under the file's top folder
/// (Outlook's hidden ones left out unless asked for), each folder's items from its contents table (the summary cells,
/// no item opened), and an item in full when it is opened. Items are named by their node id; an attached message by its
/// parent's id and its place among the attachments ("200024/1"), since a PST gives it none of its own.
/// </summary>
internal sealed class PstMailStore : IMailStore
{
    private const ushort AttributeHidden = 0x10F4;
    private const ushort SenderSmtpAddress = 0x5D01;
    private const ushort SentRepresentingSmtpAddress = 0x5D02;
    private const ushort RecipientTrackStatus = 0x5FFF;
    private const ushort AttachmentContactPhoto = 0x7FFF;

    private readonly string _path;
    private readonly bool _hidden;
    private readonly object _gate = new();
    private PstFile _pst;
    private NamedIds _ids;

    public PstMailStore(string path, bool includeHidden)
    {
        _path = path;
        _hidden = includeHidden;
        _pst = PstFile.Open(path);
        _ids = new NamedIds(_pst.NamedProperties);
        Folders = MapFolders(_pst.TopFolder);
    }

    public string Name => string.IsNullOrWhiteSpace(_pst.DisplayName) ? Path.GetFileNameWithoutExtension(_path) : _pst.DisplayName;

    public PstFormat Format => _pst.Format;

    public IReadOnlyList<MailFolder> Folders { get; private set; }

    public IEnumerable<MailItemSummary> Items(MailFolder folder)
    {
        var pstFolder = _pst.GetFolder(ParseNid(folder.Id));
        if (pstFolder is null)
        {
            yield break;
        }

        var messages = _hidden ? pstFolder.Messages.Concat(pstFolder.AssociatedMessages) : pstFolder.Messages;
        foreach (var message in messages)
        {
            yield return Summarise(message, message.Nid.ToString("X", CultureInfo.InvariantCulture));
        }
    }

    public MailItem? Open(string itemId)
    {
        var parts = itemId.Split('/');
        var message = _pst.GetMessage(ParseNid(parts[0]));
        var id = parts[0];
        for (var i = 1; message is not null && i < parts.Length; i++)
        {
            var index = int.Parse(parts[i], CultureInfo.InvariantCulture);
            var attachments = message.Attachments;
            message = index >= 0 && index < attachments.Count ? attachments[index].EmbeddedMessage : null;
            id += "/" + parts[i];
        }

        return message is null ? null : Build(message, id);
    }

    public void Refresh()
    {
        lock (_gate)
        {
            _pst.Refresh();
            _ids = new NamedIds(_pst.NamedProperties);
            Folders = MapFolders(_pst.TopFolder);
        }
    }

    public void Dispose() => _pst.Dispose();

    // --- folders ----------------------------------------------------------------------------------------------------------

    private IReadOnlyList<MailFolder> MapFolders(PstFolder parent)
    {
        var list = new List<MailFolder>();
        foreach (var folder in parent.Folders)
        {
            if (!_hidden && folder.Properties.GetBoolean(AttributeHidden) == true)
            {
                continue;
            }

            list.Add(new MailFolder(
                folder.Nid.ToString("X", CultureInfo.InvariantCulture),
                folder.Name,
                folder.ContainerClass,
                MapFolders(folder),
                folder.ContentCount + (_hidden ? folder.AssociatedContentCount : 0),
                Utc(folder.CreationTime),
                Utc(folder.LastModificationTime)));
        }

        return list;
    }

    // --- items ------------------------------------------------------------------------------------------------------------

    private MailItemSummary Summarise(PstMessage message, string id)
    {
        var kind = MailItemKinds.From(message.MessageClass);
        var p = message.Properties;
        DateTimeOffset? date = kind == MailItemKind.Appointment
            ? Utc(Get(p, _ids.AppointmentStart)?.Value as DateTime?) ?? Utc(message.CreationTime) ?? Utc(message.LastModificationTime)
            : Utc(message.MessageDeliveryTime) ?? Utc(message.ClientSubmitTime) ?? Utc(message.CreationTime) ?? Utc(message.LastModificationTime);
        // a draft or a sent item of one's own may name no sender: the first recipient stands in, as Outlook's Drafts view shows the To
        var sender = Blank(p.GetString(PstPropertyIds.SentRepresentingName)) ?? Blank(message.SenderName) ?? Blank(message.SentRepresentingEmailAddress) ?? Blank(message.SenderEmailAddress)
            ?? Blank(message.DisplayTo?.Split(';', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
        var subject = Blank(message.Subject) ?? (kind == MailItemKind.Contact ? Blank(p.GetString(PstPropertyIds.DisplayName)) : null) ?? "";
        return new MailItemSummary(id, kind, subject, sender, date, Utc(message.CreationTime), Utc(message.LastModificationTime), message.Size, message.HasAttachments);
    }

    private MailItem Build(PstMessage message, string id)
    {
        var summary = Summarise(message, id);
        var p = message.Properties;
        var attachments = new List<MailAttachment>();
        var all = message.Attachments;
        for (var i = 0; i < all.Count; i++)
        {
            var a = all[i];
            var embeddedIndex = i;
            var embedded = a.Method == PstAttachMethod.EmbeddedMessage ? a.EmbeddedMessage : null;
            var name = Blank(a.FileName) ?? (embedded is not null ? Blank(embedded.Subject) ?? "message" : "attachment") ;
            if (embedded is not null && !name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase))
            {
                name += ".eml";
            }

            attachments.Add(new MailAttachment(
                name,
                embedded is not null ? "message/rfc822" : Blank(a.MimeTag) ?? ContentTypes.FromName(name),
                Blank(a.ContentId),
                a.IsInline,
                embedded is not null ? 0 : a.Size,
                a.OpenContent,
                embedded is null ? null : () => Build(embedded, $"{id}/{embeddedIndex}"),
                a.Properties.GetBoolean(AttachmentContactPhoto) == true));
        }

        return new MailItem
        {
            Summary = summary,
            MessageClass = message.MessageClass,
            From = new MailAddress(
                Blank(message.SenderName) ?? Blank(message.SentRepresentingName),
                Smtp(p.GetString(SenderSmtpAddress), message.SenderEmailAddress, message.SenderAddressType)
                    ?? Smtp(p.GetString(SentRepresentingSmtpAddress), message.SentRepresentingEmailAddress, p.GetString(PstPropertyIds.SentRepresentingAddressType))),
            Recipients = message.Recipients.Where(r => r.Type != PstRecipientType.Originator).Select(Map).ToList(),
            Sent = Utc(message.ClientSubmitTime),
            Received = Utc(message.MessageDeliveryTime),
            Created = Utc(message.CreationTime),
            Modified = Utc(message.LastModificationTime),
            InternetMessageId = Blank(message.InternetMessageId),
            InReplyTo = Blank(p.GetString(PstPropertyIds.InReplyToId)),
            References = Blank(p.GetString(PstPropertyIds.InternetReferences)),
            TransportHeaders = Blank(message.TransportMessageHeaders),
            BodyText = Blank(message.Body),
            BodyHtml = Blank(message.BodyHtml),
            BodyRtf = Blank(message.BodyRtf),
            Importance = message.Importance,
            IsRead = message.IsRead,
            IsDraft = message.IsUnsent,
            Attachments = attachments,
            Appointment = summary.Kind == MailItemKind.Appointment ? AppointmentOf(p) : null,
            Contact = summary.Kind == MailItemKind.Contact ? ContactOf(p) : null,
        };
    }

    private static MailRecipient Map(PstRecipient r)
    {
        var kind = r.Type switch
        {
            PstRecipientType.Cc => MailRecipientKind.Cc,
            PstRecipientType.Bcc => MailRecipientKind.Bcc,
            _ => MailRecipientKind.To,
        };
        var response = r.Properties.GetInt32(RecipientTrackStatus) switch
        {
            1 => "organizer",
            2 => "tentative",
            3 => "accepted",
            4 => "declined",
            _ => null,
        };
        return new MailRecipient(kind, new MailAddress(Blank(r.DisplayName), Smtp(r.SmtpAddress, r.EmailAddress, r.AddressType)), response);
    }

    private AppointmentDetail AppointmentOf(PstProperties p)
    {
        var busy = (Get(p, _ids.BusyStatus)?.Value as int?) switch
        {
            0 => "free",
            1 => "tentative",
            2 => "busy",
            3 => "out of office",
            4 => "working elsewhere",
            _ => null,
        };
        var uid = Get(p, _ids.CleanGlobalObjectId)?.Value as byte[] ?? Get(p, _ids.GlobalObjectId)?.Value as byte[];
        return new AppointmentDetail(
            Utc(Get(p, _ids.AppointmentStart)?.Value as DateTime?),
            Utc(Get(p, _ids.AppointmentEnd)?.Value as DateTime?),
            Get(p, _ids.AllDay)?.Value is bool allDay && allDay,
            Blank(Get(p, _ids.Location)?.Value as string),
            Get(p, _ids.IsRecurring)?.Value is bool recurring && recurring,
            Blank(Get(p, _ids.RecurrencePattern)?.Value as string),
            busy,
            uid is { Length: > 0 } ? Convert.ToHexString(uid) : null);
    }

    private ContactDetail ContactOf(PstProperties p)
    {
        var emails = new List<string>();
        foreach (var id in new[] { _ids.Email1Address, _ids.Email2Address, _ids.Email3Address })
        {
            if (Blank(Get(p, id)?.Value as string) is { } email && !emails.Contains(email, StringComparer.OrdinalIgnoreCase))
            {
                emails.Add(email);
            }
        }

        var phones = new List<(string, string)>();
        void Phone(string type, ushort tag)
        {
            if (Blank(p.GetString(tag)) is { } number)
            {
                phones.Add((type, number));
            }
        }

        Phone("WORK,VOICE", PstPropertyIds.BusinessTelephoneNumber);
        Phone("WORK,VOICE", 0x3A1B);   // Business2TelephoneNumber
        Phone("HOME,VOICE", PstPropertyIds.HomeTelephoneNumber);
        Phone("HOME,VOICE", 0x3A2F);   // Home2TelephoneNumber
        Phone("CELL,VOICE", PstPropertyIds.MobileTelephoneNumber);
        Phone("WORK,FAX", 0x3A24);     // BusinessFaxNumber
        Phone("HOME,FAX", 0x3A25);     // HomeFaxNumber
        Phone("PAGER", 0x3A21);        // PagerTelephoneNumber
        Phone("VOICE", 0x3A1F);        // OtherTelephoneNumber

        var addresses = new List<(string, string, string, string, string, string)>();
        void Address(string type, string? street, string? city, string? state, string? postal, string? country)
        {
            if (!string.IsNullOrWhiteSpace(street + city + state + postal + country))
            {
                addresses.Add((type, street ?? "", city ?? "", state ?? "", postal ?? "", country ?? ""));
            }
        }

        Address("WORK", Get(p, _ids.WorkAddressStreet)?.Value as string, Get(p, _ids.WorkAddressCity)?.Value as string, Get(p, _ids.WorkAddressState)?.Value as string, Get(p, _ids.WorkAddressPostalCode)?.Value as string, Get(p, _ids.WorkAddressCountry)?.Value as string);
        Address("HOME", p.GetString(0x3A5D), p.GetString(0x3A59), p.GetString(0x3A5C), p.GetString(0x3A5B), p.GetString(0x3A5A));
        Address("POSTAL", p.GetString(0x3A63), p.GetString(0x3A5F), p.GetString(0x3A62), p.GetString(0x3A61), p.GetString(0x3A60));

        return new ContactDetail(
            Blank(Get(p, _ids.FileUnder)?.Value as string) ?? Blank(p.GetString(PstPropertyIds.DisplayName)),
            Blank(p.GetString(0x3A45)),
            Blank(p.GetString(PstPropertyIds.GivenName)),
            Blank(p.GetString(0x3A44)),
            Blank(p.GetString(PstPropertyIds.Surname)),
            Blank(p.GetString(0x3A05)),
            Blank(p.GetString(0x3A4F)),
            Blank(p.GetString(PstPropertyIds.CompanyName)),
            Blank(p.GetString(PstPropertyIds.Title)),
            Blank(p.GetString(PstPropertyIds.DepartmentName)),
            emails,
            phones,
            addresses,
            Blank(p.GetString(0x3A51)) ?? Blank(Get(p, _ids.WebPage)?.Value as string),
            Blank(Get(p, _ids.ImAddress)?.Value as string),
            Utc(p.GetDateTime(0x3A42)),
            Utc(p.GetDateTime(0x3A41)));
    }

    // --- helpers ----------------------------------------------------------------------------------------------------------

    private static PstProperty? Get(PstProperties p, ushort? id) => id is { } known ? p.Get(known) : null;

    private static uint ParseNid(string id) => uint.Parse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static DateTimeOffset? Utc(DateTime? time) => time is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null;

    /// <summary>An SMTP address: the one stored as such, else the address itself when it is SMTP (an Exchange legacy DN is not one).</summary>
    private static string? Smtp(string? smtp, string? address, string? addressType)
    {
        if (!string.IsNullOrWhiteSpace(smtp))
        {
            return smtp;
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        if (string.Equals(addressType, "EX", StringComparison.OrdinalIgnoreCase) || address.StartsWith("/o=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return address;
    }

    /// <summary>The ids this file gives the named properties the store reads ([MS-OXPROPS]); null for one the file never stored.</summary>
    private sealed class NamedIds
    {
        private static readonly Guid Appointment = new("00062002-0000-0000-C000-000000000046");
        private static readonly Guid Meeting = new("6ED8DA90-450B-101B-98DA-00AA003F1305");
        private static readonly Guid Address = new("00062004-0000-0000-C000-000000000046");

        public NamedIds(PstNamedProperties named)
        {
            AppointmentStart = named.Find(Appointment, 0x820D)?.PropertyId;
            AppointmentEnd = named.Find(Appointment, 0x820E)?.PropertyId;
            Location = named.Find(Appointment, 0x8208)?.PropertyId;
            AllDay = named.Find(Appointment, 0x8215)?.PropertyId;
            IsRecurring = named.Find(Appointment, 0x8223)?.PropertyId;
            RecurrencePattern = named.Find(Appointment, 0x8232)?.PropertyId;
            BusyStatus = named.Find(Appointment, 0x8205)?.PropertyId;
            GlobalObjectId = named.Find(Meeting, 0x0003)?.PropertyId;
            CleanGlobalObjectId = named.Find(Meeting, 0x0023)?.PropertyId;
            FileUnder = named.Find(Address, 0x8005)?.PropertyId;
            Email1Address = named.Find(Address, 0x8083)?.PropertyId;
            Email2Address = named.Find(Address, 0x8093)?.PropertyId;
            Email3Address = named.Find(Address, 0x80A3)?.PropertyId;
            WorkAddressStreet = named.Find(Address, 0x8045)?.PropertyId;
            WorkAddressCity = named.Find(Address, 0x8046)?.PropertyId;
            WorkAddressState = named.Find(Address, 0x8047)?.PropertyId;
            WorkAddressPostalCode = named.Find(Address, 0x8048)?.PropertyId;
            WorkAddressCountry = named.Find(Address, 0x8049)?.PropertyId;
            ImAddress = named.Find(Address, 0x8062)?.PropertyId;
            WebPage = named.Find(Address, 0x802B)?.PropertyId;
        }

        public ushort? AppointmentStart { get; }

        public ushort? AppointmentEnd { get; }

        public ushort? Location { get; }

        public ushort? AllDay { get; }

        public ushort? IsRecurring { get; }

        public ushort? RecurrencePattern { get; }

        public ushort? BusyStatus { get; }

        public ushort? GlobalObjectId { get; }

        public ushort? CleanGlobalObjectId { get; }

        public ushort? FileUnder { get; }

        public ushort? Email1Address { get; }

        public ushort? Email2Address { get; }

        public ushort? Email3Address { get; }

        public ushort? WorkAddressStreet { get; }

        public ushort? WorkAddressCity { get; }

        public ushort? WorkAddressState { get; }

        public ushort? WorkAddressPostalCode { get; }

        public ushort? WorkAddressCountry { get; }

        public ushort? ImAddress { get; }

        public ushort? WebPage { get; }
    }
}

/// <summary>A content type from a file name, for an attachment that carries none.</summary>
internal static class ContentTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".htm"] = "text/html",
        [".html"] = "text/html",
        [".xml"] = "text/xml",
        [".json"] = "application/json",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".msg"] = "application/vnd.ms-outlook",
        [".eml"] = "message/rfc822",
        [".ics"] = "text/calendar",
        [".vcf"] = "text/vcard",
        [".mp3"] = "audio/mpeg",
        [".mp4"] = "video/mp4",
        [".rtf"] = "application/rtf",
    };

    public static string FromName(string name) => ByExtension.TryGetValue(Path.GetExtension(name), out var type) ? type : "application/octet-stream";
}
