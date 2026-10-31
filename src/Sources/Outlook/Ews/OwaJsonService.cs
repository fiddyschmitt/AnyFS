using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnyFS.Sources.Outlook.Mail;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Outlook.Ews;

/// <summary>
/// Exchange Web Services as Outlook on the web speaks them (spec 7.7.2): JSON to <c>/owa/service.svc?action=…</c> with
/// the operation in an <c>Action</c> header and the One Outlook Web bearer the web session skimmed. The shapes are EWS's
/// with <c>:#Exchange</c> type tags (Spike 14, 24-Oct-2026: FindFolder deep, FindItem paged or over a CalendarView,
/// GetItem with AllProperties and IncludeMimeContent, GetAttachment with no shape, SyncFolderItems; no
/// SyncFolderHierarchy). A 401 asks the session for a fresh token once; 429 and 503 hold the mailbox's pace and try again.
/// </summary>
internal sealed class OwaJsonService : IEwsService, IAddressBookService, IDisposable
{
    private const int Attempts = 4;

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

    /// <summary>
    /// The persona fields FindPeople is asked for beside its default shape, which gives the names, the addresses and the
    /// city but no title, department or office: those Exchange Online took over an address list on 26-Oct-2026. It
    /// refused every telephone field there (BusinessPhoneNumbers, MobilePhones, PhoneNumber, HomePhones, OtherTelephones,
    /// the faxes and the rest), Titles and Department, and the AllProperties base shape, all as an invalid shape; so a
    /// directory entry's card has no telephone. A server that refuses these is asked for each alone
    /// (<see cref="PersonaFieldsTakenAsync"/>).
    /// </summary>
    private static readonly string[] PersonaFields =
    [
        "persona:Title", "persona:Departments", "persona:OfficeLocations", "persona:BusinessAddresses", "persona:ImAddress",
    ];

    private readonly Uri _origin;
    private readonly Func<bool, CancellationToken, Task<string>> _token;
    private readonly HttpClient _http;
    private readonly MailboxPace _pace;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _personaGate = new(1, 1);

    /// <summary>The persona fields this server takes, found when it refused <see cref="PersonaFields"/>; null until then.</summary>
    private volatile string[]? _personaFields;

