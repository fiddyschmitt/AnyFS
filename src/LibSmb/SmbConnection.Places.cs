using LibSmb.Protocol;

namespace LibSmb;

internal sealed partial class SmbConnection
{
    /// <summary>What a request in the line counts for beside its message's bytes: what the connection keeps of it.</summary>
    private const int WaitingOverhead = 1024;

    // The line: what the requests read and not worked on hold together, and the reader waiting for it to shorten.
    private readonly Lock _lineGate = new();
    private long _waiting;
    private TaskCompletionSource? _room;

    /// <summary>What the requests read and not worked on hold now, in bytes (for the tests).</summary>
    internal long WaitingBytes
    {
        get
        {
            lock (_lineGate)
            {
                return _waiting;
            }
        }
    }

    /// <summary>
    /// Whether a command calls the file system, and so takes a place among the requests worked on at once
    /// (<see cref="SmbServerOptions.MaxConcurrentRequests"/>), can wait there, and can be cancelled. The others (an ECHO, a
    /// TREE_CONNECT, an IOCTL) are answered as they come, whatever the file system is slow with.
    /// </summary>
    private static bool ReachesFileSystem(Command command)
        => command is Command.Create or Command.Close or Command.Read or Command.Write or Command.Flush or Command.QueryDirectory or Command.QueryInfo or Command.SetInfo;

    /// <summary>Completes when the line holds less than <see cref="SmbServerOptions.WaitingBytes"/>: at once, as a rule.</summary>
    private Task RoomAsync()
    {
        lock (_lineGate)
        {
            return _waiting < _server.Options.WaitingBytes
                ? Task.CompletedTask
                : (_room ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    /// <summary>A request joins the line (<paramref name="bytes"/> above nought) or leaves it; the reader is let on once there is room.</summary>
    private void Line(long bytes)
    {
        TaskCompletionSource? room = null;
        lock (_lineGate)
        {
            _waiting += bytes;
            if (_room is not null && _waiting < _server.Options.WaitingBytes)
            {
                room = _room;
                _room = null;
            }
        }

        room?.TrySetResult();
    }

    /// <summary>
    /// Waits for an open's turn (<see cref="OpenFile.Gate"/>), which the caller releases. A request that has to wait gives
    /// its place back meanwhile and takes one again with the turn in hand: waiting is not work, and the reads a client
    /// has in flight on a few slow files would otherwise take every place, with nothing left for anything else. No request
    /// holds a place while it waits for a turn, so those that hold one always get on.
    /// </summary>
    private async ValueTask TurnAsync(OpenFile open, Place? place, CancellationToken ct)
    {
        if (open.Gate.Wait(0, CancellationToken.None))
        {
            return;
        }

        var had = place?.GiveBack() ?? false;
        await open.Gate.WaitAsync(ct).ConfigureAwait(false);
        if (had)
        {
            try
            {
                await place!.TakeAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                open.Gate.Release();
                throw;
            }
        }
    }

    /// <summary>
    /// Waits for an open's turn (<see cref="TurnAsync"/>), which the caller releases after a true: false, holding no
    /// turn, when the open was closed while the request waited.
    /// </summary>
    private async ValueTask<bool> EnterAsync(OpenFile open, Place? place, CancellationToken ct)
    {
        await TurnAsync(open, place, ct).ConfigureAwait(false);
        if (!open.Closed)
        {
            return true;
        }

        open.Gate.Release();
        return false;
    }

    /// <summary>
    /// A request's place among those its connection works on at once
    /// (<see cref="SmbServerOptions.MaxConcurrentRequests"/>): taken before its first call of the file system, given back
    /// when it is done, and while it waits for its open's turn (<see cref="TurnAsync"/>). Without a place the request is
    /// in the line, from the moment the reader has read it, so the reader knows at once what the line holds. Made by the
    /// reader, then used by its request alone.
    /// </summary>
    internal sealed class Place
    {
        private readonly SmbConnection _connection;
        private readonly long _bytes;
        private bool _held;
        private bool _inLine;

        public Place(SmbConnection connection, int messageLength)
        {
            _connection = connection;
            _bytes = messageLength + WaitingOverhead;
            JoinLine();
        }

        /// <summary>Takes a place, waiting in the line until there is one; nothing to do when it has one.</summary>
        public async ValueTask TakeAsync(CancellationToken ct)
        {
            if (_held)
            {
                return;
            }

            var slots = _connection._slots;
            if (!slots.Wait(0, CancellationToken.None))
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
            }

            _held = true;
            LeaveLine();
        }

        /// <summary>Gives the place back to wait for something else, in the line again; false when it had none.</summary>
        public bool GiveBack()
        {
            if (!_held)
            {
                return false;
            }

            _held = false;
            _connection._slots.Release();
            JoinLine();
            return true;
        }

        /// <summary>The request is done: its place is given back, or it leaves the line.</summary>
        public void Done()
        {
            if (_held)
            {
                _held = false;
                _connection._slots.Release();
            }

            LeaveLine();
        }

        private void JoinLine()
        {
            if (!_inLine)
            {
                _inLine = true;
                _connection.Line(_bytes);
            }
        }

        private void LeaveLine()
        {
            if (_inLine)
            {
                _inLine = false;
                _connection.Line(-_bytes);
            }
        }
    }
}
