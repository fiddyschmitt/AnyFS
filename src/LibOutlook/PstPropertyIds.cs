using System.Collections.Frozen;

namespace LibOutlook;

/// <summary>
/// The ids of the MAPI properties this library reads and the ones a caller most often wants ([MS-OXPROPS]). A property's
/// id is the high 16 bits of its tag; the low 16 are its type, which <see cref="PstProperties"/> reports with the value.
/// </summary>
public static class PstPropertyIds
{
    /// <summary>PidTagImportance: 0 low, 1 normal, 2 high.</summary>
    public const ushort Importance = 0x0017;

    /// <summary>PidTagMessageClass: "IPM.Note", "IPM.Contact", "IPM.Appointment" and so on.</summary>
    public const ushort MessageClass = 0x001A;

    /// <summary>PidTagPriority: -1 low, 0 normal, 1 urgent.</summary>
    public const ushort Priority = 0x0026;

    /// <summary>PidTagReadReceiptRequested.</summary>
    public const ushort ReadReceiptRequested = 0x0029;

    /// <summary>PidTagSensitivity: 0 normal, 1 personal, 2 private, 3 confidential.</summary>
    public const ushort Sensitivity = 0x0036;

    /// <summary>PidTagSubject.</summary>
    public const ushort Subject = 0x0037;

    /// <summary>PidTagClientSubmitTime: when the message was sent.</summary>
    public const ushort ClientSubmitTime = 0x0039;

    /// <summary>PidTagSubjectPrefix: "RE: ", "FW: ".</summary>
    public const ushort SubjectPrefix = 0x003D;

    /// <summary>PidTagReceivedByName.</summary>
    public const ushort ReceivedByName = 0x0040;

    /// <summary>PidTagSentRepresentingName: who the message is from, as shown.</summary>
    public const ushort SentRepresentingName = 0x0042;

    /// <summary>PidTagReceivedRepresentingName.</summary>
    public const ushort ReceivedRepresentingName = 0x0044;

    /// <summary>PidTagSentRepresentingAddressType: "SMTP", "EX".</summary>
    public const ushort SentRepresentingAddressType = 0x0064;

    /// <summary>PidTagSentRepresentingEmailAddress.</summary>
    public const ushort SentRepresentingEmailAddress = 0x0065;

    /// <summary>PidTagConversationTopic.</summary>
    public const ushort ConversationTopic = 0x0070;

    /// <summary>PidTagConversationIndex.</summary>
    public const ushort ConversationIndex = 0x0071;

    /// <summary>PidTagReceivedByAddressType.</summary>
    public const ushort ReceivedByAddressType = 0x0075;

    /// <summary>PidTagReceivedByEmailAddress.</summary>
    public const ushort ReceivedByEmailAddress = 0x0076;

    /// <summary>PidTagTransportMessageHeaders: the Internet headers as received.</summary>
    public const ushort TransportMessageHeaders = 0x007D;

    /// <summary>PidTagRecipientType: 1 To, 2 Cc, 3 Bcc.</summary>
    public const ushort RecipientType = 0x0C15;

    /// <summary>PidTagSenderName.</summary>
    public const ushort SenderName = 0x0C1A;

    /// <summary>PidTagSenderAddressType.</summary>
    public const ushort SenderAddressType = 0x0C1E;

    /// <summary>PidTagSenderEmailAddress.</summary>
    public const ushort SenderEmailAddress = 0x0C1F;

    /// <summary>PidTagDisplayBcc: the Bcc line as shown.</summary>
    public const ushort DisplayBcc = 0x0E02;

    /// <summary>PidTagDisplayCc.</summary>
    public const ushort DisplayCc = 0x0E03;

    /// <summary>PidTagDisplayTo.</summary>
    public const ushort DisplayTo = 0x0E04;

    /// <summary>PidTagMessageDeliveryTime: when the message arrived.</summary>
    public const ushort MessageDeliveryTime = 0x0E06;

    /// <summary>PidTagMessageFlags: 0x1 read, 0x8 unsent, 0x10 has attachments, 0x40 from me.</summary>
    public const ushort MessageFlags = 0x0E07;

    /// <summary>PidTagMessageSize.</summary>
    public const ushort MessageSize = 0x0E08;

    /// <summary>PidTagResponsibility.</summary>
    public const ushort Responsibility = 0x0E0F;

    /// <summary>PidTagHasAttachments.</summary>
    public const ushort HasAttachments = 0x0E1B;

    /// <summary>PidTagNormalizedSubject: the subject without its prefix.</summary>
    public const ushort NormalizedSubject = 0x0E1D;

    /// <summary>PidTagRtfInSync.</summary>
    public const ushort RtfInSync = 0x0E1F;

    /// <summary>PidTagAttachSize: an attachment's size with its overhead.</summary>
    public const ushort AttachSize = 0x0E20;

