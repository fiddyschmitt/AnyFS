using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using AnyFS.Mounts.WebDav;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;

namespace AnyFS.Mounts.Tests;

/// <summary>Spec 8.4 and 16.1: the WebDAV provider in process, through HttpClient.</summary>
[TestClass]
public class WebDavMountTests
{
    private static readonly XNamespace D = "DAV:";
    private static readonly byte[] Medium = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 31 % 253)).ToArray();
    private readonly List<IAsyncDisposable> _cleanup = [];

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var d in Enumerable.Reverse(_cleanup))
        {
            await d.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static MemorySource Tree() => new MemorySource()
        .AddFile("readme.txt", "hello readme")
        .AddFile("docs/a.txt", "aaa")
        .AddFile("docs/sub/c.txt", "c")
        .AddFile("medium.bin", Medium, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero))
        .AddFile("unicode/ünïcödé ✓ 日本語.txt", "unicode names")
        .AddDirectory("emptydir");

    private sealed record Collection(string Name) : ICollectionInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private async Task<(IMount Mount, TestMountContext Context, HttpClient Client)> StartAsync(ISource source, string collection = "work", int? port = null, TestMountContext? shareWith = null, bool readOnly = false, string? user = null, string? password = null)
    {
        await source.InitialiseAsync(new TestSourceContext(collection), CancellationToken.None);
        var p = port ?? FreePort();
        var provider = new WebDavMountProvider();
        var settings = (WebDavMountSettings)provider.CreateDefaultSettings(new Collection(collection));
        settings.Port = p;
        settings.User = user;
        settings.Password = password;
        var mount = provider.CreateMount(settings, new Collection(collection));
        var context = new TestMountContext(collection, "webdav", shareWith: shareWith);
        await mount.StartAsync(new SingleSourceVfs(source, collection, readOnly), context, CancellationToken.None);
        _cleanup.Add(mount);
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{p}/") };
        _cleanup.Add(new ClientDisposer(client));
        Assert.AreEqual($"http://localhost:{p}/{collection}/", mount.AccessPaths[0].Value);
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual($@"\\localhost@{p}\DavWWWRoot\{collection}", mount.AccessPaths[1].Value, "Windows gets the redirector's UNC form too");
        }

        return (mount, context, client);
    }

    private static HttpRequestMessage Request(string method, string url, string? body = null, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "text/xml");
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    private static async Task<XDocument> XmlAsync(HttpResponseMessage response)
    {
        Assert.AreEqual((HttpStatusCode)207, response.StatusCode, await response.Content.ReadAsStringAsync());
        return XDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static XElement? ResponseFor(XDocument doc, string href)
        => doc.Root!.Elements(D + "response").FirstOrDefault(r => r.Element(D + "href")!.Value == href);

    private static XElement? Prop(XElement response, string name, int status = 200)
        => response.Elements(D + "propstat").FirstOrDefault(p => p.Element(D + "status")!.Value.Contains(status.ToString()))?.Element(D + "prop")?.Element(D + name);

    [TestMethod]
    public async Task OptionsAdvertisesClassTwo()
    {
        var (_, _, client) = await StartAsync(Tree());
        var response = await client.SendAsync(Request("OPTIONS", "work/"));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("1, 2", string.Join(", ", response.Headers.GetValues("DAV")));
        Assert.AreEqual("DAV", response.Headers.GetValues("MS-Author-Via").Single());
        var allow = string.Join(", ", response.Content.Headers.Allow);
        StringAssert.Contains(allow, "PROPFIND");
        StringAssert.Contains(allow, "LOCK");
    }

    [TestMethod]
    public async Task PropfindDepthZeroOneAndRoot()
    {
        var (_, _, client) = await StartAsync(Tree());
        var doc = await XmlAsync(await client.SendAsync(Request("PROPFIND", "work/", null, ("Depth", "1"))));
        var hrefs = doc.Root!.Elements(D + "response").Select(r => r.Element(D + "href")!.Value).ToList();
        CollectionAssert.Contains(hrefs, "/work/");
        CollectionAssert.Contains(hrefs, "/work/docs/");
        CollectionAssert.Contains(hrefs, "/work/readme.txt");
        CollectionAssert.Contains(hrefs, "/work/emptydir/");
        CollectionAssert.Contains(hrefs, "/work/unicode/");
        Assert.IsFalse(hrefs.Contains("/work/docs/a.txt"), "depth 1 stops at the children");

        var readme = ResponseFor(doc, "/work/readme.txt")!;
        Assert.AreEqual("12", Prop(readme, "getcontentlength")!.Value);
        Assert.AreEqual("readme.txt", Prop(readme, "displayname")!.Value);
        Assert.IsFalse(Prop(readme, "resourcetype")!.HasElements, "a file has an empty resourcetype");
        StringAssert.StartsWith(Prop(readme, "getetag")!.Value, "\"");
        Assert.IsNotNull(Prop(readme, "getlastmodified"));
        Assert.IsNotNull(Prop(readme, "supportedlock")!.Element(D + "lockentry"), "writable: locks offered");
        Assert.AreEqual("text/plain", Prop(readme, "getcontenttype")!.Value);

        var docs = ResponseFor(doc, "/work/docs/")!;
        Assert.IsNotNull(Prop(docs, "resourcetype")!.Element(D + "collection"));
        Assert.IsNull(Prop(docs, "getcontentlength"), "collections have no length");

        var depth0 = await XmlAsync(await client.SendAsync(Request("PROPFIND", "work/docs", null, ("Depth", "0"))));
        Assert.AreEqual(1, depth0.Root!.Elements(D + "response").Count());
        Assert.AreEqual("/work/docs/", depth0.Root!.Element(D + "response")!.Element(D + "href")!.Value, "a collection's href ends with a slash whatever the request said");

        var infinity = await client.SendAsync(Request("PROPFIND", "work/", null, ("Depth", "infinity")));
        Assert.AreEqual(HttpStatusCode.Forbidden, infinity.StatusCode);
        StringAssert.Contains(await infinity.Content.ReadAsStringAsync(), "propfind-finite-depth");

        var named = await XmlAsync(await client.SendAsync(Request("PROPFIND", "work/readme.txt",
            "<?xml version=\"1.0\"?><D:propfind xmlns:D=\"DAV:\" xmlns:x=\"urn:x\"><D:prop><D:getcontentlength/><x:nosuch/></D:prop></D:propfind>", ("Depth", "0"))));
        var response = named.Root!.Element(D + "response")!;
        Assert.AreEqual("12", Prop(response, "getcontentlength")!.Value);
        var missing = response.Elements(D + "propstat").Single(p => p.Element(D + "status")!.Value.Contains("404"));
        Assert.AreEqual("nosuch", missing.Element(D + "prop")!.Elements().Single().Name.LocalName);

        var root = await XmlAsync(await client.SendAsync(Request("PROPFIND", "", null, ("Depth", "1"))));
        CollectionAssert.Contains(root.Root!.Elements(D + "response").Select(r => r.Element(D + "href")!.Value).ToList(), "/work/");

        Assert.AreEqual(HttpStatusCode.NotFound, (await client.SendAsync(Request("PROPFIND", "work/nope", null, ("Depth", "0")))).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.SendAsync(Request("PROPFIND", "other/", null, ("Depth", "0")))).StatusCode);
    }

    [TestMethod]
    public async Task GetRangesHeadAndDirectoryPage()
    {
        var (_, _, client) = await StartAsync(Tree());
        Assert.AreEqual("hello readme", await client.GetStringAsync("work/readme.txt"));

        var request = new HttpRequestMessage(HttpMethod.Get, "work/medium.bin");
        request.Headers.Range = new RangeHeaderValue(10, 19);
        var partial = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.AreEqual("bytes 10-19/100000", partial.Content.Headers.ContentRange!.ToString());
        CollectionAssert.AreEqual(Medium[10..20], await partial.Content.ReadAsByteArrayAsync());

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "work/medium.bin"));
        Assert.AreEqual(HttpStatusCode.OK, head.StatusCode);
        Assert.AreEqual(100_000, head.Content.Headers.ContentLength);
        Assert.IsNotNull(head.Headers.ETag);
        Assert.AreEqual("bytes", head.Headers.AcceptRanges.Single());

        var page = await client.GetStringAsync("work/docs/");
        StringAssert.Contains(page, "<a href=\"a.txt\">a.txt</a>");
        StringAssert.Contains(page, "<a href=\"sub/\">sub/</a>");
    }

    [TestMethod]
    public async Task PutMkcolCopyMoveDelete()
    {
        var source = Tree();
        var (_, _, client) = await StartAsync(source);

        var put = await client.PutAsync("work/new.txt", new StringContent("hello"));
        Assert.AreEqual(HttpStatusCode.Created, put.StatusCode);
        Assert.AreEqual("hello", Encoding.UTF8.GetString(source.GetFile("new.txt")!));
        Assert.AreEqual(HttpStatusCode.NoContent, (await client.PutAsync("work/new.txt", new StringContent("hello again"))).StatusCode, "overwriting an existing file");
        Assert.AreEqual("hello again", Encoding.UTF8.GetString(source.GetFile("new.txt")!));
        Assert.AreEqual(HttpStatusCode.Conflict, (await client.PutAsync("work/missing/new.txt", new StringContent("x"))).StatusCode, "no parent");
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, (await client.PutAsync("work/docs", new StringContent("x"))).StatusCode, "a folder");

        Assert.AreEqual(HttpStatusCode.Created, (await client.SendAsync(Request("MKCOL", "work/newdir/"))).StatusCode);
        Assert.IsTrue(source.Exists("newdir"));
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, (await client.SendAsync(Request("MKCOL", "work/newdir/"))).StatusCode, "exists");
        Assert.AreEqual(HttpStatusCode.Conflict, (await client.SendAsync(Request("MKCOL", "work/nope/deeper/"))).StatusCode, "no parent");

        var copy = await client.SendAsync(Request("COPY", "work/new.txt", null, ("Destination", $"{client.BaseAddress}work/newdir/copy.txt")));
        Assert.AreEqual(HttpStatusCode.Created, copy.StatusCode, await copy.Content.ReadAsStringAsync());
        Assert.AreEqual("hello again", Encoding.UTF8.GetString(source.GetFile("newdir/copy.txt")!));
        Assert.IsTrue(source.Exists("new.txt"), "COPY keeps the source");

        var move = await client.SendAsync(Request("MOVE", "work/newdir/copy.txt", null, ("Destination", "/work/moved.txt")));
        Assert.AreEqual(HttpStatusCode.Created, move.StatusCode);
        Assert.IsFalse(source.Exists("newdir/copy.txt"));
        Assert.AreEqual("hello again", Encoding.UTF8.GetString(source.GetFile("moved.txt")!));

        var noOverwrite = await client.SendAsync(Request("MOVE", "work/moved.txt", null, ("Destination", "/work/readme.txt"), ("Overwrite", "F")));
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, noOverwrite.StatusCode);
        var overwrite = await client.SendAsync(Request("MOVE", "work/moved.txt", null, ("Destination", "/work/readme.txt")));
        Assert.AreEqual(HttpStatusCode.NoContent, overwrite.StatusCode, "replacing an existing target");
        Assert.AreEqual("hello again", Encoding.UTF8.GetString(source.GetFile("readme.txt")!));

        var copyTree = await client.SendAsync(Request("COPY", "work/docs/", null, ("Destination", "/work/docs2/")));
        Assert.AreEqual(HttpStatusCode.Created, copyTree.StatusCode);
        Assert.AreEqual("c", Encoding.UTF8.GetString(source.GetFile("docs2/sub/c.txt")!), "a collection copy is recursive");

        Assert.AreEqual(HttpStatusCode.NoContent, (await client.DeleteAsync("work/docs2/")).StatusCode, "a non-empty folder deletes recursively");
        Assert.IsFalse(source.Exists("docs2"));
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.DeleteAsync("work/docs2/")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.DeleteAsync("work/")).StatusCode, "the collection root stays");
    }

    [TestMethod]
    public async Task LocksGuardWritesUntilUnlocked()
    {
        var source = Tree();
        var (mount, _, client) = await StartAsync(source);
        const string lockInfo = "<?xml version=\"1.0\"?><D:lockinfo xmlns:D=\"DAV:\"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype><D:owner><D:href>mailto:someone@example.org</D:href></D:owner></D:lockinfo>";

        var locked = await client.SendAsync(Request("LOCK", "work/readme.txt", lockInfo, ("Timeout", "Second-120"), ("Depth", "0")));
        Assert.AreEqual(HttpStatusCode.OK, locked.StatusCode, await locked.Content.ReadAsStringAsync());
        var token = locked.Headers.GetValues("Lock-Token").Single().Trim('<', '>');
        StringAssert.StartsWith(token, "opaquelocktoken:");
        var body = XDocument.Parse(await locked.Content.ReadAsStringAsync());
        var active = body.Root!.Element(D + "lockdiscovery")!.Element(D + "activelock")!;
        Assert.AreEqual(token, active.Element(D + "locktoken")!.Element(D + "href")!.Value);
        Assert.AreEqual("Second-120", active.Element(D + "timeout")!.Value);
        Assert.AreEqual("/work/readme.txt", active.Element(D + "lockroot")!.Element(D + "href")!.Value);
        Assert.AreEqual("mailto:someone@example.org", active.Element(D + "owner")!.Element(D + "href")!.Value);

        Assert.AreEqual((HttpStatusCode)423, (await client.PutAsync("work/readme.txt", new StringContent("x"))).StatusCode, "no token");
        var withToken = Request("PUT", "work/readme.txt", null, ("If", $"(<{token}>)"));
        withToken.Content = new StringContent("locked write");
        Assert.AreEqual(HttpStatusCode.NoContent, (await client.SendAsync(withToken)).StatusCode);
        Assert.AreEqual("locked write", Encoding.UTF8.GetString(source.GetFile("readme.txt")!));

        Assert.AreEqual((HttpStatusCode)423, (await client.SendAsync(Request("LOCK", "work/readme.txt", lockInfo))).StatusCode, "someone else");
        var refresh = await client.SendAsync(Request("LOCK", "work/readme.txt", null, ("If", $"(<{token}>)"), ("Timeout", "Second-300")));
        Assert.AreEqual(HttpStatusCode.OK, refresh.StatusCode, "refresh with the token and no body");
        StringAssert.Contains(await refresh.Content.ReadAsStringAsync(), "Second-300");

        var propfind = await XmlAsync(await client.SendAsync(Request("PROPFIND", "work/readme.txt", null, ("Depth", "0"))));
        var discovery = Prop(propfind.Root!.Element(D + "response")!, "lockdiscovery")!;
        Assert.AreEqual(token, discovery.Element(D + "activelock")!.Element(D + "locktoken")!.Element(D + "href")!.Value);

        Assert.AreEqual(HttpStatusCode.Conflict, (await client.SendAsync(Request("UNLOCK", "work/readme.txt", null, ("Lock-Token", "<opaquelocktoken:wrong>")))).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await client.SendAsync(Request("UNLOCK", "work/readme.txt", null, ("Lock-Token", $"<{token}>")))).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await client.PutAsync("work/readme.txt", new StringContent("free again"))).StatusCode);

        var lockNull = await client.SendAsync(Request("LOCK", "work/brand-new.docx", lockInfo));
        Assert.AreEqual(HttpStatusCode.Created, lockNull.StatusCode, "a lock on a missing file creates it empty (lock-null), as Office expects");
        Assert.AreEqual(0, source.GetFile("brand-new.docx")!.Length);
        var nullToken = lockNull.Headers.GetValues("Lock-Token").Single().Trim('<', '>');
        Assert.AreEqual(HttpStatusCode.NoContent, (await client.SendAsync(Request("UNLOCK", "work/brand-new.docx", null, ("Lock-Token", $"<{nullToken}>")))).StatusCode);

        var dirLock = await client.SendAsync(Request("LOCK", "work/docs/", lockInfo, ("Depth", "infinity")));
        Assert.AreEqual(HttpStatusCode.OK, dirLock.StatusCode);
        Assert.AreEqual((HttpStatusCode)423, (await client.DeleteAsync("work/docs/a.txt")).StatusCode, "an infinite-depth lock covers the children");
        var dirToken = dirLock.Headers.GetValues("Lock-Token").Single().Trim('<', '>');
        Assert.AreEqual(HttpStatusCode.NoContent, (await client.SendAsync(Request("UNLOCK", "work/docs/", null, ("Lock-Token", $"<{dirToken}>")))).StatusCode);
        Assert.AreEqual(0, ((WebDavMount)mount).LockCount);
    }

    [TestMethod]
    public async Task ReadOnlyMountRefusesEveryWrite()
    {
        var source = Tree();
        var (_, _, client) = await StartAsync(source, readOnly: true);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.PutAsync("work/new.txt", new StringContent("x"))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.SendAsync(Request("MKCOL", "work/d/"))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.DeleteAsync("work/readme.txt")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.SendAsync(Request("MOVE", "work/readme.txt", null, ("Destination", "/work/r.txt")))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.SendAsync(Request("LOCK", "work/readme.txt", "<D:lockinfo xmlns:D=\"DAV:\"><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockinfo>"))).StatusCode);
        Assert.AreEqual("hello readme", await client.GetStringAsync("work/readme.txt"), "reads are fine");
        var doc = await XmlAsync(await client.SendAsync(Request("PROPFIND", "work/readme.txt", null, ("Depth", "0"))));
        Assert.IsFalse(Prop(doc.Root!.Element(D + "response")!, "supportedlock")!.HasElements, "no lock entries offered on a read-only resource");
    }

    [TestMethod]
    public async Task BasicAuthenticationWhenConfigured()
    {
        var (_, _, client) = await StartAsync(Tree(), user: "alice", password: "s3cret");
        var anonymous = await client.SendAsync(Request("PROPFIND", "work/", null, ("Depth", "0")));
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        StringAssert.StartsWith(anonymous.Headers.WwwAuthenticate.Single().ToString(), "Basic");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:wrong")));
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(Request("PROPFIND", "work/", null, ("Depth", "0")))).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret")));
        Assert.AreEqual((HttpStatusCode)207, (await client.SendAsync(Request("PROPFIND", "work/", null, ("Depth", "0")))).StatusCode);
        Assert.AreEqual("hello readme", await client.GetStringAsync("work/readme.txt"));
    }

    [TestMethod]
    public async Task TwoCollectionsShareOneServer()
    {
        var port = FreePort();
        var (_, context, client) = await StartAsync(Tree(), "work", port);
        var (_, _, _) = await StartAsync(new MemorySource().AddFile("b.txt", "b"), "other", port, shareWith: context);
        Assert.AreEqual(1, context.SharedCount, "one Kestrel for both");
        var root = await XmlAsync(await client.SendAsync(Request("PROPFIND", "", null, ("Depth", "1"))));
        var hrefs = root.Root!.Elements(D + "response").Select(r => r.Element(D + "href")!.Value).ToList();
        CollectionAssert.Contains(hrefs, "/work/");
        CollectionAssert.Contains(hrefs, "/other/");
        Assert.AreEqual("b", await client.GetStringAsync("other/b.txt"));
    }

    [TestMethod]
    public void SettingsValidation()
    {
        var provider = new WebDavMountProvider();
        var messages = provider.Validate(new WebDavMountSettings { BindAddress = "0.0.0.0", Port = 11000 }).ToList();
        Assert.IsTrue(messages.Any(m => !m.IsError && m.Message.Contains("no password")), "LAN without a password warns");
        messages = provider.Validate(new WebDavMountSettings { User = "bob", Password = "" }).ToList();
        Assert.IsTrue(messages.Any(m => m.IsError && m.Property == nameof(WebDavMountSettings.Password)));
        Assert.AreEqual(0, provider.Validate(new WebDavMountSettings()).Count());
        Assert.AreEqual(@"\\nas@11000\DavWWWRoot\work", WebDavMountProvider.WindowsUncPath("nas", 11000, "work"));
    }

    private sealed class ClientDisposer(HttpClient client) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
