using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnyFS.Sources.Atlassian;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>
/// What the stand-ins for Jira and Confluence share (spec 7.10): an installation at a context path, its accounts and how
/// they sign in — a personal access token as a bearer token, the two-step verification login of Jira 10.2+ and
/// Confluence 9.1+ and Seraph's older form (each giving a session cookie), Basic authentication where allowed — the
/// application links manifest and the status page that find the installation, 401 for a REST call without a sign-in,
/// <c>X-AUSERNAME</c> on every answer, 429 with Retry-After when told. Answered in-process through <see cref="Handler"/>.
/// </summary>
internal abstract class FakeAtlassian
{
    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private int _nextSession;

    protected FakeAtlassian(string origin, string root, string typeId, string version)
    {
        Origin = origin.TrimEnd('/');
        Root = root.TrimEnd('/');
        TypeId = typeId;
        Version = version;

        // the source paces itself for a real server; a suite of thousands of requests need not wait
    }

    public string Origin { get; }

    /// <summary>The context path the product is installed at (<c>/jira</c>), or empty for the host's root.</summary>
    public string Root { get; }

    public string TypeId { get; }

    public string Version { get; }

    /// <summary>The installation's address (what a source's address starts with).</summary>
    public string Server => Origin + Root;

    /// <summary>Every request answered, oldest first: "GET /jira/rest/api/2/project".</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Whether the two-step verification login is there (Jira 10.2+, Confluence 9.1+); off, only Seraph's form.</summary>
    public bool TwoStepLogin { get; set; } = true;

    /// <summary>Whether Seraph's login form is there (off where the server signs people in through single sign-on only).</summary>
    public bool LoginForm { get; set; } = true;

    /// <summary>Whether Basic authentication is taken on REST calls (off by default in Jira 11 and new Confluence 10).</summary>
    public bool BasicAllowed { get; set; }

    /// <summary>Whether a REST call without a sign-in is served as the anonymous user (a site open to anyone) rather than refused.</summary>
    public bool Anonymous { get; set; }

    /// <summary>A request the test wants answered otherwise: its answer in place of the product's, or null to answer as usual.</summary>
    public Func<Request, Response?>? Fault { get; set; }

    /// <summary>How many requests to answer 429 with Retry-After: 1 before answering again.</summary>
    public int ThrottleNext { get; set; }

    /// <summary>How many failed password sign-ins have been made (the real servers ask for a CAPTCHA after three).</summary>
    public int FailedLogins { get; private set; }

    public sealed class Account(string name, string display, string key)
    {
        public string Name { get; } = name;

        public string Display { get; } = display;

        public string Key { get; } = key;

        public string? Password { get; set; }

        public List<string> Tokens { get; } = [];
    }

    public List<Account> Accounts { get; } = [];

    public Account AddAccount(string name, string display, string? token = null, string? password = null)
    {
        var account = new Account(name, display, "key-" + name) { Password = password };
        if (token is not null)
        {
            account.Tokens.Add(token);
        }

        Accounts.Add(account);
        return account;
    }

    /// <summary>A session cookie for an account, as a browser signed in would hold it (for the web session's stand-in).</summary>
    public SessionCookie SessionFor(string name)
    {
        var id = "S" + Interlocked.Increment(ref _nextSession).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _sessions[id] = name;
        return new SessionCookie("JSESSIONID", id, new Uri(Origin).Host, Root.Length == 0 ? "/" : Root, false, true);
    }

    /// <summary>Ends every session: the next request with one is served to nobody.</summary>
    public void EndSessions() => _sessions.Clear();

    public int Count(string what) => Requests.Count(r => r.Contains(what, StringComparison.Ordinal));

    /// <summary>A handler that answers in-process, without a socket.</summary>
    public HttpMessageHandler Handler() => new InProcessHandler(this);

    /// <summary>A stand-in for the source: its requests come here.</summary>
    public AtlassianStandIn StandIn(Func<bool, CancellationToken, Task<IReadOnlyList<SessionCookie>>>? browser = null) => new(Handler, browser);

    // --- answering -------------------------------------------------------------------------------------------------------

    internal sealed record Request(string Method, Uri Url, string[] Segments, Dictionary<string, string> Query, Dictionary<string, string> Headers, byte[] Body)
    {
        public string? Q(string name) => Query.TryGetValue(name, out var v) ? v : null;

        public int QInt(string name, int fallback) => int.TryParse(Q(name), out var n) ? n : fallback;

