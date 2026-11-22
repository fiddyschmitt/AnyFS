using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>
/// The DevTools connection the web sessions share, over a connection in memory with the test at the browser's end: what
/// the browser sends reaches the client, and the client learns at once that the browser has gone. No browser is launched.
/// </summary>
[TestClass]
public class DevToolsClientTests
{
    // far longer than anything here takes: only a wait that would not end runs into it
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>A client, and the browser's end of its connection.</summary>
    private static (DevToolsClient Client, WebSocket Browser) Connect()
    {
        var toBrowser = new Pipe();
        var toClient = new Pipe();
        var client = WebSocket.CreateFromStream(new End(toClient.Reader, toBrowser.Writer), new WebSocketCreationOptions { IsServer = false });
        var browser = WebSocket.CreateFromStream(new End(toBrowser.Reader, toClient.Writer), new WebSocketCreationOptions { IsServer = true });
        return (new DevToolsClient(client), browser);
    }

    private static Task SayAsync(WebSocket browser, string json) => browser.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonNode> HearAsync(WebSocket browser)
    {
        var buffer = new byte[4096];
        var heard = await browser.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(Patience);
        return JsonNode.Parse(buffer.AsSpan(0, heard.Count))!;
    }

    [TestMethod]
    public async Task AnEventTheBrowserSendsReachesWhoListens()
    {
        var (client, browser) = Connect();
        await using (client)
        using (browser)
        {
            var events = Channel.CreateUnbounded<(string Method, string Parameters, string? Session)>();
            client.Event += (method, parameters, session) => events.Writer.TryWrite((method, parameters.ToJsonString(), session));

            await SayAsync(browser, """{"method":"Target.targetDestroyed","params":{"targetId":"T1"}}""");
            Assert.AreEqual(("Target.targetDestroyed", """{"targetId":"T1"}""", (string?)null), await events.Reader.ReadAsync().AsTask().WaitAsync(Patience), "the browser's own");

            await SayAsync(browser, """{"method":"Network.requestWillBeSent","params":{"requestId":"7"},"sessionId":"S1"}""");
            Assert.AreEqual(("Network.requestWillBeSent", """{"requestId":"7"}""", "S1"), await events.Reader.ReadAsync().AsTask().WaitAsync(Patience), "a page's, with the session it came from");
            Assert.IsFalse(client.Closed.IsCompleted, "the connection is up");
        }
    }

    [TestMethod]
    public async Task ABrowserThatHasGoneIsToldAtOnceToWhoWaits()
    {
        // a browser that quits closes the connection, and one that is killed drops it: either way nothing more will come,
        // which a session that only listens (Outlook's, Teams') learns from nothing else
        foreach (var killed in new[] { false, true })
        {
            var (client, browser) = Connect();
            await using (client)
            using (browser)
            {
                Assert.IsFalse(client.Closed.IsCompleted, "up while the browser is there");
                if (killed)
                {
                    browser.Dispose();
                }
                else
                {
                    await browser.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }

                await client.Closed.WaitAsync(Patience);
                var ex = await Assert.ThrowsExactlyAsync<IOException>(() => client.SendAsync("Target.getTargets"));
                Assert.AreEqual("the DevTools connection closed", ex.Message, "and a command from then on fails without being sent");
            }
        }
    }

    [TestMethod]
    public async Task AClientThatIsDisposedHasEndedItsConnectionToo()
    {
        var (client, browser) = Connect();
        using (browser)
        {
            var closed = client.Closed;
            Assert.IsFalse(closed.IsCompleted);
            await client.DisposeAsync();
            await closed.WaitAsync(Patience);
        }
    }

    [TestMethod]
    public async Task ACommandStillWaitingFailsWhenTheBrowserGoes()
    {
        var (client, browser) = Connect();
        await using (client)
        using (browser)
        {
            var answer = client.SendAsync("Target.getTargets");
            Assert.AreEqual("Target.getTargets", (await HearAsync(browser))["method"]!.GetValue<string>(), "the browser has the command");
            Assert.IsFalse(answer.IsCompleted);

            // it goes without answering: the command fails now, not at its thirty seconds
            browser.Dispose();
            var ex = await Assert.ThrowsExactlyAsync<IOException>(() => answer.WaitAsync(Patience));
            Assert.AreEqual("the DevTools connection closed", ex.Message);
        }
    }

    /// <summary>One end of a connection in memory: it reads what the other end wrote, and its closing is the other end's end of stream.</summary>
    private sealed class End(PipeReader incoming, PipeWriter outgoing) : Stream
    {
        private readonly Stream _from = incoming.AsStream();
        private readonly Stream _to = outgoing.AsStream();

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _from.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _from.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _from.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => _to.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _to.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _to.WriteAsync(buffer, cancellationToken);

        public override void Flush() => _to.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _to.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _from.Dispose();
                _to.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
