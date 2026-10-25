using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AnyFS.Sources.Microsoft365;
using AnyFS.Sources.Outlook.Ews;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The same mailbox through Microsoft Graph, for the Outlook source's app sign-in (GraphEwsService), in the shapes Spike
/// 15 recorded (25-Oct-2026): mail folders a level at a time and by their well-known names, the calendars, the contact
/// folders (the default one by its well-known name), messages' and contacts' deltas paged by the Prefer header's
/// maxpagesize with skip and delta tokens (a token from before <see cref="ForgetDeltas"/> is a 400, as a spoilt one
/// was), a calendar's view, an item with its class and size as extended properties, attachments listed without their
/// bytes (the content id only through the cast Graph needs), $value for MIME and content, an attached item by $expand.
/// Attachment sizes carry the store's overhead, as Graph's do.
/// </summary>
internal sealed partial class FakeExchange
{
    public const string GraphHost = "graph.microsoft.com";

    private const string GraphRoot = "https://graph.microsoft.com/v1.0/";

    private int _deltaEpoch;

    static FakeExchange()
    {
        // the fake need not be spared as Exchange Online must: every test's source goes at its own speed
        RequestPace.Use(GraphEwsService.PaceKey(MicrosoftCloud.Global, Account), new RequestPace(burst: 1_000_000, perSecond: RequestPace.PerSecond));
    }

    /// <summary>The most a delta page holds whatever the Prefer header asks (Graph sends ten when not asked), so that every test pages.</summary>
    public int GraphPageSize { get; set; } = 2;

    /// <summary>Every Graph request's Prefer header, in order.</summary>
    public List<string> GraphPreferences { get; } = [];

    /// <summary>From now on the delta links handed out so far are refused, as Graph refuses one it no longer keeps.</summary>
    public void ForgetDeltas()
    {
        lock (_gate)
        {
            _deltaEpoch++;
        }
    }

