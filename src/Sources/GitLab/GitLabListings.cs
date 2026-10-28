using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.GitLab;

/// <summary>
/// What the source has asked GitLab, kept for a while (spec 7.8): one request for a thing however many ask for it at once;
/// a thing that changes (a project's branches, its issues, a wiki) asked again once it is older than the refresh interval;
/// a failure not kept, so the next asking tries again. A thing that never changes (the repository at a commit) is kept in
/// <see cref="TreeStore"/> instead.
/// </summary>
internal sealed class Listings
{
    private const int MaxEntries = 20_000;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly CancellationToken _stopping;

    /// <param name="stopping">The source's own: a request shared by several askers is not cancelled by one of them giving up.</param>
    public Listings(CancellationToken stopping) => _stopping = stopping;

    // an entry's fetch starts only once the entry is the one kept: two askers racing to replace a stale entry (or to add
    // a missing one) do not both go to GitLab, and the loser's request is never left unobserved
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

    /// <summary>Forgets <paramref name="key"/>: the next asking fetches it.</summary>
    public void Forget(string key) => _entries.TryRemove(key, out _);

    private static bool Usable(Entry entry)
        => !entry.Task.IsCompleted || entry.Task.IsCompletedSuccessfully && (entry.Ttl is not { } ttl || Stopwatch.GetElapsedTime(entry.At) < ttl);

    private async Task<object> Run<T>(Func<CancellationToken, Task<T>> fetch)
        where T : class
    {
        await Task.Yield();
        return await fetch(_stopping).ConfigureAwait(false);
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

/// <summary>One entry of a repository folder at a commit, as kept: a blob's size (the LFS object's for a file in LFS).</summary>
/// <param name="N">The name in git.</param>
/// <param name="K">'b' a file, 't' a folder, 'c' a submodule, 'l' a symbolic link.</param>
/// <param name="S">The blob's or tree's id.</param>
/// <param name="L">The file's length.</param>
/// <param name="F">Whether the file is in Git LFS.</param>
internal sealed record TreeItem(string N, char K, string S, long L, bool F);

/// <summary>Repository folders at a commit, by <c>project@commit:path</c>, as kept between starts.</summary>
internal sealed class SavedTrees
{
    public Dictionary<string, List<TreeItem>> Folders { get; set; } = [];
}

[JsonSerializable(typeof(SavedTrees))]
internal sealed partial class SavedTreesJson : JsonSerializerContext;

/// <summary>
/// The repository's folders at the commits the source has listed (spec 7.8). A commit's tree never changes, so a folder
/// listed once is kept for good, between starts too (<c>state\trees.json.gz</c>): a project opened again lists at once,
/// and only a branch that moved costs requests. Bounded: past <see cref="MaxFolders"/> the oldest-listed go.
/// </summary>
internal sealed class TreeStore
{
    public const string FileName = "trees.json.gz";
    private const int MaxFolders = 50_000;

    private readonly ConcurrentDictionary<string, (List<TreeItem> Items, long Order)> _folders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<List<TreeItem>>> _listing = new(StringComparer.Ordinal);
    private readonly string? _path;
    private readonly ILogger _logger;
    private long _order;
    private int _dirty;

    public TreeStore(string? path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        if (path is not null && StateFile.Load(path, SavedTreesJson.Default.SavedTrees) is { } saved)
        {
            foreach (var (key, items) in saved.Folders)
            {
                _folders[key] = (items, Interlocked.Increment(ref _order));
            }

            logger.LogInformation("gitlab: {Count:N0} repository folders kept from before", saved.Folders.Count);
        }
    }

    public int Count => _folders.Count;

    /// <summary>The folder's entries, kept or listed now (once, however many ask).</summary>
    public async Task<List<TreeItem>> GetAsync(string key, Func<CancellationToken, Task<List<TreeItem>>> list, CancellationToken ct)
    {
        if (_folders.TryGetValue(key, out var kept))
        {
            return kept.Items;
        }

        var task = _listing.GetOrAdd(key, _ => ListAsync(key, list));
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The folder's entries when they are kept.</summary>
    public bool TryGet(string key, [MaybeNullWhen(false)] out List<TreeItem> items)
    {
        items = _folders.TryGetValue(key, out var kept) ? kept.Items : null;
        return items is not null;
    }

    /// <summary>Keeps folders listed otherwise than one by one (a commit's whole tree at once), as a folder listed alone is kept.</summary>
    public void Keep(IEnumerable<(string Key, List<TreeItem> Items)> folders)
    {
        foreach (var (key, items) in folders)
        {
            _folders[key] = (items, Interlocked.Increment(ref _order));
        }

        Interlocked.Exchange(ref _dirty, 1);
        Trim();
    }

    private async Task<List<TreeItem>> ListAsync(string key, Func<CancellationToken, Task<List<TreeItem>>> list)
    {
        try
        {
            await Task.Yield();
            var items = await list(CancellationToken.None).ConfigureAwait(false);
            _folders[key] = (items, Interlocked.Increment(ref _order));
            Interlocked.Exchange(ref _dirty, 1);
            Trim();
            return items;
        }
        finally
        {
            _listing.TryRemove(key, out _);
        }
    }

    private void Trim()
    {
        if (_folders.Count > MaxFolders)
        {
            foreach (var old in _folders.OrderBy(f => f.Value.Order).Take(MaxFolders / 10).ToList())
            {
                _folders.TryRemove(old);
            }
        }
    }

    /// <summary>Writes the folders to the state file when something was listed since the last time.</summary>
    public void Save()
    {
        if (_path is null || Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        try
        {
            var saved = new SavedTrees { Folders = _folders.ToDictionary(f => f.Key, f => f.Value.Items, StringComparer.Ordinal) };
            StateFile.Save(_path, saved, SavedTreesJson.Default.SavedTrees);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Exchange(ref _dirty, 1);
            _logger.LogWarning("gitlab: the repository folders could not be kept for the next start: {Error}", ex.Message);
        }
    }
}