    /// <param name="origin">Outlook on the web's origin for the cloud (https://outlook.office.com).</param>
    /// <param name="token">The bearer; true asks for a fresh one after a 401.</param>
    /// <param name="handler">The handler requests go through: a test's fake; null for the network.</param>
    public OwaJsonService(Uri origin, string mailbox, Func<bool, CancellationToken, Task<string>> token, HttpMessageHandler? handler, ILogger logger)
    {
        _origin = origin;
        _token = token;
        _pace = MailboxPace.For(mailbox);
        _logger = logger;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public void Dispose() => _http.Dispose();

    // --- operations -------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<EwsFolder>> FindFoldersAsync(CancellationToken ct)
    {
        var folders = new List<EwsFolder>();
        var offset = 0;
        while (true)
        {
            // the folders' change marks too (EwsFolderStamp), so a refresh syncs only the folders something happened in
            var shape = Properties("folder:FolderClass", "folder:ParentFolderId", "folder:TotalCount", "folder:UnreadCount", "folder:ChildFolderCount", "folder:DistinguishedFolderId");
            shape.Add((JsonNode)ExtendedField(EwsFolderStamp.LocalCommitTimeMax, "SystemTime"));
            shape.Add((JsonNode)ExtendedField(EwsFolderStamp.DeletedCountTotal, "Integer"));
            var message = await CallAsync("FindFolder", new JsonObject
            {
                ["__type"] = "FindFolderRequest:#Exchange",
                ["FolderShape"] = new JsonObject
                {
                    ["__type"] = "FolderResponseShape:#Exchange",
                    ["BaseShape"] = "Default",
                    ["AdditionalProperties"] = shape,
                },
                ["ParentFolderIds"] = new JsonArray(Distinguished("msgfolderroot")),
                ["Traversal"] = "Deep",
                ["Paging"] = IndexedPage(offset, 1000),
            }, ct).ConfigureAwait(false);
            var root = message.GetProperty("RootFolder");
            if (root.TryGetProperty("Folders", out var list))
            {
                foreach (var f in list.EnumerateArray())
                {
                    folders.Add(new EwsFolder(
                        f.GetProperty("FolderId").GetProperty("Id").GetString()!,
                        Str(f.GetProperty("FolderId"), "ChangeKey"),
                        f.TryGetProperty("ParentFolderId", out var parent) ? Str(parent, "Id") : null,
                        Str(f, "DisplayName") ?? "",
                        Str(f, "FolderClass"),
                        TypeOf(f),
                        Int(f, "TotalCount"),
                        Int(f, "UnreadCount"),
                        Int(f, "ChildFolderCount"),
                        Str(f, "DistinguishedFolderId"))
                    {
                        Stamp = StampOf(f),
                    });
                }

                offset += list.GetArrayLength();
            }

            if (!root.TryGetProperty("IncludesLastItemInRange", out var last) || last.GetBoolean() || offset == 0)
            {
                return folders;
            }
        }
    }

    public async Task<EwsItemPage> FindItemsAsync(string folderId, int offset, int count, CancellationToken ct)
    {
        var message = await CallAsync("FindItem", new JsonObject
        {
            ["__type"] = "FindItemRequest:#Exchange",
            ["ItemShape"] = new JsonObject { ["__type"] = "ItemResponseShape:#Exchange", ["BaseShape"] = "Default", ["AdditionalProperties"] = Properties(ListingProperties) },
            ["ParentFolderIds"] = new JsonArray(FolderIdNode(folderId)),
            ["Traversal"] = "Shallow",
            ["Paging"] = IndexedPage(offset, count),
            ["SortOrder"] = new JsonArray(new JsonObject { ["__type"] = "SortResults:#Exchange", ["Order"] = "Descending", ["Path"] = Property("item:DateTimeReceived") }),
        }, ct).ConfigureAwait(false);
        var root = message.GetProperty("RootFolder");
        var items = root.TryGetProperty("Items", out var list) ? list.EnumerateArray().Select(ItemOf).ToList() : [];
        return new EwsItemPage(items, Int(root, "TotalItemsInView"), !root.TryGetProperty("IncludesLastItemInRange", out var last) || last.GetBoolean());
    }

    public async Task<IReadOnlyList<EwsItem>> CalendarViewAsync(string folderId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var message = await CallAsync("FindItem", new JsonObject
        {
            ["__type"] = "FindItemRequest:#Exchange",
            ["ItemShape"] = new JsonObject { ["__type"] = "ItemResponseShape:#Exchange", ["BaseShape"] = "IdOnly", ["AdditionalProperties"] = Properties(CalendarProperties) },
            ["ParentFolderIds"] = new JsonArray(FolderIdNode(folderId)),
            ["Traversal"] = "Shallow",
            ["Paging"] = new JsonObject { ["__type"] = "CalendarPageView:#Exchange", ["StartDate"] = from.UtcDateTime.ToString("o"), ["EndDate"] = to.UtcDateTime.ToString("o"), ["MaxEntriesReturned"] = 1000 },
        }, ct).ConfigureAwait(false);
        var root = message.GetProperty("RootFolder");
        var items = root.TryGetProperty("Items", out var list) ? list.EnumerateArray().Select(ItemOf).ToList() : [];
        if (root.TryGetProperty("IncludesLastItemInRange", out var last) && !last.GetBoolean() && to - from > TimeSpan.FromDays(1))
        {
            // more than one answer holds: each half of the range on its own
            var middle = from + (to - from) / 2;
            var first = await CalendarViewAsync(folderId, from, middle, ct).ConfigureAwait(false);
            var second = await CalendarViewAsync(folderId, middle, to, ct).ConfigureAwait(false);
            return first.Concat(second).DistinctBy(i => i.Id).ToList();
        }

        return items;
    }

    public async Task<EwsSyncPage> SyncFolderItemsAsync(string folderId, string? syncState, int maxChanges, CancellationToken ct)
    {
        var message = await CallAsync("SyncFolderItems", new JsonObject
        {
            ["__type"] = "SyncFolderItemsRequest:#Exchange",
            ["ItemShape"] = new JsonObject { ["__type"] = "ItemResponseShape:#Exchange", ["BaseShape"] = "Default", ["AdditionalProperties"] = Properties(ListingProperties) },
            ["SyncFolderId"] = new JsonObject { ["__type"] = "TargetFolderId:#Exchange", ["BaseFolderId"] = FolderIdNode(folderId) },
            ["SyncState"] = syncState,
            ["MaxChangesReturned"] = Math.Clamp(maxChanges, 1, 512),
        }, ct).ConfigureAwait(false);
        var changes = new List<EwsChange>();
        if (message.TryGetProperty("Changes", out var wrapper) && wrapper.TryGetProperty("Changes", out var list))
        {
            foreach (var c in list.EnumerateArray())
            {
                var kind = Str(c, "ChangeType") ?? TypeOf(c).Replace("SyncFolderItems", "", StringComparison.Ordinal).Replace("Type", "", StringComparison.Ordinal);
                if (c.TryGetProperty("Item", out var item))
                {
                    var mapped = ItemOf(item);
                    changes.Add(new EwsChange(kind, mapped.Id, mapped, null));
                }
                else if (c.TryGetProperty("ItemId", out var id))
                {
                    changes.Add(new EwsChange(kind, id.GetProperty("Id").GetString()!, null, c.TryGetProperty("IsRead", out var read) ? read.GetBoolean() : null));
                }
            }
        }

        // the last page says IncludesLastItemInRange true, and the others leave it out: Outlook on the web's JSON leaves out
        // a false one here (26-Oct-2026, page after page of a folder of 1,856 items without it, then true on the last). Taken
        // for the last page, a missing one stopped every folder at its first page, and a mailbox showed only its newest mail.
        return new EwsSyncPage(Str(message, "SyncState") ?? syncState ?? "", changes, message.TryGetProperty("IncludesLastItemInRange", out var last) && last.ValueKind == JsonValueKind.True);
    }

    public async Task<EwsItem?> GetItemAsync(string itemId, bool includeMime, CancellationToken ct)
    {
        JsonElement message;
        try
        {
            message = await CallAsync("GetItem", GetItemBody([itemId], includeMime), ct).ConfigureAwait(false);
        }
        catch (EwsException ex) when (ex.IsNotFound)
        {
            return null;
        }

        return message.TryGetProperty("Items", out var items) && items.GetArrayLength() > 0 ? ItemOf(items[0]) : null;
    }

    public async Task<EwsAttachmentContent?> GetAttachmentAsync(string attachmentId, CancellationToken ct)
    {
        JsonElement message;
        try
        {
            message = await CallAsync("GetAttachment", GetAttachmentBody([attachmentId]), ct).ConfigureAwait(false);
        }
        catch (EwsException ex) when (ex.IsNotFound)
        {
            return null;
        }

        return AttachmentOf(message);
    }

    public async Task<IReadOnlyList<EwsItem?>> GetItemsAsync(IReadOnlyList<string> itemIds, bool includeMime, CancellationToken ct)
    {
        if (itemIds.Count == 0)
        {
            return [];
        }

        var messages = await CallAllAsync("GetItem", GetItemBody(itemIds, includeMime), ct).ConfigureAwait(false);
        return [.. itemIds.Select((_, i) => Answered(messages, i, "GetItem") is { } m && m.TryGetProperty("Items", out var items) && items.GetArrayLength() > 0 ? ItemOf(items[0]) : null)];
    }

    public async Task<IReadOnlyList<EwsAttachmentContent?>> GetAttachmentsAsync(IReadOnlyList<string> attachmentIds, CancellationToken ct)
    {
        if (attachmentIds.Count == 0)
        {
            return [];
        }

        var messages = await CallAllAsync("GetAttachment", GetAttachmentBody(attachmentIds), ct).ConfigureAwait(false);
        return [.. attachmentIds.Select((_, i) => Answered(messages, i, "GetAttachment") is { } m ? AttachmentOf(m) : null)];
    }

    private static JsonObject GetItemBody(IEnumerable<string> itemIds, bool includeMime) => new()
    {
        ["__type"] = "GetItemRequest:#Exchange",
        ["ItemShape"] = new JsonObject { ["__type"] = "ItemResponseShape:#Exchange", ["BaseShape"] = "AllProperties", ["IncludeMimeContent"] = includeMime, ["BodyType"] = "HTML" },
        ["ItemIds"] = new JsonArray(itemIds.Select(id => (JsonNode)new JsonObject { ["__type"] = "ItemId:#Exchange", ["Id"] = id }).ToArray()),
    };

    // OWA takes the request without a shape (Spike 14); an attached item comes with its MIME all the same
    private static JsonObject GetAttachmentBody(IEnumerable<string> attachmentIds) => new()
    {
        ["__type"] = "GetAttachmentRequest:#Exchange",
        ["AttachmentIds"] = new JsonArray(attachmentIds.Select(id => (JsonNode)new JsonObject { ["__type"] = "AttachmentId:#Exchange", ["Id"] = id }).ToArray()),
    };

    /// <summary>A GetAttachment response message's attachment: a file's bytes, or an attached item; null for none.</summary>
    private static EwsAttachmentContent? AttachmentOf(JsonElement message)
    {
        if (!message.TryGetProperty("Attachments", out var attachments) || attachments.GetArrayLength() == 0)
        {
            return null;
        }

        var a = attachments[0];
        var content = a.TryGetProperty("Content", out var c) && c.ValueKind == JsonValueKind.String ? Convert.FromBase64String(c.GetString()!) : null;
        var item = a.TryGetProperty("Item", out var i) && i.ValueKind == JsonValueKind.Object ? ItemOf(i) : null;
        return new EwsAttachmentContent(Str(a, "Name") ?? "", Str(a, "ContentType"), content, item);
    }

    /// <summary>The <paramref name="index"/>th of several response messages when it succeeded; null when it is an error (that id alone, said in the debug log) or missing.</summary>
    private JsonElement? Answered(IReadOnlyList<JsonElement> messages, int index, string action)
    {
        if (index >= messages.Count)
        {
            return null;
        }

        var message = messages[index];
        if (Str(message, "ResponseClass") is "Error")
        {
            _logger.LogDebug("outlook: {Action} answered {Code} for one of {Count} ids", action, Str(message, "ResponseCode") ?? "Error", messages.Count);
            return null;
        }

        return message;
    }

    // --- the directory (address books) ------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<AddressList>> AddressListsAsync(CancellationToken ct)
    {
        // GetPeopleFilters takes no envelope (Spike 11: a body of "{}") and answers the People page's filters: the mailbox's
        // own contact folders (a folder id with a parent folder) and then the organisation's address lists (a GUID, no parent)
        var answer = await SendAsync("GetPeopleFilters", "{}", "People", Answer.Whole, ct).ConfigureAwait(false);
        var filters = answer.ValueKind == JsonValueKind.Array ? answer : Values(answer, "Body").Any() ? answer.GetProperty("Body") : default;
        var lists = new List<AddressList>();
        if (filters.ValueKind != JsonValueKind.Array)
        {
            return lists;
        }

        foreach (var f in filters.EnumerateArray())
        {
            var folder = f.ValueKind == JsonValueKind.Object && f.TryGetProperty("FolderId", out var id) && id.ValueKind == JsonValueKind.Object ? id : default;
            var hasParent = f.ValueKind == JsonValueKind.Object && f.TryGetProperty("ParentFolderId", out var parent) && parent.ValueKind == JsonValueKind.Object;
            var isList = folder.ValueKind == JsonValueKind.Object && (TypeOf(folder).StartsWith("AddressListId", StringComparison.Ordinal) || !hasParent && Guid.TryParse(Str(folder, "Id"), out _));
            if (isList && Str(folder, "Id") is { Length: > 0 } listId && Blank(Str(f, "DisplayName")) is { } name)
            {
                lists.Add(new AddressList(listId, name));
            }
        }

        return lists;
    }

    public async Task<DirectoryPage> AddressListPageAsync(string listId, int offset, int count, CancellationToken ct)
    {
        var fields = _personaFields ?? PersonaFields;
        JsonElement answer;
        try
        {
            answer = await FindPeopleAsync(listId, offset, count, fields, ct).ConfigureAwait(false);
        }
        catch (EwsException ex) when (fields.Length > 0 && ex.Code == "ErrorInvalidArgument")
        {
            // "Invalid shape specification": a server that takes fewer of the fields than the lab's
            fields = await PersonaFieldsTakenAsync(listId, fields, ex, ct).ConfigureAwait(false);
            answer = await FindPeopleAsync(listId, offset, count, fields, ct).ConfigureAwait(false);
        }

        var body = answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("Body", out var b) && b.ValueKind == JsonValueKind.Object ? b : answer;
        var entries = Values(body, "ResultSet").Select(PersonaOf).OfType<DirectoryEntry>().ToList();
        return new DirectoryPage(entries, Int(body, "TotalNumberOfPeopleInView"));
    }

    /// <summary>
    /// After the server refused <paramref name="refused"/>: the persona fields it takes, each of <see cref="PersonaFields"/>
    /// asked for alone over a page of one, once for the service (a page that comes meanwhile waits for the answer). Should
    /// the server refuse even those later, the default shape alone is asked for from then on.
    /// </summary>
    private async Task<string[]> PersonaFieldsTakenAsync(string listId, string[] refused, EwsException ex, CancellationToken ct)
    {
        await _personaGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_personaFields is { } known && !ReferenceEquals(known, refused))
            {
                return known;
            }

            if (_personaFields is not null)
            {
                _logger.LogInformation("outlook: {Host} refused the persona fields it took before ({Error}); asking for the default ones alone", _origin.Host, ex.Message);
                return _personaFields = [];
            }

            var taken = new List<string>();
            var left = new List<string>();
            foreach (var field in PersonaFields)
            {
                try
                {
                    await FindPeopleAsync(listId, 0, 1, [field], ct).ConfigureAwait(false);
                    taken.Add(field);
                }
                catch (EwsException alone) when (alone.Code == "ErrorInvalidArgument")
                {
                    left.Add(field);
                }
            }

            _logger.LogInformation("outlook: {Host} refused the persona fields asked for beside the default ones ({Error}); it takes {Taken} and not {Left}",
                _origin.Host, ex.Message, taken.Count > 0 ? string.Join(", ", taken) : "none of them", left.Count > 0 ? string.Join(", ", left) : "none");
            return _personaFields = [.. taken];
        }
        finally
        {
            _personaGate.Release();
        }
    }

    /// <summary>One FindPeople over an address list: its default shape, with <paramref name="fields"/> beside it.</summary>
    private Task<JsonElement> FindPeopleAsync(string listId, int offset, int count, IReadOnlyList<string> fields, CancellationToken ct)
    {
        var shape = new JsonObject { ["__type"] = "PersonaResponseShape:#Exchange", ["BaseShape"] = "Default" };
        if (fields.Count > 0)
        {
            shape["AdditionalProperties"] = Properties([.. fields]);
        }

        var request = new JsonObject
        {
            ["__type"] = "FindPeopleRequest:#Exchange",
            ["IndexedPageItemView"] = IndexedPage(offset, count),
            ["QueryString"] = null,
            ["ParentFolderId"] = new JsonObject { ["__type"] = "TargetFolderId:#Exchange", ["BaseFolderId"] = new JsonObject { ["__type"] = "AddressListId:#Exchange", ["Id"] = listId } },
            ["PersonaShape"] = shape,
            ["ShouldResolveOneOffEmailAddress"] = false,
        };
        return SendAsync("FindPeople", Envelope("FindPeople", request), "People", Answer.Whole, ct);
    }

    /// <summary>
    /// A persona as FindPeople gives it (its default shape, and the attributed, many-valued fields where a server adds
    /// them: "Titles": [{"Value": …}]) as a directory entry; null for one with neither a name nor an address. Only SMTP
    /// addresses are kept: an X.500 one opens nothing outside Exchange.
    /// </summary>
    internal static DirectoryEntry? PersonaOf(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var emails = new List<string>();
        void Email(JsonElement e)
        {
            var box = Inner(e);
            if (Blank(Str(box, "EmailAddress")) is { } address && address.Contains('@') && Str(box, "RoutingType") is null or "SMTP" && !emails.Contains(address, StringComparer.OrdinalIgnoreCase))
            {
                emails.Add(address);
            }
        }

        if (p.TryGetProperty("EmailAddress", out var primary))
        {
            Email(primary);
        }

        foreach (var list in new[] { "EmailAddresses", "Emails1", "Emails2", "Emails3" })
        {
            foreach (var e in Many(p, list))
            {
                Email(e);
            }
        }

        var name = Text(p, "DisplayName", "DisplayNames") ?? Text(p, "DisplayNameFirstLast") ?? Text(p, "FileAs", "FileAses") ?? emails.FirstOrDefault();
        if (name is null)
        {
            return null;
        }

        var phones = new List<DirectoryPhone>();
        void Phone(string type, JsonElement e)
        {
            var value = Inner(e);
            var number = value.ValueKind == JsonValueKind.String ? value.GetString() : Str(value, "Number");
            if (Blank(number) is { } n && !phones.Any(x => x.Number == n))
            {
                phones.Add(new DirectoryPhone(PhoneTypeOf(Str(value, "Type")) ?? type, n));
            }
        }

        if (p.TryGetProperty("PhoneNumber", out var phone))
        {
            Phone("WORK,VOICE", phone);
        }

        foreach (var (list, type) in new[]
        {
            ("BusinessPhoneNumbers", "WORK,VOICE"), ("BusinessPhoneNumbers2", "WORK,VOICE"), ("MobilePhones", "CELL,VOICE"), ("MobilePhones2", "CELL,VOICE"),
            ("HomePhones", "HOME,VOICE"), ("HomePhones2", "HOME,VOICE"), ("WorkFaxes", "WORK,FAX"), ("HomeFaxes", "HOME,FAX"), ("Pagers", "PAGER"), ("OtherTelephones", "VOICE"),
        })
        {
            foreach (var e in Many(p, list))
            {
                Phone(type, e);
            }
        }

        var addresses = new List<DirectoryAddress>();
        foreach (var (list, type) in new[] { ("BusinessAddresses", "WORK"), ("HomeAddresses", "HOME"), ("OtherAddresses", "POSTAL") })
        {
            foreach (var e in Many(p, list))
            {
                var a = Inner(e);
                var address = new DirectoryAddress(type, Blank(Str(a, "Street")), Blank(Str(a, "City")), Blank(Str(a, "State")), Blank(Str(a, "PostalCode")), Blank(Str(a, "Country")));
                if ((address.Street ?? address.City ?? address.State ?? address.PostalCode ?? address.Country) is not null && !addresses.Contains(address))
                {
                    addresses.Add(address);
                }
            }
        }

        if (Text(p, "WorkCity") is { } city && !addresses.Any(a => a.Type == "WORK"))
        {
            addresses.Add(new DirectoryAddress("WORK", null, city, null, null, null));
        }

        var id = p.TryGetProperty("PersonaId", out var persona) && persona.ValueKind == JsonValueKind.Object ? Str(persona, "Id") : null;
        return new DirectoryEntry(Blank(id) ?? emails.FirstOrDefault() ?? name, name, Blank(Str(p, "PersonaType")) ?? Blank(Str(p, "PersonaTypeString")))
        {
            GivenName = Text(p, "GivenName", "GivenNames"),
            MiddleName = Text(p, "MiddleName", "MiddleNames"),
            Surname = Text(p, "Surname", "Surnames"),
            Nickname = Text(p, "Nickname", "Nicknames"),
            Company = Text(p, "CompanyName", "CompanyNames"),
            Department = Text(p, "Department", "Departments"),
            JobTitle = Text(p, "Title", "Titles"),
            Office = Text(p, "OfficeLocation", "OfficeLocations"),
            Emails = emails,
            Phones = phones,
            Addresses = addresses,
            ImAddress = Text(p, "ImAddress", "ImAddresses"),
        };
    }

    /// <summary>An attributed value's value ({"Value": …, "Attributions": […]}), or the element itself.</summary>
    private static JsonElement Inner(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("Value", out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.String ? v : e;

    /// <summary>A persona's string field, or else the first of its attributed values.</summary>
    private static string? Text(JsonElement p, string single, string? many = null)
    {
        if (Blank(Str(p, single)) is { } text)
        {
            return text;
        }

        foreach (var e in many is null ? [] : Many(p, many))
        {
            var value = Inner(e);
            if (Blank(value.ValueKind == JsonValueKind.String ? value.GetString() : null) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>A persona's phone type ("Business", "Mobile", "HomeFax") as a vCard's; null for none it names.</summary>
    private static string? PhoneTypeOf(string? type) => type switch
    {
        null or "" => null,
        _ when type.Contains("Fax", StringComparison.OrdinalIgnoreCase) => type.StartsWith("Home", StringComparison.OrdinalIgnoreCase) ? "HOME,FAX" : "WORK,FAX",
        _ when type.Contains("Mobile", StringComparison.OrdinalIgnoreCase) || type.Contains("Cell", StringComparison.OrdinalIgnoreCase) || type.Contains("Car", StringComparison.OrdinalIgnoreCase) => "CELL,VOICE",
        _ when type.StartsWith("Home", StringComparison.OrdinalIgnoreCase) => "HOME,VOICE",
        _ when type.Contains("Pager", StringComparison.OrdinalIgnoreCase) => "PAGER",
        _ when type.StartsWith("Business", StringComparison.OrdinalIgnoreCase) || type.StartsWith("Work", StringComparison.OrdinalIgnoreCase) || type.StartsWith("Office", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("Company", StringComparison.OrdinalIgnoreCase) || type.StartsWith("Assistant", StringComparison.OrdinalIgnoreCase) => "WORK,VOICE",
        _ => "VOICE",
    };

    private static IEnumerable<JsonElement> Values(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    /// <summary>A persona's many-valued field, named as EWS names it ("Departments") or as Outlook on the web's JSON does ("DepartmentsArray", Exchange Online on 26-Oct-2026).</summary>
    private static IEnumerable<JsonElement> Many(JsonElement p, string name) => Values(p, name).Concat(Values(p, name + "Array"));

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // --- the call -----------------------------------------------------------------------------------------------------------

    /// <summary>One operation: its first response message, checked.</summary>
    private Task<JsonElement> CallAsync(string action, JsonObject body, CancellationToken ct) => SendAsync(action, Envelope(action, body), "Mail", Answer.First, ct);

    /// <summary>One operation on several ids: its response messages, one per id in the order asked, each unchecked (an error answers that id alone).</summary>
    private async Task<IReadOnlyList<JsonElement>> CallAllAsync(string action, JsonObject body, CancellationToken ct)
        => [.. (await SendAsync(action, Envelope(action, body), "Mail", Answer.All, ct).ConfigureAwait(false)).EnumerateArray()];

    /// <summary>What a request's answer is taken as.</summary>
    private enum Answer
    {
        /// <summary>Its first response message, checked.</summary>
        First,

        /// <summary>Every response message, as an array, each unchecked; tried again when every one says the server is busy.</summary>
        All,

        /// <summary>The answer as it is, checked for an error: People's operations answer no response message.</summary>
        Whole,
    }

    /// <summary>One request, its answer taken as <paramref name="answer"/> says. Paced, signed, tried again on a 401 (once, with a fresh token) and on a hold.</summary>
    private async Task<JsonElement> SendAsync(string action, string envelope, string app, Answer answer, CancellationToken ct)
    {
        var renew = false;
        for (var attempt = 1; ; attempt++)
        {
            using var slot = await _pace.EnterAsync(ct).ConfigureAwait(false);
            var token = await _token(renew, ct).ConfigureAwait(false);
            renew = false;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_origin.AbsoluteUri.TrimEnd('/')}/owa/service.svc?action={action}&app={app}")
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Action", action);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < Attempts)
            {
                _logger.LogInformation("outlook: {Action} to {Host} failed ({Error}); trying again", action, _origin.Host, ex.Message);
                _pace.Hold(null);
                continue;
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (attempt < Attempts && !renew)
                    {
                        _logger.LogInformation("outlook: {Host} refused the web session's token on {Action}; asking for a fresh one", _origin.Host, action);
                        renew = true;
                        continue;
                    }

                    throw new EwsException("401", $"{_origin.Host} refused the web session's token", transient: false);
                }

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    _pace.Hold(response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow));
                    if (attempt < Attempts)
                    {
                        continue;
                    }

                    throw new EwsException(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), $"{_origin.Host} is busy ({(int)response.StatusCode}); try later", transient: true);
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(text);
                }
                catch (JsonException)
                {
                    throw new EwsException(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), $"{_origin.Host} answered {action} with something other than JSON ({(int)response.StatusCode} {response.ReasonPhrase}; {Trim(text)})", transient: (int)response.StatusCode >= 500);
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (!response.IsSuccessStatusCode)
                    {
                        // {"Body":{"ErrorCode":400,"ExceptionName":"...","FaultMessage":"...","IsTransient":false,"ResponseCode":"BadRequest"}}
                        var fault = root.TryGetProperty("Body", out var b) ? b : root;
                        var transient = fault.TryGetProperty("IsTransient", out var t) && t.ValueKind == JsonValueKind.True;
                        if (transient && attempt < Attempts)
                        {
                            _pace.Hold(null);
                            continue;
                        }

                        throw new EwsException(Str(fault, "ResponseCode") ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), $"{action}: {Str(fault, "FaultMessage") ?? Trim(text)}", transient);
                    }

                    if (answer == Answer.Whole)
                    {
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Body", out var body) && body.ValueKind == JsonValueKind.Object && Str(body, "ResponseClass") is "Error")
                        {
                            var code = Str(body, "ResponseCode") ?? "Error";
                            throw new EwsException(code, $"{action}: {Str(body, "MessageText") ?? code}");
                        }

                        return root.Clone();
                    }

                    if (!root.TryGetProperty("Body", out var responseBody) || !responseBody.TryGetProperty("ResponseMessages", out var messages) || !messages.TryGetProperty("Items", out var items) || items.GetArrayLength() == 0)
                    {
                        throw new EwsException("NoResponse", $"{action}: {_origin.Host} answered without a response message ({Trim(text)})");
                    }

                    if (answer == Answer.All)
                    {
                        if (attempt < Attempts && items.EnumerateArray().All(m => Str(m, "ResponseClass") is "Error" && IsTransient(Str(m, "ResponseCode"))))
                        {
                            _pace.Hold(TimeSpan.FromSeconds(10));
                            continue;
                        }

                        return items.Clone();
                    }

                    var message = items[0];
                    if (Str(message, "ResponseClass") is "Error")
                    {
                        var code = Str(message, "ResponseCode") ?? "Error";
                        var transient = IsTransient(code);
                        if (transient && attempt < Attempts)
                        {
                            _pace.Hold(message.TryGetProperty("MessageXml", out _) ? null : TimeSpan.FromSeconds(10));
                            continue;
                        }

                        throw new EwsException(code, $"{action}: {Str(message, "MessageText") ?? code}", transient);
                    }

                    return message.Clone();
                }
            }
        }
    }

    private static bool IsTransient(string? code) => code is "ErrorServerBusy" or "ErrorTimeoutExpired" or "ErrorInternalServerTransientError" or "ErrorMailboxStoreUnavailable" or "ErrorTooManyObjectsOpened" or "ErrorConnectionFailed";

    private static string Envelope(string action, JsonObject body) => new JsonObject
    {
        ["__type"] = $"{action}JsonRequest:#Exchange",
        ["Header"] = new JsonObject
        {
            ["__type"] = "JsonRequestHeaders:#Exchange",
            ["RequestServerVersion"] = "Exchange2013",
            ["TimeZoneContext"] = new JsonObject { ["__type"] = "TimeZoneContext:#Exchange", ["TimeZoneDefinition"] = new JsonObject { ["__type"] = "TimeZoneDefinitionType:#Exchange", ["Id"] = "UTC" } },
        },
        ["Body"] = body,
    }.ToJsonString();

    private static string Trim(string text)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    // --- request pieces ---------------------------------------------------------------------------------------------------

    private static JsonObject Property(string fieldUri) => new() { ["__type"] = "PropertyUri:#Exchange", ["FieldURI"] = fieldUri };

    /// <summary>An extended (MAPI) property by its tag, for a shape's additional properties.</summary>
    private static JsonObject ExtendedField(string propertyTag, string propertyType) => new() { ["__type"] = "ExtendedPropertyUri:#Exchange", ["PropertyTag"] = propertyTag, ["PropertyType"] = propertyType };

    /// <summary>A folder's change marks from the extended properties it came with (<see cref="EwsFolderStamp"/>); null where it came with none.</summary>
    private static string? StampOf(JsonElement folder)
    {
        if (!folder.TryGetProperty("ExtendedProperty", out var properties) || properties.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return EwsFolderStamp.Of(properties.EnumerateArray()
            .Select(p => (Tag: p.TryGetProperty("ExtendedFieldURI", out var uri) ? Str(uri, "PropertyTag") ?? "" : "", Value: Str(p, "Value") ?? "")));
    }

    private static JsonArray Properties(params string[] fieldUris) => new(fieldUris.Select(f => (JsonNode)Property(f)).ToArray());

    private static JsonObject Distinguished(string id) => new() { ["__type"] = "DistinguishedFolderId:#Exchange", ["Id"] = id };

    /// <summary>A folder id node: a distinguished name ("inbox") or an id.</summary>
    private static JsonObject FolderIdNode(string folderId) => folderId.All(char.IsAsciiLetterLower) ? Distinguished(folderId) : new JsonObject { ["__type"] = "FolderId:#Exchange", ["Id"] = folderId };

    private static JsonObject IndexedPage(int offset, int count) => new() { ["__type"] = "IndexedPageView:#Exchange", ["BasePoint"] = "Beginning", ["Offset"] = offset, ["MaxEntriesReturned"] = count };

    // --- the answers' shapes --------------------------------------------------------------------------------------------------

    /// <summary>An item as the JSON gives it, in every field the store reads.</summary>
    internal static EwsItem ItemOf(JsonElement e)
    {
        var body = e.TryGetProperty("Body", out var b) && b.ValueKind == JsonValueKind.Object ? b : default;
        var complete = e.TryGetProperty("CompleteName", out var cn) && cn.ValueKind == JsonValueKind.Object ? cn : default;
        return new EwsItem
        {
            Id = e.GetProperty("ItemId").GetProperty("Id").GetString()!,
            ChangeKey = Str(e.GetProperty("ItemId"), "ChangeKey"),
            ParentFolderId = e.TryGetProperty("ParentFolderId", out var p) && p.ValueKind == JsonValueKind.Object ? Str(p, "Id") : null,
            Type = TypeOf(e),
            ItemClass = Str(e, "ItemClass"),
            Subject = Str(e, "Subject"),
            DateTimeReceived = Date(e, "DateTimeReceived"),
            DateTimeSent = Date(e, "DateTimeSent"),
            DateTimeCreated = Date(e, "DateTimeCreated"),
            LastModifiedTime = Date(e, "LastModifiedTime"),
            Size = Long(e, "Size"),
            HasAttachments = Bool(e, "HasAttachments"),
            IsRead = Bool(e, "IsRead"),
            IsDraft = Bool(e, "IsDraft"),
            Importance = Str(e, "Importance"),
            InternetMessageId = Str(e, "InternetMessageId"),
            InReplyTo = Str(e, "InReplyTo"),
            References = Str(e, "References"),
            DisplayTo = Str(e, "DisplayTo"),
            DisplayCc = Str(e, "DisplayCc"),
            From = Single(e, "From"),
            Sender = Single(e, "Sender"),
            ToRecipients = Mailboxes(e, "ToRecipients"),
            CcRecipients = Mailboxes(e, "CcRecipients"),
            BccRecipients = Mailboxes(e, "BccRecipients"),
            Body = body.ValueKind == JsonValueKind.Object ? Str(body, "Value") : null,
            BodyIsHtml = body.ValueKind == JsonValueKind.Object && Str(body, "BodyType") == "HTML",
            Mime = e.TryGetProperty("MimeContent", out var m) && m.ValueKind == JsonValueKind.Object && Str(m, "Value") is { } mime ? Convert.FromBase64String(mime) : null,
            Attachments = e.TryGetProperty("Attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array
                ? attachments.EnumerateArray().Select(a => new EwsAttachmentInfo(
                    a.GetProperty("AttachmentId").GetProperty("Id").GetString()!,
                    Str(a, "Name") ?? "",
                    Str(a, "ContentType"),
                    Long(a, "Size"),
                    Bool(a, "IsInline"),
                    Str(a, "ContentId"),
                    TypeOf(a).StartsWith("ItemAttachment", StringComparison.Ordinal),
                    Bool(a, "IsContactPhoto"))).ToList()
                : [],
            Start = Date(e, "Start"),
            End = Date(e, "End"),
            IsAllDayEvent = Bool(e, "IsAllDayEvent"),
            Location = Str(e, "Location"),
            Organizer = Single(e, "Organizer"),
            RequiredAttendees = Attendees(e, "RequiredAttendees"),
            OptionalAttendees = Attendees(e, "OptionalAttendees"),
            IsRecurring = Bool(e, "IsRecurring"),
            CalendarItemType = Str(e, "CalendarItemType"),
            Uid = Str(e, "UID"),
            LegacyFreeBusyStatus = Str(e, "LegacyFreeBusyStatus"),
            DisplayName = Str(e, "DisplayName"),
            FileAs = Str(e, "FileAs"),
            GivenName = Str(e, "GivenName") ?? (complete.ValueKind == JsonValueKind.Object ? Str(complete, "FirstName") : null),
            MiddleName = Str(e, "MiddleName") ?? (complete.ValueKind == JsonValueKind.Object ? Str(complete, "MiddleName") : null),
            Surname = Str(e, "Surname") ?? (complete.ValueKind == JsonValueKind.Object ? Str(complete, "LastName") : null),
            Nickname = Str(e, "Nickname") ?? (complete.ValueKind == JsonValueKind.Object ? Str(complete, "Nickname") : null),
            Generation = Str(e, "Generation") ?? (complete.ValueKind == JsonValueKind.Object ? Str(complete, "Suffix") : null),
            Title = complete.ValueKind == JsonValueKind.Object ? Str(complete, "Title") : null,
            CompanyName = Str(e, "CompanyName"),
            JobTitle = Str(e, "JobTitle"),
            Department = Str(e, "Department"),
            EmailAddresses = Keyed(e, "EmailAddresses", "EmailAddress"),
            PhoneNumbers = Keyed(e, "PhoneNumbers", "PhoneNumber"),
            PhysicalAddresses = e.TryGetProperty("PhysicalAddresses", out var addresses) && addresses.ValueKind == JsonValueKind.Array
                ? addresses.EnumerateArray().Select(a => new EwsPhysicalAddress(Str(a, "Key") ?? "", Str(a, "Street"), Str(a, "City"), Str(a, "State"), Str(a, "PostalCode"), Str(a, "CountryOrRegion"))).ToList()
                : [],
            BusinessHomePage = Str(e, "BusinessHomePage"),
            ImAddresses = Keyed(e, "ImAddresses", "ImAddress").Select(k => k.Item2).ToList(),
            Birthday = Date(e, "Birthday"),
            WeddingAnniversary = Date(e, "WeddingAnniversary"),
        };
    }

    private static string TypeOf(JsonElement e) => (Str(e, "__type") ?? "Item").Replace(":#Exchange", "", StringComparison.Ordinal);

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static long Long(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement e, string name)
        => Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    /// <summary>A SingleRecipient ({"Mailbox":{...}}) or a bare mailbox.</summary>
    private static EwsMailbox? Single(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var mailbox = v.TryGetProperty("Mailbox", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : v;
        var box = MailboxOf(mailbox);
        return box.Name is null && box.Address is null ? null : box;
    }

    private static EwsMailbox MailboxOf(JsonElement mailbox) => new(Str(mailbox, "Name"), Str(mailbox, "EmailAddress"), Str(mailbox, "RoutingType"));

    private static IReadOnlyList<EwsMailbox> Mailboxes(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(r => MailboxOf(r.TryGetProperty("Mailbox", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : r)).ToList()
            : [];

    private static IReadOnlyList<EwsAttendee> Attendees(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(a => new EwsAttendee(MailboxOf(a.TryGetProperty("Mailbox", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : a), Str(a, "ResponseType"))).ToList()
            : [];

    /// <summary>A keyed list ([{"Key":"EmailAddress1","EmailAddress":"…"}]) as (key, value) pairs, empty values left out.</summary>
    private static IReadOnlyList<(string, string)> Keyed(JsonElement e, string name, string valueName)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(k => (Str(k, "Key") ?? "", Str(k, valueName) ?? "")).Where(k => k.Item2.Length > 0).ToList()
            : [];
}
