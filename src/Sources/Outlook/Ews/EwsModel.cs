namespace AnyFS.Sources.Outlook.Ews;

/// <summary>A folder as Exchange Web Services describes it.</summary>
/// <param name="Type">Folder, CalendarFolder, ContactsFolder, TasksFolder, SearchFolder.</param>
/// <param name="DistinguishedId">"inbox", "sentitems"... for the well-known ones; null for the rest.</param>
internal sealed record EwsFolder(string Id, string? ChangeKey, string? ParentId, string Name, string? FolderClass, string Type, int TotalCount, int UnreadCount, int ChildFolderCount, string? DistinguishedId);

/// <summary>A mailbox as EWS names one: a name and an address, with its routing type ("SMTP", "EX").</summary>
internal sealed record EwsMailbox(string? Name, string? Address, string? RoutingType)
{
    /// <summary>The SMTP address, or null for an Exchange legacy DN.</summary>
    public string? Smtp => Address is null || string.Equals(RoutingType, "EX", StringComparison.OrdinalIgnoreCase) || Address.StartsWith("/o=", StringComparison.OrdinalIgnoreCase) ? null : Address;
}

/// <summary>An attendee: who, and how they answered (Unknown, Organizer, Tentative, Accept, Decline, NoResponseReceived).</summary>
internal sealed record EwsAttendee(EwsMailbox Mailbox, string? ResponseType);

/// <summary>An attachment as an item lists it: a file (its content fetched by its id) or an attached item.</summary>
internal sealed record EwsAttachmentInfo(string Id, string Name, string? ContentType, long Size, bool IsInline, string? ContentId, bool IsItem, bool IsContactPhoto);

/// <summary>An attachment fetched: a file's bytes, or an attached item with its MIME and properties.</summary>
internal sealed record EwsAttachmentContent(string Name, string? ContentType, byte[]? Content, EwsItem? Item);

/// <summary>A postal address of a contact.</summary>
internal sealed record EwsPhysicalAddress(string Key, string? Street, string? City, string? State, string? PostalCode, string? Country);

/// <summary>
/// An item as EWS describes it, in the fields the store reads: the common ones, a message's, a calendar item's, a
/// contact's. Fields the item lacks are null. The JSON (Outlook on the web) and SOAP (Exchange Server) services both
/// produce it, so the store is written once.
/// </summary>
internal sealed class EwsItem
{
    public required string Id { get; init; }

    public string? ChangeKey { get; init; }

    public string? ParentFolderId { get; init; }

    /// <summary>Message, CalendarItem, Contact, Task, DistributionList, MeetingRequest, Item...</summary>
    public required string Type { get; init; }

    public string? ItemClass { get; init; }

    public string? Subject { get; init; }

    public DateTimeOffset? DateTimeReceived { get; init; }

    public DateTimeOffset? DateTimeSent { get; init; }

    public DateTimeOffset? DateTimeCreated { get; init; }

    public DateTimeOffset? LastModifiedTime { get; init; }

    public long Size { get; init; }

    public bool HasAttachments { get; init; }

    public bool IsRead { get; init; }

    public bool IsDraft { get; init; }

    /// <summary>Low, Normal, High.</summary>
    public string? Importance { get; init; }

    public string? InternetMessageId { get; init; }

    public string? InReplyTo { get; init; }

    public string? References { get; init; }

    public string? DisplayTo { get; init; }

    public string? DisplayCc { get; init; }

    public EwsMailbox? From { get; init; }

    public EwsMailbox? Sender { get; init; }

    public IReadOnlyList<EwsMailbox> ToRecipients { get; init; } = [];

    public IReadOnlyList<EwsMailbox> CcRecipients { get; init; } = [];

    public IReadOnlyList<EwsMailbox> BccRecipients { get; init; } = [];

    /// <summary>The body as HTML or text, with which it is.</summary>
    public string? Body { get; init; }

    public bool BodyIsHtml { get; init; }

    public byte[]? Mime { get; init; }

    public IReadOnlyList<EwsAttachmentInfo> Attachments { get; init; } = [];

    // calendar
    public DateTimeOffset? Start { get; init; }

    public DateTimeOffset? End { get; init; }

    public bool IsAllDayEvent { get; init; }

    public string? Location { get; init; }

    public EwsMailbox? Organizer { get; init; }

    public IReadOnlyList<EwsAttendee> RequiredAttendees { get; init; } = [];

    public IReadOnlyList<EwsAttendee> OptionalAttendees { get; init; } = [];

    public bool IsRecurring { get; init; }

    /// <summary>Single, Occurrence, Exception, RecurringMaster.</summary>
    public string? CalendarItemType { get; init; }

    public string? Uid { get; init; }

    /// <summary>Free, Tentative, Busy, OOF, WorkingElsewhere, NoData.</summary>
    public string? LegacyFreeBusyStatus { get; init; }

    // contact
    public string? DisplayName { get; init; }

    public string? FileAs { get; init; }

    public string? GivenName { get; init; }

    public string? MiddleName { get; init; }

    public string? Surname { get; init; }

    public string? Nickname { get; init; }

    public string? Generation { get; init; }

    public string? Title { get; init; }

    public string? CompanyName { get; init; }

    public string? JobTitle { get; init; }

    public string? Department { get; init; }

    public IReadOnlyList<(string Key, string Address)> EmailAddresses { get; init; } = [];

