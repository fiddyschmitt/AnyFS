using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AnyFS.Sources.Common;
using AnyFS.Sources.Microsoft365;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

/// <summary>
/// A stand-in for SharePoint when the lab's farms and the Microsoft 365 sandbox are not there (spec 7.3): one model of
/// sites, subsites, libraries, folders, files, pages and accounts, answered as the real servers were seen to answer
/// (M6-NOTES, the lab's probes). Two kinds:
/// <list type="bullet">
/// <item>a farm (<see cref="StartFarm"/>): SharePoint Server's REST API on a port of 127.0.0.1, signed in to with a
/// Windows account (Basic here, any OS; or Windows' own NTLM, Windows only), refusing with a 401 as the farms do, no
/// throttling, no search service unless switched on;</item>
/// <item>a tenant (<see cref="Tenant"/>): SharePoint Online through <see cref="Handler"/>, its REST API on a browser
/// session (FedAuth, the 403s, a session cookie set again on every signed-in answer, 429 with Retry-After when told),
/// Graph for an app sign-in (sites, drives, items, upload sessions, download URLs, search, subsites) and SharePoint
/// Embedded's containers.</item>
/// </list>
/// The REST API is in FakeSharePoint.Rest.cs, Graph in FakeSharePoint.Graph.cs, the lab's and the sandbox's contents in
/// FakeSharePoint.Seeds.cs. Every answer goes through <see cref="Answer"/>, under one lock; switches change what it
/// answers (<see cref="Fail"/>, <see cref="Throttle"/>, <see cref="HonoursRanges"/>, <see cref="SearchService"/>...),
/// counters say what it was asked (<see cref="Requests"/>).
/// </summary>
internal sealed partial class FakeSharePoint : IDisposable
{
    public enum Kind
    {
        /// <summary>SharePoint Server (Subscription Edition, 2019): Windows sign-in, a refusal is a 401.</summary>
        Farm,

        /// <summary>SharePoint Online and Graph: a browser session or a bearer token, a refusal is a 403.</summary>
        Online,
    }

    public enum FarmSignIn
    {
        /// <summary>Basic: the user name and password the source was given, checked here (any OS).</summary>
        Basic,

        /// <summary>Windows' own Negotiate or NTLM, as the farms: the signed-in Windows account (Windows only).</summary>
        Windows,
    }

    public enum Role
    {
        None,
        Read,
        Edit,

        /// <summary>A site collection administrator: also the only one whose file times SharePoint sets.</summary>
        Owner,
    }

    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Web> _collections = [];
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Fault> _faults = [];
    private HttpListener? _listener;

    private FakeSharePoint(Kind kind, string scheme, string host)
    {
        Flavour = kind;
        Scheme = scheme;
        Host = host.ToLowerInvariant();
        Root = AddCollectionLocked("", "Home");
    }

    /// <summary>Farm or tenant.</summary>
    public Kind Flavour { get; }

    public string Scheme { get; }

    /// <summary>The farm's or the tenant's host, with its port where it has one ("127.0.0.1:54321", "anyfs-fake.sharepoint.com").</summary>
    public string Host { get; }

    /// <summary>"http://127.0.0.1:54321", "https://anyfs-fake.sharepoint.com": what a site's address starts with.</summary>
    public string Origin => $"{Scheme}://{Host}";

    /// <summary>The root site collection's own web (the tenant's or the farm's root site, "/").</summary>
    public Web Root { get; }

    /// <summary>Every request answered, oldest first.</summary>
    public ConcurrentQueue<Seen> Requests { get; } = new();

    /// <summary>How long every answer waits first.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>Whether a Range is honoured (206); false sends the whole file (200), as a careless proxy would.</summary>
    public bool HonoursRanges { get; set; } = true;

    /// <summary>Whether search answers (the lab's farms have no search service; SharePoint Online does).</summary>
    public bool SearchService { get; set; }

    /// <summary>How long a request digest is good for (the farms and SharePoint Online say 1,800 s).</summary>
    public TimeSpan DigestLifetime { get; set; } = TimeSpan.FromSeconds(1800);

    /// <summary>Whether the pages' list items have CanvasContent1 (modern pages); false answers as a SharePoint without them.</summary>
    public bool ModernPages { get; set; } = true;

