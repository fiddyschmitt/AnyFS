using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using LibSmb.Protocol;
using LibSmb.Security;
using Microsoft.Extensions.Logging;

namespace LibSmb;

/// <summary>
/// One client's TCP connection, which carries everything that client machine asks of the server: every program's
/// requests, over every open. So each message (a compound chain whole) is handled on its own, beside the others, and a
/// request whose file system call is slow holds up only itself. Replies go to one writer as they are done, in whatever
/// order: SMB2 matches them to their requests by MessageId. The handshake is handled before the next message is read,
/// one open's requests take turns (<see cref="OpenFile.Gate"/>), and at most
/// <see cref="SmbServerOptions.MaxConcurrentRequests"/> are worked on at once: past that, the next message waits in the
/// socket until one is done.
/// </summary>
internal sealed partial class SmbConnection
{
    /// <summary>How many big replies may wait for the wire: enough that the wire never idles while one is made, few enough that a client that stops reading holds little memory.</summary>
    private const int OutboundDepth = 8;

    /// <summary>How many small replies may wait for the wire (a small reply is a few hundred bytes, a listing at most 64 KiB).</summary>
    private const int UrgentDepth = 64;

    /// <summary>A reply this long or shorter goes out ahead of the big ones (READ data) that wait.</summary>
    private const int UrgentLimit = 64 << 10;

    /// <summary>The most opens a connection may hold, so a client that never closes cannot use up the backend's handles.</summary>
    internal const int MaxOpenFiles = 16384;

    private readonly SmbServer _server;
    private readonly Socket? _socket;

