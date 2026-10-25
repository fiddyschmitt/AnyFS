using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnyFS.Sources.Tests;

/// <summary>A document the fake platform holds: its 3DX fields as written on the wire, and its files.</summary>
internal sealed record FakeDocument(string Id, string Name, string? Title, string Revision, string Type, string Originated, string Modified, List<FakeFile> Files);

/// <summary>A file of a fake document: its content, and its size as 3DX writes it (null for none).</summary>
internal sealed record FakeFile(string Id, string Title, byte[] Content, string Originated, string Modified, bool SizeGiven = true);

/// <summary>
/// A small 3DEXPERIENCE platform for the 3DX source's tests (spec 7.6), on HttpListener at 127.0.0.1: 3DPassport as a CAS
/// server (its CASTGC cookie, service tickets, its scripted sign-in by <c>login?action=get_auth_params</c>), 3DSpace and
/// 3DSearch as CAS clients with sessions of their own (JSESSIONID, by path), the document, ticket, attribute, person and
/// services calls Mount3DX makes, 3DSearch's paged search, a file server that honours ranges (or not), and an identity
/// provider behind 3DPassport that signs in by Windows sign-in, a password page, a smartcard badge (on a port of its own, as
/// Mount3DX's) or a script only a browser runs. Sessions can be expired to see the source renew them.
/// </summary>
internal sealed class Fake3DX : IDisposable
{
    public enum Passport
    {
        /// <summary>3DPassport's own sign-in: a page drawn by script, the user name and password posted with a login ticket.</summary>
        Form,

        /// <summary>3DPassport signs in by the Windows sign-in (Negotiate), as a Kerberos-enabled 3DPassport does.</summary>
        Negotiate,

        /// <summary>3DPassport hands the sign-in to an identity provider by SAML (<see cref="Idp"/> says how that signs in).</summary>
        Saml,
    }

    public enum Idp
    {
        Negotiate,
        Password,
        Badge,

        /// <summary>A page that signs in only by running its script (a browser).</summary>
        Script,
    }

    private const string TicketParameter = "ticket";

    private readonly HttpListener _listener = new();
    private readonly HttpListener _badgeListener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, string> _ticketGrantingTickets = new();
    private readonly ConcurrentDictionary<string, string> _serviceTickets = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, bool> _loginTickets = new();
    private readonly ConcurrentDictionary<string, bool> _samlResponses = new();
    private readonly ConcurrentDictionary<string, string> _relays = new();
    private readonly ConcurrentDictionary<string, string> _pendingSignIns = new();
    private readonly ConcurrentDictionary<string, (FakeDocument Document, FakeFile File)> _downloads = new();
    private int _counter;

    /// <param name="idpHost">The identity provider's host: the platform's own (127.0.0.1), or localhost for a site of its own, as a browser tells an organisation's sign-in apart from 3DX.</param>
    public Fake3DX(Passport passport = Passport.Form, Idp idp = Idp.Negotiate, string idpHost = "127.0.0.1")
    {
        PassportMode = passport;
        IdpMode = idp;
        IdpHost = idpHost;
        Port = FreePort();
        BadgePort = FreePort();
        foreach (var host in new[] { "127.0.0.1", idpHost }.Distinct())
        {
            _listener.Prefixes.Add($"http://{host}:{Port}/");
            _badgeListener.Prefixes.Add($"http://{host}:{BadgePort}/");
        }

        _listener.AuthenticationSchemeSelectorDelegate = SchemeFor;
        _listener.Start();
        _badgeListener.Start();
        _ = ServeAsync(_listener);
        _ = ServeAsync(_badgeListener);
    }

    public Passport PassportMode { get; }

    public Idp IdpMode { get; }

    public string IdpHost { get; }

    public int Port { get; }

    public int BadgePort { get; }

    public string Origin => $"http://127.0.0.1:{Port}";

    /// <summary>Where the identity provider and its sign-in page (GAS) answer.</summary>
    public string IdpOrigin => $"http://{IdpHost}:{Port}";

    /// <summary>Whether the identity provider's pages ask a browser to keep them apart from the pages that led there (Cross-Origin-Opener-Policy: same-origin), as some sign-in pages do.</summary>
    public bool IdpOpenerPolicy { get; set; }

    /// <summary>Whether the script-only sign-in shows a notice in a dialog of the browser's (alert) before it goes on, as some sign-in pages do.</summary>
    public bool IdpAlert { get; set; }

    /// <summary>What the source is given: the 3DSpace service.</summary>
    public string SpaceUrl => $"{Origin}/3dspace";

    public string User { get; set; } = "tester";

    public string Password { get; set; } = "s3cret";

    public List<FakeDocument> Documents { get; } = [];

