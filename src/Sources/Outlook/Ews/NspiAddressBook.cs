using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AnyFS.Sources.Outlook.Mail;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Outlook.Ews;

/// <summary>
/// An Exchange server's address book as Outlook reads it (spec 7.7, address books; Spike 16, 26-Oct-2026): NSPI
/// ([MS-OXNSPI]) over MAPI over HTTP's address book endpoint ([MS-OXCMAPIHTTP] 2.2.5, <c>/mapi/nspi/</c>), with the
/// same Windows sign-in as EWS. Bind opens a session context (its cookies), GetSpecialTable gives the address lists and
/// their nesting, QueryRows a list's entries with every card field at once. Requests go one at a time, as a session
/// context takes them; a session the server has forgotten is bound again. The lists are flat, as Outlook on the web
/// gives them: the Global Address List, then every list under All Address Lists (that container itself left out).
/// </summary>
internal sealed class NspiAddressBook : IAddressBookService, IDisposable
{
    private const int Attempts = 4;

    // property tags ([MS-OXPROPS]): the id in the high word, the type in the low
    private const uint EntryIdTag = 0x0FFF0102, ContainerFlagsTag = 0x36000003, DepthTag = 0x30050003, ContainerIdTag = 0xFFFD0003, DisplayNameTag = 0x3001001F;
    private const uint DisplayTypeTag = 0x39000003, DisplayTypeExTag = 0x39050003, SmtpAddressTag = 0x39FE001F, ProxyAddressesTag = 0x800F101F;
    private const uint GivenNameTag = 0x3A06001F, MiddleNameTag = 0x3A44001F, SurnameTag = 0x3A11001F, TitleTag = 0x3A17001F, DepartmentTag = 0x3A18001F, OfficeTag = 0x3A19001F, CompanyTag = 0x3A16001F;
    private const uint BusinessPhoneTag = 0x3A08001F, MobilePhoneTag = 0x3A1C001F, HomePhoneTag = 0x3A09001F, PagerTag = 0x3A21001F, BusinessFaxTag = 0x3A24001F;
    private const uint StreetTag = 0x3A29001F, CityTag = 0x3A27001F, StateTag = 0x3A28001F, PostalCodeTag = 0x3A2A001F, CountryTag = 0x3A26001F;

    /// <summary>The columns a list's entries are asked for in: what a card is made of.</summary>
    private static readonly uint[] EntryColumns =
    [
        EntryIdTag, DisplayNameTag, DisplayTypeTag, DisplayTypeExTag, SmtpAddressTag, ProxyAddressesTag, GivenNameTag, MiddleNameTag, SurnameTag, TitleTag, DepartmentTag,
        OfficeTag, CompanyTag, BusinessPhoneTag, MobilePhoneTag, HomePhoneTag, PagerTag, BusinessFaxTag, StreetTag, CityTag, StateTag, PostalCodeTag, CountryTag,
    ];

    /// <summary>PidTagContainerFlags' AB_SUBCONTAINERS: a container with lists under it.</summary>
    private const uint SubContainers = 0x2;

    /// <summary>The Global Address List's id here: its DN in the hierarchy table.</summary>
    public const string GalId = "/";

    public const string GalName = "Global Address List";

    private readonly Uri _url;
    private readonly HttpClient _http;
    private readonly MailboxPace _pace;
    private readonly ILogger _logger;
    private readonly string _application;
    private readonly string _clientInfo = Guid.NewGuid().ToString("B").ToUpperInvariant() + ":1";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;