    public IReadOnlyList<(string Key, string Number)> PhoneNumbers { get; init; } = [];

    public IReadOnlyList<EwsPhysicalAddress> PhysicalAddresses { get; init; } = [];

    public string? BusinessHomePage { get; init; }

    public IReadOnlyList<string> ImAddresses { get; init; } = [];

    public DateTimeOffset? Birthday { get; init; }

    public DateTimeOffset? WeddingAnniversary { get; init; }
}

/// <summary>One change SyncFolderItems reports.</summary>
/// <param name="Kind">Create, Update, Delete, ReadFlagChange.</param>
/// <param name="Item">The item, for a create or update; null for the rest.</param>
internal sealed record EwsChange(string Kind, string ItemId, EwsItem? Item, bool? IsRead);

/// <summary>What one SyncFolderItems call gave: the state to continue from, the changes, whether that was the last page.</summary>
internal sealed record EwsSyncPage(string SyncState, IReadOnlyList<EwsChange> Changes, bool IncludesLastItemInRange);

/// <summary>A page of a folder's items and how many the folder has in all.</summary>
internal sealed record EwsItemPage(IReadOnlyList<EwsItem> Items, int TotalItemsInView, bool IncludesLastItemInRange);

/// <summary>Exchange answered a call with an error: its response code and message, and whether trying again may help.</summary>
internal sealed class EwsException(string code, string message, bool transient = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>EWS's ResponseCode ("ErrorItemNotFound"), or an HTTP status for an answer that was no EWS answer.</summary>
    public string Code { get; } = code;

    /// <summary>Whether the server said to try again later (busy, throttled, a timeout).</summary>
    public bool Transient { get; } = transient;

    public bool IsNotFound => Code is "ErrorItemNotFound" or "ErrorFolderNotFound" or "ErrorAttachmentNotFound";
}

/// <summary>
/// The Exchange Web Services operations the mail store uses, in their results' shapes ([MS-OXWSCORE] and friends):
/// Outlook on the web's service.svc answers them as JSON, Exchange Server's /EWS/Exchange.asmx as SOAP. Each call is
/// one request; the implementation paces, signs and retries.
/// </summary>
internal interface IEwsService
{
    /// <summary>Every folder under the message root, deep.</summary>
    Task<IReadOnlyList<EwsFolder>> FindFoldersAsync(CancellationToken ct);

    /// <summary>A page of a folder's items, newest first, with the listing properties.</summary>
    Task<EwsItemPage> FindItemsAsync(string folderId, int offset, int count, CancellationToken ct);

    /// <summary>A calendar folder's appointments between two instants, recurring ones expanded to their occurrences (at most two years).</summary>
    Task<IReadOnlyList<EwsItem>> CalendarViewAsync(string folderId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>The changes in a folder since <paramref name="syncState"/> (all of its items for null), with the listing properties.</summary>
    Task<EwsSyncPage> SyncFolderItemsAsync(string folderId, string? syncState, int maxChanges, CancellationToken ct);

    /// <summary>An item in full: every property, the body as HTML, and its MIME when asked; null when it is gone.</summary>
    Task<EwsItem?> GetItemAsync(string itemId, bool includeMime, CancellationToken ct);

    /// <summary>An attachment's content: a file's bytes, or an attached item with its MIME; null when it is gone.</summary>
    Task<EwsAttachmentContent?> GetAttachmentAsync(string attachmentId, CancellationToken ct);

    /// <summary>
    /// Several items in full, as <see cref="GetItemAsync"/> gives one, in one request where the service takes several ids
    /// (EWS does); in the order asked, each null when it is gone or the server would not give it (<see cref="GetItemAsync"/>
    /// then says why). Trouble with the request itself is thrown. By default each is asked for alone, all at once (the
    /// mailbox's pace lets four through at a time).
    /// </summary>
    Task<IReadOnlyList<EwsItem?>> GetItemsAsync(IReadOnlyList<string> itemIds, bool includeMime, CancellationToken ct)
        => EwsBatch.OneByOneAsync(itemIds, id => GetItemAsync(id, includeMime, ct));

    /// <summary>Several attachments' contents, as <see cref="GetAttachmentAsync"/> gives one, in one request where the service takes several ids; in the order asked, each null when it is gone or not given.</summary>
    Task<IReadOnlyList<EwsAttachmentContent?>> GetAttachmentsAsync(IReadOnlyList<string> attachmentIds, CancellationToken ct)
        => EwsBatch.OneByOneAsync(attachmentIds, id => GetAttachmentAsync(id, ct));
}

/// <summary>Asking for several items or attachments of a service that takes one id per request (Microsoft Graph).</summary>
internal static class EwsBatch
{
    /// <summary>
    /// Each id asked for alone, all at once: the mailbox's pace lets four through at a time. One the server refuses is null,
    /// for the single call to say why when it is asked for again; trouble with the service itself (the sign-in, a server too
    /// busy to answer) is thrown.
    /// </summary>
    public static async Task<IReadOnlyList<T?>> OneByOneAsync<T>(IReadOnlyList<string> ids, Func<string, Task<T?>> get)
        where T : class
    {
        var asked = ids.Select(async id =>
        {
            try
            {
                return await get(id).ConfigureAwait(false);
            }
            catch (EwsException ex) when (ex.Code != "401" && !ex.Transient)
            {
                return null;
            }
        });
        return await Task.WhenAll(asked).ConfigureAwait(false);
    }
}
