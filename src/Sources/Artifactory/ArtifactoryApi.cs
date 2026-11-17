using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Artifactory;

/// <summary>
/// Artifactory's REST API and its query language, as the source asks them (spec 7.13): at most so many requests open at
/// once (the settings' repositories at a time times the requests at a time per repository), a client's ahead of a walk's,
/// and no pace of AnyFS's own. A throttled answer (429, 503 with Retry-After) holds every request and is tried again; a
/// server that runs only so many queries at once and refuses the rest (429, after making them wait) is sent fewer at a
/// time from then on (<see cref="SendFewer"/>); a server error or a dropped connection tried again twice; a refusal of a
/// web session's access token (401) renewed once through the session and tried again; a request sent on elsewhere (a
/// file kept in a storage service: a signed address there for a time) followed, the sign-in kept to the server; errors
/// as <see cref="VfsException"/>s by status.
/// </summary>
internal sealed class ArtifactoryApi : IDisposable
{
    private const int Attempts = 3;
    private const int ThrottledAttempts = 6;
    private const int MostRedirects = 5;

    private readonly ArtifactorySession _session;
    private readonly RequestPace _pace;
    private readonly PriorityGate _gate;
    private readonly PriorityGate _queryGate;
    private readonly ILogger _logger;
    private long _queriesCutAt;

    public ArtifactoryApi(ArtifactorySession session, int concurrency, ILogger logger)
    {
        _session = session;

        // the holds a server asks for, shared by every source and client of that host; no pace besides
        _pace = RequestPace.Unpaced("artifactory:" + session.Address.Base.Authority);

        // a client's request (a listing or an open someone waits on) takes the next free slot ahead of the walk's
        _gate = new PriorityGate(Math.Clamp(concurrency, 1, 64 * 64));

        // queries at once: as many as requests, until the server refuses some (SendFewer)
        _queryGate = new PriorityGate(_gate.Slots);
        _logger = logger;
    }

    public ArtifactoryAddress Address => _session.Address;

    private string Host => Address.Base.Host;