    /// <param name="url">The address book endpoint (https://mail.contoso.com/mapi/nspi/).</param>
    /// <param name="http">The client EWS goes through: its handler signs the requests in. Cookies are kept here, not by it.</param>
    public NspiAddressBook(Uri url, HttpClient http, MailboxPace pace, ILogger logger)
    {
        _url = url;
        _http = http;
        _pace = pace;
        _logger = logger;

        // the server takes any name but wants a four-part version (Spike 16: "AnyFS/0.2" was refused as not properly formatted)
        _application = "AnyFS/" + (typeof(NspiAddressBook).Assembly.GetName().Version ?? new Version(0, 0, 0, 0)).ToString(4);
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    public async Task<IReadOnlyList<AddressList>> AddressListsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // a new session each time the lists are asked for: they are what a session's minimal ids are read from
            _session = null;
            return (await SessionAsync(ct).ConfigureAwait(false)).Lists;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DirectoryPage> AddressListPageAsync(string listId, int offset, int count, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var bound = 0; ; bound++)
            {
                var session = await SessionAsync(ct).ConfigureAwait(false);
                if (!session.Containers.TryGetValue(listId, out var container))
                {
                    throw new EwsException("ErrorAddressListNotFound", $"{_url.Host}'s address book has no list {listId} now");
                }

                try
                {
                    return await QueryRowsAsync(session, container, offset, count, ct).ConfigureAwait(false);
                }
                catch (LostSessionException) when (bound == 0)
                {
                    // the server forgot the session (idle too long, restarted): bound again, the list's id read again
                    _session = null;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The session context, bound and its lists read when there is none (or the last was lost).</summary>
    private async Task<Session> SessionAsync(CancellationToken ct)
    {
        if (_session is { } current)
        {
            return current;
        }

        var session = new Session();
        for (var bound = 0; ; bound++)
        {
            try
            {
                // Bind: Flags, HasState, STAT, AuxiliaryBufferSize; the answer's StatusCode, ErrorCode, ServerGuid
                var bind = await SendAsync(session, "Bind", w => { w.Write(0u); w.Write((byte)1); Stat(w, 0, 0); w.Write(0u); }, ct).ConfigureAwait(false);
                Check("Bind", bind.U32(), bind.U32());

                // GetSpecialTable with NspiUnicodeStrings: the address book hierarchy table, a row (a property value list) per container
                var table = await SendAsync(session, "GetSpecialTable", w => { w.Write(0x4u); w.Write((byte)1); Stat(w, 0, 0); w.Write((byte)1); w.Write(0u); w.Write(0u); }, ct).ConfigureAwait(false);
                Check("GetSpecialTable", table.U32(), table.U32());
                table.U32(); // the code page
                if (table.U8() != 0)
                {
                    table.U32(); // the table's version
                }

                var lists = new List<AddressList>();
                var rows = table.U8() != 0 ? table.U32() : 0;
                for (var i = 0; i < rows; i++)
                {
                    var row = table.ValueList();
                    var dn = row.GetValueOrDefault(Id(EntryIdTag)) is byte[] entryId ? NspiReader.EntryDn(entryId) : null;
                    var id = row.GetValueOrDefault(Id(ContainerIdTag)) is uint mid ? mid : 0u;
                    var depth = row.GetValueOrDefault(Id(DepthTag)) is uint d ? d : 0u;
                    var flags = row.GetValueOrDefault(Id(ContainerFlagsTag)) is uint f ? f : 0u;
                    var name = (row.GetValueOrDefault(Id(DisplayNameTag)) as string)?.Trim();
                    if (dn == GalId || id == 0 && string.IsNullOrEmpty(name))
                    {
                        session.Containers[GalId] = id;
                        lists.Insert(0, new AddressList(GalId, GalName));
                    }
                    else if (dn is not null && !string.IsNullOrEmpty(name) && !(depth == 0 && (flags & SubContainers) != 0))
                    {
                        // the lists under All Address Lists (any depth), not the container itself
                        session.Containers[dn] = id;
                        lists.Add(new AddressList(dn, name));
                    }
                }

                session.Lists = lists;
                _session = session;
                return session;
            }
            catch (LostSessionException) when (bound == 0)
            {
                session = new Session();
            }
        }
    }

    /// <summary>QueryRows over a container from <paramref name="offset"/> (CurrentRec at the table's start, Delta the offset): the entries and the list's count.</summary>
    private async Task<DirectoryPage> QueryRowsAsync(Session session, uint container, int offset, int count, CancellationToken ct)
    {
        var page = await SendAsync(session, "QueryRows", w =>
        {
            w.Write(0u); // Flags: permanent entry ids
            w.Write((byte)1);
            Stat(w, container, offset);
            w.Write(0u); // no explicit table: the container's own rows
            w.Write((uint)count);
            w.Write((byte)1);
            w.Write((uint)EntryColumns.Length);
            foreach (var tag in EntryColumns)
            {
                w.Write(tag);
            }

            w.Write(0u);
        }, ct).ConfigureAwait(false);
        Check("QueryRows", page.U32(), page.U32());
        var total = 0;
        if (page.U8() != 0)
        {
            // the STAT after the read: TotalRecs is the list's count
            page.Skip(20);
            total = (int)page.U32();
            page.Skip(12);
        }

        var entries = new List<DirectoryEntry>();
        if (page.U8() != 0)
        {
            var columns = new uint[page.U32()];
            for (var i = 0; i < columns.Length; i++)
            {
                columns[i] = page.U32();
            }

            var rows = page.U32();
            for (var i = 0; i < rows; i++)
            {
                if (EntryOf(page.Row(columns)) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        return new DirectoryPage(entries, total);
    }

    /// <summary>A STAT ([MS-OXNSPI] 2.2.8): sorted by display name, the container, the start of its table, the offset from there; Windows-1252 and en-US.</summary>
    private static void Stat(BinaryWriter w, uint container, int delta)
    {
        w.Write(0u); // SortType: SortTypeDisplayName
        w.Write(container); // ContainerID: 0 is the Global Address List
        w.Write(0u); // CurrentRec: MID_BEGINNING_OF_TABLE
        w.Write(delta); // Delta
        w.Write(0u); // NumPos
        w.Write(0u); // TotalRecs
        w.Write(1252u); // CodePage, for 8-bit strings (strings are asked for as Unicode)
        w.Write(0x409u); // TemplateLocale
        w.Write(0x409u); // SortLocale
    }

    /// <summary>A response body's StatusCode and ErrorCode, which are both 0 when the request did what it was asked.</summary>
    private void Check(string requestType, uint status, uint error)
    {
        if (status != 0 || error != 0)
        {
            throw new EwsException(
                "Nspi" + (error != 0 ? error : status).ToString("X8", CultureInfo.InvariantCulture),
                $"{_url.Host}'s address book answered {requestType} with {(status != 0 ? "status" : "error")} 0x{(status != 0 ? status : error):X8}");
        }
    }

    /// <summary>A list's row as a directory entry; null for one with neither a name nor an address. Only SMTP addresses are kept, the primary first.</summary>
    internal static DirectoryEntry? EntryOf(IReadOnlyDictionary<ushort, object?> row)
    {
        string? Text(uint tag) => row.GetValueOrDefault(Id(tag)) is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        var proxies = row.GetValueOrDefault(Id(ProxyAddressesTag)) as string[] ?? [];
        var emails = new List<string>();
        void Email(string? address)
        {
            if (address is { Length: > 0 } a && a.Contains('@') && !emails.Contains(a, StringComparer.OrdinalIgnoreCase))
            {
                emails.Add(a);
            }
        }

        // the primary address, then the others ("SMTP:" is the primary among the proxies, "smtp:" the rest)
        Email(Text(SmtpAddressTag));
        foreach (var proxy in proxies.Where(p => p.StartsWith("SMTP:", StringComparison.Ordinal)).Concat(proxies.Where(p => p.StartsWith("smtp:", StringComparison.Ordinal))))
        {
            Email(proxy[5..].Trim());
        }

        var name = Text(DisplayNameTag) ?? emails.FirstOrDefault();
        if (name is null)
        {
            return null;
        }

        var phones = new List<DirectoryPhone>();
        foreach (var (tag, type) in new[] { (BusinessPhoneTag, "WORK,VOICE"), (MobilePhoneTag, "CELL,VOICE"), (HomePhoneTag, "HOME,VOICE"), (PagerTag, "PAGER"), (BusinessFaxTag, "WORK,FAX") })
        {
            if (Text(tag) is { } number && !phones.Any(p => p.Number == number))
            {
                phones.Add(new DirectoryPhone(type, number));
            }
        }

        var address = new DirectoryAddress("WORK", Text(StreetTag), Text(CityTag), Text(StateTag), Text(PostalCodeTag), Text(CountryTag));
        var sip = proxies.FirstOrDefault(p => p.StartsWith("sip:", StringComparison.OrdinalIgnoreCase));
        var dn = row.GetValueOrDefault(Id(EntryIdTag)) is byte[] entryId ? NspiReader.EntryDn(entryId) : null;
        return new DirectoryEntry(dn ?? emails.FirstOrDefault() ?? name, name, KindOf(row))
        {
            GivenName = Text(GivenNameTag),
            MiddleName = Text(MiddleNameTag),
            Surname = Text(SurnameTag),
            Company = Text(CompanyTag),
            Department = Text(DepartmentTag),
            JobTitle = Text(TitleTag),
            Office = Text(OfficeTag),
            Emails = emails,
            Phones = phones,
            Addresses = (address.Street ?? address.City ?? address.State ?? address.PostalCode ?? address.Country) is null ? [] : [address],
            ImAddress = sip is null ? null : "sip:" + sip[4..].Trim(),
        };
    }

    /// <summary>What an entry is, from its display types ([MS-OXOABK]: PidTagDisplayTypeEx's low byte for a room or equipment, else PidTagDisplayType).</summary>
    private static string? KindOf(IReadOnlyDictionary<ushort, object?> row)
    {
        if (row.GetValueOrDefault(Id(DisplayTypeExTag)) is uint ex && (ex & 0xFF) is 7u or 8u)
        {
            return (ex & 0xFF) == 7u ? "Room" : "Equipment";
        }

        return row.GetValueOrDefault(Id(DisplayTypeTag)) switch
        {
            0u or 6u => "Person", // a mailbox user; a mail contact
            1u or 5u => "DistributionList",
            2u => "PublicFolder",
            _ => null,
        };
    }

    private static ushort Id(uint tag) => (ushort)(tag >> 16);

    /// <summary>
    /// One request in the session: paced, signed in by the client's handler, the session's cookies sent and those
    /// answered kept; the response body after its meta-tags. Tried again on a busy server and a dropped connection; a lost
    /// session context throws <see cref="LostSessionException"/>, any other refusal an <see cref="EwsException"/>.
    /// </summary>
    private async Task<NspiReader> SendAsync(Session session, string requestType, Action<BinaryWriter> write, CancellationToken ct)
    {
        byte[] body;
        using (var buffer = new MemoryStream())
        {
            using (var w = new BinaryWriter(buffer, Encoding.Unicode, leaveOpen: true))
            {
                write(w);
            }

            body = buffer.ToArray();
        }

        for (var attempt = 1; ; attempt++)
        {
            using var slot = await _pace.EnterAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/mapi-http");
            request.Headers.Add("X-RequestType", requestType);
            request.Headers.Add("X-RequestId", $"{session.RequestGuid}:{++session.Counter}");
            request.Headers.Add("X-ClientInfo", _clientInfo);
            request.Headers.Add("X-ClientApplication", _application);
            if (session.Cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", session.Cookies.Select(c => c.Key + "=" + c.Value)));
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < Attempts && ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded or HttpRequestError.Unknown)
            {
                _logger.LogInformation("outlook: {Request} to {Host}'s address book failed ({Error}); trying again", requestType, _url.Host, ex.Message);
                _pace.Hold(null);
                continue;
            }

            using (response)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new EwsException("401", $"{_url.Host} refused the Windows sign-in for its address book ({string.Join(", ", response.Headers.WwwAuthenticate.Select(w => w.Scheme))} asked for)");
                }

                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    _pace.Hold(response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow));
                    if (attempt < Attempts)
                    {
                        continue;
                    }

                    throw new EwsException(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), $"{_url.Host}'s address book is busy ({(int)response.StatusCode}); try later", transient: true);
                }

                Keep(session, response);
                var code = response.Headers.TryGetValues("X-ResponseCode", out var codes) && int.TryParse(codes.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : (int?)null;
                if (code is { } refused and not 0)
                {
                    throw Refusal(requestType, refused);
                }

                if (code is null || !response.IsSuccessStatusCode)
                {
                    // no MAPI over HTTP answer at all: an older server, or one with it turned off
                    throw new EwsException(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                        $"{_url.Host} has no MAPI over HTTP address book at {_url.AbsolutePath} ({(int)response.StatusCode} {response.ReasonPhrase})", transient: (int)response.StatusCode >= 500);
                }

                return Body(requestType, bytes);
            }
        }
    }

    /// <summary>The cookies an answer sets, kept for the session's next request (an emptied or expired one dropped).</summary>
    private static void Keep(Session session, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return;
        }

        foreach (var cookie in cookies)
        {
            var pair = cookie.Split(';', 2)[0];
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var name = pair[..equals].Trim();
            var value = pair[(equals + 1)..].Trim();
            if (value.Length == 0 || cookie.Contains("expires=Thu, 01-Jan-1970", StringComparison.OrdinalIgnoreCase))
            {
                session.Cookies.Remove(name);
            }
            else
            {
                session.Cookies[name] = value;
            }
        }
    }

    /// <summary>A refusal ([MS-OXCMAPIHTTP] 2.2.3.3.3, X-ResponseCode) as the exception that says it.</summary>
    private Exception Refusal(string requestType, int code)
    {
        if (code is 6 or 10 or 13)
        {
            // Invalid Context Cookie, Context Not Found, Missing Cookie: the session context is gone
            return new LostSessionException();
        }

        var meaning = code switch
        {
            1 => "an unknown failure",
            4 => "an invalid header",
            5 => "a request type it does not know",
            7 => "a header missing or not properly formatted",
            8 => "an anonymous request",
            9 => "a request too large",
            11 => "no privilege",
            12 => "an invalid request body",
            15 => "requests out of sequence",
            16 => "the endpoint is disabled",
            18 => "the endpoint is shutting down",
            _ => "code " + code.ToString(CultureInfo.InvariantCulture),
        };
        return new EwsException("Nspi" + code.ToString(CultureInfo.InvariantCulture), $"{_url.Host}'s address book refused {requestType}: {meaning} (X-ResponseCode {code})", transient: code is 1 or 18);
    }

    /// <summary>The response body: after PROCESSING, PENDING... and DONE, the extra headers (X-ElapsedTime, X-StartTime, a late X-ResponseCode) and an empty line.</summary>
    private NspiReader Body(string requestType, byte[] bytes)
    {
        var done = bytes.AsSpan().IndexOf("DONE\r\n"u8);
        if (done < 0)
        {
            throw new EwsException("NspiBody", $"{_url.Host}'s address book answered {requestType} without DONE");
        }

        var at = done + 6;
        while (true)
        {
            var end = bytes.AsSpan(at).IndexOf("\r\n"u8);
            if (end < 0)
            {
                throw new EwsException("NspiBody", $"{_url.Host}'s address book answered {requestType} with its headers cut short");
            }

            var line = Encoding.ASCII.GetString(bytes, at, end);
            at += end + 2;
            if (line.Length == 0)
            {
                return new NspiReader(bytes, at, $"{_url.Host}'s address book answered {requestType}");
            }

            if (line.StartsWith("X-ResponseCode:", StringComparison.OrdinalIgnoreCase) && int.TryParse(line[15..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var late) && late != 0)
            {
                throw Refusal(requestType, late);
            }
        }
    }

    /// <summary>A session context: the cookies that are it, its request ids, and the address lists' minimal ids in it.</summary>
    private sealed class Session
    {
        public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

        public string RequestGuid { get; } = Guid.NewGuid().ToString("B").ToUpperInvariant();

        public int Counter { get; set; }

        /// <summary>The lists' minimal entry ids in this session, by their ids (DNs).</summary>
        public Dictionary<string, uint> Containers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<AddressList> Lists { get; set; } = [];
    }

    /// <summary>The server no longer has the session context: it is bound again.</summary>
    private sealed class LostSessionException() : Exception("the address book's session context was lost");
}