    /// <summary>Whether the file server answers a range with just that range (206); false sends the whole file (200).</summary>
    public bool HonoursRanges { get; set; } = true;

    /// <summary>How long 3DSearch's search and 3DSpace's documents call take to answer, as a server far away does; a change applies to the calls already waiting too.</summary>
    public TimeSpan ListingDelay { get; set; }

    private async Task ListingWaitAsync()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < ListingDelay)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, ListingDelay.TotalMilliseconds)));
        }
    }

    /// <summary>Whether 3DSpace's API answers a request without a session with a bare 401 instead of sending it to 3DPassport (its pages still do).</summary>
    public bool ApiRefusesWithoutSession { get; set; }

    /// <summary>Whether the badge sign-in refuses (a PIN nobody gave).</summary>
    public bool BadgeRefuses { get; set; }

    /// <summary>Whether the identity provider's page names the badge sign-in's path in an inline script of its own, rather than in a script file.</summary>
    public bool BadgePathInline { get; set; }

    /// <summary>Whether the page's configuration gives the badge sign-in's port as text ("50123") rather than as a number.</summary>
    public bool BadgePortAsText { get; set; }

    /// <summary>Whether the identity provider sends the browser to its sign-in page with the resume parameters after a '#' (<c>/gas/#/?resumeUrl=...</c>, an application that routes by its fragment) rather than in the query.</summary>
    public bool ResumeInFragment { get; set; }

    /// <summary>Whether 3DPassport, given a SAML response without a relay state, shows its own page (signed in) rather than sending the client on to the service.</summary>
    public bool StaysWithoutRelayState { get; set; }

    /// <summary>Whether a ticket's PUT with a CSRF token 3DSpace does not take is answered 200 with an error of 3DSpace's own and no ticket, rather than 403.</summary>
    public bool WrongTokenAnsweredOk { get; set; }

    /// <summary>How many of the next tickets' PUTs (with a right token) are answered 200 with an error and no ticket, as a 3DSpace that cannot give one answers.</summary>
    public int TicketlessAnswers
    {
        get => Volatile.Read(ref _ticketlessAnswers);
        set => Volatile.Write(ref _ticketlessAnswers, value);
    }

    private int _ticketlessAnswers;

    /// <summary>How many of the next documents calls are answered 500 with 3DSpace's error of its own, as a server under strain answers.</summary>
    public int DocumentErrors
    {
        get => Volatile.Read(ref _documentErrors);
        set => Volatile.Write(ref _documentErrors, value);
    }

    private int _documentErrors;

    /// <summary>Whether 3DSpace's and 3DSearch's sessions time out right after the next CSRF token is given: the request that carries it is sent to the sign-in.</summary>
    public bool ExpireSessionsAfterNextCsrf { get; set; }

    /// <summary>Tickets' PUTs refused for their CSRF token (missing, of another session, or one no longer taken).</summary>
    public int TicketsRefused => _ticketsRefused;

    private int _ticketsRefused;

    /// <summary>The SecurityContext header of each ticket's PUT, in order (null for none).</summary>
    public ConcurrentQueue<string?> TicketContexts { get; } = new();

    /// <summary>One of a count of answers to give, if any are left.</summary>
    private static bool TakeOne(ref int count)
    {
        for (var left = Volatile.Read(ref count); left > 0; left = Volatile.Read(ref count))
        {
            if (Interlocked.CompareExchange(ref count, left - 1, left) == left)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every session's CSRF token is replaced by a new one, and the old ones are no longer taken, as tokens that lapsed with time.</summary>
    public void RenewCsrfTokens()
    {
        foreach (var session in _sessions.Values)
        {
            session.Csrf = Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>Every request, in order: its method, path (the badge port's prefixed with its port), user agent and whether it carried cookies.</summary>
    public ConcurrentQueue<(string Method, string Path, string? UserAgent, bool HadCookies)> Requests { get; } = new();

    /// <summary>How many times the person signed in was asked for (their security context with it).</summary>
    public int PersonRequests => Requests.Count(r => r.Path == "/3dspace/resources/modeler/pno/person");

    /// <summary>How many badge sign-ins were asked for: each one a PIN prompt on the person's screen.</summary>
    public int BadgeAttempts => _badgeAttempts;

    private int _badgeAttempts;

    public int SearchPosts => _searchPosts;

    public int DocumentPosts => _documentPosts;

    /// <summary>Sign-ins 3DPassport took with credentials (not with its own cookie): a password, the Windows sign-in, a SAML answer.</summary>
    public int SignIns => _signIns;

    /// <summary>Service tickets 3DSpace or 3DSearch took: each opened a session.</summary>
    public int SessionsOpened => _sessionsOpened;

    public int Downloads => _downloads.Count;

    public int RangedDownloads => _rangedDownloads;

    private int _searchPosts;
    private int _documentPosts;
    private int _signIns;
    private int _sessionsOpened;
    private int _rangedDownloads;

    /// <summary>3DSpace's and 3DSearch's sessions time out; 3DPassport's sign-in (CASTGC) stands.</summary>
    public void ExpireSessions() => _sessions.Clear();

    /// <summary>Every session and 3DPassport's sign-in time out: only a sign-in with credentials opens them again.</summary>
    public void ExpireAll()
    {
        _sessions.Clear();
        _ticketGrantingTickets.Clear();
    }

    public static string NewId() => Guid.NewGuid().ToString("N").ToUpperInvariant();

    /// <summary>A document with files of the given titles and contents.</summary>
    public FakeDocument Add(string name, string revision, string? title, string type = "Document", params (string Title, byte[] Content)[] files)
    {
        var document = new FakeDocument(NewId(), name, title, revision, type, "03/07/2018 10:12:33 AM", "14/02/2024 4:05:06 PM",
            files.Select(f => new FakeFile(NewId(), f.Title, f.Content, "03/07/2018 10:12:33 AM", "14/02/2024 4:05:06 PM")).ToList());
        lock (Documents)
        {
            Documents.Add(document);
        }

        return document;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _badgeListener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private string Next(string prefix) => $"{prefix}-{Interlocked.Increment(ref _counter)}-{Guid.NewGuid():N}";

    /// <summary>HttpListener asks per request: the Windows sign-in where 3DPassport or the identity provider takes it, nothing elsewhere.</summary>
    private AuthenticationSchemes SchemeFor(HttpListenerRequest request)
    {
        var path = request.Url!.AbsolutePath;
        var windows = (PassportMode == Passport.Negotiate && path == "/3dpassport/login" && request.QueryString["service"] is not null && !HasTicketGrantingTicket(request) && request.HttpMethod == "GET")
            || (PassportMode == Passport.Saml && IdpMode == Idp.Negotiate && path == "/idp/sso");
        return windows ? AuthenticationSchemes.Negotiate | AuthenticationSchemes.Ntlm : AuthenticationSchemes.Anonymous;
    }

    private async Task ServeAsync(HttpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(context, listener == _badgeListener);
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
                {
                }
                finally
                {
                    try
                    {
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                    }
                }
            });
        }
    }

    private async Task HandleAsync(HttpListenerContext context, bool badge)
    {
        var request = context.Request;
        var path = request.Url!.AbsolutePath;
        Requests.Enqueue((request.HttpMethod, (badge ? $":{BadgePort}" : "") + path, request.UserAgent, request.Headers["Cookie"] is not null));
        if (badge)
        {
            await BadgeAsync(context);
            return;
        }

        if (path.StartsWith("/3dpassport/", StringComparison.Ordinal))
        {
            await PassportAsync(context);
        }
        else if (path.StartsWith("/idp/", StringComparison.Ordinal))
        {
            await IdentityProviderAsync(context);
        }
        else if (path.StartsWith("/gas/", StringComparison.Ordinal))
        {
            await GasAsync(context);
        }
        else if (path.StartsWith("/fcs/", StringComparison.Ordinal))
        {
            await FileServerAsync(context);
        }
        else if (path.StartsWith("/3dspace", StringComparison.Ordinal))
        {
            if (await SessionAsync(context, "3dspace") is { } session)
            {
                await SpaceAsync(context, session);
            }
        }
        else if (path.StartsWith("/federated", StringComparison.Ordinal))
        {
            if (await SessionAsync(context, "federated") is { } session)
            {
                await SearchAsync(context, session);
            }
        }
        else
        {
            context.Response.StatusCode = 404;
        }
    }

    // --- 3DPassport -------------------------------------------------------------------------------------------------------

    private bool HasTicketGrantingTicket(HttpListenerRequest request)
        => request.Cookies["CASTGC"] is { } cookie && _ticketGrantingTickets.ContainsKey(cookie.Value);

    private async Task PassportAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url!.AbsolutePath;
        var service = request.QueryString["service"];
        if (path == "/3dpassport/login" && request.HttpMethod == "GET" && request.QueryString["action"] == "get_auth_params")
        {
            var lt = Next("LT");
            _loginTickets[lt] = true;
            await JsonAsync(response, 200, new JsonObject { ["response"] = "login", ["lt"] = lt });
            return;
        }

        if (path == "/3dpassport/login" && request.HttpMethod == "GET" && service is not null)
        {
            if (HasTicketGrantingTicket(request))
            {
                Redirect(response, WithTicket(service));
                return;
            }

            switch (PassportMode)
            {
                case Passport.Negotiate when context.User?.Identity?.IsAuthenticated == true:
                    SignedIn(response, service);
                    return;
                case Passport.Saml:
                    // the sign-in on its way is remembered in 3DPassport's own session too, so an answer without the relay
                    // state (Mount3DX posts the request alone) still finds its service
                    var pending = Next("PS");
                    _pendingSignIns[pending] = service;
                    response.AppendHeader("Set-Cookie", $"PSESSION={pending}; Path=/3dpassport; HttpOnly");
                    await HtmlAsync(response, 200, $"""
                        <html><head><title>Redirecting</title></head><body onload="document.forms[0].submit()">
                        <form method="post" action="{IdpOrigin}/idp/sso"><input type="hidden" name="SAMLRequest" value="{Convert.ToBase64String(Encoding.UTF8.GetBytes(Next("AuthnRequest")))}"/><input type="hidden" name="RelayState" value="{WebUtility.HtmlEncode(service)}"/><noscript><input type="submit" value="Continue"/></noscript></form>
                        </body></html>
                        """);
                    return;
                default:
                    // 3DPassport's sign-in page: an application its script draws, no form in the page
                    await HtmlAsync(response, 200, "<html><head><title>3DEXPERIENCE platform</title><script src=\"/3dpassport/app.js\"></script></head><body><div id=\"app\"></div></body></html>");
                    return;
            }
        }

        if (path == "/3dpassport/login" && request.HttpMethod == "POST" && service is not null)
        {
            var form = await FormAsync(request);
            if (form.TryGetValue("lt", out var lt) && _loginTickets.TryRemove(lt, out _) && form.GetValueOrDefault("username") == User && form.GetValueOrDefault("password") == Password)
            {
                SignedIn(response, service);
                return;
            }

            await HtmlAsync(response, 200, "<html><head><title>3DEXPERIENCE platform</title></head><body>Invalid user name or password</body></html>");
            return;
        }

        if (path == "/3dpassport/saml/acs" && request.HttpMethod == "POST")
        {
            var form = await FormAsync(request);
            var given = form.GetValueOrDefault("RelayState") is { Length: > 0 } relay ? relay : null;
            var remembered = request.Cookies["PSESSION"] is { } session && _pendingSignIns.TryGetValue(session.Value, out var pending) ? pending : null;
            if (!form.TryGetValue("SAMLResponse", out var answer) || !_samlResponses.TryRemove(answer, out _) || given is null && remembered is null)
            {
                response.StatusCode = 403;
                return;
            }

            if (given is null && StaysWithoutRelayState)
            {
                await SignedInHereAsync(response);
                return;
            }

            SignedIn(response, given ?? remembered!);
            return;
        }

        if (path == "/3dpassport/app.js")
        {
            await TextAsync(response, 200, "application/javascript", "document.getElementById('app').innerText = 'Sign in';");
            return;
        }

        response.StatusCode = 404;
    }

    /// <summary>3DPassport's own sign-in done: its cookie set, and the browser sent back to the service with a ticket.</summary>
    private void SignedIn(HttpListenerResponse response, string service)
    {
        SetTicketGrantingTicket(response);
        Redirect(response, WithTicket(service));
    }

    /// <summary>3DPassport's own sign-in done, and its own page shown: a service opens through its cookie at the next request.</summary>
    private Task SignedInHereAsync(HttpListenerResponse response)
    {
        SetTicketGrantingTicket(response);
        return HtmlAsync(response, 200, "<html><head><title>3DEXPERIENCE platform</title></head><body>You are signed in.</body></html>");
    }

    private void SetTicketGrantingTicket(HttpListenerResponse response)
    {
        Interlocked.Increment(ref _signIns);
        var tgt = Next("TGC");
        _ticketGrantingTickets[tgt] = User;
        response.AppendHeader("Set-Cookie", $"CASTGC={tgt}; Path=/3dpassport; HttpOnly");
    }

    private string WithTicket(string service)
    {
        var ticket = Next("ST");
        _serviceTickets[ticket] = service;
        return service + (service.Contains('?') ? "&" : "?") + TicketParameter + "=" + Uri.EscapeDataString(ticket);
    }

    // --- 3DSpace and 3DSearch as CAS clients ----------------------------------------------------------------------------

    private sealed class Session(string service)
    {
        public string Service { get; } = service;

        public string Csrf { get; set; } = Guid.NewGuid().ToString("N");
    }

    /// <summary>The request's session for the service, or null when the response is already written: a ticket taken (and the client sent on without it), or the client sent to 3DPassport.</summary>
    private async Task<Session?> SessionAsync(HttpListenerContext context, string service)
    {
        var request = context.Request;
        var response = context.Response;
        if (request.QueryString[TicketParameter] is { } ticket)
        {
            var url = request.Url!.AbsoluteUri;
            var without = RemoveTicket(url);
            if (_serviceTickets.TryRemove(ticket, out var forService) && RemoveTicket(forService) == without)
            {
                Interlocked.Increment(ref _sessionsOpened);
                var id = Next("JS");
                _sessions[id] = new Session(service);
                response.AppendHeader("Set-Cookie", $"JSESSIONID={id}; Path=/{service}; HttpOnly");
                Redirect(response, without);
                return null;
            }

            response.StatusCode = 403;
            await Task.CompletedTask;
            return null;
        }

        if (request.Cookies["JSESSIONID"] is { } cookie && _sessions.TryGetValue(cookie.Value, out var session) && session.Service == service)
        {
            return session;
        }

        if (ApiRefusesWithoutSession && request.Url!.AbsolutePath.Contains("/resources/", StringComparison.Ordinal))
        {
            // an API that refuses a request without a session outright, as a CAS filter set up for scripts may
            response.StatusCode = 401;
            return null;
        }

        Redirect(response, $"{Origin}/3dpassport/login?service={Uri.EscapeDataString(request.Url!.AbsoluteUri)}");
        return null;
    }

    private static string RemoveTicket(string url)
    {
        var at = url.IndexOf("ticket=", StringComparison.Ordinal);
        if (at < 0)
        {
            return url;
        }

        var start = at - 1;
        var end = url.IndexOf('&', at);
        var rest = end < 0 ? "" : url[(end + 1)..];
        var head = url[..start];
        return rest.Length == 0 ? head : head + url[start] + rest;
    }

    private async Task SpaceAsync(HttpListenerContext context, Session session)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url!.AbsolutePath;
        switch (request.HttpMethod, path)
        {
            case ("GET", "/3dspace/resources/v1/application/CSRF"):
                await JsonAsync(response, 200, new JsonObject { ["success"] = true, ["csrf"] = new JsonObject { ["name"] = "ENO_CSRF_TOKEN", ["value"] = session.Csrf } });
                if (ExpireSessionsAfterNextCsrf)
                {
                    ExpireSessionsAfterNextCsrf = false;
                    _sessions.Clear();
                }

                return;
            case ("GET", "/3dspace/resources/AppsMngt/api/v1/services"):
                await JsonAsync(response, 200, new JsonObject
                {
                    ["platforms"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "OnPremise",
                        ["services"] = new JsonArray(
                            new JsonObject { ["id"] = "3dspace", ["name"] = "3DSpace", ["url"] = $"{Origin}/3dspace" },
                            new JsonObject { ["id"] = "3dpassport", ["name"] = "3DPassport", ["url"] = $"{Origin}/3dpassport" },
                            new JsonObject { ["id"] = "3dsearch", ["name"] = "3DSearch", ["url"] = $"{Origin}/federated" }),
                    }),
                });
                return;
            case ("GET", "/3dspace/resources/modeler/pno/person"):
                await JsonAsync(response, 200, new JsonObject
                {
                    ["name"] = User,
                    ["preferredcredentials"] = new JsonObject
                    {
                        ["role"] = new JsonObject { ["name"] = "VPLMProjectLeader" },
                        ["organization"] = new JsonObject { ["name"] = "Company Name" },
                        ["collabspace"] = new JsonObject { ["name"] = "Common Space" },
                    },
                });
                return;
            case ("POST", "/3dspace/resources/v1/modeler/documents/ids"):
                Interlocked.Increment(ref _documentPosts);
                await ListingWaitAsync();
                if (TakeOne(ref _documentErrors))
                {
                    await JsonAsync(response, 500, new JsonObject { ["success"] = false, ["statusCode"] = 500, ["error"] = "An unexpected error has occurred" });
                    return;
                }

                var body = await new StreamReader(request.InputStream).ReadToEndAsync();
                var ids = body.StartsWith("$ids=", StringComparison.Ordinal) ? Uri.UnescapeDataString(body[5..]).Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
                List<FakeDocument> found;
                lock (Documents)
                {
                    found = ids.Select(id => Documents.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).OfType<FakeDocument>().ToList();
                }

                await JsonAsync(response, 200, new JsonObject { ["success"] = true, ["data"] = new JsonArray(found.Select(DocumentJson).ToArray<JsonNode?>()) });
                return;
            case ("POST", "/3dspace/resources/v1/collabServices/attributes/op/read"):
                if (request.Headers["SecurityContext"] != "VPLMProjectLeader.Company Name.Common Space")
                {
                    await JsonAsync(response, 403, new JsonObject { ["error"] = "no security context" });
                    return;
                }

                var read = JsonNode.Parse(await new StreamReader(request.InputStream).ReadToEndAsync())!;
                var busId = read["busIDs"]![0]!.GetValue<string>();
                await JsonAsync(response, 200, new JsonObject { ["results"] = new JsonArray(new JsonObject { ["busID"] = busId, ["attributes"] = new JsonArray(new JsonObject { ["name"] = "Owner", ["value"] = User }) }) });
                return;
            case ("PUT", _) when path.EndsWith("/DownloadTicket", StringComparison.Ordinal):
                TicketContexts.Enqueue(request.Headers["SecurityContext"]);
                if (request.Headers["ENO_CSRF_TOKEN"] != session.Csrf)
                {
                    Interlocked.Increment(ref _ticketsRefused);
                    await JsonAsync(response, WrongTokenAnsweredOk ? 200 : 403, WrongTokenAnsweredOk
                        ? new JsonObject { ["success"] = false, ["statusCode"] = 403, ["error"] = "Invalid CSRF token" }
                        : new JsonObject { ["error"] = "CSRF token is missing or wrong" });
                    return;
                }

                if (TakeOne(ref _ticketlessAnswers))
                {
                    await JsonAsync(response, 200, new JsonObject { ["success"] = false, ["statusCode"] = 500, ["error"] = "No file server for the file's store" });
                    return;
                }

                var parts = path.Split('/');
                var documentId = parts[^4];
                var fileId = parts[^2];
                FakeDocument? document;
                lock (Documents)
                {
                    document = Documents.FirstOrDefault(d => d.Id == documentId);
                }

                var file = document?.Files.FirstOrDefault(f => f.Id == fileId);
                if (document is null || file is null)
                {
                    await JsonAsync(response, 404, new JsonObject { ["error"] = "no such file" });
                    return;
                }

                var job = Next("JOB");
                _downloads[job] = (document, file);
                await JsonAsync(response, 200, new JsonObject { ["data"] = new JsonArray(new JsonObject { ["dataelements"] = new JsonObject { ["ticketURL"] = $"{Origin}/fcs/servlet/fcs/ticket?__fcs__jobTicket={job}" } }) });
                return;
            case ("GET", _):
                await HtmlAsync(response, 200, "<html><head><title>3DSpace</title></head><body>3DEXPERIENCE 3DSpace</body></html>");
                return;
            default:
                response.StatusCode = 405;
                return;
        }
    }

    private static JsonObject DocumentJson(FakeDocument d)
    {
        var elements = new JsonObject { ["name"] = d.Name, ["revision"] = d.Revision, ["typeNLS"] = d.Type, ["originated"] = d.Originated, ["modified"] = d.Modified };
        if (d.Title is not null)
        {
            elements["title"] = d.Title;
        }

        var files = new JsonArray(d.Files.Select(f =>
        {
            var fileElements = new JsonObject { ["title"] = f.Title, ["originated"] = f.Originated, ["modified"] = f.Modified };
            if (f.SizeGiven)
            {
                fileElements["fileSize"] = f.Content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return (JsonNode?)new JsonObject { ["id"] = f.Id, ["type"] = "DOCUMENTS", ["dataelements"] = fileElements };
        }).ToArray());
        return new JsonObject { ["id"] = d.Id, ["type"] = d.Type, ["dataelements"] = elements, ["relateddata"] = new JsonObject { ["files"] = files } };
    }

    private async Task SearchAsync(HttpListenerContext context, Session session)
    {
        _ = session;
        var request = context.Request;
        var response = context.Response;
        if (request.Url!.AbsolutePath != "/federated/search")
        {
            response.StatusCode = 404;
            return;
        }

        if (request.HttpMethod == "GET")
        {
            await JsonAsync(response, 400, new JsonObject { ["error"] = "FS_INVALID_QUERY__NO_QUERY" });
            return;
        }

        Interlocked.Increment(ref _searchPosts);
        await ListingWaitAsync();
        var query = JsonNode.Parse(await new StreamReader(request.InputStream).ReadToEndAsync())!;
        var size = query["nresults"]!.GetValue<int>();
        var start = query["next_start"] is { } next ? int.Parse(next.GetValue<string>().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture) : query["start"]!.GetValue<int>();
        List<FakeDocument> all;
        lock (Documents)
        {
            all = Documents.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
        }

        var page = all.Skip(start).Take(size).ToList();
        var searchId = "SEARCH" + Guid.NewGuid().ToString("N")[..8];
        await JsonAsync(response, 200, new JsonObject
        {
            ["infos"] = new JsonObject { ["nmatches"] = all.Count, ["next_start"] = $"{start + page.Count} 0 {start / Math.Max(1, size) + 1} {searchId}" },
            ["results"] = new JsonArray(page.Select(d => (JsonNode?)new JsonObject
            {
                ["attributes"] = new JsonArray(
                    new JsonObject { ["name"] = "ds6w:label", ["value"] = d.Title ?? d.Name },
                    new JsonObject { ["name"] = "resourceid", ["value"] = d.Id }),
            }).ToArray()),
        });
    }

    // --- the file server ----------------------------------------------------------------------------------------------

    private async Task FileServerAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        if (request.QueryString["__fcs__jobTicket"] is not { } job || !_downloads.TryGetValue(job, out var download))
        {
            response.StatusCode = 403;
            return;
        }

        var content = download.File.Content;
        var range = request.Headers["Range"];
        if (range is not null && HonoursRanges && range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _rangedDownloads);
            var from = long.Parse(range[6..].Split('-')[0], System.Globalization.CultureInfo.InvariantCulture);
            if (from >= content.Length)
            {
                response.StatusCode = 416;
                response.AddHeader("Content-Range", $"bytes */{content.Length}");
                return;
            }

            response.StatusCode = 206;
            response.AddHeader("Content-Range", $"bytes {from}-{content.Length - 1}/{content.Length}");
            response.ContentType = "application/octet-stream";
            response.ContentLength64 = content.Length - from;
            await response.OutputStream.WriteAsync(content.AsMemory((int)from));
            return;
        }

        response.StatusCode = 200;
        response.ContentType = "application/octet-stream";
        response.ContentLength64 = content.Length;
        await response.OutputStream.WriteAsync(content);
    }

    // --- the identity provider -------------------------------------------------------------------------------------

    private async Task IdentityProviderAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url!.AbsolutePath;
        if (IdpOpenerPolicy)
        {
            response.AddHeader("Cross-Origin-Opener-Policy", "same-origin");
        }

        if (path == "/idp/sso" && request.HttpMethod == "POST")
        {
            var form = await FormAsync(request);
            var relay = form.GetValueOrDefault("RelayState") ?? "";
            switch (IdpMode)
            {
                case Idp.Negotiate when context.User?.Identity?.IsAuthenticated == true:
                    await SamlAnswerAsync(response, relay);
                    return;
                case Idp.Password:
                    await HtmlAsync(response, 200, $"""
                        <html><head><title>Sign in</title></head><body>
                        <form method="post" action="/idp/login"><input type="hidden" name="RelayState" value="{WebUtility.HtmlEncode(relay)}"/>
                        <input type="text" name="pf.username"/><input type="password" name="pf.pass"/><input type="checkbox" name="remember"/><input type="submit" name="go" value="Sign on"/></form>
                        </body></html>
                        """);
                    return;
                case Idp.Badge:
                    var resume = "/idp/resume/" + Guid.NewGuid().ToString("N")[..10];
                    _relays[resume] = relay;
                    var parameters = $"resumeUrl={Uri.EscapeDataString(IdpOrigin + "/idp/resume")}&resumePath={Uri.EscapeDataString(resume)}&instanceId=inst7&assuranceLevel=2";
                    Redirect(response, ResumeInFragment ? $"{IdpOrigin}/gas/#/?{parameters}" : $"{IdpOrigin}/gas/?{parameters}");
                    return;
                case Idp.Script:
                    var key = Guid.NewGuid().ToString("N")[..10];
                    _relays[key] = relay;
                    var notice = IdpAlert ? "alert('This system is for authorised use only.'); " : "";
                    await HtmlAsync(response, 200, $"""
                        <html><head><title>Signing in</title></head><body>
                        <script>setTimeout(function () {"{"} {notice}document.cookie = 'idpjs={key}; path=/'; window.location.href = '/idp/js-done'; {"}"}, {(IdpAlert ? 1500 : 0)});</script>
                        </body></html>
                        """);
                    return;
                default:
                    response.StatusCode = 401;
                    return;
            }
        }

        if (path == "/idp/login" && request.HttpMethod == "POST")
        {
            var form = await FormAsync(request);
            if (form.GetValueOrDefault("pf.username") == User && form.GetValueOrDefault("pf.pass") == Password)
            {
                await SamlAnswerAsync(response, form.GetValueOrDefault("RelayState") ?? "");
                return;
            }

            await HtmlAsync(response, 200, "<html><head><title>Sign in</title></head><body>Wrong user name or password</body></html>");
            return;
        }

        if (path == "/idp/js-done")
        {
            if (request.Cookies["idpjs"] is { } cookie && _relays.TryRemove(cookie.Value, out var relay))
            {
                await SamlAnswerAsync(response, relay);
                return;
            }

            await HtmlAsync(response, 200, "<html><head><title>Sign in</title></head><body>This sign-in needs JavaScript.</body></html>");
            return;
        }

        response.StatusCode = 404;
    }

    /// <summary>The identity provider's answer: a form that posts a SAML response to 3DPassport by itself.</summary>
    private async Task SamlAnswerAsync(HttpListenerResponse response, string relay)
    {
        var answer = Convert.ToBase64String(Encoding.UTF8.GetBytes(Next("Assertion")));
        _samlResponses[answer] = true;
        await HtmlAsync(response, 200, $"""
            <html><head><title>Working</title></head><body onload="document.forms[0].submit()">
            <form method="post" action="{Origin}/3dpassport/saml/acs"><input type="hidden" name="SAMLResponse" value="{answer}"/><input type="hidden" name="RelayState" value="{WebUtility.HtmlEncode(relay)}"/></form>
            </body></html>
            """);
    }

    /// <summary>The identity provider's sign-in page that offers the badge: an application drawn by its script, its configuration beside it.</summary>
    private async Task GasAsync(HttpListenerContext context)
    {
        var response = context.Response;
        var path = context.Request.Url!.AbsolutePath;
        if (IdpOpenerPolicy)
        {
            response.AddHeader("Cross-Origin-Opener-Policy", "same-origin");
        }

        const string BadgePath = "window.Tn_INTERNAL_SECURE_BADGE_AUTH=\"/old\";var _INTERNAL_SECURE_BADGE_AUTH=\"/badge/auth\";";
        if (path == "/gas/")
        {
            var inline = BadgePathInline ? $"<script>var a=1;{BadgePath}</script>" : "";
            await HtmlAsync(response, 200, $"<html><head><title>Sign in</title>{inline}<script src=\"js/vendor.js\"></script><script src=\"js/app.js\"></script></head><body><div id=\"root\"></div></body></html>");
        }
        else if (path == "/gas/js/vendor.js")
        {
            await TextAsync(response, 200, "application/javascript", "var vendor = {};");
        }
        else if (path == "/gas/js/app.js")
        {
            await TextAsync(response, 200, "application/javascript", "var a=1;" + (BadgePathInline ? "" : BadgePath));
        }
        else if (path == "/gas/configuration/internal")
        {
            var port = BadgePortAsText ? JsonValue.Create(BadgePort.ToString(System.Globalization.CultureInfo.InvariantCulture)) : JsonValue.Create(BadgePort);
            await JsonAsync(response, 200, new JsonObject { ["configuration"] = new JsonObject { ["internal.secure_badge_auth_port"] = port } });
        }
        else
        {
            response.StatusCode = 404;
        }
    }

    /// <summary>The badge sign-in on its own port: the resume parameters checked, the SAML answer for the sign-in it resumes.</summary>
    private async Task BadgeAsync(HttpListenerContext context)
    {
        Interlocked.Increment(ref _badgeAttempts);
        if (BadgeRefuses)
        {
            // as a smartcard nobody gave the PIN for
            context.Response.StatusCode = 403;
            return;
        }

        // the error page's address as Mount3DX writes it: the sign-in page's scheme, host and path, no port
        var q = context.Request.QueryString;
        if (context.Request.Url!.AbsolutePath != "/badge/auth" || q["pingfedDropoff"] != "true" || q["instanceId"] != "inst7" || q["assuranceLevel"] != "2"
            || q["gasWebClient"] != "true" || q["errorUrl"] != $"http://{IdpHost}/gas/" || q["coordinates"] != "null" || q["countryCodeRequested"] != "false"
            || q["usaItarGeoRequested"] != "false" || q["resumeUrl"] != IdpOrigin + "/idp/resume" || q["resumePath"] is not { } resume || !_relays.TryRemove(resume, out var relay))
        {
            context.Response.StatusCode = 400;
            return;
        }

        await SamlAnswerAsync(context.Response, relay);
    }

    // --- writing answers --------------------------------------------------------------------------------------------------

    private static void Redirect(HttpListenerResponse response, string location)
    {
        response.StatusCode = 302;
        response.RedirectLocation = location;
    }

    private static Task JsonAsync(HttpListenerResponse response, int status, JsonNode body) => TextAsync(response, status, "application/json", body.ToJsonString());

    private static Task HtmlAsync(HttpListenerResponse response, int status, string html) => TextAsync(response, status, "text/html; charset=utf-8", html);

    private static async Task TextAsync(HttpListenerResponse response, int status, string type, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = status;
        response.ContentType = type;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private static async Task<Dictionary<string, string>> FormAsync(HttpListenerRequest request)
    {
        var body = await new StreamReader(request.InputStream).ReadToEndAsync();
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            form[name] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }

        return form;
    }
}
