using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.ThreeDX;

/// <summary>
/// The 3DEXPERIENCE web services a 3DX source uses (spec 7.6), the ones Mount3DX uses: 3DSearch for every document's id
/// (paged, a hundred at a time), 3DSpace's <c>modeler/documents</c> for the documents and their files, a download ticket
/// (behind a CSRF token asked for it) for a file's content, which the file server (FCS) serves with ranges, and 3DSpace's
/// attributes and the person's security context for a document's metadata. Requests go through the
/// <see cref="ThreeDXSession"/>: one sent to the sign-in instead of answered has the session renewed and is made again; a
/// failure to connect, a 5xx or a 429 is tried again after a pause.
/// </summary>
internal sealed partial class ThreeDXClient
{
    public const int PageSize = 100;

    private const int MaxAttempts = 4;

    private static readonly JsonWriterOptions Indented = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ThreeDXSession _session;
    private readonly DateOrder _dates;
    private readonly ILogger _logger;
    private readonly Action<string>? _onTrouble;
    private readonly Action? _onSuccess;
    private readonly string _label = "anyfs-" + Guid.NewGuid().ToString("N")[..16];
    private SessionValue? _securityContext;

    public ThreeDXClient(ThreeDXSession session, DateOrder dates, ILogger logger, Action<string>? onTrouble = null, Action? onSuccess = null)
    {
        _session = session;
        _dates = dates;
        _logger = logger;
        _onTrouble = onTrouble;
        _onSuccess = onSuccess;
    }

    public Uri Space => _session.Space;

    // --- services -------------------------------------------------------------------------------------------------------

    /// <summary>3DSearch's address, as 3DSpace lists its platform's services (<c>resources/AppsMngt/api/v1/services</c>), found once.</summary>
    public async Task<Uri> SearchAsync(CancellationToken ct)
    {
        if (_session.Search is { } known)
        {
            return known;
        }

        using var services = await GetJsonAsync(new Uri(Space, "resources/AppsMngt/api/v1/services"), "the platform's services", ct).ConfigureAwait(false);
        var search = SearchServiceOf(services.RootElement) ?? throw new VfsException(VfsError.IoError, $"{Space.Host} lists no 3DSearch service among its platform's services");
        _session.Search = search;
        _logger.LogInformation("3dx: 3DSearch is at {Search}", search);
        return search;
    }

    /// <summary>3DSearch's address with the cookies of <paramref name="http"/> (the web session's browser, before the source has a session of its own); null when 3DSpace lists none.</summary>
    public static async Task<Uri?> SearchServiceAsync(HttpClient http, Uri space, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(space, "resources/AppsMngt/api/v1/services"), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || !ThreeDXSession.IsJson(response))
        {
            return null;
        }

