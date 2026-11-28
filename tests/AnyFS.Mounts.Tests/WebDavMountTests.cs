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

    private async Task<(IMount Mount, TestMountContext Context, HttpClient Client)> StartAsync(ISource source, string collection = "work", int? port = null, TestMountContext? shareWith = null, bool readOnly = false, string? user = null, string? password = null, Func<IVirtualFileSystem, IVirtualFileSystem>? wrap = null)
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
        IVirtualFileSystem vfs = new SingleSourceVfs(source, collection, readOnly);
        await mount.StartAsync(wrap?.Invoke(vfs) ?? vfs, context, CancellationToken.None);
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

    /// <summary>
    /// A PUT whose body breaks off part way is no file. The part that came was closed as the file, which a source that takes a
    /// file at its close (SharePoint's upload) put in place of the one that was there (review of 21-Nov-2026, R3): it is
    /// given up now, and a PUT that came whole is closed as before.
    /// </summary>
    [TestMethod]
    public async Task APutWhoseBodyBreaksOffIsGivenUpNotKeptAsFarAsItCame()
    {
        ClosesTold told = null!;
        var (_, _, client) = await StartAsync(Tree(), wrap: vfs => told = new ClosesTold(vfs));

        using var broken = new HttpRequestMessage(HttpMethod.Put, "work/broken.bin") { Content = new BreakingContent(declared: 4 << 20, sent: 1 << 20) };
        try
        {
            using var answer = await client.SendAsync(broken);
            Assert.Fail($"the request was to break off, and was answered {(int)answer.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
        }

        var until = System.Diagnostics.Stopwatch.StartNew();
        while (told.Of("broken.bin").Length == 0 && until.Elapsed < TimeSpan.FromSeconds(15))
        {
            await Task.Delay(50);
        }

        CollectionAssert.AreEqual(new[] { "given up" }, told.Of("broken.bin"), string.Join(", ", told.Of("broken.bin")));

        Assert.AreEqual(HttpStatusCode.Created, (await client.PutAsync("work/whole.bin", new ByteArrayContent(new byte[100_000]))).StatusCode);
        CollectionAssert.AreEqual(new[] { "closed" }, told.Of("whole.bin"), string.Join(", ", told.Of("whole.bin")));
    }

    /// <summary>A body that says how long it is and breaks off before that.</summary>
    private sealed class BreakingContent(long declared, int sent) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(new byte[sent]);
            await stream.FlushAsync();
            throw new IOException("the upload broke off");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = declared;
            return true;
        }
    }

    /// <summary>
    /// A COPY whose read fails part way is no file either. The part that was read was closed as the target, which a source
    /// that takes a file at its close put in place of the one that was there: a PUT was given up for that, and a COPY was
    /// not (the review of 21-Nov-2026, N16). One that is read whole is closed as before.
    /// </summary>
    [TestMethod]
    public async Task ACopyThatFailsPartWayIsGivenUpNotKeptAsFarAsItCame()
    {
        ClosesTold told = null!;
        var source = Tree().AddFile("big.bin", new byte[1 << 20]);
        var (_, _, client) = await StartAsync(source, wrap: vfs => told = new ClosesTold(new Faulty(vfs, (call, path, offset)
            => call == "read" && path == "big.bin" && offset >= 200_000 ? VfsException.Unavailable("the source stopped answering") : null)));

        var broken = await client.SendAsync(Request("COPY", "work/big.bin", null, ("Destination", "/work/part.bin")));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, broken.StatusCode, await broken.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(new[] { "given up" }, told.Of("part.bin"), string.Join(", ", told.Of("part.bin")));

        var whole = await client.SendAsync(Request("COPY", "work/medium.bin", null, ("Destination", "/work/whole.bin")));
        Assert.AreEqual(HttpStatusCode.Created, whole.StatusCode, await whole.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(new[] { "closed" }, told.Of("whole.bin"), string.Join(", ", told.Of("whole.bin")));
        CollectionAssert.AreEqual(Medium, source.GetFile("whole.bin"));
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
    public async Task AFoldersListingIsSentAsItIsMadeAndOneThatBreaksOffPartWayIsBrokenOffToo()
    {
        // Every entry's answer was made, put into one document, that into one string and that into bytes before any of
        // it was sent: 780 MB allocated and 511 MB held at once to list 100,000 files (the review of 21-Nov-2026, P13)
        var source = Tree();
        for (var n = 0; n < 3000; n++)
        {
            source.AddFile($"many/file {n:D4}.txt", "x");
        }

        var breaksAt = long.MaxValue;
        var (_, _, client) = await StartAsync(source, wrap: vfs => new Faulty(vfs, (call, path, given)
            => call == "list" && path == "many" && given >= Volatile.Read(ref breaksAt) ? new IOException("the source's connection broke") : null));

        using (var whole = await client.SendAsync(Request("PROPFIND", "work/many/", null, ("Depth", "1")), HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.AreEqual((HttpStatusCode)207, whole.StatusCode);
            Assert.IsNull(whole.Content.Headers.ContentLength, "sent in pieces, its length not known when it begins");
            var doc = XDocument.Parse(await whole.Content.ReadAsStringAsync());
            var hrefs = doc.Root!.Elements(D + "response").Select(r => r.Element(D + "href")!.Value).ToList();
            Assert.AreEqual(3001, hrefs.Count, "the folder and its 3,000 files");
            Assert.AreEqual("/work/many/", hrefs[0]);
            CollectionAssert.Contains(hrefs, "/work/many/file%200000.txt");
            CollectionAssert.Contains(hrefs, "/work/many/file%202999.txt");
            Assert.AreEqual("1", Prop(ResponseFor(doc, "/work/many/file%201500.txt")!, "getcontentlength")!.Value);
        }

        // broken off part way: the answer has begun, and ends without its end
        Volatile.Write(ref breaksAt, 2000);
        using (var broken = await client.SendAsync(Request("PROPFIND", "work/many/", null, ("Depth", "1")), HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.AreEqual((HttpStatusCode)207, broken.StatusCode);
            var read = new MemoryStream();
            var failed = false;
            try
            {
                await (await broken.Content.ReadAsStreamAsync()).CopyToAsync(read);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                failed = true;
            }

            var text = Encoding.UTF8.GetString(read.ToArray());
            Assert.IsTrue(failed, $"the answer came to an end as if whole, {text.Length:N0} characters of it");
            Assert.IsFalse(text.Contains("</D:multistatus>", StringComparison.Ordinal));
        }

        // broken before its first entry: answered with the error, as it was
        Volatile.Write(ref breaksAt, 0);
        using var refused = await client.SendAsync(Request("PROPFIND", "work/many/", null, ("Depth", "1")));
        Assert.IsTrue((int)refused.StatusCode >= 500, $"{(int)refused.StatusCode}");

        // and a folder with nothing in it is its own answer alone
        Volatile.Write(ref breaksAt, long.MaxValue);
        var empty = await XmlAsync(await client.SendAsync(Request("PROPFIND", "work/emptydir/", null, ("Depth", "1"))));
        Assert.AreEqual(1, empty.Root!.Elements(D + "response").Count());
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

    /// <summary>
    /// A name that reads as an escape ("Q3%20report.pdf") is that name and no other. The path the server framework hands
    /// over is decoded already; decoded once more, every request for the file went to "Q3 report.pdf": it was listed, and
    /// its GET, PUT and DELETE reached the other file, or none (review of 21-Nov-2026). A Destination, which comes escaped,
    /// is decoded the one time too, as an address and as a path.
    /// </summary>
    [TestMethod]
    public async Task ANameThatReadsAsAnEscapeIsReachedByTheHrefItIsListedWith()
    {
        // each with the file a second decoding, or a plus read as a space, would name
        (string Name, string Twin)[] names = [("Q3%20report.pdf", "Q3 report.pdf"), ("a+b.txt", "a b.txt"), ("100%25.txt", "100%.txt"), ("50%2Foff.txt", "50/off.txt")];
        var source = Tree();
        foreach (var (name, twin) in names)
        {
            source.AddFile("odd/" + name, "this is " + name).AddFile("odd/" + twin, "the twin of " + name);
        }

        var (_, _, client) = await StartAsync(source);
        var doc = await XmlAsync(await client.SendAsync(Request("PROPFIND", "work/odd/", null, ("Depth", "1"))));
        foreach (var (name, twin) in names)
        {
            var href = "/work/odd/" + Uri.EscapeDataString(name);
            Assert.AreEqual(name, Prop(ResponseFor(doc, href)!, "displayname")!.Value, "listed by an href that spells its name, escaped once");
            Assert.AreEqual("this is " + name, await client.GetStringAsync(href), "the href sent back reads that very file");

            Assert.AreEqual(HttpStatusCode.NoContent, (await client.PutAsync(href, new StringContent("written to " + name))).StatusCode, "a PUT replaces it, and makes no other");
            Assert.AreEqual("written to " + name, Encoding.UTF8.GetString(source.GetFile("odd/" + name)!));
            Assert.AreEqual("the twin of " + name, Encoding.UTF8.GetString(source.GetFile("odd/" + twin)!), "its twin is as it was");

            var copy = await client.SendAsync(Request("COPY", href, null, ("Destination", $"{client.BaseAddress}work/odd/{Uri.EscapeDataString("copy of " + name)}")));
            Assert.AreEqual(HttpStatusCode.Created, copy.StatusCode, await copy.Content.ReadAsStringAsync());
            Assert.AreEqual("written to " + name, Encoding.UTF8.GetString(source.GetFile("odd/copy of " + name)!), "a Destination that is an address");
            var move = await client.SendAsync(Request("MOVE", "/work/odd/" + Uri.EscapeDataString("copy of " + name), null, ("Destination", "/work/odd/" + Uri.EscapeDataString("moved " + name))));
            Assert.AreEqual(HttpStatusCode.Created, move.StatusCode, await move.Content.ReadAsStringAsync());
            Assert.AreEqual("written to " + name, Encoding.UTF8.GetString(source.GetFile("odd/moved " + name)!), "a Destination that is a path");
            Assert.IsFalse(source.Exists("odd/copy of " + name), "moved from where the copy went");

            Assert.AreEqual(HttpStatusCode.NoContent, (await client.DeleteAsync(href)).StatusCode);
            Assert.IsFalse(source.Exists("odd/" + name), "the DELETE took that file");
            Assert.IsTrue(source.Exists("odd/" + twin), "and not its twin");
        }
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

    /// <summary>
    /// The root tells a client of the collections it may see and of no other: those with no login, and those whose login
    /// it sent. It listed every collection to anyone, the names of those behind a login among them (the review of
    /// 21-Nov-2026, R14b). With none to show a client that has not signed in, it asks for the sign-in as any other path
    /// does. OPTIONS, which says nothing of the collections and is what Windows asks first, answers as it did.
    /// </summary>
    [TestMethod]
    public async Task TheRootTellsNothingOfACollectionToAClientThatHasNotSignedIn()
    {
        var port = FreePort();
        var (_, context, client) = await StartAsync(Tree(), "private", port, user: "alice", password: "s3cret");
        const string alice = "alice:s3cret";

        HttpRequestMessage Root(string method, string? login = null, string depth = "1")
            => login is null
                ? Request(method, "", null, ("Depth", depth))
                : Request(method, "", null, ("Depth", depth), ("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(login))));

        async Task<string[]> ListedAsync(string? login)
            => (await XmlAsync(await client.SendAsync(Root("PROPFIND", login)))).Root!.Elements(D + "response").Select(r => r.Element(D + "href")!.Value).ToArray();

        // a collection behind a login and no other: the root says nothing of it to a client that has not signed in, and asks
        foreach (var (method, depth) in new[] { ("PROPFIND", "1"), ("PROPFIND", "0"), ("GET", "1"), ("HEAD", "1") })
        {
            var asked = await client.SendAsync(Root(method, depth: depth));
            Assert.AreEqual(HttpStatusCode.Unauthorized, asked.StatusCode, $"{method}, depth {depth}, not signed in");
            StringAssert.StartsWith(asked.Headers.WwwAuthenticate.Single().ToString(), "Basic");
            Assert.IsFalse((await asked.Content.ReadAsStringAsync()).Contains("private", StringComparison.Ordinal), $"{method}, depth {depth}: the name is not told");
        }

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(Root("PROPFIND", "alice:wrong"))).StatusCode, "a wrong password is no sign-in");

        var options = await client.SendAsync(Root("OPTIONS"));
        Assert.AreEqual(HttpStatusCode.OK, options.StatusCode, "OPTIONS needs no sign-in");
        Assert.AreEqual("1, 2", string.Join(", ", options.Headers.GetValues("DAV")));
        Assert.AreEqual("DAV", options.Headers.GetValues("MS-Author-Via").Single());
        StringAssert.Contains(string.Join(", ", options.Content.Headers.Allow), "PROPFIND");

        CollectionAssert.AreEqual(new[] { "/", "/private/" }, await ListedAsync(alice), "signed in, the collection is listed");
        StringAssert.Contains(await (await client.SendAsync(Root("GET", alice))).Content.ReadAsStringAsync(), "<a href=\"/private/\">private/</a>");

        // beside it a collection with no login: anyone is told of that one, as before, and of it alone
        await StartAsync(new MemorySource().AddFile("b.txt", "b"), "open", port, shareWith: context);
        CollectionAssert.AreEqual(new[] { "/", "/open/" }, await ListedAsync(null), "not signed in");
        CollectionAssert.AreEqual(new[] { "/", "/open/" }, await ListedAsync("alice:wrong"), "a wrong password");
        var page = await (await client.SendAsync(Root("GET"))).Content.ReadAsStringAsync();
        StringAssert.Contains(page, "<a href=\"/open/\">open/</a>");
        Assert.IsFalse(page.Contains("private", StringComparison.Ordinal), "the page names no collection behind a login");
        CollectionAssert.AreEqual(new[] { "/", "/open/", "/private/" }, await ListedAsync(alice), "signed in");

        // and one behind another login: each is told of their own
        await StartAsync(new MemorySource().AddFile("c.txt", "c"), "theirs", port, shareWith: context, user: "bob", password: "pa55");
        CollectionAssert.AreEqual(new[] { "/", "/open/", "/private/" }, await ListedAsync(alice));
        CollectionAssert.AreEqual(new[] { "/", "/open/", "/theirs/" }, await ListedAsync("bob:pa55"));
        CollectionAssert.AreEqual(new[] { "/", "/open/" }, await ListedAsync(null));
    }

    /// <summary>
    /// A body that is XML is refused where it passes the limit, as it is read. All of it was taken into memory first,
    /// whatever its size, and only then held against the limit (the review of 21-Nov-2026, R14b). A PUT's body has no
    /// limit: a file is big.
    /// </summary>
    [TestMethod]
    [Timeout(120000)]
    public async Task AnXmlBodyPastTheLimitIsRefusedAsItIsReadNotTakenWhole()
    {
        const int limit = 1 << 20;
        var source = Tree();
        var (_, _, client) = await StartAsync(source);
        var port = client.BaseAddress!.Port;

        // said to be eight megabytes, and a little of it has come: the answer does not wait for the rest
        var head = $"PROPFIND /work/ HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nDepth: 0\r\nContent-Type: text/xml\r\n";
        var declared = await AnswerBeforeTheBodyEndsAsync(port, head + $"Content-Length: {8 << 20}\r\n\r\n", Encoding.ASCII.GetBytes(PropfindOf(4096)));
        StringAssert.StartsWith(declared, "HTTP/1.1 413", "a body whose length says it is over the limit");

        // not said how long (chunked), two megabytes in and no end sent: refused where it passed the limit
        var chunk = Encoding.ASCII.GetBytes($"{65536:X}\r\n{new string(' ', 65536)}\r\n");
        var chunked = await AnswerBeforeTheBodyEndsAsync(port, head + "Transfer-Encoding: chunked\r\n\r\n", Enumerable.Repeat(chunk, 32).SelectMany(c => c).ToArray());
        StringAssert.StartsWith(chunked, "HTTP/1.1 413", "a body that passes the limit as it comes");

        // sent whole by a client that sends to the end, to everything that reads a body as XML
        foreach (var (method, url) in new[] { ("PROPFIND", "work/readme.txt"), ("PROPPATCH", "work/readme.txt"), ("LOCK", "work/readme.txt"), ("PROPFIND", "") })
        {
            var over = await client.SendAsync(Request(method, url, PropfindOf(limit + 1), ("Depth", "0")));
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, over.StatusCode, $"{method} /{url} with a byte over the limit");
        }

        // at the limit it is read as it was, and so is one within it that does not say how long it is
        var within = await XmlAsync(await client.SendAsync(Request("PROPFIND", "work/readme.txt", PropfindOf(limit), ("Depth", "0"))));
        Assert.AreEqual("12", Prop(within.Root!.Element(D + "response")!, "getcontentlength")!.Value);
        var unsized = Request("PROPFIND", "work/readme.txt", PropfindOf(4096), ("Depth", "0"));
        unsized.Headers.TransferEncodingChunked = true;
        within = await XmlAsync(await client.SendAsync(unsized));
        Assert.AreEqual("12", Prop(within.Root!.Element(D + "response")!, "getcontentlength")!.Value);

        var big = new byte[3 << 20];
        new Random(7).NextBytes(big);
        Assert.AreEqual(HttpStatusCode.Created, (await client.PutAsync("work/big.bin", new ByteArrayContent(big))).StatusCode, "a PUT past that limit is a file like any other");
        CollectionAssert.AreEqual(big, source.GetFile("big.bin"));
    }

    /// <summary>A well-formed PROPFIND body of exactly so many bytes: the question is short, and blanks fill it out.</summary>
    private static string PropfindOf(int bytes)
    {
        const string open = "<?xml version=\"1.0\"?><D:propfind xmlns:D=\"DAV:\"><D:prop><D:getcontentlength/></D:prop>";
        const string close = "</D:propfind>";
        return open + new string(' ', bytes - open.Length - close.Length) + close;
    }

    /// <summary>
    /// Sends a request's head and the start of its body over a connection of its own, and no more: the first line of what
    /// the server answers while the rest of the body is still owed, or what happened in its place.
    /// </summary>
    private static async Task<string> AnswerBeforeTheBodyEndsAsync(int port, string head, byte[] bodySoFar)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        var sending = SendAsync();
        string answer;
        using (var patience = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                answer = await reader.ReadLineAsync(patience.Token) ?? "the connection closed with no answer";
            }
            catch (OperationCanceledException)
            {
                answer = "no answer in 15 s";
            }
            catch (IOException ex)
            {
                answer = "the connection broke with no answer: " + ex.Message;
            }
        }

        tcp.Close();
        await sending;
        return answer;

        async Task SendAsync()
        {
            try
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(bodySoFar);
                await stream.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // answered, and closed, before all of it was taken
            }
        }
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