    /// <summary>What the farm's Windows sign-in counts as (the account a Windows-signed-in request is), for <see cref="FarmSignIn.Windows"/>.</summary>
    public string WindowsUserAs { get; set; } = "";

    // --- construction ---------------------------------------------------------------------------------------------

    /// <summary>A farm on a free port of <paramref name="listenHost"/> (127.0.0.1, or localhost for a second "server").</summary>
    public static FakeSharePoint StartFarm(string listenHost = "127.0.0.1", FarmSignIn signIn = FarmSignIn.Basic)
    {
        var port = FreePort();
        var fake = new FakeSharePoint(Kind.Farm, "http", $"{listenHost}:{port}") { SignIn = signIn };
        fake.Listen(listenHost, port);
        return fake;
    }

    /// <summary>
    /// A tenant, reached through <see cref="Handler"/> (its host, Graph's, and the download and upload addresses it gives).
    /// Unless <paramref name="paced"/>, the source sends to it as fast as it likes: SharePoint Online's pace of 10 a second
    /// would make a suite of a few thousand requests take minutes (a throttled answer still holds them, Retry-After).
    /// </summary>
    public static FakeSharePoint Tenant(string label = "anyfs-fake", bool paced = false)
    {
        var fake = new FakeSharePoint(Kind.Online, "https", $"{label}.sharepoint.com") { SearchService = true, Paced = paced };
        fake.Unpace(fake.Host);
        return fake;
    }

    /// <summary>Whether the source paces itself for this tenant as for SharePoint Online (10 a second, 100 at once).</summary>
    public bool Paced { get; private init; }

    private void Unpace(string key)
    {
        if (!Paced)
        {
            RequestPace.Use(key, new RequestPace(burst: 1_000_000, perSecond: MicrosoftPace.PerSecond));
        }
    }

    /// <summary>How the farm's sign-in is checked.</summary>
    public FarmSignIn SignIn { get; private init; }

    // --- the model ------------------------------------------------------------------------------------------------

    /// <summary>A site collection at <paramref name="path"/> ("/sites/anyfs"): its own web, with its own permissions.</summary>
    public Web AddSite(string path, string title, bool modern = true)
    {
        lock (_gate)
        {
            var web = AddCollectionLocked(path, title);
            web.Modern = modern;
            return web;
        }
    }

    private Web AddCollectionLocked(string path, string title)
    {
        var web = new Web(this, path.TrimEnd('/'), title, parent: null) { IsSiteCollection = true, UniquePermissions = true };
        _collections.Add(web);
        return web;
    }

    /// <summary>A subsite of <paramref name="parent"/> named <paramref name="leaf"/>; it inherits its parent's permissions unless given its own.</summary>
    public Web AddSubsite(Web parent, string leaf, string title, bool modern = true, bool uniquePermissions = false)
    {
        lock (_gate)
        {
            var web = new Web(this, $"{parent.Path}/{leaf}", title, parent) { Modern = modern, UniquePermissions = uniquePermissions };
            parent.Subwebs.Add(web);
            return web;
        }
    }

    /// <summary>A library of <paramref name="web"/>: its title, the folder name in its address, its template (101 documents, 119 pages).</summary>
    public Library AddLibrary(Web web, string title, string urlName, int baseTemplate = 101, bool hidden = false, bool inGraphDrives = true)
    {
        lock (_gate)
        {
            var library = new Library(this, web, title, urlName, baseTemplate) { Hidden = hidden, InGraphDrives = inGraphDrives };
            web.Libraries.Add(library);
            if (baseTemplate == 101 && !hidden)
            {
                // a document library's own forms folder, which SharePoint lists and the source leaves out
                var forms = library.Root.AddFolder("Forms");
                forms.AddFile("AllItems.aspx", Encoding.UTF8.GetBytes("<%@ Page %>"));
            }

            return library;
        }
    }

    /// <summary>An account: its name ("ANYFS\sp_user", "tester@anyfs-fake.onmicrosoft.com") and password (a farm's Basic sign-in).</summary>
    public Account AddAccount(string name, string? password = null)
    {
        lock (_gate)
        {
            var account = new Account(name, password);
            _accounts[name] = account;
            if (Flavour == Kind.Online)
            {
                // SharePoint Embedded's requests are paced by the account
                Unpace(name);
            }

            return account;
        }
    }

