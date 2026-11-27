using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace AnyFS.Sources.Common;

/// <summary>
/// What a source has asked its service, kept for a while (GitLab, Teams, Jira, Confluence): one request for a thing however
/// many ask for it at once; a thing kept until it is older than the time it was asked with (null: for the session); a
/// failure not kept, so the next asking tries again. An entry's fetch starts only once that entry is the one kept: two
/// askers racing to replace a stale entry (or to add a missing one) do not both go upstream, and the loser's request is
/// never left unobserved.
/// <para>
/// Bounded by what it holds, as well as by how many: past <see cref="MaxEntries"/> entries, or once they weigh more than
/// the bytes it was made with, what is past its time goes, and then what was longest unasked for, down to four fifths of
/// each bound (not the oldest: the list of a server's projects is as old as the run, small, and asked for all the time).
/// It was bounded by the count alone, twenty thousand answers of any size, and an answer past its time went only when it
/// was asked for again: a GitLab's notes and diffs, a Confluence's comments, a wiki read whole, stayed for the run (the
/// review of 21-Nov-2026, P6). An answer weighs half as much again as the bytes the server sent for it
/// (<see cref="Received"/>: what is kept of one is mostly its text, two bytes a character, less the names and the fields
/// not kept; GitLab's notes and diffs and Confluence's comments held 0.74 of twice the bytes), or what it is where it is
/// text or bytes itself, and never less than <see cref="LeastWeight"/>.
/// </para>
/// </summary>
/// <param name="stopping">The source's own: a request shared by several askers is not cancelled by one of them giving up. It is stopped with the source, or when it has given up its place and no one waits for it any more.</param>
/// <param name="maxBytes">What the answers kept may weigh together.</param>
/// <param name="time">The clock an answer's time is told by (a test's own).</param>
public sealed class RequestCache(CancellationToken stopping, long maxBytes = RequestCache.DefaultMaxBytes, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // each asking in the order it came: what a trim tells the longest unasked for by, whatever the clock's grain
    private long _uses;

    public const int MaxEntries = 20_000;

    /// <summary>What a source's answers kept may weigh together, where it says nothing else.</summary>
    public const long DefaultMaxBytes = 64L << 20;

    /// <summary>What an answer weighs at least: its entry, its key, its task.</summary>
    public const long LeastWeight = 512;

    // the bytes received for the fetch this flow is in: set as a fetch starts, added to by whatever reads its answers
    private static readonly AsyncLocal<Tally?> Fetching = new();

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _bytes;
    private int _trimming;

    /// <summary>How many answers are kept, on their way among them.</summary>
    public int Count => _entries.Count;

    /// <summary>What the answers kept weigh together.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>
    /// Says that <paramref name="bytes"/> of an answer were read for whatever fetch is under way here, if one is: what a
    /// source's client calls as it takes an answer whole (<see cref="RequestCore"/> does). Nothing where no fetch
    /// is under way, and nothing for a body its caller reads as a stream: that is a file's, and is not kept.
    /// </summary>
    public static void Received(long bytes)
    {
        if (bytes > 0 && Fetching.Value is { } tally)
        {
            Interlocked.Add(ref tally.Bytes, bytes);
        }
    }

    private sealed class Tally
    {
        public long Bytes;
    }

    private sealed class Entry
    {
        private readonly Lazy<Task<object>> _task;
        private readonly object _gate = new();
        private CancellationTokenSource? _stop;
        private int _waiting;
        private bool _gaveUpItsPlace;

        // what it weighs, once it has answered; whether that is in the cache's total; whether it has left the cache
        private long _weight;
        private bool _counted;
        private bool _removed;

        // which asking of the cache's was the last for it
        private long _used;

        public Entry(RequestCache cache, Func<CancellationToken, Task<object>> start, long at, long use, TimeSpan? ttl, CancellationToken stopping)
        {
            At = at;
            Ttl = ttl;
            _used = use;
            _task = new Lazy<Task<object>>(() => RunAsync(cache, start, stopping), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>An answer that was not fetched through the cache: kept as it is.</summary>
        public Entry(object value, long at, long use, TimeSpan? ttl)
        {
            At = at;
            Ttl = ttl;
            _used = use;
            _task = new Lazy<Task<object>>(System.Threading.Tasks.Task.FromResult(value));
        }

        public Task<object> Task => _task.Value;

        public long At { get; }

        public TimeSpan? Ttl { get; }

        /// <summary>Which asking was the last for it: what a trim lets go by.</summary>
        public long Used => Volatile.Read(ref _used);

        public void Touch(long use) => Volatile.Write(ref _used, use);

        /// <summary>Counts in one who waits for the fetch; false where it has given up its place, and whoever asks fetches anew.</summary>
        public bool TryEnter()
        {
            lock (_gate)
            {
                if (_gaveUpItsPlace)
                {
                    return false;
                }

                _waiting++;
                return true;
            }
        }

        /// <summary>
        /// One who waited has left; with <paramref name="givenUp"/>, given up by the host, and the fetch has given up its
        /// place. A fetch that has, with no one left who waits for it, is stopped: no one can come to wait for it any
        /// more, and the request it has out held one of the source's places to the end of that request's own time, so a
        /// source whose every place was held so answered again only as they timed out (the review of 21-Nov-2026, R6).
        /// </summary>
        public void Leave(bool givenUp)
        {
            CancellationTokenSource? stop = null;
            lock (_gate)
            {
                _gaveUpItsPlace |= givenUp;
                if (--_waiting == 0 && _gaveUpItsPlace)
                {
                    stop = _stop;
                }
            }

            try
            {
                stop?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // it ended as it was being stopped
            }
        }

        /// <summary>The answer's weight, as it comes; what to add to the cache's total, which is nothing for an entry that has left the cache.</summary>
        public long Weighs(long weight)
        {
            lock (_gate)
            {
                if (_removed || _counted)
                {
                    return 0;
                }

                _weight = weight;
                _counted = true;
                return weight;
            }
        }

        /// <summary>The entry has left the cache; what to take from the cache's total.</summary>
        public long Left()
        {
            lock (_gate)
            {
                _removed = true;
                if (!_counted)
                {
                    return 0;
                }

                _counted = false;
                return _weight;
            }
        }

        private async Task<object> RunAsync(RequestCache cache, Func<CancellationToken, Task<object>> start, CancellationToken stopping)
        {
            // a token of its own under the source's
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            lock (_gate)
            {
                _stop = stop;
            }

            // for this fetch and what it calls, and no further: what an async method sets here is not its caller's
            var tally = new Tally();
            Fetching.Value = tally;
            try
            {
                var value = await start(stop.Token).ConfigureAwait(false);

                // asked for as lately as it answers: whatever it put by on its way (the items of a list) is older than it
                Touch(cache.NextUse());
                cache.Weighed(this, WeightOf(value, Interlocked.Read(ref tally.Bytes)));
                return value;
            }
            finally
            {
                lock (_gate)
                {
                    _stop = null;
                }
            }
        }
    }

    /// <summary>The kept <typeparamref name="T"/> for <paramref name="key"/> while it is younger than <paramref name="ttl"/> (null: for good), else fetched.</summary>
    public Task<T> GetAsync<T>(string key, TimeSpan? ttl, Func<CancellationToken, Task<T>> fetch, CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(fetch);
        return GetAsync<T>(key, ttl, (_, token) => fetch(token), ct);
    }

    /// <summary>
    /// As <see cref="GetAsync{T}(string, TimeSpan?, Func{CancellationToken, Task{T}}, CancellationToken)"/>, for a thing
    /// that is asked again for what changed since it was last read: the fetch is given what was kept for the key and is
    /// past its time, if it is still kept (null otherwise, and it reads the thing whole). Such a value says so by being
    /// an <see cref="IBuiltOn"/>, and is then not let go for being past its time alone.
    /// </summary>
    public async Task<T> GetAsync<T>(string key, TimeSpan? ttl, Func<T?, CancellationToken, Task<T>> fetch, CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(fetch);
        while (true)
        {
            if (_entries.TryGetValue(key, out var kept) && Usable(kept) && kept.TryEnter())
            {
                kept.Touch(NextUse());
                return await WaitedForAsync<T>(key, kept, ct).ConfigureAwait(false);
            }

            // not started until it wins its place: a loser of the race never asks upstream
            var previous = kept is { Task.IsCompletedSuccessfully: true } ? kept.Task.Result as T : null;
            var fresh = new Entry(this, token => Run(inner => fetch(previous, inner), token), _time.GetTimestamp(), NextUse(), ttl, stopping);
            if (kept is null ? _entries.TryAdd(key, fresh) : _entries.TryUpdate(key, fresh, kept))
            {
                if (kept is not null)
                {
                    Left(kept);
                }

                Trim();

                // in its place a moment already: another may have come, been given up and left, and so stopped it
                if (fresh.TryEnter())
                {
                    return await WaitedForAsync<T>(key, fresh, ct).ConfigureAwait(false);
                }
            }
            else if (kept is not null)
            {
                // the one found has given up its place and is on its way out of it
                await Task.Yield();
            }
        }
    }

    /// <summary>One caller's wait for an entry's fetch, counted in already (<see cref="Entry.TryEnter"/>).</summary>
    private async Task<T> WaitedForAsync<T>(string key, Entry entry, CancellationToken ct)
        where T : class
    {
        var givenUp = false;
        try
        {
            return (T)await entry.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested && entry.Task.IsFaulted)
        {
            Drop(key, entry);
            throw;
        }
        catch (OperationCanceledException) when (AnyFS.Sdk.UpstreamPatience.GivenUp && !entry.Task.IsCompleted)
        {
            GiveUpPlace(key, entry);
            givenUp = true;
            throw;
        }
        finally
        {
            entry.Leave(givenUp);
        }
    }

    /// <summary>
    /// A fetch still on its way whose caller the host gave up for an upstream that says nothing: it goes on for whoever
    /// else waits on it, and gives up its place, so that the next to ask fetches anew. Joined, the one that hung held
    /// every later caller until its own request's time was up, though the upstream answered again long before.
    /// </summary>
    private void GiveUpPlace(string key, Entry entry) => Drop(key, entry);

    /// <summary>The kept <typeparamref name="T"/> for <paramref name="key"/> if there is one, finished and younger than the time it was asked with; nothing is fetched.</summary>
    public T? Peek<T>(string key)
        where T : class
    {
        if (_entries.TryGetValue(key, out var kept) && kept.Task.IsCompletedSuccessfully && Usable(kept) && kept.Task.Result is T value)
        {
            kept.Touch(NextUse());
            return value;
        }

        return null;
    }

    /// <summary>
    /// Keeps <paramref name="value"/> for <paramref name="key"/>, in the place of whatever is kept for it: one of many that
    /// a single request brought (a page of a search read ahead), which no fetch of its own stands for. It weighs
    /// <paramref name="bytes"/>, as its caller says, and is found with <see cref="Peek{T}"/> or by a
    /// <see cref="GetAsync{T}"/> for the key while it is younger than <paramref name="ttl"/>.
    /// </summary>
    public void Put<T>(string key, T value, TimeSpan? ttl, long bytes)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        var entry = new Entry(value, _time.GetTimestamp(), NextUse(), ttl);
        _ = entry.Task;
        while (true)
        {
            if (_entries.TryGetValue(key, out var old))
            {
                if (_entries.TryUpdate(key, entry, old))
                {
                    Left(old);
                    break;
                }
            }
            else if (_entries.TryAdd(key, entry))
            {
                break;
            }
        }

        Weighed(entry, LeastWeight + Math.Max(0, bytes));
        Trim();
    }

    /// <summary>Forgets <paramref name="key"/>: the next asking fetches it.</summary>
    public void Forget(string key)
    {
        if (_entries.TryRemove(key, out var entry))
        {
            Left(entry);
        }
    }

    private bool Usable(Entry entry)
        => !entry.Task.IsCompleted || entry.Task.IsCompletedSuccessfully && (entry.Ttl is not { } ttl || _time.GetElapsedTime(entry.At) < ttl);

    private long NextUse() => Interlocked.Increment(ref _uses);

    private static async Task<object> Run<T>(Func<CancellationToken, Task<T>> fetch, CancellationToken ct)
        where T : class
    {
        await Task.Yield();
        return await fetch(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What an answer weighs: half as much again as the bytes read for it, or what it is where it is text or bytes itself (or a pair or a
    /// box of them), whichever is more; and what an entry costs with nothing in it.
    /// </summary>
    internal static long WeightOf(object? value, long received) => LeastWeight + (Said(value) ?? Math.Max(received + (received / 2), Plain(value)));

    // what a value says it weighs itself, alone or in a box
    private static long? Said(object? value) => value switch
    {
        IWeighed weighed => Math.Max(0, weighed.Bytes),
        IBoxed boxed => Said(boxed.Boxed),
        _ => null,
    };

    private static long Plain(object? value) => value switch
    {
        string text => 2L * text.Length,
        byte[] bytes => bytes.LongLength,
        ITuple tuple => Enumerable.Range(0, tuple.Length).Sum(i => Plain(tuple[i])),
        IBoxed boxed => Plain(boxed.Boxed),
        _ => 0,
    };

    /// <summary>An entry's fetch has answered: its weight joins the total, unless the entry has left meanwhile.</summary>
    private void Weighed(Entry entry, long weight)
    {
        if (entry.Weighs(weight) is > 0 and var added && Interlocked.Add(ref _bytes, added) > maxBytes)
        {
            Trim();
        }
    }

    private void Left(Entry entry)
    {
        if (entry.Left() is > 0 and var weight)
        {
            Interlocked.Add(ref _bytes, -weight);
        }
    }

    private void Drop(string key, Entry entry)
    {
        if (_entries.TryRemove(new KeyValuePair<string, Entry>(key, entry)))
        {
            Left(entry);
        }
    }

    private void Trim()
    {
        if ((_entries.Count <= MaxEntries && Bytes <= maxBytes) || Interlocked.Exchange(ref _trimming, 1) != 0)
        {
            return;
        }

        try
        {
            // from a copy made whole: others go on asking, and sorting the dictionary itself as it grows throws
            var all = _entries.ToArray();

            // what is past its time answers no one any more; but what the next read of it builds on is still that
            foreach (var old in all)
            {
                if (!Usable(old.Value) && !(old.Value.Task.IsCompletedSuccessfully && old.Value.Task.Result is IBuiltOn))
                {
                    Drop(old.Key, old.Value);
                }
            }

            if (_entries.Count <= MaxEntries && Bytes <= maxBytes)
            {
                return;
            }

            // then what was longest unasked for, until a fifth of each bound is free: the next trim is as far off as that
            foreach (var old in all.OrderBy(e => e.Value.Used))
            {
                var many = _entries.Count > MaxEntries / 5 * 4;
                if (!many && Bytes <= maxBytes / 5 * 4)
                {
                    break;
                }

                // one still on its way weighs nothing yet: letting it go frees nothing, and what it is fetching would
                // be asked for again at once. It goes only where there are too many
                if (many || old.Value.Task.IsCompleted)
                {
                    Drop(old.Key, old.Value);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _trimming, 0);
        }
    }
}

/// <summary>
/// A value kept in a <see cref="RequestCache"/> that says what it weighs: one whose fetch does not read all of what it
/// holds (a list read again for what changed since, most of it carried over from the last read).
/// </summary>
public interface IWeighed
{
    /// <summary>What the value holds in memory, about, in bytes.</summary>
    long Bytes { get; }
}

/// <summary>
/// A value kept in a <see cref="RequestCache"/> that its own next fetch builds on: a list asked again for what changed
/// since. Past its time it answers no one, and is still worth what reading it whole again would cost, so it is not let
/// go for being past its time alone: only, with everything else, as what was longest unasked for.
/// </summary>
public interface IBuiltOn;

/// <summary>What a <see cref="Box{T}"/> holds, whatever its type: for <see cref="RequestCache"/> to weigh.</summary>
internal interface IBoxed
{
    object? Boxed { get; }
}

/// <summary>A value kept in a <see cref="RequestCache"/>, which keeps only references: a struct, or a null for a thing that is not there.</summary>
public sealed record Box<T>(T Value) : IBoxed
{
    object? IBoxed.Boxed => Value;
}
