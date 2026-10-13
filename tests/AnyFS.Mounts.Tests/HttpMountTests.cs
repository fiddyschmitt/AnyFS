using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AnyFS.Mounts.Http;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;

namespace AnyFS.Mounts.Tests;

/// <summary>Spec 8.6 and 16.1: the plain HTTP provider in process, through HttpClient.</summary>
[TestClass]
public class HttpMountTests
{
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
        .AddFile("docs/b.txt", "bbbbbb")
        .AddFile("docs/sub/c.txt", "c")
        .AddFile("medium.bin", Medium, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero))
        .AddFile("unicode/ünïcödé ✓ 日本語.txt", "unicode names")
        .AddDirectory("emptydir");

    private async Task<(IMount Mount, TestMountContext Context, HttpClient Client, string Base)> StartAsync(ISource source, string collection = "work", int? port = null, TestMountContext? shareWith = null, bool readOnly = false)
    {
        await source.InitialiseAsync(new TestSourceContext(collection), CancellationToken.None);
        var p = port ?? FreePort();
        var provider = new HttpMountProvider();
        var settings = (HttpMountSettings)provider.CreateDefaultSettings(new Info(collection));
        settings.Port = p;
        var mount = provider.CreateMount(settings, new Info(collection));
        var context = new TestMountContext(collection, "http", shareWith: shareWith);
        await mount.StartAsync(new SingleSourceVfs(source, collection, readOnly), context, CancellationToken.None);
        _cleanup.Add(mount);
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{p}/") };
        _cleanup.Add(new ClientDisposer(client));
        Assert.AreEqual($"http://localhost:{p}/{collection}/", mount.AccessPaths.Single().Value);
        Assert.IsInstanceOfType<MountOpenAction.Browser>(mount.OpenAction);
        return (mount, context, client, $"{collection}/");
    }

    [TestMethod]
    public async Task ListingPageAndJson()
    {
        var (_, _, client, b) = await StartAsync(Tree());
        var html = await client.GetStringAsync(b);
        StringAssert.Contains(html, "<a href=\"docs/\">docs/</a>");
        StringAssert.Contains(html, "<a href=\"readme.txt\">readme.txt</a>");
        StringAssert.Contains(html, "<a href=\"emptydir/\">emptydir/</a>");
        StringAssert.Contains(html, "?sort=size");
        Assert.IsTrue(html.IndexOf("docs/", StringComparison.Ordinal) < html.IndexOf("medium.bin", StringComparison.Ordinal), "directories first");

        using var doc = JsonDocument.Parse(await client.GetStringAsync(b + "docs/?format=json"));
        var items = doc.RootElement.EnumerateArray().ToList();
        CollectionAssert.AreEqual(new[] { "sub", "a.txt", "b.txt" }, items.Select(i => i.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual("directory", items[0].GetProperty("type").GetString());
        Assert.AreEqual("sub/", items[0].GetProperty("href").GetString());
        Assert.AreEqual(6, items[2].GetProperty("size").GetInt64());
        Assert.AreEqual("file", items[2].GetProperty("type").GetString());

        using var bySize = JsonDocument.Parse(await client.GetStringAsync(b + "docs/?format=json&sort=size&dir=desc"));
        CollectionAssert.AreEqual(new[] { "sub", "b.txt", "a.txt" }, bySize.RootElement.EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToArray());

        var index = await client.GetStringAsync("/");
        StringAssert.Contains(index, "<a href=\"/work/\">work/</a>");
        using var indexJson = JsonDocument.Parse(await client.GetStringAsync("/?format=json"));
        Assert.AreEqual("work", indexJson.RootElement[0].GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task DirectoriesWithoutATrailingSlashRedirect()
    {
        var (_, _, client, _) = await StartAsync(Tree());
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var raw = new HttpClient(handler) { BaseAddress = client.BaseAddress };
        var r = await raw.GetAsync("work");
        Assert.AreEqual(HttpStatusCode.MovedPermanently, r.StatusCode);
        Assert.AreEqual("/work/", r.Headers.Location!.ToString());
        r = await raw.GetAsync("work/docs/sub?format=json");
        Assert.AreEqual(HttpStatusCode.MovedPermanently, r.StatusCode);
        Assert.AreEqual("/work/docs/sub/?format=json", r.Headers.Location!.ToString());
        r = await raw.GetAsync("work/readme.txt/");
        Assert.AreEqual(HttpStatusCode.NotFound, r.StatusCode, "a file with a trailing slash is not a directory");
    }

    [TestMethod]
    public async Task FilesHaveResumableHeadersAndRanges()
    {
        var (_, _, client, b) = await StartAsync(Tree());
        var full = await client.GetAsync(b + "medium.bin");
        Assert.AreEqual(HttpStatusCode.OK, full.StatusCode);
        Assert.AreEqual("bytes", full.Headers.AcceptRanges.Single());
        Assert.IsNotNull(full.Headers.ETag);
        Assert.IsFalse(full.Headers.ETag.IsWeak);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), full.Content.Headers.LastModified);
        Assert.AreEqual("application/octet-stream", full.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual("inline", full.Content.Headers.ContentDisposition!.DispositionType);
        Assert.AreEqual(Medium.Length, full.Content.Headers.ContentLength);
        CollectionAssert.AreEqual(Medium, await full.Content.ReadAsByteArrayAsync());

        var text = await client.GetAsync(b + "readme.txt");
        Assert.AreEqual("text/plain", text.Content.Headers.ContentType!.MediaType);

        using (var request = new HttpRequestMessage(HttpMethod.Get, b + "medium.bin"))
        {
            request.Headers.Range = new RangeHeaderValue(1000, 1999);
            var r = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.PartialContent, r.StatusCode);
            Assert.AreEqual("bytes 1000-1999/100000", r.Content.Headers.ContentRange!.ToString());
            CollectionAssert.AreEqual(Medium[1000..2000], await r.Content.ReadAsByteArrayAsync());
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, b + "medium.bin"))
        {
            request.Headers.Range = new RangeHeaderValue(99_000, null);
            var r = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.PartialContent, r.StatusCode);
            Assert.AreEqual(1000, (await r.Content.ReadAsByteArrayAsync()).Length, "an open-ended range resumes to the end");
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, b + "medium.bin"))
        {
            request.Headers.Range = new RangeHeaderValue(10, 19);
            request.Headers.IfRange = new RangeConditionHeaderValue(full.Headers.ETag);
            var r = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.PartialContent, r.StatusCode, "If-Range with the current ETag honours the range");
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, b + "medium.bin"))
        {
            request.Headers.Range = new RangeHeaderValue(10, 19);
            request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"stale\""));
            var r = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode, "a stale If-Range sends the whole file");
            Assert.AreEqual(Medium.Length, r.Content.Headers.ContentLength);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, b + "medium.bin"))
        {
            request.Headers.Range = new RangeHeaderValue(5_000_000, null);
            var r = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.RequestedRangeNotSatisfiable, r.StatusCode);
            Assert.AreEqual("bytes */100000", r.Content.Headers.ContentRange!.ToString());
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, b + "medium.bin"))
        {
            request.Headers.Range = new RangeHeaderValue { Ranges = { new RangeItemHeaderValue(0, 9), new RangeItemHeaderValue(20, 29) } };
            var r = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode, "multi-range requests get the whole file");
            Assert.AreEqual(Medium.Length, r.Content.Headers.ContentLength);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Head, b + "medium.bin"))
        {
            var r = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual(Medium.Length, r.Content.Headers.ContentLength);
            Assert.AreEqual(0, (await r.Content.ReadAsByteArrayAsync()).Length);
            Assert.AreEqual("bytes", r.Headers.AcceptRanges.Single());
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, b + "medium.bin"))
        {
            request.Headers.IfNoneMatch.Add(full.Headers.ETag);
            var r = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.NotModified, r.StatusCode);
        }
    }

    [TestMethod]
    public async Task ErrorsAndUnicode()
    {
        var (_, _, client, b) = await StartAsync(Tree());
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(b + "nope.txt")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("other/")).StatusCode);
        var put = await client.PutAsync(b + "x.txt", new StringContent("x"));
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.AreEqual("GET, HEAD", string.Join(", ", put.Content.Headers.Allow));

        var listing = await client.GetStringAsync(b + "unicode/");
        StringAssert.Contains(listing, "href=\"%C3%BCn%C3%AFc%C3%B6d%C3%A9%20%E2%9C%93%20%E6%97%A5%E6%9C%AC%E8%AA%9E.txt\"");
        StringAssert.Contains(WebUtility.HtmlDecode(listing), ">ünïcödé ✓ 日本語.txt</a>", "non-ASCII is HTML-encoded on the page");
        Assert.AreEqual("unicode names", await client.GetStringAsync(b + "unicode/%C3%BCn%C3%AFc%C3%B6d%C3%A9%20%E2%9C%93%20%E6%97%A5%E6%9C%AC%E8%AA%9E.txt"));
    }

    [TestMethod]
    public async Task UnavailableSourcesGive503()
    {
        var (_, _, client, b) = await StartAsync(new UnavailableSource());
        var r = await client.GetAsync(b + "anything.txt");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        StringAssert.Contains(await r.Content.ReadAsStringAsync(), "starting");
    }

    [TestMethod]
    public async Task TwoCollectionsShareOneListener()
    {
        var port = FreePort();
        var (workMount, workContext, client, _) = await StartAsync(Tree(), "work", port);
        var (bobMount, bobContext, _, _) = await StartAsync(new MemorySource().AddFile("bob.txt", "bob"), "bob", port, shareWith: workContext);
        Assert.AreEqual(1, workContext.SharedCount, "one Kestrel for the port");
        Assert.AreEqual("hello readme", await client.GetStringAsync("work/readme.txt"));
        Assert.AreEqual("bob", await client.GetStringAsync("bob/bob.txt"));
        StringAssert.Contains(await client.GetStringAsync("/"), "bob/");

        await workMount.StopAsync(CancellationToken.None);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("work/readme.txt")).StatusCode, "work is gone");
        Assert.AreEqual("bob", await client.GetStringAsync("bob/bob.txt"), "bob still served by the shared listener");
        Assert.AreEqual(1, bobContext.SharedCount);

        await bobMount.StopAsync(CancellationToken.None);
        Assert.AreEqual(0, bobContext.SharedCount, "the listener went with the last mount");
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetAsync("bob/bob.txt"));
    }

    [TestMethod]
    public async Task TheSameCollectionNameTwiceOnOnePortFails()
    {
        var port = FreePort();
        var (_, context, _, _) = await StartAsync(Tree(), "work", port);
        var second = new HttpMountProvider().CreateMount(new HttpMountSettings { Port = port }, new Info("WORK"));
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.StartAsync(new SingleSourceVfs(new MemorySource(), "WORK"), new TestMountContext("WORK", shareWith: context), CancellationToken.None));
        StringAssert.Contains(ex.Message, "already served");
        Assert.AreEqual(1, context.SharedCount, "the failed mount released its lease");
    }

    [TestMethod]
    public async Task PortInUseFailsToStart()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        var mount = new HttpMountProvider().CreateMount(new HttpMountSettings { Port = port }, new Info("work"));
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => mount.StartAsync(new SingleSourceVfs(new MemorySource()), new TestMountContext(), CancellationToken.None));
        StringAssert.Contains(ex.Message, "could not listen");
    }

    [TestMethod]
    public async Task ConnectionsAreCounted()
    {
        var (_, context, client, b) = await StartAsync(Tree());
        Assert.AreEqual(0, context.ActiveConnections);
        await client.GetStringAsync(b + "readme.txt");
        Assert.AreEqual(1, context.ActiveConnections, "HttpClient keeps its connection open");
        await client.GetStringAsync(b + "docs/a.txt");
        Assert.AreEqual(1, context.ActiveConnections, "the same connection, counted once");
        client.Dispose();
        for (var i = 0; i < 100 && context.ActiveConnections != 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.AreEqual(0, context.ActiveConnections);
    }

    [TestMethod]
    public async Task StatsCountServedFilesThroughTheCountingWrapper()
    {
        // the counting itself is the core's (CountingVfsFile); here: the mount reads through whatever file it's given, fully
        var source = Tree();
        var (_, _, client, b) = await StartAsync(source);
        var bytes = await client.GetByteArrayAsync(b + "docs/b.txt");
        Assert.AreEqual("bbbbbb", Encoding.UTF8.GetString(bytes));
    }

    [TestMethod]
    public void ProviderMetadataAndValidation()
    {
        var provider = new HttpMountProvider();
        Assert.AreEqual("http", provider.TypeId);
        Assert.IsFalse(provider.SupportsWrites);
        Assert.AreEqual(PerformanceTier.Protocol, provider.Tier);
        Assert.IsNull(provider.UnavailableReason);
        Assert.AreEqual(3, provider.SupportedPlatforms.Count);
        Assert.AreEqual(11001, ((HttpMountSettings)provider.CreateDefaultSettings(new Info("x"))).Port);

        Assert.AreEqual(0, provider.Validate(new HttpMountSettings()).Count(m => m.IsError));
        var messages = provider.Validate(new HttpMountSettings { BindAddress = "not an address", Port = 70000 }).ToList();
        Assert.AreEqual(2, messages.Count(m => m.IsError));
        Assert.IsTrue(provider.Validate(new HttpMountSettings { Port = 80 }).Any(m => !m.IsError && m.Message.Contains("administrator", StringComparison.Ordinal)));

        Assert.IsTrue(HttpMountProvider.TryParseBindAddress("localhost", out var a) && IPAddress.IsLoopback(a));
        Assert.IsTrue(HttpMountProvider.TryParseBindAddress("", out a) && IPAddress.IsLoopback(a));
        Assert.IsTrue(HttpMountProvider.TryParseBindAddress("*", out a) && a.Equals(IPAddress.Any));
        Assert.IsTrue(HttpMountProvider.TryParseBindAddress("0.0.0.0", out a) && a.Equals(IPAddress.Any));
        Assert.IsTrue(HttpMountProvider.TryParseBindAddress("::1", out a) && IPAddress.IsLoopback(a));
        Assert.IsFalse(HttpMountProvider.TryParseBindAddress("example.com", out _));
        Assert.AreEqual("localhost", HttpMountProvider.HostFor(IPAddress.Loopback));
        Assert.AreEqual(Environment.MachineName.ToLowerInvariant(), HttpMountProvider.HostFor(IPAddress.Any));
        Assert.AreEqual("192.168.0.31", HttpMountProvider.HostFor(IPAddress.Parse("192.168.0.31")));
        Assert.AreEqual("localhost", HttpMountProvider.HostFor(IPAddress.IPv6Loopback));
        Assert.AreEqual("[fe80::1]", HttpMountProvider.HostFor(IPAddress.Parse("fe80::1")));
        Assert.AreEqual("1.5 KB", HttpServer.HumanSize(1536));
        Assert.AreEqual("/work/a%20b/c%23.txt", HttpServer.UrlFor("work", "a b/c#.txt", false));
    }

    private sealed record Info(string Name) : ICollectionInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private sealed class ClientDisposer(HttpClient client) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnavailableSource : SourceBase
    {
        public override Task<VfsEntry?> StatAsync(string path, CancellationToken ct) => throw VfsException.Unavailable("source 'x' is starting");

        public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            throw VfsException.Unavailable("source 'x' is starting");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public override Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct) => throw VfsException.Unavailable("source 'x' is starting");
    }
}
