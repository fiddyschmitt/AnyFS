using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Outlook.Ews;

/// <summary>
/// The operations the mail store runs on (<see cref="IEwsService"/>) answered by Microsoft Graph, for a mailbox on
/// Exchange Online after an app sign-in (spec 7.7.1). The mail folders come from <c>me/mailFolders</c> a level at a time,
/// with the account's own calendars and the contact folders (the default one by its well-known name) beside them; a
/// folder's changes from its delta, the next or delta link being the sync state; a calendar's occurrences from its
/// calendarView; an item in full with its class and size from their extended properties, its attachments listed
/// without their bytes, and a message's MIME (<c>$value</c>); a file attachment's content by <c>$value</c>, an attached
/// item by <c>$expand</c> (Spike 15, 25-Oct-2026, has the shapes). Every request asks for immutable ids and UTC times,
/// goes at the mailbox's pace (four at a time) and through the shared <see cref="GraphClient"/>, whose retries,
/// Retry-After and token renewal apply; Graph's refusals come out as <see cref="EwsException"/>s, as the mail store
/// reads them. Graph has a path per kind where EWS has one GetItem, so the ids are kept with what they are of
/// ("message:", "event:", "contact:"; a folder's "mail:", "calendar:", "contacts:"); an attachment's id is its item's
/// and its own, joined with '>' (no Graph id holds one), one more for each attached item it is inside.
/// </summary>
internal sealed class GraphEwsService : IEwsService, IDisposable
{
    private const string Preferences = "IdType=\"ImmutableId\", outlook.timezone=\"UTC\"";
    private const string FolderFields = "id,displayName,parentFolderId,childFolderCount,totalItemCount,unreadItemCount";
    private const string MessageFields = "id,changeKey,parentFolderId,subject,receivedDateTime,sentDateTime,createdDateTime,lastModifiedDateTime,hasAttachments,isRead,isDraft,importance,internetMessageId,from,sender,toRecipients";
    private const string EventFields = "id,subject,start,end,isAllDay,location,organizer,type,iCalUId,showAs,createdDateTime,lastModifiedDateTime,hasAttachments";
    private const string ContactFields = "id,parentFolderId,displayName,fileAs,givenName,surname,companyName,createdDateTime,lastModifiedDateTime";
    private const string AttachmentFields = "id,name,contentType,size,isInline,microsoft.graph.fileAttachment/contentId";

    // an item's message class (PidTagMessageClass) and size (PidTagMessageSize): Graph has no field for either, and
    // takes the expansion on a GET though not in a delta (Spike 15)
    private const string ClassAndSize = "$expand=singleValueExtendedProperties($filter=id eq 'String 0x001A' or id eq 'Integer 0x0E08')";

    private readonly GraphClient _graph;
    private readonly MailboxPace _pace;
    private readonly bool _hidden;
    private readonly ILogger _logger;

    /// <param name="mailbox">The mailbox's address: whose pace the requests go at.</param>
    /// <param name="showHidden">Ask for the hidden mail folders too.</param>
    public GraphEwsService(GraphClient graph, string mailbox, bool showHidden, ILogger logger)
    {
        _graph = graph;
        _pace = MailboxPace.For(mailbox);
        _hidden = showHidden;
        _logger = logger;
    }

    /// <summary>The key of a mailbox's pace in Graph (<see cref="RequestPace"/>): its cloud's Graph and its address.</summary>
    public static string PaceKey(MicrosoftCloud cloud, string mailbox) => $"{cloud.GraphHost}|{mailbox}";

    public void Dispose() => _graph.Dispose();

