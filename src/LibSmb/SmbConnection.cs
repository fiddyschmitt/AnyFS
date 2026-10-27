using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using LibSmb.Protocol;
using LibSmb.Security;
using Microsoft.Extensions.Logging;

namespace LibSmb;

/// <summary>
/// One client's TCP connection. Its messages are handled one at a time, in order; their replies go to a writer, in
/// order, so the next request is taken up while the last reply is still going out (a client with credits sends its next
/// requests without waiting: a copy keeps several READs in flight, and writing a big reply takes as long as the wire).
/// </summary>
internal sealed partial class SmbConnection
{
    /// <summary>How many replies may wait for the wire: enough that the reader never stalls on a draining socket, few enough that a client that stops reading holds little memory.</summary>
    private const int OutboundDepth = 8;

    /// <summary>The most opens a connection may hold, so a client that never closes cannot use up the backend's handles.</summary>
    internal const int MaxOpenFiles = 16384;

    private readonly SmbServer _server;
    private readonly Socket? _socket;
    private readonly Channel<Outbound> _outbound = Channel.CreateBounded<Outbound>(new BoundedChannelOptions(OutboundDepth) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _closing = new();
    private readonly ConcurrentDictionary<ulong, Session> _sessions = new();
    private readonly ConcurrentDictionary<ulong, byte[]> _challenges = new();
    private readonly ConcurrentDictionary<uint, Tree> _trees = new();
    private readonly Dictionary<FileId, OpenFile> _opens = [];
    private readonly Lock _opensGate = new();
    private ulong _nextFileId;
    private int _closed;

    public SmbConnection(SmbServer server, Socket? socket)
    {
        _server = server;
        _socket = socket;
        Remote = socket?.RemoteEndPoint?.ToString() ?? "(none)";
    }

    /// <summary>Who is on the other end, for the log.</summary>
    public string Remote { get; }

    /// <summary>The dialect negotiated; 0 before NEGOTIATE.</summary>
    internal ushort Dialect { get; set; }

    /// <summary>Whether the client negotiated Apple's ReadDirAttr: its directory entries carry Apple's layout.</summary>
    internal bool AppleReadDirAttr { get; set; }

    private ILogger Logger => _server.Logger;

    /// <summary>Serves the connection until the client goes, the server stops or the connection breaks.</summary>
    public async Task RunAsync(CancellationToken serverStopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverStopping, _closing.Token);
        var ct = linked.Token;
        var socket = _socket ?? throw new InvalidOperationException("no socket");
        socket.NoDelay = true;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        Logger.LogDebug("smb: connection from {Remote}", Remote);
        var stream = new NetworkStream(socket, ownsSocket: true);
        var writer = Task.Run(() => WriteAsync(stream, ct), CancellationToken.None);
        try
        {
            var header = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

                // the first byte is zero, then the message's length, 24 bits big-endian
                var length = (header[1] << 16) | (header[2] << 8) | header[3];
                if (length == 0)
                {
                    continue;
                }

                if (length > Smb2.MaxMessageSize)
                {
                    Logger.LogDebug("smb: {Remote} sent a message of {Length} bytes; dropping the connection", Remote, length);
                    return;
                }

                var message = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    await stream.ReadExactlyAsync(message.AsMemory(0, length), ct).ConfigureAwait(false);
                    var reply = await ProcessAsync(message.AsMemory(0, length), ct).ConfigureAwait(false);
                    if (reply is not null)
                    {
                        await _outbound.Writer.WriteAsync(reply, ct).ConfigureAwait(false);
                    }
                }
                catch (InvalidDataException ex)
                {
                    Logger.LogDebug("smb: {Remote}: {Error}; dropping the connection", Remote, ex.Message);
                    return;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(message);
                }
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or SocketException or OperationCanceledException or ObjectDisposedException or ChannelClosedException)
        {
            Logger.LogDebug("smb: connection from {Remote} closed: {Reason}", Remote, ex.Message);
        }
        catch (Exception ex)
        {
            // a fault in one request's handling must not take the server down: this connection alone goes
            Logger.LogError(ex, "smb: serving {Remote} failed; dropping the connection", Remote);
        }
        finally
        {
            _outbound.Writer.TryComplete();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }

            await CloseOpensAsync(_ => true).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            DisposeSessions();
            Interlocked.Exchange(ref _closed, 1);
        }
    }

    /// <summary>Ends the connection (the server is stopping).</summary>
    public Task CloseAsync()
    {
        _closing.Cancel();
        try
        {
            _socket?.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }

        return Task.CompletedTask;
    }

    /// <summary>A share that is no longer served: its opens on this connection are closed.</summary>
    public Task ForgetShareAsync(SmbShare share) => CloseOpensAsync(o => ReferenceEquals(o.Share, share));

    /// <summary>
    /// Handles one message: a single command, or a compound chain of them ([MS-SMB2] 3.3.5.2.7), answered as one compound
    /// reply. Null when nothing is to be sent back (a CANCEL).
    /// </summary>
    internal async ValueTask<Outbound?> ProcessAsync(Memory<byte> message, CancellationToken ct)
    {
        // The Windows redirector may open with a legacy SMB1 negotiate offering SMB2 dialects: an SMB2 negotiate reply
        // with the wildcard dialect makes it negotiate again in SMB2.
        if (Smb2.IsSmb1Negotiate(message.Span))
        {
            return Outbound.From(Smb1NegotiateReply());
        }

        if (!Header.TryParse(message.Span, out _))
        {
            throw new InvalidDataException($"not an SMB2 message ({message.Length} bytes)");
        }

        var replies = new List<(Reply Reply, Signer? Signer)>();
        var chain = new Chain();
        try
        {
            for (var offset = 0; offset + Smb2.HeaderSize <= message.Length;)
            {
                if (!Header.TryParse(message.Span[offset..], out var header))
                {
                    break;
                }

                var next = (int)header.NextCommand;
                var pdu = next >= Smb2.HeaderSize && offset + next <= message.Length ? message.Slice(offset, next) : message[offset..];
                var handled = await HandleCommandAsync(header, pdu, chain, ct).ConfigureAwait(false);
                if (handled.Reply is not null)
                {
                    replies.Add((handled.Reply, handled.Signer));
                }

                if (next < Smb2.HeaderSize)
                {
                    break;
                }

                offset += next;
            }

            return Assemble(replies);
        }
        finally
        {
            foreach (var (reply, _) in replies)
            {
                reply.Dispose();
            }
        }
    }

    /// <summary>
    /// The replies as the message to send: one alone, from its own buffer (the big one, a 1 MiB READ, is not copied); a
    /// chain laid out 8-aligned with each NextCommand set. Each PDU is signed with its session's key, padding included.
    /// </summary>
    private static Outbound? Assemble(List<(Reply Reply, Signer? Signer)> replies)
    {
        if (replies.Count == 0)
        {
            return null;
        }

        if (replies.Count == 1)
        {
            var (reply, signer) = replies[0];
            signer?.Sign(reply.Pdu);
            replies.Clear();
            return Outbound.From(reply);
        }

        var total = 0;
        for (var i = 0; i < replies.Count; i++)
        {
            total += i < replies.Count - 1 ? Wire.RoundUp(replies[i].Reply.PduLength, 8) : replies[i].Reply.PduLength;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Reply.Headroom + total);
        var message = buffer.AsSpan(Reply.Headroom, total);
        message.Clear();
        var at = 0;
        for (var i = 0; i < replies.Count; i++)
        {
            var (reply, signer) = replies[i];
            var span = i < replies.Count - 1 ? Wire.RoundUp(reply.PduLength, 8) : reply.PduLength;
            reply.Pdu.CopyTo(message[at..]);
            var pdu = message.Slice(at, span);
            if (i < replies.Count - 1)
            {
                Wire.Put32(pdu, 20, (uint)span); // NextCommand
            }

            signer?.Sign(pdu);
            at += span;
        }

        return Outbound.FromBuffer(buffer, total);
    }

    private async Task WriteAsync(NetworkStream stream, CancellationToken ct)
    {
        await foreach (var outbound in _outbound.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            using (outbound)
            {
                try
                {
                    await stream.WriteAsync(outbound.Frame, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // the peer is gone: end the reader too, and keep draining so it never blocks on a full channel
                    _closing.Cancel();
                    while (_outbound.Reader.TryRead(out var rest))
                    {
                        rest.Dispose();
                    }

                    throw;
                }
            }
        }
    }

    // ----- the handle table -----

    internal FileId AddOpen(Func<FileId, OpenFile> create)
    {
        lock (_opensGate)
        {
            var id = new FileId(++_nextFileId, _nextFileId);
            var open = create(id);
            _opens[id] = open;
            return id;
        }
    }

    internal int OpenCount
    {
        get
        {
            lock (_opensGate)
            {
                return _opens.Count;
            }
        }
    }

    /// <summary>An open by its FileId, as long as its share is still served.</summary>
    internal OpenFile? FindOpen(FileId id)
    {
        lock (_opensGate)
        {
            return _opens.TryGetValue(id, out var open) ? open : null;
        }
    }

    internal OpenFile? RemoveOpen(FileId id)
    {
        lock (_opensGate)
        {
            return _opens.Remove(id, out var open) ? open : null;
        }
    }

    private async Task CloseOpensAsync(Func<OpenFile, bool> which)
    {
        List<OpenFile> closing;
        lock (_opensGate)
        {
            closing = [.. _opens.Values.Where(which)];
            foreach (var open in closing)
            {
                _opens.Remove(open.Id);
            }
        }

        foreach (var open in closing)
        {
            if (open.File is { } file)
            {
                try
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("smb: closing {Path} of {Share} for {Remote} failed: {Error}", open.Path, open.Share.Name, Remote, ex.Message);
                }
            }
        }
    }

    // ----- sessions and trees -----

    private void DisposeSessions()
    {
        foreach (var session in _sessions.Values)
        {
            session.Signer?.Dispose();
        }

        _sessions.Clear();
    }

    private Signer? SignerFor(ulong sessionId) => _sessions.TryGetValue(sessionId, out var session) ? session.Signer : null;

    /// <summary>A session taken as signed in, with a signing key from <paramref name="sessionKey"/> when given (for the tests).</summary>
    internal Signer? AddSessionForTests(ulong id, byte[]? sessionKey = null)
    {
        var signer = sessionKey is null ? null : new Signer(sessionKey, Dialect);
        _sessions[id] = new Session(id, sessionKey is null, signer);
        return signer;
    }

    /// <summary>A session that finished signing in, and its signing key (none for a guest).</summary>
    internal sealed class Session(ulong id, bool guest, Signer? signer)
    {
        public ulong Id { get; } = id;

        public bool Guest { get; } = guest;

        public Signer? Signer { get; } = signer;
    }

    /// <summary>A tree connect: a disk share, or IPC$ (<see cref="Share"/> null).</summary>
    internal sealed record Tree(uint Id, SmbShare? Share);

    /// <summary>What the commands of a compound chain share: the FileId a CREATE made and the session and tree to report.</summary>
    internal sealed class Chain
    {
        public FileId? FileId { get; set; }

        public ulong SessionId { get; set; }

        public uint TreeId { get; set; }

        /// <summary>The error the chain's last command failed with, which a related command needing its FileId inherits.</summary>
        public uint? Failed { get; set; }
    }
}
