using System.Runtime.CompilerServices;
using AnyFS.Core.Runtime;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Core.Operations;

/// <summary>
/// The source as its runner hands it out (<see cref="SourceHandle.Current"/>): every call goes through here and is
/// recorded in the <see cref="OperationTracker"/>, so the router, the count walk and anything else that reaches a source
/// through its handle is seen in one place. A file handle is one operation from the open until it is closed, with the
/// bytes that went through it; a listing is one from its first entry to its last.
/// <para>
/// It is also where a person's call stops waiting for an upstream that says nothing (the review of 21-Nov-2026, R6). A
/// stat, a listing or an open for reading that a file manager made waited as long as the source's own retries took: six
/// minutes for a server that accepts a request and never answers, thirteen or fifteen for some, an hour where the server
/// had asked for no requests; and through WinFsp each held one of the volume's threads meanwhile. Such a call is given
/// up, as <see cref="VfsError.Unavailable"/>, once the source has had requests out and nothing back for the source's
/// limit (<see cref="UpstreamSilence"/>): a server its own requests are out to, or, for a call that only waits on what
/// someone else asked, every server with requests out. Not by how long the call itself has taken: a great folder or a
/// great file is long while the server answers all the while. From then until something comes from that server, the
/// calls that would wait on it are given up at once, one in every ten seconds excepted, which is how it is found to
/// answer again; what the source answers from what it keeps is not touched, nor what it asks of another server. Not
/// limited: reading, which has its own minute without a byte; anything
/// that writes (an upload takes what it takes, and a rename given up half way is neither done nor not); and what the
/// source does by itself, its walks and counts, which wait as before.
/// </para>
/// </summary>
public sealed class TrackedSource : ISource
{
    private const string SilentNote = "silent";

    /// <summary>How often a call that waits is looked at, at least.</summary>
    private static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(1);

