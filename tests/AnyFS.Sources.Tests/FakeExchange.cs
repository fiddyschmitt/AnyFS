using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnyFS.Sources.Outlook;
using AnyFS.Sources.Outlook.Files;
using AnyFS.Sources.Outlook.Mail;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A stand-in for Exchange Online's Outlook on the web service (service.svc, EWS as JSON), in process, for the Outlook
/// source's tests when the sandbox is not there: a seeded mailbox answering FindFolder, FindItem (paged, or a calendar
/// view), GetItem (MIME included), GetAttachment and SyncFolderItems in the shapes Spike 14 recorded (24-Oct-2026), with
/// a bearer it checks (so a token renewal can be tested), a change log per folder (so a delta can be), and a hook for
/// answering a request otherwise (a 429, say).
/// </summary>
internal sealed partial class FakeExchange
{
    public const string Account = "jane@fake.test";

    /// <summary>What Exchange adds to an attachment's size, its properties' share (Spike 12: 247 bytes for a 70-byte image), so the fake's sizes are as far out as the real ones.</summary>
    public const int StoreOverhead = 177;

    private readonly Dictionary<string, Folder> _folders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Item Owner, Attachment Attachment)> _attachments = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _next;
    private int _tokens;

    public FakeExchange()
    {
        ServerToken = NextToken();
    }

    public Uri Origin { get; } = new("https://outlook.fake.test");

    /// <summary>The bearer the fake accepts now; <see cref="RotateToken"/> changes it, as a lapsed token would.</summary>
    public string ServerToken { get; private set; }

    /// <summary>Every action asked for, in order.</summary>
    public List<string> Actions { get; } = [];

    /// <summary>How many requests were refused for their token.</summary>
    public int Refusals { get; private set; }

    /// <summary>How many times a folder's items were asked for whole (a sync with no state, a delta with no token): what a listing kept between starts spares.</summary>
    public int FullSyncs { get; private set; }

    /// <summary>How many items GetItem was asked for, whatever the number of requests (several go in one).</summary>
    public int ItemsFetched { get; private set; }

    /// <summary>How many items each GetItem asked for, in the order they came.</summary>
    public List<int> ItemsPerRequest { get; } = [];

    /// <summary>The most changes a SyncFolderItems page gives, whatever is asked for (a server may give fewer).</summary>
    public int MostChangesPerPage { get; set; } = int.MaxValue;

    /// <summary>A server that says each SyncFolderItems page is the last (as the source took Outlook on the web's to say, before 26-Oct-2026).</summary>
    public bool EveryPageSaysLast { get; set; }

    /// <summary>How many attachments GetAttachment was asked for, whatever the number of requests.</summary>
    public int AttachmentsFetched { get; private set; }

    /// <summary>Answers a request instead of the fake when it returns one (a 429 with Retry-After, say).</summary>
    public Func<HttpRequestMessage, string, HttpResponseMessage?>? Intercept { get; set; }

    public IReadOnlyDictionary<string, Folder> Folders => _folders;

    /// <summary>A fresh token the server accepts from now on; the old one is refused.</summary>
    public string RotateToken() => ServerToken = NextToken();

    private string NextToken() => $"fake-token-{++_tokens}";

    /// <summary>What a source or the Test button is given in place of the browser and the network.</summary>
    public OutlookStandIn StandIn()
    {
        var token = ServerToken;
        return new OutlookStandIn(Handler, Account, (renew, _) =>
        {
            if (renew)
            {
                token = ServerToken;
            }

            return Task.FromResult(token);
        }, Origin);
    }

    public HttpMessageHandler Handler() => new InProcessHandler(this);

    // --- seeding ----------------------------------------------------------------------------------------------------------

    /// <summary>A mailbox as a new one looks, with a little mail, a calendar and a contact: what most tests start from.</summary>
    public static FakeExchange Mailbox()
    {
        var fake = new FakeExchange();
        var root = fake.AddFolder("Top of Information Store", "IPF.Note", null, null, root: true);
        var inbox = fake.AddFolder("Inbox", "IPF.Note", root, "inbox");
        fake.AddFolder("Sent Items", "IPF.Note", root, "sentitems");
        fake.AddFolder("Drafts", "IPF.Note", root, "drafts");
        fake.AddFolder("Deleted Items", "IPF.Note", root, "deleteditems");
        var calendar = fake.AddFolder("Calendar", "IPF.Appointment", root, "calendar", "CalendarFolder");
        var contacts = fake.AddFolder("Contacts", "IPF.Contact", root, "contacts", "ContactsFolder");
        fake.AddFolder("Recipient Cache", "IPF.Contact.RecipientCache", contacts, "recipientcache", "ContactsFolder");
        fake.AddFolder("Conversation Action Settings", "IPF.Configuration", root, null);
        var yammer = fake.AddFolder("Yammer Root", "IPF", root, null);
        fake.AddFolder("Feeds", "IPF", yammer, null);
        var projects = fake.AddFolder("Projects", "IPF.Note", inbox, null);

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var figures = fake.AddMessage(inbox, "Quarterly figures", ("Adele Vance", "adelev@fake.test"), new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero), "<p>Figures attached.</p>",
            [new Attachment("figures.csv", "text/csv", Encoding.ASCII.GetBytes("quarter,amount\nQ3,42\nQ4,58\n"), false, null)]);
        figures.IsRead = true;
        var photo = fake.AddMessage(inbox, "Photo from the site visit", ("Megan Bowen", "meganb@fake.test"), new DateTimeOffset(2026, 3, 15, 16, 5, 42, TimeSpan.Zero), "<p>Here it is:</p><p><img src=\"cid:site-dot\"></p>",
            [new Attachment("dot.png", "image/png", png, true, "site-dot")]);
        fake.AddMessage(inbox, "Fwd: Photo from the site visit", ("Alex Wilber", "alexw@fake.test"), new DateTimeOffset(2026, 4, 2, 8, 0, 0, TimeSpan.Zero), "<p>See attached.</p>",
            [new Attachment("Photo from the site visit", "message/rfc822", null, false, null) { Embedded = photo }]);
        fake.AddMessage(projects, "RE: Project kick-off", ("Diego Siciliani", "diegos@fake.test"), new DateTimeOffset(2026, 4, 2, 10, 15, 0, TimeSpan.Zero), "<p>In a subfolder.</p>", [], cc: [("Jane", Account)]);

        var design = fake.AddAppointment(calendar, "Design review", new DateTimeOffset(2026, 10, 27, 4, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1), "Room 4", ("Adele Vance", "adelev@fake.test"));
        design.Attendees.Add((("Jane", Account), "Accept"));
        for (var week = 0; week < 3; week++)
        {
            var o = fake.AddAppointment(calendar, "Stand-up", new DateTimeOffset(2026, 10, 5, 23, 0, 0, TimeSpan.Zero).AddDays(7 * week), TimeSpan.FromMinutes(15), "Teams", ("Jane", Account));
            o.CalendarItemType = "Occurrence";
            o.IsRecurring = true;
            o.Uid = "040000008200E00074C5B7101A82E008STANDUP";
        }

        fake.AddContact(contacts, "Adele Vance", "Adele", "Vance", "adelev@fake.test", "Contoso", "Retail Manager", "+1 555 0100");
        return fake;
    }

    public Folder AddFolder(string name, string folderClass, Folder? parent, string? distinguished, string type = "Folder", bool root = false)
    {
        lock (_gate)
        {
            var folder = new Folder($"f{++_next}", name, folderClass, parent?.Id, distinguished, type, root);
            _folders[folder.Id] = folder;
            return folder;
        }
    }

    public Item AddMessage(Folder folder, string subject, (string Name, string Address) from, DateTimeOffset received, string html, IReadOnlyList<Attachment> attachments, IReadOnlyList<(string Name, string Address)>? to = null, IReadOnlyList<(string Name, string Address)>? cc = null)
    {
        lock (_gate)
        {
            var item = new Item($"i{++_next}", folder.Id, "Message", "IPM.Note")
            {
                Subject = subject,
                From = from,
                Received = received,
                Sent = received - TimeSpan.FromSeconds(30),
                Created = received,
                Modified = received,
                BodyHtml = html,
                To = (to ?? [("Jane", Account)]).ToList(),
                Cc = (cc ?? []).ToList(),
                InternetMessageId = $"<{Guid.NewGuid():N}@fake.test>",
            };
            foreach (var a in attachments)
            {
                a.Id = $"a{++_next}";
                item.Attachments.Add(a);
                _attachments[a.Id] = (item, a);
            }

            item.Mime = Mime(item);
            item.Size = item.Mime.Length;
            _items[item.Id] = item;
            folder.Log("Create", item.Id);
            return item;
        }
    }

    /// <summary>A meeting request as it sits in a mail folder: a message from the organiser that carries the meeting's start, end and location.</summary>
    public Item AddMeetingRequest(Folder folder, string subject, (string Name, string Address) organizer, DateTimeOffset received, DateTimeOffset start, TimeSpan length, string location)
    {
        lock (_gate)
        {
            var item = new Item($"i{++_next}", folder.Id, "MeetingRequest", "IPM.Schedule.Meeting.Request")
            {
                Subject = subject,
                From = organizer,
                Organizer = organizer,
                Received = received,
                Sent = received - TimeSpan.FromSeconds(30),
                Created = received,
                Modified = received,
                Start = start,
                End = start + length,
                Location = location,
                BodyHtml = "<p>Please come.</p>",
                To = [("Jane", Account)],
                InternetMessageId = $"<{Guid.NewGuid():N}@fake.test>",
                Uid = Guid.NewGuid().ToString("N"),
            };
            item.Mime = Mime(item);
            item.Size = item.Mime.Length;
            _items[item.Id] = item;
            folder.Log("Create", item.Id);
            return item;
        }
    }

    public Item AddAppointment(Folder folder, string subject, DateTimeOffset start, TimeSpan length, string location, (string Name, string Address) organizer)
    {
        lock (_gate)
        {
            var item = new Item($"i{++_next}", folder.Id, "CalendarItem", "IPM.Appointment")
            {
                Subject = subject,
                Start = start,
                End = start + length,
                Location = location,
                Organizer = organizer,
                Created = start - TimeSpan.FromDays(7),
                Modified = start - TimeSpan.FromDays(7),
                BodyHtml = "<p>Agenda: the Outlook source.</p>",
                Uid = Guid.NewGuid().ToString("N"),
            };
            item.Size = 2000;
            _items[item.Id] = item;
            folder.Log("Create", item.Id);
            return item;
        }
    }

    public Item AddContact(Folder folder, string displayName, string given, string surname, string email, string company, string jobTitle, string phone)
    {
        lock (_gate)
        {
            var item = new Item($"i{++_next}", folder.Id, "Contact", "IPM.Contact")
            {
                Subject = displayName,
                DisplayName = displayName,
                GivenName = given,
                Surname = surname,
                Email = email,
                Company = company,
                JobTitle = jobTitle,
                Phone = phone,
                Created = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero),
                Modified = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero),
            };
            item.Size = 1500;
            _items[item.Id] = item;
            folder.Log("Create", item.Id);
            return item;
        }
    }

    /// <summary>Changes an item as a client saving it would: what <paramref name="change"/> does, a later last-modified time, its MIME and size anew, and an update in its folder's log.</summary>
    public void Update(Item item, Action<Item> change)
    {
        lock (_gate)
        {
            change(item);
            item.Modified = (item.Modified ?? DateTimeOffset.UtcNow) + TimeSpan.FromMinutes(1);
            if (item.Type == "Message")
            {
                item.Mime = Mime(item);
                item.Size = item.Mime.Length;
            }

            _folders[item.FolderId].Log("Update", item.Id);
        }
    }

    public void Delete(Item item)
    {
        lock (_gate)
        {
            _items.Remove(item.Id);
            _folders[item.FolderId].Log("Delete", item.Id);
        }
    }

    /// <summary>The MIME a message is served as: made the way the source makes one for a PST, so the .eml tests have a known shape.</summary>
    private static byte[] Mime(Item item)
    {
        var mail = new MailItem
        {
            Summary = new MailItemSummary(item.Id, MailItemKind.Message, item.Subject ?? "", item.From?.Name, item.Received, item.Created, item.Modified, 0, item.Attachments.Count > 0),
            MessageClass = "IPM.Note",
            From = new MailAddress(item.From?.Name, item.From?.Address),
            Recipients = [.. item.To.Select(t => new MailRecipient(MailRecipientKind.To, new MailAddress(t.Name, t.Address))), .. item.Cc.Select(t => new MailRecipient(MailRecipientKind.Cc, new MailAddress(t.Name, t.Address)))],
            Sent = item.Sent,
            Received = item.Received,
            InternetMessageId = item.InternetMessageId,
            BodyHtml = item.BodyHtml,
            Attachments = item.Attachments.Select(a => new MailAttachment(a.Name, a.ContentType, a.ContentId, a.IsInline, a.Content?.Length ?? 0,
                _ => Task.FromResult<Stream>(new MemoryStream(a.Content ?? [], writable: false)),
                a.Embedded is null ? null : _ => Task.FromResult<MailItem?>(new MailItem { Summary = new MailItemSummary(a.Embedded.Id, MailItemKind.Message, a.Embedded.Subject ?? "", null, a.Embedded.Received, null, null, 0, false), MessageClass = "IPM.Note", RawMime = a.Embedded.Mime }))).ToList(),
        };
        return MimeWriter.Build(mail);
    }

    // --- the service ------------------------------------------------------------------------------------------------------

    /// <summary>Whether SOAP requests (an Exchange server's EWS) are refused for their sign-in, as a wrong password would be.</summary>
    public bool RefuseWindowsSignIn { get; set; }

    private HttpResponseMessage Serve(HttpRequestMessage request, string body)
    {
        if (request.RequestUri?.Host == GraphHost)
        {
            return ServeGraph(request);
        }

        if (request.RequestUri?.AbsolutePath.EndsWith("/EWS/Exchange.asmx", StringComparison.OrdinalIgnoreCase) == true)
        {
            return ServeSoap(request, body);
        }

        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri?.Query ?? "");
        var action = query["action"] ?? (request.Headers.TryGetValues("Action", out var values) ? values.First() : "");
        lock (_gate)
        {
            Actions.Add(action);
        }

        if (Intercept?.Invoke(request, action) is { } intercepted)
        {
            return intercepted;
        }

        if (request.Headers.Authorization is not { Scheme: "Bearer" } auth || auth.Parameter != ServerToken)
        {
            lock (_gate)
            {
                Refusals++;
            }

            var refused = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("", Encoding.UTF8, "text/plain") };
            refused.Headers.WwwAuthenticate.ParseAdd("Bearer");
            return refused;
        }

        if (request.RequestUri?.AbsolutePath != "/owa/service.svc")
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var json = JsonNode.Parse(body)?.AsObject() ?? [];
        var requestBody = json["Body"]?.AsObject() ?? [];
        lock (_gate)
        {
            return action switch
            {
                "FindFolder" => Ok(action, FindFolder()),
                "FindItem" => Ok(action, FindItem(requestBody)),
                "GetItem" => GetItem(requestBody),
                "GetAttachment" => GetAttachment(requestBody),
                "SyncFolderItems" => Ok(action, SyncFolderItems(requestBody)),
                "GetFolder" => Ok(action, GetFolder(requestBody)),
                "GetPeopleFilters" => GetPeopleFilters(query["app"] ?? ""),
                "FindPeople" => FindPeople(query["app"] ?? "", requestBody),
                _ => Text(500, $"OWA service API '{action}' does not exist in the service method map."),
            };
        }
    }

    private JsonObject FindFolder()
    {
        var root = _folders.Values.Single(f => f.IsRoot);
        var list = new JsonArray(_folders.Values.Where(f => !f.IsRoot).Select(f => (JsonNode)FolderJson(f, root)).ToArray());
        return new JsonObject { ["__type"] = "FindFolderResponseMessage:#Exchange", ["RootFolder"] = new JsonObject { ["__type"] = "FindFolderParentWrapper:#Exchange", ["Folders"] = list, ["IndexedPagingOffset"] = list.Count, ["TotalItemsInView"] = list.Count, ["IncludesLastItemInRange"] = true }, ["ResponseClass"] = "Success", ["ResponseCode"] = "NoError" };
    }

    private JsonObject GetFolder(JsonObject body)
    {
        var folder = Resolve(body["FolderIds"]?[0]);
        return folder is null
            ? new JsonObject { ["__type"] = "FolderInfoResponseMessage:#Exchange", ["ResponseClass"] = "Error", ["ResponseCode"] = "ErrorFolderNotFound", ["MessageText"] = "The specified folder could not be found in the store." }
            : new JsonObject { ["__type"] = "FolderInfoResponseMessage:#Exchange", ["Folders"] = new JsonArray(FolderJson(folder, _folders.Values.Single(f => f.IsRoot))), ["ResponseClass"] = "Success", ["ResponseCode"] = "NoError" };
    }

    private JsonObject FindItem(JsonObject body)
    {
        var folder = Resolve(body["ParentFolderIds"]?[0]);
        if (folder is null)
        {
            return Error("FindItemResponseMessage", "ErrorFolderNotFound", "The specified folder could not be found in the store.");
        }

        var paging = body["Paging"]?.AsObject();
        var items = _items.Values.Where(i => i.FolderId == folder.Id).ToList();
        if (paging?["__type"]?.GetValue<string>() == "CalendarPageView:#Exchange")
        {
            var from = DateTimeOffset.Parse(paging["StartDate"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            var to = DateTimeOffset.Parse(paging["EndDate"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            if (to - from > TimeSpan.FromDays(731))
            {
                return Error("FindItemResponseMessage", "ErrorCalendarViewRangeTooBig", "The specified view range exceeds the maximum range of two years.");
            }

            var inRange = items.Where(i => i.Start is { } s && s >= from && s < to).OrderBy(i => i.Start).ToList();
            return new JsonObject { ["__type"] = "FindItemResponseMessage:#Exchange", ["RootFolder"] = new JsonObject { ["__type"] = "FindItemParentWrapper:#Exchange", ["Items"] = new JsonArray(inRange.Select(i => (JsonNode)ItemJson(i, full: false, mime: false)).ToArray()), ["TotalItemsInView"] = inRange.Count, ["IncludesLastItemInRange"] = true }, ["ResponseClass"] = "Success", ["ResponseCode"] = "NoError" };
        }

        var offset = paging?["Offset"]?.GetValue<int>() ?? 0;
        var max = paging?["MaxEntriesReturned"]?.GetValue<int>() ?? 1000;
        var ordered = items.OrderByDescending(i => i.Received ?? i.Created ?? DateTimeOffset.MinValue).ToList();
        var page = ordered.Skip(offset).Take(max).ToList();
        return new JsonObject { ["__type"] = "FindItemResponseMessage:#Exchange", ["RootFolder"] = new JsonObject { ["__type"] = "FindItemParentWrapper:#Exchange", ["Items"] = new JsonArray(page.Select(i => (JsonNode)ItemJson(i, full: false, mime: false)).ToArray()), ["IndexedPagingOffset"] = offset + page.Count, ["TotalItemsInView"] = ordered.Count, ["IncludesLastItemInRange"] = offset + page.Count >= ordered.Count }, ["ResponseClass"] = "Success", ["ResponseCode"] = "NoError" };
    }

    private HttpResponseMessage GetItem(JsonObject body)
    {
        var shape = body["ItemShape"]?.AsObject();
        if (shape?["AdditionalProperties"] is JsonArray extra && extra.Any(p => p?["FieldURI"]?.GetValue<string>() is "item:TextBody" or "item:MimeContent" or "message:InternetMessageHeaders"))
        {
            // what Outlook on the web does with those (Spike 14)
            return Text(400, """{"Body":{"ErrorCode":400,"ExceptionName":"OwaMethodArgumentException","FaultMessage":"Invalid argument used to call method GetItem","IsTransient":false,"ResponseCode":"BadRequest"}}""");
        }

        // one response message per id asked for, in order, as Exchange answers
        var mime = shape?["IncludeMimeContent"]?.GetValue<bool>() == true;
        var messages = new List<JsonObject>();
        ItemsPerRequest.Add(body["ItemIds"]?.AsArray().Count ?? 0);
        foreach (var asked in body["ItemIds"]?.AsArray() ?? [])
        {
            ItemsFetched++;
            messages.Add(_items.TryGetValue(asked?["Id"]?.GetValue<string>() ?? "", out var item)
                ? new JsonObject { ["__type"] = "ItemInfoResponseMessage:#Exchange", ["Items"] = new JsonArray(ItemJson(item, full: true, mime: mime)), ["ResponseClass"] = "Success", ["ResponseCode"] = "NoError" }
                : Error("ItemInfoResponseMessage", "ErrorItemNotFound", "The specified object was not found in the store."));
        }

        return Ok("GetItem", [.. messages]);
    }

    private HttpResponseMessage GetAttachment(JsonObject body)
    {
        if (body["AttachmentShape"] is JsonObject shape && shape["BodyType"] is not null)
        {
            return Text(400, """{"Body":{"ErrorCode":400,"ExceptionName":"OwaMethodArgumentException","FaultMessage":"Invalid argument used to call method GetAttachment","IsTransient":false,"ResponseCode":"BadRequest"}}""");
        }

        var messages = new List<JsonObject>();
        foreach (var asked in body["AttachmentIds"]?.AsArray() ?? [])
        {
            AttachmentsFetched++;
            if (!_attachments.TryGetValue(asked?["Id"]?.GetValue<string>() ?? "", out var found))
            {
                messages.Add(Error("AttachmentInfoResponseMessage", "ErrorAttachmentNotFound", "The attachment could not be found."));
                continue;
            }

            var (_, a) = found;
            JsonObject json = a.Embedded is { } embedded
                ? new JsonObject { ["__type"] = "ItemAttachment:#Exchange", ["AttachmentId"] = new JsonObject { ["Id"] = a.Id }, ["Name"] = a.Name, ["ContentType"] = a.ContentType, ["Item"] = ItemJson(embedded, full: true, mime: true) }
                : new JsonObject { ["__type"] = "FileAttachment:#Exchange", ["Content"] = Convert.ToBase64String(a.Content ?? []), ["AttachmentId"] = new JsonObject { ["Id"] = a.Id }, ["Name"] = a.Name, ["ContentType"] = a.ContentType };
            messages.Add(new JsonObject { ["__type"] = "AttachmentInfoResponseMessage:#Exchange", ["Attachments"] = new JsonArray(json), ["ResponseClass"] = "Success", ["ResponseCode"] = "NoError" });
        }

        return Ok("GetAttachment", [.. messages]);
    }

    private JsonObject SyncFolderItems(JsonObject body)
    {
        var folder = Resolve(body["SyncFolderId"]?["BaseFolderId"]);
        if (folder is null)
        {
            return Error("SyncFolderItemsResponseMessage", "ErrorFolderNotFound", "The specified folder could not be found in the store.");
        }

        var state = body["SyncState"]?.GetValue<string>();
        if (state is null)
        {
            FullSyncs++;
        }

        var since = 0;
        if (state is not null)
        {
            var parts = state.Split(':');
            if (parts.Length != 3 || parts[0] != "sync" || parts[1] != folder.Id || !int.TryParse(parts[2], out since))
            {
                return Error("SyncFolderItemsResponseMessage", "ErrorInvalidSyncStateData", "The sync state data is corrupt.");
            }
        }

        var changes = new JsonArray();
        var max = Math.Min(body["MaxChangesReturned"]?.GetValue<int>() ?? 100, MostChangesPerPage);
        var last = since;
        foreach (var (version, kind, itemId) in folder.Changes.Where(c => c.Version > since).Take(max))
        {
            last = version;
            if (kind == "Delete" || !_items.TryGetValue(itemId, out var item))
            {
                changes.Add(new JsonObject { ["__type"] = "SyncFolderItemsDeleteType:#Exchange", ["ItemId"] = new JsonObject { ["__type"] = "ItemId:#Exchange", ["Id"] = itemId }, ["ChangeType"] = "Delete" });
            }
            else
            {
                changes.Add(new JsonObject { ["__type"] = $"SyncFolderItems{kind}Type:#Exchange", ["Item"] = ItemJson(item, full: false, mime: false), ["ChangeType"] = kind });
            }
        }

        // as Outlook on the web answers: IncludesLastItemInRange true on the last page, left out on the others (26-Oct-2026)
        var includesLast = folder.Changes.All(c => c.Version <= last) || EveryPageSaysLast;
        var message = new JsonObject { ["__type"] = "SyncFolderItemsResponseMessage:#Exchange", ["SyncState"] = $"sync:{folder.Id}:{last}", ["TotalCount"] = 0, ["Changes"] = new JsonObject { ["Changes"] = changes }, ["ResponseCode"] = "NoError", ["ResponseClass"] = "Success" };
        if (includesLast)
        {
            message["IncludesLastItemInRange"] = true;
        }

        return message;
    }

    private Folder? Resolve(JsonNode? idNode)
    {
        if (idNode is null)
        {
            return null;
        }

        var id = idNode["Id"]?.GetValue<string>() ?? "";
        return idNode["__type"]?.GetValue<string>() == "DistinguishedFolderId:#Exchange"
            ? (id == "msgfolderroot" ? _folders.Values.Single(f => f.IsRoot) : _folders.Values.FirstOrDefault(f => f.Distinguished == id))
            : _folders.GetValueOrDefault(id);
    }

    private static JsonObject FolderJson(Folder f, Folder root)
    {
        var json = new JsonObject
        {
            ["__type"] = $"{f.Type}:#Exchange",
            ["FolderId"] = new JsonObject { ["__type"] = "FolderId:#Exchange", ["Id"] = f.Id, ["ChangeKey"] = "ck" },
            ["ParentFolderId"] = new JsonObject { ["__type"] = "FolderId:#Exchange", ["Id"] = f.ParentId ?? root.Id, ["ChangeKey"] = "ck" },
            ["FolderClass"] = f.FolderClass,
            ["DisplayName"] = f.Name,
            ["TotalCount"] = f.Count,
            ["ChildFolderCount"] = 0,
        };
        if (f.Type == "Folder")
        {
            json["UnreadCount"] = 0;
        }

        if (f.Distinguished is not null)
        {
            json["DistinguishedFolderId"] = f.Distinguished;
        }

        return json;
    }

    private static JsonObject ItemJson(Item i, bool full, bool mime)
    {
        var json = new JsonObject
        {
            ["__type"] = $"{i.Type}:#Exchange",
            ["ItemId"] = new JsonObject { ["__type"] = "ItemId:#Exchange", ["ChangeKey"] = "ck", ["Id"] = i.Id },
            ["ItemClass"] = i.ItemClass,
            ["Subject"] = i.Subject,
            ["Size"] = i.Size,
            ["HasAttachments"] = i.Attachments.Count > 0,
            ["LastModifiedTime"] = Stamp(i.Modified),
            ["DateTimeCreated"] = Stamp(i.Created),
        };
        if (i.Type is "Message" or "MeetingRequest")
        {
            json["DateTimeReceived"] = Stamp(i.Received);
            json["DateTimeSent"] = Stamp(i.Sent);
            json["IsRead"] = i.IsRead;
            json["IsDraft"] = false;
            json["Importance"] = "Normal";
            json["InternetMessageId"] = i.InternetMessageId;
            json["DisplayTo"] = string.Join("; ", i.To.Select(t => t.Name));
            json["DisplayCc"] = string.Join("; ", i.Cc.Select(t => t.Name));
            if (i.From is { } from)
            {
                json["From"] = new JsonObject { ["Mailbox"] = Mailbox(from) };
                json["Sender"] = new JsonObject { ["Mailbox"] = Mailbox(from) };
            }

            if (full)
            {
                json["ToRecipients"] = new JsonArray(i.To.Select(t => (JsonNode)Mailbox(t)).ToArray());
                json["CcRecipients"] = new JsonArray(i.Cc.Select(t => (JsonNode)Mailbox(t)).ToArray());
            }
        }
        if (i.Type is "CalendarItem" or "MeetingRequest")
        {
            json["Start"] = Stamp(i.Start);
            json["End"] = Stamp(i.End);
            json["IsAllDayEvent"] = false;
            json["Location"] = i.Location;
            json["IsRecurring"] = i.IsRecurring;
            json["CalendarItemType"] = i.CalendarItemType;
            json["UID"] = i.Uid;
            json["LegacyFreeBusyStatus"] = "Busy";
            if (i.Organizer is { } organizer)
            {
                json["Organizer"] = new JsonObject { ["Mailbox"] = Mailbox(organizer) };
            }

            if (full)
            {
                json["RequiredAttendees"] = new JsonArray(i.Attendees.Select(a => (JsonNode)new JsonObject { ["Mailbox"] = Mailbox(a.Who), ["ResponseType"] = a.Response }).ToArray());
            }
        }
        else if (i.Type == "Contact")
        {
            json["DisplayName"] = i.DisplayName;
            json["FileAs"] = i.DisplayName;
            json["GivenName"] = i.GivenName;
            json["Surname"] = i.Surname;
            json["CompanyName"] = i.Company;
            json["JobTitle"] = i.JobTitle;
            json["EmailAddresses"] = new JsonArray(new JsonObject { ["Key"] = "EmailAddress1", ["EmailAddress"] = i.Email, ["RoutingType"] = "SMTP" });
            json["PhoneNumbers"] = new JsonArray(new JsonObject { ["Key"] = "BusinessPhone", ["PhoneNumber"] = i.Phone });
        }

        if (full)
        {
            json["Body"] = new JsonObject { ["BodyType"] = "HTML", ["Value"] = i.BodyHtml ?? "", ["IsTruncated"] = false };
            json["Attachments"] = new JsonArray(i.Attachments.Select(a => (JsonNode)new JsonObject
            {
                ["__type"] = a.Embedded is null ? "FileAttachment:#Exchange" : "ItemAttachment:#Exchange",
                ["AttachmentId"] = new JsonObject { ["Id"] = a.Id },
                ["Name"] = a.Name,
                ["ContentType"] = a.ContentType,
                ["Size"] = a.Content is { } content ? content.Length + StoreOverhead : a.Embedded?.Size ?? 0,
                ["IsInline"] = a.IsInline,
                ["ContentId"] = a.ContentId,
            }).ToArray());
            if (mime && i.Mime is { } bytes)
            {
                json["MimeContent"] = new JsonObject { ["CharacterSet"] = "UTF-8", ["Value"] = Convert.ToBase64String(bytes) };
            }
        }

        return json;
    }

    private static JsonObject Mailbox((string Name, string Address) m) => new() { ["Name"] = m.Name, ["EmailAddress"] = m.Address, ["RoutingType"] = "SMTP", ["MailboxType"] = "Mailbox" };

    private static string? Stamp(DateTimeOffset? time) => time?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static JsonObject Error(string type, string code, string text) => new() { ["__type"] = $"{type}:#Exchange", ["ResponseClass"] = "Error", ["ResponseCode"] = code, ["MessageText"] = text };

    private static HttpResponseMessage Ok(string action, params JsonObject[] messages)
    {
        var envelope = new JsonObject
        {
            ["Header"] = new JsonObject { ["ServerVersionInfo"] = new JsonObject { ["MajorVersion"] = 15, ["MinorVersion"] = 21, ["Version"] = "V2018_01_08" } },
            ["Body"] = new JsonObject { ["__type"] = $"{action}Response:#Exchange", ["ResponseMessages"] = new JsonObject { ["Items"] = new JsonArray([.. messages]) } },
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private static HttpResponseMessage Text(int status, string text) => new((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    // --- the same mailbox as an Exchange server's EWS (SOAP) ------------------------------------------------------------

    private static readonly System.Xml.Linq.XNamespace S = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly System.Xml.Linq.XNamespace T = "http://schemas.microsoft.com/exchange/services/2006/types";
    private static readonly System.Xml.Linq.XNamespace M = "http://schemas.microsoft.com/exchange/services/2006/messages";

    private HttpResponseMessage ServeSoap(HttpRequestMessage request, string body)
    {
        System.Xml.Linq.XElement operation;
        try
        {
            operation = System.Xml.Linq.XDocument.Parse(body).Root?.Element(S + "Body")?.Elements().FirstOrDefault() ?? throw new InvalidOperationException("no operation");
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("not SOAP", Encoding.UTF8, "text/plain") };
        }

        var action = operation.Name.LocalName;
        lock (_gate)
        {
            Actions.Add(action);
        }

        if (Intercept?.Invoke(request, action) is { } intercepted)
        {
            return intercepted;
        }

        if (RefuseWindowsSignIn)
        {
            lock (_gate)
            {
                Refusals++;
            }

            var refused = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            refused.Headers.WwwAuthenticate.ParseAdd("Negotiate");
            refused.Headers.WwwAuthenticate.ParseAdd("NTLM");
            return refused;
        }

        lock (_gate)
        {
            object message = action switch
            {
                "FindFolder" => SoapFindFolder(),
                "FindItem" => SoapFindItem(operation),
                "GetItem" => SoapGetItem(operation),
                "GetAttachment" => SoapGetAttachment(operation),
                "SyncFolderItems" => SoapSyncFolderItems(operation),
                _ => SoapError($"{action}ResponseMessage", "ErrorInvalidRequest", $"{action} is not served by the fake"),
            };
            var envelope = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement(S + "Envelope",
                new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "s", S), new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "t", T), new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "m", M),
                new System.Xml.Linq.XElement(S + "Header", new System.Xml.Linq.XElement(T + "ServerVersionInfo", new System.Xml.Linq.XAttribute("MajorVersion", 15), new System.Xml.Linq.XAttribute("MinorVersion", 2), new System.Xml.Linq.XAttribute("Version", "V2017_07_11"))),
                new System.Xml.Linq.XElement(S + "Body", new System.Xml.Linq.XElement(M + $"{action}Response", new System.Xml.Linq.XElement(M + "ResponseMessages", message)))));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope.ToString(System.Xml.Linq.SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml") };
        }
    }

    private System.Xml.Linq.XElement SoapFindFolder()
    {
        var root = _folders.Values.Single(f => f.IsRoot);
        var list = _folders.Values.Where(f => !f.IsRoot).Select(f => FolderXml(f, root)).ToList();
        return new System.Xml.Linq.XElement(M + "FindFolderResponseMessage", new System.Xml.Linq.XAttribute("ResponseClass", "Success"), new System.Xml.Linq.XElement(M + "ResponseCode", "NoError"),
            new System.Xml.Linq.XElement(M + "RootFolder", new System.Xml.Linq.XAttribute("IndexedPagingOffset", list.Count), new System.Xml.Linq.XAttribute("TotalItemsInView", list.Count), new System.Xml.Linq.XAttribute("IncludesLastItemInRange", "true"), new System.Xml.Linq.XElement(T + "Folders", list)));
    }

    private System.Xml.Linq.XElement SoapFindItem(System.Xml.Linq.XElement operation)
    {
        var folder = ResolveXml(operation.Element(M + "ParentFolderIds")?.Elements().FirstOrDefault());
        if (folder is null)
        {
            return SoapError("FindItemResponseMessage", "ErrorFolderNotFound", "The specified folder could not be found in the store.");
        }

        var items = _items.Values.Where(i => i.FolderId == folder.Id).ToList();
        if (operation.Element(M + "CalendarView") is { } view)
        {
            var from = DateTimeOffset.Parse((string)view.Attribute("StartDate")!, CultureInfo.InvariantCulture);
            var to = DateTimeOffset.Parse((string)view.Attribute("EndDate")!, CultureInfo.InvariantCulture);
            if (to - from > TimeSpan.FromDays(731))
            {
                return SoapError("FindItemResponseMessage", "ErrorCalendarViewRangeTooBig", "The specified view range exceeds the maximum range of two years.");
            }

            var inRange = items.Where(i => i.Start is { } s && s >= from && s < to).OrderBy(i => i.Start).ToList();
            return new System.Xml.Linq.XElement(M + "FindItemResponseMessage", new System.Xml.Linq.XAttribute("ResponseClass", "Success"), new System.Xml.Linq.XElement(M + "ResponseCode", "NoError"),
                new System.Xml.Linq.XElement(M + "RootFolder", new System.Xml.Linq.XAttribute("TotalItemsInView", inRange.Count), new System.Xml.Linq.XAttribute("IncludesLastItemInRange", "true"), new System.Xml.Linq.XElement(T + "Items", inRange.Select(i => ItemXml(i, full: false, mime: false)))));
        }

        var paging = operation.Element(M + "IndexedPageItemView");
        var offset = int.TryParse((string?)paging?.Attribute("Offset"), out var o) ? o : 0;
        var max = int.TryParse((string?)paging?.Attribute("MaxEntriesReturned"), out var m) ? m : 1000;
        var ordered = items.OrderByDescending(i => i.Received ?? i.Created ?? DateTimeOffset.MinValue).ToList();
        var page = ordered.Skip(offset).Take(max).ToList();
        return new System.Xml.Linq.XElement(M + "FindItemResponseMessage", new System.Xml.Linq.XAttribute("ResponseClass", "Success"), new System.Xml.Linq.XElement(M + "ResponseCode", "NoError"),
            new System.Xml.Linq.XElement(M + "RootFolder", new System.Xml.Linq.XAttribute("IndexedPagingOffset", offset + page.Count), new System.Xml.Linq.XAttribute("TotalItemsInView", ordered.Count), new System.Xml.Linq.XAttribute("IncludesLastItemInRange", offset + page.Count >= ordered.Count ? "true" : "false"),
                new System.Xml.Linq.XElement(T + "Items", page.Select(i => ItemXml(i, full: false, mime: false)))));
    }

    /// <summary>One response message per id asked for, in order, as Exchange answers.</summary>
    private List<System.Xml.Linq.XElement> SoapGetItem(System.Xml.Linq.XElement operation)
    {
        var mime = string.Equals(operation.Element(M + "ItemShape")?.Element(T + "IncludeMimeContent")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        var messages = new List<System.Xml.Linq.XElement>();
        ItemsPerRequest.Add(operation.Element(M + "ItemIds")?.Elements(T + "ItemId").Count() ?? 0);
        foreach (var asked in operation.Element(M + "ItemIds")?.Elements(T + "ItemId") ?? [])
        {
            ItemsFetched++;
            messages.Add(_items.TryGetValue((string?)asked.Attribute("Id") ?? "", out var item)
                ? new System.Xml.Linq.XElement(M + "GetItemResponseMessage", new System.Xml.Linq.XAttribute("ResponseClass", "Success"), new System.Xml.Linq.XElement(M + "ResponseCode", "NoError"),
                    new System.Xml.Linq.XElement(M + "Items", ItemXml(item, full: true, mime: mime)))
                : SoapError("GetItemResponseMessage", "ErrorItemNotFound", "The specified object was not found in the store."));
        }

        return messages;
    }

    private List<System.Xml.Linq.XElement> SoapGetAttachment(System.Xml.Linq.XElement operation)
    {
        var messages = new List<System.Xml.Linq.XElement>();
        foreach (var asked in operation.Element(M + "AttachmentIds")?.Elements(T + "AttachmentId") ?? [])
        {
            AttachmentsFetched++;
            if (!_attachments.TryGetValue((string?)asked.Attribute("Id") ?? "", out var found))
            {
                messages.Add(SoapError("GetAttachmentResponseMessage", "ErrorAttachmentNotFound", "The attachment could not be found."));
                continue;
            }

            var (_, a) = found;
            var xml = a.Embedded is { } embedded
                ? new System.Xml.Linq.XElement(T + "ItemAttachment", new System.Xml.Linq.XElement(T + "AttachmentId", new System.Xml.Linq.XAttribute("Id", a.Id)), new System.Xml.Linq.XElement(T + "Name", a.Name), new System.Xml.Linq.XElement(T + "ContentType", a.ContentType), ItemXml(embedded, full: true, mime: true))
                : new System.Xml.Linq.XElement(T + "FileAttachment", new System.Xml.Linq.XElement(T + "AttachmentId", new System.Xml.Linq.XAttribute("Id", a.Id)), new System.Xml.Linq.XElement(T + "Name", a.Name), new System.Xml.Linq.XElement(T + "ContentType", a.ContentType), new System.Xml.Linq.XElement(T + "Content", Convert.ToBase64String(a.Content ?? [])));
            messages.Add(new System.Xml.Linq.XElement(M + "GetAttachmentResponseMessage", new System.Xml.Linq.XAttribute("ResponseClass", "Success"), new System.Xml.Linq.XElement(M + "ResponseCode", "NoError"), new System.Xml.Linq.XElement(M + "Attachments", xml)));
        }

        return messages;
    }

    private System.Xml.Linq.XElement SoapSyncFolderItems(System.Xml.Linq.XElement operation)
    {
        var folder = ResolveXml(operation.Element(M + "SyncFolderId")?.Elements().FirstOrDefault());
        if (folder is null)
        {
            return SoapError("SyncFolderItemsResponseMessage", "ErrorFolderNotFound", "The specified folder could not be found in the store.");
        }

        var state = operation.Element(M + "SyncState")?.Value;
        if (string.IsNullOrEmpty(state))
        {
            FullSyncs++;
        }

        var since = 0;
        if (!string.IsNullOrEmpty(state))
        {
            var parts = state.Split(':');
            if (parts.Length != 3 || parts[0] != "sync" || parts[1] != folder.Id || !int.TryParse(parts[2], out since))
            {
                return SoapError("SyncFolderItemsResponseMessage", "ErrorInvalidSyncStateData", "The sync state data is corrupt.");
            }
        }

        var max = int.TryParse(operation.Element(M + "MaxChangesReturned")?.Value, out var n) ? n : 100;
        var changes = new System.Xml.Linq.XElement(M + "Changes");
        var last = since;
        foreach (var (version, kind, itemId) in folder.Changes.Where(c => c.Version > since).Take(max))
        {
            last = version;
            if (kind == "Delete" || !_items.TryGetValue(itemId, out var item))
            {
                changes.Add(new System.Xml.Linq.XElement(T + "Delete", new System.Xml.Linq.XElement(T + "ItemId", new System.Xml.Linq.XAttribute("Id", itemId))));
            }
            else
            {
                changes.Add(new System.Xml.Linq.XElement(T + kind, ItemXml(item, full: false, mime: false)));
            }
        }

        return new System.Xml.Linq.XElement(M + "SyncFolderItemsResponseMessage", new System.Xml.Linq.XAttribute("ResponseClass", "Success"), new System.Xml.Linq.XElement(M + "ResponseCode", "NoError"),
            new System.Xml.Linq.XElement(M + "SyncState", $"sync:{folder.Id}:{last}"), new System.Xml.Linq.XElement(M + "IncludesLastItemInRange", folder.Changes.All(c => c.Version <= last) ? "true" : "false"), changes);
    }

    private Folder? ResolveXml(System.Xml.Linq.XElement? idNode)
    {
        if (idNode is null)
        {
            return null;
        }

        var id = (string?)idNode.Attribute("Id") ?? "";
        return idNode.Name == T + "DistinguishedFolderId"
            ? (id == "msgfolderroot" ? _folders.Values.Single(f => f.IsRoot) : _folders.Values.FirstOrDefault(f => f.Distinguished == id))
            : _folders.GetValueOrDefault(id);
    }

    private static System.Xml.Linq.XElement SoapError(string type, string code, string text)
        => new(M + type, new System.Xml.Linq.XAttribute("ResponseClass", "Error"), new System.Xml.Linq.XElement(M + "MessageText", text), new System.Xml.Linq.XElement(M + "ResponseCode", code));

    private static System.Xml.Linq.XElement FolderXml(Folder f, Folder root)
    {
        var xml = new System.Xml.Linq.XElement(T + f.Type,
            new System.Xml.Linq.XElement(T + "FolderId", new System.Xml.Linq.XAttribute("Id", f.Id), new System.Xml.Linq.XAttribute("ChangeKey", "ck")),
            new System.Xml.Linq.XElement(T + "ParentFolderId", new System.Xml.Linq.XAttribute("Id", f.ParentId ?? root.Id), new System.Xml.Linq.XAttribute("ChangeKey", "ck")),
            new System.Xml.Linq.XElement(T + "FolderClass", f.FolderClass),
            new System.Xml.Linq.XElement(T + "DisplayName", f.Name),
            new System.Xml.Linq.XElement(T + "TotalCount", f.Count),
            new System.Xml.Linq.XElement(T + "ChildFolderCount", 0));
        if (f.Type == "Folder")
        {
            xml.Add(new System.Xml.Linq.XElement(T + "UnreadCount", 0));
        }

        if (f.Distinguished is not null)
        {
            xml.Add(new System.Xml.Linq.XElement(T + "DistinguishedFolderId", f.Distinguished));
        }

        return xml;
    }

    private static System.Xml.Linq.XElement ItemXml(Item i, bool full, bool mime)
    {
        var xml = new System.Xml.Linq.XElement(T + i.Type);
        if (mime && i.Mime is { } bytes)
        {
            xml.Add(new System.Xml.Linq.XElement(T + "MimeContent", new System.Xml.Linq.XAttribute("CharacterSet", "UTF-8"), Convert.ToBase64String(bytes)));
        }

        xml.Add(new System.Xml.Linq.XElement(T + "ItemId", new System.Xml.Linq.XAttribute("Id", i.Id), new System.Xml.Linq.XAttribute("ChangeKey", "ck")),
            new System.Xml.Linq.XElement(T + "ItemClass", i.ItemClass),
            new System.Xml.Linq.XElement(T + "Subject", i.Subject),
            new System.Xml.Linq.XElement(T + "Size", i.Size),
            new System.Xml.Linq.XElement(T + "HasAttachments", i.Attachments.Count > 0 ? "true" : "false"),
            new System.Xml.Linq.XElement(T + "LastModifiedTime", Stamp(i.Modified)),
            new System.Xml.Linq.XElement(T + "DateTimeCreated", Stamp(i.Created)));
        if (full)
        {
            xml.Add(new System.Xml.Linq.XElement(T + "Body", new System.Xml.Linq.XAttribute("BodyType", "HTML"), i.BodyHtml ?? ""));
            xml.Add(new System.Xml.Linq.XElement(T + "Attachments", i.Attachments.Select(a => new System.Xml.Linq.XElement(T + (a.Embedded is null ? "FileAttachment" : "ItemAttachment"),
                new System.Xml.Linq.XElement(T + "AttachmentId", new System.Xml.Linq.XAttribute("Id", a.Id)),
                new System.Xml.Linq.XElement(T + "Name", a.Name),
                new System.Xml.Linq.XElement(T + "ContentType", a.ContentType),
                a.ContentId is null ? null : new System.Xml.Linq.XElement(T + "ContentId", a.ContentId),
                new System.Xml.Linq.XElement(T + "Size", a.Content is { } content ? content.Length + StoreOverhead : a.Embedded?.Size ?? 0),
                new System.Xml.Linq.XElement(T + "IsInline", a.IsInline ? "true" : "false")))));
        }

        if (i.Type is "Message" or "MeetingRequest")
        {
            xml.Add(new System.Xml.Linq.XElement(T + "DateTimeReceived", Stamp(i.Received)), new System.Xml.Linq.XElement(T + "DateTimeSent", Stamp(i.Sent)),
                new System.Xml.Linq.XElement(T + "IsRead", i.IsRead ? "true" : "false"), new System.Xml.Linq.XElement(T + "IsDraft", "false"), new System.Xml.Linq.XElement(T + "Importance", "Normal"),
                new System.Xml.Linq.XElement(T + "InternetMessageId", i.InternetMessageId), new System.Xml.Linq.XElement(T + "DisplayTo", string.Join("; ", i.To.Select(t => t.Name))), new System.Xml.Linq.XElement(T + "DisplayCc", string.Join("; ", i.Cc.Select(t => t.Name))));
            if (i.From is { } from)
            {
                xml.Add(new System.Xml.Linq.XElement(T + "From", MailboxXml(from)), new System.Xml.Linq.XElement(T + "Sender", MailboxXml(from)));
            }

            if (full)
            {
                xml.Add(new System.Xml.Linq.XElement(T + "ToRecipients", i.To.Select(MailboxXml)), new System.Xml.Linq.XElement(T + "CcRecipients", i.Cc.Select(MailboxXml)));
            }
        }
        if (i.Type is "CalendarItem" or "MeetingRequest")
        {
            xml.Add(new System.Xml.Linq.XElement(T + "UID", i.Uid), new System.Xml.Linq.XElement(T + "Start", Stamp(i.Start)), new System.Xml.Linq.XElement(T + "End", Stamp(i.End)), new System.Xml.Linq.XElement(T + "IsAllDayEvent", "false"),
                new System.Xml.Linq.XElement(T + "LegacyFreeBusyStatus", "Busy"), new System.Xml.Linq.XElement(T + "Location", i.Location), new System.Xml.Linq.XElement(T + "IsRecurring", i.IsRecurring ? "true" : "false"), new System.Xml.Linq.XElement(T + "CalendarItemType", i.CalendarItemType));
            if (i.Organizer is { } organizer)
            {
                xml.Add(new System.Xml.Linq.XElement(T + "Organizer", MailboxXml(organizer)));
            }

            if (full)
            {
                xml.Add(new System.Xml.Linq.XElement(T + "RequiredAttendees", i.Attendees.Select(a => new System.Xml.Linq.XElement(T + "Attendee", MailboxXml(a.Who), new System.Xml.Linq.XElement(T + "ResponseType", a.Response)))));
            }
        }
        else if (i.Type == "Contact")
        {
            xml.Add(new System.Xml.Linq.XElement(T + "FileAs", i.DisplayName), new System.Xml.Linq.XElement(T + "DisplayName", i.DisplayName), new System.Xml.Linq.XElement(T + "GivenName", i.GivenName),
                new System.Xml.Linq.XElement(T + "CompanyName", i.Company),
                new System.Xml.Linq.XElement(T + "EmailAddresses", new System.Xml.Linq.XElement(T + "Entry", new System.Xml.Linq.XAttribute("Key", "EmailAddress1"), i.Email)),
                new System.Xml.Linq.XElement(T + "PhoneNumbers", new System.Xml.Linq.XElement(T + "Entry", new System.Xml.Linq.XAttribute("Key", "BusinessPhone"), i.Phone)),
                new System.Xml.Linq.XElement(T + "JobTitle", i.JobTitle), new System.Xml.Linq.XElement(T + "Surname", i.Surname));
        }

        return xml;
    }

    private static System.Xml.Linq.XElement MailboxXml((string Name, string Address) m)
        => new(T + "Mailbox", new System.Xml.Linq.XElement(T + "Name", m.Name), new System.Xml.Linq.XElement(T + "EmailAddress", m.Address), new System.Xml.Linq.XElement(T + "RoutingType", "SMTP"), new System.Xml.Linq.XElement(T + "MailboxType", "Mailbox"));

    // --- the model --------------------------------------------------------------------------------------------------------

    internal sealed class Folder(string id, string name, string folderClass, string? parentId, string? distinguished, string type, bool isRoot)
    {
        private int _version;

        public string Id => id;

        public string Name => name;

        public string FolderClass => folderClass;

        public string? ParentId => parentId;

        public string? Distinguished => distinguished;

        public string Type => type;

        public bool IsRoot => isRoot;

        public List<(int Version, string Kind, string ItemId)> Changes { get; } = [];

        public int Count => Changes.Count(c => c.Kind == "Create") - Changes.Count(c => c.Kind == "Delete");

        public void Log(string kind, string itemId) => Changes.Add((++_version, kind, itemId));
    }

    internal sealed class Item(string id, string folderId, string type, string itemClass)
    {
        public string Id => id;

        public string FolderId => folderId;

        public string Type => type;

        public string ItemClass => itemClass;

        public string? Subject { get; set; }

        public (string Name, string Address)? From { get; set; }

        public List<(string Name, string Address)> To { get; set; } = [];

        public List<(string Name, string Address)> Cc { get; set; } = [];

        public DateTimeOffset? Received { get; set; }

        public DateTimeOffset? Sent { get; set; }

        public DateTimeOffset? Created { get; set; }

        public DateTimeOffset? Modified { get; set; }

        public long Size { get; set; }

        public bool IsRead { get; set; }

        public string? InternetMessageId { get; set; }

        public string? BodyHtml { get; set; }

        public byte[]? Mime { get; set; }

        public List<Attachment> Attachments { get; } = [];

        public DateTimeOffset? Start { get; set; }

        public DateTimeOffset? End { get; set; }

        public string? Location { get; set; }

        public (string Name, string Address)? Organizer { get; set; }

        public List<((string Name, string Address) Who, string Response)> Attendees { get; } = [];

        public bool IsRecurring { get; set; }

        public string CalendarItemType { get; set; } = "Single";

        public string? Uid { get; set; }

        public string? DisplayName { get; set; }

        public string? GivenName { get; set; }

        public string? Surname { get; set; }

        public string? Email { get; set; }

        public string? Company { get; set; }

        public string? JobTitle { get; set; }

        public string? Phone { get; set; }
    }

    internal sealed class Attachment(string name, string contentType, byte[]? content, bool isInline, string? contentId)
    {
        public string Id { get; set; } = "";

        public string Name => name;

        public string ContentType => contentType;

        public byte[]? Content => content;

        public bool IsInline => isInline;

        public string? ContentId => contentId;

        public Item? Embedded { get; init; }
    }

    private sealed class InProcessHandler(FakeExchange fake) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var response = fake.Serve(request, body);
            response.RequestMessage = request;
            return response;
        }
    }
}