    /// <summary>Gives <paramref name="account"/> a role on <paramref name="web"/> (and the webs that inherit from it).</summary>
    public void Grant(Web web, string account, Role role)
    {
        lock (_gate)
        {
            web.Roles[account] = role;
        }
    }

    /// <summary>A file at <paramref name="path"/> in <paramref name="library"/>, its folders made as needed.</summary>
    public FileItem Seed(Library library, string path, byte[] content)
    {
        lock (_gate)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var folder = library.Root;
            foreach (var part in parts[..^1])
            {
                folder = folder.Folder(part) ?? folder.AddFolder(part);
            }

            return folder.AddFile(parts[^1], content);
        }
    }

    /// <summary>Every web of the farm or tenant, collections and subsites, parents first.</summary>
    public IEnumerable<Web> Webs
    {
        get
        {
            lock (_gate)
            {
                var all = new List<Web>();
                foreach (var collection in _collections)
                {
                    Walk(collection, all);
                }

                return all;
            }
        }
    }

    private static void Walk(Web web, List<Web> all)
    {
        all.Add(web);
        foreach (var sub in web.Subwebs)
        {
            Walk(sub, all);
        }
    }

    /// <summary>The web at a server-relative path ("/sites/anyfs/anyfs-subsite"), whatever its case; null when no web is there.</summary>
    public Web? WebAt(string path)
    {
        lock (_gate)
        {
            var (web, rest) = DeepestWeb(path);
            return rest.Length == 0 ? web : null;
        }
    }

    /// <summary>The deepest web a server-relative path is in, and what is left of the path below it.</summary>
    private (Web Web, string Remaining) DeepestWeb(string path)
    {
        var trimmed = "/" + path.Trim('/');
        if (trimmed == "/")
        {
            trimmed = "";
        }

        var collection = _collections
            .Where(c => c.Path.Length == 0 || trimmed.Equals(c.Path, StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith(c.Path + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Path.Length)
            .First();
        var web = collection;
        var rest = trimmed[collection.Path.Length..].Trim('/');
        while (rest.Length > 0)
        {
            var slash = rest.IndexOf('/');
            var segment = slash < 0 ? rest : rest[..slash];
            var sub = web.Subwebs.FirstOrDefault(w => w.Leaf.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (sub is null)
            {
                break;
            }

            web = sub;
            rest = slash < 0 ? "" : rest[(slash + 1)..];
        }

        return (web, rest);
    }

    /// <summary>
    /// What a server-relative path names: its web, the library it is in (null for the web's own folder), and the item
    /// (null when nothing is there). A library's own address names its root folder.
    /// </summary>
    private (Web Web, Library? Library, Item? Item) Resolve(string path)
    {
        var (web, rest) = DeepestWeb(path);
        if (rest.Length == 0)
        {
            return (web, null, web.OwnFolder);
        }

        var parts = rest.Split('/');
        var library = web.Libraries.FirstOrDefault(l => l.UrlName.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (library is null)
        {
            // the web's own folder: default.aspx, a classic page
            Item? own = parts.Length == 1 ? web.OwnFolder.Child(parts[0]) : null;
            return (web, null, own);
        }

        Item? item = library.Root;
        foreach (var part in parts[1..])
        {
            item = (item as FolderItem)?.Child(part);
            if (item is null)
            {
                break;
            }
        }

        return (web, library, item);
    }

    /// <summary>What <paramref name="account"/> may do in <paramref name="web"/>: its role there, or where the web inherits from.</summary>
    private Role RoleOf(Web web, Account? account)
    {
        if (account is null)
        {
            return Role.None;
        }

        // a site collection administrator is one throughout the collection
        var collection = web.Collection;
        if (collection.Roles.TryGetValue(account.Name, out var top) && top == Role.Owner)
        {
            return Role.Owner;
        }

        var w = web;
        while (!w.UniquePermissions && w.Parent is not null)
        {
            w = w.Parent;
        }

        return w.Roles.TryGetValue(account.Name, out var role) ? role : w.Everyone;
    }

    // --- switches -------------------------------------------------------------------------------------------------

    /// <summary>The next <paramref name="times"/> requests that <paramref name="when"/> matches get <paramref name="answer"/> instead.</summary>
    public void Fail(Func<Request, bool> when, int times, Func<Request, Response> answer)
    {
        lock (_gate)
        {
            _faults.Add(new Fault(when, times, answer));
        }
    }

    /// <summary>The next <paramref name="times"/> requests (that <paramref name="when"/> matches) are throttled: 429 (or 503) with Retry-After.</summary>
    public void Throttle(int times, int retryAfterSeconds = 1, Func<Request, bool>? when = null, int status = 429)
        => Fail(when ?? (_ => true), times, request => request.IsGraph
            ? Response.Json(status, GraphError("activityLimitReached", "The request has been throttled")).With("Retry-After", retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : Response.Json(status, RestError("-2147024860, Microsoft.SharePoint.SPQueryThrottledException", "The request has been throttled.")).With("Retry-After", retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Requests answered that match, for a test's counting.</summary>
    public int Count(Func<Seen, bool> match) => Requests.Count(match);

    // --- answering ------------------------------------------------------------------------------------------------

    /// <summary>The answer to one request, whichever way it came (the listener, or <see cref="Handler"/>).</summary>
    public async Task<Response> Answer(Request request, CancellationToken ct)
    {
        if (Latency > TimeSpan.Zero)
        {
            await Task.Delay(Latency, ct).ConfigureAwait(false);
        }

        Response response;
        lock (_gate)
        {
            response = TakeFault(request) ?? AnswerLocked(request);
        }

        Requests.Enqueue(new Seen(request.Method, request.Host, request.RawPath, request.RawQuery, response.Status, request.Account?.Name));
        return response;
    }

    private Response? TakeFault(Request request)
    {
        foreach (var fault in _faults)
        {
            if (fault.Remaining > 0 && fault.When(request))
            {
                fault.Remaining--;
                return fault.Answer(request);
            }
        }

        return null;
    }

    private Response AnswerLocked(Request request)
    {
        if (Flavour == Kind.Online && request.IsGraph)
        {
            return Graph(request);
        }

        if (!request.Host.Equals(Host, StringComparison.OrdinalIgnoreCase))
        {
            return Response.Empty(404);
        }

        if (Flavour == Kind.Online && request.RawPath.StartsWith(GraphTransfers, StringComparison.OrdinalIgnoreCase))
        {
            return GraphTransfer(request);
        }

        return SharePoint(request);
    }

    private sealed class Fault(Func<Request, bool> when, int times, Func<Request, Response> answer)
    {
        public Func<Request, bool> When { get; } = when;

        public int Remaining { get; set; } = times;

        public Func<Request, Response> Answer { get; } = answer;
    }

    /// <summary>One request answered: what was asked, and how it went.</summary>
    public sealed record Seen(string Method, string Host, string RawPath, string RawQuery, int Status, string? Account)
    {
        public string Path => Uri.UnescapeDataString(RawPath);
    }

    // --- hosting --------------------------------------------------------------------------------------------------

    private void Listen(string host, int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{host}:{port}/");
        if (SignIn == FarmSignIn.Windows)
        {
            listener.AuthenticationSchemes = AuthenticationSchemes.Negotiate | AuthenticationSchemes.Ntlm;
        }

        listener.Start();
        _listener = listener;
        _ = ServeAsync(listener);
    }

    private async Task ServeAsync(HttpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => ServeOneAsync(context));
        }
    }

    private async Task ServeOneAsync(HttpListenerContext context)
    {
        try
        {
            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body).ConfigureAwait(false);
            var raw = context.Request.RawUrl ?? "/";
            var question = raw.IndexOf('?');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in context.Request.Headers.AllKeys)
            {
                if (name is not null)
                {
                    headers[name] = context.Request.Headers[name] ?? "";
                }
            }

            var request = new Request
            {
                Method = context.Request.HttpMethod,
                Scheme = "http",
                Host = context.Request.Url?.Authority.ToLowerInvariant() ?? Host,
                RawPath = question < 0 ? raw : raw[..question],
                RawQuery = question < 0 ? "" : raw[(question + 1)..],
                Headers = headers,
                Body = body.ToArray(),
                WindowsUser = context.User?.Identity is { IsAuthenticated: true } identity ? identity.Name : null,
            };
            var response = await Answer(request, _stop.Token).ConfigureAwait(false);
            await WriteAsync(context.Response, response).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
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
    }

    private static async Task WriteAsync(HttpListenerResponse target, Response response)
    {
        target.StatusCode = response.Status;
        foreach (var (name, value) in response.Headers)
        {
            if (name.Equals("Location", StringComparison.OrdinalIgnoreCase))
            {
                target.RedirectLocation = value;
            }
            else
            {
                target.AppendHeader(name, value);
            }
        }

        if (response.ContentType is not null)
        {
            target.ContentType = response.ContentType;
        }

        target.ContentLength64 = response.Body.Length;
        if (response.Body.Length > 0)
        {
            await target.OutputStream.WriteAsync(response.Body).ConfigureAwait(false);
        }
    }

    /// <summary>A handler that answers in-process, without a socket: the tenant's host, Graph's, and the addresses it gives out.</summary>
    public HttpMessageHandler Handler() => new InProcessHandler(this);

    private sealed class InProcessHandler(FakeSharePoint fake) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
        {
            var uri = message.RequestUri!;
            var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            if (!host.Equals(fake.Host, StringComparison.OrdinalIgnoreCase) && !(fake.Flavour == Kind.Online && IsGraphHost(uri.Host)))
            {
                throw new HttpRequestException($"No such host is known. ({uri.Host})");
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

            var pathAndQuery = uri.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
            var question = pathAndQuery.IndexOf('?');
            var request = new Request
            {
                Method = message.Method.Method,
                Scheme = uri.Scheme,
                Host = host.ToLowerInvariant(),
                RawPath = question < 0 ? pathAndQuery : pathAndQuery[..question],
                RawQuery = question < 0 ? "" : pathAndQuery[(question + 1)..],
                Headers = headers,
                Body = body,
            };
            var response = await fake.Answer(request, ct).ConfigureAwait(false);
            var answer = new HttpResponseMessage((HttpStatusCode)response.Status) { RequestMessage = message, Content = new ByteArrayContent(response.Body) };
            if (response.ContentType is not null)
            {
                answer.Content.Headers.TryAddWithoutValidation("Content-Type", response.ContentType);
            }

            foreach (var (name, value) in response.Headers)
            {
                if (!answer.Headers.TryAddWithoutValidation(name, value))
                {
                    answer.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }

            return answer;
        }
    }

    private static bool IsGraphHost(string host) => host is "graph.microsoft.com" or "graph.microsoft.us";

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    // --- the model's parts ----------------------------------------------------------------------------------------

    /// <summary>An account, and the password a farm's Basic sign-in checks.</summary>
    public sealed class Account(string name, string? password)
    {
        public string Name { get; } = name;

        public string? Password { get; set; } = password;

        /// <summary>Whether Graph lists containers for it at all (an account without a OneDrive gets none).</summary>
        public bool HasOneDrive { get; set; } = true;
    }

    /// <summary>A web: a site collection's own, or a subsite.</summary>
    public sealed class Web
    {
        internal Web(FakeSharePoint owner, string path, string title, Web? parent)
        {
            Owner = owner;
            Path = path;
            Title = title;
            Parent = parent;
            OwnFolder = new FolderItem(this, library: null, name: "", parent: null);
        }

        public FakeSharePoint Owner { get; }

        /// <summary>Server-relative, no trailing slash: "" for the root, "/sites/anyfs", "/sites/anyfs/anyfs-subsite".</summary>
        public string Path { get; }

        public string Leaf => Path[(Path.LastIndexOf('/') + 1)..];

        public string Title { get; set; }

        public Web? Parent { get; }

        public bool IsSiteCollection { get; init; }

        /// <summary>Whether its default.aspx forwards to its home page in Site Pages (a modern site), or is a classic page.</summary>
        public bool Modern { get; set; } = true;

        public bool UniquePermissions { get; set; }

        /// <summary>The role every account has here, where no role is given it.</summary>
        public Role Everyone { get; set; }

        public Dictionary<string, Role> Roles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<Web> Subwebs { get; } = [];

        public List<Library> Libraries { get; } = [];

        /// <summary>The web's own folder: default.aspx, a classic page.</summary>
        public FolderItem OwnFolder { get; }

        public Guid Id { get; } = Guid.NewGuid();

        public Web Collection => IsSiteCollection ? this : Parent!.Collection;

        public string Url => Owner.Origin + Path;

        /// <summary>Graph's id of the site: the host, the collection's id, the web's id.</summary>
        public string GraphId => $"{Owner.Host},{Collection.Id},{Id}";

        /// <summary>Whether search finds it (as a site collection; a new one only after a while).</summary>
        public bool Searchable { get; set; } = true;

        public Library Library(string title) => Libraries.First(l => l.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A library (a document library, Site Pages), or a SharePoint Embedded container (no web).</summary>
    public sealed class Library
    {
        internal Library(FakeSharePoint owner, Web? web, string title, string urlName, int baseTemplate)
        {
            Owner = owner;
            Web = web;
            Title = title;
            UrlName = urlName;
            BaseTemplate = baseTemplate;
            Root = new FolderItem(web, this, name: urlName, parent: null);
            DriveId = "b!" + Convert.ToBase64String([.. (web?.Collection.Id ?? Guid.Empty).ToByteArray(), .. Id.ToByteArray()]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public FakeSharePoint Owner { get; }

        public Web? Web { get; }

        public string Title { get; set; }

        public string UrlName { get; }

        /// <summary>101 a document library, 119 a site's pages.</summary>
        public int BaseTemplate { get; }

        public bool Hidden { get; set; }

        /// <summary>Whether Graph lists it among the site's drives (it leaves out Form Templates, Site Assets, Style Library and Site Pages).</summary>
        public bool InGraphDrives { get; set; } = true;

        public bool Pages => BaseTemplate == 119;

        public FolderItem Root { get; }

        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>Graph's id of its drive: "b!" and the ids of its site and list.</summary>
        public string DriveId { get; internal set; }

        /// <summary>Server-relative: "/sites/anyfs/Shared Documents".</summary>
        public string Url => $"{Web?.Path}/{UrlName}";

        public long QuotaTotal { get; set; } = 1L << 40;

        // SharePoint Embedded
        public Guid ContainerTypeId { get; init; }

        public string Status { get; set; } = "active";

        public HashSet<string> Members { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long Used => Root.Size;

        /// <summary>When an item in it last changed (the list's LastItemModifiedDate, Graph's lastModifiedDateTime): what a library's stamp is made of.</summary>
        public DateTimeOffset LastItemModified => Everything(Root).Select(i => i.Modified).DefaultIfEmpty(Root.Modified).Max();

        /// <summary>How many items it holds (the list's ItemCount).</summary>
        public int ItemCount => Everything(Root).Count();
    }

    /// <summary>A file or a folder.</summary>
    public abstract class Item
    {
        protected Item(Web? web, Library? library, string name, FolderItem? parent)
        {
            Web = web;
            Library = library;
            Name = name;
            Parent = parent;
            var now = DateTimeOffset.UtcNow;
            Created = Modified = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
        }

        public Web? Web { get; }

        public Library? Library { get; }

        public string Name { get; set; }

        public FolderItem? Parent { get; set; }

        public Guid UniqueId { get; } = Guid.NewGuid();

        /// <summary>Graph's item id: opaque, "01" and 32 more.</summary>
        public string GraphId => "01" + Convert.ToHexString(UniqueId.ToByteArray());

        public DateTimeOffset Created { get; set; }

        public DateTimeOffset Modified { get; set; }

        /// <summary>Raised on every change (the eTag's number).</summary>
        public int Version { get; set; } = 1;

        /// <summary>The path below the library's root ("" for the root), or below the web's own folder.</summary>
        public string RelativePath => Parent is null ? "" : Parent.RelativePath.Length == 0 ? Name : $"{Parent.RelativePath}/{Name}";

        /// <summary>Server-relative: "/sites/anyfs/Shared Documents/a/b.txt", "/sites/anyfs/default.aspx".</summary>
        public string ServerRelativeUrl => Library is { } library
            ? RelativePath.Length == 0 ? library.Url : $"{library.Url}/{RelativePath}"
            : $"{Web!.Path}/{Name}";

        /// <summary>REST's ETag: "{GUID},version" in quotes.</summary>
        public string RestETag => $"\"{{{UniqueId.ToString().ToUpperInvariant()}}},{Version}\"";

        public string GraphETag => $"\"{{{UniqueId.ToString().ToUpperInvariant()}}},{Version}\"";

        public void Touch(DateTimeOffset? when = null)
        {
            var now = when ?? DateTimeOffset.UtcNow;
            Modified = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
            Version++;
        }
    }

    public sealed class FolderItem : Item
    {
        internal FolderItem(Web? web, Library? library, string name, FolderItem? parent)
            : base(web, library, name, parent)
        {
        }

        public List<Item> Children { get; } = [];

        public Item? Child(string name) => Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public FolderItem? Folder(string name) => Child(name) as FolderItem;

        public FileItem? File(string name) => Child(name) as FileItem;

        public FolderItem AddFolder(string name)
        {
            var folder = new FolderItem(Web, Library, name, this);
            Children.Add(folder);
            Touch();
            return folder;
        }

        public FileItem AddFile(string name, byte[] content)
        {
            if (File(name) is { } existing)
            {
                existing.Replace(content);
                return existing;
            }

            var file = new FileItem(Web, Library, name, this) { Content = content };
            Children.Add(file);
            Touch();
            return file;
        }

        public long Size => Children.Sum(c => c switch
        {
            FileItem f => f.Content.LongLength,
            FolderItem d => d.Size,
            _ => 0,
        });
    }

    public sealed class FileItem : Item
    {
        internal FileItem(Web? web, Library? library, string name, FolderItem parent)
            : base(web, library, name, parent)
        {
        }

        public byte[] Content { get; set; } = [];

        /// <summary>Raised on every change to the content (the cTag's number).</summary>
        public int ContentVersion { get; set; } = 1;

        // a page's list item fields
        public string? Title { get; set; }

        public string? CanvasContent1 { get; set; }

        public string? WikiField { get; set; }

        /// <summary>Checked out or locked: writes refused.</summary>
        public bool Locked { get; set; }

        public string GraphCTag => $"\"c:{{{UniqueId.ToString().ToUpperInvariant()}}},{ContentVersion}\"";

        public void Replace(byte[] content)
        {
            Content = content;
            ContentVersion++;
            Touch();
        }
    }

    // --- requests and answers -------------------------------------------------------------------------------------

    /// <summary>A request, whichever way it came: its address as sent (still percent-encoded), headers, body.</summary>
    public sealed class Request
    {
        private Dictionary<string, string>? _query;

        public required string Method { get; init; }

        public required string Scheme { get; init; }

        /// <summary>Host and port, lower-case.</summary>
        public required string Host { get; init; }

        /// <summary>The path as sent, percent-encoded.</summary>
        public required string RawPath { get; init; }

        /// <summary>The query as sent, without the '?'.</summary>
        public required string RawQuery { get; init; }

        public required Dictionary<string, string> Headers { get; init; }

        public required byte[] Body { get; init; }

        /// <summary>The farm's Windows sign-in, as the listener checked it.</summary>
        public string? WindowsUser { get; init; }

        /// <summary>Who the request was answered as, once signed in.</summary>
        public Account? Account { get; set; }

        public bool IsGraph => Host.StartsWith("graph.microsoft.", StringComparison.OrdinalIgnoreCase);

        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

        /// <summary>The query's parameters, unescaped (names as sent).</summary>
        public IReadOnlyDictionary<string, string> Query => _query ??= ParseQuery(RawQuery);

        public string? Cookie(string name)
        {
            foreach (var part in (Header("Cookie") ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq > 0 && part[..eq].Equals(name, StringComparison.Ordinal))
                {
                    return part[(eq + 1)..];
                }
            }

            return null;
        }

        private static Dictionary<string, string> ParseQuery(string raw)
        {
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var name = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
                query[name] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }

            return query;
        }
    }

    /// <summary>An answer: status, headers, body.</summary>
    public sealed class Response
    {
        public int Status { get; init; }

        public List<(string Name, string Value)> Headers { get; } = [];

        public byte[] Body { get; init; } = [];

        public string? ContentType { get; init; }

        public Response With(string name, string value)
        {
            Headers.Add((name, value));
            return this;
        }

        public static Response Empty(int status) => new() { Status = status };

        public static Response Json(int status, System.Text.Json.Nodes.JsonNode json, string contentType = "application/json;odata=nometadata;streaming=true;charset=utf-8")
            => new() { Status = status, Body = Encoding.UTF8.GetBytes(json.ToJsonString()), ContentType = contentType };

        public static Response Text(int status, string text, string contentType = "text/plain; charset=utf-8")
            => new() { Status = status, Body = Encoding.UTF8.GetBytes(text), ContentType = contentType };

        public static Response Bytes(int status, byte[] body, string contentType = "application/octet-stream") => new() { Status = status, Body = body, ContentType = contentType };
    }

    private static string NewSecret(int bytes = 32) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));
}
