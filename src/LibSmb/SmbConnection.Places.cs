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
    /// A READ or WRITE of an open asks for its turn on it as the reader reads it, before the next message: one open's reads
    /// and writes are worked on in the order they came. The thread pool starts the requests of a burst in an order of its
    /// own (newest first, as a rule), and a backend that streams reads on from where it stands only in the client's: the
    /// Mac's read-ahead, sixteen READs of 64 KiB in one burst, worked on backwards had a download from SharePoint opened
    /// afresh for every READ. Anything else (a compound chain among them) asks for its turn when it gets there.
    /// <see cref="Cancellable"/> makes a <see cref="PendingRequest"/> for single commands only.
    /// </summary>
    private void AskTurn(ReadOnlySpan<byte> message, PendingRequest? pending, Place place)
    {
        if (pending?.Header is not { Command: Command.Read or Command.Write, Related: false } || message.Length < Smb2.HeaderSize + 32)
        {
            return;
        }

        // the FileId of both, 16 bytes into the body
        if (FindOpen(FileId.Read(message[(Smb2.HeaderSize + 16)..])) is { IsDirectory: false } open)
        {
            place.AskTurn(open);
        }
    }

    /// <summary>
    /// Waits for an open's turn (<see cref="OpenFile.Gate"/>), which the caller releases: the one its request asked for as
    /// it was read (<see cref="AskTurn"/>), else one asked for now. A request that has to wait gives its place back
    /// meanwhile and takes one again with the turn in hand: waiting is not work, and the reads a client has in flight on a
    /// few slow files would otherwise take every place, with nothing left for anything else. No request holds a place
    /// while it waits for a turn, so those that hold one always get on.
    /// </summary>
    private async ValueTask TurnAsync(OpenFile open, Place? place, CancellationToken ct)
    {
        var asked = place?.TakeTurn(open);
        if (asked is null ? open.Gate.Wait(0, CancellationToken.None) : asked.IsCompletedSuccessfully)
        {
            return;
        }

        var had = place?.GiveBack() ?? false;
        if (asked is null)
        {
            await open.Gate.WaitAsync(ct).ConfigureAwait(false);
        }
        else
        {
            try
            {
                await asked.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // given up while it waited: its turn goes to the next when it comes
                HandOn(asked, open.Gate);
                throw;
            }
        }

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

    /// <summary>A turn asked for and not to be used goes to the next request when it comes: at once when it came already.</summary>
    private static void HandOn(Task turn, SemaphoreSlim gate)
    {
        if (turn.IsCompleted)
        {
            if (turn.IsCompletedSuccessfully)
            {
                gate.Release();
            }

            return;
        }

        _ = turn.ContinueWith(static (t, g) =>
        {
            if (t.IsCompletedSuccessfully)
            {
                ((SemaphoreSlim)g!).Release();
            }
        }, gate, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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

        // the turn on an open its request asked for as it was read, until the request takes it or is done
        private OpenFile? _turnOn;
        private Task? _turn;

        public Place(SmbConnection connection, int messageLength)
        {
            _connection = connection;
            _bytes = messageLength + WaitingOverhead;
            JoinLine();
        }

        /// <summary>Asks for the request's turn on <paramref name="open"/> now, behind the requests on it read before (<see cref="SmbConnection.AskTurn"/>).</summary>
        public void AskTurn(OpenFile open)
        {
            _turnOn = open;
            _turn = open.Gate.WaitAsync(CancellationToken.None);
        }

        /// <summary>The turn on <paramref name="open"/> asked for as the request was read, the request's to wait for from now; null if none was.</summary>
        public Task? TakeTurn(OpenFile open)
        {
            if (_turn is not { } turn || !ReferenceEquals(_turnOn, open))
            {
                return null;
            }

            _turn = null;
            _turnOn = null;
            return turn;
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

        /// <summary>
        /// The request is done: its place is given back, or it leaves the line. A turn it asked for and never took (it
        /// was answered before it needed one: a bad request, a refused session) goes to the next request on the open.
        /// </summary>
        public void Done()
        {
            if (_turn is { } turn)
            {
                HandOn(turn, _turnOn!.Gate);
                _turn = null;
                _turnOn = null;
            }

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
