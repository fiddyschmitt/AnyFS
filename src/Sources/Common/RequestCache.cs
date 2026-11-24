using System.Collections.Concurrent;
using System.Diagnostics;

namespace AnyFS.Sources.Common;

/// <summary>
/// What a source has asked its service, kept for a while (GitLab, Teams, Jira, Confluence): one request for a thing however
/// many ask for it at once; a thing kept until it is older than the time it was asked with (null: for the session); a
/// failure not kept, so the next asking tries again. An entry's fetch starts only once that entry is the one kept: two
/// askers racing to replace a stale entry (or to add a missing one) do not both go upstream, and the loser's request is
/// never left unobserved. Bounded: past <see cref="MaxEntries"/> the oldest fifth go.
/// </summary>
/// <param name="stopping">The source's own: a request shared by several askers is not cancelled by one of them giving up. It is stopped with the source, or when it has given up its place and no one waits for it any more.</param>
public sealed class RequestCache(CancellationToken stopping)
{
    public const int MaxEntries = 20_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _trimming;

    private sealed class Entry
    {
        private readonly Lazy<Task<object>> _task;
        private readonly object _gate = new();
        private CancellationTokenSource? _stop;
        private int _waiting;
        private bool _gaveUpItsPlace;

        public Entry(Func<CancellationToken, Task<object>> start, long at, TimeSpan? ttl, CancellationToken stopping)
        {
            At = at;
            Ttl = ttl;
            _task = new Lazy<Task<object>>(() => RunAsync(start, stopping), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Task<object> Task => _task.Value;

        public long At { get; }

        public TimeSpan? Ttl { get; }

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

        private async Task<object> RunAsync(Func<CancellationToken, Task<object>> start, CancellationToken stopping)
        {
            // a token of its own under the source's
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            lock (_gate)
            {
                _stop = stop;
            }

            try
            {
                return await start(stop.Token).ConfigureAwait(false);
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
    public async Task<T> GetAsync<T>(string key, TimeSpan? ttl, Func<CancellationToken, Task<T>> fetch, CancellationToken ct)
        where T : class
    {
        while (true)
        {
            if (_entries.TryGetValue(key, out var kept) && Usable(kept) && kept.TryEnter())
            {
                return await WaitedForAsync<T>(key, kept, ct).ConfigureAwait(false);
            }

            // not started until it wins its place: a loser of the race never asks upstream
            var fresh = new Entry(token => Run(fetch, token), Stopwatch.GetTimestamp(), ttl, stopping);
            if (kept is null ? _entries.TryAdd(key, fresh) : _entries.TryUpdate(key, fresh, kept))
            {
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
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
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
    private void GiveUpPlace(string key, Entry entry) => _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));

    /// <summary>The kept <typeparamref name="T"/> for <paramref name="key"/> if there is one, finished and younger than the time it was asked with; nothing is fetched.</summary>
    public T? Peek<T>(string key)
        where T : class
        => _entries.TryGetValue(key, out var kept) && kept.Task.IsCompletedSuccessfully && Usable(kept) ? kept.Task.Result as T : null;

    /// <summary>Forgets <paramref name="key"/>: the next asking fetches it.</summary>
    public void Forget(string key) => _entries.TryRemove(key, out _);

    private static bool Usable(Entry entry)
        => !entry.Task.IsCompleted || entry.Task.IsCompletedSuccessfully && (entry.Ttl is not { } ttl || Stopwatch.GetElapsedTime(entry.At) < ttl);

    private static async Task<object> Run<T>(Func<CancellationToken, Task<T>> fetch, CancellationToken ct)
        where T : class
    {
        await Task.Yield();
        return await fetch(ct).ConfigureAwait(false);
    }

    private void Trim()
    {
        if (_entries.Count <= MaxEntries || Interlocked.Exchange(ref _trimming, 1) != 0)
        {
            return;
        }

        try
        {
            // from a copy made whole: others go on asking, and sorting the dictionary itself as it grows throws
            foreach (var old in _entries.ToArray().OrderBy(e => e.Value.At).Take(MaxEntries / 5))
            {
                _entries.TryRemove(old);
            }
        }
        finally
        {
            Volatile.Write(ref _trimming, 0);
        }
    }
}

/// <summary>A value kept in a <see cref="RequestCache"/>, which keeps only references: a struct, or a null for a thing that is not there.</summary>
public sealed record Box<T>(T Value);