    /// <summary>PidTagAttachNumber.</summary>
    public const ushort AttachNumber = 0x0E21;

    /// <summary>PidTagRecordKey.</summary>
    public const ushort RecordKey = 0x0FF9;

    /// <summary>PidTagObjectType.</summary>
    public const ushort ObjectType = 0x0FFE;

    /// <summary>PidTagEntryId.</summary>
    public const ushort EntryId = 0x0FFF;

    /// <summary>PidTagBody: the plain-text body.</summary>
    public const ushort Body = 0x1000;

    /// <summary>PidTagRtfCompressed: the RTF body, LZFu-compressed ([MS-OXRTFCP]; see <see cref="RtfCompression"/>).</summary>
    public const ushort RtfCompressed = 0x1009;

    /// <summary>PidTagHtml / PidTagBodyHtml: the HTML body, as bytes in the message's Internet code page.</summary>
    public const ushort Html = 0x1013;

    /// <summary>PidTagInternetMessageId.</summary>
    public const ushort InternetMessageId = 0x1035;

    /// <summary>PidTagInternetReferences.</summary>
    public const ushort InternetReferences = 0x1039;

    /// <summary>PidTagInReplyToId.</summary>
    public const ushort InReplyToId = 0x1042;

    /// <summary>PidTagIconIndex.</summary>
    public const ushort IconIndex = 0x1080;

    /// <summary>PidTagDisplayName: a folder's, recipient's, attachment's or the store's name.</summary>
    public const ushort DisplayName = 0x3001;

    /// <summary>PidTagAddressType.</summary>
    public const ushort AddressType = 0x3002;

    /// <summary>PidTagEmailAddress.</summary>
    public const ushort EmailAddress = 0x3003;

    /// <summary>PidTagComment.</summary>
    public const ushort Comment = 0x3004;

    /// <summary>PidTagCreationTime.</summary>
    public const ushort CreationTime = 0x3007;

    /// <summary>PidTagLastModificationTime.</summary>
    public const ushort LastModificationTime = 0x3008;

    /// <summary>PidTagSearchKey.</summary>
    public const ushort SearchKey = 0x300B;

    /// <summary>PidTagIpmSubTreeEntryId: on the message store, the entry id of the top folder.</summary>
    public const ushort IpmSubTreeEntryId = 0x35E0;

    /// <summary>PidTagIpmOutboxEntryId.</summary>
    public const ushort IpmOutboxEntryId = 0x35E2;

    /// <summary>PidTagIpmWastebasketEntryId: Deleted Items.</summary>
    public const ushort IpmWastebasketEntryId = 0x35E3;

    /// <summary>PidTagIpmSentMailEntryId.</summary>
    public const ushort IpmSentMailEntryId = 0x35E4;

    /// <summary>PidTagViewsEntryId.</summary>
    public const ushort ViewsEntryId = 0x35E5;

    /// <summary>PidTagCommonViewsEntryId.</summary>
    public const ushort CommonViewsEntryId = 0x35E6;

    /// <summary>PidTagFinderEntryId: the search folders' root.</summary>
    public const ushort FinderEntryId = 0x35E7;

    /// <summary>PidTagContentCount: how many messages a folder holds.</summary>
    public const ushort ContentCount = 0x3602;

    /// <summary>PidTagContentUnreadCount.</summary>
    public const ushort ContentUnreadCount = 0x3603;

    /// <summary>PidTagSubfolders: whether a folder has any.</summary>
    public const ushort Subfolders = 0x360A;

    /// <summary>PidTagContainerClass: "IPF.Note", "IPF.Contact", "IPF.Appointment" and so on.</summary>
    public const ushort ContainerClass = 0x3613;

    /// <summary>PidTagAssociatedContentCount: how many hidden (associated) items a folder holds.</summary>
    public const ushort AssociatedContentCount = 0x3617;

    /// <summary>PidTagAttachDataBinary (PtypBinary) or PidTagAttachDataObject (PtypObject): an attachment's content.</summary>
    public const ushort AttachData = 0x3701;

    /// <summary>PidTagAttachEncoding.</summary>
    public const ushort AttachEncoding = 0x3702;

    /// <summary>PidTagAttachExtension: ".pdf".</summary>
    public const ushort AttachExtension = 0x3703;

    /// <summary>PidTagAttachFilename: the short (8.3) file name.</summary>
    public const ushort AttachFilename = 0x3704;

    /// <summary>PidTagAttachMethod: see <see cref="PstAttachMethod"/>.</summary>
    public const ushort AttachMethod = 0x3705;

    /// <summary>PidTagAttachLongFilename.</summary>
    public const ushort AttachLongFilename = 0x3707;

    /// <summary>PidTagAttachPathname.</summary>
    public const ushort AttachPathname = 0x3708;