        using var services = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return SearchServiceOf(services.RootElement);
    }

    /// <summary>The 3DSearch service in a services listing (<c>platforms[0].services[name=3DSearch].url</c>), with a slash at the end.</summary>
    internal static Uri? SearchServiceOf(JsonElement services)
    {
        if (!services.TryGetProperty("platforms", out var platforms) || platforms.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var platform in platforms.EnumerateArray())
        {
            if (!platform.TryGetProperty("services", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var service in list.EnumerateArray())
            {
                if (Text(service, "name") is "3DSearch" && Text(service, "url") is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var address))
                {
                    return new Uri(address.AbsoluteUri.TrimEnd('/') + "/");
                }
            }
        }

        return null;
    }

    // --- search -----------------------------------------------------------------------------------------------------------

    /// <summary>How many documents the search finds, and the search's id that the pages after the first go by.</summary>
    public async Task<(int Count, string? SearchId)> CountAsync(string query, string tenant, CancellationToken ct)
    {
        using var result = await SearchPageAsync(0, 1, null, query, tenant, ct).ConfigureAwait(false);
        var infos = result.RootElement.TryGetProperty("infos", out var i) ? i : throw new VfsException(VfsError.IoError, "3DSearch's answer has no infos (nmatches, next_start)");
        var count = Number(infos, "nmatches") ?? throw new VfsException(VfsError.IoError, "3DSearch's answer gives no nmatches");
        var next = Text(infos, "next_start");
        return ((int)Math.Min(count, int.MaxValue), next?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault());
    }

    /// <summary>The ids (<c>resourceid</c>) of the documents on one page of the search, as Mount3DX pages it.</summary>
    public async Task<IReadOnlyList<string>> PageAsync(int page, int pageSize, string? searchId, string query, string tenant, CancellationToken ct)
    {
        using var result = await SearchPageAsync(page, pageSize, searchId, query, tenant, ct).ConfigureAwait(false);
        if (!result.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            throw new VfsException(VfsError.IoError, $"3DSearch's answer for page {page} has no results");
        }

        var ids = new List<string>();
        foreach (var hit in results.EnumerateArray())
        {
            if (!hit.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var attribute in attributes.EnumerateArray())
            {
                if (Text(attribute, "name") == "resourceid" && Text(attribute, "value") is { Length: > 0 } id)
                {
                    ids.Add(id);
                    break;
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// One page of the search: the first a search of its own from the start, the rest at their place in the search the count
    /// began (<c>next_start</c>: "start 0 page id"), as 3DSearch's own pages ask.
    /// </summary>
    private async Task<JsonDocument> SearchPageAsync(int page, int pageSize, string? searchId, string query, string tenant, CancellationToken ct)
    {
        var search = await SearchAsync(ct).ConfigureAwait(false);
        var body = SearchBody(page, pageSize, searchId, query, tenant, _label);
        return await PostJsonAsync(new Uri(search, "search?xrequestedwith=xmlhttprequest"), () => new StringContent(body, Encoding.UTF8, "application/json"), $"page {page} of the document search", ct).ConfigureAwait(false);
    }

    internal static string SearchBody(int page, int pageSize, string? searchId, string query, string tenant, string label)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("label", label);
            json.WriteString("locale", "en");
            json.WriteNumber("nresults", pageSize);
            json.WriteString("query", query);
            json.WriteStartObject("refine");
            json.WriteEndObject();
            json.WriteStartArray("select_predicate");
            json.WriteStringValue("ds6w:label");
            json.WriteStringValue("ds6w:status");
            json.WriteStringValue("ds6w:type");
            json.WriteEndArray();
            json.WriteStartArray("source");
            json.WriteStringValue("3dspace");
            json.WriteEndArray();
            if (page > 0)
            {
                json.WriteString("next_start", $"{page * pageSize} 0 {page} {searchId}");
            }
            else
            {
                json.WriteNumber("start", 0);
            }

            json.WriteString("tenant", tenant);
            json.WriteBoolean("with_synthesis", true);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // --- documents --------------------------------------------------------------------------------------------------------

    /// <summary>The documents with these ids and their files; one 3DX cannot describe (no title, or a field missing) is left out and logged.</summary>
    public async Task<IReadOnlyList<DxDocument>> DocumentsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        using var result = await DocumentsJsonAsync(ids, ct).ConfigureAwait(false);
        var documents = new List<DxDocument>();
        foreach (var data in Data(result))
        {
            if (Document(data) is { } document)
            {
                documents.Add(document);
            }
        }

        return documents;
    }

    private Task<JsonDocument> DocumentsJsonAsync(IReadOnlyList<string> ids, CancellationToken ct)
        => PostJsonAsync(new Uri(Space, "resources/v1/modeler/documents/ids"),
            () => new StringContent("$ids=" + string.Join(",", ids.Select(Uri.EscapeDataString)), Encoding.UTF8, "application/x-www-form-urlencoded"),
            ids.Count == 1 ? $"document {ids[0]}" : $"{ids.Count} documents", ct);

    private static IEnumerable<JsonElement> Data(JsonDocument result)
        => result.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data.EnumerateArray() : [];

    /// <summary>A document from its <c>data</c> element, or null (logged) when it lacks what a folder needs.</summary>
    internal DxDocument? Document(JsonElement data)
    {
        var id = Text(data, "id");
        if (id is null || !data.TryGetProperty("dataelements", out var elements))
        {
            _logger.LogInformation("3dx: a document without an id or data elements was left out");
            return null;
        }

        var title = Text(elements, "title");
        if (title is null)
        {
            // as Mount3DX: a document with no title is no document to show
            return null;
        }

        var name = Text(elements, "name");
        var revision = Text(elements, "revision");
        var type = Text(elements, "typeNLS");
        if (name is null || revision is null || type is null)
        {
            _logger.LogInformation("3dx: document {Id} was left out: it has no {Missing}", id, name is null ? "name" : revision is null ? "revision" : "typeNLS");
            return null;
        }

        var files = new List<DxFile>();
        if (data.TryGetProperty("relateddata", out var related) && related.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in list.EnumerateArray())
            {
                var fileId = Text(file, "id");
                var fileElements = file.TryGetProperty("dataelements", out var fe) ? fe : default;
                var fileTitle = fileElements.ValueKind == JsonValueKind.Object ? Text(fileElements, "title") : null;
                if (fileId is null || fileTitle is null)
                {
                    _logger.LogInformation("3dx: a file of document {Id} was left out: it has no {Missing}", id, fileId is null ? "id" : "title");
                    continue;
                }

                var size = Number(fileElements, "fileSize");
                files.Add(new DxFile(fileId, fileTitle, size is >= 0 ? size : null, Date(fileElements, "originated"), Date(fileElements, "modified")));
            }
        }

        return new DxDocument(id, name, title, revision, type, Date(elements, "originated"), Date(elements, "modified"), files);
    }

    private DateTimeOffset? Date(JsonElement element, string name) => ThreeDXDates.Parse(Text(element, name), _dates);

    // --- metadata ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// What <see cref="ThreeDXNames.MetadataFile"/> holds, as Mount3DX writes it: the document's attributes
    /// (<c>collabServices/attributes/op/read</c>, in the person's security context) and its description
    /// (<c>modeler/documents/ids</c>'s data), under the names of the calls, indented.
    /// </summary>
    public async Task<byte[]> MetadataAsync(string documentId, CancellationToken ct)
    {
        var context = await SecurityContextAsync(ct).ConfigureAwait(false);
        var body = AttributesBody(documentId);
        using var attributes = await PostJsonAsync(new Uri(Space, "resources/v1/collabServices/attributes/op/read"), () => new StringContent(body, Encoding.UTF8, "application/json"),
            $"the attributes of document {documentId}", ct, ("SecurityContext", context)).ConfigureAwait(false);
        using var described = await DocumentsJsonAsync([documentId], ct).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Indented))
        {
            json.WriteStartObject();
            json.WritePropertyName("resources/v1/collabServices/attributes/op/read");
            attributes.RootElement.WriteTo(json);
            json.WritePropertyName("resources/v1/modeler/documents/ids");
            if (described.RootElement.TryGetProperty("data", out var data))
            {
                data.WriteTo(json);
            }
            else
            {
                json.WriteNullValue();
            }

            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>What <c>collabServices/attributes/op/read</c> is asked for a document, as Mount3DX asks it.</summary>
    internal static string AttributesBody(string documentId)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartArray("lIds");
            json.WriteEndArray();
            json.WriteStartArray("relIDs");
            json.WriteEndArray();
            json.WriteStartArray("busIDs");
            json.WriteStringValue(documentId);
            json.WriteEndArray();
            json.WriteString("plmparameters", "false");
            json.WriteString("attributes", "true");
            json.WriteString("navigateToMain", "true");
            json.WriteString("readonly", "true");
            json.WriteString("debug_properties", "");
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The person's security context ("role.organization.collaborative space", from their preferred credentials), which
    /// 3DSpace's collaboration services want with each call; asked once per 3DSpace session, as Mount3DX asks it once per
    /// sign-in (a new sign-in may carry other preferred credentials).
    /// </summary>
    public async Task<string> SecurityContextAsync(CancellationToken ct)
    {
        if (_securityContext is { } known && known.Generation == _session.GenerationOf(Space))
        {
            return known.Value;
        }

        var person = await PersonAsync(ct).ConfigureAwait(false);
        _securityContext = new SessionValue(person.SecurityContext, _session.GenerationOf(Space));
        return person.SecurityContext;
    }

    /// <summary>What was asked of a 3DSpace session, and which one (its <see cref="ThreeDXSession.GenerationOf"/>).</summary>
    private sealed record SessionValue(string Value, int Generation);

    /// <summary>Who 3DX says is signed in: their name (where 3DX gives it) and security context.</summary>
    public async Task<(string? Name, string SecurityContext)> PersonAsync(CancellationToken ct)
    {
        using var person = await GetJsonAsync(new Uri(Space, "resources/modeler/pno/person?current=true&select=preferredcredentials"), "the person signed in", ct).ConfigureAwait(false);
        var root = person.RootElement;
        var credentials = root.TryGetProperty("preferredcredentials", out var c) ? c : default;
        string? Part(string name) => credentials.ValueKind == JsonValueKind.Object && credentials.TryGetProperty(name, out var part) && part.ValueKind == JsonValueKind.Object ? Text(part, "name") : null;
        var context = $"{Part("role")}.{Part("organization")}.{Part("collabspace")}";
        return (root.ValueKind == JsonValueKind.Object ? Text(root, "name") : null, context);
    }

    // --- files ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A file's content from <paramref name="offset"/>, its headers read: a download ticket for it (3DSpace hands one out for a
    /// PUT that carries a CSRF token it has just given), then the file server's answer to a GET of the ticket, with a Range
    /// when the offset is not 0. The answer may be the whole file (206 not given): the caller skips to the offset then.
    /// </summary>
    /// <param name="file">The file as the list holds it.</param>
    public async Task<HttpResponseMessage> OpenFileAsync(string documentId, DxFile file, long offset, string path, CancellationToken ct)
    {
        var ticket = await TicketAsync(documentId, file, path, ct).ConfigureAwait(false);
        for (var hop = 0; ; hop++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, ticket);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
            }

            HttpResponseMessage response;
            try
            {
                response = await _session.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                _onTrouble?.Invoke(ex.Message);
                throw VfsException.Unavailable($"'{path}': the file server {ticket.Host} could not be reached: {ex.Message}");
            }

            // a file server may hand the download to another
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location && hop < 5)
            {
                ticket = new Uri(ticket, location);
                response.Dispose();
                continue;
            }

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                _onSuccess?.Invoke();
                return response;
            }

            var status = response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            throw Error(status, text, $"'{path}' from the file server {ticket.Host}");
        }
    }

    /// <summary>
    /// The download ticket's address (<c>DownloadTicket</c>'s <c>data[0].dataelements.ticketURL</c>), asked as Mount3DX asks
    /// it: a PUT carrying a CSRF token 3DSpace has just given (<see cref="CsrfAsync"/>), so the token is the session's as it is
    /// when the PUT goes, a session renewed on the way included. A PUT refused, or answered without a ticket, is logged with
    /// what 3DX answered; the file is looked up afresh in its document (<see cref="RecheckAsync"/>: the list may be older than
    /// the file), and the ticket asked once more, with a new token and the person's security context (as 3DX's own pages send
    /// it). 3DX in trouble (a 5xx, after its tries) is said at once.
    /// </summary>
    private async Task<Uri> TicketAsync(string documentId, DxFile file, string path, CancellationToken ct)
    {
        var url = new Uri(Space, $"resources/v1/modeler/documents/{Uri.EscapeDataString(documentId)}/files/{Uri.EscapeDataString(file.Id)}/DownloadTicket");
        var what = $"a download ticket for '{path}'";
        var found = "";
        for (var attempt = 1; ; attempt++)
        {
            var context = attempt > 1 ? await SecurityContextAsync(ct).ConfigureAwait(false) : null;
            using var response = await SendAsync(async tokenCt =>
            {
                var csrf = await CsrfAsync(tokenCt).ConfigureAwait(false);
                var request = new HttpRequestMessage(HttpMethod.Put, url);
                request.Headers.TryAddWithoutValidation("ENO_CSRF_TOKEN", csrf);
                if (context is not null)
                {
                    request.Headers.TryAddWithoutValidation("SecurityContext", context);
                }

                return request;
            }, expectsJson: true, HttpCompletionOption.ResponseContentRead, what, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string problem;
            if (!response.IsSuccessStatusCode)
            {
                var error = Error(response.StatusCode, body, what);
                if (error.Error == VfsError.Unavailable)
                {
                    throw error;
                }

                if (attempt > 1)
                {
                    throw new VfsException(error.Error, error.Message + found, error);
                }

                problem = error.Message;
            }
            else if (TicketOf(body) is { } ticket)
            {
                return ticket;
            }
            else if (attempt > 1)
            {
                throw new VfsException(VfsError.IoError, $"3DX gave no download ticket for '{path}', also when asked again ({Gist(response, body)}){found}");
            }
            else
            {
                problem = $"3DX answered without one ({Gist(response, body)})";
            }

            found = await RecheckAsync(documentId, file, path, ct).ConfigureAwait(false);
            _logger.LogInformation("3dx: no download ticket for '{Path}': {Problem}{Found}; asking again with a new CSRF token and your security context", path, problem, found);
        }
    }

    /// <summary>
    /// After 3DX gave no ticket for a file: whether it is still the file the list holds, from its document asked for afresh
    /// (one request). A document or file 3DX no longer gives, or another file in its place (a new version), is said as such:
    /// the next listing brings the change. Otherwise what was found, for the log and the error.
    /// </summary>
    private async Task<string> RecheckAsync(string documentId, DxFile file, string path, CancellationToken ct)
    {
        IReadOnlyList<DxDocument> now;
        try
        {
            now = await DocumentsAsync([documentId], ct).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            return $"; its document could not be asked for afresh ({ex.Message})";
        }

        if (now.Count == 0)
        {
            throw new VfsException(VfsError.NotFound, $"3DX no longer gives the document of '{path}'; the next listing takes it out");
        }

        if (now[0].Files.Any(f => f.Id == file.Id))
        {
            return "; its document, asked for afresh, still holds the file as listed";
        }

        throw now[0].Files.FirstOrDefault(f => string.Equals(f.Title, file.Title, StringComparison.OrdinalIgnoreCase)) is { } other
            ? new VfsException(VfsError.IoError, $"'{path}' is another file in 3DX now than the one listed (a new version, most likely: file {other.Id}, listed as {file.Id}); the next listing brings it")
            : new VfsException(VfsError.NotFound, $"'{path}' is no longer among its document's files in 3DX; the next listing takes it out");
    }

    /// <summary>The ticket's address in <c>DownloadTicket</c>'s answer, resolved against 3DSpace; null when it holds none.</summary>
    private Uri? TicketOf(string body)
    {
        try
        {
            using var result = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
            var first = Data(result).FirstOrDefault();
            var elements = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("dataelements", out var e) ? e : default;
            return elements.ValueKind == JsonValueKind.Object && Text(elements, "ticketURL") is { } ticket && Uri.TryCreate(Space, ticket, out var address) ? address : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What 3DX answered, for the log and errors (a 5xx tried again, an answer that should have held a ticket): its status,
    /// type and length, 3DX's own success, statusCode and message, and the names of what came (never their values, which
    /// may be a ticket's).
    /// </summary>
    private static string Gist(HttpResponseMessage response, string body)
    {
        var parts = new List<string> { $"HTTP {(int)response.StatusCode}", response.Content.Headers.ContentType?.MediaType ?? "no content type", body.Length == 0 ? "empty" : $"{body.Length} characters" };
        if (body.Length > 0)
        {
            try
            {
                using var result = JsonDocument.Parse(body);
                parts.AddRange(JsonGist(result.RootElement));
            }
            catch (JsonException)
            {
                parts.Add(Message(body) is { } text ? $"not JSON: \"{text}\"" : "not JSON");
            }
        }

        return string.Join(", ", parts);

        IEnumerable<string> JsonGist(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                yield return $"a JSON {root.ValueKind.ToString().ToLowerInvariant()}";
                yield break;
            }

            foreach (var name in new[] { "success", "statusCode" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String)
                {
                    yield return $"{name} {(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText())}";
                }
            }

            if (Message(body) is { } message)
            {
                yield return $"\"{message}\"";
            }

            if (root.TryGetProperty("data", out var data))
            {
                yield return DataGist(data);
            }

            yield return $"fields {Names(root)}";
        }

        static string DataGist(JsonElement data)
        {
            if (data.ValueKind != JsonValueKind.Array)
            {
                return $"data a {data.ValueKind.ToString().ToLowerInvariant()}";
            }

            var count = data.GetArrayLength();
            if (count == 0 || data[0].ValueKind != JsonValueKind.Object)
            {
                return count == 0 ? "data empty" : $"data of {count}";
            }

            var first = data[0];
            var gist = $"data of {count}, the first with {Names(first)}";
            if (first.TryGetProperty("dataelements", out var elements) && elements.ValueKind == JsonValueKind.Object)
            {
                gist += $" (dataelements {Names(elements)}{(elements.TryGetProperty("ticketURL", out _) && Text(elements, "ticketURL") is null ? "; ticketURL empty" : "")})";
            }

            return gist;
        }

        static string Names(JsonElement element) => string.Join(" ", element.EnumerateObject().Select(p => p.Name).Take(12)) is { Length: > 0 } names ? names : "none";
    }

    /// <summary>A CSRF token from 3DSpace's session as it is now; asked for every download ticket, as Mount3DX asks it (one kept from earlier may no longer be taken).</summary>
    private async Task<string> CsrfAsync(CancellationToken ct)
    {
        using var result = await GetJsonAsync(ThreeDXSession.SpaceProbe(Space), "a CSRF token", ct).ConfigureAwait(false);
        var root = result.RootElement;
        return (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("csrf", out var csrf) ? Text(csrf, "value") : null)
            ?? throw new VfsException(VfsError.IoError, $"{Space.Host} gave no CSRF token for downloads");
    }

    // --- keeping the sessions alive -------------------------------------------------------------------------------------

    /// <summary>Touches 3DSpace and, once known, 3DSearch, as Mount3DX's keep-alive does: a session that lapsed meanwhile is renewed on the way.</summary>
    public async Task PingAsync(CancellationToken ct)
    {
        using (await GetJsonAsync(ThreeDXSession.SpaceProbe(Space), "3DSpace's keep-alive", ct).ConfigureAwait(false))
        {
        }

        if (_session.Search is { } search)
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, ThreeDXSession.SearchProbe(search)), expectsJson: false, HttpCompletionOption.ResponseContentRead, "3DSearch's keep-alive", ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!ThreeDXSession.IsAnswer(response, body, search))
            {
                throw new VfsException(VfsError.Unavailable, $"3DSearch answered its keep-alive with {(int)response.StatusCode}");
            }
        }
    }

    // --- the one send path ------------------------------------------------------------------------------------------------

    private Task<JsonDocument> GetJsonAsync(Uri url, string what, CancellationToken ct)
        => SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), what, ct);

    private Task<JsonDocument> PostJsonAsync(Uri url, Func<HttpContent> content, string what, CancellationToken ct, params (string Name, string Value)[] headers)
        => SendJsonAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content() };
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            return request;
        }, what, ct);

    private async Task<JsonDocument> SendJsonAsync(Func<HttpRequestMessage> make, string what, CancellationToken ct)
    {
        using var response = await SendAsync(make, expectsJson: true, HttpCompletionOption.ResponseContentRead, what, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw Error(response.StatusCode, body, what);
        }

        try
        {
            return JsonDocument.Parse(body.Length == 0 ? "{}" : body);
        }
        catch (JsonException ex)
        {
            throw new VfsException(VfsError.IoError, $"3DX answered the request for {what} with something other than JSON: {ex.Message}");
        }
    }

    private Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, bool expectsJson, HttpCompletionOption completion, string what, CancellationToken ct)
        => SendAsync(_ => Task.FromResult(make()), expectsJson, completion, what, ct);

    /// <summary>
    /// Sends a request to 3DSpace or 3DSearch: a lapsed session is renewed once and the request made and sent again (a
    /// renewal is no attempt); a connection that fails, a 5xx or a 429 is tried again after a pause, up to
    /// <see cref="MaxAttempts"/> times. The request is made afresh for each sending, so one that carries what a session gave
    /// (a CSRF token) asks the session it goes to.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<CancellationToken, Task<HttpRequestMessage>> make, bool expectsJson, HttpCompletionOption completion, string what, CancellationToken ct)
    {
        var renewed = false;
        for (var attempt = 1; ; attempt++)
        {
            var request = await make(ct).ConfigureAwait(false);
            var service = _session.ServiceOf(request.RequestUri!);
            var generation = _session.GenerationOf(service);
            HttpResponseMessage response;
            try
            {
                response = await _session.Http.SendAsync(request, completion, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                _onTrouble?.Invoke(ex.Message);
                if (attempt < MaxAttempts)
                {
                    _logger.LogWarning("3dx: the request for {What} failed ({Error}); trying again", what, ex.Message);
                    await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }

                throw VfsException.Unavailable($"{request.RequestUri!.Host} could not be reached for {what}: {ex.Message}");
            }

            if (ThreeDXSession.IsLapsed(response, service, expectsJson))
            {
                response.Dispose();
                if (renewed || !await _session.RenewAsync(service, generation, ct).ConfigureAwait(false))
                {
                    _onTrouble?.Invoke("3DX keeps sending AnyFS to its sign-in");
                    throw VfsException.Unavailable($"3DX sent the request for {what} to its sign-in, also after AnyFS signed in again");
                }

                renewed = true;
                attempt--;
                continue;
            }

            if (((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests) && attempt < MaxAttempts)
            {
                var answer = Gist(response, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                _logger.LogWarning("3dx: the request for {What} was answered {Answer}; trying again", what, answer);
                _onTrouble?.Invoke($"3DX answered {(int)response.StatusCode}");
                response.Dispose();
                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                _onSuccess?.Invoke();
            }

            return response;
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(2 * attempt);

    private static VfsException Error(HttpStatusCode status, string body, string what)
    {
        var detail = Detail(body);
        return status switch
        {
            HttpStatusCode.NotFound => new VfsException(VfsError.NotFound, $"3DX has no {what}{detail}"),
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => new VfsException(VfsError.AccessDenied, $"3DX refused the request for {what} ({(int)status}){detail}"),
            HttpStatusCode.BadRequest => new VfsException(VfsError.InvalidPath, $"3DX rejected the request for {what}{detail}"),
            _ when (int)status >= 500 || status == HttpStatusCode.TooManyRequests => VfsException.Unavailable($"3DX answered the request for {what} with {(int)status}{detail}"),
            _ => new VfsException(VfsError.IoError, $"3DX answered the request for {what} with {(int)status}{detail}"),
        };
    }

    /// <summary>The message of a 3DX error body, briefly: ": message" or nothing.</summary>
    private static string Detail(string body) => Message(body) is { } message ? ": " + message : "";

    /// <summary>
    /// The message of a 3DX answer, at most 300 characters: a JSON body's error or message, a page's title, or the first line
    /// of plain text; null when it has none.
    /// </summary>
    private static string? Message(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        string? text = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var name in new[] { "error", "message", "errorMessage", "msg" })
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var value))
                {
                    text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                    break;
                }
            }
        }
        catch (JsonException)
        {
            // a page (an error page of the server's, or of a proxy before it): its title; other text: its first line
            text = body.TrimStart().StartsWith('<')
                ? HtmlTitle().Match(body) is { Success: true } title ? WebUtility.HtmlDecode(title.Groups[1].Value) : null
                : body.TrimStart().Split('\n', 2)[0];
        }

        text = text?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length > 300 ? text[..300] : text;
    }

    [GeneratedRegex("<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlTitle();

    /// <summary>A string property, or a number's text; null when absent or empty.</summary>
    private static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>A number given as a number or as text; null when absent or not a number.</summary>
    private static long? Number(JsonElement element, string name)
        => Text(element, name) is { } text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