        public JsonNode? JsonBody => Body.Length == 0 ? null : JsonNode.Parse(Body);

        public Dictionary<string, string> Form => Encoding.UTF8.GetString(Body).Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "", StringComparer.Ordinal);
    }

    internal sealed record Response(int Status, byte[] Body, string? ContentType = "application/json", Dictionary<string, string>? Headers = null)
    {
        public static Response Json(JsonNode node, int status = 200, Dictionary<string, string>? headers = null) => new(status, Encoding.UTF8.GetBytes(node.ToJsonString()), "application/json;charset=UTF-8", headers);

        public static Response Message(int status, string message) => Json(new JsonObject { ["message"] = message, ["errorMessages"] = new JsonArray((JsonNode)message) }, status);

        public static Response NotFound(string what = "Not Found") => Message(404, what);

        public static Response Html(string html, int status = 200, Dictionary<string, string>? headers = null) => new(status, Encoding.UTF8.GetBytes(html), "text/html;charset=UTF-8", headers);

        public static Response Redirect(string to, Dictionary<string, string>? headers = null) => new(302, [], null, new Dictionary<string, string>(headers ?? [], StringComparer.OrdinalIgnoreCase) { ["Location"] = to });
    }

    /// <summary>The product's own answers: <paramref name="path"/> below the installation, the signed-in account (null for nobody).</summary>
    protected abstract Response AnswerProduct(Request request, string path, Account? user);

    /// <summary>The product's who-am-I call (Jira's myself, Confluence's user/current), answered for the account.</summary>
    protected abstract string WhoPath { get; }

    protected abstract JsonNode Who(Account? user);

    private Response Answer(Request request)
    {
        var full = "/" + string.Join('/', request.Segments);
        Requests.Enqueue($"{request.Method} {full}{request.Url.Query}");
        if (Root.Length > 0 && !full.StartsWith(Root + "/", StringComparison.Ordinal) && full != Root)
        {
            return Response.Html("<html>Not Found</html>", 404);
        }

        var path = full[Root.Length..].TrimStart('/');
        if (path == "status")
        {
            return Response.Json(new JsonObject { ["state"] = "RUNNING" });
        }

        if (path == "rest/applinks/1.0/manifest")
        {
            return Response.Json(new JsonObject { ["typeId"] = TypeId, ["version"] = Version, ["name"] = "fake", ["url"] = Server });
        }

        if (path == "rest/tsv/latest/authenticate" && request.Method == "POST")
        {
            if (!TwoStepLogin)
            {
                return Response.Html("<html>Not Found</html>", 404);
            }

            var body = request.JsonBody;
            return Login(body?["username"]?.GetValue<string>(), body?["password"]?.GetValue<string>(), json: true);
        }

        if (request.Method == "POST" && path is "login.jsp" or "dologin.action")
        {
            if (!LoginForm)
            {
                return Response.Redirect("https://idp.fake.test/saml");
            }

            var form = request.Form;
            return Login(form.GetValueOrDefault("os_username"), form.GetValueOrDefault("os_password"), json: false);
        }

        var (user, refusal) = Authenticate(request);
        if (refusal is not null)
        {
            return refusal;
        }

        if (ThrottleNext > 0 && path.StartsWith("rest/", StringComparison.Ordinal))
        {
            ThrottleNext--;
            return Response.Json(new JsonObject { ["message"] = "Rate limit exceeded" }, 429, new Dictionary<string, string> { ["Retry-After"] = "1" });
        }

        if (Fault?.Invoke(request) is { } faulted)
        {
            return faulted;
        }

        var answer = path == WhoPath ? (user is null && !Anonymous ? Response.Message(401, "Client must be authenticated to access this resource.") : Response.Json(Who(user))) : AnswerProduct(request, path, user);
        var headers = new Dictionary<string, string>(answer.Headers ?? [], StringComparer.OrdinalIgnoreCase) { ["X-AUSERNAME"] = user?.Name ?? "anonymous" };
        return answer with { Headers = headers };
    }

    private Response Login(string? name, string? password, bool json)
    {
        var account = Accounts.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (account is null || account.Password is null || account.Password != password)
        {
            FailedLogins++;
            return json
                ? Response.Json(new JsonObject { ["message"] = "Invalid credentials" }, 401)
                : Response.Html("<html>Sorry, your username and password are incorrect</html>", 200, new Dictionary<string, string> { ["X-Seraph-LoginReason"] = "AUTHENTICATED_FAILED" });
        }

        var cookie = SessionFor(account.Name);
        var headers = new Dictionary<string, string> { ["Set-Cookie"] = $"JSESSIONID={cookie.Value}; Path={cookie.Path}; HttpOnly", ["X-Seraph-LoginReason"] = "OK" };
        return json ? Response.Json(new JsonObject { ["result"] = "success" }, 200, headers) : Response.Redirect(Server + "/", headers);
    }

    /// <summary>Who a request is from: its bearer token's account, its session's, its Basic credentials' (where taken), else nobody; a refusal for a bad token.</summary>
    private (Account? User, Response? Refusal) Authenticate(Request request)
    {
        if (request.Headers.TryGetValue("Authorization", out var authorization))
        {
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authorization[7..].Trim();
                var holder = Accounts.FirstOrDefault(a => a.Tokens.Contains(token));
                return holder is null ? (null, Response.Message(401, "The personal access token is not valid")) : (holder, null);
            }

            if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                if (!BasicAllowed)
                {
                    return (null, Response.Message(403, "Basic Authentication has been disabled on this instance."));
                }

                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim())).Split(':', 2);
                var account = Accounts.FirstOrDefault(a => a.Name.Equals(pair[0], StringComparison.OrdinalIgnoreCase) && a.Password == (pair.Length > 1 ? pair[1] : null));
                return account is null ? (null, new Response(401, [], null, new Dictionary<string, string> { ["X-Seraph-LoginReason"] = "AUTHENTICATED_FAILED" })) : (account, null);
            }
        }

        if (request.Headers.TryGetValue("Cookie", out var cookies))
        {
            foreach (var part in cookies.Split(';', StringSplitOptions.TrimEntries))
            {
                if (part.StartsWith("JSESSIONID=", StringComparison.Ordinal) && _sessions.TryGetValue(part[11..], out var name))
                {
                    return (Accounts.First(a => a.Name == name), null);
                }
            }
        }

        return (null, null);
    }

    /// <summary>A REST answer that needs a signed-in account, for nobody: 401, or (a site open to anyone) what the anonymous user may see.</summary>
    protected Response? Unauthenticated(Account? user) => user is null && !Anonymous ? Response.Message(401, "Client must be authenticated to access this resource.") : null;

    /// <summary>Bytes from an offset, as a server honouring Range answers (206), else whole (200).</summary>
    protected static Response Bytes(Request request, byte[] content, string type = "application/octet-stream")
    {
        if (request.Headers.TryGetValue("Range", out var range) && range.StartsWith("bytes=", StringComparison.Ordinal) && long.TryParse(range[6..].Split('-')[0], out var from) && from < content.Length)
        {
            return new Response(206, content[(int)from..], type, new Dictionary<string, string> { ["Content-Range"] = $"bytes {from}-{content.Length - 1}/{content.Length}" });
        }

        return new Response(200, content, type);
    }

    private sealed class InProcessHandler(FakeAtlassian fake) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
        {
            var url = message.RequestUri!;
            if (!url.GetLeftPart(UriPartial.Authority).Equals(fake.Origin, StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException($"No such host is known. ({url.Host})");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in message.Headers)
            {
                headers[header.Key] = string.Join(header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ? "; " : ", ", header.Value);
            }

            byte[] body = [];
            if (message.Content is { } content)
            {
                foreach (var header in content.Headers)
                {
                    headers[header.Key] = string.Join(", ", header.Value);
                }

                body = await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }

            var raw = url.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
            var segments = raw.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
            var query = (url.Query.Length > 1 ? url.Query[1..] : "").Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).GroupBy(p => Uri.UnescapeDataString(p[0]), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Length > 1 ? Uri.UnescapeDataString(g.First()[1].Replace('+', ' ')) : "", StringComparer.Ordinal);
            var answer = fake.Answer(new Request(message.Method.Method, url, segments, query, headers, body));
            var response = new HttpResponseMessage((HttpStatusCode)answer.Status) { RequestMessage = message, Content = new ByteArrayContent(message.Method == HttpMethod.Head ? [] : answer.Body) };
            if (answer.ContentType is not null)
            {
                response.Content.Headers.TryAddWithoutValidation("Content-Type", answer.ContentType);
            }

            foreach (var (name, value) in answer.Headers ?? [])
            {
                if (!response.Headers.TryAddWithoutValidation(name, value))
                {
                    response.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }

            return response;
        }
    }
}
