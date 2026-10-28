using System.Collections.Concurrent;
using System.Diagnostics;

namespace AnyFS.Sources.Teams;

/// <summary>
/// What the source has asked the Teams services, kept for a while (spec 7.9): one request for a thing however many ask
/// for it at once; a thing kept until it is older than the refresh interval (null: for the session). A failure is not
/// kept, so the next asking tries again.
/// </summary>
internal sealed class TeamsCache(CancellationToken stopping)
{
    private const int MaxEntries = 20_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed record Entry(Task<object> Task, long At, TimeSpan? Ttl);

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

            var fresh = new Entry(Run(fetch), Stopwatch.GetTimestamp(), ttl);
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

/// <summary>The seam that gives a source its <see cref="ITeamsApi"/> in tests: the fake's, in place of the live web session.</summary>
internal sealed record TeamsStandIn(ITeamsApi Api);