    // --- folders ----------------------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<EwsFolder>> FindFoldersAsync(CancellationToken ct) => CallAsync<IReadOnlyList<EwsFolder>>(async () =>
    {
        var folders = new List<EwsFolder>();
        var hidden = _hidden ? "&includeHiddenFolders=true" : "";
        var inbox = await InboxIdAsync(ct).ConfigureAwait(false);
        var level = await ListAsync($"me/mailFolders?$top=250&$select={FolderFields}{hidden}", "the mail folders", ct).ConfigureAwait(false);
        while (level.Count > 0)
        {
            var below = new List<JsonElement>();
            foreach (var f in level)
            {
                var id = Str(f, "id") ?? "";
                folders.Add(new EwsFolder("mail:" + id, null, Prefixed("mail:", Str(f, "parentFolderId")), Str(f, "displayName") ?? "", "IPF.Note", "Folder",
                    Int(f, "totalItemCount"), Int(f, "unreadItemCount"), Int(f, "childFolderCount"), id == inbox ? "inbox" : null));
                if (Int(f, "childFolderCount") > 0)
                {
                    below.AddRange(await ListAsync($"me/mailFolders/{Escape(id)}/childFolders?$top=250&$select={FolderFields}{hidden}", Str(f, "displayName") ?? "a folder", ct).ConfigureAwait(false));
                }
            }

            level = below;
        }

        // the account's own calendars: the default one's owner is the account; another person's, shared, is out (spec 7.7)
        var calendars = await ListAsync("me/calendars?$top=250&$select=id,name,owner,isDefaultCalendar", "the calendars", ct).ConfigureAwait(false);
        var own = calendars.Where(c => Bool(c, "isDefaultCalendar")).Select(Owner).FirstOrDefault();
        foreach (var c in calendars.Where(c => Bool(c, "isDefaultCalendar") || own is null || string.Equals(Owner(c), own, StringComparison.OrdinalIgnoreCase)))
        {
            folders.Add(new EwsFolder("calendar:" + Str(c, "id"), null, null, Str(c, "name") ?? "Calendar", "IPF.Appointment", "CalendarFolder", 0, 0, 0, Bool(c, "isDefaultCalendar") ? "calendar" : null));
        }

        folders.AddRange(await ContactFoldersAsync(ct).ConfigureAwait(false));
        return folders;
    });

    /// <summary>The default Contacts folder by its well-known name, then the folders below it, a level at a time.</summary>
    private async Task<List<EwsFolder>> ContactFoldersAsync(CancellationToken ct)
    {
        var folders = new List<EwsFolder>();
        using var contacts = await TryGetAsync("me/contactFolders/contacts?$select=id,displayName", "the contacts", ct).ConfigureAwait(false);
        if (contacts?.RootElement is not { } root || Str(root, "id") is not { } contactsId)
        {
            return folders;
        }

        folders.Add(new EwsFolder("contacts:" + contactsId, null, null, Str(root, "displayName") ?? "Contacts", "IPF.Contact", "ContactsFolder", 0, 0, 0, "contacts"));
        var parents = new List<string> { contactsId };
        for (var depth = 0; parents.Count > 0 && depth < 8; depth++)
        {
            var next = new List<string>();
            foreach (var parent in parents)
            {
                // the default folder's children are me/contactFolders itself
                var url = parent == contactsId ? "me/contactFolders?$top=250&$select=id,displayName,parentFolderId" : $"me/contactFolders/{Escape(parent)}/childFolders?$top=250&$select=id,displayName,parentFolderId";
                foreach (var f in await ListAsync(url, "the contact folders", ct).ConfigureAwait(false))
                {
                    var id = Str(f, "id") ?? "";
                    folders.Add(new EwsFolder("contacts:" + id, null, "contacts:" + parent, Str(f, "displayName") ?? "", "IPF.Contact", "ContactsFolder", 0, 0, 0, null));
                    next.Add(id);
                }
            }

            parents = next;
        }

        return folders;
    }

    /// <summary>The Inbox's id, by its well-known name; a mailbox Graph cannot reach is said as such.</summary>
    private async Task<string?> InboxIdAsync(CancellationToken ct)
    {
        using var response = await SendAsync("me/mailFolders/inbox?$select=id", null, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (GraphClient.ErrorCode(body) is "MailboxNotEnabledForRESTAPI" or "MailboxNotSupportedForRESTAPI")
        {
            throw new EwsException("ErrorMailboxNotReachable", "Microsoft Graph cannot reach this mailbox: it is inactive, or it is on an Exchange server of the organisation's own, whose address (with a Windows account) reads it instead");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw GraphClient.MapError(response.StatusCode, body, "the Inbox", _graph.Service);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return Str(doc.RootElement, "id");
        }
        catch (JsonException)
        {
            // a proxy's page, a captive portal's: something answering in Graph's place
            throw VfsException.Unavailable($"'the Inbox': Graph answered {(int)response.StatusCode} with something other than JSON");
        }
    }

    private static string? Owner(JsonElement calendar) => calendar.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object ? Str(owner, "address") : null;

    // --- items ------------------------------------------------------------------------------------------------------------

    public Task<EwsItemPage> FindItemsAsync(string folderId, int offset, int count, CancellationToken ct) => CallAsync(async () =>
    {
        var (_, id) = Split(folderId);
        using var doc = await GetAsync($"me/mailFolders/{Escape(id)}/messages?$top={count}&$skip={offset}&$count=true&$orderby=receivedDateTime desc&$select={MessageFields}", "a folder's messages", null, ct).ConfigureAwait(false);
        var items = doc.RootElement.GetProperty("value").EnumerateArray().Select(m => MessageOf(m)).ToList();
        var total = doc.RootElement.TryGetProperty("@odata.count", out var c) && c.TryGetInt32(out var n) ? n : offset + items.Count;
        return new EwsItemPage(items, total, !doc.RootElement.TryGetProperty("@odata.nextLink", out _));
    });

    public Task<IReadOnlyList<EwsItem>> CalendarViewAsync(string folderId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) => CallAsync<IReadOnlyList<EwsItem>>(async () =>
    {
        var (_, id) = Split(folderId);
        var url = $"me/calendars/{Escape(id)}/calendarView?startDateTime={Iso(from)}&endDateTime={Iso(to)}&$top=500&$select={EventFields}";
        return (await ListAsync(url, "a calendar", ct).ConfigureAwait(false)).Select(e => EventOf(e)).ToList();
    });

    public Task<EwsSyncPage> SyncFolderItemsAsync(string folderId, string? syncState, int maxChanges, CancellationToken ct) => CallAsync(async () =>
    {
        var (kind, id) = Split(folderId);
        var url = syncState ?? kind switch
        {
            "mail" => $"me/mailFolders/{Escape(id)}/messages/delta?$select={MessageFields}",
            "contacts" => $"me/contactFolders/{Escape(id)}/contacts/delta?$select={ContactFields}",
            _ => throw new EwsException("ErrorInvalidOperation", $"{folderId} is no folder of messages or contacts"),
        };
        using var response = await SendAsync(url, Math.Clamp(maxChanges, 1, 1000), ct).ConfigureAwait(false);
        if (syncState is not null && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // a link Graph no longer takes (spoilt or expired: Spike 15 saw a 400) ends the sync state; the store reads the folder again
            throw new EwsException("ErrorInvalidSyncStateData", $"Graph no longer takes the folder's delta link ({(int)response.StatusCode} {GraphClient.ErrorCode(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))})".TrimEnd());
        }

        using var doc = await _graph.ReadJsonAsync(response, "a folder's changes", ct).ConfigureAwait(false);
        var changes = new List<EwsChange>();
        foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var itemId = (kind == "mail" ? "message:" : "contact:") + Str(e, "id");
            if (e.TryGetProperty("@removed", out _))
            {
                // deleted, or moved away: either way no longer in this folder
                changes.Add(new EwsChange("Delete", itemId, null, null));
                continue;
            }

            var item = kind == "mail" ? MessageOf(e) : ContactOf(e);
            changes.Add(new EwsChange("Update", item.Id, item, null));
        }

        var next = Str(doc.RootElement, "@odata.nextLink");
        return new EwsSyncPage(next ?? Str(doc.RootElement, "@odata.deltaLink") ?? syncState ?? "", changes, IncludesLastItemInRange: next is null);
    });

    public Task<EwsItem?> GetItemAsync(string itemId, bool includeMime, CancellationToken ct) => CallAsync(async () =>
    {
        var (kind, id) = Split(itemId);
        var path = PathOf(kind, id);
        using var doc = await TryGetAsync($"{path}?{ClassAndSize}", "an item", ct).ConfigureAwait(false);
        if (doc is null)
        {
            return null;
        }

        var attachments = kind == "contact" ? [] : await AttachmentsAsync(path, itemId, ct).ConfigureAwait(false);
        var mime = includeMime && kind == "message" ? await BytesAsync($"{path}/$value", "a message's MIME", ct).ConfigureAwait(false) : null;
        return kind switch
        {
            "message" => MessageOf(doc.RootElement, attachments, mime, itemId),
            "event" => EventOf(doc.RootElement, attachments, itemId),
            _ => ContactOf(doc.RootElement, itemId),
        };
    });

    /// <summary>An item's attachments as its folder lists them: no bytes, the content id a file has; a link to a file elsewhere (a reference attachment) has no content here and is left out.</summary>
    private async Task<IReadOnlyList<EwsAttachmentInfo>> AttachmentsAsync(string path, string itemId, CancellationToken ct)
    {
        var listed = await ListAsync($"{path}/attachments?$select={AttachmentFields}", "an item's attachments", ct).ConfigureAwait(false);
        var links = listed.Count(a => Str(a, "@odata.type") == "#microsoft.graph.referenceAttachment");
        if (links > 0)
        {
            _logger.LogDebug("outlook: {Count} of an item's attachments are links to files elsewhere, left out", links);
        }

        return listed.Where(a => Str(a, "@odata.type") != "#microsoft.graph.referenceAttachment").Select(a => AttachmentOf(a, itemId)).ToList();
    }

    private static EwsAttachmentInfo AttachmentOf(JsonElement a, string parentId)
        => new($"{parentId}>{Str(a, "id")}", Str(a, "name") ?? "", Str(a, "contentType"), Long(a, "size"), Bool(a, "isInline"), Str(a, "contentId"), Str(a, "@odata.type") == "#microsoft.graph.itemAttachment", IsContactPhoto: false);

    public Task<EwsAttachmentContent?> GetAttachmentAsync(string attachmentId, CancellationToken ct) => CallAsync(async () =>
    {
        // the item's path, the attachment on it, then one inside an attached item for each further step
        var steps = attachmentId.Split('>');
        var (kind, id) = Split(steps[0]);
        var path = $"{PathOf(kind, id)}/attachments/{Escape(steps[1])}";
        const string Expand = "?$expand=microsoft.graph.itemattachment/item";
        using var first = await TryGetAsync(steps.Length > 2 ? path + Expand : path, "an attachment", ct).ConfigureAwait(false);
        if (first is null)
        {
            return null;
        }

        // an attached item comes with its properties (and theirs, attachments and all) only when expanded
        using var expanded = steps.Length == 2 && IsItem(first.RootElement) ? await TryGetAsync(path + Expand, "an attached item", ct).ConfigureAwait(false) : null;
        var attachment = (expanded ?? first).RootElement;
        for (var i = 2; i < steps.Length; i++)
        {
            if (!attachment.TryGetProperty("item", out var inner) || !inner.TryGetProperty("attachments", out var list) || list.ValueKind != JsonValueKind.Array
                || list.EnumerateArray().FirstOrDefault(a => Str(a, "id") == steps[i]) is not { ValueKind: JsonValueKind.Object } found)
            {
                return null;
            }

            attachment = found;
        }

        var name = Str(attachment, "name") ?? "";
        var contentType = Str(attachment, "contentType");
        if (IsItem(attachment))
        {
            if (!attachment.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            {
                return new EwsAttachmentContent(name, contentType, null, null);
            }

            // an attached message's MIME by $value; one inside another attached item has no path of its own, and is made from its properties
            var mime = steps.Length == 2 && Str(item, "@odata.type") is null or "#microsoft.graph.message" ? await BytesAsync($"{path}/$value", "an attached message's MIME", ct).ConfigureAwait(false) : null;
            var inside = item.TryGetProperty("attachments", out var attached) && attached.ValueKind == JsonValueKind.Array
                ? attached.EnumerateArray().Where(a => Str(a, "@odata.type") != "#microsoft.graph.referenceAttachment").Select(a => AttachmentOf(a, attachmentId)).ToList()
                : [];
            var embedded = Str(item, "@odata.type") switch
            {
                "#microsoft.graph.event" => EventOf(item, inside, attachmentId),
                "#microsoft.graph.contact" => ContactOf(item, attachmentId),
                _ => MessageOf(item, inside, mime, attachmentId),
            };
            return new EwsAttachmentContent(name, contentType, null, embedded);
        }

        // a file's bytes come with it; by $value should they not
        var content = Str(attachment, "contentBytes") is { } base64 ? Convert.FromBase64String(base64) : steps.Length == 2 ? await BytesAsync($"{path}/$value", "an attachment", ct).ConfigureAwait(false) : [];
        return new EwsAttachmentContent(name, contentType, content, null);
    });

    private static bool IsItem(JsonElement attachment) => Str(attachment, "@odata.type") == "#microsoft.graph.itemAttachment";

    // --- mapping ----------------------------------------------------------------------------------------------------------

    /// <summary>A message as EWS would describe it: a meeting's request, response or cancellation by Graph's type for it.</summary>
    private static EwsItem MessageOf(JsonElement m, IReadOnlyList<EwsAttachmentInfo>? attachments = null, byte[]? mime = null, string? id = null)
    {
        var type = Str(m, "@odata.type") is { } odata && odata.StartsWith("#microsoft.graph.eventMessage", StringComparison.Ordinal)
            ? Str(m, "meetingMessageType") switch
            {
                "meetingCancelled" => "MeetingCancellation",
                "meetingAccepted" or "meetingTenativelyAccepted" or "meetingTentativelyAccepted" or "meetingDeclined" => "MeetingResponse",
                _ => "MeetingRequest",
            }
            : "Message";
        var to = Recipients(m, "toRecipients");
        var cc = Recipients(m, "ccRecipients");
        var (html, body) = Body(m, "body");
        return new EwsItem
        {
            Id = id ?? "message:" + Str(m, "id"),
            ChangeKey = Str(m, "changeKey"),
            ParentFolderId = Prefixed("mail:", Str(m, "parentFolderId")),
            Type = type,
            ItemClass = ExtendedString(m, 0x001A),
            Subject = Str(m, "subject"),
            DateTimeReceived = Time(m, "receivedDateTime"),
            DateTimeSent = Time(m, "sentDateTime"),
            DateTimeCreated = Time(m, "createdDateTime"),
            LastModifiedTime = Time(m, "lastModifiedDateTime"),
            Size = ExtendedLong(m, 0x0E08) ?? mime?.Length ?? 0,
            HasAttachments = Bool(m, "hasAttachments"),
            IsRead = Bool(m, "isRead"),
            IsDraft = Bool(m, "isDraft"),
            Importance = Str(m, "importance") switch { "low" => "Low", "high" => "High", "normal" => "Normal", _ => null },
            InternetMessageId = Str(m, "internetMessageId"),
            InReplyTo = mime is null ? null : MimeHeader(mime, "In-Reply-To"),
            References = mime is null ? null : MimeHeader(mime, "References"),
            DisplayTo = to.Count == 0 ? null : string.Join("; ", to.Select(r => r.Name ?? r.Address)),
            DisplayCc = cc.Count == 0 ? null : string.Join("; ", cc.Select(r => r.Name ?? r.Address)),
            From = Mailbox(m, "from"),
            Sender = Mailbox(m, "sender"),
            ToRecipients = to,
            CcRecipients = cc,
            BccRecipients = Recipients(m, "bccRecipients"),
            Body = body,
            BodyIsHtml = html,
            Mime = mime,
            Attachments = attachments ?? [],

            // a meeting request's own times, as Graph gives them on one
            Start = Zoned(m, "startDateTime"),
            End = Zoned(m, "endDateTime"),
            IsAllDayEvent = Bool(m, "isAllDay"),
            Location = m.TryGetProperty("location", out var where) && where.ValueKind == JsonValueKind.Object ? Str(where, "displayName") : null,
        };
    }

    private static EwsItem EventOf(JsonElement e, IReadOnlyList<EwsAttachmentInfo>? attachments = null, string? id = null)
    {
        var (html, body) = Body(e, "body");
        var type = Str(e, "type");
        return new EwsItem
        {
            Id = id ?? "event:" + Str(e, "id"),
            Type = "CalendarItem",
            ItemClass = ExtendedString(e, 0x001A),
            Subject = Str(e, "subject"),
            DateTimeCreated = Time(e, "createdDateTime"),
            LastModifiedTime = Time(e, "lastModifiedDateTime"),
            Size = ExtendedLong(e, 0x0E08) ?? 0,
            HasAttachments = Bool(e, "hasAttachments"),
            Start = Zoned(e, "start"),
            End = Zoned(e, "end"),
            IsAllDayEvent = Bool(e, "isAllDay"),
            Location = e.TryGetProperty("location", out var where) && where.ValueKind == JsonValueKind.Object ? Str(where, "displayName") : null,
            Organizer = Mailbox(e, "organizer"),
            RequiredAttendees = Attendees(e, "required"),
            OptionalAttendees = Attendees(e, "optional"),
            IsRecurring = type is "occurrence" or "exception" or "seriesMaster",
            CalendarItemType = type switch { "occurrence" => "Occurrence", "exception" => "Exception", "seriesMaster" => "RecurringMaster", _ => "Single" },
            Uid = Str(e, "iCalUId"),
            LegacyFreeBusyStatus = Str(e, "showAs") switch { "free" => "Free", "tentative" => "Tentative", "busy" => "Busy", "oof" => "OOF", "workingElsewhere" => "WorkingElsewhere", _ => null },
            Body = body,
            BodyIsHtml = html,
            Attachments = attachments ?? [],
        };
    }

    private static EwsItem ContactOf(JsonElement c, string? id = null)
    {
        var phones = new List<(string Key, string Number)>();
        foreach (var (property, key) in new[] { ("businessPhones", "BusinessPhone"), ("homePhones", "HomePhone") })
        {
            var n = 0;
            foreach (var number in Strings(c, property))
            {
                phones.Add((n++ == 0 ? key : key + "2", number));
            }
        }

        if (Str(c, "mobilePhone") is { Length: > 0 } mobile)
        {
            phones.Add(("MobilePhone", mobile));
        }

        var addresses = new List<EwsPhysicalAddress>();
        foreach (var (property, key) in new[] { ("businessAddress", "Business"), ("homeAddress", "Home"), ("otherAddress", "Other") })
        {
            if (c.TryGetProperty(property, out var a) && a.ValueKind == JsonValueKind.Object && a.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Length > 0))
            {
                addresses.Add(new EwsPhysicalAddress(key, Str(a, "street"), Str(a, "city"), Str(a, "state"), Str(a, "postalCode"), Str(a, "countryOrRegion")));
            }
        }

        var emails = c.TryGetProperty("emailAddresses", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select((e, i) => ($"EmailAddress{i + 1}", Str(e, "address") ?? "")).Where(e => e.Item2.Length > 0).ToList()
            : [];
        var (html, body) = Body(c, "personalNotes");
        return new EwsItem
        {
            Id = id ?? "contact:" + Str(c, "id"),
            ParentFolderId = Prefixed("contacts:", Str(c, "parentFolderId")),
            Type = "Contact",
            ItemClass = ExtendedString(c, 0x001A),
            DateTimeCreated = Time(c, "createdDateTime"),
            LastModifiedTime = Time(c, "lastModifiedDateTime"),
            Size = ExtendedLong(c, 0x0E08) ?? 0,
            DisplayName = Str(c, "displayName"),
            FileAs = Str(c, "fileAs"),
            GivenName = Str(c, "givenName"),
            MiddleName = Str(c, "middleName"),
            Surname = Str(c, "surname"),
            Nickname = Str(c, "nickName"),
            Generation = Str(c, "generation"),
            Title = Str(c, "title"),
            CompanyName = Str(c, "companyName"),
            JobTitle = Str(c, "jobTitle"),
            Department = Str(c, "department"),
            EmailAddresses = emails,
            PhoneNumbers = phones,
            PhysicalAddresses = addresses,
            BusinessHomePage = Str(c, "businessHomePage"),
            ImAddresses = Strings(c, "imAddresses").ToList(),
            Birthday = Time(c, "birthday"),
            Body = body,
            BodyIsHtml = html,
        };
    }

    private static EwsMailbox? Mailbox(JsonElement e, string property)
        => e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("emailAddress", out var a) && a.ValueKind == JsonValueKind.Object
            ? new EwsMailbox(Str(a, "name"), Str(a, "address"), "SMTP")
            : null;

    private static List<EwsMailbox> Recipients(JsonElement e, string property)
        => e.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(r => r.TryGetProperty("emailAddress", out var a) && a.ValueKind == JsonValueKind.Object ? new EwsMailbox(Str(a, "name"), Str(a, "address"), "SMTP") : null).OfType<EwsMailbox>().ToList()
            : [];

    private static List<EwsAttendee> Attendees(JsonElement e, string type)
        => e.TryGetProperty("attendees", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(a => Str(a, "type") == type && a.TryGetProperty("emailAddress", out var m) && m.ValueKind == JsonValueKind.Object).Select(a =>
            {
                var who = a.GetProperty("emailAddress");
                var response = a.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object ? Str(status, "response") : null;
                return new EwsAttendee(new EwsMailbox(Str(who, "name"), Str(who, "address"), "SMTP"), response switch
                {
                    "accepted" => "Accept",
                    "declined" => "Decline",
                    "tentativelyAccepted" => "Tentative",
                    "organizer" => "Organizer",
                    "notResponded" or "none" => "NoResponseReceived",
                    _ => null,
                });
            }).ToList()
            : [];

    /// <summary>A body: whether it is HTML, and its text; a contact's notes are a string of their own.</summary>
    private static (bool Html, string? Text) Body(JsonElement e, string property)
    {
        if (!e.TryGetProperty(property, out var body))
        {
            return (false, null);
        }

        return body.ValueKind switch
        {
            JsonValueKind.Object => (string.Equals(Str(body, "contentType"), "html", StringComparison.OrdinalIgnoreCase), Str(body, "content")),
            JsonValueKind.String => (false, body.GetString()),
            _ => (false, null),
        };
    }

    /// <summary>An extended property's value by its tag (0x001A, the message class), as $expand gave it: Graph names it "String 0x1a".</summary>
    private static string? ExtendedString(JsonElement e, int tag) => Extended(e, "String", tag);

    private static long? ExtendedLong(JsonElement e, int tag) => long.TryParse(Extended(e, "Integer", tag), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string? Extended(JsonElement e, string type, int tag)
    {
        if (!e.TryGetProperty("singleValueExtendedProperties", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var p in list.EnumerateArray())
        {
            var parts = (Str(p, "id") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Equals(type, StringComparison.OrdinalIgnoreCase) && parts[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(parts[1].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var found) && found == tag)
            {
                return Str(p, "value");
            }
        }

        return null;
    }

    /// <summary>A dateTimeTimeZone ({"dateTime":"2026-10-27T04:00:00.0000000","timeZone":"UTC"}): UTC, as every request asks; another zone by its name.</summary>
    private static DateTimeOffset? Zoned(JsonElement e, string property)
    {
        if (!e.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object || Str(value, "dateTime") is not { } text
            || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        var zone = Str(value, "timeZone");
        if (zone is null or "UTC" or "Etc/UTC" or "tzone://Microsoft/Utc")
        {
            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        try
        {
            var info = TimeZoneInfo.FindSystemTimeZoneById(zone);
            return new DateTimeOffset(local, info.GetUtcOffset(local));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.Zero);
        }
    }

    private static DateTimeOffset? Time(JsonElement e, string property)
        => e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string? Str(JsonElement e, string property)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        } : null;

    private static IEnumerable<string> Strings(JsonElement e, string property)
        => e.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).Where(s => s.Length > 0).ToList()
            : [];

    private static int Int(JsonElement e, string property) => e.TryGetProperty(property, out var value) && value.TryGetInt32(out var n) ? n : 0;

    private static long Long(JsonElement e, string property) => e.TryGetProperty(property, out var value) && value.TryGetInt64(out var n) ? n : 0;

    private static bool Bool(JsonElement e, string property) => e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Prefixed(string prefix, string? id) => id is null ? null : prefix + id;

    /// <summary>An id of this service's as what it is of and Graph's id: "message:AAMk…" is ("message", "AAMk…").</summary>
    private static (string Kind, string Id) Split(string id)
    {
        var colon = id.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? ("", id) : (id[..colon], id[(colon + 1)..]);
    }

    private static string PathOf(string kind, string id) => kind switch
    {
        "message" => "me/messages/",
        "event" => "me/events/",
        "contact" => "me/contacts/",
        _ => throw new EwsException("ErrorInvalidIdMalformed", $"{kind}:{id} is no item's id of this mailbox's"),
    } + Escape(id);

    private static string Escape(string id) => Uri.EscapeDataString(id);

    /// <summary>A top-level header of a MIME message, unfolded: In-Reply-To and References, which Graph has no field for.</summary>
    internal static string? MimeHeader(byte[] mime, string name)
    {
        var end = mime.AsSpan().IndexOf("\r\n\r\n"u8);
        var head = Encoding.Latin1.GetString(mime, 0, end < 0 ? Math.Min(mime.Length, 64 * 1024) : end);
        string? found = null;
        foreach (var line in head.Replace("\r\n\t", " ", StringComparison.Ordinal).Replace("\r\n ", " ", StringComparison.Ordinal).Split("\r\n"))
        {
            if (line.Length > name.Length && line[name.Length] == ':' && line.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                found = line[(name.Length + 1)..].Trim();
                break;
            }
        }

        return string.IsNullOrEmpty(found) ? null : found;
    }

    // --- the calls --------------------------------------------------------------------------------------------------------

    /// <summary>One operation, with Graph's refusals as the mail store reads them.</summary>
    private static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            throw ex.Error switch
            {
                VfsError.NotFound => new EwsException("ErrorItemNotFound", ex.Message, inner: ex),
                VfsError.AccessDenied => new EwsException("ErrorAccessDenied", ex.Message, inner: ex),
                VfsError.Unavailable => new EwsException("ErrorServerBusy", ex.Message, transient: true, inner: ex),
                _ => new EwsException("ErrorInvalidRequest", ex.Message, inner: ex),
            };
        }
    }

    /// <summary>A GET at the mailbox's pace, asking for immutable ids and UTC (and a page size, for a delta); Graph's client retries and renews the token.</summary>
    private async Task<HttpResponseMessage> SendAsync(string url, int? pageSize, CancellationToken ct)
    {
        using var slot = await _pace.EnterAsync(ct).ConfigureAwait(false);
        return await _graph.SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, _graph.Absolute(url));
            request.Headers.TryAddWithoutValidation("Prefer", pageSize is { } size ? $"odata.maxpagesize={size}, {Preferences}" : Preferences);
            return request;
        }, HttpCompletionOption.ResponseContentRead, authenticated: true, idempotent: true, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> GetAsync(string url, string what, int? pageSize, CancellationToken ct)
    {
        using var response = await SendAsync(url, pageSize, ct).ConfigureAwait(false);
        return await _graph.ReadJsonAsync(response, what, ct).ConfigureAwait(false);
    }

    /// <summary>Like <see cref="GetAsync"/>, but null for a 404: an item or attachment that is gone.</summary>
    private async Task<JsonDocument?> TryGetAsync(string url, string what, CancellationToken ct)
    {
        using var response = await SendAsync(url, null, ct).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await _graph.ReadJsonAsync(response, what, ct).ConfigureAwait(false);
    }

    /// <summary>Every element of a collection, its next links followed.</summary>
    private async Task<List<JsonElement>> ListAsync(string url, string what, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        for (string? next = url; next is not null;)
        {
            using var doc = await GetAsync(next, what, null, ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                all.AddRange(value.EnumerateArray().Select(e => e.Clone()));
            }

            next = Str(doc.RootElement, "@odata.nextLink");
        }

        return all;
    }

    /// <summary>Raw content ($value: a message's MIME, an attachment's bytes).</summary>
    private async Task<byte[]> BytesAsync(string url, string what, CancellationToken ct)
    {
        using var response = await SendAsync(url, null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw GraphClient.MapError(response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), what, _graph.Service);
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }
}
