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
/// <param name="stopping">The source's own: a request shared by several askers is not cancelled by one of them giving up.</param>
public sealed class RequestCache(CancellationToken stopping)
{
    public const int MaxEntries = 20_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry(Func<Task<object>> start, long at, TimeSpan? ttl)
    {
        private readonly Lazy<Task<object>> _task = new(start, LazyThreadSafetyMode.ExecutionAndPublication);

        public Task<object> Task => _task.Value;

        public long At { get; } = at;

        public TimeSpan? Ttl { get; } = ttl;
    }

    /// <summary>The kept <typeparamref name="T"/> for <paramref name="key"/> while it is younger than <paramref name="ttl"/> (null: for good), else fetched.</summary>
    public async Task<T> GetAsync<T>(string key, TimeSpan? ttl, Func<CancellationToken, Task<T>> fetch, CancellationToken ct)
        where T : class
    {
        while (true)
        {
            if (_entries.TryGetValue(key, out var kept) && Usable(kept))
            {
                try
                {
                    return (T)await kept.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested && kept.Task.IsFaulted)
                {
                    _entries.TryRemove(new KeyValuePair<string, Entry>(key, kept));
                    throw;
                }
            }

            // not started until it wins its place: a loser of the race never asks upstream
            var fresh = new Entry(() => Run(fetch), Stopwatch.GetTimestamp(), ttl);
            if (kept is null ? _entries.TryAdd(key, fresh) : _entries.TryUpdate(key, fresh, kept))
            {
                Trim();
                try
                {
                    return (T)await fresh.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested && fresh.Task.IsFaulted)
                {
                    _entries.TryRemove(new KeyValuePair<string, Entry>(key, fresh));
                    throw;
                }
            }
        }
    }

    /// <summary>The kept <typeparamref name="T"/> for <paramref name="key"/> if there is one, finished and younger than the time it was asked with; nothing is fetched.</summary>
    public T? Peek<T>(string key)
        where T : class
        => _entries.TryGetValue(key, out var kept) && kept.Task.IsCompletedSuccessfully && Usable(kept) ? kept.Task.Result as T : null;

    /// <summary>Forgets <paramref name="key"/>: the next asking fetches it.</summary>
    public void Forget(string key) => _entries.TryRemove(key, out _);

    private static bool Usable(Entry entry)
        => !entry.Task.IsCompleted || entry.Task.IsCompletedSuccessfully && (entry.Ttl is not { } ttl || Stopwatch.GetElapsedTime(entry.At) < ttl);

    private async Task<object> Run<T>(Func<CancellationToken, Task<T>> fetch)
        where T : class
    {
        await Task.Yield();
        return await fetch(stopping).ConfigureAwait(false);
    }

    private void Trim()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (var old in _entries.OrderBy(e => e.Value.At).Take(MaxEntries / 5).ToList())
        {
            _entries.TryRemove(old);
        }
    }
}

/// <summary>A value kept in a <see cref="RequestCache"/>, which keeps only references: a struct, or a null for a thing that is not there.</summary>
public sealed record Box<T>(T Value);
