using System.Collections.Concurrent;
using AnyFS.Sources.Outlook.Ews;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Outlook.Mail;

/// <summary>
/// A mailbox on its server as a mail store (spec 7.7), over <see cref="IEwsService"/>: the folder tree from one deep
/// FindFolder (Outlook's own hidden folders left out unless asked for); each mail folder's items through
/// SyncFolderItems, all of them the first time and only what changed after (the delta per folder the spec asks for);
/// a calendar folder through a CalendarView over a window around now, recurring appointments expanded to their
/// occurrences by the server; an item in full, MIME included, through GetItem when its folder is opened; attachments
/// through GetAttachment when read. Item ids are the server's; an attached message is named by its parent's id and the
/// attachment's, joined with '|'.
/// </summary>
internal sealed class EwsMailStore : IMailStore
{
    private const int SyncPage = 500;

    private readonly IEwsService _ews;
    private readonly bool _hidden;
    private readonly TimeSpan _calendarBack;
    private readonly TimeSpan _calendarAhead;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, FolderState> _states = new(StringComparer.Ordinal);

    public EwsMailStore(IEwsService ews, string account, bool showHidden, int calendarMonthsBack, int calendarMonthsAhead, ILogger logger)
    {
        _ews = ews;
        Name = account;
        _hidden = showHidden;
        _calendarBack = TimeSpan.FromDays(30.44 * Math.Max(0, calendarMonthsBack));
        _calendarAhead = TimeSpan.FromDays(30.44 * Math.Max(1, calendarMonthsAhead));
        _logger = logger;
    }

    public string Name { get; }

    public IReadOnlyList<MailFolder> Folders { get; private set; } = [];

    /// <summary>How many items the synced folders hold between them (for logs and tests).</summary>
    public int ItemsKnown => _states.Values.Sum(s => s.Items.Count);