    /// <summary>One REST call's JSON (<paramref name="relative"/> to <c>api/</c>).</summary>
    public async Task<JsonDocument> GetAsync(string relative, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Address.Api(relative)), relative, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, relative, ct).ConfigureAwait(false);
    }

    /// <summary>One REST call's JSON, or null where Artifactory answers 404 (a thing that is not there, or not to this account).</summary>
    public async Task<JsonDocument?> FindAsync(string relative, CancellationToken ct)
    {
        try
        {
            return await GetAsync(relative, ct).ConfigureAwait(false);
        }
        catch (VfsException ex) when (ex.Error == VfsError.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// A query's items (<c>items.find(…)</c>, posted as text), read as they arrive: up to <paramref name="most"/> of them,
    /// and whether there were more (the rest is not read: the answer is let go of, and the server stops making it). The
    /// <paramref name="what"/> is for messages and the Operations tab. A query that may find a great many
    /// (<paramref name="streamed"/>) asks the server to read its database a batch at a time (<c>stream: true</c>) rather
    /// than all it finds at once; it is not given a limit instead, since Artifactory applies one before it leaves out what
    /// the account may not read, and an answer cut short that way looks whole.
    /// </summary>
    public async Task<AqlAnswer> AqlAsync(string query, int most, string what, CancellationToken ct, bool streamed = false)
    {
        for (var refusals = 0; ;)
        {
            await _queryGate.WaitAsync(urgent: !Walks.Current, ct).ConfigureAwait(false);
            var sentAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                return await AqlNowAsync(query, most, what, streamed, ct).ConfigureAwait(false);
            }
            catch (OneTooManyException)
            {
                // its place is given back first: it goes again when there is one under what the server takes
            }
            finally
            {
                _queryGate.Release();
            }

            if (!SendFewer(sentAt))
            {
                // one at a time already: the server is busy with others' queries, and made this one wait before it refused it
                if (++refusals >= ThrottledAttempts)
                {
                    throw new VfsException(VfsError.Unavailable, $"Artifactory ({Host}) is busy with other queries and refused {what} {refusals} times (429)");
                }

                await Task.Delay(TimeSpan.FromSeconds(refusals), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A query was refused as one too many (429): Artifactory runs so many at once, makes the rest wait ten seconds, then
    /// refuses them. Half as many are sent at a time from here on, once for all those refused together (each was sent
    /// before the cut the first of them made). False where they go one at a time already.
    /// </summary>
    private bool SendFewer(long sentAt)
    {
        if (sentAt < Interlocked.Read(ref _queriesCutAt))
        {
            return true;
        }

        var fewer = Math.Max(1, _queryGate.Slots / 2);
        if (fewer >= _queryGate.Slots)
        {
            return false;
        }

        Interlocked.Exchange(ref _queriesCutAt, System.Diagnostics.Stopwatch.GetTimestamp());
        _queryGate.Shrink(fewer);
        _logger.LogInformation("artifactory: {Host} refuses queries beyond those it runs at once (429); AnyFS sends it {Count} at a time from now on", Host, fewer);
        return true;
    }

    /// <summary>A query refused as one too many.</summary>
    private sealed class OneTooManyException : Exception;

    private async Task<AqlAnswer> AqlNowAsync(string query, int most, string what, bool streamed, CancellationToken ct)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Address.Api("search/aql")) { Content = new StringContent(query, Encoding.UTF8, "text/plain") };
            if (streamed)
            {
                request.Headers.TryAddWithoutValidation("stream", "true");
            }

            return request;
        }, what, ct, HttpCompletionOption.ResponseHeadersRead, query: true).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var reader = new AqlReader();
        var items = new List<ArtItem>();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var filled = 0;
            var final = false;
            while (true)
            {
                int consumed;
                try
                {
                    consumed = reader.Read(buffer.AsSpan(0, filled), final, items);
                }
                catch (JsonException ex)
                {
                    throw new VfsException(VfsError.IoError, $"Artifactory answered {what} with JSON AnyFS cannot read: {ex.Message}", ex);
                }

                if (items.Count > most)
                {
                    items.RemoveRange(most, items.Count - most);
                    return new AqlAnswer(items, More: true);
                }

                if (final || reader.Done)
                {
                    // cut at a limit of the server's own (a hosted Artifactory's half a million): there was more
                    return new AqlAnswer(items, More: reader.Trimmed);
                }

                // what is left is the start of something not all here yet: more of it, in a buffer that holds it
                filled -= consumed;
                if (filled > 0 && consumed > 0)
                {
                    buffer.AsSpan(consumed, filled).CopyTo(buffer);
                }

                if (filled == buffer.Length)
                {
                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, filled).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                var read = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
                final = read == 0;
                filled += read;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new VfsException(VfsError.Unavailable, $"Artifactory ({Host}) broke off its answer to {what}: {ex.Message}", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// A file's bytes from <paramref name="offset"/> on (a Range request, where it is not the start): the response with its
    /// body unread, for the caller to stream and dispose. A server that ignores the range answers 200 with the whole file.
    /// </summary>
    public Task<HttpResponseMessage> OpenAsync(Uri url, long offset, string what, CancellationToken ct)
        => SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
            }

            return request;
        }, what, ct, HttpCompletionOption.ResponseHeadersRead);

    /// <summary>
    /// Sends a request (made afresh for each attempt) and returns a successful response; refusals and failures become
    /// <see cref="VfsException"/>s, after the retries the class describes.
    /// </summary>
    /// <param name="query">A query: one refused as one too many (429 with no time to wait) is its caller's to send again (<see cref="OneTooManyException"/>).</param>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, string what, CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, bool query = false)
    {
        var renewed = false;
        var redirects = 0;
        Uri? sentOn = null;
        for (var attempt = 1; ; attempt++)
        {
            await _pace.WaitAsync(ct).ConfigureAwait(false);
            var generation = _session.Generation;
            HttpResponseMessage response;
            await _gate.WaitAsync(urgent: !Walks.Current, ct).ConfigureAwait(false);
            try
            {
                using var request = make();
                if (sentOn is not null)
                {
                    request.RequestUri = sentOn;
                }

                await _session.AuthorizeAsync(request, ct).ConfigureAwait(false);
                response = await _session.Http.SendAsync(request, completion, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested) && attempt < Attempts)
            {
                _logger.LogInformation("artifactory: {What} did not answer ({Error}); trying again", what, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new VfsException(VfsError.Unavailable, $"Artifactory ({Host}) did not answer: {ex.Message}", ex);
            }
            finally
            {
                _gate.Release();
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = response.StatusCode;
            if (status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                && response.Headers.Location is { } location)
            {
                var target = location.IsAbsoluteUri ? location : new Uri(response.RequestMessage?.RequestUri ?? Address.Base, location);
                response.Dispose();
                if (++redirects > MostRedirects)
                {
                    throw new VfsException(VfsError.IoError, $"Artifactory sent {what} on more than {MostRedirects} times, the last to {target.Host}.");
                }

                sentOn = target;
                attempt = 0;
                continue;
            }

            if (status == HttpStatusCode.Unauthorized && !renewed && _session.CanRenew && sentOn is null)
            {
                response.Dispose();
                renewed = true;
                await _session.RenewAsync(generation, ct).ConfigureAwait(false);
                continue;
            }

            if (_pace.Hold(response) is { } hold)
            {
                _logger.LogInformation("artifactory: {Host} asks AnyFS to wait {Seconds:N0} s ({Status}); the request it answered so: {What}", Host, hold.TotalSeconds, (int)status, what);
            }

            var retryAfter = response.Headers.RetryAfter is not null;
            if (query && status is HttpStatusCode.TooManyRequests && !retryAfter)
            {
                response.Dispose();
                throw new OneTooManyException();
            }

            if ((status is HttpStatusCode.TooManyRequests || status is HttpStatusCode.ServiceUnavailable && retryAfter) && attempt < ThrottledAttempts
                || status is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable && attempt < Attempts)
            {
                response.Dispose();

                if (!retryAfter)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
                }

                continue;
            }

            using (response)
            {
                throw await ErrorAsync(response, what, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<VfsException> ErrorAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
        }

        var message = Message(body);
        var status = (int)response.StatusCode;
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => new VfsException(VfsError.NotFound, $"Artifactory has no {what} for this account ({message ?? "404 Not Found"})"),
            HttpStatusCode.Forbidden => new VfsException(VfsError.AccessDenied, $"Artifactory refused {what}: {message ?? "403 Forbidden"}"),
            HttpStatusCode.Unauthorized => new VfsException(VfsError.Unavailable, $"Artifactory ({Host}) no longer takes {_session.Settings.Who}: {message ?? "401 Unauthorized"}. Retry the source to sign in again."),
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => new VfsException(VfsError.Unavailable, $"Artifactory ({Host}) is busy ({status}){(message is null ? "" : ": " + message)}"),

            // a call this server does not have, or does not take in this form (an edition without it, a query it cannot run)
            HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented => new VfsException(VfsError.NotSupported, $"Artifactory would not take {what} ({status}){(message is null ? "" : ": " + message)}"),
            _ => new VfsException(VfsError.IoError, $"Artifactory answered {status} to {what}{(message is null ? "" : ": " + message)}"),
        };
    }

    /// <summary>Artifactory's own words for an error: the messages of its <c>errors</c>, else a short start of a body that is not a page.</summary>
    internal static string? Message(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var text = body.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var parts = doc.RootElement.Arr("errors").Select(e => e.Str("message")).Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
                if (parts.Count > 0)
                {
                    return string.Join("; ", parts);
                }

                return doc.RootElement.Str("message") ?? doc.RootElement.Str("error");
            }
            catch (JsonException)
            {
            }
        }

        return text.StartsWith('<') ? null : text.Length <= 200 ? text : text[..200];
    }

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new VfsException(VfsError.IoError, $"Artifactory ({Host}) answered {what} with {(type.Length == 0 ? "no data" : type)}, not data.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new VfsException(VfsError.IoError, $"Artifactory answered {what} with JSON AnyFS cannot read: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>A query's items as read: all of them, or the first so many with <paramref name="More"/> there (not read, or cut off by the server at a limit of its own).</summary>
internal sealed record AqlAnswer(List<ArtItem> Items, bool More);

/// <summary>
/// Reads the answer to <c>items.find</c> as it arrives, a buffer at a time: <c>{"results":[{…},{…}],"range":{…}}</c>. Each
/// item is read once it is all there, straight into an <see cref="ArtItem"/>: an answer of a million items is never held
/// as text or as a document. A folder's path, the same for every item in it, is one string.
/// </summary>
internal sealed class AqlReader
{
    private enum Phase
    {
        Start,
        Top,
        Results,
        Done,
    }

    private JsonReaderState _state;
    private Phase _phase;
    private string? _lastFolder;

    /// <summary>Whether the whole answer has been read.</summary>
    public bool Done => _phase == Phase.Done;

    /// <summary>Whether the server says it cut the answer short at a limit of its own (<c>range.notification</c>: "…results are trimmed").</summary>
    public bool Trimmed { get; private set; }

    /// <summary>Reads what is whole in <paramref name="data"/>, adding the items it holds; how many bytes it took (the rest wants more after it).</summary>
    public int Read(ReadOnlySpan<byte> data, bool final, List<ArtItem> into)
    {
        var reader = new Utf8JsonReader(data, final, _state);
        while (_phase != Phase.Done)
        {
            var before = reader;
            if (!reader.Read())
            {
                reader = before;
                break;
            }

            var whole = true;
            switch (_phase)
            {
                case Phase.Start:
                    _phase = reader.TokenType == JsonTokenType.StartObject ? Phase.Top : throw new JsonException("not an object");
                    break;

                case Phase.Top when reader.TokenType == JsonTokenType.EndObject:
                    _phase = Phase.Done;
                    break;

                case Phase.Top when reader.TokenType == JsonTokenType.PropertyName:
                    if (reader.ValueTextEquals("results"u8))
                    {
                        whole = reader.Read();
                        if (whole)
                        {
                            _phase = reader.TokenType == JsonTokenType.StartArray ? Phase.Results : throw new JsonException("results is not a list");
                        }
                    }
                    else if (reader.ValueTextEquals("range"u8))
                    {
                        whole = Whole(ref reader) && ReadRange(ref reader);
                    }
                    else
                    {
                        whole = reader.TrySkip();
                    }

                    break;

                case Phase.Results when reader.TokenType == JsonTokenType.EndArray:
                    _phase = Phase.Top;
                    break;

                case Phase.Results when reader.TokenType == JsonTokenType.StartObject:
                    var item = reader;
                    whole = item.TrySkip();
                    if (whole && ReadItem(ref reader) is { } read)
                    {
                        into.Add(read);
                    }

                    break;

                default:
                    whole = reader.TrySkip();
                    break;
            }

            if (!whole)
            {
                // not all here yet: from before it again, with more
                reader = before;
                break;
            }
        }

        _state = reader.CurrentState;
        return (int)reader.BytesConsumed;
    }

    /// <summary>Whether the value after the property name the reader is at is all here (the reader is not moved).</summary>
    private static bool Whole(ref Utf8JsonReader reader)
    {
        var probe = reader;
        return probe.Read() && probe.TrySkip();
    }

    /// <summary>The range object after its property name, all of it here: whether it says the answer was cut short.</summary>
    private bool ReadRange(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return reader.TrySkip();
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var notification = reader.ValueTextEquals("notification"u8);
            reader.Read();
            if (notification && reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 })
            {
                Trimmed = true;
            }
            else
            {
                reader.TrySkip();
            }
        }

        return true;
    }

    /// <summary>
    /// One item, all of it here, from its opening brace to its closing one: <c>path</c> is its folder's (<c>.</c> for the
    /// repository's root), and the root is an item too, named <c>.</c>, which is nobody's child: null for it.
    /// </summary>
    private ArtItem? ReadItem(ref Utf8JsonReader reader)
    {
        string? folder = null;
        string? name = null;
        string? sha1 = null;
        var isFolder = false;
        long size = 0;
        DateTimeOffset? created = null;
        DateTimeOffset? modified = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("path"u8))
            {
                reader.Read();
                folder = _lastFolder is not null && reader.ValueTextEquals(_lastFolder) ? _lastFolder : _lastFolder = reader.GetString();
            }
            else if (reader.ValueTextEquals("name"u8))
            {
                reader.Read();
                name = reader.GetString();
            }
            else if (reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                isFolder = reader.ValueTextEquals("folder"u8);
            }
            else if (reader.ValueTextEquals("size"u8))
            {
                reader.Read();
                size = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var n) ? n
                    : reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(), out var s) ? s : 0;
            }
            else if (reader.ValueTextEquals("created"u8))
            {
                reader.Read();
                created = Time(ref reader);
            }
            else if (reader.ValueTextEquals("modified"u8))
            {
                reader.Read();
                modified = Time(ref reader);
            }
            else if (reader.ValueTextEquals("actual_sha1"u8))
            {
                reader.Read();
                sha1 = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else
            {
                reader.Read();
                reader.TrySkip();
            }
        }

        if (string.IsNullOrEmpty(name) || name == ".")
        {
            return null;
        }

        folder = folder is null or "." or "/" ? "" : folder.Trim('/');
        return new ArtItem(folder, name, isFolder, isFolder ? 0 : Math.Max(0, size), created, modified, isFolder ? null : ArtItem.Hex(sha1));
    }

    private static DateTimeOffset? Time(ref Utf8JsonReader reader)
        => reader.TokenType != JsonTokenType.String ? null : reader.TryGetDateTimeOffset(out var time) ? time : Json.ParseDate(reader.GetString());
}
