using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Outlook.Ews;

/// <summary>
/// Exchange Web Services as Exchange Server speaks them (spec 7.7): SOAP to <c>/EWS/Exchange.asmx</c> with a Windows
/// sign-in (Negotiate: Kerberos or NTLM, as the person or with a user name and password), the same operations as
/// <see cref="OwaJsonService"/> in XML ([MS-OXWSCORE], [MS-OXWSFOLD], [MS-OXWSSYNC], [MS-OXWSMTGS], [MS-OXWSCONT]).
/// A busy server (ErrorServerBusy, 503) holds the mailbox's pace and the call is tried again.
/// </summary>
internal sealed class SoapEwsService : IEwsService, IDisposable
{
    private const int Attempts = 4;

    private static readonly XNamespace S = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace T = "http://schemas.microsoft.com/exchange/services/2006/types";
    private static readonly XNamespace M = "http://schemas.microsoft.com/exchange/services/2006/messages";

    private static readonly string[] ListingProperties =
    [
        "item:Subject", "item:DateTimeReceived", "item:DateTimeSent", "item:DateTimeCreated", "item:LastModifiedTime", "item:Size", "item:HasAttachments",
        "item:ItemClass", "item:Importance", "item:DisplayTo", "message:From", "message:Sender", "message:IsRead", "message:InternetMessageId",
    ];

    private static readonly string[] CalendarProperties =
    [
        "item:Subject", "calendar:Start", "calendar:End", "calendar:Location", "calendar:IsAllDayEvent", "calendar:CalendarItemType", "calendar:Organizer",
        "calendar:IsRecurring", "calendar:UID", "calendar:LegacyFreeBusyStatus", "item:ItemClass", "item:LastModifiedTime", "item:DateTimeCreated", "item:HasAttachments", "item:Size",
    ];

    private readonly Uri _url;
    private readonly HttpClient _http;
    private readonly MailboxPace _pace;
    private readonly ILogger _logger;

