using System.Collections.Concurrent;
using AnyFS.Sources.Outlook.Ews;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Outlook.Mail;

/// <summary>
/// A mailbox on its server as a mail store (spec 7.7), over <see cref="IEwsService"/>: the folder tree from one deep
/// FindFolder (Outlook's own hidden folders left out unless asked for); each mail folder's items through
/// SyncFolderItems, all of them the first time and only what changed after (the delta per folder the spec asks for);
/// a calendar folder through a CalendarView over a window around now, recurring appointments expanded to their
/// occurrences by the server; items in full, MIME included, through GetItem when their folders are opened, several in one
/// request with all their attachments in the next (<see cref="OpenManyAsync"/>), or one alone with each attachment
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

    public bool OpensTogether => true;

    /// <summary>How many items the synced folders hold between them (for logs and tests).</summary>
    public int ItemsKnown => _states.Values.Sum(s => s.Items.Count);

    /// <summary>Counts the changes to what the store knows (its folders, items and calendar views), so its listing is kept again only when it changed.</summary>
    public long Revision => Interlocked.Read(ref _revision);

    private long _revision;

    /// <summary>What the store knows, to keep for the next start: the folder tree, each synced folder's items and sync state, each calendar's last view.</summary>
    /// <param name="shape">The settings the listing depends on, kept with it.</param>
    public async Task<MailboxIndex> ExportAsync(string shape, CancellationToken ct)
    {
        var states = new List<FolderIndex>();
        foreach (var state in _states.Values)
        {
            await state.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (state.Folder.IsCalendar && state.Calendar is { } view)
                {
                    states.Add(new FolderIndex(state.Folder.Id, null, view, IsCalendarView: true));
                }
                else if (!state.Folder.IsCalendar && state.Complete)
                {
                    states.Add(new FolderIndex(state.Folder.Id, state.SyncState, [.. state.Items.Values], IsCalendarView: false));
                }
            }
            finally
            {
                state.Gate.Release();
            }
        }

        return new MailboxIndex(MailboxIndexFile.Version, Name, shape, DateTimeOffset.UtcNow, Folders, states);
    }

    /// <summary>
    /// Takes in a kept listing: its folders, and each folder's items and sync state, are served from it, a calendar's
    /// last view too, until <see cref="RefreshAsync"/> asks the server what changed since.
    /// </summary>
    public void Import(MailboxIndex index)
    {
        Folders = index.Folders;
        var byId = new Dictionary<string, MailFolder>(StringComparer.Ordinal);
        void Collect(IReadOnlyList<MailFolder> folders)
        {
            foreach (var folder in folders)
            {
                byId[folder.Id] = folder;
                Collect(folder.Folders);
            }
        }

        Collect(index.Folders);
        foreach (var kept in index.States)
        {
            if (!byId.TryGetValue(kept.FolderId, out var folder))
            {
                continue;
            }

            var view = kept.IsCalendarView ? kept.Items : null;
            var state = new FolderState(folder) { SyncState = kept.SyncState, Complete = !kept.IsCalendarView, Calendar = view, Kept = view };
            if (!kept.IsCalendarView)
            {
                foreach (var item in kept.Items)
                {
                    state.Items[item.Id] = item;
                }
            }

            _states[folder.Id] = state;
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var all = await _ews.FindFoldersAsync(ct).ConfigureAwait(false);
        var ids = new HashSet<string>(all.Select(f => f.Id), StringComparer.Ordinal);
        var byParent = all.GroupBy(f => f.ParentId is { } p && ids.Contains(p) ? p : "").ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var folders = Map(byParent.GetValueOrDefault("") ?? [], byParent);
        if (Signature(folders) != Signature(Folders))
        {
            Interlocked.Increment(ref _revision);
        }

        Folders = folders;

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
            if (state.Calendar is null)
            {
                var view = await CalendarAsync(folder, ct).ConfigureAwait(false);
                if (state.Kept is not { } kept || !kept.SequenceEqual(view))
                {
                    Interlocked.Increment(ref _revision);
                }

                state.Calendar = state.Kept = view;
            }

            return state.Calendar;
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

    /// <summary>
    /// The items in one GetItem, MIME included; then every attachment of theirs in GetAttachments of at most
    /// <see cref="AttachmentsPerRequest"/> ids and <see cref="AttachmentBytesPerRequest"/> by the sizes the server gives
    /// (a bigger one alone): a file's content, so its length is exact, and an attached item, so its message is made without
    /// asking again. An attached message's own id (with '|') is opened alone.
    /// </summary>
    public async Task<IReadOnlyList<MailItem?>> OpenManyAsync(IReadOnlyList<string> itemIds, CancellationToken ct)
    {
        var whole = itemIds.Where(id => !id.Contains('|', StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        var fetched = await _ews.GetItemsAsync(whole, includeMime: true, ct).ConfigureAwait(false);
        var items = new Dictionary<string, EwsItem>(StringComparer.Ordinal);
        for (var i = 0; i < whole.Count; i++)
        {
            if (fetched[i] is { } item)
            {
                items[whole[i]] = item;
            }
        }

        var contents = new Dictionary<string, EwsAttachmentContent>(StringComparer.Ordinal);
        foreach (var chunk in Chunks(items.Values.SelectMany(i => i.Attachments).ToList()))
        {
            IReadOnlyList<EwsAttachmentContent?> got;
            try
            {
                got = await _ews.GetAttachmentsAsync([.. chunk.Select(a => a.Id)], ct).ConfigureAwait(false);
            }
            catch (EwsException ex) when (!ex.Transient && ex.Code != "401")
            {
                // refused as a whole (a fault): each is fetched alone as its item's files are made, so one that fails fails its item alone
                _logger.LogDebug("outlook: GetAttachment for {Count} attachments was refused ({Error}); each is asked for alone", chunk.Count, ex.Message);
                continue;
            }

            for (var i = 0; i < chunk.Count; i++)
            {
                if (got[i] is { } content)
                {
                    contents[chunk[i].Id] = content;
                }
            }
        }

        var opened = new List<MailItem?>(itemIds.Count);
        foreach (var id in itemIds)
        {
            opened.Add(id.Contains('|', StringComparison.Ordinal) ? await OpenAsync(id, ct).ConfigureAwait(false) : items.TryGetValue(id, out var item) ? Build(item, id, contents) : null);
        }

        return opened;
    }

    /// <summary>Attachments asked for at a time.</summary>
    private const int AttachmentsPerRequest = 16;

    /// <summary>What a GetAttachment may bring back at a time, by the sizes the server gives (8 MB); one bigger comes alone.</summary>
    private const long AttachmentBytesPerRequest = 8L << 20;

    private static IEnumerable<List<EwsAttachmentInfo>> Chunks(IReadOnlyList<EwsAttachmentInfo> attachments)
    {
        var chunk = new List<EwsAttachmentInfo>();
        long bytes = 0;
        foreach (var attachment in attachments)
        {
            if (chunk.Count > 0 && (chunk.Count == AttachmentsPerRequest || bytes + attachment.Size > AttachmentBytesPerRequest))
            {
                yield return chunk;
                chunk = [];
                bytes = 0;
            }

            chunk.Add(attachment);
            bytes += attachment.Size;
        }

        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }

    public void Dispose()
    {
        (_ews as IDisposable)?.Dispose();
    }

    // --- folders ----------------------------------------------------------------------------------------------------------

    /// <summary>The folder tree as one string: what tells a changed tree from the same one.</summary>
    private static string Signature(IReadOnlyList<MailFolder> folders)
        => string.Join(';', folders.Select(f => $"{f.Id}|{f.Name}|{f.ContainerClass}|{f.ItemCount}[{Signature(f.Folders)}]"));

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
            var updated = 0;
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
                                    updated++;
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

            if (created + updated + deleted > 0)
            {
                Interlocked.Increment(ref _revision);
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

    /// <param name="contents">The attachments' contents fetched with the item (<see cref="OpenManyAsync"/>), by attachment id; one not there is fetched when read.</param>
    private MailItem Build(EwsItem item, string id, IReadOnlyDictionary<string, EwsAttachmentContent>? contents = null)
    {
        var summary = Summarise(item, id, inCalendar: false);
        var attachments = item.Attachments.Select(a =>
        {
            var name = a.IsItem && !a.Name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) ? a.Name + ".eml" : a.Name;
            var contentType = a.IsItem ? "message/rfc822" : Blank(a.ContentType) ?? ContentTypes.FromName(a.Name);
            if (contents?.GetValueOrDefault(a.Id) is not { } content)
            {
                return new MailAttachment(name, contentType, Blank(a.ContentId), a.IsInline, a.Size,
                    a.IsItem ? _ => Task.FromResult(Stream.Null) : ct => OpenAttachmentAsync(a.Id, ct),
                    a.IsItem ? ct => OpenEmbeddedAsync(id, a.Id, ct) : null,
                    a.IsContactPhoto,
                    ExactSize: false);
            }

            if (a.IsItem)
            {
                var embedded = content.Item is { } inner ? Build(inner, id + "|" + a.Id) : null;
                return new MailAttachment(name, contentType, Blank(a.ContentId), a.IsInline, a.Size, _ => Task.FromResult(Stream.Null), _ => Task.FromResult(embedded), a.IsContactPhoto, ExactSize: false);
            }

            var bytes = content.Content ?? [];
            return new MailAttachment(name, contentType, Blank(a.ContentId), a.IsInline, bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)), null, a.IsContactPhoto, ExactSize: true);
        }).ToList();
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

        /// <summary>The calendar view last read (or kept from the last start), to tell a changed view from the same one.</summary>
        public IReadOnlyList<MailItemSummary>? Kept;
        public readonly Dictionary<string, MailItemSummary> Items = new(StringComparer.Ordinal);
        public readonly SemaphoreSlim Gate = new(1, 1);
    }
}