    /// <summary>PidTagAttachRendering.</summary>
    public const ushort AttachRendering = 0x3709;

    /// <summary>PidTagRenderingPosition: where in the body an attachment is shown; -1 for not inline.</summary>
    public const ushort RenderingPosition = 0x370B;

    /// <summary>PidTagAttachLongPathname.</summary>
    public const ushort AttachLongPathname = 0x370D;

    /// <summary>PidTagAttachMimeTag: the content type.</summary>
    public const ushort AttachMimeTag = 0x370E;

    /// <summary>PidTagAttachContentId: what an HTML body's cid: references name.</summary>
    public const ushort AttachContentId = 0x3712;

    /// <summary>PidTagAttachContentLocation.</summary>
    public const ushort AttachContentLocation = 0x3713;

    /// <summary>PidTagAttachFlags: 0x1 invisible in HTML, 0x2 invisible in RTF, 0x4 rendered in the body.</summary>
    public const ushort AttachFlags = 0x3714;

    /// <summary>PidTagDisplayType.</summary>
    public const ushort DisplayType = 0x3900;

    /// <summary>PidTagSmtpAddress.</summary>
    public const ushort SmtpAddress = 0x39FE;

    /// <summary>PidTagAddressBookDisplayNamePrintable.</summary>
    public const ushort DisplayNamePrintable = 0x39FF;

    /// <summary>PidTagAccount.</summary>
    public const ushort Account = 0x3A00;

    /// <summary>PidTagGivenName.</summary>
    public const ushort GivenName = 0x3A06;

    /// <summary>PidTagBusinessTelephoneNumber.</summary>
    public const ushort BusinessTelephoneNumber = 0x3A08;

    /// <summary>PidTagHomeTelephoneNumber.</summary>
    public const ushort HomeTelephoneNumber = 0x3A09;

    /// <summary>PidTagInitials.</summary>
    public const ushort Initials = 0x3A0A;

    /// <summary>PidTagSurname.</summary>
    public const ushort Surname = 0x3A11;

    /// <summary>PidTagCompanyName.</summary>
    public const ushort CompanyName = 0x3A16;

    /// <summary>PidTagTitle: a contact's job title.</summary>
    public const ushort Title = 0x3A17;

    /// <summary>PidTagDepartmentName.</summary>
    public const ushort DepartmentName = 0x3A18;

    /// <summary>PidTagMobileTelephoneNumber.</summary>
    public const ushort MobileTelephoneNumber = 0x3A1C;

    /// <summary>PidTagInternetCodepage: the code page of the HTML body.</summary>
    public const ushort InternetCodepage = 0x3FDE;

    /// <summary>PidTagMessageLocaleId.</summary>
    public const ushort MessageLocaleId = 0x3FF1;

    /// <summary>PidTagMessageCodepage: the code page of the message's 8-bit strings.</summary>
    public const ushort MessageCodepage = 0x3FFD;

    /// <summary>PidTagRecipientDisplayName.</summary>
    public const ushort RecipientDisplayName = 0x5FF6;

    /// <summary>PidTagLtpRowId: in a PST table, the row's id (a folder's or message's NID).</summary>
    public const ushort LtpRowId = 0x67F2;

    /// <summary>PidTagLtpRowVer.</summary>
    public const ushort LtpRowVer = 0x67F3;

    /// <summary>PidTagAttachmentHidden: an attachment not to be shown in the list (an inline image).</summary>
    public const ushort AttachmentHidden = 0x7FFE;

