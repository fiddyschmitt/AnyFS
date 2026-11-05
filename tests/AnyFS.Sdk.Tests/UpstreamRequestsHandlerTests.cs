using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AnyFS.Sdk.Testing;

namespace AnyFS.Sdk.Tests;

/// <summary>What a watcher is told of each request: from an answer made here, and over a real connection, whose body comes as it comes.</summary>
[TestClass]
public class UpstreamRequestsHandlerTests
{
    private sealed class Answering(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request, cancellationToken);
    }

    private static (HttpClient Client, RecordingUpstreamWatcher Watcher) Client(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var watcher = new RecordingUpstreamWatcher();
        return (new HttpClient(UpstreamRequestsHandler.Around(new Answering((r, _) => Task.FromResult(answer(r))), watcher)), watcher);
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    [TestMethod]
    public void WithNoWatcherNothingIsPutInTheWay()
    {
        using var inner = new Answering((_, _) => Task.FromResult(Ok("")));
        Assert.AreSame(inner, UpstreamRequestsHandler.Around(inner, null));
        Assert.AreNotSame(inner, UpstreamRequestsHandler.Around(inner, new RecordingUpstreamWatcher()));
    }

    [TestMethod]
    public async Task AnAnswerReadWholeIsOneRequestWithWhatTheServerSaidAndItsBytes()
    {
        var (client, watcher) = Client(_ => Ok("hello"));
        using var _c = client;
        Assert.AreEqual("hello", await client.GetStringAsync("https://server.test/api/thing?page=2"));
        var asked = watcher.Requests.Single();
        Assert.AreEqual("GET", asked.What);
        Assert.AreEqual("https://server.test/api/thing?page=2", asked.Target);
        Assert.AreEqual("200 OK", asked.Answer);
        Assert.IsFalse(asked.Refused);
        Assert.AreEqual(5, asked.Bytes);
        Assert.IsTrue(asked.IsDone);
        Assert.IsNull(asked.Error);
    }

    [TestMethod]
    public async Task ARequestRunsUntilItsBodyHasBeenRead()
    {
        var (client, watcher) = Client(_ => Ok("hello world"));
        using var _c = client;
        using var response = await client.GetAsync("https://server.test/big", HttpCompletionOption.ResponseHeadersRead);
        var asked = watcher.Requests.Single();
        Assert.AreEqual("200 OK", asked.Answer, "it says what the server answered as soon as it has");
        Assert.IsFalse(asked.IsDone, "and goes on while the body comes");

        await using var body = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[5];
        Assert.AreEqual(5, await body.ReadAsync(buffer));
        Assert.AreEqual(5, asked.Bytes);
        Assert.IsFalse(asked.IsDone);

        var rest = new byte[100];
        Assert.AreEqual(6, await body.ReadAsync(rest));
        Assert.AreEqual(0, await body.ReadAsync(rest));
        Assert.AreEqual(11, asked.Bytes);
        Assert.IsTrue(asked.IsDone, "its end is the request's");
    }

    [TestMethod]
    public async Task AnAnswerLeftUnreadEndsTheRequestWhenItIsDisposed()
    {
        var (client, watcher) = Client(_ => Ok("hello"));
        using var _c = client;
        var response = await client.GetAsync("https://server.test/x", HttpCompletionOption.ResponseHeadersRead);
        Assert.IsFalse(watcher.Requests.Single().IsDone);
        response.Dispose();
        Assert.IsTrue(watcher.Requests.Single().IsDone);
        Assert.AreEqual(0, watcher.Requests.Single().Bytes);
        Assert.IsNull(watcher.Requests.Single().Error, "giving up on the rest of an answer is no failure of the server's");
    }

    [TestMethod]
    public async Task AStreamClosedEarlyEndsTheRequest()
    {
        var (client, watcher) = Client(_ => Ok("hello world"));
        using var _c = client;
        var response = await client.GetAsync("https://server.test/x", HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStreamAsync();
        Assert.AreEqual(3, await body.ReadAsync(new byte[3]));
        await body.DisposeAsync();
        Assert.IsTrue(watcher.Requests.Single().IsDone);
        Assert.AreEqual(3, watcher.Requests.Single().Bytes);
    }

    [TestMethod]
    public async Task ARefusalIsToldAsARefusalWithWhatTheServerSaid()
    {
        var (client, watcher) = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no such thing") });
        using var _c = client;
        using var response = await client.GetAsync("https://server.test/x");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual("no such thing", await response.Content.ReadAsStringAsync(), "the body is there for whoever wants it");
        var asked = watcher.Requests.Single();
        Assert.AreEqual("404 Not Found", asked.Answer);
        Assert.IsTrue(asked.Refused);
        Assert.IsTrue(asked.IsDone);
    }

    [TestMethod]
    public async Task ARedirectIsAnAnswerAndNoRefusal()
    {
        var (client, watcher) = Client(_ => new HttpResponseMessage(HttpStatusCode.Found));
        using var _c = client;
        using var response = await client.GetAsync("https://server.test/x");
        Assert.AreEqual("302 Found", watcher.Requests.Single().Answer);
        Assert.IsFalse(watcher.Requests.Single().Refused);
    }

    [TestMethod]
    public async Task ARequestThatGotNoAnswerFailed()
    {
        var watcher = new RecordingUpstreamWatcher();
        using var client = new HttpClient(UpstreamRequestsHandler.Around(new Answering((_, _) => throw new HttpRequestException("no route to host")), watcher));
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetAsync("https://server.test/x"));
        var asked = watcher.Requests.Single();
        Assert.AreEqual("no route to host", asked.Error?.Message);
        Assert.IsNull(asked.Answer);
        Assert.IsTrue(asked.IsDone);
    }

    [TestMethod]
    public async Task ABodyThatBreaksOffFailsTheRequest()
    {
        var (client, watcher) = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream()) });
        using var _c = client;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetAsync("https://server.test/x"));
        var asked = watcher.Requests.Single();
        Assert.IsNotNull(asked.Error, "the answer began well and came to nothing");
        Assert.AreEqual(3, asked.Bytes, "what got through counts");
        Assert.IsTrue(asked.IsDone);
    }

    private sealed class BreakingStream : MemoryStream
    {
        private int _reads;

        // no length to go by: a stream's says how much will come, and this one's is nothing
        public override bool CanSeek => false;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ > 0)
            {
                throw new IOException("the connection dropped");
            }

            "abc"u8.CopyTo(buffer.Span);
            return ValueTask.FromResult(3);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [TestMethod]
    public async Task WhatHasNoBodyEndsAsSoonAsItIsAnswered()
    {
        var (client, watcher) = Client(r => r.RequestUri!.AbsolutePath switch
        {
            "/none" => new HttpResponseMessage(HttpStatusCode.NoContent),
            "/same" => new HttpResponseMessage(HttpStatusCode.NotModified),
            "/empty" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new HeadersOnlyContent(4096) },
        });
        using var _c = client;
        foreach (var address in new[] { "https://server.test/none", "https://server.test/same", "https://server.test/empty" })
        {
            var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead);
            Assert.IsTrue(watcher.Requests[^1].IsDone, address);
            response.Dispose();
        }

        using var request = new HttpRequestMessage(HttpMethod.Head, "https://server.test/size");
        using var head = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.AreEqual("HEAD", watcher.Requests[^1].What);
        Assert.IsTrue(watcher.Requests[^1].IsDone, "whatever length it says, none will come");
        Assert.AreEqual(4096, head.Content.Headers.ContentLength, "and what it says stays as it was");
    }

    private sealed class HeadersOnlyContent : HttpContent
    {
        public HeadersOnlyContent(long length) => Headers.ContentLength = length;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [TestMethod]
    public async Task WhatIsSentCounts()
    {
        var (client, watcher) = Client(_ => Ok("ok"));
        using var _c = client;
        using var response = await client.PutAsync("https://server.test/upload", new ByteArrayContent(new byte[1000]));
        var asked = watcher.Requests.Single();
        Assert.AreEqual("PUT", asked.What);
        Assert.AreEqual(1002, asked.Bytes, "what went up and what came back");
    }

    [TestMethod]
    public async Task AnAnswersHeadersAreAsTheyWere()
    {
        var modified = new DateTimeOffset(2026, 11, 5, 1, 2, 3, TimeSpan.Zero);
        var (client, _) = Client(_ =>
        {
            var content = new ByteArrayContent(new byte[10]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(5, 14, 100);
            content.Headers.LastModified = modified;
            content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = "a.txt" };
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        });
        using var _c = client;
        using var response = await client.GetAsync("https://server.test/x", HttpCompletionOption.ResponseHeadersRead);
        var headers = response.Content.Headers;
        Assert.AreEqual("application/json", headers.ContentType?.MediaType);
        Assert.AreEqual("utf-8", headers.ContentType?.CharSet);
        Assert.AreEqual(10, headers.ContentLength);
        Assert.AreEqual(5, headers.ContentRange?.From);
        Assert.AreEqual(100, headers.ContentRange?.Length);
        Assert.AreEqual(modified, headers.LastModified);
        Assert.AreEqual("a.txt", headers.ContentDisposition?.FileName);
    }

    [TestMethod]
    [DataRow("https://server.test/api/v4/projects/1?ref=main&per_page=100", "https://server.test/api/v4/projects/1?ref=main&per_page=100", DisplayName = "nothing to leave out")]
    [DataRow("https://me:secret@server.test:8443/a/b", "https://server.test:8443/a/b", DisplayName = "no account, and the port stays")]
    [DataRow("https://server.test/a#fragment", "https://server.test/a", DisplayName = "no fragment")]
    [DataRow("https://server.test/a%20b/c%2Fd?path=x%2Fy", "https://server.test/a%20b/c%2Fd?path=x%2Fy", DisplayName = "as it was sent")]
    [DataRow("https://server.test/a?private_token=abc&page=2", "https://server.test/a?private_token=…&page=2", DisplayName = "GitLab's token")]
    [DataRow("https://server.test/a?access_token=abc", "https://server.test/a?access_token=…", DisplayName = "a bearer")]
    [DataRow("https://tenant.test/_layouts/15/download.aspx?UniqueId=abc&tempauth=def", "https://tenant.test/_layouts/15/download.aspx?UniqueId=abc&tempauth=…", DisplayName = "SharePoint's download link")]
    [DataRow("https://s3.test/o?X-Amz-Credential=a&X-Amz-Signature=b&X-Amz-Security-Token=c&X-Amz-Expires=60", "https://s3.test/o?X-Amz-Credential=…&X-Amz-Signature=…&X-Amz-Security-Token=…&X-Amz-Expires=60", DisplayName = "a signed S3 link")]
    [DataRow("https://blob.test/o?sv=2020&sig=abc", "https://blob.test/o?sv=2020&sig=…", DisplayName = "an Azure SAS")]
    [DataRow("https://fcs.test/d?__fcs__jobTicket=abc&name=a.pdf", "https://fcs.test/d?__fcs__jobTicket=…&name=a.pdf", DisplayName = "3DX's download ticket")]
    [DataRow("https://login.test/t?code=abc&client_secret=def&password=ghi&SessionId=jkl&api_key=mno", "https://login.test/t?code=…&client_secret=…&password=…&SessionId=…&api_key=…", DisplayName = "a sign-in's")]
    [DataRow("https://server.test/a?id=eyJhbGciOiJSUzI1NiJ9", "https://server.test/a?id=…", DisplayName = "a JWT under any name")]
    [DataRow("https://server.test/a?flag&x=", "https://server.test/a?flag&x=", DisplayName = "values not all there")]
    public void AnAddressIsShownWithoutWhatIsSecret(string address, string shown) => Assert.AreEqual(shown, UpstreamRequestsHandler.Shown(new Uri(address)));

    [TestMethod]
    public void ALongValueIsLeftOutWhateverItIsCalled()
    {
        var value = new string('x', 129);
        Assert.AreEqual("https://server.test/a?syncState=…&page=1", UpstreamRequestsHandler.Shown(new Uri($"https://server.test/a?syncState={value}&page=1")));
        var shorter = new string('x', 128);
        Assert.AreEqual($"https://server.test/a?path={shorter}", UpstreamRequestsHandler.Shown(new Uri($"https://server.test/a?path={shorter}")));
        Assert.AreEqual("https://server.test/a?…", UpstreamRequestsHandler.Shown(new Uri($"https://server.test/a?{value}")));
    }

    [TestMethod]
    public void AnAddressThatIsNotWholeLosesItsQuery()
    {
        Assert.AreEqual("", UpstreamRequestsHandler.Shown(null));
        Assert.AreEqual("a/b", UpstreamRequestsHandler.Shown(new Uri("a/b?token=abc", UriKind.Relative)));
    }

    [TestMethod]
    public async Task ACallIsOneRequestHoweverItGoes()
    {
        var watcher = new RecordingUpstreamWatcher();
        Assert.AreEqual(7, await watcher.AskAsync("SFTP stat", "/a", () => Task.FromResult(7), CancellationToken.None));
        Assert.IsTrue(watcher.Requests[0].IsDone);
        Assert.IsNull(watcher.Requests[0].Error);

        await Assert.ThrowsExactlyAsync<IOException>(() => watcher.AskAsync<int>("SFTP stat", "/b", () => throw new IOException("gone"), CancellationToken.None));
        Assert.AreEqual("gone", watcher.Requests[1].Error?.Message);
        Assert.IsTrue(watcher.Requests[1].IsDone);

        await watcher.AskAsync("SFTP mkdir", "/c", () => Task.CompletedTask, CancellationToken.None);
        Assert.AreEqual(("SFTP mkdir", "/c", true), (watcher.Requests[2].What, watcher.Requests[2].Target, watcher.Requests[2].IsDone));

        await Assert.ThrowsExactlyAsync<IOException>(() => watcher.AskAsync("SFTP mkdir", "/d", () => throw new IOException("full"), CancellationToken.None));
        Assert.AreEqual("full", watcher.Requests[3].Error?.Message);

        IUpstreamWatcher? nobody = null;
        Assert.AreEqual(8, await nobody.AskAsync("SFTP stat", "/e", () => Task.FromResult(8), CancellationToken.None), "with nobody to tell, it is the call");
        var called = false;
        await nobody.AskAsync("SFTP mkdir", "/f", () =>
        {
            called = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.IsTrue(called);
    }

    [TestMethod]
    public void ACallThatBlocksIsOneRequestHoweverItGoes()
    {
        var watcher = new RecordingUpstreamWatcher();
        Assert.AreEqual(7, watcher.Ask("Get metadata", "/a", () => 7));
        Assert.AreEqual(("Get metadata", "/a", true), (watcher.Requests[0].What, watcher.Requests[0].Target, watcher.Requests[0].IsDone));
        Assert.IsNull(watcher.Requests[0].Error);

        Assert.ThrowsExactly<IOException>(() => watcher.Ask<int>("Get metadata", "/b", () => throw new IOException("gone")));
        Assert.AreEqual(("gone", true), (watcher.Requests[1].Error?.Message, watcher.Requests[1].IsDone));

        var ran = 0;
        watcher.Ask("Delete", "/c", () => ran++);
        Assert.AreEqual((1, "Delete", true), (ran, watcher.Requests[2].What, watcher.Requests[2].IsDone));

        Assert.ThrowsExactly<IOException>(() => watcher.Ask("Delete", "/d", () => throw new IOException("in use")));
        Assert.AreEqual(("in use", true), (watcher.Requests[3].Error?.Message, watcher.Requests[3].IsDone));

        IUpstreamWatcher? nobody = null;
        Assert.AreEqual(8, nobody.Ask("Get metadata", "/e", () => 8), "with nobody to tell, it is the call");
        nobody.Ask("Delete", "/f", () => ran++);
        Assert.AreEqual(2, ran);
    }

    // --- over a real connection -----------------------------------------------------------------------------------------

    /// <summary>HTTP/1.1 on a port of this machine: a fake handler's answer is already in memory, where a real one's comes off a socket and holds the connection until it has been read.</summary>
    private sealed class Server : IDisposable
    {
        private static readonly byte[] Compressed = Gzip("hello from far away");
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private int _connections;

        public Server()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public string Address => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public int Connections => Volatile.Read(ref _connections);

        private static byte[] Gzip(string text)
        {
            using var bytes = new MemoryStream();
            using (var gzip = new GZipStream(bytes, CompressionMode.Compress))
            {
                gzip.Write(Encoding.UTF8.GetBytes(text));
            }

            return bytes.ToArray();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    Interlocked.Increment(ref _connections);
                    _ = ServeAsync(client);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            try
            {
                var stream = client.GetStream();
                while (true)
                {
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(one, _stopping.Token) == 0)
                        {
                            return;
                        }

                        head.Append((char)one[0]);
                    }

                    var path = head.ToString().Split(' ')[1];
                    switch (path)
                    {
                        case "/plain":
                            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 11\r\n\r\nhello world");
                            break;
                        case "/chunked":
                            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n");
                            await WriteAsync(stream, "6\r\n world\r\n0\r\n\r\n");
                            break;
                        case "/gzip":
                            await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: {Compressed.Length}\r\n\r\n");
                            await stream.WriteAsync(Compressed, _stopping.Token);
                            break;
                        case "/missing":
                            await WriteAsync(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 4\r\n\r\ngone");
                            break;
                        case "/hang":
                            await Task.Delay(Timeout.Infinite, _stopping.Token);
                            break;
                        case "/half":
                            await WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\nonly this");
                            return;
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
        }

        private Task WriteAsync(NetworkStream stream, string text) => stream.WriteAsync(Encoding.UTF8.GetBytes(text), _stopping.Token).AsTask();

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();
            _stopping.Dispose();
        }
    }

    private static (HttpClient Client, RecordingUpstreamWatcher Watcher) RealClient()
    {
        var watcher = new RecordingUpstreamWatcher();
        var sockets = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false };
        return (new HttpClient(UpstreamRequestsHandler.Around(sockets, watcher)) { Timeout = TimeSpan.FromSeconds(30) }, watcher);
    }

    [TestMethod]
    public async Task OverARealConnectionEveryBodyIsCountedAndTheConnectionIsUsedAgain()
    {
        using var server = new Server();
        var (client, watcher) = RealClient();
        using var _c = client;

        Assert.AreEqual("hello world", await client.GetStringAsync(server.Address + "/plain"));
        Assert.AreEqual((11L, "200 OK", true), (watcher.Requests[^1].Bytes, watcher.Requests[^1].Answer, watcher.Requests[^1].IsDone));

        Assert.AreEqual("hello world", await client.GetStringAsync(server.Address + "/chunked"));
        Assert.AreEqual((11L, true), (watcher.Requests[^1].Bytes, watcher.Requests[^1].IsDone), "a length nobody said");

        Assert.AreEqual("hello from far away", await client.GetStringAsync(server.Address + "/gzip"));
        Assert.AreEqual((19L, true), (watcher.Requests[^1].Bytes, watcher.Requests[^1].IsDone), "as it came out of the decompression, which is below");

        using (var response = await client.GetAsync(server.Address + "/plain", HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.IsFalse(watcher.Requests[^1].IsDone);
            Assert.AreEqual(11, response.Content.Headers.ContentLength);
            Assert.AreEqual("text/plain", response.Content.Headers.ContentType?.MediaType);
            await using var body = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(body);
            Assert.AreEqual("hello world", await reader.ReadToEndAsync());
            Assert.IsTrue(watcher.Requests[^1].IsDone);
        }

        using (var refused = await client.GetAsync(server.Address + "/missing"))
        {
            Assert.AreEqual("gone", await refused.Content.ReadAsStringAsync());
        }

        Assert.AreEqual(("404 Not Found", true, 4L, true), (watcher.Requests[^1].Answer, watcher.Requests[^1].Refused, watcher.Requests[^1].Bytes, watcher.Requests[^1].IsDone));
        Assert.AreEqual(5, watcher.Requests.Count);
        Assert.AreEqual(0, watcher.Running.Count);
        Assert.AreEqual(1, server.Connections, "each answer read to its end gave its connection back for the next");
    }

    [TestMethod]
    public async Task OverARealConnectionWhatHangsIsGivenUpOnAndWhatBreaksOffFails()
    {
        using var server = new Server();
        var (client, watcher) = RealClient();
        using var _c = client;
        using (var gaveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => client.GetAsync(server.Address + "/hang", gaveUp.Token));
        }

        Assert.IsInstanceOfType<OperationCanceledException>(watcher.Requests[^1].Error);
        Assert.IsTrue(watcher.Requests[^1].IsDone);
        Assert.IsNull(watcher.Requests[^1].Answer, "the server said nothing");

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => client.GetStringAsync(server.Address + "/half"));
        Assert.AreEqual("200 OK", watcher.Requests[^1].Answer, "it began well");
        Assert.IsNotNull(watcher.Requests[^1].Error, "and ended short of what it said");
        Assert.AreEqual(9, watcher.Requests[^1].Bytes);
        Assert.IsTrue(watcher.Requests[^1].IsDone);
    }
}