    /// <param name="url">The EWS endpoint (https://mail.contoso.com/EWS/Exchange.asmx).</param>
    /// <param name="credentials">The Windows sign-in.</param>
    /// <param name="trustAnyCertificate">Accept the server's certificate whatever it is (a lab's self-signed one).</param>
    /// <param name="handler">The handler requests go through: a test's fake; null for the network.</param>
    /// <param name="paceKey">Whose pace the calls share: the account and the server.</param>
    public SoapEwsService(Uri url, ICredentials credentials, bool trustAnyCertificate, HttpMessageHandler? handler, string paceKey, ILogger logger)
    {
        _url = url;
        _pace = MailboxPace.For(paceKey);
        _logger = logger;
        if (handler is null)
        {
            var windows = new HttpClientHandler { Credentials = credentials, PreAuthenticate = true, UseCookies = false, AutomaticDecompression = DecompressionMethods.All };
            if (trustAnyCertificate)
            {
                windows.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            handler = windows;
        }

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AnyFS");
    }

    public void Dispose() => _http.Dispose();

    // --- operations -------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<EwsFolder>> FindFoldersAsync(CancellationToken ct)
    {
        var folders = new List<EwsFolder>();
        var offset = 0;
        while (true)
        {
            var message = await CallAsync("FindFolder", new XElement(M + "FindFolder", new XAttribute("Traversal", "Deep"),
                new XElement(M + "FolderShape", new XElement(T + "BaseShape", "Default"), Properties("folder:FolderClass", "folder:ParentFolderId", "folder:TotalCount", "folder:UnreadCount", "folder:ChildFolderCount", "folder:DistinguishedFolderId")),
                new XElement(M + "IndexedPageFolderView", new XAttribute("MaxEntriesReturned", 1000), new XAttribute("Offset", offset), new XAttribute("BasePoint", "Beginning")),
                new XElement(M + "ParentFolderIds", new XElement(T + "DistinguishedFolderId", new XAttribute("Id", "msgfolderroot")))), ct).ConfigureAwait(false);
            var root = message.Element(M + "RootFolder") ?? throw new EwsException("NoRootFolder", "FindFolder answered without a RootFolder");
            var list = root.Element(T + "Folders")?.Elements().ToList() ?? [];
            foreach (var f in list)
            {
                folders.Add(new EwsFolder(
                    Attr(f.Element(T + "FolderId"), "Id") ?? "",
                    Attr(f.Element(T + "FolderId"), "ChangeKey"),
                    Attr(f.Element(T + "ParentFolderId"), "Id"),
                    Text(f, "DisplayName") ?? "",
                    Text(f, "FolderClass"),
                    f.Name.LocalName,
                    Int(f, "TotalCount"),
                    Int(f, "UnreadCount"),
                    Int(f, "ChildFolderCount"),
                    Text(f, "DistinguishedFolderId")));
            }

            offset += list.Count;
            if (list.Count == 0 || string.Equals((string?)root.Attribute("IncludesLastItemInRange"), "true", StringComparison.OrdinalIgnoreCase))
            {
                return folders;
            }
        }
    }

    public async Task<EwsItemPage> FindItemsAsync(string folderId, int offset, int count, CancellationToken ct)
    {
        var message = await CallAsync("FindItem", new XElement(M + "FindItem", new XAttribute("Traversal", "Shallow"),
            new XElement(M + "ItemShape", new XElement(T + "BaseShape", "Default"), Properties(ListingProperties)),
            new XElement(M + "IndexedPageItemView", new XAttribute("MaxEntriesReturned", count), new XAttribute("Offset", offset), new XAttribute("BasePoint", "Beginning")),
            new XElement(M + "SortOrder", new XElement(T + "FieldOrder", new XAttribute("Order", "Descending"), new XElement(T + "FieldURI", new XAttribute("FieldURI", "item:DateTimeReceived")))),
            new XElement(M + "ParentFolderIds", FolderIdNode(folderId))), ct).ConfigureAwait(false);
        var root = message.Element(M + "RootFolder") ?? throw new EwsException("NoRootFolder", "FindItem answered without a RootFolder");
        var items = root.Element(T + "Items")?.Elements().Select(ItemOf).ToList() ?? [];
        return new EwsItemPage(items, int.TryParse((string?)root.Attribute("TotalItemsInView"), out var total) ? total : items.Count, !string.Equals((string?)root.Attribute("IncludesLastItemInRange"), "false", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<EwsItem>> CalendarViewAsync(string folderId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var message = await CallAsync("FindItem", new XElement(M + "FindItem", new XAttribute("Traversal", "Shallow"),
            new XElement(M + "ItemShape", new XElement(T + "BaseShape", "IdOnly"), Properties(CalendarProperties)),
            new XElement(M + "CalendarView", new XAttribute("MaxEntriesReturned", 1000), new XAttribute("StartDate", from.UtcDateTime.ToString("o")), new XAttribute("EndDate", to.UtcDateTime.ToString("o"))),
            new XElement(M + "ParentFolderIds", FolderIdNode(folderId))), ct).ConfigureAwait(false);
        var root = message.Element(M + "RootFolder") ?? throw new EwsException("NoRootFolder", "FindItem answered without a RootFolder");
        var items = root.Element(T + "Items")?.Elements().Select(ItemOf).ToList() ?? [];
        if (string.Equals((string?)root.Attribute("IncludesLastItemInRange"), "false", StringComparison.OrdinalIgnoreCase) && to - from > TimeSpan.FromDays(1))
        {
            var middle = from + (to - from) / 2;
            var first = await CalendarViewAsync(folderId, from, middle, ct).ConfigureAwait(false);
            var second = await CalendarViewAsync(folderId, middle, to, ct).ConfigureAwait(false);
            return first.Concat(second).DistinctBy(i => i.Id).ToList();
        }

        return items;
    }

    public async Task<EwsSyncPage> SyncFolderItemsAsync(string folderId, string? syncState, int maxChanges, CancellationToken ct)
    {
        var request = new XElement(M + "SyncFolderItems",
            new XElement(M + "ItemShape", new XElement(T + "BaseShape", "Default"), Properties(ListingProperties)),
            new XElement(M + "SyncFolderId", FolderIdNode(folderId)));
        if (syncState is not null)
        {
            request.Add(new XElement(M + "SyncState", syncState));
        }

        request.Add(new XElement(M + "MaxChangesReturned", Math.Clamp(maxChanges, 1, 512)));
        var message = await CallAsync("SyncFolderItems", request, ct).ConfigureAwait(false);
        var changes = new List<EwsChange>();
        foreach (var c in message.Element(M + "Changes")?.Elements() ?? [])
        {
            var kind = c.Name.LocalName;   // Create, Update, Delete, ReadFlagChange
            var item = c.Elements().FirstOrDefault(e => e.Name != T + "ItemId" && e.Name != T + "IsRead");
            if (item is not null && kind is "Create" or "Update")
            {
                var mapped = ItemOf(item);
                changes.Add(new EwsChange(kind, mapped.Id, mapped, null));
            }
            else if (c.Element(T + "ItemId") is { } id)
            {
                changes.Add(new EwsChange(kind == "ReadFlagChange" ? "ReadFlagChange" : kind, Attr(id, "Id") ?? "", null, Text(c, "IsRead") is { } read ? read.Equals("true", StringComparison.OrdinalIgnoreCase) : null));
            }
        }

        return new EwsSyncPage(Text(message, "SyncState", M) ?? syncState ?? "", changes, !string.Equals(Text(message, "IncludesLastItemInRange", M), "false", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<EwsItem?> GetItemAsync(string itemId, bool includeMime, CancellationToken ct)
    {
        XElement message;
        try
        {
            message = await CallAsync("GetItem", new XElement(M + "GetItem",
                new XElement(M + "ItemShape", new XElement(T + "BaseShape", "AllProperties"), new XElement(T + "IncludeMimeContent", includeMime ? "true" : "false"), new XElement(T + "BodyType", "HTML")),
                new XElement(M + "ItemIds", new XElement(T + "ItemId", new XAttribute("Id", itemId)))), ct).ConfigureAwait(false);
        }
        catch (EwsException ex) when (ex.IsNotFound)
        {
            return null;
        }

        var item = message.Element(M + "Items")?.Elements().FirstOrDefault();
        return item is null ? null : ItemOf(item);
    }

    public async Task<EwsAttachmentContent?> GetAttachmentAsync(string attachmentId, CancellationToken ct)
    {
        XElement message;
        try
        {
            message = await CallAsync("GetAttachment", new XElement(M + "GetAttachment",
                new XElement(M + "AttachmentShape", new XElement(T + "IncludeMimeContent", "true"), new XElement(T + "BodyType", "HTML")),
                new XElement(M + "AttachmentIds", new XElement(T + "AttachmentId", new XAttribute("Id", attachmentId)))), ct).ConfigureAwait(false);
        }
        catch (EwsException ex) when (ex.IsNotFound)
        {
            return null;
        }

        var a = message.Element(M + "Attachments")?.Elements().FirstOrDefault();
        if (a is null)
        {
            return null;
        }

        var content = Text(a, "Content") is { } base64 ? Convert.FromBase64String(base64) : null;
        var inner = a.Name == T + "ItemAttachment" ? a.Elements().FirstOrDefault(e => e.Name.Namespace == T && e.Name.LocalName is "Message" or "CalendarItem" or "Contact" or "Task" or "Item" or "MeetingRequest" or "MeetingResponse" or "MeetingCancellation" or "PostItem") : null;
        return new EwsAttachmentContent(Text(a, "Name") ?? "", Text(a, "ContentType"), content, inner is null ? null : ItemOf(inner));
    }

    /// <summary>
    /// The signed-in mailbox's own SMTP addresses, resolved from its login name (ResolveNames against Active Directory);
    /// for the address book's Autodiscover, which asks by address. Empty when the name resolves to nothing.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveAddressesAsync(string name, CancellationToken ct)
    {
        XElement message;
        try
        {
            message = await CallAsync("ResolveNames", new XElement(M + "ResolveNames", new XAttribute("ReturnFullContactData", "true"), new XAttribute("SearchScope", "ActiveDirectory"),
                new XElement(M + "UnresolvedEntry", name)), ct).ConfigureAwait(false);
        }
        catch (EwsException ex) when (ex.Code is "ErrorNameResolutionNoResults" or "ErrorNameResolutionNoMailbox")
        {
            return [];
        }

        var addresses = new List<string>();
        foreach (var mailbox in message.Descendants(T + "Mailbox"))
        {
            if (string.Equals(Text(mailbox, "RoutingType"), "SMTP", StringComparison.OrdinalIgnoreCase) && Text(mailbox, "EmailAddress") is { Length: > 0 } smtp && !addresses.Contains(smtp, StringComparer.OrdinalIgnoreCase))
            {
                addresses.Add(smtp);
            }
        }

        return addresses;
    }

    // --- the call -----------------------------------------------------------------------------------------------------------

    /// <summary>One operation: its first response message, checked. Paced; tried again on a hold (a busy server, 503) and on a dropped connection.</summary>
    private async Task<XElement> CallAsync(string operation, XElement body, CancellationToken ct)
    {
        var envelope = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(S + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soap", S), new XAttribute(XNamespace.Xmlns + "t", T), new XAttribute(XNamespace.Xmlns + "m", M),
            new XElement(S + "Header", new XElement(T + "RequestServerVersion", new XAttribute("Version", "Exchange2013_SP1")), new XElement(T + "TimeZoneContext", new XElement(T + "TimeZoneDefinition", new XAttribute("Id", "UTC")))),
            new XElement(S + "Body", body))).ToString(SaveOptions.DisableFormatting);
        for (var attempt = 1; ; attempt++)
        {
            using var slot = await _pace.EnterAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new StringContent(envelope, Encoding.UTF8, "text/xml") };
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < Attempts && ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded or HttpRequestError.Unknown)
            {
                _logger.LogInformation("outlook: {Operation} to {Host} failed ({Error}); trying again", operation, _url.Host, ex.Message);
                _pace.Hold(null);
                continue;
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new EwsException("401", $"{_url.Host} refused the Windows sign-in ({string.Join(", ", response.Headers.WwwAuthenticate.Select(w => w.Scheme))} asked for)");
                }

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    _pace.Hold(response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow));
                    if (attempt < Attempts)
                    {
                        continue;
                    }

                    throw new EwsException(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), $"{_url.Host} is busy ({(int)response.StatusCode}); try later", transient: true);
                }

                XDocument document;
                try
                {
                    document = XDocument.Parse(text);
                }
                catch (System.Xml.XmlException)
                {
                    throw new EwsException(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), $"{_url.Host} answered {operation} with something other than SOAP ({(int)response.StatusCode} {response.ReasonPhrase}; {Trim(text)})", transient: (int)response.StatusCode >= 500);
                }

                if (document.Descendants(S + "Fault").FirstOrDefault() is { } fault)
                {
                    var code = fault.Descendants(M + "ResponseCode").FirstOrDefault()?.Value ?? fault.Descendants(T + "ResponseCode").FirstOrDefault()?.Value ?? fault.Element("faultcode")?.Value ?? "Fault";
                    var reason = fault.Element("faultstring")?.Value ?? code;
                    var transient = IsTransient(code);
                    if (transient && attempt < Attempts)
                    {
                        _pace.Hold(TimeSpan.FromSeconds(10));
                        continue;
                    }

                    throw new EwsException(code, $"{operation}: {reason}", transient);
                }

                var message = document.Descendants(M + "ResponseMessages").FirstOrDefault()?.Elements().FirstOrDefault()
                    ?? throw new EwsException("NoResponse", $"{operation}: {_url.Host} answered without a response message ({Trim(text)})");
                if (string.Equals((string?)message.Attribute("ResponseClass"), "Error", StringComparison.OrdinalIgnoreCase))
                {
                    var code = Text(message, "ResponseCode", M) ?? "Error";
                    var transient = IsTransient(code);
                    if (transient && attempt < Attempts)
                    {
                        _pace.Hold(TimeSpan.FromSeconds(10));
                        continue;
                    }

                    throw new EwsException(code, $"{operation}: {Text(message, "MessageText", M) ?? code}", transient);
                }

                return message;
            }
        }
    }

    private static bool IsTransient(string code) => code is "ErrorServerBusy" or "ErrorTimeoutExpired" or "ErrorInternalServerTransientError" or "ErrorMailboxStoreUnavailable" or "ErrorTooManyObjectsOpened" or "ErrorConnectionFailed";

    private static string Trim(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    // --- request pieces ---------------------------------------------------------------------------------------------------

    private static XElement Properties(params string[] fieldUris) => new(T + "AdditionalProperties", fieldUris.Select(f => new XElement(T + "FieldURI", new XAttribute("FieldURI", f))));

    private static XElement FolderIdNode(string folderId) => folderId.All(char.IsAsciiLetterLower)
        ? new XElement(T + "DistinguishedFolderId", new XAttribute("Id", folderId))
        : new XElement(T + "FolderId", new XAttribute("Id", folderId));

    // --- the answers' shapes --------------------------------------------------------------------------------------------------

    /// <summary>An item as the XML gives it, in every field the store reads.</summary>
    internal static EwsItem ItemOf(XElement e)
    {
        var body = e.Element(T + "Body");
        var complete = e.Element(T + "CompleteName");
        return new EwsItem
        {
            Id = Attr(e.Element(T + "ItemId"), "Id") ?? "",
            ChangeKey = Attr(e.Element(T + "ItemId"), "ChangeKey"),
            ParentFolderId = Attr(e.Element(T + "ParentFolderId"), "Id"),
            Type = e.Name.LocalName,
            ItemClass = Text(e, "ItemClass"),
            Subject = Text(e, "Subject"),
            DateTimeReceived = Date(e, "DateTimeReceived"),
            DateTimeSent = Date(e, "DateTimeSent"),
            DateTimeCreated = Date(e, "DateTimeCreated"),
            LastModifiedTime = Date(e, "LastModifiedTime"),
            Size = long.TryParse(Text(e, "Size"), out var size) ? size : 0,
            HasAttachments = Bool(e, "HasAttachments"),
            IsRead = Bool(e, "IsRead"),
            IsDraft = Bool(e, "IsDraft"),
            Importance = Text(e, "Importance"),
            InternetMessageId = Text(e, "InternetMessageId"),
            InReplyTo = Text(e, "InReplyTo"),
            References = Text(e, "References"),
            DisplayTo = Text(e, "DisplayTo"),
            DisplayCc = Text(e, "DisplayCc"),
            From = Single(e, "From"),
            Sender = Single(e, "Sender"),
            ToRecipients = Mailboxes(e, "ToRecipients"),
            CcRecipients = Mailboxes(e, "CcRecipients"),
            BccRecipients = Mailboxes(e, "BccRecipients"),
            Body = body?.Value,
            BodyIsHtml = string.Equals(Attr(body, "BodyType"), "HTML", StringComparison.OrdinalIgnoreCase),
            Mime = Text(e, "MimeContent") is { } mime ? Convert.FromBase64String(mime) : null,
            Attachments = e.Element(T + "Attachments")?.Elements().Select(a => new EwsAttachmentInfo(
                Attr(a.Element(T + "AttachmentId"), "Id") ?? "",
                Text(a, "Name") ?? "",
                Text(a, "ContentType"),
                long.TryParse(Text(a, "Size"), out var n) ? n : 0,
                Bool(a, "IsInline"),
                Text(a, "ContentId"),
                a.Name == T + "ItemAttachment",
                Bool(a, "IsContactPhoto"))).ToList() ?? [],
            Start = Date(e, "Start"),
            End = Date(e, "End"),
            IsAllDayEvent = Bool(e, "IsAllDayEvent"),
            Location = Text(e, "Location"),
            Organizer = Single(e, "Organizer"),
            RequiredAttendees = Attendees(e, "RequiredAttendees"),
            OptionalAttendees = Attendees(e, "OptionalAttendees"),
            IsRecurring = Bool(e, "IsRecurring"),
            CalendarItemType = Text(e, "CalendarItemType"),
            Uid = Text(e, "UID"),
            LegacyFreeBusyStatus = Text(e, "LegacyFreeBusyStatus"),
            DisplayName = Text(e, "DisplayName"),
            FileAs = Text(e, "FileAs"),
            GivenName = Text(e, "GivenName") ?? (complete is null ? null : Text(complete, "FirstName")),
            MiddleName = Text(e, "MiddleName") ?? (complete is null ? null : Text(complete, "MiddleName")),
            Surname = Text(e, "Surname") ?? (complete is null ? null : Text(complete, "LastName")),
            Nickname = Text(e, "Nickname") ?? (complete is null ? null : Text(complete, "Nickname")),
            Generation = Text(e, "Generation") ?? (complete is null ? null : Text(complete, "Suffix")),
            Title = complete is null ? null : Text(complete, "Title"),
            CompanyName = Text(e, "CompanyName"),
            JobTitle = Text(e, "JobTitle"),
            Department = Text(e, "Department"),
            EmailAddresses = Entries(e, "EmailAddresses"),
            PhoneNumbers = Entries(e, "PhoneNumbers"),
            PhysicalAddresses = e.Element(T + "PhysicalAddresses")?.Elements(T + "Entry").Select(a => new EwsPhysicalAddress(Attr(a, "Key") ?? "", Text(a, "Street"), Text(a, "City"), Text(a, "State"), Text(a, "PostalCode"), Text(a, "CountryOrRegion"))).ToList() ?? [],
            BusinessHomePage = Text(e, "BusinessHomePage"),
            ImAddresses = Entries(e, "ImAddresses").Select(k => k.Item2).ToList(),
            Birthday = Date(e, "Birthday"),
            WeddingAnniversary = Date(e, "WeddingAnniversary"),
        };
    }

    private static string? Attr(XElement? e, string name) => e?.Attribute(name)?.Value;

    private static string? Text(XElement e, string name) => Text(e, name, T);

    private static string? Text(XElement e, string name, XNamespace ns)
    {
        var value = e.Element(ns + name)?.Value;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int Int(XElement e, string name) => int.TryParse(Text(e, name), out var n) ? n : 0;

    private static bool Bool(XElement e, string name) => string.Equals(Text(e, name), "true", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? Date(XElement e, string name)
        => Text(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    private static EwsMailbox? Single(XElement e, string name)
    {
        var mailbox = e.Element(T + name)?.Element(T + "Mailbox") ?? e.Element(T + name);
        if (mailbox is null)
        {
            return null;
        }

        var box = MailboxOf(mailbox);
        return box.Name is null && box.Address is null ? null : box;
    }

    private static EwsMailbox MailboxOf(XElement mailbox) => new(Text(mailbox, "Name"), Text(mailbox, "EmailAddress"), Text(mailbox, "RoutingType"));

    private static IReadOnlyList<EwsMailbox> Mailboxes(XElement e, string name) => e.Element(T + name)?.Elements(T + "Mailbox").Select(MailboxOf).ToList() ?? [];

    private static IReadOnlyList<EwsAttendee> Attendees(XElement e, string name)
        => e.Element(T + name)?.Elements(T + "Attendee").Select(a => new EwsAttendee(a.Element(T + "Mailbox") is { } m ? MailboxOf(m) : new EwsMailbox(null, null, null), Text(a, "ResponseType"))).ToList() ?? [];

    /// <summary>A keyed dictionary (&lt;t:EmailAddresses&gt;&lt;t:Entry Key="EmailAddress1"&gt;…) as (key, value) pairs, empty values left out.</summary>
    private static IReadOnlyList<(string, string)> Entries(XElement e, string name)
        => e.Element(T + name)?.Elements(T + "Entry").Select(k => (Attr(k, "Key") ?? "", k.Value)).Where(k => k.Item2.Length > 0).ToList() ?? [];
}