    private static readonly FrozenDictionary<ushort, string> Names = new Dictionary<ushort, string>
    {
        [Importance] = nameof(Importance),
        [MessageClass] = nameof(MessageClass),
        [Priority] = nameof(Priority),
        [ReadReceiptRequested] = nameof(ReadReceiptRequested),
        [Sensitivity] = nameof(Sensitivity),
        [Subject] = nameof(Subject),
        [ClientSubmitTime] = nameof(ClientSubmitTime),
        [SubjectPrefix] = nameof(SubjectPrefix),
        [ReceivedByName] = nameof(ReceivedByName),
        [SentRepresentingName] = nameof(SentRepresentingName),
        [ReceivedRepresentingName] = nameof(ReceivedRepresentingName),
        [SentRepresentingAddressType] = nameof(SentRepresentingAddressType),
        [SentRepresentingEmailAddress] = nameof(SentRepresentingEmailAddress),
        [ConversationTopic] = nameof(ConversationTopic),
        [ConversationIndex] = nameof(ConversationIndex),
        [ReceivedByAddressType] = nameof(ReceivedByAddressType),
        [ReceivedByEmailAddress] = nameof(ReceivedByEmailAddress),
        [TransportMessageHeaders] = nameof(TransportMessageHeaders),
        [RecipientType] = nameof(RecipientType),
        [SenderName] = nameof(SenderName),
        [SenderAddressType] = nameof(SenderAddressType),
        [SenderEmailAddress] = nameof(SenderEmailAddress),
        [DisplayBcc] = nameof(DisplayBcc),
        [DisplayCc] = nameof(DisplayCc),
        [DisplayTo] = nameof(DisplayTo),
        [MessageDeliveryTime] = nameof(MessageDeliveryTime),
        [MessageFlags] = nameof(MessageFlags),
        [MessageSize] = nameof(MessageSize),
        [Responsibility] = nameof(Responsibility),
        [HasAttachments] = nameof(HasAttachments),
        [NormalizedSubject] = nameof(NormalizedSubject),
        [RtfInSync] = nameof(RtfInSync),
        [AttachSize] = nameof(AttachSize),
        [AttachNumber] = nameof(AttachNumber),
        [RecordKey] = nameof(RecordKey),
        [ObjectType] = nameof(ObjectType),
        [EntryId] = nameof(EntryId),
        [Body] = nameof(Body),
        [RtfCompressed] = nameof(RtfCompressed),
        [Html] = nameof(Html),
        [InternetMessageId] = nameof(InternetMessageId),
        [InternetReferences] = nameof(InternetReferences),
        [InReplyToId] = nameof(InReplyToId),
        [IconIndex] = nameof(IconIndex),
        [DisplayName] = nameof(DisplayName),
        [AddressType] = nameof(AddressType),
        [EmailAddress] = nameof(EmailAddress),
        [Comment] = nameof(Comment),
        [CreationTime] = nameof(CreationTime),
        [LastModificationTime] = nameof(LastModificationTime),
        [SearchKey] = nameof(SearchKey),
        [IpmSubTreeEntryId] = nameof(IpmSubTreeEntryId),
        [IpmOutboxEntryId] = nameof(IpmOutboxEntryId),
        [IpmWastebasketEntryId] = nameof(IpmWastebasketEntryId),
        [IpmSentMailEntryId] = nameof(IpmSentMailEntryId),
        [ViewsEntryId] = nameof(ViewsEntryId),
        [CommonViewsEntryId] = nameof(CommonViewsEntryId),
        [FinderEntryId] = nameof(FinderEntryId),
        [ContentCount] = nameof(ContentCount),
        [ContentUnreadCount] = nameof(ContentUnreadCount),
        [Subfolders] = nameof(Subfolders),
        [ContainerClass] = nameof(ContainerClass),
        [AssociatedContentCount] = nameof(AssociatedContentCount),
        [AttachData] = nameof(AttachData),
        [AttachEncoding] = nameof(AttachEncoding),
        [AttachExtension] = nameof(AttachExtension),
        [AttachFilename] = nameof(AttachFilename),
        [AttachMethod] = nameof(AttachMethod),
        [AttachLongFilename] = nameof(AttachLongFilename),
        [AttachPathname] = nameof(AttachPathname),
        [AttachRendering] = nameof(AttachRendering),
        [RenderingPosition] = nameof(RenderingPosition),
        [AttachLongPathname] = nameof(AttachLongPathname),
        [AttachMimeTag] = nameof(AttachMimeTag),
        [AttachContentId] = nameof(AttachContentId),
        [AttachContentLocation] = nameof(AttachContentLocation),
        [AttachFlags] = nameof(AttachFlags),
        [DisplayType] = nameof(DisplayType),
        [SmtpAddress] = nameof(SmtpAddress),
        [DisplayNamePrintable] = nameof(DisplayNamePrintable),
        [Account] = nameof(Account),
        [GivenName] = nameof(GivenName),
        [BusinessTelephoneNumber] = nameof(BusinessTelephoneNumber),
        [HomeTelephoneNumber] = nameof(HomeTelephoneNumber),
        [Initials] = nameof(Initials),
        [Surname] = nameof(Surname),
        [CompanyName] = nameof(CompanyName),
        [Title] = nameof(Title),
        [DepartmentName] = nameof(DepartmentName),
        [MobileTelephoneNumber] = nameof(MobileTelephoneNumber),
        [InternetCodepage] = nameof(InternetCodepage),
        [MessageLocaleId] = nameof(MessageLocaleId),
        [MessageCodepage] = nameof(MessageCodepage),
        [RecipientDisplayName] = nameof(RecipientDisplayName),
        [LtpRowId] = nameof(LtpRowId),
        [LtpRowVer] = nameof(LtpRowVer),
        [AttachmentHidden] = nameof(AttachmentHidden),
    }.ToFrozenDictionary();

    /// <summary>The name of a property id this class knows, or null.</summary>
    public static string? NameOf(ushort id) => Names.TryGetValue(id, out var name) ? name : null;
}
