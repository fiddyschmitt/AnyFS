using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AnyFS.Sources.Artifactory;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A stand-in for a JFrog Artifactory server (spec 7.13), answering as JFrog's documentation, the published source of its
/// open-source edition and JFrog's own public server say it does: repositories (local, remote with what they have cached, virtual) of folders and files; accounts that read all of a
/// repository, a folder of one, or nothing; the repositories list, the storage calls (folder info, file info, the file
/// list of the paid editions), the query language (<c>items.find</c> with the criteria the source writes, for signed-in
/// accounts only, a limit applied before what the account may not read is left out), downloads with ranges, checksums and
/// a redirect to a storage service's signed address; bearer tokens (signed ones that name their account, reference tokens
/// that do not), API keys, Basic, nobody; and JFrog's client sign-in, as the lab's 7.161 answered it: a session asked for
/// and held a while (<see cref="ClientSessionsLast"/>), the person signing in on the platform's page for it and giving the
/// code (<see cref="SignInAt"/>), the token handed over
/// once, a year's, renewed with its refresh token (each taken once) with the access token as the bearer, even one that has
/// run out, and the renewed one revoked. In-process, through <see cref="Handler"/>.
/// </summary>
internal sealed class FakeArtifactory(string origin = "https://artifactory.fake.test", string root = "/artifactory")
{
    public const string Storage = "https://cdn.fake.test";
    private const string ProOnly = "This REST API is available only in Artifactory Pro (see: jfrog.com/artifactory/features). If you are already running Artifactory Pro please make sure your server is activated with a valid license key.";

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, (string Name, DateTimeOffset Until)> _access = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _revoked = new(StringComparer.Ordinal);