    // Replies wait for the one writer in two lines: the small ones (a listing, an open, a status) go out ahead of the big
    // ones, so a listing is not stuck behind megabytes of other programs' READ data on the same connection.
    private readonly Channel<Outbound> _urgent = Channel.CreateBounded<Outbound>(new BoundedChannelOptions(UrgentDepth) { SingleReader = true, SingleWriter = false });
    private readonly Channel<Outbound> _outbound = Channel.CreateBounded<Outbound>(new BoundedChannelOptions(OutboundDepth) { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _closing = new();
    private readonly ConcurrentDictionary<ulong, Session> _sessions = new();
    private readonly ConcurrentDictionary<ulong, byte[]> _challenges = new();
    private readonly ConcurrentDictionary<uint, Tree> _trees = new();
    private readonly Dictionary<FileId, OpenFile> _opens = [];
    private readonly Lock _opensGate = new();

    /// <summary>One per request worked on at once; the reader takes one before it reads the next message.</summary>
    private readonly SemaphoreSlim _slots;

    /// <summary>The requests a CANCEL can name, by MessageId, and by AsyncId once they went asynchronous.</summary>
    private readonly ConcurrentDictionary<ulong, PendingRequest> _byMessageId = new();
    private readonly ConcurrentDictionary<ulong, PendingRequest> _byAsyncId = new();
    private long _nextAsyncId;

    /// <summary>Completes once the connection is ending and no request is running any more.</summary>
    private readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ulong _nextFileId;
    private int _closed;
    private int _running;
    private bool _ending;

    public SmbConnection(SmbServer server, Socket? socket)
    {
        _server = server;
        _socket = socket;
        _slots = new SemaphoreSlim(server.Options.MaxConcurrentRequests);
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
        var delay = _server.Options.InterimResponseDelay;
        var sweep = delay == Timeout.InfiniteTimeSpan ? Task.CompletedTask : Task.Run(() => SweepAsync(delay, ct), CancellationToken.None);
        try
        {
            var header = new byte[4];
            while (true)
            {
                // a slot before the next message: with every one taken, it waits in the socket until a request is done
                await _slots.WaitAsync(ct).ConfigureAwait(false);
                var handedOver = false;
                try
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
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(message);
                        throw;
                    }

                    if (InTurn(message.AsSpan(0, length)))
                    {
                        // the handshake, before the next message is read: what a client sends after it needs what it set
                        // up; and a CANCEL, at once
                        await ServeAsync(message, length, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // known by its MessageId before the next message is read, so a CANCEL right behind it finds it
                        var pending = Cancellable(message.AsSpan(0, length), ct);
                        Interlocked.Increment(ref _running);
                        handedOver = true;
                        _ = Task.Run(() => ServeBesideAsync(message, length, pending, ct), CancellationToken.None);
                    }
                }
                catch (InvalidDataException ex)
                {
                    Logger.LogDebug("smb: {Remote}: {Error}; dropping the connection", Remote, ex.Message);
                    return;
                }
                finally
                {
                    if (!handedOver)
                    {
                        _slots.Release();
                    }
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
            // the requests still running end first: the client is gone or the server stops, so their file system calls are
            // cancelled and their replies dropped; then the writer, then what was left open
            await _closing.CancelAsync().ConfigureAwait(false);
            foreach (var pending in _byMessageId.Values)
            {
                pending.Cancel();
            }

            Volatile.Write(ref _ending, true);
            if (Volatile.Read(ref _running) == 0)
            {
                _idle.TrySetResult();
            }

            await _idle.Task.ConfigureAwait(false);
            await sweep.ConfigureAwait(false);
            _urgent.Writer.TryComplete();
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
    /// Whether a message is handled before the next is read: the SMB1 negotiate and the handshake (NEGOTIATE,
    /// SESSION_SETUP, LOGOFF), since what a client sends after them depends on what they set up or end, and CANCEL, which
    /// must not wait behind anything. Anything that is not SMB2 too, which ends the connection.
    /// </summary>
    private static bool InTurn(ReadOnlySpan<byte> message)
        => Smb2.IsSmb1Negotiate(message) || !Header.TryParse(message, out var header) || header.Command is Command.Negotiate or Command.SessionSetup or Command.Logoff or Command.Cancel;

    /// <summary>
    /// A request the client can cancel and that may go asynchronous: a single command (not a compound chain) that can wait
    /// on the file system, known by its MessageId until it is answered. Null for anything else.
    /// </summary>
    private PendingRequest? Cancellable(ReadOnlySpan<byte> message, CancellationToken ct)
    {
        if (!Header.TryParse(message, out var h) || h.NextCommand != 0 || h.Command is not (Command.Create or Command.Close or Command.Read or Command.Write or Command.Flush or Command.QueryDirectory or Command.QueryInfo or Command.SetInfo))
        {
            return null;
        }

        var pending = new PendingRequest(h);
        _byMessageId[h.MessageId] = pending;
        if (ct.IsCancellationRequested)
        {
            pending.Cancel();
        }

        return pending;
    }

    /// <summary>
    /// Every so often, the requests still running past <see cref="SmbServerOptions.InterimResponseDelay"/> go asynchronous:
    /// each gets its AsyncId and its interim STATUS_PENDING response. One sweep for the connection, not a timer per request.
    /// </summary>
    private async Task SweepAsync(TimeSpan delay, CancellationToken ct)
    {
        using var ticks = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds / 4, 10, 250)));
        try
        {
            while (await ticks.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = Stopwatch.GetTimestamp();
                foreach (var pending in _byMessageId.Values)
                {
                    if (!pending.IsRunning || Stopwatch.GetElapsedTime(pending.Started, now) < delay || !pending.TryGoAsync((ulong)Interlocked.Increment(ref _nextAsyncId)))
                    {
                        continue;
                    }

                    _byAsyncId[pending.AsyncId] = pending;
                    try
                    {
                        await SendAsync(Interim(pending), ct).ConfigureAwait(false);
                        pending.InterimSent.TrySetResult();
                    }
                    catch
                    {
                        pending.InterimSent.TrySetCanceled(CancellationToken.None);
                        throw;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    /// <summary>
    /// CANCEL ([MS-SMB2] 3.3.5.16): the request it names, by AsyncId once that went asynchronous, else by MessageId, has its
    /// file system call cancelled and is answered STATUS_CANCELLED. One already answered, or unknown, is left alone.
    /// </summary>
    private void Cancel(Header h)
    {
        var target = h.Async ? _byAsyncId.GetValueOrDefault(h.AsyncId) : _byMessageId.GetValueOrDefault(h.MessageId);
        if (target is null)
        {
            return;
        }

        Logger.LogDebug("smb: {Remote} cancelled {Command} message {MessageId}", Remote, target.Header.Command, target.Header.MessageId);
        target.Cancel();
    }

    /// <summary>One message handled and its reply handed to the writer; its buffer goes back to the pool.</summary>
    private async Task ServeAsync(byte[] message, int length, CancellationToken ct)
    {
        Outbound? reply = null;
        try
        {
            reply = await ProcessAsync(message.AsMemory(0, length), ct).ConfigureAwait(false);
            if (reply is not null)
            {
                await SendAsync(reply, ct).ConfigureAwait(false);
                reply = null;
            }
        }
        finally
        {
            reply?.Dispose();
            ArrayPool<byte>.Shared.Return(message);
        }
    }

    /// <summary>A reply handed to the writer: a small one in the line that goes first.</summary>
    private ValueTask SendAsync(Outbound reply, CancellationToken ct)
        => (reply.Frame.Length <= UrgentLimit ? _urgent : _outbound).Writer.WriteAsync(reply, ct);

    /// <summary>
    /// A request that may go asynchronous: still running after <see cref="SmbServerOptions.InterimResponseDelay"/>, it is
    /// answered STATUS_PENDING under a new AsyncId (<see cref="SweepAsync"/>), and its real reply follows under it.
    /// Cancelled by the client's CANCEL, it is answered STATUS_CANCELLED; given up by the file system on its own (a
    /// time-out), STATUS_IO_TIMEOUT: the connection, which carries every other program's requests and opens, carries on.
    /// </summary>
    private async Task ServePendingAsync(byte[] message, int length, PendingRequest pending, CancellationToken ct)
    {
        Outbound? reply = null;
        try
        {
            try
            {
                reply = await ProcessAsync(message.AsMemory(0, length), pending.Cancellation.Token, pending).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                var cancelled = pending.Cancellation.IsCancellationRequested;
                Logger.LogDebug("smb: {Remote}: {Command} message {MessageId} {What}", Remote, pending.Header.Command, pending.Header.MessageId, cancelled ? "cancelled by the client" : "given up by the file system");
                reply = Failed(pending, cancelled ? Status.Cancelled : Status.IoTimeout);
            }

            if (reply is not null)
            {
                // the interim response goes out first
                if (pending.WentAsync)
                {
                    await pending.InterimSent.Task.WaitAsync(ct).ConfigureAwait(false);
                }

                await SendAsync(reply, ct).ConfigureAwait(false);
                reply = null;
            }
        }
        finally
        {
            reply?.Dispose();
            ArrayPool<byte>.Shared.Return(message);
        }
    }

    /// <summary>The interim response of a request gone asynchronous: STATUS_PENDING, its credits, unsigned ([MS-SMB2] 3.3.4.2).</summary>
    private static Outbound Interim(PendingRequest pending)
    {
        var h = pending.Header;
        var reply = Reply.Error();
        h.WriteResponse(reply.Pdu, Status.Pending, h.SessionId, 0, Math.Max(h.CreditRequest, (ushort)1));
        Wire.Put32(reply.Pdu, 16, Wire.U32(reply.Pdu, 16) | (uint)HeaderFlags.AsyncCommand);
        Wire.Put64(reply.Pdu, 32, pending.AsyncId);
        return Outbound.From(reply);
    }

    /// <summary>An error reply to a request whose handling ended without one: asynchronous when it went so, signed when its session signs.</summary>
    private Outbound Failed(PendingRequest pending, uint status)
    {
        var h = pending.Header;
        var reply = Reply.Error();
        h.WriteResponse(reply.Pdu, status, h.SessionId, h.TreeId, Math.Max(h.CreditRequest, (ushort)1));
        if (!pending.TryFinish())
        {
            Header.MakeAsync(reply.Pdu, pending.AsyncId);
        }

        SignerFor(h.SessionId)?.Sign(reply.Pdu);
        return Outbound.From(reply);
    }

    /// <summary>
    /// A message handled beside the others. A failure that would have ended the connection still does (and never the
    /// server), so no request is left unanswered while the connection goes on.
    /// </summary>
    private async Task ServeBesideAsync(byte[] message, int length, PendingRequest? pending, CancellationToken ct)
    {
        try
        {
            if (pending is null)
            {
                await ServeAsync(message, length, ct).ConfigureAwait(false);
            }
            else
            {
                await ServePendingAsync(message, length, pending, ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // the connection is going: there is no one to answer
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or SocketException or OperationCanceledException or ObjectDisposedException or ChannelClosedException)
        {
            Logger.LogDebug("smb: {Remote}: {Error}; dropping the connection", Remote, ex.Message);
            await CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // a fault in one request's handling must not take the server down: this connection alone goes
            Logger.LogError(ex, "smb: serving {Remote} failed; dropping the connection", Remote);
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            if (pending is not null)
            {
                _byMessageId.TryRemove(new KeyValuePair<ulong, PendingRequest>(pending.Header.MessageId, pending));
                if (pending.AsyncId != 0)
                {
                    _byAsyncId.TryRemove(new KeyValuePair<ulong, PendingRequest>(pending.AsyncId, pending));
                }

                pending.Dispose();
            }

            _slots.Release();
            if (Interlocked.Decrement(ref _running) == 0 && Volatile.Read(ref _ending))
            {
                _idle.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Handles one message: a single command, or a compound chain of them ([MS-SMB2] 3.3.5.2.7), answered as one compound
    /// reply. Null when nothing is to be sent back (a CANCEL).
    /// </summary>
    internal async ValueTask<Outbound?> ProcessAsync(Memory<byte> message, CancellationToken ct, PendingRequest? pending = null)
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
                    // a request that went asynchronous meanwhile is answered so, under its AsyncId (signed after)
                    if (pending is not null && !pending.TryFinish())
                    {
                        Header.MakeAsync(handled.Reply.Pdu, pending.AsyncId);
                    }

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

    /// <summary>The one writer: a small reply waiting goes before a big one, and it ends once both lines are completed and empty.</summary>
    private async Task WriteAsync(NetworkStream stream, CancellationToken ct)
    {
        var urgent = _urgent.Reader;
        var bulk = _outbound.Reader;
        while (true)
        {
            if (urgent.TryRead(out var outbound) || bulk.TryRead(out outbound))
            {
                using (outbound)
                {
                    try
                    {
                        await stream.WriteAsync(outbound.Frame, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                    {
                        // the peer is gone: end the reader too, and keep draining so no request blocks on a full line
                        _closing.Cancel();
                        while (urgent.TryRead(out var rest) || bulk.TryRead(out rest))
                        {
                            rest.Dispose();
                        }

                        throw;
                    }
                }

                continue;
            }

            // nothing to write: wait for either line
            var urgentOpen = !urgent.Completion.IsCompleted;
            var bulkOpen = !bulk.Completion.IsCompleted;
            if (!urgentOpen && !bulkOpen)
            {
                return;
            }

            Task waiting = urgentOpen && bulkOpen
                ? Task.WhenAny(urgent.WaitToReadAsync(CancellationToken.None).AsTask(), bulk.WaitToReadAsync(CancellationToken.None).AsTask())
                : (urgentOpen ? urgent : bulk).WaitToReadAsync(CancellationToken.None).AsTask();
            await waiting.ConfigureAwait(false);
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
            // a request still waiting for its turn on it is answered STATUS_FILE_CLOSED
            open.Closed = true;
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