    public async Task RefreshAsync(CancellationToken ct)
    {
        var all = await _ews.FindFoldersAsync(ct).ConfigureAwait(false);
        var ids = new HashSet<string>(all.Select(f => f.Id), StringComparer.Ordinal);
        var byParent = all.GroupBy(f => f.ParentId is { } p && ids.Contains(p) ? p : "").ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        Folders = Map(byParent.GetValueOrDefault("") ?? [], byParent);

        // folders that are gone take their state with them; the ones read before take in what changed
        foreach (var gone in _states.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _states.TryRemove(gone, out _);
        }

        foreach (var state in _states.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (state.Folder.IsCalendar)
            {
                state.Calendar = null;
            }
            else if (state.Complete)
            {
                await SyncAsync(state, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<MailItemSummary>> ItemsAsync(MailFolder folder, CancellationToken ct)
    {
        var state = _states.GetOrAdd(folder.Id, _ => new FolderState(folder));
        state.Folder = folder;
        if (folder.IsCalendar)
        {
            return state.Calendar ??= await CalendarAsync(folder, ct).ConfigureAwait(false);
        }

        if (!state.Complete)
        {
            await SyncAsync(state, ct).ConfigureAwait(false);
        }

        return state.Items.Values.OrderByDescending(i => i.Date ?? DateTimeOffset.MinValue).ToList();
    }

    public async Task<MailItem?> OpenAsync(string itemId, CancellationToken ct)
    {
        var parts = itemId.Split('|');
        var item = await _ews.GetItemAsync(parts[0], includeMime: true, ct).ConfigureAwait(false);
        var id = parts[0];
        for (var i = 1; item is not null && i < parts.Length; i++)
        {
            var attachment = await _ews.GetAttachmentAsync(parts[i], ct).ConfigureAwait(false);
            item = attachment?.Item;
            id += "|" + parts[i];
        }

        return item is null ? null : Build(item, id);
    }

    public void Dispose()
    {
        (_ews as IDisposable)?.Dispose();
    }

    // --- folders ----------------------------------------------------------------------------------------------------------

    private IReadOnlyList<MailFolder> Map(List<EwsFolder> folders, Dictionary<string, List<EwsFolder>> byParent)
    {
        var list = new List<MailFolder>();
        foreach (var folder in folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var containerClass = folder.FolderClass ?? folder.Type switch
            {
                "CalendarFolder" => "IPF.Appointment",
                "ContactsFolder" => "IPF.Contact",
                "TasksFolder" => "IPF.Task",
                _ => null,
            };
            if (!_hidden && !MailItemKinds.IsShownFolder(containerClass, folder.Name))
            {
                continue;
            }

            var children = Map(byParent.GetValueOrDefault(folder.Id) ?? [], byParent);
            list.Add(new MailFolder(folder.Id, folder.Name, containerClass, children, folder.TotalCount, null, null));
        }

        return list;
    }

    // --- items ------------------------------------------------------------------------------------------------------------

    /// <summary>Brings a folder's index up to date: everything the first time, the changes since after; one sync of a folder at a time.</summary>
    private async Task SyncAsync(FolderState state, CancellationToken ct)
    {
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var created = 0;
            var deleted = 0;
            for (var attempt = 0; ; attempt++)
            {
                EwsSyncPage page;
                try
                {
                    page = await _ews.SyncFolderItemsAsync(state.Folder.Id, state.SyncState, SyncPage, ct).ConfigureAwait(false);
                }
                catch (EwsException ex) when (ex.Code is "ErrorInvalidSyncStateData" or "ErrorInvalidSyncStateVersion" && state.SyncState is not null && attempt == 0)
                {
                    _logger.LogInformation("outlook: the sync state for '{Folder}' is no longer accepted; reading the folder whole", state.Folder.Name);
                    state.SyncState = null;
                    state.Items.Clear();
                    continue;
                }

                foreach (var change in page.Changes)
                {
                    switch (change.Kind)
                    {
                        case "Create":
                        case "Update":
                            if (change.Item is { } item)
                            {
                                if (state.Items.TryAdd(item.Id, Summarise(item, item.Id, inCalendar: false)))
                                {
                                    created++;
                                }
                                else
                                {
                                    state.Items[item.Id] = Summarise(item, item.Id, inCalendar: false);
                                }
                            }

                            break;
                        case "Delete":
                            if (state.Items.Remove(change.ItemId))
                            {
                                deleted++;
                            }

                            break;
                    }
                }

                state.SyncState = page.SyncState;
                if (page.IncludesLastItemInRange || page.Changes.Count == 0)
                {
                    break;
                }
            }

            if (!state.Complete)
            {
                _logger.LogInformation("outlook: '{Folder}': {Count:N0} items", state.Folder.Name, state.Items.Count);
            }
            else if (created + deleted > 0)
            {
                _logger.LogInformation("outlook: '{Folder}': {Created} new, {Deleted} gone", state.Folder.Name, created, deleted);
            }

            state.Complete = true;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>A calendar folder's occurrences over the window, a year of it per request.</summary>
    private async Task<IReadOnlyList<MailItemSummary>> CalendarAsync(MailFolder folder, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var from = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero) - _calendarBack;
        var to = now + _calendarAhead;
        var items = new Dictionary<string, MailItemSummary>(StringComparer.Ordinal);
        for (var start = from; start < to; start += TimeSpan.FromDays(365))
        {
            var end = start + TimeSpan.FromDays(365) < to ? start + TimeSpan.FromDays(365) : to;
            foreach (var item in await _ews.CalendarViewAsync(folder.Id, start, end, ct).ConfigureAwait(false))
            {
                items.TryAdd(item.Id, Summarise(item, item.Id, inCalendar: true));
            }
        }

        _logger.LogInformation("outlook: '{Folder}': {Count:N0} appointments between {From:yyyy-MM-dd} and {To:yyyy-MM-dd}", folder.Name, items.Count, from, to);
        return items.Values.OrderBy(i => i.Date).ToList();
    }

    // --- mapping ----------------------------------------------------------------------------------------------------------

    /// <param name="inCalendar">Whether the item is listed from a calendar folder: an appointment there is an event, dated by its start; a meeting request in a mail folder is dated by its arrival.</param>
    private static MailItemSummary Summarise(EwsItem item, string id, bool inCalendar)
    {
        var kind = MailItemKinds.From(item.ItemClass ?? item.Type switch
        {
            "CalendarItem" or "MeetingRequest" or "MeetingResponse" or "MeetingCancellation" => "IPM.Appointment",
            "Contact" => "IPM.Contact",
            "DistributionList" => "IPM.DistList",
            "Task" => "IPM.Task",
            _ => "IPM.Note",
        });
        var subject = Blank(item.Subject) ?? (kind == MailItemKind.Contact ? Blank(item.DisplayName) ?? Blank(item.FileAs) : null) ?? "";
        var sender = Blank(item.From?.Name) ?? Blank(item.Sender?.Name) ?? Blank(item.Organizer?.Name) ?? item.From?.Smtp ?? item.Sender?.Smtp
            ?? Blank(item.DisplayTo?.Split(';', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            ?? Blank(item.ToRecipients.FirstOrDefault()?.Name) ?? item.ToRecipients.FirstOrDefault()?.Smtp;
        var isEvent = kind == MailItemKind.Appointment && inCalendar;
        var date = isEvent
            ? item.Start ?? item.DateTimeCreated ?? item.LastModifiedTime
            : item.DateTimeReceived ?? item.DateTimeSent ?? item.DateTimeCreated ?? item.LastModifiedTime;
        return new MailItemSummary(id, kind, subject, sender, date, item.DateTimeCreated, item.LastModifiedTime, item.Size, item.HasAttachments) { IsEvent = isEvent };
    }

    private MailItem Build(EwsItem item, string id)
    {
        var summary = Summarise(item, id, inCalendar: false);
        var attachments = item.Attachments.Select(a => new MailAttachment(
            a.IsItem && !a.Name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) ? a.Name + ".eml" : a.Name,
            a.IsItem ? "message/rfc822" : Blank(a.ContentType) ?? ContentTypes.FromName(a.Name),
            Blank(a.ContentId),
            a.IsInline,
            a.Size,
            a.IsItem ? _ => Task.FromResult(Stream.Null) : ct => OpenAttachmentAsync(a.Id, ct),
            a.IsItem ? ct => OpenEmbeddedAsync(id, a.Id, ct) : null,
            a.IsContactPhoto,
            ExactSize: false)).ToList();
        var recipients = new List<MailRecipient>();
        recipients.AddRange(item.ToRecipients.Select(m => new MailRecipient(MailRecipientKind.To, Address(m))));
        recipients.AddRange(item.CcRecipients.Select(m => new MailRecipient(MailRecipientKind.Cc, Address(m))));
        recipients.AddRange(item.BccRecipients.Select(m => new MailRecipient(MailRecipientKind.Bcc, Address(m))));
        recipients.AddRange(item.RequiredAttendees.Select(a => new MailRecipient(MailRecipientKind.To, Address(a.Mailbox), Response(a.ResponseType))));
        recipients.AddRange(item.OptionalAttendees.Select(a => new MailRecipient(MailRecipientKind.Cc, Address(a.Mailbox), Response(a.ResponseType))));

        return new MailItem
        {
            Summary = summary,
            MessageClass = item.ItemClass ?? "",
            From = Address(item.From ?? item.Sender ?? item.Organizer),
            Recipients = recipients,
            Sent = item.DateTimeSent,
            Received = item.DateTimeReceived,
            Created = item.DateTimeCreated,
            Modified = item.LastModifiedTime,
            InternetMessageId = Blank(item.InternetMessageId),
            InReplyTo = Blank(item.InReplyTo),
            References = Blank(item.References),
            BodyHtml = item.BodyIsHtml ? Blank(item.Body) : null,
            BodyText = item.BodyIsHtml ? null : Blank(item.Body),
            RawMime = item.Mime,
            Importance = item.Importance switch { "High" => 2, "Low" => 0, "Normal" => 1, _ => null },
            IsRead = item.IsRead,
            IsDraft = item.IsDraft,
            Attachments = attachments,
            Appointment = summary.Kind == MailItemKind.Appointment
                ? new AppointmentDetail(item.Start, item.End, item.IsAllDayEvent, Blank(item.Location), item.IsRecurring, null, item.LegacyFreeBusyStatus switch
                {
                    "Free" => "free",
                    "Tentative" => "tentative",
                    "Busy" => "busy",
                    "OOF" => "out of office",
                    "WorkingElsewhere" => "working elsewhere",
                    _ => null,
                }, Blank(item.Uid))
                : null,
            Contact = summary.Kind == MailItemKind.Contact
                ? new ContactDetail(
                    Blank(item.FileAs) ?? Blank(item.DisplayName),
                    Blank(item.Title),
                    Blank(item.GivenName),
                    Blank(item.MiddleName),
                    Blank(item.Surname),
                    Blank(item.Generation),
                    Blank(item.Nickname),
                    Blank(item.CompanyName),
                    Blank(item.JobTitle),
                    Blank(item.Department),
                    item.EmailAddresses.Select(e => e.Address).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    item.PhoneNumbers.Select(p => (PhoneType(p.Key), p.Number)).ToList(),
                    item.PhysicalAddresses.Select(a => (a.Key.ToUpperInvariant() switch { "BUSINESS" => "WORK", "HOME" => "HOME", _ => "POSTAL" }, a.Street ?? "", a.City ?? "", a.State ?? "", a.PostalCode ?? "", a.Country ?? "")).ToList(),
                    Blank(item.BusinessHomePage),
                    item.ImAddresses.FirstOrDefault(),
                    item.Birthday,
                    item.WeddingAnniversary)
                : null,
        };
    }

    private async Task<Stream> OpenAttachmentAsync(string attachmentId, CancellationToken ct)
    {
        var content = await _ews.GetAttachmentAsync(attachmentId, ct).ConfigureAwait(false);
        return new MemoryStream(content?.Content ?? [], writable: false);
    }

    private async Task<MailItem?> OpenEmbeddedAsync(string parentId, string attachmentId, CancellationToken ct)
    {
        var content = await _ews.GetAttachmentAsync(attachmentId, ct).ConfigureAwait(false);
        return content?.Item is { } item ? Build(item, parentId + "|" + attachmentId) : null;
    }

    private static MailAddress Address(EwsMailbox? m) => m is null ? new MailAddress(null, null) : new MailAddress(Blank(m.Name), m.Smtp);

    private static string? Response(string? responseType) => responseType switch
    {
        "Accept" => "accepted",
        "Decline" => "declined",
        "Tentative" => "tentative",
        "Organizer" => "organizer",
        _ => null,
    };

    private static string PhoneType(string key) => key switch
    {
        "BusinessPhone" or "BusinessPhone2" or "CompanyMainPhone" or "AssistantPhone" => "WORK,VOICE",
        "HomePhone" or "HomePhone2" => "HOME,VOICE",
        "MobilePhone" or "CarPhone" => "CELL,VOICE",
        "BusinessFax" or "OtherFax" => "WORK,FAX",
        "HomeFax" => "HOME,FAX",
        "Pager" => "PAGER",
        _ => "VOICE",
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>What the store knows of one folder: its sync state and the items the server has told it of.</summary>
    private sealed class FolderState(MailFolder folder)
    {
        public MailFolder Folder = folder;
        public string? SyncState;
        public bool Complete;
        public IReadOnlyList<MailItemSummary>? Calendar;
        public readonly Dictionary<string, MailItemSummary> Items = new(StringComparer.Ordinal);
        public readonly SemaphoreSlim Gate = new(1, 1);
    }
}