    private HttpResponseMessage ServeGraph(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        var path = Uri.UnescapeDataString(uri.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal) ? uri.AbsolutePath["/v1.0/".Length..] : uri.AbsolutePath.TrimStart('/'));
        var action = "graph " + path;
        var prefer = request.Headers.TryGetValues("Prefer", out var values) ? string.Join(", ", values) : "";
        lock (_gate)
        {
            Actions.Add(action);
            GraphPreferences.Add(prefer);
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

            var refused = GraphError(401, "InvalidAuthenticationToken", "Lifetime validation failed, the token is expired.");
            refused.Headers.WwwAuthenticate.ParseAdd("Bearer");
            return refused;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var size = prefer.Split(',').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("odata.maxpagesize=", StringComparison.Ordinal)) is { } max && int.TryParse(max["odata.maxpagesize=".Length..], out var n) ? n : 100;
        lock (_gate)
        {
            return path.Split('/') switch
            {
                ["me", "mailFolders"] => Graph(Collection(MailFolders(Root.Id, query))),
                ["me", "mailFolders", var id] => GraphMailFolder(id),
                ["me", "mailFolders", var id, "childFolders"] => Graph(Collection(MailFolders(id, query))),
                ["me", "mailFolders", var id, "messages", "delta"] => GraphDelta($"me/mailFolders/{id}/messages/delta", id, query, size),
                ["me", "calendars"] => Graph(Collection(_folders.Values.Where(f => f.FolderClass.StartsWith("IPF.Appointment", StringComparison.Ordinal)).Select(f => (JsonNode)new JsonObject
                {
                    ["id"] = f.Id, ["name"] = f.Name, ["isDefaultCalendar"] = f.Distinguished == "calendar", ["canEdit"] = true,
                    ["owner"] = new JsonObject { ["name"] = "Jane", ["address"] = Account },
                }))),
                ["me", "calendars", var id, "calendarView"] => GraphCalendarView(id, query),
                ["me", "contactFolders", "contacts"] => Graph(ContactFolderJson(ContactsFolder)),
                ["me", "contactFolders"] => Graph(Collection(ContactFolders(ContactsFolder.Id))),
                ["me", "contactFolders", var id, "childFolders"] => Graph(Collection(ContactFolders(id))),
                ["me", "contactFolders", var id, "contacts", "delta"] => GraphDelta($"me/contactFolders/{id}/contacts/delta", id, query, size),
                ["me", "messages" or "events" or "contacts", var id] => GraphItem(path.Split('/')[1], id, query),
                ["me", "messages", var id, "$value"] => _items.TryGetValue(id, out var m) && m.Mime is { } mime ? Bytes(mime, "text/plain") : GraphError(404, "ErrorItemNotFound", "The specified object was not found in the store."),
                ["me", "messages" or "events", var id, "attachments"] => GraphAttachments(id, query),
                ["me", "messages" or "events", var id, "attachments", var aid] => GraphAttachment(id, aid, query),
                ["me", "messages" or "events", var id, "attachments", var aid, "$value"] => GraphAttachmentValue(id, aid),
                _ => GraphError(400, "BadRequest", $"Resource not found for the segment '{path}'."),
            };
        }
    }

    private Folder Root => _folders.Values.Single(f => f.IsRoot);

    private Folder ContactsFolder => _folders.Values.Single(f => f.Distinguished == "contacts");

    // --- folders ----------------------------------------------------------------------------------------------------------

    /// <summary>A folder's mail folders: Graph's mailFolders hold mail only, Outlook's own folders (settings, feeds) only when the hidden ones are asked for.</summary>
    private IEnumerable<JsonNode> MailFolders(string parentId, System.Collections.Specialized.NameValueCollection query)
    {
        var hidden = string.Equals(query["includeHiddenFolders"], "true", StringComparison.OrdinalIgnoreCase);
        return _folders.Values.Where(f => f.ParentId == parentId && IsMailFolder(f, hidden)).Select(f => (JsonNode)MailFolderJson(f, hidden));
    }

    private static bool IsMailFolder(Folder f, bool hidden)
        => !f.IsRoot && (f.FolderClass.StartsWith("IPF.Note", StringComparison.Ordinal) || hidden && f.FolderClass is "IPF" or "IPF.Configuration");

    private JsonObject MailFolderJson(Folder f, bool hidden) => new()
    {
        ["id"] = f.Id,
        ["displayName"] = f.Name,
        ["parentFolderId"] = f.ParentId ?? Root.Id,
        ["childFolderCount"] = _folders.Values.Count(c => c.ParentId == f.Id && IsMailFolder(c, hidden)),
        ["totalItemCount"] = f.Count,
        ["unreadItemCount"] = 0,
        ["isHidden"] = !f.FolderClass.StartsWith("IPF.Note", StringComparison.Ordinal),
    };

    /// <summary>A mail folder by its id or its well-known name (inbox, sentitems, msgfolderroot...).</summary>
    private HttpResponseMessage GraphMailFolder(string id)
    {
        var folder = id == "msgfolderroot" ? Root : _folders.GetValueOrDefault(id) ?? _folders.Values.FirstOrDefault(f => f.Distinguished == id);
        return folder is null || !folder.IsRoot && !IsMailFolder(folder, hidden: true)
            ? GraphError(404, "ErrorItemNotFound", "The specified object was not found in the store.")
            : Graph(MailFolderJson(folder, hidden: false));
    }

    /// <summary>The contact folders below one: contacts' own, not Outlook's people caches.</summary>
    private IEnumerable<JsonNode> ContactFolders(string parentId)
        => _folders.Values.Where(f => f.ParentId == parentId && f.FolderClass == "IPF.Contact").Select(f => (JsonNode)ContactFolderJson(f));

    private static JsonObject ContactFolderJson(Folder f) => new() { ["id"] = f.Id, ["displayName"] = f.Name, ["parentFolderId"] = f.ParentId };

    // --- items ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A folder's changes from where a token says, a page at a time: a skiptoken carries on a round, a deltatoken starts
    /// the next; both are "epoch.version", and one of an earlier epoch is refused as Graph refuses a spoilt one.
    /// </summary>
    private HttpResponseMessage GraphDelta(string self, string folderId, System.Collections.Specialized.NameValueCollection query, int size)
    {
        if (!_folders.TryGetValue(folderId, out var folder))
        {
            return GraphError(404, "ErrorItemNotFound", "The specified object was not found in the store.");
        }

        if ((query["$skiptoken"] ?? query["$deltatoken"]) is null)
        {
            FullSyncs++;
        }

        var since = 0;
        if ((query["$skiptoken"] ?? query["$deltatoken"]) is { } token)
        {
            var parts = token.Split('.');
            if (parts.Length != 2 || parts[0] != _deltaEpoch.ToString(CultureInfo.InvariantCulture) || !int.TryParse(parts[1], out since))
            {
                return GraphError(400, "BadRequest", "Badly formed token.");
            }
        }

        var page = folder.Changes.Where(c => c.Version > since).Take(Math.Min(size, GraphPageSize)).ToList();
        var last = page.Count > 0 ? page[^1].Version : since;
        var value = new JsonArray();
        foreach (var (_, kind, itemId) in page)
        {
            value.Add(kind == "Delete" || !_items.TryGetValue(itemId, out var item)
                ? new JsonObject { ["@odata.type"] = folder.Type == "ContactsFolder" ? "#microsoft.graph.contact" : "#microsoft.graph.message", ["id"] = itemId, ["@removed"] = new JsonObject { ["reason"] = "deleted" } }
                : item.Type == "Contact" ? GraphContactJson(item, full: false) : GraphMessageJson(item, full: false, query: null));
        }

        var json = new JsonObject { ["@odata.context"] = GraphRoot + "$metadata#Collection(message)", ["value"] = value };
        var link = $"{GraphRoot}{self}?{(folder.Changes.Any(c => c.Version > last) ? "$skiptoken" : "$deltatoken")}={_deltaEpoch}.{last}";
        json[folder.Changes.Any(c => c.Version > last) ? "@odata.nextLink" : "@odata.deltaLink"] = link;
        return Graph(json);
    }

    private HttpResponseMessage GraphCalendarView(string calendarId, System.Collections.Specialized.NameValueCollection query)
    {
        if (!_folders.ContainsKey(calendarId) || query["startDateTime"] is not { } start || query["endDateTime"] is not { } end)
        {
            return GraphError(400, "ErrorInvalidParameter", "This request requires a time window specified by the query string parameters StartDateTime and EndDateTime.");
        }

        var from = DateTimeOffset.Parse(start, CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse(end, CultureInfo.InvariantCulture);
        var events = _items.Values.Where(i => i.FolderId == calendarId && i.Start is { } s && s >= from && s < to).OrderBy(i => i.Start).Select(i => (JsonNode)GraphEventJson(i, full: false, query: null));
        return Graph(Collection(events));
    }

    /// <summary>An item in full, from its collection's path: a message, an event, a contact; another kind's id is not found there.</summary>
    private HttpResponseMessage GraphItem(string collection, string id, System.Collections.Specialized.NameValueCollection query)
    {
        var type = collection switch { "messages" => "Message", "events" => "CalendarItem", _ => "Contact" };
        if (!_items.TryGetValue(id, out var item) || item.Type != type)
        {
            return GraphError(404, "ErrorItemNotFound", "The specified object was not found in the store.");
        }

        return Graph(type switch
        {
            "Message" => GraphMessageJson(item, full: true, query),
            "CalendarItem" => GraphEventJson(item, full: true, query),
            _ => GraphContactJson(item, full: true, query),
        });
    }

    private static JsonObject GraphMessageJson(Item i, bool full, System.Collections.Specialized.NameValueCollection? query)
    {
        var json = new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.message",
            ["@odata.etag"] = "W/\"ck\"",
            ["id"] = i.Id,
            ["changeKey"] = "ck",
            ["parentFolderId"] = i.FolderId,
            ["subject"] = i.Subject,
            ["receivedDateTime"] = Stamp(i.Received),
            ["sentDateTime"] = Stamp(i.Sent),
            ["createdDateTime"] = Stamp(i.Created),
            ["lastModifiedDateTime"] = Stamp(i.Modified),
            ["hasAttachments"] = i.Attachments.Any(a => !a.IsInline),
            ["isRead"] = i.IsRead,
            ["isDraft"] = false,
            ["importance"] = "normal",
            ["internetMessageId"] = i.InternetMessageId,
            ["toRecipients"] = new JsonArray(i.To.Select(t => (JsonNode)Who(t)).ToArray()),
        };
        if (i.From is { } from)
        {
            json["from"] = Who(from);
            json["sender"] = Who(from);
        }

        if (full)
        {
            json["ccRecipients"] = new JsonArray(i.Cc.Select(t => (JsonNode)Who(t)).ToArray());
            json["bccRecipients"] = new JsonArray();
            json["body"] = new JsonObject { ["contentType"] = "html", ["content"] = i.BodyHtml ?? "" };
            Extend(json, i, query);
        }

        return json;
    }

    private static JsonObject GraphEventJson(Item i, bool full, System.Collections.Specialized.NameValueCollection? query)
    {
        var json = new JsonObject
        {
            ["@odata.etag"] = "W/\"ck\"",
            ["id"] = i.Id,
            ["subject"] = i.Subject,
            ["start"] = Zoned(i.Start),
            ["end"] = Zoned(i.End),
            ["isAllDay"] = false,
            ["location"] = new JsonObject { ["displayName"] = i.Location },
            ["type"] = i.CalendarItemType == "Occurrence" ? "occurrence" : "singleInstance",
            ["iCalUId"] = i.Uid,
            ["showAs"] = "busy",
            ["createdDateTime"] = Stamp(i.Created),
            ["lastModifiedDateTime"] = Stamp(i.Modified),
            ["hasAttachments"] = false,
        };
        if (i.Organizer is { } organizer)
        {
            json["organizer"] = Who(organizer);
        }

        if (full)
        {
            json["attendees"] = new JsonArray(i.Attendees.Select(a => (JsonNode)new JsonObject
            {
                ["type"] = "required",
                ["status"] = new JsonObject { ["response"] = a.Response switch { "Accept" => "accepted", "Decline" => "declined", "Tentative" => "tentativelyAccepted", _ => "none" }, ["time"] = "0001-01-01T00:00:00Z" },
                ["emailAddress"] = new JsonObject { ["name"] = a.Who.Name, ["address"] = a.Who.Address },
            }).ToArray());
            json["body"] = new JsonObject { ["contentType"] = "html", ["content"] = i.BodyHtml ?? "" };
            Extend(json, i, query);
        }

        return json;
    }

    private static JsonObject GraphContactJson(Item i, bool full, System.Collections.Specialized.NameValueCollection? query = null)
    {
        var json = new JsonObject
        {
            ["@odata.etag"] = "W/\"ck\"",
            ["id"] = i.Id,
            ["parentFolderId"] = i.FolderId,
            ["displayName"] = i.DisplayName,
            ["fileAs"] = "",
            ["givenName"] = i.GivenName,
            ["surname"] = i.Surname,
            ["companyName"] = i.Company,
            ["createdDateTime"] = Stamp(i.Created),
            ["lastModifiedDateTime"] = Stamp(i.Modified),
        };
        if (full)
        {
            json["jobTitle"] = i.JobTitle;
            json["emailAddresses"] = new JsonArray(new JsonObject { ["name"] = i.DisplayName, ["address"] = i.Email });
            json["businessPhones"] = new JsonArray(i.Phone);
            json["homePhones"] = new JsonArray();
            json["mobilePhone"] = null;
            json["businessAddress"] = new JsonObject();
            json["homeAddress"] = new JsonObject();
            json["otherAddress"] = new JsonObject();
            json["imAddresses"] = new JsonArray();
            json["birthday"] = null;
            json["personalNotes"] = "";
            Extend(json, i, query);
        }

        return json;
    }

    /// <summary>The message class and size as Graph gives them when the $expand asks: "String 0x1a", "Integer 0xe08".</summary>
    private static void Extend(JsonObject json, Item i, System.Collections.Specialized.NameValueCollection? query)
    {
        if (query?["$expand"]?.Contains("singleValueExtendedProperties", StringComparison.Ordinal) == true)
        {
            json["singleValueExtendedProperties"] = new JsonArray(
                new JsonObject { ["id"] = "String 0x1a", ["value"] = i.ItemClass },
                new JsonObject { ["id"] = "Integer 0xe08", ["value"] = i.Size.ToString(CultureInfo.InvariantCulture) });
        }
    }

    // --- attachments ------------------------------------------------------------------------------------------------------

    /// <summary>An item's attachments: no bytes when a $select leaves them out; a file's content id only through the cast Graph needs for it (a bare contentId is refused).</summary>
    private HttpResponseMessage GraphAttachments(string itemId, System.Collections.Specialized.NameValueCollection query)
    {
        if (!_items.TryGetValue(itemId, out var item))
        {
            return GraphError(404, "ErrorItemNotFound", "The specified object was not found in the store.");
        }

        var select = query["$select"]?.Split(',').Select(s => s.Trim()).ToHashSet(StringComparer.Ordinal);
        if (select?.Contains("contentId") == true)
        {
            return GraphError(400, "RequestBroker--ParseUri", "Could not find a property named 'contentId' on type 'microsoft.graph.attachment'.");
        }

        return Graph(Collection(item.Attachments.Select(a => (JsonNode)GraphAttachmentJson(a, select, expand: false))));
    }

    private static JsonObject GraphAttachmentJson(Attachment a, HashSet<string>? select, bool expand)
    {
        var json = new JsonObject
        {
            ["@odata.type"] = a.Embedded is null ? "#microsoft.graph.fileAttachment" : "#microsoft.graph.itemAttachment",
            ["@odata.mediaContentType"] = a.ContentType,
            ["id"] = a.Id,
            ["name"] = a.Name,
            ["contentType"] = a.ContentType,
            ["size"] = a.Content is { } content ? content.Length + StoreOverhead : a.Embedded?.Size ?? 0,
            ["isInline"] = a.IsInline,
        };
        if (a.Embedded is null && (select is null || select.Contains("microsoft.graph.fileAttachment/contentId")))
        {
            json["contentId"] = a.ContentId;
        }

        if (a.Embedded is null && select is null)
        {
            json["contentBytes"] = Convert.ToBase64String(a.Content ?? []);
        }

        if (a.Embedded is { } embedded && expand)
        {
            var item = GraphMessageJson(embedded, full: true, query: null);
            item["attachments"] = new JsonArray(embedded.Attachments.Select(inner => (JsonNode)GraphAttachmentJson(inner, null, expand: true)).ToArray());
            json["item"] = item;
        }

        return json;
    }

    /// <summary>One attachment: a file with its bytes; an attached item with its properties (and its own attachments, bytes and all) when $expand asks.</summary>
    private HttpResponseMessage GraphAttachment(string itemId, string attachmentId, System.Collections.Specialized.NameValueCollection query)
    {
        if (!_attachments.TryGetValue(attachmentId, out var found) || found.Owner.Id != itemId)
        {
            return GraphError(404, "ErrorItemNotFound", "The specified object was not found in the store.");
        }

        var expand = query["$expand"]?.Contains("microsoft.graph.itemattachment/item", StringComparison.OrdinalIgnoreCase) == true;
        return Graph(GraphAttachmentJson(found.Attachment, null, expand));
    }

    /// <summary>An attachment's raw content: a file's bytes, an attached message's MIME.</summary>
    private HttpResponseMessage GraphAttachmentValue(string itemId, string attachmentId)
    {
        if (!_attachments.TryGetValue(attachmentId, out var found) || found.Owner.Id != itemId)
        {
            return GraphError(404, "ErrorItemNotFound", "The specified object was not found in the store.");
        }

        var a = found.Attachment;
        return a.Embedded is { } embedded ? Bytes(embedded.Mime ?? [], "text/plain") : Bytes(a.Content ?? [], a.ContentType);
    }

    // --- answers ----------------------------------------------------------------------------------------------------------

    private static JsonObject Who((string Name, string Address) m) => new() { ["emailAddress"] = new JsonObject { ["name"] = m.Name, ["address"] = m.Address } };

    private static JsonObject? Zoned(DateTimeOffset? time)
        => time is { } t ? new JsonObject { ["dateTime"] = t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture), ["timeZone"] = "UTC" } : null;

    private static JsonObject Collection(IEnumerable<JsonNode> values) => new() { ["@odata.context"] = GraphRoot + "$metadata", ["value"] = new JsonArray(values.ToArray()) };

    private static HttpResponseMessage Graph(JsonObject json) => new(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] content, string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }

    private static HttpResponseMessage GraphError(int status, string code, string message)
        => new((HttpStatusCode)status) { Content = new StringContent(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(), Encoding.UTF8, "application/json") };
}