    // the client sign-in's sessions (asked for; signed in as whom once the person gave the code; handed over), and the
    // refresh tokens it gave, each with the account it renews and the access token it came with
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Name, string Access)> _refresh = new(StringComparer.Ordinal);
    private int _next;
    private int _queriesNow;
    private int _queriesRefused;

    public string Origin { get; } = origin.TrimEnd('/');

    /// <summary>The path Artifactory is at on the host (<c>/artifactory</c>), or empty for the host's root.</summary>
    public string Root { get; } = root.TrimEnd('/');

    /// <summary>Artifactory's address (what a source's address starts with).</summary>
    public string Server => Origin + Root;

    public string Version { get; set; } = "7.161.26";

    /// <summary>The server's clock: what new files are dated, and what its answers' Date says.</summary>
    public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Every request answered, oldest first: "GET /artifactory/api/repositories".</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Every query posted, oldest first, with "stream " before one that asked for a streamed answer.</summary>
    public ConcurrentQueue<string> Queries { get; } = new();

    /// <summary>Whether the server has the query language at all (an edition without it has no such call).</summary>
    public bool Queryable { get; set; } = true;

    /// <summary>Whether the server has the file list call (the paid editions').</summary>
    public bool FileList { get; set; } = true;

    /// <summary>Whether a request with no sign-in is served as the anonymous user rather than refused.</summary>
    public bool Anonymous { get; set; }

    public bool BasicAllowed { get; set; } = true;

    /// <summary>How many queries the server runs at once; one too many waits <see cref="QueryPending"/>, then is refused (429). Null for no limit.</summary>
    public int? QueriesAtOnce { get; set; }

    public TimeSpan QueryPending { get; set; } = TimeSpan.FromMilliseconds(40);

    /// <summary>How long a query takes to answer.</summary>
    public TimeSpan QueryTakes { get; set; }

    /// <summary>The most queries seen running at once; a test sets it back to see the most from there on.</summary>
    public int MostQueriesAtOnce { get; set; }

    /// <summary>How many queries were refused as one too many.</summary>
    public int QueriesRefused => Volatile.Read(ref _queriesRefused);

    /// <summary>What became of each query, in the order the server was done with them: refused as one too many, or answered.</summary>
    public ConcurrentQueue<(string Query, bool Refused)> QueryAnswers { get; } = new();

    /// <summary>The most items an answer holds, the rest cut off with the server's notice (a hosted Artifactory's half a million); null for no limit.</summary>
    public int? HardLimit { get; set; }

    /// <summary>
    /// The most items an answer to a query that asked for a streamed answer holds: a client that hangs up part way gets no
    /// more of a real one, and a test of a great many files need not have the fake write what nobody reads. Null for all.
    /// </summary>
    public int? StreamedHolds { get; set; }

    /// <summary>How many answers to a query break off part way: half of the answer is sent, and then the connection is lost.</summary>
    public int QueryAnswersBreak { get; set; }

    /// <summary>Whether a file's download is sent on to a signed address at <see cref="Storage"/>, which refuses a request that carries a sign-in.</summary>
    public bool Redirects { get; set; }

    /// <summary>How many times a remote repository was asked for a file it has not cached (the real one would fetch it from the other server).</summary>
    public int RemoteFetches { get; private set; }

    /// <summary>A request the test wants answered otherwise: its answer in place of the server's, or null to answer as usual.</summary>
    public Func<Request, Response?>? Fault { get; set; }

    /// <summary>How many API requests to answer 429 with Retry-After: 1 before answering again.</summary>
    public int ThrottleNext { get; set; }

    /// <summary>Whether the platform has JFrog's client sign-in (an Artifactory before 7.64 has not: its calls are not there).</summary>
    public bool ClientSignIn { get; set; } = true;

    /// <summary>How long a token the client sign-in or a renewal gives lasts: a year, as the lab's 7.161 gave them.</summary>
    public TimeSpan ClientTokenLasts { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// How long the platform holds a client sign-in's session from when it was asked for: its access service forgets one
    /// then (its <c>jfrog-client-login</c> cache's <c>expire-after-write-seconds</c>, 300 unless an administrator changed
    /// it), after which the page cannot approve it ("Session is not exist", as the lab answered one gone stale) and no token
    /// is handed over for it. On this computer's clock, not <see cref="Now"/>: the person at the window takes real time.
    /// </summary>
    public TimeSpan ClientSessionsLast { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The client sign-in's sessions asked for, oldest first.</summary>
    public ConcurrentQueue<string> SessionsAsked { get; } = new();

    /// <summary>The sign-in pages the person was at (<see cref="SignInAt"/>), oldest first.</summary>
    public ConcurrentQueue<Uri> SignInPages { get; } = new();

    // --- the model -------------------------------------------------------------------------------------------------------

    public sealed class Account(string name)
    {
        public string Name { get; } = name;

        public string? Password { get; set; }

        public string? ApiKey { get; set; }

        public bool Admin { get; set; }

        public List<string> Tokens { get; } = [];

        /// <summary>What it may read: a repository's key to the folder it reads from there down (empty for all of it).</summary>
        public Dictionary<string, string> Reads { get; } = new(StringComparer.Ordinal);
    }

    public sealed class Repository(string key, string type, string packageType)
    {
        public string Key { get; } = key;

        public string Type { get; } = type;

        public string PackageType { get; } = packageType;

        public string Description { get; set; } = "";

        /// <summary>A virtual repository's members, by key.</summary>
        public List<string> Members { get; } = [];

        /// <summary>Its folders and files by path; the root is not among them.</summary>
        public SortedList<string, Item> Items { get; } = new(StringComparer.Ordinal);

        /// <summary>The items below a folder, at any depth (all of them for the root), found by where their paths sort.</summary>
        public IEnumerable<KeyValuePair<string, Item>> Below(string folder)
        {
            if (folder.Length == 0)
            {
                return Items;
            }

            int First(string key)
            {
                var (low, high) = (0, Items.Count);
                while (low < high)
                {
                    var middle = (low + high) / 2;
                    if (string.CompareOrdinal(Items.Keys[middle], key) < 0)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle;
                    }
                }

                return low;
            }

            // "a/" up to, not with, "a0": the character after the slash
            var (from, to) = (First(folder + "/"), First(folder + "0"));
            return Enumerable.Range(from, to - from).Select(i => new KeyValuePair<string, Item>(Items.Keys[i], Items.Values[i]));
        }

        public DateTimeOffset Created { get; set; }

        /// <summary>The key its files are stored and asked for under: a remote repository's cache.</summary>
        public string StorageKey => Type == "REMOTE" ? Key + "-cache" : Key;
    }

    public sealed class Item(bool folder, byte[] content, DateTimeOffset when)
    {
        public bool Folder { get; } = folder;

        public byte[] Content { get; set; } = content;

        public DateTimeOffset Created { get; set; } = when;

        public DateTimeOffset Modified { get; set; } = when;

        public DateTimeOffset Updated { get; set; } = when;

        public string Sha1 => Convert.ToHexStringLower(SHA1.HashData(Content));
    }

    public List<Account> Accounts { get; } = [];

    public List<Repository> Repositories { get; } = [];

    public Account AddAccount(string name, string? token = null, string? password = null, bool admin = false)
    {
        var account = new Account(name) { Password = password, Admin = admin };
        if (token is not null)
        {
            account.Tokens.Add(token);
        }

        Accounts.Add(account);
        return account;
    }

    public Repository AddRepository(string key, string type = "LOCAL", string packageType = "Generic", params string[] members)
    {
        var repository = new Repository(key, type, packageType) { Created = Now };
        repository.Members.AddRange(members);
        lock (_gate)
        {
            Repositories.Add(repository);
        }

        return repository;
    }

    public Repository RepositoryAt(string key) => Repositories.Single(r => r.Key == key);

    /// <summary>Puts a file, dated now (the server's clock) or <paramref name="when"/>: its folders are made where there are none; a file there already is replaced, its creation kept.</summary>
    public void Put(string repository, string path, byte[] content, DateTimeOffset? when = null)
    {
        var at = when ?? Now;
        var repo = RepositoryAt(repository);
        lock (_gate)
        {
            var parts = path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                repo.Items.TryAdd(string.Join('/', parts[..i]), new Item(true, [], at));
            }

            if (repo.Items.TryGetValue(path, out var there) && !there.Folder)
            {
                there.Content = content;
                there.Modified = at;
                there.Updated = at;
            }
            else
            {
                repo.Items[path] = new Item(false, content, at);
            }
        }
    }

    public void AddFolder(string repository, string path, DateTimeOffset? when = null)
    {
        var repo = RepositoryAt(repository);
        lock (_gate)
        {
            var parts = path.Split('/');
            for (var i = 1; i <= parts.Length; i++)
            {
                repo.Items.TryAdd(string.Join('/', parts[..i]), new Item(true, [], when ?? Now));
            }
        }
    }

    /// <summary>Deletes a file or a folder with all below it; nothing else is touched, as on the real server.</summary>
    public void Delete(string repository, string path)
    {
        var repo = RepositoryAt(repository);
        lock (_gate)
        {
            foreach (var gone in repo.Items.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
            {
                repo.Items.Remove(gone);
            }
        }
    }

    /// <summary>A signed token for an account, as the access service makes them: its subject names the account, it says when it was made, and it may run out.</summary>
    public string TokenFor(string name, DateTimeOffset? until = null)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = new JsonObject { ["sub"] = $"jfac@01fake/users/{name}", ["scp"] = "applied-permissions/user", ["aud"] = "*@*", ["iss"] = "jfac@01fake", ["iat"] = Now.ToUnixTimeSeconds(), ["jti"] = Interlocked.Increment(ref _next) };
        if (until is { } exp)
        {
            payload["exp"] = exp.ToUnixTimeSeconds();
        }

        var token = $"{Part("{\"alg\":\"RS256\",\"typ\":\"JWT\"}")}.{Part(payload.ToJsonString())}.c2lnbmF0dXJl";
        _access[token] = (name, until ?? DateTimeOffset.MaxValue);
        return token;
    }

    private sealed class ClientSession
    {
        public long Asked { get; } = Stopwatch.GetTimestamp();

        public string? Name { get; set; }

        public bool Taken { get; set; }
    }

    /// <summary>Whether the platform still holds a session: until <see cref="ClientSessionsLast"/> after it was asked for.</summary>
    private bool Holds(ClientSession session) => Stopwatch.GetElapsedTime(session.Asked) < ClientSessionsLast;

    /// <summary>
    /// What the person does on the platform's sign-in page for a client's session (the page's address names it): signs in
    /// as <paramref name="name"/> and gives the code the page asks for, after which the platform hands the session's token
    /// over. As on the real page, one opened without the code asked for (<c>jfClientCode</c>) says the client is out of date
    /// and lets nothing through, and a session the platform was not asked for, or has forgotten
    /// (<see cref="ClientSessionsLast"/>), is not there.
    /// </summary>
    public void SignInAt(Uri page, string name)
    {
        SignInPages.Enqueue(page);
        var query = page.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "", StringComparer.Ordinal);
        if (!page.AbsolutePath.EndsWith("/ui/login", StringComparison.Ordinal) || !query.TryGetValue("jfClientSession", out var session))
        {
            throw new InvalidOperationException($"not the platform's sign-in page for a client: {page}");
        }

        if (!query.ContainsKey("jfClientCode"))
        {
            throw new InvalidOperationException("the page says the client is out of date, and asks for no code");
        }

        if (!_sessions.TryGetValue(session, out var asked) || !Holds(asked))
        {
            throw new InvalidOperationException("Session is not exist");
        }

        asked.Name = name;
    }

    /// <summary>A person at the browser who signs in as <paramref name="name"/> wherever a window may be shown; each sign-in asked of them is noted in <paramref name="asked"/> (whether a window could be shown).</summary>
    public Func<bool, Uri, CancellationToken, Task> Person(string name, List<bool>? asked = null) => (interactive, page, _) =>
    {
        asked?.Add(interactive);
        if (interactive)
        {
            SignInAt(page, name);
        }

        return Task.CompletedTask;
    };

    /// <summary>
    /// A person who signs in as <paramref name="name"/> and gives the code <paramref name="after"/> their window opened,
    /// then leaves the window open until AnyFS closes it, even when the page could not approve the session (its time up).
    /// What they came to is noted in <paramref name="said"/>: "code given", "the page: …" for the page's refusal, or
    /// "closed before the code was given" for a window AnyFS closed first.
    /// </summary>
    public Func<bool, Uri, CancellationToken, Task> PersonTaking(TimeSpan after, string name, ConcurrentQueue<string> said) => async (_, page, ct) =>
    {
        try
        {
            await Task.Delay(after, ct);
        }
        catch (OperationCanceledException)
        {
            said.Enqueue("closed before the code was given");
            throw;
        }

        try
        {
            SignInAt(page, name);
            said.Enqueue("code given");
        }
        catch (InvalidOperationException ex)
        {
            said.Enqueue("the page: " + ex.Message);
        }

        await Task.Delay(Timeout.Infinite, ct);
    };

    /// <summary>Revokes every token the client sign-in or a renewal gave, and their refresh tokens: only a new sign-in gets in.</summary>
    public void EndSessions()
    {
        _refresh.Clear();
        foreach (var token in _access.Where(t => t.Value.Until != DateTimeOffset.MaxValue).Select(t => t.Key).ToList())
        {
            Revoke(token);
        }
    }

    /// <summary>Revokes one access token: refused from now on, by the REST API and as the bearer of its renewal.</summary>
    public void Revoke(string token)
    {
        _access.TryRemove(token, out _);
        _revoked[token] = 0;
    }

    /// <summary>The access tokens the client sign-in and its renewals have given, oldest first.</summary>
    public List<string> ClientTokens { get; } = [];

    public int Count(string what) => Requests.Count(r => r.Contains(what, StringComparison.Ordinal));

    /// <summary>A handler that answers in-process, without a socket.</summary>
    public HttpMessageHandler Handler() => new InProcessHandler(this);

    /// <summary>A stand-in for the source: its requests come here, and <paramref name="browser"/> stands in for the person at the browser (<see cref="Person"/>).</summary>
    /// <summary>The stand-in a source of this server is made with; AnyFS takes a client sign-in's session to be held as long as this platform holds one (<see cref="ClientSessionsLast"/>).</summary>
    public ArtifactoryStandIn StandIn(Func<bool, Uri, CancellationToken, Task>? browser = null, bool useKept = true) => new(Handler, browser, useKept, ClientSessionsLast);

    // --- answering -------------------------------------------------------------------------------------------------------

    internal sealed record Request(string Method, Uri Url, string[] Segments, Dictionary<string, string> Query, Dictionary<string, string> Headers, byte[] Body)
    {
        public string? Q(string name) => Query.TryGetValue(name, out var v) ? v : null;
    }

    internal sealed record Response(int Status, byte[] Body, string? ContentType = "application/json", Dictionary<string, string>? Headers = null)
    {
        /// <summary>Half of the body is sent, and then the connection is lost.</summary>
        public bool BreaksOff { get; init; }

        public static Response Json(JsonNode node, int status = 200, Dictionary<string, string>? headers = null) => new(status, Encoding.UTF8.GetBytes(node.ToJsonString()), "application/json", headers);

        public static Response Text(string text, int status = 200, string type = "text/plain") => new(status, Encoding.UTF8.GetBytes(text), type);

        /// <summary>An error as Artifactory writes one.</summary>
        public static Response Error(int status, string message, Dictionary<string, string>? headers = null)
            => Json(new JsonObject { ["errors"] = new JsonArray((JsonNode)new JsonObject { ["status"] = status, ["message"] = message }) }, status, headers);
    }

    private static readonly Dictionary<string, string> Realm = new() { ["WWW-Authenticate"] = "Basic realm=\"Artifactory Realm\"" };

    private async Task<Response> AnswerAsync(Request request)
    {
        var full = "/" + string.Join('/', request.Segments);
        Requests.Enqueue($"{request.Method} {full}{request.Url.Query}{(request.Headers.TryGetValue("Range", out var asked) ? " " + asked : "")}");
        if (request.Url.GetLeftPart(UriPartial.Authority).Equals(Storage, StringComparison.OrdinalIgnoreCase))
        {
            return Stored(request);
        }

        if (full == "/ui/api/v1/ui/auth/current")
        {
            return Current();
        }

        if (full.StartsWith("/access/", StringComparison.Ordinal))
        {
            return Access(request, full);
        }

        if (Root.Length > 0 && !full.StartsWith(Root + "/", StringComparison.Ordinal) && full != Root)
        {
            return Response.Text("404 page not found", 404);
        }

        var path = full[Root.Length..].TrimStart('/');
        if (path == "api/system/ping")
        {
            return Response.Text("OK");
        }

        var (user, refusal) = Authenticate(request);
        if (refusal is not null)
        {
            return refusal;
        }

        if (ThrottleNext > 0 && path.StartsWith("api/", StringComparison.Ordinal))
        {
            ThrottleNext--;
            return Response.Error(429, "too many requests", new Dictionary<string, string> { ["Retry-After"] = "1" });
        }

        if (Fault?.Invoke(request) is { } faulted)
        {
            return faulted;
        }

        if (!path.StartsWith("api/", StringComparison.Ordinal))
        {
            return Download(request, path.Split('/'), user);
        }

        if (user is null)
        {
            return Response.Error(401, "Authentication is required", Realm);
        }

        var s = path.Split('/');
        switch (s)
        {
            case ["api", "system", "version"]:
                return new Response(200, Encoding.UTF8.GetBytes(new JsonObject { ["version"] = Version, ["revision"] = "77161260", ["addons"] = new JsonArray(), ["license"] = "fake" }.ToJsonString()),
                    "application/vnd.org.jfrog.artifactory.system.Version+json");
            case ["api", "repositories"]:
                lock (_gate)
                {
                    var listed = Repositories.Where(r => Sees(user, r)).OrderBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.Key, StringComparer.Ordinal).Select(r => (JsonNode)new JsonObject
                    {
                        ["key"] = r.Key,
                        ["description"] = r.Description,
                        ["type"] = r.Type,
                        ["url"] = r.Type == "REMOTE" ? "https://upstream.fake.test/" : $"{Server}/{r.Key}",
                        ["packageType"] = r.PackageType,
                    }).ToArray();
                    return new Response(200, Encoding.UTF8.GetBytes(new JsonArray(listed).ToJsonString()), "application/vnd.org.jfrog.artifactory.repositories.RepositoryDetailsList+json");
                }

            case ["api", "storage", var key, .. var rest]:
                lock (_gate)
                {
                    return StorageInfo(request, user, key, string.Join('/', rest));
                }

            case ["api", "search", "aql"] when request.Method == "POST":
                return await QueryAsync(request, user).ConfigureAwait(false);
            default:
                return Response.Error(404, "Not Found");
        }
    }

    /// <summary>Who a request is from: its bearer token's account, its API key's, its Basic credentials'; with none, the anonymous user where the server has one. A refusal for a bad one.</summary>
    private (Account? User, Response? Refusal) Authenticate(Request request)
    {
        if (request.Headers.TryGetValue("Authorization", out var authorization))
        {
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authorization[7..].Trim();
                if (_access.TryGetValue(token, out var signed))
                {
                    return signed.Until > Now ? (Accounts.First(a => a.Name == signed.Name), null) : (null, Response.Error(401, "Token failed verification: expired", Realm));
                }

                if (_revoked.ContainsKey(token))
                {
                    return (null, Response.Error(401, "Token failed verification: revoked", Realm));
                }

                var holder = Accounts.FirstOrDefault(a => a.Tokens.Contains(token));
                return holder is null ? (null, Response.Error(401, "Token failed verification: parse", Realm)) : (holder, null);
            }

            if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim())).Split(':', 2);
                var secret = pair.Length > 1 ? pair[1] : "";
                var account = Accounts.FirstOrDefault(a => a.Name.Equals(pair[0], StringComparison.OrdinalIgnoreCase)
                    && (BasicAllowed && a.Password == secret || a.ApiKey == secret || a.Tokens.Contains(secret)));
                return account is null ? (null, Response.Error(401, "Bad credentials", Realm)) : (account, null);
            }
        }

        if (request.Headers.TryGetValue("X-JFrog-Art-Api", out var key))
        {
            var account = Accounts.FirstOrDefault(a => a.ApiKey == key);
            return account is null ? (null, Response.Error(401, "Bad authentication Key", Realm)) : (account, null);
        }

        return (Anonymous ? Accounts.FirstOrDefault(a => a.Name == "anonymous") ?? new Account("anonymous") : null, null);
    }

    /// <summary>
    /// The platform's who-am-I, as its pages ask it with their own session: nobody here, since nothing of AnyFS's signs in
    /// as the pages do (a sign-in sent the way the REST API takes one says nothing to it).
    /// </summary>
    private Response Current() => Response.Json(new JsonObject { ["name"] = "anonymous", ["admin"] = false, ["anonAccessEnabled"] = Anonymous, ["realm"] = "internal" });

    /// <summary>An error as the access service writes one.</summary>
    private static Response AccessError(int status, string code, string message)
        => Response.Json(new JsonObject { ["errors"] = new JsonArray((JsonNode)new JsonObject { ["code"] = code, ["message"] = message }) }, status);

    /// <summary>
    /// The access service's calls the source makes: JFrog's client sign-in (a session asked for; its token, once the person
    /// has signed in at the page and given the code, handed over once and 400 before and after) and a token's renewal (the
    /// refresh token, taken once, with the access token it came with as the bearer), answered as the lab's 7.161 answered.
    /// </summary>
    private Response Access(Request request, string full)
    {
        const string login = "/access/api/v2/authentication/jfrog_client_login/";
        if (full.StartsWith(login, StringComparison.Ordinal))
        {
            if (!ClientSignIn)
            {
                return Response.Text("404 page not found", 404);
            }

            if (full == login + "request" && request.Method == "POST")
            {
                var session = JsonNode.Parse(request.Body)?["session"]?.GetValue<string>();
                if (string.IsNullOrEmpty(session))
                {
                    return AccessError(400, "BAD_REQUEST", "session is required");
                }

                _sessions[session] = new ClientSession();
                SessionsAsked.Enqueue(session);
                return new Response(200, [], null);
            }

            if (full.StartsWith(login + "token/", StringComparison.Ordinal) && request.Method == "GET")
            {
                var session = full[(login.Length + "token/".Length)..];
                if (_sessions.TryGetValue(session, out var asked) && Holds(asked) && asked.Name is { } name && !asked.Taken)
                {
                    asked.Taken = true;
                    return ClientToken(name);
                }

                return AccessError(400, "BAD_REQUEST", "Failed to generate token");
            }

            return AccessError(404, "NOT_FOUND", "Not Found");
        }

        if (full == "/access/api/v1/tokens" && request.Method == "POST")
        {
            // the bearer may have run out (the lab's 7.161 renews such a one all the same); one revoked is refused
            var bearer = request.Headers.TryGetValue("Authorization", out var authorization) && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : null;
            if (bearer is null || !_access.ContainsKey(bearer))
            {
                return AccessError(401, "UNAUTHORIZED", bearer is not null && _revoked.ContainsKey(bearer) ? "Invalid token, revoked" : "Authentication required");
            }

            var body = JsonNode.Parse(request.Body);
            if (body?["grant_type"]?.GetValue<string>() != "refresh_token" || body["refresh_token"]?.GetValue<string>() is not { } refresh)
            {
                return AccessError(400, "BAD_REQUEST", "only a renewal is answered here");
            }

            if (!_refresh.TryRemove(refresh, out var renews))
            {
                return AccessError(404, "NOT_FOUND", "Token matching the provided refresh token was not found.");
            }

            // the access token it came with is revoked (on the lab some minutes after; here at once)
            Revoke(renews.Access);
            return ClientToken(renews.Name);
        }

        return AccessError(404, "NOT_FOUND", "Not Found");
    }

    /// <summary>A token as the client sign-in and a renewal hand one over: an access token for the year, and the refresh token that renews it once.</summary>
    private Response ClientToken(string name)
    {
        var access = TokenFor(name, Now + ClientTokenLasts);
        var refresh = Guid.NewGuid().ToString("D");
        _refresh[refresh] = (name, access);
        lock (ClientTokens)
        {
            ClientTokens.Add(access);
        }

        return Response.Json(new JsonObject
        {
            ["token_id"] = Guid.NewGuid().ToString("D"),
            ["access_token"] = access,
            ["refresh_token"] = refresh,
            ["expires_in"] = (long)ClientTokenLasts.TotalSeconds,
            ["scope"] = "applied-permissions/user",
            ["token_type"] = "Bearer",
            ["username"] = name,
        });
    }

    // --- what an account may see -------------------------------------------------------------------------------------------

    private bool Sees(Account user, Repository repository)
        => user.Admin || user.Reads.ContainsKey(repository.Key) || repository.Type == "VIRTUAL" && repository.Members.Any(m => user.Reads.ContainsKey(m));

    /// <summary>Whether an account may read a path of a repository: all of it, or the folder it reads from there down, and the folders that lead to it.</summary>
    private static bool Reads(Account user, Repository repository, string path)
    {
        if (user.Admin)
        {
            return true;
        }

        if (!user.Reads.TryGetValue(repository.Key, out var from))
        {
            return false;
        }

        return from.Length == 0 || path == from || path.StartsWith(from + "/", StringComparison.Ordinal) || path.Length == 0 || from.StartsWith(path + "/", StringComparison.Ordinal);
    }

    /// <summary>The repository a key names for storage and downloads: its own key, or a remote repository's cache.</summary>
    private Repository? Named(string key)
        => Repositories.FirstOrDefault(r => r.Key == key) ?? Repositories.FirstOrDefault(r => r.Type == "REMOTE" && r.Key + "-cache" == key);

    /// <summary>A path in a repository, through a virtual one's members: the item and the repository that holds it.</summary>
    private (Repository Holder, Item? Item)? Find(Repository repository, string path, Account user)
    {
        if (repository.Type != "VIRTUAL")
        {
            return path.Length == 0 ? (repository, null) : repository.Items.TryGetValue(path, out var item) ? (repository, item) : null;
        }

        foreach (var member in repository.Members.Select(m => Repositories.FirstOrDefault(r => r.Key == m)).OfType<Repository>())
        {
            if (Reads(user, member, path) && (path.Length == 0 || member.Items.ContainsKey(path)))
            {
                return (member, path.Length == 0 ? null : member.Items[path]);
            }
        }

        return null;
    }

    /// <summary>A folder's children: in a virtual repository, those of every member, each name once.</summary>
    private List<(string Name, Item Item)> Children(Repository repository, string path, Account user)
    {
        var prefix = path.Length == 0 ? "" : path + "/";
        var members = repository.Type == "VIRTUAL" ? repository.Members.Select(m => Repositories.FirstOrDefault(r => r.Key == m)).OfType<Repository>().ToList() : [repository];
        var children = new SortedDictionary<string, Item>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            foreach (var (at, item) in member.Items)
            {
                if (at.StartsWith(prefix, StringComparison.Ordinal) && at.IndexOf('/', prefix.Length) < 0 && Reads(user, member, at))
                {
                    children.TryAdd(at[prefix.Length..], item);
                }
            }
        }

        return children.Select(c => (c.Key, c.Value)).ToList();
    }

    private static string Iso(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // --- the storage calls -----------------------------------------------------------------------------------------------

    private Response StorageInfo(Request request, Account user, string key, string path)
    {
        if (Named(key) is not { } repository || !Sees(user, repository))
        {
            return Response.Error(404, "Unable to find item");
        }

        if (!Reads(user, repository, path) && repository.Type != "VIRTUAL")
        {
            return Response.Error(403, $"Request for '{key}:{path}' is forbidden for user: '{user.Name}'.");
        }

        if (Find(repository, path, user) is not { } found)
        {
            return Response.Error(404, "Unable to find item");
        }

        var uri = $"{Server}/api/storage/{key}{(path.Length == 0 ? "" : "/" + path)}";
        if (found.Item is { Folder: false } file)
        {
            return Response.Json(new JsonObject
            {
                ["repo"] = key,
                ["path"] = "/" + path,
                ["created"] = Iso(file.Created),
                ["createdBy"] = "tester",
                ["lastModified"] = Iso(file.Modified),
                ["modifiedBy"] = "tester",
                ["lastUpdated"] = Iso(file.Updated),
                ["downloadUri"] = $"{Server}/{key}/{path}",
                ["mimeType"] = "application/octet-stream",
                ["size"] = file.Content.LongLength.ToString(CultureInfo.InvariantCulture),
                ["checksums"] = new JsonObject { ["sha1"] = file.Sha1, ["md5"] = Convert.ToHexStringLower(MD5.HashData(file.Content)), ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(file.Content)) },
                ["originalChecksums"] = new JsonObject { ["sha1"] = file.Sha1 },
                ["uri"] = uri,
            });
        }

        var children = Children(repository, path, user);
        if (request.Query.ContainsKey("list"))
        {
            if (user.Name == "anonymous")
            {
                return Response.Text("This resource is available to authenticated users only.", 403, "application/json");
            }

            if (!FileList)
            {
                return Response.Error(400, ProOnly);
            }

            var folders = request.Q("listFolders") == "1";
            var files = children.Where(c => folders || !c.Item.Folder).Select(c => (JsonNode)(c.Item.Folder
                ? new JsonObject { ["uri"] = "/" + c.Name, ["size"] = "-1", ["lastModified"] = Iso(c.Item.Modified), ["folder"] = true }
                : new JsonObject { ["uri"] = "/" + c.Name, ["size"] = c.Item.Content.LongLength.ToString(CultureInfo.InvariantCulture), ["lastModified"] = Iso(c.Item.Modified), ["folder"] = false, ["sha1"] = c.Item.Sha1 })).ToArray();
            return Response.Json(new JsonObject { ["uri"] = uri, ["created"] = Iso(Now), ["files"] = new JsonArray(files) });
        }

        var when = found.Item?.Created ?? repository.Created;
        return Response.Json(new JsonObject
        {
            ["repo"] = key,
            ["path"] = "/" + path,
            ["created"] = Iso(when),
            ["lastModified"] = Iso(found.Item?.Modified ?? when),
            ["lastUpdated"] = Iso(found.Item?.Updated ?? when),
            ["children"] = new JsonArray(children.Select(c => (JsonNode)new JsonObject { ["uri"] = "/" + c.Name, ["folder"] = c.Item.Folder }).ToArray()),
            ["uri"] = uri,
        });
    }

    // --- downloads -------------------------------------------------------------------------------------------------------

    private Response Download(Request request, string[] segments, Account? user)
    {
        if (request.Method is not ("GET" or "HEAD") || segments.Length < 2)
        {
            return Response.Error(404, "Not Found");
        }

        if (user is null)
        {
            return Response.Error(401, "Authentication is required", Realm);
        }

        lock (_gate)
        {
            var key = segments[0];
            var path = string.Join('/', segments[1..]);
            if (Named(key) is not { } repository || !Sees(user, repository) || repository.Type != "VIRTUAL" && !Reads(user, repository, path))
            {
                return Response.Error(404, "Resource not found");
            }

            if (Find(repository, path, user) is not { Item: { Folder: false } file })
            {
                if (repository.Type == "REMOTE" && key == repository.Key)
                {
                    // the real one asks the other server for it now
                    RemoteFetches++;
                }

                return Response.Error(404, "File not found.");
            }

            if (Redirects && request.Method == "GET")
            {
                return new Response(302, [], null, new Dictionary<string, string> { ["Location"] = $"{Storage}/filestore/{file.Sha1[..2]}/{file.Sha1}?Signature={Signature(file.Sha1)}&X-Artifactory-username={user.Name}" });
            }

            return Bytes(request, file);
        }
    }

    private static string Signature(string sha1) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("signed:" + sha1)))[..16];

    /// <summary>The storage service a download is sent on to: the file by its checksum and signature, to a request that carries no sign-in of Artifactory's.</summary>
    private Response Stored(Request request)
    {
        if (request.Headers.ContainsKey("Authorization") || request.Headers.ContainsKey("X-JFrog-Art-Api") || request.Headers.ContainsKey("Cookie"))
        {
            return Response.Text("<Error><Code>InvalidArgument</Code><Message>Only one auth mechanism allowed</Message></Error>", 400, "application/xml");
        }

        lock (_gate)
        {
            var sha1 = request.Segments[^1];
            var file = Repositories.SelectMany(r => r.Items.Values).FirstOrDefault(i => !i.Folder && i.Sha1 == sha1);
            return file is null || request.Q("Signature") != Signature(sha1) ? Response.Text("<Error><Code>AccessDenied</Code></Error>", 403, "application/xml") : Bytes(request, file);
        }
    }

    /// <summary>A file's bytes, from an offset where a range asks (206), with the headers Artifactory sends.</summary>
    private static Response Bytes(Request request, Item file)
    {
        var headers = new Dictionary<string, string>
        {
            ["ETag"] = file.Sha1,
            ["X-Checksum-Sha1"] = file.Sha1,
            ["Accept-Ranges"] = "bytes",
            ["Last-Modified"] = file.Modified.ToString("R", CultureInfo.InvariantCulture),
        };
        var content = file.Content;
        if (request.Headers.TryGetValue("Range", out var range) && range.StartsWith("bytes=", StringComparison.Ordinal) && long.TryParse(range[6..].Split('-')[0], out var from))
        {
            if (from >= content.Length)
            {
                return new Response(416, [], null, headers);
            }

            headers["Content-Range"] = $"bytes {from}-{content.Length - 1}/{content.Length}";
            return new Response(206, content[(int)from..], "application/octet-stream", headers);
        }

        return new Response(200, content, "application/octet-stream", headers);
    }

    // --- the query language ----------------------------------------------------------------------------------------------

    private sealed record Row(string Repo, string Path, string Name, Item? Item, Repository Holder, string Full)
    {
        public bool Folder => Item?.Folder ?? true;
    }

    private static readonly Regex QueryShape = new(@"^items\.find\((?<criteria>.*)\)\.include\((?<include>[^)]*)\)(?<rest>.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private async Task<Response> QueryAsync(Request request, Account user)
    {
        if (!Queryable)
        {
            return Response.Error(400, ProOnly);
        }

        if (user.Name == "anonymous")
        {
            return Response.Text("Only non-anonymous users are allowed to access AQL queries\n", 403, "application/json");
        }

        var text = Encoding.UTF8.GetString(request.Body);
        Queries.Enqueue((request.Headers.GetValueOrDefault("stream") == "true" ? "stream " : "") + text);
        var running = Interlocked.Increment(ref _queriesNow);
        var refused = false;
        try
        {
            lock (_gate)
            {
                MostQueriesAtOnce = Math.Max(MostQueriesAtOnce, running);
            }

            if (QueriesAtOnce is { } most && running > most)
            {
                // one too many waits a while for a place, then is refused; no Retry-After, as the real one sends none
                await Task.Delay(QueryPending).ConfigureAwait(false);
                if (Volatile.Read(ref _queriesNow) > most)
                {
                    refused = true;
                    Interlocked.Increment(ref _queriesRefused);
                    return Response.Error(429, "too many requests");
                }
            }

            if (QueryTakes > TimeSpan.Zero)
            {
                await Task.Delay(QueryTakes).ConfigureAwait(false);
            }

            lock (_gate)
            {
                var answer = Query(text, user, request.Headers.GetValueOrDefault("stream") == "true");
                if (answer.Status != 200 || QueryAnswersBreak <= 0)
                {
                    return answer;
                }

                QueryAnswersBreak--;
                return answer with { BreaksOff = true };
            }
        }
        finally
        {
            Interlocked.Decrement(ref _queriesNow);
            QueryAnswers.Enqueue((text, refused));
        }
    }

    private Response Query(string text, Account user, bool streamed)
    {
        var shape = QueryShape.Match(text.Trim());
        if (!shape.Success)
        {
            return Response.Error(400, "Failed to parse query: " + text);
        }

        JsonObject criteria;
        try
        {
            criteria = JsonNode.Parse(shape.Groups["criteria"].Value)!.AsObject();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return Response.Error(400, "Failed to parse query: " + ex.Message);
        }

        var include = shape.Groups["include"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim('"')).ToList();
        if (!user.Admin && !(include.Contains("repo") && include.Contains("path") && include.Contains("name")))
        {
            return Response.Error(400, "For permissions reasons AQL items domain demands the following fields: repo, path and name.");
        }

        var rest = shape.Groups["rest"].Value;
        int? Number(string what) => Regex.Match(rest, @"\." + what + @"\((\d+)\)") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        var offset = Number("offset") ?? 0;
        var limit = Number("limit");
        var sort = Regex.Match(rest, @"\.sort\(\{""\$(asc|desc)"":\[(?<fields>[^\]]*)\]\}\)");

        // every item of every repository under the key its files are stored under, the root of each among them
        // (those of the one repository a query names, where it names one: a scan of every other finds nothing)
        var named = criteria.TryGetPropertyValue("repo", out var repo) && repo is JsonValue ? repo.GetValue<string>() : null;
        // and of the one folder it names, where it names one: its own items (a path), or those and all below (the pair the source writes)
        var folder = criteria.TryGetPropertyValue("path", out var own) && own is JsonValue ? own.GetValue<string>()
            : criteria.TryGetPropertyValue("$or", out var either) && either is JsonArray pair && pair.Count == 2 && pair[0] is JsonObject first && first.TryGetPropertyValue("path", out var top) && top is JsonValue ? top.GetValue<string>()
            : ".";
        var rows = new List<Row>();
        foreach (var repository in Repositories.Where(r => r.Type != "VIRTUAL" && (named is null || r.StorageKey == named)))
        {
            if (folder == ".")
            {
                rows.Add(new Row(repository.StorageKey, ".", ".", null, repository, ""));
            }

            foreach (var (path, item) in repository.Below(folder == "." ? "" : folder))
            {
                var slash = path.LastIndexOf('/');
                rows.Add(new Row(repository.StorageKey, slash < 0 ? "." : path[..slash], path[(slash + 1)..], item, repository, path));
            }
        }

        List<Row> found;
        try
        {
            var files = !criteria.ContainsKey("type");
            found = rows.Where(r => (!files || !r.Folder) && Matches(r, criteria)).ToList();
        }
        catch (NotSupportedException ex)
        {
            return Response.Error(400, "Failed to parse query: " + ex.Message);
        }

        if (sort.Success)
        {
            var name = sort.Groups["fields"].Value.Contains("\"path\"", StringComparison.Ordinal)
                ? found.OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal)
                : found.OrderBy(r => r.Name, StringComparer.Ordinal);
            found = (sort.Groups[1].Value == "desc" ? name.Reverse() : name).ToList();
        }

        // the limit first, then what the account may not read is left out: a page can come back short with more to come
        var page = found.Skip(offset).Take(limit ?? int.MaxValue).Where(r => Sees(user, r.Holder) && Reads(user, r.Holder, r.Full)).ToList();
        if (streamed && StreamedHolds is { } holds && page.Count > holds)
        {
            page = page.Take(holds).ToList();
        }

        var trimmed = HardLimit is { } hard && page.Count > hard;
        if (trimmed)
        {
            page = page.Take(HardLimit!.Value).ToList();
        }

        var results = new JsonArray(page.Select(r =>
        {
            var o = new JsonObject();
            foreach (var field in include)
            {
                o[field] = field switch
                {
                    "repo" => r.Repo,
                    "path" => r.Path,
                    "name" => r.Name,
                    "type" => r.Folder ? "folder" : "file",
                    "size" => r.Folder ? 0 : r.Item!.Content.LongLength,
                    "created" => Iso(r.Item?.Created ?? r.Holder.Created),
                    "modified" => Iso(r.Item?.Modified ?? r.Holder.Created),
                    "updated" => Iso(r.Item?.Updated ?? r.Holder.Created),
                    "actual_sha1" => r.Folder ? null : r.Item!.Sha1,
                    _ => null,
                };
            }

            return (JsonNode)o;
        }).ToArray());
        var range = new JsonObject { ["start_pos"] = offset, ["end_pos"] = offset + page.Count, ["total"] = page.Count };
        if (limit is { } l)
        {
            range["limit"] = l;
        }

        if (trimmed)
        {
            range["notification"] = "AQL query reached the search hard limit, results are trimmed.";
        }

        return Response.Json(new JsonObject { ["results"] = results, ["range"] = range });
    }

    private static bool Matches(Row row, JsonObject criteria)
    {
        foreach (var (key, value) in criteria)
        {
            var ok = key switch
            {
                "$or" => value!.AsArray().Any(c => Matches(row, c!.AsObject())),
                "$and" => value!.AsArray().All(c => Matches(row, c!.AsObject())),
                "repo" => Compares(row.Repo, value!),
                "path" => Compares(row.Path, value!),
                "name" => Compares(row.Name, value!),
                "type" => value!.GetValue<string>() switch { "any" => true, "folder" => row.Folder, _ => !row.Folder },
                "created" => After(row.Item?.Created ?? row.Holder.Created, value!),
                "modified" => After(row.Item?.Modified ?? row.Holder.Created, value!),
                "updated" => After(row.Item?.Updated ?? row.Holder.Created, value!),
                _ => throw new NotSupportedException("unknown field " + key),
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Compares(string actual, JsonNode value)
    {
        if (value is JsonValue)
        {
            return actual == value.GetValue<string>();
        }

        var (op, operand) = value.AsObject().Single();
        var text = operand!.GetValue<string>();
        return op switch
        {
            "$eq" => actual == text,
            "$ne" => actual != text,
            "$match" => Regex.IsMatch(actual, "^" + Regex.Escape(text).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant),
            _ => throw new NotSupportedException("unknown operator " + op),
        };
    }

    private static bool After(DateTimeOffset actual, JsonNode value)
    {
        var (op, operand) = value.AsObject().Single();
        var time = DateTimeOffset.Parse(operand!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        return op switch
        {
            "$gt" => actual > time,
            "$gte" => actual >= time,
            "$lt" => actual < time,
            _ => throw new NotSupportedException("unknown operator " + op),
        };
    }

    // --- in-process ------------------------------------------------------------------------------------------------------

    private sealed class InProcessHandler(FakeArtifactory fake) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
        {
            var url = message.RequestUri!;
            var host = url.GetLeftPart(UriPartial.Authority);
            if (!host.Equals(fake.Origin, StringComparison.OrdinalIgnoreCase) && !host.Equals(Storage, StringComparison.OrdinalIgnoreCase))
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
            var answer = await fake.AnswerAsync(new Request(message.Method.Method, url, segments, query, headers, body)).ConfigureAwait(false);
            var response = new HttpResponseMessage((HttpStatusCode)answer.Status)
            {
                RequestMessage = message,
                Content = answer.BreaksOff ? new StreamContent(new BrokenOff(answer.Body)) : new ByteArrayContent(message.Method == HttpMethod.Head ? [] : answer.Body),
            };
            if (answer.ContentType is not null)
            {
                response.Content.Headers.TryAddWithoutValidation("Content-Type", answer.ContentType);
            }

            if (message.Method == HttpMethod.Head)
            {
                response.Content.Headers.ContentLength = answer.Body.Length;
            }

            if (host.Equals(fake.Origin, StringComparison.OrdinalIgnoreCase))
            {
                response.Headers.Date = fake.Now;
                if (answer.Status != 401)
                {
                    response.Headers.TryAddWithoutValidation("X-JFrog-Version", $"Artifactory/{fake.Version} 77161260");
                    response.Headers.TryAddWithoutValidation("X-Artifactory-Id", "fake0123456789");
                }
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

    /// <summary>An answer's body of which half arrives, and then the connection is lost.</summary>
    private sealed class BrokenOff(byte[] body) : MemoryStream(body, 0, body.Length / 2, writable: false)
    {
        // no length to go by: the answer was to be longer than what comes of it
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return read > 0 ? read : throw new IOException("The response ended prematurely.");
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