    private readonly SourceHandle _handle;
    private readonly OperationTracker _tracker;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, UpstreamSilence> _upstreams = new(StringComparer.Ordinal);
    private readonly Func<TimeSpan?> _patience;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <param name="patience">
    /// How long a person's call waits while the source has requests out and nothing comes back, read at each call so
    /// that a change of the setting needs no restart; null, or none given, for as long as the source itself waits.
    /// </param>
    public TrackedSource(ISource inner, SourceHandle handle, OperationTracker tracker, Func<TimeSpan?>? patience = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(tracker);
        Inner = inner;
        _handle = handle;
        _tracker = tracker;
        _time = tracker.Clock;
        _patience = patience ?? (() => null);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The source itself.</summary>
    public ISource Inner { get; }

    /// <summary>The source itself, whether or not it is tracked.</summary>
    public static ISource Unwrap(ISource source) => source is TrackedSource tracked ? tracked.Inner : source;

    public SourceCapabilities Capabilities => Inner.Capabilities;

    public SourceCounters Counters => Inner.Counters;

    /// <summary>Begins an operation of this source, named as the source is now.</summary>
    public Operation Begin(OperationKind kind, string path = "", string? detail = null) => _tracker.Begin(_handle.Id, _handle.Name, kind, path, detail);

    /// <summary>A row for each folder the source goes through in a count of its own, which its calls here do not show (<see cref="SourceCounters.FolderWatcher"/>).</summary>
    public IFolderWatcher CountFolderRows() => new FolderRows(this);

    private sealed class FolderRows(TrackedSource source) : IFolderWatcher
    {
        public IWatchedFolder Entered(string folder) => new FolderRow(source.Begin(OperationKind.CountFolder, folder));
    }

    private sealed class FolderRow(Operation op) : IWatchedFolder
    {
        public void Listed(long entries) => op.AddItems(entries);

        public void Kept() => op.Detail = "kept as it was";

        public void Failed(Exception error, CancellationToken ct) => op.Fail(error, ct);

        public void Done() => op.Complete();
    }

    /// <summary>A row for each thing the source asks of what is behind it, its server or its folders, for its whole life, whoever or whatever it is for (<see cref="ISourceContext.UpstreamWatcher"/>).</summary>
    public IUpstreamWatcher UpstreamRows() => new RequestRows(this);

    private sealed class RequestRows(TrackedSource source) : IUpstreamWatcher
    {
        public IWatchedRequest Asked(string what, string target)
        {
            // the person's call this request is made for, where it is made on that call's own thread of work
            var call = UpstreamPatience.Call as CallWatch;
            if (!ReferenceEquals(call?.Source, source))
            {
                call = null;
            }

            var upstream = source._upstreams.GetOrAdd(UpstreamOf(target), static (name, time) => new UpstreamSilence(name, time), source._time);
            if (call is not null && upstream.IsSilent && !call.MayWaitOn(upstream))
            {
                // A person's request to a server taken for silent is refused here, before it is sent: it would be given up
                // a moment later anyway, and sent it would be one more request for a server that is down to hold, each
                // taking one of the source's few places until its own time was up. What a walk asks is sent as ever.
                throw source.SaidNothing(upstream);
            }

            return new RequestRow(
                source._tracker.Begin(source._handle.Id, source._handle.Name, OperationKind.Upstream, target, what: what),
                source,
                upstream,
                call);
        }
    }

    /// <summary>
    /// The server an address names, by which the requests to it are told from those to another; empty for anything that
    /// is no address (a path, a mail file), a source's one upstream.
    /// </summary>
    internal static string UpstreamOf(string target)
    {
        var scheme = target.IndexOf("://", StringComparison.Ordinal);
        if (scheme <= 0 || target.AsSpan(0, scheme).ContainsAny(" /\\"))
        {
            return "";
        }

        var from = scheme + 3;
        var end = target.AsSpan(from).IndexOfAny("/?# ");
        return (end < 0 ? target[from..] : target.Substring(from, end)).ToLowerInvariant();
    }

    /// <summary>
    /// One request, as a row and as what its upstream's silence is told by: out from here until it is done or has
    /// failed, and heard from with each answer, each run of bytes or entries, and its end where that is no failure.
    /// </summary>
    private sealed class RequestRow : IWatchedRequest
    {
        private readonly Operation _op;
        private readonly TrackedSource _source;
        private readonly UpstreamSilence _upstream;
        private readonly CallWatch? _call;
        private int _failed;
        private int _over;

        public RequestRow(Operation op, TrackedSource source, UpstreamSilence upstream, CallWatch? call)
        {
            _op = op;
            _source = source;
            _upstream = upstream;
            _call = call;
            upstream.Began();
            call?.Began(upstream);
        }

        public void AddBytes(long count)
        {
            _op.AddBytes(count);
            _source.Heard(_upstream);
        }

        // what a request takes with it is no word from the upstream: every request sent would end its silence
        public void Sent(long count) => _op.AddBytes(count);

        public void AddEntries(long count)
        {
            _op.AddItems(count);
            _source.Heard(_upstream);
        }

        public void Answered(string answer, bool refused)
        {
            if (refused)
            {
                _op.Note(answer);
            }
            else
            {
                _op.Answer = answer;
            }

            // a refusal is an answer too: the upstream is there
            _source.Heard(_upstream);
        }

        public void Failed(Exception error, CancellationToken ct)
        {
            Volatile.Write(ref _failed, 1);
            _op.Fail(error, ct);
            Over();
        }

        public void Done()
        {
            if (Volatile.Read(ref _failed) == 0 && Volatile.Read(ref _over) == 0)
            {
                _source.Heard(_upstream);
            }

            _op.Complete();
            Over();
        }

        private void Over()
        {
            if (Interlocked.Exchange(ref _over, 1) == 0)
            {
                _upstream.Ended();
                _call?.Ended(_upstream);
            }
        }
    }

    /// <summary>
    /// One call a person's file manager made, while it runs: the upstreams its own requests are out to, which is what it
    /// waits on. A request made on the call's own thread of work is its own; one that someone else began and the call
    /// only joined (a listing a walk is fetching) is not, and neither is anything begun by the source by itself.
    /// </summary>
    private sealed class CallWatch(TrackedSource source, TimeSpan limit) : UpstreamPatience.Waiting(limit)
    {
        // Most calls are answered from what is kept and ask nothing: one is made for each, on the path a search takes for
        // every file, so it holds nothing until the call asks its upstream, and is its own lock.
        private readonly Lock _gate = new();
        private List<UpstreamSilence>? _out;
        private Dictionary<UpstreamSilence, bool>? _mayWait;
        private bool _asked;

        public TrackedSource Source => source;

        public void Began(UpstreamSilence upstream)
        {
            lock (_gate)
            {
                _asked = true;
                (_out ??= []).Add(upstream);
            }
        }

        public void Ended(UpstreamSilence upstream)
        {
            lock (_gate)
            {
                _out?.Remove(upstream);
            }
        }

        /// <summary>
        /// How long the call may go on waiting before it is looked at again, having waited <paramref name="waited"/>;
        /// zero or less when it is to be given up, with the upstream it is given up for.
        /// </summary>
        public TimeSpan Look(TimeSpan waited, TimeSpan limit, out UpstreamSilence? silent)
        {
            silent = null;
            UpstreamSilence[] mine;
            bool asked;
            lock (_gate)
            {
                mine = _out is null ? [] : [.. _out.Distinct()];
                asked = _asked;
            }

            if (mine.Length > 0)
            {
                // given up for the first of its own upstreams that is silent: its request there is what it waits for
                var soonest = TimeSpan.MaxValue;
                foreach (var upstream in mine)
                {
                    var left = Left(upstream, waited, limit);
                    if (left < soonest)
                    {
                        soonest = left;
                        silent = upstream;
                    }
                }

                return soonest;
            }

            if (asked)
            {
                // between requests of its own (a turn waited for, work of its own): nothing of its own is out to be silent
                return limit;
            }

            // Nothing of its own yet: it waits on what someone else asked (a listing a walk began, which it joined), or on
            // no request at all. Given up only when every upstream with requests out is silent: one that answers may be
            // the one it waits on.
            var latest = TimeSpan.MinValue;
            foreach (var upstream in source._upstreams.Values)
            {
                if (upstream.SilentFor() is null)
                {
                    continue;
                }

                var left = Left(upstream, waited, limit);
                if (left > latest)
                {
                    latest = left;
                    silent = upstream;
                }
            }

            return silent is null ? limit : latest;
        }

        private TimeSpan Left(UpstreamSilence upstream, TimeSpan waited, TimeSpan limit)
        {
            if (upstream.IsSilent && !MayWaitOn(upstream))
            {
                return UpstreamSilence.Grace - waited;
            }

            // silent for as long as nothing has come while this call waited: what was quiet before it began is not held against it
            var quiet = upstream.SilentFor() is { } nothing ? (nothing < waited ? nothing : waited) : TimeSpan.Zero;
            return limit - quiet;
        }

        /// <summary>Asked of an upstream once, when the call first finds it silent: whether this is the one call let wait it out.</summary>
        public bool MayWaitOn(UpstreamSilence upstream)
        {
            lock (_gate)
            {
                _mayWait ??= [];
                if (!_mayWait.TryGetValue(upstream, out var may))
                {
                    _mayWait[upstream] = may = upstream.MayWait();
                }

                return may;
            }
        }
    }

    /// <summary>Something has come back from an upstream: it is no longer taken for silent, if it was.</summary>
    private void Heard(UpstreamSilence upstream)
    {
        if (upstream.Arrived())
        {
            _handle.ClearNotes(NoteOf(upstream));
            _logger.LogInformation("source {Source}: {Upstream} answers again; nothing had come from it since {Since:HH:mm:ss}", _handle.Name, Named(upstream), upstream.SilentSince.ToLocalTime());
        }
    }

    private static string Named(UpstreamSilence upstream) => upstream.Name.Length == 0 ? "its server" : upstream.Name;

    private static string NoteOf(UpstreamSilence upstream) => $"{SilentNote}:{upstream.Name}|";

    /// <summary>
    /// Runs a call a person's file manager made under the source's limit; a walk's, and any call where there is no
    /// limit, as it is. The call sees the limit too (<see cref="UpstreamPatience"/>), for a wait it would make before
    /// sending anything.
    /// </summary>
    private async Task<T> PatientlyAsync<T>(Func<CancellationToken, Task<T>> ask, CancellationToken ct)
    {
        if (Walks.Current || _patience() is not { } limit)
        {
            return await ask(ct).ConfigureAwait(false);
        }

        // for this call and what it starts: an async method's own, and the caller's again when it returns
        var call = new CallWatch(this, limit);
        UpstreamPatience.Put(call);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = ask(stop.Token);
        if (!task.IsCompleted && await GivenUpAsync(task, call, limit, stop).ConfigureAwait(false) is { } silent)
        {
            LeftBehind(task);
            throw silent;
        }

        return await task.ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a person's call that is under way: null once it has ended, or what to tell the person once an upstream
    /// it waits on has had requests out and nothing back for the limit, the call stopped. While that upstream is taken
    /// for silent the wait is <see cref="UpstreamSilence.Grace"/>, unless this is the call let wait it out.
    /// </summary>
    private async Task<VfsException?> GivenUpAsync(Task task, CallWatch call, TimeSpan limit, CancellationTokenSource stop)
    {
        var began = _time.GetTimestamp();
        while (true)
        {
            var waited = _time.GetElapsedTime(began);
            var left = call.Look(waited, limit, out var silent);
            if (left <= TimeSpan.Zero)
            {
                return GiveUp(silent!, call, limit, waited, stop);
            }

            try
            {
                // looked at again before long whatever is left: the call may ask a server found silent meanwhile
                await task.WaitAsync(left < LookEvery ? left : LookEvery, _time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (Exception) when (task.IsCompleted)
            {
                // the call's own failure: its caller's to see, from the task
            }

            if (task.IsCompleted)
            {
                return null;
            }
        }
    }

    private VfsException GiveUp(UpstreamSilence upstream, CallWatch call, TimeSpan limit, TimeSpan waited, CancellationTokenSource stop)
    {
        var first = upstream.Find(out var requestsOut, out var silentFor);

        // said first: what the cancelling ends (the call's wait on a fetch it shares) is to see why it ends
        call.GiveUp();
        stop.Cancel();
        var since = upstream.SilentSince.ToLocalTime();
        var tried = $"tried again every {UpstreamSilence.ProbeEvery.TotalSeconds:N0} s";
        if (first)
        {
            _handle.AddNote(NoteOf(upstream), $"{Named(upstream)} has sent nothing since {since:HH:mm:ss}; what is listed still answers, and it is {tried}");
            _logger.LogWarning(
                "source {Source}: nothing has come from {Upstream} for {Silent:N0} s with {Out} of the source's requests out to it (the limit is {Limit:N0} s); a call that had waited {Waited:N0} s is given up, and until something comes the calls that would wait on it are given up at once, one tried every {Probe:N0} s",
                _handle.Name, Named(upstream), silentFor.TotalSeconds, requestsOut, limit.TotalSeconds, waited.TotalSeconds, UpstreamSilence.ProbeEvery.TotalSeconds);
        }
        else
        {
            _logger.LogDebug("source {Source}: a call given up after {Waited:N1} s: nothing has come from {Upstream} since {Since:HH:mm:ss}", _handle.Name, waited.TotalSeconds, Named(upstream), since);
        }

        return SaidNothing(upstream);
    }

    /// <summary>What a person is told of a call given up, or a request not sent, for a server that says nothing.</summary>
    private VfsException SaidNothing(UpstreamSilence upstream)
        => VfsException.Unavailable($"{_handle.Name}: {Named(upstream)} has sent nothing since {upstream.SilentSince.ToLocalTime():HH:mm:ss}, and AnyFS has stopped waiting for it; what is already listed still answers, and it is tried again every {UpstreamSilence.ProbeEvery.TotalSeconds:N0} s");

    /// <summary>A call given up on goes on until it notices: what it ends with is looked at, so that no failure is left unseen, and a file it opened after all is closed.</summary>
    private static void LeftBehind<T>(Task<T> task)
        => _ = task.ContinueWith(
            static async ended =>
            {
                if (!ended.IsCompletedSuccessfully)
                {
                    _ = ended.Exception;
                    return;
                }

                if (ended.Result is IAsyncDisposable opened)
                {
                    try
                    {
                        await opened.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // nobody is left to tell
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public Task InitialiseAsync(ISourceContext context, CancellationToken ct)
        => TrackAsync(Begin(OperationKind.Start), () => Inner.InitialiseAsync(context, ct), ct);

    public async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        var op = Begin(OperationKind.Stat, path);
        try
        {
            VfsEntry? entry;
            if (Walks.Current || _patience() is not { } limit)
            {
                entry = await Inner.StatAsync(path, ct).ConfigureAwait(false);
            }
            else
            {
                // as PatientlyAsync has a call, written out: a stat is the call a search makes most, and so it makes no closure
                var call = new CallWatch(this, limit);
                UpstreamPatience.Put(call);
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var stat = Inner.StatAsync(path, stop.Token);
                if (!stat.IsCompleted && await GivenUpAsync(stat, call, limit, stop).ConfigureAwait(false) is { } silent)
                {
                    LeftBehind(stat);
                    throw silent;
                }

                entry = await stat.ConfigureAwait(false);
            }

            if (entry is null)
            {
                op.Detail = "not found";
            }

            op.Complete();
            return entry;
        }
        catch (Exception ex)
        {
            op.Fail(ex, ct);
            throw;
        }
    }

    public async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var op = Begin(OperationKind.List, path);
        var limit = Walks.Current ? null : _patience();
        using var stop = limit is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct);
        var listing = stop?.Token ?? ct;

        var call = limit is null ? null : new CallWatch(this, limit.Value);

        // whether the next step is run as the call's own: the first, where a listing asks what it asks, and any after one that waited
        var asks = true;

        // a step of the listing that was given up on: the enumerator is still in it, and is closed when the step ends
        Task<bool>? leftIn = null;
        Action<Task<bool>> givenUp = step => leftIn = step;
        IAsyncEnumerator<VfsEntry> enumerator;
        try
        {
            enumerator = Inner.ListAsync(path, listing).GetAsyncEnumerator(listing);
        }
        catch (Exception ex)
        {
            // a source whose ListAsync throws before it gets to an iterator of its own: left out, the row would run forever
            op.Fail(ex, ct);
            throw;
        }

        try
        {
            while (true)
            {
                bool more;
                try
                {
                    if (limit is not { } patience)
                    {
                        more = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    else if (asks)
                    {
                        more = await NextAsync(enumerator, call!, patience, stop!, givenUp).ConfigureAwait(false);
                        asks = false;
                    }
                    else
                    {
                        // An entry of a listing already had: handed over as it is. Making each step the call's own costs
                        // a hundred bytes an entry, on the path a search takes for every file. A step that waits after
                        // all (a lister that asks for more as it goes) is waited on under the limit, and from then on
                        // each step is the call's own again.
                        var next = enumerator.MoveNextAsync();
                        if (next.IsCompletedSuccessfully)
                        {
                            more = next.Result;
                        }
                        else
                        {
                            asks = true;
                            more = await StepAsync(next.AsTask(), call!, patience, stop!, givenUp).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    op.Fail(ex, ct);
                    throw;
                }

                if (!more)
                {
                    break;
                }

                op.AddItems(1);
                yield return enumerator.Current;
            }

            op.Complete();
        }
        finally
        {
            // the caller stopped before the end (a no-op once the listing finished either way); first, so that a dispose that throws leaves no row running
            op.Cancel("stopped early");
            if (leftIn is null)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                CloseWhenOver(enumerator, leftIn);
            }
        }
    }

    /// <summary>The listing's next step, under the limit as <see cref="PatientlyAsync"/> has a call; a step given up on is handed to <paramref name="givenUp"/>.</summary>
    private async Task<bool> NextAsync(IAsyncEnumerator<VfsEntry> enumerator, CallWatch call, TimeSpan limit, CancellationTokenSource stop, Action<Task<bool>> givenUp)
    {
        // again at each step: what an async method sets is its caller's again when it returns
        UpstreamPatience.Put(call);
        var next = enumerator.MoveNextAsync();
        if (next.IsCompleted)
        {
            return await next.ConfigureAwait(false);
        }

        return await StepAsync(next.AsTask(), call, limit, stop, givenUp).ConfigureAwait(false);
    }

    /// <summary>A step of a listing that is under way, waited on under the limit.</summary>
    private async Task<bool> StepAsync(Task<bool> step, CallWatch call, TimeSpan limit, CancellationTokenSource stop, Action<Task<bool>> givenUp)
    {
        if (await GivenUpAsync(step, call, limit, stop).ConfigureAwait(false) is { } silent)
        {
            givenUp(step);
            throw silent;
        }

        return await step.ConfigureAwait(false);
    }

    /// <summary>An enumerator cannot be closed while a step of it is under way: it is closed once the step given up on has ended.</summary>
    private static void CloseWhenOver(IAsyncEnumerator<VfsEntry> enumerator, Task<bool> step)
        => _ = step.ContinueWith(
            async ended =>
            {
                _ = ended.Exception;
                try
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // nobody is left to tell
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        var op = Begin(mode.IsWrite() ? OperationKind.WriteFile : OperationKind.ReadFile, path);
        IVfsFile file;
        try
        {
            // an open for writing is not limited: it may fetch what is there first, and it begins what a close is to end
            file = mode.IsWrite()
                ? await Inner.OpenAsync(path, mode, ct).ConfigureAwait(false)
                : await PatientlyAsync(stop => Inner.OpenAsync(path, mode, stop), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            op.Fail(ex, ct);
            throw;
        }

        return new TrackedFile(file, op);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
        => TrackAsync(Begin(OperationKind.CreateDirectory, path), () => Inner.CreateDirectoryAsync(path, ct), ct);

    public Task DeleteAsync(string path, CancellationToken ct)
        => TrackAsync(Begin(OperationKind.Delete, path), () => Inner.DeleteAsync(path, ct), ct);

    public Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct)
        => TrackAsync(Begin(OperationKind.Rename, fromPath, "to " + toPath), () => Inner.RenameAsync(fromPath, toPath, replace, ct), ct);

    public Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct)
        => TrackAsync(Begin(OperationKind.SetTimes, path), () => Inner.SetTimesAsync(path, created, modified, ct), ct);

    public Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct)
        => TrackAsync(Begin(OperationKind.GetSpace, path), () => PatientlyAsync(stop => Inner.GetSpaceAsync(path, stop), ct), ct);

    public async ValueTask DisposeAsync()
    {
        var op = Begin(OperationKind.Stop);
        try
        {
            await Inner.DisposeAsync().ConfigureAwait(false);
            op.Complete();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    private static async Task TrackAsync(Operation op, Func<Task> call, CancellationToken ct)
    {
        try
        {
            await call().ConfigureAwait(false);
            op.Complete();
        }
        catch (Exception ex)
        {
            op.Fail(ex, ct);
            throw;
        }
    }

    private static async Task<T> TrackAsync<T>(Operation op, Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            var result = await call().ConfigureAwait(false);
            op.Complete();
            return result;
        }
        catch (Exception ex)
        {
            op.Fail(ex, ct);
            throw;
        }
    }

    /// <summary>The open file: bytes are counted on its operation, a failed call is noted on it, and closing it ends it.</summary>
    private sealed class TrackedFile(IVfsFile inner, Operation op) : IVfsFile
    {
        public long Length => inner.Length;

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            try
            {
                var n = await inner.ReadAsync(offset, buffer, ct).ConfigureAwait(false);
                op.AddBytes(n);
                return n;
            }
            catch (Exception ex)
            {
                op.Note(ex, ct);
                throw;
            }
        }

        public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            try
            {
                await inner.WriteAsync(offset, data, ct).ConfigureAwait(false);
                op.AddBytes(data.Length);
            }
            catch (Exception ex)
            {
                op.Note(ex, ct);
                throw;
            }
        }

        public async ValueTask SetLengthAsync(long length, CancellationToken ct)
        {
            try
            {
                await inner.SetLengthAsync(length, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                op.Note(ex, ct);
                throw;
            }
        }

        public async ValueTask FlushAsync(CancellationToken ct)
        {
            try
            {
                await inner.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                op.Note(ex, ct);
                throw;
            }
        }

        /// <summary>A write given up ends the operation as one its caller gave up, not as one that succeeded.</summary>
        public async ValueTask AbandonAsync()
        {
            try
            {
                await inner.AbandonAsync().ConfigureAwait(false);
                op.Cancel("the write was given up: nothing of it is kept");
            }
            catch (Exception ex)
            {
                op.Fail(ex);
                throw;
            }
        }

        /// <summary>The close is part of the operation: a write-on-close upload that fails ends it Failed.</summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                op.Complete();
            }
            catch (Exception ex)
            {
                op.Fail(ex);
                throw;
            }
        }
    }
}
