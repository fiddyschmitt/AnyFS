using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace AnyFS.Sources.GitLab;

/// <summary>One entry of a repository folder at a commit, as kept: a blob's size (the LFS object's for a file in LFS).</summary>
/// <param name="N">The name in git.</param>
/// <param name="K">'b' a file, 't' a folder, 'c' a submodule, 'l' a symbolic link.</param>
/// <param name="S">The blob's or tree's id.</param>
/// <param name="L">The file's length.</param>
/// <param name="F">Whether the file is in Git LFS.</param>
internal sealed record TreeItem(string N, char K, string S, long L, bool F);

/// <summary>
/// The repository's folders at the commits the source has listed this run (spec 7.8), by <c>project@commit:path</c>. A
/// commit's tree never changes, so a folder listed once is kept, and a branch and a tag at one commit share it. Kept for the
/// run alone since 6-Nov-2026: between runs the kept listings hold what the walks saw (the SDK's file, written as a walk
/// goes), and a repository's folder is stamped by its commit, so one whose commit has not moved is kept whole without this.
/// Written whole every five minutes and at every stop, the file this was had grown past a hundred megabytes on a big
/// server, and its write was what kept a stop from ending in time. Bounded: past <see cref="MaxFolders"/> the
/// oldest-listed go (a million; the SDK's kept listings hold the whole tree, as bytes, since 7-Nov-2026).
/// </summary>
internal sealed class TreeStore
{
    private const int MaxFolders = 1_000_000;

    private readonly ConcurrentDictionary<string, (List<TreeItem> Items, long Order)> _folders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<List<TreeItem>>> _listing = new(StringComparer.Ordinal);
    private long _order;

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

        Trim();
    }

    private async Task<List<TreeItem>> ListAsync(string key, Func<CancellationToken, Task<List<TreeItem>>> list)
    {
        try
        {
            await Task.Yield();
            var items = await list(CancellationToken.None).ConfigureAwait(false);
            _folders[key] = (items, Interlocked.Increment(ref _order));
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
}
